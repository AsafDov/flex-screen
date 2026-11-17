using static FlexScreen.Companion.BarItem;

namespace FlexScreen.Companion;

/// <summary>
/// Microsoft Teams (the new one and classic): the meeting keys, then its pages. In a meeting
/// the call panel's own controls (through Teams' API, <see cref="TeamsApi"/>), reactions and
/// sharing.
/// </summary>
sealed class TeamsApp(AppServices services) : IAppIntegration
{
    const int Color = 0x6264A7;

    static readonly BarItem[] Items =
    [
        K("activity", "Activity", "bell", "Ctrl+1"), K("chat", "Chat", "chat", "Ctrl+2"), K("teams", "Teams", "users", "Ctrl+3"),
        K("calendar", "Calendar", "calendar", "Ctrl+4"), K("calls", "Calls", "phone", "Ctrl+5"), K("new-chat", "New chat", "edit", "Ctrl+N"),
        K("search", "Search", "search", "Ctrl+E"), K("goto", "Go to", "switch", "Ctrl+G"),
    ];

    static readonly AppBank Own = new("", Color, [.. MeetingKeys.Items("teams"), .. Items], [.. MeetingKeys.Default, "chat"]);

    static readonly AppBank InCall = new("", Color,
    [
        .. MeetingKeys.Items("teams"), A("meet-chat", "Chat", "chat", "call.chat"), A("hand", "Raise hand", "hand", "call.hand"),
        K("share", "Share", "screen", "Ctrl+Shift+E"), A("leave", "Leave", "leave", "call.leave"), A("show-call", "Show call", "window", "call.focus"),
        A("react-like", "Like", "thumbs-up", "call.react:like"), A("react-love", "Love", "heart", "call.react:love"),
        A("react-applause", "Applause", "hand", "call.react:applause"), A("react-laugh", "Laugh", "smile", "call.react:laugh"),
        A("react-wow", "Wow", "surprise", "call.react:wow"),
        .. Items,
    ], [.. MeetingKeys.Default, "meet-chat", "share", "leave"]);

    public IReadOnlyList<string> Exes { get; } = ["ms-teams", "teams"];

    /// <summary>Teams' own call (not another app's).</summary>
    public string Context(AppWindow window) => services.Call() is { Active: true, App: "teams" } ? "call" : "";

    public AppBank? Bank(string context) => context switch
    {
        "" => Own,
        "call" => InCall,
        _ => null,
    };

    public BarItem Live(BarItem item, AppWindow window) => MeetingKeys.Live(item, services);

    public IReadOnlyList<string> Topics { get; } = ["call"];
}
