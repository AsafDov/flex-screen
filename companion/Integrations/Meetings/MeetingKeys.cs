using static FlexScreen.Companion.BarItem;

namespace FlexScreen.Companion;

/// <summary>
/// The keys every meeting app starts with (Teams, Zoom; Discord its own way): the app's next
/// meeting on the calendar, by its link (its key shows "12m · Standup"; a tap shows Today on
/// the deck), Join (that one), Mute and Camera (the call's, showing its state; Mute is
/// Windows' mic outside a call), and the app's own New meeting. The call itself is
/// <see cref="CallSource"/>'s.
/// </summary>
static class MeetingKeys
{
    public static BarItem[] Items(string app) =>
    [
        A("meet-next", "No meetings", "calendar", $"meet.next:{app}"), A("meet-join", "Join", "phone", $"meet.join:{app}"),
        A("mute", "Mute", "mic", "call.mic"), A("camera", "Camera", "video", "call.camera"),
        A("meet-new", "New meeting", "plus", $"meet.new:{app}"),
    ];

    public static readonly string[] Default = ["meet-next", "meet-join", "mute", "camera", "meet-new"];

    /// <summary>The next meeting's countdown; Mute and Camera with the call's state.</summary>
    public static BarItem Live(BarItem item, AppServices services)
    {
        switch (item.Id)
        {
            case "meet-next": return item with { Name = Label(services.NextMeeting(item.Target.Split(':').Last()), DateTimeOffset.UtcNow) };
            case "mute" when item.Target == "call.mic":
                var muted = services.Call() is { Active: true } inCall ? inCall.MicMuted : services.MicMuted();
                return item with { Name = muted ? "Muted" : "Mute", Glyph = muted ? "mic-off" : "mic" };
            case "camera" when item.Target == "call.camera" && services.Call() is { Active: true, Camera: >= 0 } withCamera:
                return item with { Name = withCamera.Camera == 1 ? "Cam on" : "Cam off" };
        }
        return item;
    }

    /// <summary>"12m · Standup", "Now · …", "3h · …": the time first, so a long title only cuts its own end; "No meetings" without one.</summary>
    public static string Label(CalendarEvent? next, DateTimeOffset now)
    {
        if (next == null) return "No meetings";
        if (next.Start <= now) return $"Now · {next.Title}";
        var minutes = (int)Math.Ceiling((next.Start - now).TotalMinutes);
        return minutes < 60 ? $"{minutes}m · {next.Title}" : $"{minutes / 60}h · {next.Title}";
    }
}
