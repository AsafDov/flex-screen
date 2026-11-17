using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlexScreen.Companion;

/// <summary>One event, from any calendar.</summary>
/// <param name="Join">A Meet, Zoom, Teams or Discord link, or "".</param>
record CalendarEvent(string Id, string Title, DateTimeOffset Start, DateTimeOffset End, bool AllDay, string Join)
{
    /// <summary>Which calendar, for the deck when it's worth saying ("Work", "Family"); "" for an account's own main one without a label.</summary>
    public string Calendar { get; init; } = "";

    /// <summary>The same meeting on several calendars has the same one (iCalendar's UID); "" when not known.</summary>
    public string Uid { get; init; } = "";

    /// <summary>The account it came from said no to it.</summary>
    public bool Declined { get; init; }

    /// <summary>Marked free (Google's "available", Outlook's "free", iCalendar's TRANSPARENT).</summary>
    public bool Free { get; init; }

    /// <summary>Its calendar's color, 0xRRGGBB (Google's and Outlook's own, a link's "color" in the profile), or null.</summary>
    public int? Color { get; init; }
}

/// <param name="Status">"ok"; else why there's nothing: "setup" (no calendar, or one not set up), "signin", "error"; "" before the first read.</param>
record CalendarState(string Status, IReadOnlyList<CalendarEvent> Events);

/// <summary>One calendar of a source (a Google account's own, a shared one, Outlook's), for the tray's Calendars menu.</summary>
/// <param name="Shown">Its events go on the deck.</param>
/// <param name="Default">On the deck unless the profile says otherwise (ticked in Google Calendar; Outlook's main one).</param>
record CalendarInfo(string Id, string Name, bool Shown, bool Default);

/// <param name="Status">"ok", "setup" (not set up on this PC), "signin" or "error".</param>
record CalendarRead(string Status, IReadOnlyList<CalendarEvent> Events, IReadOnlyList<CalendarInfo> Calendars);

/// <summary>
/// A place calendar events come from: a Google account, a Microsoft 365 account, an iCalendar
/// link. Each is a class of its own (GoogleCalendar, MicrosoftCalendar, IcsCalendar) made
/// from an entry of the profile's "accounts"; <see cref="CalendarSource"/> merges them.
/// </summary>
interface ICalendarProvider
{
    /// <summary>The account's id in the profile ("google", "microsoft-work", "ics-school").</summary>
    string Id { get; }

    /// <summary>For people: "Google (Work)", "School".</summary>
    string Name { get; }

    /// <summary>The events that overlap the span, every occurrence of a repeating one on its own. Throws when the source can't be read.</summary>
    Task<CalendarRead> ReadAsync(DateTimeOffset from, DateTimeOffset to);
}

/// <summary>Meeting links in events, and which app opens them.</summary>
static class MeetingLinks
{
    // Zoom (a meeting or someone's personal room), Teams (classic and short links, and Teams
    // free), Meet, and Discord (an invite, an event, a channel).
    static readonly Regex Link = new(
        @"https://[\w.-]*(zoom\.us/(j|my)|teams\.microsoft\.com/(l/meetup-join|meet)|teams\.live\.com/meet|meet\.google\.com|" +
        @"discord\.gg|discord\.com/(invite|events|channels))/[^\s""<>]+", RegexOptions.IgnoreCase);

    /// <summary>The first meeting link in an event's text (its location, description), or "".</summary>
    public static string Find(string text) => Link.Match(text) is { Success: true } m ? WebUtility.HtmlDecode(m.Value) : "";

    /// <summary>Which app a meeting link opens in: "zoom", "teams", "meet", "discord", or "".</summary>
    public static string App(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return "";
        var host = uri.Host.ToLowerInvariant();
        if (host == "zoom.us" || host.EndsWith(".zoom.us")) return "zoom";
        if (host.EndsWith("teams.microsoft.com") || host.EndsWith("teams.live.com")) return "teams";
        if (host == "meet.google.com") return "meet";
        if (host is "discord.gg" or "discord.com" or "www.discord.com") return "discord";
        return "";
    }
}

