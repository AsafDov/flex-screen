using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlexScreen.Companion;

/// <param name="Join">A Meet, Zoom or Teams link, or "".</param>
record CalendarEvent(string Id, string Title, DateTimeOffset Start, DateTimeOffset End, bool AllDay, string Join);

/// <param name="Status">"ok", "setup" (no OAuth client file yet, or no Google account), "signin" (not connected), "error", or "" before the first read.</param>
record GoogleState(string Status, IReadOnlyList<CalendarEvent> Events);

/// <summary>
/// Google Calendar: today's and tomorrow's events of every Google account in the profile
/// (<see cref="GoogleAccount"/>; a personal and a work one, say), on every calendar ticked in
/// Google Calendar, so a calendar another account shares with one of them shows too. A
/// meeting on several of them shows once. Read with the user's own OAuth client; the sign-in
/// also covers Google Tasks, which GoogleTasks reads through the account. Signing in is a
/// one-time browser consent per account, from the tray menu; the refresh tokens are kept
/// encrypted for this Windows user (DPAPI).
/// </summary>
sealed class GoogleSource
{
    const int MaxEvents = 8;
    static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    // Zoom (a meeting or someone's personal room), Teams (classic and short links, and Teams
    // free), Meet, and Discord (an invite, an event, a channel).
    static readonly Regex MeetingLink = new(
        @"https://[\w.-]*(zoom\.us/(j|my)|teams\.microsoft\.com/(l/meetup-join|meet)|teams\.live\.com/meet|meet\.google\.com|" +
        @"discord\.gg|discord\.com/(invite|events|channels))/[^\s""<>]+", RegexOptions.IgnoreCase);

    /// <summary>Which app a meeting link opens in: "zoom", "teams", "meet", "discord", or "".</summary>
    public static string MeetingApp(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return "";
        var host = uri.Host.ToLowerInvariant();
        if (host == "zoom.us" || host.EndsWith(".zoom.us")) return "zoom";
        if (host.EndsWith("teams.microsoft.com") || host.EndsWith("teams.live.com")) return "teams";
        if (host == "meet.google.com") return "meet";
        if (host is "discord.gg" or "discord.com" or "www.discord.com") return "discord";
        return "";
    }

    readonly System.Threading.Timer _timer;
    readonly SemaphoreSlim _busy = new(1, 1);
    readonly object _gate = new();
    readonly Dictionary<string, List<JsonNode?>> _kept = [];  // by account: its events as last read, for when it can't be read
    List<GoogleAccount> _accounts = [];
    string _accountsSeen = "\0";
    bool _again;  // asked to read while reading (another profile, a sign-in): once more after

    // Not "setup" until the first read says so: a slow first read (it timed out once, right
    // after a restart) left the deck asking to connect Google for minutes.
    public GoogleState Current { get; private set; } = new("", []);

    /// <summary>Raised on a worker thread after each refresh that changed something.</summary>
    public event Action<GoogleState>? Changed;

    public event Action<Toast>? Toast;

    /// <summary>Raised after a sign-in or a sign-out, so the other Google readers (Tasks) read again.</summary>
    public event Action? SignedIn;

