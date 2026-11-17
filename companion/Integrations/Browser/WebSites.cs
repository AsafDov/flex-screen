using static FlexScreen.Companion.BarItem;

namespace FlexScreen.Companion;

/// <summary>A site with keys of its own in a browser.</summary>
/// <param name="Id">The browser's context for it: the bar's key is "chrome:gmail".</param>
/// <param name="Match">By the tab's title.</param>
record WebSite(string Id, Func<string, bool> Match, AppBank Bank);

/// <summary>The sites a browser's bar has keys for.</summary>
static class WebSites
{
    /// <summary>The first that matches wins. Gmail's keys need its keyboard shortcuts on (Gmail settings, off by default).</summary>
    public static readonly WebSite[] All =
    [
        new("gmail", t => t.EndsWith(" - Gmail", StringComparison.Ordinal) || t.Contains("@gmail.com"), new("Gmail", 0xEA4335,
        [
            K("compose", "Compose", "edit", "c"), K("reply", "Reply", "reply", "r"), K("reply-all", "Reply all", "reply", "a"),
            K("forward-mail", "Forward", "share", "f"), K("archive", "Archive", "download", "e"), K("delete", "Delete", "trash", "Shift+3"),
            K("unread", "Mark unread", "eye", "Shift+U"), K("newer", "Newer", "up", "k"), K("older", "Older", "down", "j"),
            K("search", "Search mail", "search", "slash"), K("snooze", "Snooze", "moon", "b"), K("inbox", "Inbox", "home", "g, i"),
            K("send", "Send", "check", "Ctrl+Enter"),
        ], ["compose", "reply", "archive", "delete", "newer", "older", "search"])),
        new("youtube", t => t.EndsWith("- YouTube", StringComparison.Ordinal) || t == "YouTube", new("YouTube", 0xFF0033,
        [
            K("back10", "Back 10 s", "back", "j"), K("play", "Play/pause", "play", "k"), K("ahead10", "Ahead 10 s", "forward", "l"),
            K("mute", "Mute", "volume", "m"), K("captions", "Captions", "comment", "c"), K("theater", "Theater", "window", "t"),
            K("fullscreen-video", "Full screen", "expand", "f"), K("slower", "Slower", "minus", "Shift+comma"),
            K("faster", "Faster", "plus", "Shift+period"), K("next-video", "Next video", "next-track", "Shift+N"),
            K("miniplayer", "Mini player", "window", "i"),
        ], ["back10", "play", "ahead10", "mute", "captions", "fullscreen-video"])),
        new("calendar", t => t.Contains("Google Calendar", StringComparison.Ordinal), new("Calendar", 0x4285F4,
        [
            K("prev-period", "Previous", "prev", "k"), K("today", "Today", "calendar", "t"), K("next-period", "Next", "next", "j"),
            K("day", "Day", "list", "d"), K("week", "Week", "split-right", "w"), K("month", "Month", "calendar", "m"),
            K("new-event", "New event", "plus", "c"), K("search-cal", "Search", "search", "slash"), K("agenda", "Agenda", "line", "a"),
        ], ["prev-period", "today", "next-period", "day", "week", "month", "new-event"])),
    ];

    public static WebSite? Of(string title) => All.FirstOrDefault(s => s.Match(title));
}
