using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// A Microsoft 365 or Outlook.com account's calendar, through Microsoft Graph, with the user's
/// own app registration (as for Microsoft To Do: the account's "clientId", else the profile's
/// "microsoftTodo" one, which then needs Graph's delegated Calendars.Read too). The account's
/// main calendar goes on the deck, and any other it picks (<see cref="CalendarChoice"/>). Its
/// refresh token is this PC's (Secrets' "&lt;id&gt;-token").
/// <code>
/// "microsoft-work": { "type": "microsoft", "label": "Work", "clientId": "…", "tenant": "organizations" }
/// </code>
/// </summary>
sealed class MicrosoftCalendar(string id, JsonObject config) : ICalendarProvider
{
    const string Scopes = "openid offline_access User.Read Calendars.Read";
    const string Api = "https://graph.microsoft.com/v1.0";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    readonly SemaphoreSlim _tokenGate = new(1, 1);
    string? _access;
    string _accessClient = "";
    DateTime _expires;

    public string Id => id;
    public JsonObject Config { get; set; } = config;
    public string Label => ((string?)Config["label"] ?? "").Trim();
    public string Name => Label.Length > 0 ? $"Outlook ({Label})" : "Outlook";

    /// <summary>Who signed in, once it's been read; "" before.</summary>
    public string Email { get; private set; } = "";

    string TokenName => $"{id}-token";

    /// <summary>The app registration: the account's, else Microsoft To Do's.</summary>
    public (string ClientId, string Tenant)? Client()
    {
        var todo = Settings.Source("microsoftTodo");
        var clientId = ((string?)Config["clientId"] ?? (string?)todo?["clientId"] ?? "").Trim();
        if (clientId.Length == 0) return null;
        var tenant = ((string?)Config["tenant"] ?? (Config.ContainsKey("clientId") ? null : (string?)todo?["tenant"]) ?? "common").Trim();
        return (clientId, tenant.Length > 0 ? tenant : "common");
    }

    public bool HasSignIn => Secrets.Load(TokenName) != null;

