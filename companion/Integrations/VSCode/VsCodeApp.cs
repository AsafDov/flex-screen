using static FlexScreen.Companion.BarItem;

namespace FlexScreen.Companion;

/// <summary>
/// VS Code. Its keys run VS Code's own commands through the flex-screen extension (whatever
/// the keybindings, and with no window focus needed); an older extension gets the keys. With
/// Claude Code's chat tab in front (the extension says so, 0.4 and later): Claude's keys, in
/// Claude's color, as in the Claude app; VS Code's after them.
/// </summary>
sealed class VsCodeApp(AppServices services, ClaudeKeys claude) : IAppIntegration
{
    static readonly AppBank Own = new("", 0x2F9BF0,
    [
        K("build", "Build", "play", "Ctrl+Shift+B", "workbench.action.tasks.build"),
        K("run", "Debug", "bug", "F5", "workbench.action.debug.start"),
        K("terminal", "Terminal", "terminal", "Ctrl+`", "workbench.action.terminal.toggleTerminal"),
        K("commands", "Commands", "keyboard", "Ctrl+Shift+P", "workbench.action.showCommands"),
        K("goto-file", "Go to file", "file", "Ctrl+P", "workbench.action.quickOpen"),
        K("goto-symbol", "Go to symbol", "at", "Ctrl+Shift+O", "workbench.action.gotoSymbol"),
        K("goto-line", "Go to line", "line", "Ctrl+G", "workbench.action.gotoLine"),
        K("definition", "Definition", "target", "F12", "editor.action.revealDefinition"),
        K("search", "Search", "search", "Ctrl+Shift+F", "workbench.action.findInFiles"),
        K("split", "Split", "split-right", "Ctrl+\\", "workbench.action.splitEditor"),
        K("sidebar", "Sidebar", "bars", "Ctrl+B", "workbench.action.toggleSidebarVisibility"),
        K("format", "Format", "format", "Shift+Alt+F", "editor.action.formatDocument"),
        K("rename", "Rename", "cursor", "F2", "editor.action.rename"),
        K("comment", "Comment", "comment", "Ctrl+/", "editor.action.commentLine"),
        K("quick-fix", "Quick fix", "bolt", "Ctrl+.", "editor.action.quickFix"),
        K("git", "Source control", "branch", "Ctrl+Shift+G", "workbench.view.scm"),
        K("problems", "Problems", "bell", "Ctrl+Shift+M", "workbench.actions.view.problems"),
        K("back", "Back", "back", "Alt+Left", "workbench.action.navigateBack"),
        K("forward", "Forward", "forward", "Alt+Right", "workbench.action.navigateForward"),
        K("close-tab", "Close tab", "close", "Ctrl+W", "workbench.action.closeActiveEditor"),
        K("reopen", "Reopen tab", "history", "Ctrl+Shift+T", "workbench.action.reopenClosedEditor"),
        K("new-terminal", "New terminal", "plus", "Ctrl+Shift+`", "workbench.action.terminal.new"),
        K("preview", "Preview", "eye", "Ctrl+Shift+V", "markdown.showPreview"),
        K("step-over", "Step over", "forward", "F10", "workbench.action.debug.stepOver"),
        K("step-into", "Step into", "down", "F11", "workbench.action.debug.stepInto"),
        K("step-out", "Step out", "up", "Shift+F11", "workbench.action.debug.stepOut"),
        K("breakpoint", "Breakpoint", "target", "F9", "editor.debug.action.toggleBreakpoint"),
        K("stop-debug", "Stop", "stop", "Shift+F5", "workbench.action.debug.stop"),
        // Claude Code's VS Code extension: its own commands (only through the flex-screen extension).
        K("claude", "Claude", "chat", "", "claude-vscode.focus"),
        K("claude-new", "New Claude chat", "plus", "", "claude-vscode.newConversation"),
        K("claude-accept", "Accept changes", "check", "", "claude-vscode.acceptProposedDiff"),
        K("claude-reject", "Reject changes", "close", "", "claude-vscode.rejectProposedDiff"),
        K("claude-mention", "Mention file", "at", "Alt+K", "claude-vscode.insertAtMention"),
        .. ClaudeKeys.Items,
    ], ["goto-file", "goto-symbol", "terminal", "search", "claude-approve", "claude-model"]);

    static readonly AppBank ClaudeTab = new AppBank("Claude Code", 0xD97757,
    [
        .. ClaudeKeys.Items,
        K("claude-new", "New chat", "plus", "", "claude-vscode.newConversation"),
        K("claude-stop", "Stop", "stop", "Esc"),
        K("claude-input", "Claude's input", "chat", "", "claude-vscode.focus"),
        K("claude-accept", "Accept changes", "check", "", "claude-vscode.acceptProposedDiff"),
        K("claude-reject", "Reject changes", "close", "", "claude-vscode.rejectProposedDiff"),
        K("claude-new-tab", "Another chat", "window", "", "claude-vscode.editor.open"),
        K("claude-reopen", "Reopen chat", "history", "", "claude-vscode.reopenClosedSession"),
        K("claude-focus-view", "Focus view", "eye", "", "claude-vscode.toggleFocusView"),
    ], ["claude-approve", "claude-deny", "claude-model", "claude-effort", "claude-usage", "claude-new"]).Over(Own);

    public IReadOnlyList<string> Exes { get; } = ["code"];

    /// <summary>Claude's tab in front, as the extension says (not the title).</summary>
    public string Context(AppWindow window) => services.ClaudeInCode() ? "claude" : "";

    public AppBank? Bank(string context) => context switch
    {
        "" => Own,
        "claude" => ClaudeTab,
        _ => null,
    };

    public BarItem Live(BarItem item, AppWindow window) => claude.Live(item, window);

    public bool Run(BarItem key, AppWindow window)
    {
        if (claude.Run(key, window, FocusClaude)) return true;
        if (key.Command.Length > 0 && services.VsCode(key.Command)) return true;
        if (key.Kind == "keys" && key.Target.Length == 0)
        {
            services.Toast(new Toast("error", $"{key.Name} needs the flex-screen VS Code extension 0.3"));
            return true;
        }
        return false;
    }

    public void Choose(BarItem key, string choice, AppWindow window) => claude.Choose(key, choice, window, FocusClaude);

    /// <summary>Claude's input box gets the focus through the extension (Ctrl+Esc would toggle back to the editor if it already had it).</summary>
    bool FocusClaude(string command)
    {
        if (!services.VsCode("claude-vscode.focus"))
        {
            services.Toast(new Toast("error", $"{command} in VS Code needs the flex-screen extension 0.3"));
            return false;
        }
        Thread.Sleep(350);
        return true;
    }

    /// <summary>The terminal, the build and recent actions (DevSource, from the extension), and git for its folder.</summary>
    public IReadOnlyList<string> Topics { get; } = ["term", "build", "recent", "git"];
}
