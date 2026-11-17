using static FlexScreen.Companion.BarItem;

namespace FlexScreen.Companion;

/// <summary>
/// Discord: the next meeting and Join, then its own shortcuts. It has no camera key and no
/// meetings: New meeting is "Start call" in the open DM; Mute and Deafen are Discord's.
/// </summary>
sealed class DiscordApp(AppServices services) : IAppIntegration
{
    static readonly AppBank Own = new("", 0x5865F2,
    [
        A("meet-next", "No meetings", "calendar", "meet.next:discord"), A("meet-join", "Join", "phone", "meet.join:discord"),
        K("meet-new", "Start call", "phone", "Ctrl+'"),
        K("switch", "Switch", "switch", "Ctrl+K"), K("prev-channel", "Prev channel", "up", "Alt+Up"),
        K("next-channel", "Next channel", "down", "Alt+Down"), K("unread", "Unread", "unread", "Alt+Shift+Down"),
        K("prev-server", "Prev server", "up", "Ctrl+Alt+Up"), K("next-server", "Next server", "down", "Ctrl+Alt+Down"),
        K("read", "Mark read", "check", "Esc"), K("search", "Search", "search", "Ctrl+F"), K("mute", "Mute", "mic-off", "Ctrl+Shift+M"),
        K("deafen", "Deafen", "headphones", "Ctrl+Shift+D"), K("inbox", "Inbox", "bell", "Ctrl+I"), K("pins", "Pins", "pin", "Ctrl+P"),
        K("upload", "Upload", "upload", "Ctrl+Shift+U"), K("emoji", "Emoji", "smile", "Ctrl+E"), K("members", "Members", "users", "Ctrl+U"),
        K("answer", "Answer call", "phone", "Ctrl+Enter"),
    ], ["meet-next", "meet-join", "mute", "deafen", "meet-new", "switch"]);

    public IReadOnlyList<string> Exes { get; } = ["discord"];

    public AppBank? Bank(string context) => context.Length == 0 ? Own : null;

    public BarItem Live(BarItem item, AppWindow window) => MeetingKeys.Live(item, services);

    public IReadOnlyList<string> Topics { get; } = ["call"];
}