    /// <summary>The browser consent, from the tray menu; Microsoft's account picker first.</summary>
    /// <returns>Whether it signed in.</returns>
    public async Task<bool> SignInAsync()
    {
        if (Client() is not { } client) return false;
        var pending = OAuthLoopback.Prepare();
        var url = $"https://login.microsoftonline.com/{Uri.EscapeDataString(client.Tenant)}/oauth2/v2.0/authorize?response_type=code" +
                  $"&response_mode=query&prompt=select_account&client_id={Uri.EscapeDataString(client.ClientId)}" +
                  $"&redirect_uri={Uri.EscapeDataString(pending.Redirect)}&scope={Uri.EscapeDataString(Scopes)}" +
                  $"&code_challenge={pending.Challenge}&code_challenge_method=S256&state={pending.State}";
        if (await OAuthLoopback.CodeAsync(pending, url, Name) is not { } code) return false;
        var tokens = await PostTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = pending.Redirect,
            ["code_verifier"] = pending.Verifier, ["scope"] = Scopes,
        });
        return tokens != null;
    }

    public void SignOut()
    {
        Secrets.Delete(TokenName);
        _access = null;
        Email = "";
    }

    public async Task<CalendarRead> ReadAsync(DateTimeOffset from, DateTimeOffset to)
    {
        if (Client() == null) return new CalendarRead("setup", [], []);
        if (!await EnsureTokenAsync()) return new CalendarRead("signin", [], []);
        if (Email.Length == 0)
        {
            var me = await GetAsync($"{Api}/me?$select=mail,userPrincipalName");
            Email = (string?)me?["mail"] ?? (string?)me?["userPrincipalName"] ?? "";
        }
        var list = await GetAsync($"{Api}/me/calendars?$select=id,name,isDefaultCalendar&$top=100");
        var calendars = new List<(CalendarInfo Info, bool Main)>();
        foreach (var c in list?["value"] as JsonArray ?? [])
        {
            if ((string?)c?["id"] is not { } cid) continue;
            var main = (bool?)c?["isDefaultCalendar"] == true;
            var name = (string?)c?["name"] ?? "Calendar";
            calendars.Add((new CalendarInfo(cid, name, CalendarChoice.Shown(Config, cid, name, main), main), main));
        }
        var span = $"startDateTime={Uri.EscapeDataString(from.UtcDateTime.ToString("o"))}&endDateTime={Uri.EscapeDataString(to.UtcDateTime.ToString("o"))}";
        var reads = await Task.WhenAll(calendars.Where(c => c.Info.Shown).Select(async c =>
        {
            var json = await GetAsync($"{Api}/me/calendars/{Uri.EscapeDataString(c.Info.Id)}/calendarView?{span}&$top=200" +
                                      "&$select=id,subject,start,end,isAllDay,isCancelled,showAs,responseStatus,onlineMeeting,onlineMeetingUrl,location,bodyPreview,iCalUId");
            return (json?["value"] as JsonArray ?? []).Select(e => Event(e, c.Main ? Label : c.Info.Name)).OfType<CalendarEvent>().ToList();
        }));
        return new CalendarRead("ok", reads.SelectMany(r => r).ToList(), calendars.Select(c => c.Info).ToList());
    }

    /// <summary>One of Graph's events (times in UTC, as asked), or null for a cancelled one.</summary>
    public static CalendarEvent? Event(JsonNode? e, string calendar)
    {
        if (e == null || (bool?)e["isCancelled"] == true) return null;
        var allDay = (bool?)e["isAllDay"] == true;
        if (Time(e["start"], allDay) is not { } start || Time(e["end"], allDay) is not { } end) return null;
        var join = (string?)e["onlineMeeting"]?["joinUrl"] is { Length: > 0 } j ? j
                 : (string?)e["onlineMeetingUrl"] is { Length: > 0 } u ? u
                 : MeetingLinks.Find($"{(string?)e["location"]?["displayName"]} {(string?)e["bodyPreview"]}");
        return new CalendarEvent((string?)e["id"] ?? "", (string?)e["subject"] is { Length: > 0 } s ? s : "(no title)", start, end, allDay, join)
        {
            Calendar = calendar,
            Uid = (string?)e["iCalUId"] ?? "",
            Declined = (string?)e["responseStatus"]?["response"] == "declined",
            Free = (string?)e["showAs"] is "free" or "workingElsewhere",
        };
    }

    /// <summary>Graph's {dateTime, timeZone} (UTC, as asked for). An all-day event's is that date, at midnight here.</summary>
    static DateTimeOffset? Time(JsonNode? t, bool allDay)
    {
        if ((string?)t?["dateTime"] is not { Length: >= 10 } dt) return null;
        if (allDay) return new DateTimeOffset(DateTime.ParseExact(dt[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture));
        var utc = DateTime.Parse(dt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    async Task<JsonNode?> GetAsync(string url)
    {
        if (!await EnsureTokenAsync()) throw new HttpRequestException($"not signed in to {Name}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _access);
        request.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
        using var reply = await Http.SendAsync(request);
        var text = await reply.Content.ReadAsStringAsync();
        if (!reply.IsSuccessStatusCode)
        {
            string? reason = null;
            try
            {
                reason = (string?)JsonNode.Parse(text)?["error"]?["message"];
            }
            catch (System.Text.Json.JsonException)
            {
            }
            if (reply.StatusCode == HttpStatusCode.Unauthorized) _access = null;
            throw new HttpRequestException($"Microsoft Graph: {(int)reply.StatusCode} {reason ?? reply.ReasonPhrase}", null, reply.StatusCode);
        }
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    async Task<bool> EnsureTokenAsync()
    {
        await _tokenGate.WaitAsync();  // calendars are read side by side; one refresh for all of them
        try
        {
            if (Client() is not { } client) return false;
            if (_access != null && DateTime.UtcNow < _expires && _accessClient == client.ClientId) return true;
            _access = null;
            if (Secrets.Load(TokenName) is not { Length: > 0 } refresh) return false;
            return await PostTokenAsync(client, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["scope"] = Scopes,
            }) != null;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    /// <summary>A token request; keeps the new access token and the refresh token Microsoft sends with it (it rotates).</summary>
    async Task<JsonNode?> PostTokenAsync((string ClientId, string Tenant) client, Dictionary<string, string> form)
    {
        form["client_id"] = client.ClientId;
        using var reply = await Http.PostAsync($"https://login.microsoftonline.com/{Uri.EscapeDataString(client.Tenant)}/oauth2/v2.0/token",
                                               new FormUrlEncodedContent(form));
        var json = JsonNode.Parse(await reply.Content.ReadAsStringAsync());
        if (!reply.IsSuccessStatusCode || (string?)json?["access_token"] is not { Length: > 0 } access)
        {
            var error = (string?)json?["error"];
            Log.Write($"calendar: {id}: token request failed: {error}: {((string?)json?["error_description"])?.Split('\n')[0]}");
            if (error == "invalid_grant") Secrets.Delete(TokenName);  // revoked or expired: sign in again
            return null;
        }
        _access = access;
        _accessClient = client.ClientId;
        _expires = DateTime.UtcNow.AddSeconds(((int?)json["expires_in"] ?? 3600) - 60);
        if ((string?)json["refresh_token"] is { Length: > 0 } refresh) Secrets.Save(TokenName, refresh);
        return json;
    }
}
