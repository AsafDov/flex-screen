using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// One Google account's calendars as a calendar source: the ones ticked in Google Calendar (its
/// own and the ones shared with it), less the account's "calendars" "hide", plus its "show"
/// (<see cref="CalendarChoice"/>). Free/busy-only shares are left out, since they have no
/// event details to show.
/// </summary>
sealed class GoogleCalendar(GoogleAccount account) : ICalendarProvider
{
    const string Api = "https://www.googleapis.com/calendar/v3";

    public GoogleAccount Account => account;
    public string Id => account.Id;
    public string Name => account.Name;

    public async Task<CalendarRead> ReadAsync(DateTimeOffset from, DateTimeOffset to)
    {
        var status = await account.AuthStatusAsync();
        if (status != "ok") return new CalendarRead(status, [], []);
        var config = Settings.Accounts("google").FirstOrDefault(a => a.Id == account.Id).Config;
        var list = await account.SendAsync(HttpMethod.Get, $"{Api}/users/me/calendarList?minAccessRole=reader&maxResults=250");
        var calendars = new List<(CalendarInfo Info, bool Primary, int? Color)>();
        foreach (var c in list?["items"] as JsonArray ?? [])
        {
            if ((string?)c?["id"] is not { } id || (bool?)c?["deleted"] == true) continue;
            var primary = (bool?)c?["primary"] == true;
            if (primary) account.Email = id;  // the primary calendar's id is the account's address
            var name = (string?)c?["summaryOverride"] ?? (string?)c?["summary"] ?? id;
            var ticked = (bool?)c?["selected"] == true && (bool?)c?["hidden"] != true;
            calendars.Add((new CalendarInfo(id, name, CalendarChoice.Shown(config, id, name, ticked), ticked), primary, ContextBar.Color(c?["backgroundColor"])));
        }
        if (calendars.Count == 0) calendars.Add((new CalendarInfo("primary", account.Email, true, true), true, null));
        var span = $"&timeMin={Uri.EscapeDataString(from.ToString("o"))}&timeMax={Uri.EscapeDataString(to.ToString("o"))}";
        var shown = calendars.Where(c => c.Info.Shown).ToList();
        var replies = await Task.WhenAll(shown.Select(c => EventsAsync(c.Info, c.Primary, c.Color, span)));
        return new CalendarRead("ok", replies.SelectMany(r => r).ToList(), calendars.Select(c => c.Info).ToList());
    }

    /// <summary>One calendar's events in the span; none if it can't be read (the share was
    /// withdrawn since the list was read, say), so one calendar doesn't blank the rest.</summary>
    async Task<List<CalendarEvent>> EventsAsync(CalendarInfo calendar, bool primary, int? color, string span)
    {
        JsonNode? json;
        try
        {
            json = await account.SendAsync(HttpMethod.Get, $"{Api}/calendars/{Uri.EscapeDataString(calendar.Id)}/events" +
                                                           "?singleEvents=true&orderBy=startTime&maxResults=100" + span);
        }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            Log.Write($"calendar: {account.Id}: skipped a calendar that can't be read ({(int)e.StatusCode!})");
            return [];
        }
        // The deck names the calendar unless it's the account's own: then the account's label, if any.
        var label = primary ? account.Label : calendar.Name;
        var events = new List<CalendarEvent>();
        foreach (var e in json?["items"] as JsonArray ?? [])
        {
            if (e == null || (string?)e["status"] == "cancelled") continue;
            var start = Time(e["start"]);
            var end = Time(e["end"]);
            if (start == null || end == null) continue;
            var id = (string?)e["id"] ?? "";
            events.Add(new CalendarEvent(id, (string?)e["summary"] ?? "(no title)", start.Value, end.Value, e["start"]?["date"] != null, JoinLink(e))
            {
                Calendar = label,
                Color = color,
                Uid = (string?)e["iCalUID"] ?? "",
                // "self" is the calendar this copy is on.
                Declined = (e["attendees"] as JsonArray ?? []).Any(a => (bool?)a?["self"] == true && (string?)a?["responseStatus"] == "declined"),
                Free = (string?)e["transparency"] == "transparent",
            });
        }
        return events;
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
        return MeetingLinks.Find($"{(string?)e["location"]} {(string?)e["description"]}");
    }
}