/// <summary>
/// Which of a source's calendars go on the deck: the account's "calendars" in the profile,
/// <c>{ "hide": ["Birthdays"], "show": ["Team OOO"] }</c>, by a calendar's id or name, over the
/// source's own choice (the calendars ticked in Google Calendar; Outlook's main calendar).
/// </summary>
static class CalendarChoice
{
    public static bool Shown(JsonObject? account, string id, string name, bool byDefault)
    {
        bool In(string list) => (account?["calendars"]?[list] as JsonArray ?? []).Any(n =>
            (string?)n is { } s && (string.Equals(s, id, StringComparison.OrdinalIgnoreCase) || string.Equals(s, name, StringComparison.OrdinalIgnoreCase)));
        if (In("hide")) return false;
        return byDefault || In("show");
    }

    /// <summary>The account's settings with one calendar shown or hidden (by id), keeping the rest.</summary>
    public static JsonObject With(JsonObject account, string id, string name, bool byDefault, bool show)
    {
        var config = (JsonObject)account.DeepClone();
        var choice = config["calendars"] as JsonObject ?? [];
        JsonArray Without(string list) => new((choice[list] as JsonArray ?? [])
            .Where(n => (string?)n is { } s && !string.Equals(s, id, StringComparison.OrdinalIgnoreCase) && !string.Equals(s, name, StringComparison.OrdinalIgnoreCase))
            .Select(n => n?.DeepClone()).ToArray());
        var hide = Without("hide");
        var showList = Without("show");
        if (show && !byDefault) showList.Add(id);
        if (!show && byDefault) hide.Add(id);
        choice = new JsonObject();
        if (hide.Count > 0) choice["hide"] = hide;
        if (showList.Count > 0) choice["show"] = showList;
        if (choice.Count > 0) config["calendars"] = choice;
        else config.Remove("calendars");
        return config;
    }
}

/// <summary>
/// The profile's "calendarFilter": which events stay off the deck, from every calendar.
/// <code>
/// "calendarFilter": {
///   "hideTitles": ["Lunch", "/^Focus/"],  // a word or phrase in the title (any case), or a /regular expression/
///   "hideAllDay": false,
///   "hideDeclined": true,                 // those you said no to (on unless false)
///   "hideFree": false                     // those marked free
/// }
/// </code>
/// </summary>
static class CalendarFilter
{
    public static bool HideAllDay(JsonObject? f) => Flag(f?["hideAllDay"], false);
    public static bool HideDeclined(JsonObject? f) => Flag(f?["hideDeclined"], true);
    public static bool HideFree(JsonObject? f) => Flag(f?["hideFree"], false);

    public static IReadOnlyList<string> HideTitles(JsonObject? f) =>
        (f?["hideTitles"] as JsonArray ?? []).Select(n => (string?)n ?? "").Where(s => s.Trim().Length > 0).ToList();

    public static bool Keeps(CalendarEvent e, JsonObject? f)
    {
        if (e.AllDay && HideAllDay(f)) return false;
        if (e.Declined && HideDeclined(f)) return false;
        if (e.Free && HideFree(f)) return false;
        return !HideTitles(f).Any(t => Matches(e.Title, t));
    }

    static bool Matches(string title, string pattern)
    {
        pattern = pattern.Trim();
        if (pattern.Length > 2 && pattern.StartsWith('/') && pattern.EndsWith('/'))
        {
            try
            {
                return Regex.IsMatch(title, pattern[1..^1], RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
            }
            catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException)
            {
                return false;  // a mistyped expression hides nothing
            }
        }
        return title.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    static bool Flag(JsonNode? node, bool byDefault) => node is JsonValue v && v.TryGetValue(out bool on) ? on : byDefault;
}

/// <summary>
/// The deck's calendar: today's events (from midnight, so the day view has the whole day) and
/// tomorrow's, from every calendar source in the profile, filtered (<see cref="CalendarFilter"/>),
/// soonest first, a meeting on several calendars once. Reads every 5 minutes, and at once when
/// the profile or its accounts change or after a sign-in. A source that can't be read keeps
/// its last events; the deck says something's wrong only when none can be read.
/// </summary>
sealed class CalendarSource
{
    public const int MaxEvents = 24;  // the deck's: a whole day and tomorrow
    static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    readonly Func<IReadOnlyList<ICalendarProvider>> _sources;
    readonly System.Threading.Timer _timer;
    readonly SemaphoreSlim _busy = new(1, 1);
    readonly object _gate = new();
    readonly Dictionary<string, CalendarRead> _reads = [];  // by source: its last read (the last good events when it can't be read)
    bool _again;

