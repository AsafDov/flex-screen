using System.Globalization;
using System.Text;

namespace FlexScreen.Companion;

/// <summary>
/// iCalendar (RFC 5545), as far as a day's calendar needs: the VEVENTs of a feed (a published
/// Outlook calendar, Google's or iCloud's secret address, a school's or a team's), their times
/// (UTC, a time zone by TZID, floating, all-day), and repeats expanded over a span: RRULE with
/// FREQ DAILY, WEEKLY, MONTHLY or YEARLY, INTERVAL, COUNT, UNTIL, BYDAY (with an ordinal for
/// "the 2nd Tuesday", "the last Friday"), BYMONTHDAY and BYMONTH; EXDATE; and a changed or
/// cancelled occurrence (RECURRENCE-ID). Not BYSETPOS, BYWEEKNO, BYYEARDAY or RDATE.
/// </summary>
static class Ics
{
    /// <param name="Text">Its location, description and URL: where a meeting link would be.</param>
    public record Occurrence(string Uid, string Title, DateTimeOffset Start, DateTimeOffset End, bool AllDay, bool Free, string Text);

    sealed class Event
    {
        public string Uid = "", Title = "", Text = "", Rule = "";
        public Moment? Start, End, RecurrenceId;
        public TimeSpan? Duration;
        public bool Cancelled, Free;
        public List<DateTimeOffset> Except = [];
    }

    /// <summary>A time as written: a wall-clock time and its zone (UTC, a TZID, or this PC's for floating and all-day ones).</summary>
    readonly record struct Moment(DateTime Wall, TimeZoneInfo Zone, bool Date)
    {
        public DateTimeOffset Instant => ToInstant(Wall, Zone);
    }

    const int MaxSteps = 20000;  // periods walked through for one repeating event, at most

    /// <summary>Every occurrence that overlaps [from, to), soonest first.</summary>
    public static List<Occurrence> Expand(string text, DateTimeOffset from, DateTimeOffset to)
    {
        var events = Parse(text);
        var changed = events.Where(e => e.RecurrenceId != null && e.Uid.Length > 0)
            .GroupBy(e => e.Uid)
            .ToDictionary(g => g.Key, g => g.Select(e => e.RecurrenceId!.Value.Instant).ToHashSet());
        var result = new List<Occurrence>();
        foreach (var e in events)
        {
            if (e.Start is not { } start) continue;
            var length = Length(e, start);
            if (e.RecurrenceId != null || e.Rule.Length == 0)
            {
                if (e.Cancelled) continue;
                Add(result, e, start.Instant, length, start.Date, from, to);
                continue;
            }
            if (e.Cancelled) continue;
            var skip = changed.GetValueOrDefault(e.Uid) ?? [];
            foreach (var wall in Repeat(e.Rule, start, to.UtcDateTime.AddDays(1)))
            {
                var at = ToInstant(wall, start.Zone);
                if (at >= to) break;
                if (skip.Contains(at) || e.Except.Contains(at)) continue;
                Add(result, e, at, length, start.Date, from, to);
            }
        }
        return result.OrderBy(o => o.Start).ToList();
    }

    static void Add(List<Occurrence> into, Event e, DateTimeOffset start, TimeSpan length, bool allDay, DateTimeOffset from, DateTimeOffset to)
    {
        var end = start + length;
        if (start < to && end > from) into.Add(new Occurrence(e.Uid, e.Title.Length > 0 ? e.Title : "(no title)", start, end, allDay, e.Free, e.Text));
    }

    /// <summary>How long it lasts: to DTEND, else its DURATION, else a day (all-day) or nothing.</summary>
    static TimeSpan Length(Event e, Moment start)
    {
        if (e.End is { } end)
        {
            var length = end.Date && start.Date ? end.Wall.Date - start.Wall.Date : end.Instant - start.Instant;
            if (length > TimeSpan.Zero) return length;
        }
        if (e.Duration is { } d && d > TimeSpan.Zero) return d;
        return start.Date ? TimeSpan.FromDays(1) : TimeSpan.Zero;
    }

    // ---- reading

