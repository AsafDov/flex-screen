using static FlexScreen.Companion.BarItem;

namespace FlexScreen.Companion;

/// <summary>The Claude app: Claude Code's keys (<see cref="ClaudeKeys"/>), then its own.</summary>
sealed class ClaudeApp(ClaudeKeys claude) : IAppIntegration
{
    static readonly AppBank Own = new("", 0xD97757,
    [
        .. ClaudeKeys.Items,
        K("new-chat", "New chat", "plus", "Ctrl+N"), K("stop", "Stop", "stop", "Esc"), K("search-chats", "Search chats", "search", "Ctrl+K"),
    ], ["claude-approve", "claude-deny", "claude-model", "claude-effort", "claude-usage", "new-chat"]);

    public IReadOnlyList<string> Exes { get; } = ["claude"];

    public AppBank? Bank(string context) => context.Length == 0 ? Own : null;

    public BarItem Live(BarItem item, AppWindow window) => claude.Live(item, window);

    public bool Run(BarItem key, AppWindow window) => claude.Run(key, window);

    public void Choose(BarItem key, string choice, AppWindow window) => claude.Choose(key, choice, window);

    /// <summary>Claude Code's, wherever it runs: its sessions, the 5-hour usage, approvals and questions (the hooks send them).</summary>
    public IReadOnlyList<string> Topics { get; } = ["claude", "usage", "approval", "question"];
}