    /// <param name="sources">The sources there are now: they follow the profile.</param>
    public CalendarSource(Func<IReadOnlyList<ICalendarProvider>> sources)
    {
        _sources = sources;
        _timer = new System.Threading.Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    // Not "setup" until the first read says so: a slow first read (it timed out once, right
    // after a restart) left the deck asking to connect a calendar for minutes.
    public CalendarState Current { get; private set; } = new("", []);

    /// <summary>Raised on a worker thread after each refresh that changed something.</summary>
    public event Action<CalendarState>? Changed;

    public void Start() => _timer.Change(TimeSpan.Zero, Every);

    /// <summary>A source's calendars and status as last read ("" before it's been read), for the tray.</summary>
    public (string Status, IReadOnlyList<CalendarInfo> Calendars) Of(string source)
    {
        lock (_gate) return _reads.TryGetValue(source, out var r) ? (r.Status, r.Calendars) : ("", []);
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
        var sources = _sources();
        if (sources.Count == 0)
        {
            lock (_gate) _reads.Clear();
            Publish(new CalendarState("setup", []));
            return;
        }
        var today = DateTime.Today;
        var from = new DateTimeOffset(today);
        var to = new DateTimeOffset(today.AddDays(2));  // the whole of today and tomorrow
        var reads = await Task.WhenAll(sources.Select(s => ReadOneAsync(s, from, to)));
        var statuses = reads.Select(r => r.Status).ToList();
        lock (_gate)
        {
            foreach (var gone in _reads.Keys.Except(sources.Select(s => s.Id)).ToList()) _reads.Remove(gone);
        }
        var ok = statuses.Contains("ok") || (statuses.Contains("error") && Current.Status == "ok");
        var status = ok ? "ok" : statuses.Contains("error") ? "error" : statuses.Contains("signin") ? "signin" : "setup";
        Publish(new CalendarState(status, Merge(reads.SelectMany(r => r.Events), Settings.Source("calendarFilter"))));
    }

    async Task<CalendarRead> ReadOneAsync(ICalendarProvider source, DateTimeOffset from, DateTimeOffset to)
    {
        try
        {
            var read = await source.ReadAsync(from, to);
            lock (_gate) _reads[source.Id] = read;
            return read;
        }
        catch (Exception e)  // whatever one source does (a reply of a shape not expected), the others are still read
        {
            Log.Write($"calendar: {source.Id}: {e.GetType().Name}: {e.Message}");
            lock (_gate)
            {
                var last = _reads.GetValueOrDefault(source.Id);
                var kept = new CalendarRead("error", last?.Events ?? [], last?.Calendars ?? []);
                _reads[source.Id] = kept;
                return kept;
            }
        }
    }

    /// <summary>
    /// The event's id on the deck: short and the same from read to read (Outlook's own ids are
    /// ~150 characters, and the deck's line holds 8 KB), so cal.join finds it again.
    /// </summary>
    public static string DeckId(CalendarEvent e) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes($"{e.Id}|{e.Start.UtcTicks}")))[..12].ToLowerInvariant();

    /// <summary>An event by its deck id (or its own id, from a deck before deck ids), or null.</summary>
    public CalendarEvent? Find(string id) => Current.Events.FirstOrDefault(e => DeckId(e) == id) ?? Current.Events.FirstOrDefault(e => e.Id == id);

    /// <summary>Filtered (each copy on its own: declined on one calendar, accepted on another, still shows), then each meeting once, soonest first.</summary>
    public static List<CalendarEvent> Merge(IEnumerable<CalendarEvent> events, JsonObject? filter)
    {
        var seen = new HashSet<string>();
        return events
            .Where(e => CalendarFilter.Keeps(e, filter))
            .OrderBy(e => e.Start).ThenBy(e => !e.AllDay)
            // Each occurrence of a repeating event shares the UID, hence the start time too.
            .Where(e => seen.Add($"{(e.Uid.Length > 0 ? e.Uid : e.Id)}|{e.Start.UtcTicks}"))
            .Take(MaxEvents)
            .ToList();
    }

    void Publish(CalendarState state)
    {
        var changed = state.Status != Current.Status || !state.Events.SequenceEqual(Current.Events);
        Current = state;
        if (changed) Changed?.Invoke(state);
    }
}