    public GoogleSource()
    {
        _timer = new System.Threading.Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(TimeSpan.Zero, Every);

    /// <summary>The profile's Google accounts, in its order. The same object stays for an account while the profile has it.</summary>
    public IReadOnlyList<GoogleAccount> Accounts
    {
        get
        {
            lock (_gate)
            {
                var stamp = Settings.AccountsStamp;
                if (stamp == _accountsSeen) return _accounts;
                _accountsSeen = stamp;
                var accounts = new List<GoogleAccount>();
                foreach (var (id, config) in Settings.Accounts("google"))
                {
                    var account = _accounts.FirstOrDefault(a => a.Id == id);
                    if (account != null) account.Configure(config);
                    else account = new GoogleAccount(id, config);
                    accounts.Add(account);
                }
                _accounts = accounts;
                return accounts;
            }
        }
    }

    public GoogleAccount? Account(string id) => Accounts.FirstOrDefault(a => a.Id == id);

    /// <summary>The browser consent for one account, from the tray menu.</summary>
    public async Task SignInAsync(GoogleAccount account)
    {
        if (await account.SignInAsync() is not { } missing) return;
        Log.Write(missing.Count == 0 ? $"google: {account.Id}: connected" : $"google: {account.Id}: connected without {string.Join(" and ", missing)} access");
        Toast?.Invoke(missing.Count == 0
            ? new Toast("ok", $"Connected to {account.Name}")
            : new Toast("error", $"Google didn't grant {string.Join(" and ", missing)} access. Connect again and tick every box."));
        await RefreshAsync();
        // Each account is meant to be another Google account: the same one twice shows its
        // tasks twice.
        if (account.Email.Length > 0 && Accounts.FirstOrDefault(a => a != account && a.Email == account.Email) is { } twin)
            Toast?.Invoke(new Toast("attention", $"{account.Name} is the same Google account as {twin.Name}"));
        SignedIn?.Invoke();
    }

    /// <summary>Forgets one account's sign-in on this PC.</summary>
    public async Task SignOutAsync(GoogleAccount account)
    {
        account.SignOut();
        lock (_gate) _kept.Remove(account.Id);
        Log.Write($"google: {account.Id}: signed out on this PC");
        await RefreshAsync();
        SignedIn?.Invoke();  // its tasks go too
    }

    public async Task RefreshAsync()
    {
        if (!await _busy.WaitAsync(0))
        {
            _again = true;
            return;
        }
        try
        {
            do
            {
                _again = false;
                await ReadAllAsync();
            } while (_again);
        }
        finally
        {
            _busy.Release();
        }
    }

    async Task ReadAllAsync()
    {
        var accounts = Accounts.Where(a => a.Calendar).ToList();
        if (accounts.Count == 0)
        {
            Publish(new GoogleState("setup", []));
            return;
        }
        var now = DateTimeOffset.Now;
        var until = now.Date.AddDays(2);  // the rest of today and tomorrow
        var span = $"&timeMin={Uri.EscapeDataString(now.ToString("o"))}&timeMax={Uri.EscapeDataString(new DateTimeOffset(until).ToString("o"))}";
        var reads = await Task.WhenAll(accounts.Select(a => ReadAccountAsync(a, span)));
        var statuses = reads.Select(r => r.Status).ToList();
        // One account that can't be read just now keeps its last events; the deck says
        // something is wrong only when none can be.
        var ok = statuses.Contains("ok") || (statuses.Contains("error") && Current.Status == "ok");
        var status = ok ? "ok" : statuses.Contains("error") ? "error" : statuses.Contains("signin") ? "signin" : "setup";
        Publish(new GoogleState(status, Merge(reads.SelectMany(r => r.Items))));
    }

    /// <summary>One account's events in the span, as Google has them; the last ones it had when it can't be read.</summary>
    async Task<(string Status, List<JsonNode?> Items)> ReadAccountAsync(GoogleAccount account, string span)
    {
        try
        {
            var status = await account.AuthStatusAsync();
            if (status != "ok") return (status, []);
            var calendars = await CalendarIdsAsync(account);
            var replies = await Task.WhenAll(calendars.Select(id => CalendarEventsAsync(account, id, span)));
            var items = replies.SelectMany(i => i).ToList();
            lock (_gate) _kept[account.Id] = items;
            return ("ok", items);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Write($"google: {account.Id}: {e.Message}");
            lock (_gate) return ("error", _kept.GetValueOrDefault(account.Id) ?? []);
        }
    }

    void Publish(GoogleState state)
    {
        var changed = state.Status != Current.Status || !state.Events.SequenceEqual(Current.Events);
        Current = state;
        if (changed) Changed?.Invoke(state);
    }

    /// <summary>The events the deck shows, from every account's: soonest first, each meeting once.</summary>
    static List<CalendarEvent> Merge(IEnumerable<JsonNode?> items)
    {
        var events = new List<CalendarEvent>();
        var seen = new HashSet<string>();
        foreach (var e in items)
        {
            if (e == null || (string?)e["status"] == "cancelled") continue;
            // "self" is the calendar this copy is on, so a meeting declined in one account
            // but accepted in the other still shows (from the other's copy).
            var declined = (e["attendees"] as JsonArray ?? []).Any(a => (bool?)a?["self"] == true && (string?)a?["responseStatus"] == "declined");
            if (declined) continue;
            var allDay = e["start"]?["date"] != null;
            var start = Time(e["start"]);
            var end = Time(e["end"]);
            if (start == null || end == null) continue;
            var id = (string?)e["id"] ?? "";
            // A meeting on two of the calendars (both accounts invited) shows once. Each
            // occurrence of a repeating event shares the iCalUID, hence the start time too.
            if (!seen.Add($"{(string?)e["iCalUID"] ?? id}|{start.Value.UtcTicks}")) continue;
            events.Add(new CalendarEvent(id, (string?)e["summary"] ?? "(no title)", start.Value, end.Value, allDay, JoinLink(e)));
        }
        return events.OrderBy(e => e.Start).ThenBy(e => !e.AllDay).Take(MaxEvents).ToList();
    }

    /// <summary>
    /// The calendars ticked in the account's Google Calendar list: its own and the ones shared
    /// with it (another Google account's, say), as Google Calendar itself shows them.
    /// Free/busy-only shares are left out, since they have no event details to show. Also
    /// learns who is signed in: the primary calendar's id is the account's address.
    /// </summary>
    static async Task<List<string>> CalendarIdsAsync(GoogleAccount account)
    {
        var json = await account.SendAsync(HttpMethod.Get, "https://www.googleapis.com/calendar/v3/users/me/calendarList?minAccessRole=reader&maxResults=250");
        var items = json?["items"] as JsonArray ?? [];
        if (items.FirstOrDefault(c => (bool?)c?["primary"] == true) is { } primary) account.Email = (string?)primary["id"] ?? "";
        var ids = items
            .Where(c => (bool?)c?["selected"] == true && (bool?)c?["hidden"] != true && (bool?)c?["deleted"] != true)
            .Select(c => (string?)c?["id"])
            .OfType<string>()
            .ToList();
        return ids.Count > 0 ? ids : ["primary"];
    }

    /// <summary>One calendar's events in the span; none if it can't be read (the share was
    /// withdrawn since the list was read, say), so one calendar doesn't blank the rest.</summary>
    static async Task<IEnumerable<JsonNode?>> CalendarEventsAsync(GoogleAccount account, string calendar, string span)
    {
        try
        {
            var json = await account.SendAsync(HttpMethod.Get, $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(calendar)}/events" +
                                                              "?singleEvents=true&orderBy=startTime&maxResults=30" + span);
            return json?["items"] as JsonArray ?? [];
        }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            Log.Write($"google: {account.Id}: skipped a calendar that can't be read ({(int)e.StatusCode!})");
            return [];
        }
    }

    static DateTimeOffset? Time(JsonNode? t)
    {
        if ((string?)t?["dateTime"] is { } dt) return DateTimeOffset.Parse(dt, CultureInfo.InvariantCulture);
        if ((string?)t?["date"] is { } d) return new DateTimeOffset(DateTime.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        return null;
    }

    static string JoinLink(JsonNode e)
    {
        if ((string?)e["hangoutLink"] is { Length: > 0 } meet) return meet;
        foreach (var p in e["conferenceData"]?["entryPoints"] as JsonArray ?? [])
        {
            if ((string?)p?["entryPointType"] == "video" && (string?)p?["uri"] is { Length: > 0 } uri) return uri;
        }
        var text = $"{(string?)e["location"]} {(string?)e["description"]}";
        return MeetingLink.Match(text) is { Success: true } m ? WebUtility.HtmlDecode(m.Value) : "";
    }
}
