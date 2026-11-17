using static FlexScreen.Companion.BarItem;

namespace FlexScreen.Companion;

/// <summary>
/// Zoom Workplace: the meeting keys, its Team Chat and search between meetings. In a meeting
/// the call panel's controls (pressed on Zoom's own toolbar, <see cref="ZoomControl"/>),
/// whiteboard and annotation, and its meeting shortcuts.
/// </summary>
sealed class ZoomApp(AppServices services) : IAppIntegration
{
    const int Color = 0x2D8CFF;

    // Zoom Workplace's own window, between meetings: its Chat is Team Chat.
    static readonly AppBank Own = new("", Color,
        [.. MeetingKeys.Items("zoom"), A("meet-chat", "Chat", "chat", "meet.chat:zoom"), K("search", "Search", "search", "Ctrl+F")],
        [.. MeetingKeys.Default, "meet-chat"]);

    static readonly AppBank InCall = new("", Color,
    [
        .. MeetingKeys.Items("zoom"), A("meet-chat", "Chat", "chat", "call.chat"), A("share", "Share", "screen", "call.share"),
        A("leave", "Leave", "leave", "call.leave"), K("participants", "Participants", "users", "Alt+U"),
        K("reactions", "Reactions", "smile", "Ctrl+Shift+Y"), A("whiteboard", "Whiteboard", "whiteboard", "call.whiteboard"),
        A("annotate", "Annotate", "pen", "call.annotate"), A("hand", "Raise hand", "hand", "call.hand"),
        K("record", "Record", "target", "Alt+R"), K("full-screen", "Full screen", "expand", "Alt+F"),
        K("clap", "Clap", "hand", "Alt+Shift+4"), K("thumbs-up", "Thumbs up", "thumbs-up", "Alt+Shift+5"),
        K("heart", "Heart", "heart", "Alt+Shift+6"), K("laugh", "Laugh", "smile", "Alt+Shift+7"),
        A("show-call", "Show call", "window", "call.focus"),
    ], [.. MeetingKeys.Default, "meet-chat", "share", "leave"]);

    public IReadOnlyList<string> Exes { get; } = ["zoom"];

    /// <summary>Zoom's own meeting (not another app's).</summary>
    public string Context(AppWindow window) => services.Call() is { Active: true, App: "zoom" } ? "call" : "";

    public AppBank? Bank(string context) => context switch
    {
        "" => Own,
        "call" => InCall,
        _ => null,
    };

    public BarItem Live(BarItem item, AppWindow window) => MeetingKeys.Live(item, services);

    /// <summary>The call, with the whiteboard's and annotation's drawing tools while they're open.</summary>
    public IReadOnlyList<string> Topics { get; } = ["call"];
}
