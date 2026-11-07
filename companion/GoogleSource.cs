using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlexScreen.Companion;

/// <param name="Join">A Meet, Zoom or Teams link, or "".</param>
record CalendarEvent(string Id, string Title, DateTimeOffset Start, DateTimeOffset End, bool AllDay, string Join);

/// <param name="From">The mail's sender, from the task's notes.</param>
/// <param name="Due">"yyyy-MM-dd", or "".</param>
record MailTask(string List, string Id, string Title, string From, string Due, bool Done);

/// <param name="Status">"ok", "setup" (no OAuth client file yet), "signin" (not connected) or "error".</param>
record GoogleState(string Status, IReadOnlyList<CalendarEvent> Events, IReadOnlyList<MailTask> Tasks);

/// <summary>
/// Google Calendar (today's and tomorrow's events on every calendar ticked in Google
/// Calendar, so a calendar another account shares with this one shows too) and Google Tasks
/// (the open tasks the todo skill made from mail, tagged "[todo src=gmail:…]"), read with
/// the user's own OAuth client. Signing in is a one-time browser consent from the tray
/// menu; the refresh token is kept encrypted for this Windows user (DPAPI).
/// </summary>
sealed class GoogleSource
{
    const int MaxEvents = 8;
    const int MaxTasks = 6;
    const string Scopes = "https://www.googleapis.com/auth/calendar.readonly https://www.googleapis.com/auth/tasks";
    const string MailTag = "[todo src=gmail:";
    static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "flex-screen");
    public static readonly string ClientPath = Path.Combine(Folder, "google-client.json");
    static readonly string TokenPath = Path.Combine(Folder, "google-token.bin");
    static readonly Regex MeetingLink = new(@"https://[\w.-]*(zoom\.us/j|teams\.microsoft\.com/l/meetup-join|meet\.google\.com)/[^\s""<>]+", RegexOptions.IgnoreCase);
    static readonly Regex Sender = new(@"^From:\s*(.+?)\s*(<[^>]*>)?\s*$", RegexOptions.Multiline);

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    readonly System.Threading.Timer _timer;
    readonly SemaphoreSlim _busy = new(1, 1);
    readonly HashSet<string> _doneHere = [];  // completed from the deck, shown ticked for the rest of the day
    DateTime _doneDay = DateTime.Today;
    string? _accessToken;
    DateTime _accessExpires;

    public GoogleState Current { get; private set; } = new("setup", [], []);

    /// <summary>Raised on a worker thread after each refresh that changed something.</summary>
    public event Action<GoogleState>? Changed;

    public event Action<Toast>? Toast;

    public GoogleSource()
    {
        _timer = new System.Threading.Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(TimeSpan.Zero, Every);

    public bool HasClient => File.Exists(ClientPath);

    /// <summary>The browser consent, from the tray menu. Listens on localhost for Google's redirect.</summary>
    public async Task SignInAsync()
    {
        if (ReadClient() is not { } client) return;
        var (clientId, clientSecret) = client;
        var port = FreePort();
        var redirect = $"http://localhost:{port}/";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var url = "https://accounts.google.com/o/oauth2/v2/auth?response_type=code&access_type=offline&prompt=consent" +
                  $"&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirect)}" +
                  $"&scope={Uri.EscapeDataString(Scopes)}&code_challenge={challenge}&code_challenge_method=S256&state={state}";
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(5));
        var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? "");
        var ok = query["state"] == state && query["code"] is { Length: > 0 };
        var page = Encoding.UTF8.GetBytes(ok ? "<p>flex-screen is connected to Google. You can close this tab.</p>"
                                             : "<p>Google sign-in didn't complete. Try again from the flex-screen tray menu.</p>");
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.OutputStream.WriteAsync(page);
        context.Response.Close();
        if (!ok)
        {
            Log.Write($"google: sign-in failed ({query["error"] ?? "no code"})");
            return;
        }
        var tokens = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = query["code"]!, ["redirect_uri"] = redirect,
            ["client_id"] = clientId, ["client_secret"] = clientSecret, ["code_verifier"] = verifier,
        });
        if ((string?)tokens?["refresh_token"] is not { Length: > 0 } refresh)
        {
            Log.Write("google: no refresh token in the reply");
            return;
        }
        SaveRefreshToken(refresh);
        _accessToken = (string?)tokens["access_token"];
        _accessExpires = DateTime.UtcNow.AddSeconds(((int?)tokens["expires_in"] ?? 3600) - 60);
        // Google's consent page has a box per scope, and unticked ones aren't granted.
        var granted = ((string?)tokens["scope"] ?? Scopes).Split(' ');
        var missing = Scopes.Split(' ').Where(s => !granted.Contains(s)).Select(s => s.EndsWith("tasks") ? "Tasks" : "Calendar").ToList();
        Log.Write(missing.Count == 0 ? "google: connected" : $"google: connected without {string.Join(" and ", missing)} access");
        Toast?.Invoke(missing.Count == 0
            ? new Toast("ok", "Connected to Google Calendar and Tasks")
            : new Toast("error", $"Google didn't grant {string.Join(" and ", missing)} access. Connect again and tick every box."));
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (!await _busy.WaitAsync(0)) return;
        try
        {
            if (!HasClient)
            {
                Publish(new GoogleState("setup", [], []));
                return;
            }
            if (!await EnsureTokenAsync())
            {
                Publish(new GoogleState("signin", [], []));
                return;
            }
            var events = await EventsAsync();
            var tasks = await TasksAsync();
            Publish(new GoogleState("ok", events, tasks));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Write($"google: {e.Message}");
            Publish(Current with { Status = Current.Status == "ok" ? "ok" : "error" });  // keep showing the last good data
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>Ticks a task off (or back on) in Google Tasks.</summary>
    public async Task SetDoneAsync(string list, string id, bool done)
    {
        try
        {
            if (!await EnsureTokenAsync()) return;
            var body = done ? new JsonObject { ["status"] = "completed" } : new JsonObject { ["status"] = "needsAction", ["completed"] = null };
            using var request = new HttpRequestMessage(HttpMethod.Patch,
                $"https://tasks.googleapis.com/tasks/v1/lists/{Uri.EscapeDataString(list)}/tasks/{Uri.EscapeDataString(id)}")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            using var reply = await _http.SendAsync(request);
            reply.EnsureSuccessStatusCode();
            if (done) _doneHere.Add(id);
            else _doneHere.Remove(id);
            Publish(Current with { Tasks = Current.Tasks.Select(t => t.Id == id ? t with { Done = done } : t).ToList() });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Log.Write($"google: can't update the task: {e.Message}");
            Toast?.Invoke(new Toast("error", "Couldn't update the task in Google Tasks"));
        }
    }

    void Publish(GoogleState state)
    {
        var changed = state.Status != Current.Status || !state.Events.SequenceEqual(Current.Events) || !state.Tasks.SequenceEqual(Current.Tasks);
        Current = state;
        if (changed) Changed?.Invoke(state);
    }

    async Task<List<CalendarEvent>> EventsAsync()
    {
        var now = DateTimeOffset.Now;
        var until = now.Date.AddDays(2);  // the rest of today and tomorrow
        var span = $"&timeMin={Uri.EscapeDataString(now.ToString("o"))}&timeMax={Uri.EscapeDataString(new DateTimeOffset(until).ToString("o"))}";
        var calendars = await CalendarIdsAsync();
        var replies = await Task.WhenAll(calendars.Select(id => CalendarEventsAsync(id, span)));
        var events = new List<CalendarEvent>();
        var seen = new HashSet<string>();
        foreach (var e in replies.SelectMany(items => items))
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
    /// The calendars ticked in Google Calendar's list: the user's own and the ones shared
    /// with them (another Google account's, say), as Google Calendar itself shows them.
    /// Free/busy-only shares are left out, since they have no event details to show.
    /// </summary>
    async Task<List<string>> CalendarIdsAsync()
    {
        var json = await GetAsync("https://www.googleapis.com/calendar/v3/users/me/calendarList?minAccessRole=reader&maxResults=250");
        var ids = (json?["items"] as JsonArray ?? [])
            .Where(c => (bool?)c?["selected"] == true && (bool?)c?["hidden"] != true && (bool?)c?["deleted"] != true)
            .Select(c => (string?)c?["id"])
            .OfType<string>()
            .ToList();
        return ids.Count > 0 ? ids : ["primary"];
    }

    /// <summary>One calendar's events in the span; none if it can't be read (the share was
    /// withdrawn since the list was read, say), so one calendar doesn't blank the rest.</summary>
    async Task<IEnumerable<JsonNode?>> CalendarEventsAsync(string calendar, string span)
    {
        try
        {
            var json = await GetAsync($"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(calendar)}/events" +
                                      "?singleEvents=true&orderBy=startTime&maxResults=30" + span);
            return json?["items"] as JsonArray ?? [];
        }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            Log.Write($"google: skipped a calendar that can't be read ({(int)e.StatusCode!})");
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

    async Task<List<MailTask>> TasksAsync()
    {
        if (_doneDay != DateTime.Today)
        {
            _doneHere.Clear();
            _doneDay = DateTime.Today;
        }
        var lists = await GetAsync("https://tasks.googleapis.com/tasks/v1/users/@me/lists?maxResults=100");
        var tasks = new List<MailTask>();
        foreach (var l in lists?["items"] as JsonArray ?? [])
        {
            var list = (string?)l?["id"];
            if (list == null) continue;
            var json = await GetAsync($"https://tasks.googleapis.com/tasks/v1/lists/{Uri.EscapeDataString(list)}/tasks?maxResults=100&showCompleted=false");
            foreach (var t in json?["items"] as JsonArray ?? [])
            {
                var notes = (string?)t?["notes"] ?? "";
                if (!notes.Contains(MailTag)) continue;
                var id = (string?)t?["id"] ?? "";
                const bool done = false;
                var from = Sender.Match(notes) is { Success: true } m ? m.Groups[1].Value.Trim('"', ' ') : "";
                var due = (string?)t?["due"] is { Length: >= 10 } d ? d[..10] : "";
                tasks.Add(new MailTask(list, id, (string?)t?["title"] ?? "", from, due, done));
            }
        }
        // Ticked off from the deck today: still shown, struck through.
        tasks.AddRange(Current.Tasks.Where(t => t.Done && _doneHere.Contains(t.Id) && tasks.All(o => o.Id != t.Id)));
        // Open first, soonest due first (no date last).
        return tasks.OrderBy(t => t.Done).ThenBy(t => t.Due.Length == 0).ThenBy(t => t.Due).Take(MaxTasks).ToList();
    }

    async Task<JsonNode?> GetAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        using var reply = await _http.SendAsync(request);
        var body = await reply.Content.ReadAsStringAsync();
        if (!reply.IsSuccessStatusCode)
        {
            // Google says why (the API isn't enabled in the Cloud project, a scope wasn't
            // granted, a quota…): put that in the log, not just the status code.
            var api = url.Contains("tasks.googleapis.com") ? "Tasks" : "Calendar";
            string? reason = null;
            try
            {
                var error = JsonNode.Parse(body)?["error"];
                reason = (string?)error?["message"] ?? (string?)error?["status"];
            }
            catch (System.Text.Json.JsonException)
            {
            }
            throw new HttpRequestException($"{api} API: {(int)reply.StatusCode} {reason ?? reply.ReasonPhrase}", null, reply.StatusCode);
        }
        return JsonNode.Parse(body);
    }

    async Task<bool> EnsureTokenAsync()
    {
        if (_accessToken != null && DateTime.UtcNow < _accessExpires) return true;
        var refresh = LoadRefreshToken();
        if (refresh == null || ReadClient() is not { } client) return false;
        var (clientId, clientSecret) = client;
        var tokens = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = clientId, ["client_secret"] = clientSecret,
        });
        if ((string?)tokens?["access_token"] is not { Length: > 0 } access) return false;
        _accessToken = access;
        _accessExpires = DateTime.UtcNow.AddSeconds(((int?)tokens["expires_in"] ?? 3600) - 60);
        return true;
    }

    async Task<JsonNode?> PostTokenAsync(Dictionary<string, string> form)
    {
        using var reply = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form));
        var json = JsonNode.Parse(await reply.Content.ReadAsStringAsync());
        if (!reply.IsSuccessStatusCode)
        {
            Log.Write($"google: token request failed: {(string?)json?["error"]}");
            if ((string?)json?["error"] == "invalid_grant") File.Delete(TokenPath);  // revoked: sign in again
            return null;
        }
        return json;
    }

    /// <summary>The OAuth client (a "Desktop app" client from Google Cloud Console, as downloaded).</summary>
    static (string Id, string Secret)? ReadClient()
    {
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(ClientPath));
            var c = json?["installed"] ?? json?["web"];
            return (string?)c?["client_id"] is { } id && (string?)c?["client_secret"] is { } secret ? (id, secret) : null;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    static void SaveRefreshToken(string token)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(TokenPath, ProtectedBytes.Protect(Encoding.UTF8.GetBytes(token)));
    }

    static string? LoadRefreshToken()
    {
        try
        {
            return File.Exists(TokenPath) ? Encoding.UTF8.GetString(ProtectedBytes.Unprotect(File.ReadAllBytes(TokenPath))) : null;
        }
        catch (Exception e) when (e is IOException or CryptographicException)
        {
            return null;
        }
    }

    static int FreePort()
    {
        var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

}
