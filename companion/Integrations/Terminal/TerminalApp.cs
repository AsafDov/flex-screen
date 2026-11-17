using static FlexScreen.Companion.BarItem;

namespace FlexScreen.Companion;

/// <summary>Windows Terminal: history, tabs, panes, a few commands typed, and Claude Code's keys for a session running in it.</summary>
sealed class TerminalApp(ClaudeKeys claude) : IAppIntegration
{
    static readonly AppBank Own = new("", 0xE8A33D,
    [
        K("prev-cmd", "Prev cmd", "up", "Up"), K("next-cmd", "Next cmd", "down", "Down"), K("complete", "Complete", "forward", "Tab"),
        K("stop", "Stop", "stop", "Ctrl+C"), K("clear", "Clear", "minus", "Ctrl+L"), K("search-history", "History", "history", "Ctrl+R"),
        K("new-tab", "New tab", "plus", "Ctrl+Shift+T"), K("prev-tab", "Prev tab", "prev", "Ctrl+Shift+Tab"),
        K("next-tab", "Next tab", "next", "Ctrl+Tab"), K("dup-tab", "Duplicate tab", "copy", "Ctrl+Shift+D"),
        K("split-right", "Split right", "split-right", "Alt+Shift+plus"), K("split-down", "Split down", "split-down", "Alt+Shift+minus"),
        K("pane-left", "Pane left", "back", "Alt+Left"), K("pane-right", "Pane right", "forward", "Alt+Right"),
        K("close", "Close pane", "close", "Ctrl+Shift+W"), K("find", "Find", "search", "Ctrl+Shift+F"),
        K("copy", "Copy", "copy", "Ctrl+Shift+C"), K("paste", "Paste", "paste", "Ctrl+Shift+V"),
        K("scroll-up", "Scroll up", "up", "Ctrl+Shift+PageUp"), K("scroll-down", "Scroll down", "down", "Ctrl+Shift+PageDown"),
        K("palette", "Commands", "keyboard", "Ctrl+Shift+P"), K("zoom-in", "Bigger", "zoom-in", "Ctrl+plus"),
        K("zoom-out", "Smaller", "zoom-out", "Ctrl+minus"), K("settings", "Settings", "settings", "Ctrl+comma"),
        T("git-status", "git status", "branch", "git status\n"), T("git-pull", "git pull", "download", "git pull\n"),
        T("start-claude", "Start Claude", "chat", "claude\n"),
        .. ClaudeKeys.Items,
    ], ["prev-cmd", "new-tab", "prev-tab", "next-tab", "split-right", "split-down"]);

    public IReadOnlyList<string> Exes { get; } = ["windowsterminal"];

    public AppBank? Bank(string context) => context.Length == 0 ? Own : null;

    public BarItem Live(BarItem item, AppWindow window) => claude.Live(item, window);

    public bool Run(BarItem key, AppWindow window) => claude.Run(key, window);

    public void Choose(BarItem key, string choice, AppWindow window) => claude.Choose(key, choice, window);
}
