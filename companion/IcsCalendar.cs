using System.Net;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// A calendar from an iCalendar link (.ics, webcal://): a published Outlook calendar, Google's or
/// iCloud's secret address, a school's or a team's. No sign-in: the link is the key, so it's
/// kept on this PC with the sign-ins (Secrets' "&lt;id&gt;-url"), unless the profile's entry has a
/// "url" itself (a public calendar's: holidays, a sports team).
/// <code>
/// "ics-school": { "type": "ics", "label": "School" }
/// </code>
/// </summary>
sealed class IcsCalendar(string id, JsonObject config) : ICalendarProvider
{
    static readonly HttpClient Http = MakeHttp();
    static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(4);  // a feed read again at most this often

    string _fetchedUrl = "";
    string _text = "";
    DateTime _fetchedAt = DateTime.MinValue;

    public string Id => id;
    public JsonObject Config { get; set; } = config;
    public string Label => ((string?)Config["label"] ?? "").Trim();
    public string Name => Label.Length > 0 ? Label : "Calendar link";

    /// <summary>The feed's address on this PC: the profile's "url", else the one kept here; null when there's none.</summary>
    public string? Url => (string?)Config["url"] is { Length: > 0 } url ? url : Secrets.Load(UrlName);

    /// <summary>Where this PC keeps the link (Secrets): "ics-school-url".</summary>
    public string UrlName => $"{id}-url";

    public async Task<CalendarRead> ReadAsync(DateTimeOffset from, DateTimeOffset to)
    {
        var calendars = new List<CalendarInfo> { new("ics", Name, true, true) };
        if (Normalize(Url) is not { } url) return new CalendarRead("setup", [], calendars);
        if (url != _fetchedUrl || DateTime.UtcNow - _fetchedAt > KeepFor)
        {
            using var reply = await Http.GetAsync(url);
            if (!reply.IsSuccessStatusCode)
                throw new HttpRequestException($"the link answered {(int)reply.StatusCode} {reply.ReasonPhrase}", null, reply.StatusCode);
            _text = await reply.Content.ReadAsStringAsync();
            _fetchedUrl = url;
            _fetchedAt = DateTime.UtcNow;
            if (!_text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("the link doesn't give an iCalendar feed (no BEGIN:VCALENDAR)");
        }
        var events = Ics.Expand(_text, from, to).Select(o => new CalendarEvent(
            $"{id}/{o.Uid}/{o.Start.UtcDateTime:yyyyMMddTHHmm}", o.Title, o.Start, o.End, o.AllDay, MeetingLinks.Find(o.Text))
        {
            Calendar = Label,
            Color = ContextBar.Color(Config["color"]),
            Uid = o.Uid,
            Free = o.Free,
        }).ToList();
        return new CalendarRead("ok", events, calendars);
    }

    /// <summary>webcal:// as https://; null for anything that isn't a web address.</summary>
    public static string? Normalize(string? url)
    {
        url = url?.Trim();
        if (string.IsNullOrEmpty(url)) return null;
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url["webcal://".Length..];
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.AbsoluteUri : null;
    }

    static HttpClient MakeHttp()
    {
        var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("flex-screen-companion");
        return http;
    }
}