    static List<Event> Parse(string text)
    {
        TimeZoneInfo? calendarZone = null;  // X-WR-TIMEZONE: the zone of times written without one
        // Unfolded: a line that starts with a space or a tab goes on the one before.
        var unfolded = text.Replace("\r\n ", "").Replace("\r\n\t", "").Replace("\n ", "").Replace("\n\t", "");
        var events = new List<Event>();
        Event? e = null;
        var depth = 0;  // inside a VALARM (or anything else) within the event
        foreach (var raw in unfolded.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var (name, parameters, value) = Split(line);
            if (name == "BEGIN")
            {
                if (value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase) && e == null) e = new Event();
                else if (e != null) depth++;
                continue;
            }
            if (name == "END")
            {
                if (e != null && depth > 0) depth--;
                else if (e != null && value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    events.Add(e);
                    e = null;
                }
                continue;
            }
            if (name == "X-WR-TIMEZONE" && e == null)
            {
                calendarZone = Zone(value);
                continue;
            }
            if (e == null || depth > 0) continue;
            switch (name)
            {
                case "UID": e.Uid = value.Trim(); break;
                case "SUMMARY": e.Title = Unescape(value).Trim(); break;
                case "LOCATION":
                case "DESCRIPTION":
                case "URL":
                    e.Text += " " + Unescape(value);
                    break;
                case "DTSTART": e.Start = Time(value, parameters, calendarZone); break;
                case "DTEND": e.End = Time(value, parameters, calendarZone); break;
                case "DURATION": e.Duration = Duration(value); break;
                case "RRULE": e.Rule = value.Trim(); break;
                case "RECURRENCE-ID": e.RecurrenceId = Time(value, parameters, calendarZone); break;
                case "STATUS": e.Cancelled = value.Trim().Equals("CANCELLED", StringComparison.OrdinalIgnoreCase); break;
                case "TRANSP": e.Free = value.Trim().Equals("TRANSPARENT", StringComparison.OrdinalIgnoreCase); break;
                case "EXDATE":
                    foreach (var one in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (Time(one, parameters, calendarZone) is { } t) e.Except.Add(t.Instant);
                    }
                    break;
            }
        }
        return events;
    }

    /// <summary>"DTSTART;TZID=Europe/Paris:20261108T090000" → ("DTSTART", {TZID: Europe/Paris}, "20261108T090000").</summary>
    static (string Name, Dictionary<string, string> Parameters, string Value) Split(string line)
    {
        var quoted = false;
        var colon = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted)
            {
                colon = i;
                break;
            }
        }
        if (colon < 0) return ("", [], "");
        var head = line[..colon].Split(';');
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in head.Skip(1))
        {
            var eq = p.IndexOf('=');
            if (eq > 0) parameters[p[..eq]] = p[(eq + 1)..].Trim('"');
        }
        return (head[0].ToUpperInvariant(), parameters, line[(colon + 1)..]);
    }

    static string Unescape(string value)
    {
        var s = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var c = value[++i];
                s.Append(c is 'n' or 'N' ? '\n' : c);
            }
            else s.Append(value[i]);
        }
        return s.ToString();
    }

    static Moment? Time(string value, Dictionary<string, string> parameters, TimeZoneInfo? calendarZone)
    {
        value = value.Trim();
        var date = parameters.TryGetValue("VALUE", out var kind) && kind.Equals("DATE", StringComparison.OrdinalIgnoreCase) || value.Length == 8;
        if (date)
        {
            return DateTime.TryParseExact(value[..Math.Min(8, value.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? new Moment(day, TimeZoneInfo.Local, true)
                : null;
        }
        var utc = value.EndsWith('Z');
        if (!DateTime.TryParseExact(value.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall)) return null;
        var zone = utc ? TimeZoneInfo.Utc
                 : parameters.TryGetValue("TZID", out var tzid) ? Zone(tzid) ?? calendarZone ?? TimeZoneInfo.Local
                 : calendarZone ?? TimeZoneInfo.Local;  // floating: wherever the PC is
        return new Moment(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), zone, false);
    }

    /// <summary>A TZID as Windows or IANA name it ("W. Europe Standard Time", "Europe/Berlin"), or Outlook's "(UTC+01:00) Amsterdam, …" by the offset; null if none fits.</summary>
    static TimeZoneInfo? Zone(string tzid)
    {
        tzid = tzid.Trim().Trim('"');
        if (tzid.StartsWith('/')) tzid = tzid.TrimStart('/');  // "/mozilla.org/…/Europe/Paris" kinds
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(tzid);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
        }
        var slash = tzid.LastIndexOf('/');
        if (slash > 0)
        {
            for (var i = tzid.IndexOf('/'); i > 0 && i <= slash; i = tzid.IndexOf('/', i + 1))
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(tzid[(i + 1)..]);
                }
                catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                }
            }
        }
        return TimeZoneInfo.GetSystemTimeZones().FirstOrDefault(z => z.DisplayName.Equals(tzid, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"PT1H30M", "P1D", "P1W", "-PT15M".</summary>
    static TimeSpan? Duration(string value)
    {
        value = value.Trim().ToUpperInvariant();
        var sign = value.StartsWith('-') ? -1 : 1;
        value = value.TrimStart('+', '-');
        if (!value.StartsWith('P')) return null;
        var total = TimeSpan.Zero;
        var number = 0;
        var time = false;
        foreach (var c in value[1..])
        {
            if (char.IsDigit(c)) number = number * 10 + (c - '0');
            else
            {
                switch (c)
                {
                    case 'T': time = true; break;
                    case 'W': total += TimeSpan.FromDays(7 * number); break;
                    case 'D': total += TimeSpan.FromDays(number); break;
                    case 'H': total += TimeSpan.FromHours(number); break;
                    case 'M' when time: total += TimeSpan.FromMinutes(number); break;
                    case 'S': total += TimeSpan.FromSeconds(number); break;
                }
                number = 0;
            }
        }
        return sign * total;
    }

    static DateTimeOffset ToInstant(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        // A wall time a clock change skips (02:30 the night clocks go forward) is an hour on.
        if (zone.IsInvalidTime(wall)) wall = wall.AddHours(1);
        var offset = zone.GetUtcOffset(wall);
        return new DateTimeOffset(wall, offset);
    }

    // ---- repeating

    static readonly Dictionary<string, DayOfWeek> Days = new()
    {
        ["MO"] = DayOfWeek.Monday, ["TU"] = DayOfWeek.Tuesday, ["WE"] = DayOfWeek.Wednesday, ["TH"] = DayOfWeek.Thursday,
        ["FR"] = DayOfWeek.Friday, ["SA"] = DayOfWeek.Saturday, ["SU"] = DayOfWeek.Sunday,
    };

    /// <summary>The wall-clock starts of a repeating event, from its first, in order, until `until` (UTC) or the rule's end.</summary>
    static IEnumerable<DateTime> Repeat(string rule, Moment first, DateTime until)
    {
        var parts = rule.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim().ToUpperInvariant(), p => p[1].Trim().ToUpperInvariant());
        var freq = parts.GetValueOrDefault("FREQ", "");
        var interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), out var n) && n > 0 ? n : 1;
        int? count = int.TryParse(parts.GetValueOrDefault("COUNT"), out var c) && c > 0 ? c : null;
        DateTimeOffset? end = null;
        if (parts.TryGetValue("UNTIL", out var u))
        {
            var untilTime = Time(u, [], first.Zone);
            if (untilTime is { } t) end = t.Date ? ToInstant(t.Wall.AddDays(1).AddTicks(-1), first.Zone) : t.Instant;
        }
        var byDay = (parts.GetValueOrDefault("BYDAY") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(d => d.Length >= 2)
            .Select(d => (Ordinal: int.TryParse(d[..^2], out var o) ? o : 0, Day: Days.GetValueOrDefault(d[^2..], (DayOfWeek)(-1))))
            .Where(d => (int)d.Day >= 0).ToList();
        var byMonthDay = (parts.GetValueOrDefault("BYMONTHDAY") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => int.TryParse(d, out var v) ? v : 0).Where(v => v != 0).ToList();
        var byMonth = (parts.GetValueOrDefault("BYMONTH") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(m => int.TryParse(m, out var v) ? v : 0).Where(v => v is >= 1 and <= 12).ToHashSet();
        var start = first.Wall;
        var time = start.TimeOfDay;
        var emitted = 0;

        IEnumerable<DateTime> Period(int k)
        {
            switch (freq)
            {
                case "DAILY":
                {
                    var day = start.Date.AddDays((long)k * interval);
                    if ((byMonth.Count == 0 || byMonth.Contains(day.Month)) && (byDay.Count == 0 || byDay.Any(d => d.Day == day.DayOfWeek)) &&
                        (byMonthDay.Count == 0 || byMonthDay.Any(m => DayOfMonth(day.Year, day.Month, m) == day.Day)))
                        yield return day + time;
                    break;
                }
                case "WEEKLY":
                {
                    var monday = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7)).AddDays(7L * k * interval);
                    var days = byDay.Count > 0 ? byDay.Select(d => d.Day).Distinct() : [start.DayOfWeek];
                    foreach (var day in days.Select(d => monday.AddDays(((int)d + 6) % 7)).Order())
                    {
                        if (byMonth.Count == 0 || byMonth.Contains(day.Month)) yield return day + time;
                    }
                    break;
                }
                case "MONTHLY":
                {
                    var month = new DateTime(start.Year, start.Month, 1).AddMonths(k * interval);
                    if (byMonth.Count > 0 && !byMonth.Contains(month.Month)) break;
                    foreach (var day in InMonth(month.Year, month.Month)) yield return day + time;
                    break;
                }
                case "YEARLY":
                {
                    var year = start.Year + k * interval;
                    if (year > 9998) break;
                    var months = byMonth.Count > 0 ? byMonth.Order() : (IEnumerable<int>)[start.Month];
                    foreach (var m in months)
                    {
                        foreach (var day in InMonth(year, m)) yield return day + time;
                    }
                    break;
                }
            }
        }

        // The days of a month the rule picks: BYMONTHDAY, else BYDAY (the nth one, or each),
        // else the first one's day of the month (none in a month too short for it).
        IEnumerable<DateTime> InMonth(int year, int month)
        {
            var days = new List<DateTime>();
            var length = DateTime.DaysInMonth(year, month);
            if (byMonthDay.Count > 0)
            {
                days.AddRange(byMonthDay.Select(m => DayOfMonth(year, month, m)).Where(d => d is >= 1 && d <= length).Select(d => new DateTime(year, month, d)));
            }
            else if (byDay.Count > 0 && freq is "MONTHLY" or "YEARLY")
            {
                foreach (var (ordinal, weekday) in byDay)
                {
                    var all = Enumerable.Range(1, length).Select(d => new DateTime(year, month, d)).Where(d => d.DayOfWeek == weekday).ToList();
                    if (ordinal == 0) days.AddRange(all);
                    else if (ordinal > 0 && ordinal <= all.Count) days.Add(all[ordinal - 1]);
                    else if (ordinal < 0 && -ordinal <= all.Count) days.Add(all[all.Count + ordinal]);
                }
            }
            else if (start.Day <= length)
            {
                days.Add(new DateTime(year, month, start.Day));
            }
            return days.Distinct().Order();
        }

        if (freq is not ("DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY"))
        {
            yield return start;  // a rule this doesn't follow: the first one, at least
            yield break;
        }
        for (var k = 0; k < MaxSteps; k++)
        {
            // A period that begins after the span is over (a day of margin for time zones):
            // nothing in it or after it is wanted, even when it has no occurrence of its own.
            var begins = freq switch
            {
                "DAILY" => start.Date.AddDays((long)k * interval),
                "WEEKLY" => start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7)).AddDays(7L * k * interval),
                "MONTHLY" => new DateTime(start.Year, start.Month, 1).AddMonths(k * interval),
                _ => start.Year + k * interval > 9998 ? DateTime.MaxValue.Date : new DateTime(start.Year + k * interval, 1, 1),
            };
            if (begins > until.AddDays(1)) yield break;
            foreach (var at in Period(k))
            {
                if (at < start) continue;
                var instant = ToInstant(at, first.Zone);
                if (end != null && instant > end) yield break;
                if (instant.UtcDateTime > until) yield break;
                yield return at;
                if (count != null && ++emitted >= count) yield break;
            }
        }
    }

    /// <summary>BYMONTHDAY's day: 15 is the 15th, -1 the last day of the month.</summary>
    static int DayOfMonth(int year, int month, int byMonthDay) =>
        byMonthDay > 0 ? byMonthDay : DateTime.DaysInMonth(year, month) + byMonthDay + 1;
}
