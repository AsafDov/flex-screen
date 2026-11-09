using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// The deck's context bar: keys for the app in front, in that app's color. Each app has a
/// bank of actions (VS Code, Chrome and Edge with Gmail, YouTube and Google Calendar sets,
/// Windows Terminal, Explorer, Obsidian, Spotify, Teams with another bank during a meeting,
/// Discord, the Claude app; for any app an "everywhere" bank: copy, paste, snap,
/// screenshot…) and a few of them on the bar by default. Claude's keys (Approve, Deny, Model,
/// Effort, Usage) are in the Claude app's, VS Code's and Terminal's banks. On the deck, a
/// long press on a key picks another action from the bank for that place (one already on the
/// bar swaps places with it), removes the key, or resets the app's bar; "+" adds one. Those
/// choices are kept per app in %LOCALAPPDATA%\flex-screen\context-bar.json.
/// settings.json's "contextBar" adds actions to an app's bank (by exe name, "chrome", or
/// the name the deck shows, "Chrome"; "*" for apps without a bank) and makes them its
/// default bar, or turns the bar off:
/// <code>
/// "contextBar": {
///   "Spotify": { "color": "#1DB954", "keys": [ { "name": "Like", "keys": "Alt+Shift+B", "glyph": "heart" } ] }
/// }
/// </code>
/// An action is "keys" (pressed in that app's window, brought back to the front first if
/// something else took it; "g, i" presses one after the other), "text" (typed, "\n" for
/// Enter), "action" (a deck action, as for shortcuts) or "action": "close" (closes the
/// window, as its X button does).
/// </summary>
sealed class ContextBar
{
    public const int MaxKeys = 8;

    /// <param name="Kind">keys, text, action, close, folder (Explorer's open folder), pick (a list to choose from), url.</param>
    /// <param name="Command">For VS Code: the command run through the flex-screen extension instead of the keys.</param>
    public record Item(string Id, string Name, string Glyph, string Kind, string Target, string Command = "");

    /// <param name="Key">Whose layout this is: the exe's name ("code"), "chrome:gmail" on Gmail, "ms-teams:call" during a meeting.</param>
    /// <param name="Color">The bar's color, 0xRRGGBB.</param>
    /// <param name="Bank">Every action the bar can hold: the app's own first (<paramref name="Own"/> of them), then the everyday ones.</param>
    public record Bar(string App, string Key, string Icon, int Color, IReadOnlyList<Item> Keys, IReadOnlyList<Item> Bank, int Own)
    {
        public static readonly Bar None = new("", "", "", 0, [], [], 0);
    }

    record AppSet(int? Color, Item[] Items, string[] Default);

    static Item K(string id, string name, string glyph, string keys, string command = "") => new(id, name, glyph, "keys", keys, command);
    static Item A(string id, string name, string glyph, string action) => new(id, name, glyph, "action", action);
    static Item T(string id, string name, string glyph, string text) => new(id, name, glyph, "text", text);
    static Item F(string id, string name, string glyph, string what) => new(id, name, glyph, "folder", what);

    /// <summary>For any app, after its own.</summary>
    static readonly Item[] Everyday =
    [
        K("any-copy", "Copy", "copy", "Ctrl+C"), K("any-paste", "Paste", "paste", "Ctrl+V"), K("any-cut", "Cut", "cut", "Ctrl+X"),
        K("any-undo", "Undo", "undo", "Ctrl+Z"), K("any-redo", "Redo", "redo", "Ctrl+Y"), K("any-save", "Save", "save", "Ctrl+S"),
        K("any-find", "Find", "search", "Ctrl+F"), K("any-all", "Select all", "check", "Ctrl+A"), K("any-new", "New", "plus", "Ctrl+N"),
        K("any-open", "Open", "folder", "Ctrl+O"), K("any-print", "Print", "print", "Ctrl+P"), new("any-close", "Close", "close", "close", ""),
        K("any-switch", "Switch app", "switch", "Alt+Tab"), K("any-snap-left", "Snap left", "back", "Win+Left"),
        K("any-snap-right", "Snap right", "forward", "Win+Right"), K("any-max", "Maximize", "expand", "Win+Up"),
        K("any-min", "Minimize", "down", "Win+Down"), K("any-tasks", "Task view", "window", "Win+Tab"),
        K("any-shot", "Screenshot", "camera", "Win+Shift+S"), K("any-emoji", "Emoji", "smile", "Win+."),
        A("any-desktop", "Desktop", "home", "desktop.show"), A("any-lock", "Lock PC", "lock", "pc.lock"),
    ];
    static readonly string[] EverydayDefault = ["any-copy", "any-paste", "any-undo", "any-save", "any-find", "any-close"];

    // ---- Claude: the same keys wherever Claude Code runs (its app, VS Code, a terminal)

    const string UsageUrl = "https://claude.ai/settings/usage";

    /// <summary>Approve and Deny answer the newest permission request; Model and Effort open a list, and show the session's.</summary>
    static readonly Item[] ClaudeItems =
    [
        A("claude-approve", "Approve", "approve", "claude.approve"), A("claude-deny", "Deny", "deny", "claude.deny"),
        new("claude-model", "Model", "brain", "pick", "model"), new("claude-effort", "Effort", "gauge", "pick", "effort"),
        new("claude-usage", "Usage", "chart", "url", UsageUrl),
    ];

    static readonly (string Id, string Name)[] Models =
        [("default", "Default"), ("opus", "Opus"), ("sonnet", "Sonnet"), ("haiku", "Haiku"), ("fable", "Fable"), ("opusplan", "Opus plan")];
    static readonly (string Id, string Name)[] Efforts =
        [("low", "Low"), ("medium", "Medium"), ("high", "High"), ("xhigh", "Extra high"), ("max", "Max"), ("auto", "Auto")];

    // ---- the browsers, and the sites that get their own keys there

    static readonly Item[] BrowserItems =
    [
        K("back", "Back", "back", "Alt+Left"), K("forward", "Forward", "forward", "Alt+Right"), K("reload", "Reload", "reload", "F5"),
        K("prev-tab", "Prev tab", "prev", "Ctrl+Shift+Tab"), K("next-tab", "Next tab", "next", "Ctrl+Tab"),
        K("new-tab", "New tab", "plus", "Ctrl+T"), K("reopen", "Reopen", "history", "Ctrl+Shift+T"),
        K("close-tab", "Close tab", "close", "Ctrl+W"), K("find", "Find", "search", "Ctrl+F"),
        K("address", "Address bar", "link", "Ctrl+L"), K("bookmark", "Bookmark", "bookmark", "Ctrl+D"),
        K("history", "History", "list", "Ctrl+H"), K("downloads", "Downloads", "download", "Ctrl+J"),
        K("new-window", "New window", "window", "Ctrl+N"), K("private", "Private", "eye", "Ctrl+Shift+N"),
        K("zoom-in", "Zoom in", "zoom-in", "Ctrl+plus"), K("zoom-out", "Zoom out", "zoom-out", "Ctrl+minus"),
        K("fullscreen", "Full screen", "expand", "F11"), K("devtools", "DevTools", "code", "F12"),
    ];
    static readonly string[] BrowserDefault = ["back", "reload", "prev-tab", "next-tab", "new-tab", "reopen", "find"];

    /// <summary>By the tab's title. Gmail's keys need its keyboard shortcuts on (Gmail settings, off by default).</summary>
    static readonly (string Site, string Name, Func<string, bool> Match, AppSet Set)[] Sites =
    [
        ("gmail", "Gmail", t => t.EndsWith(" - Gmail", StringComparison.Ordinal) || t.Contains("@gmail.com"), new(0xEA4335,
        [
            K("compose", "Compose", "edit", "c"), K("reply", "Reply", "reply", "r"), K("reply-all", "Reply all", "reply", "a"),
            K("forward-mail", "Forward", "share", "f"), K("archive", "Archive", "download", "e"), K("delete", "Delete", "trash", "Shift+3"),
            K("unread", "Mark unread", "eye", "Shift+U"), K("newer", "Newer", "up", "k"), K("older", "Older", "down", "j"),
            K("search", "Search mail", "search", "slash"), K("snooze", "Snooze", "moon", "b"), K("inbox", "Inbox", "home", "g, i"),
            K("send", "Send", "check", "Ctrl+Enter"),
        ], ["compose", "reply", "archive", "delete", "newer", "older", "search"])),
        ("youtube", "YouTube", t => t.EndsWith("- YouTube", StringComparison.Ordinal) || t == "YouTube", new(0xFF0033,
        [
            K("back10", "Back 10 s", "back", "j"), K("play", "Play/pause", "play", "k"), K("ahead10", "Ahead 10 s", "forward", "l"),
            K("mute", "Mute", "volume", "m"), K("captions", "Captions", "comment", "c"), K("theater", "Theater", "window", "t"),
            K("fullscreen-video", "Full screen", "expand", "f"), K("slower", "Slower", "minus", "Shift+comma"),
            K("faster", "Faster", "plus", "Shift+period"), K("next-video", "Next video", "next-track", "Shift+N"),
            K("miniplayer", "Mini player", "window", "i"),
        ], ["back10", "play", "ahead10", "mute", "captions", "fullscreen-video"])),
        ("calendar", "Calendar", t => t.Contains("Google Calendar", StringComparison.Ordinal), new(0x4285F4,
        [
            K("prev-period", "Previous", "prev", "k"), K("today", "Today", "calendar", "t"), K("next-period", "Next", "next", "j"),
            K("day", "Day", "list", "d"), K("week", "Week", "split-right", "w"), K("month", "Month", "calendar", "m"),
            K("new-event", "New event", "plus", "c"), K("search-cal", "Search", "search", "slash"), K("agenda", "Agenda", "line", "a"),
        ], ["prev-period", "today", "next-period", "day", "week", "month", "new-event"])),
    ];

    // ---- Teams

    static readonly Item[] TeamsItems =
    [
        K("activity", "Activity", "bell", "Ctrl+1"), K("chat", "Chat", "chat", "Ctrl+2"), K("teams", "Teams", "users", "Ctrl+3"),
        K("calendar", "Calendar", "calendar", "Ctrl+4"), K("calls", "Calls", "phone", "Ctrl+5"), K("new-chat", "New chat", "edit", "Ctrl+N"),
        K("search", "Search", "search", "Ctrl+E"), K("goto", "Go to", "switch", "Ctrl+G"),
    ];

    // In a meeting: the call panel's own controls (Teams' API), reactions, and sharing.
    static readonly Item[] TeamsCallItems =
    [
        A("mute", "Mute", "mic-off", "call.mic"), A("camera", "Camera", "video", "call.camera"), A("hand", "Raise hand", "hand", "call.hand"),
        K("share", "Share", "screen", "Ctrl+Shift+E"), A("leave", "Leave", "leave", "call.leave"), A("show-call", "Show call", "window", "call.focus"),
        A("react-like", "Like", "thumbs-up", "call.react:like"), A("react-love", "Love", "heart", "call.react:love"),
        A("react-applause", "Applause", "hand", "call.react:applause"), A("react-laugh", "Laugh", "smile", "call.react:laugh"),
        A("react-wow", "Wow", "surprise", "call.react:wow"),
    ];

    /// <summary>By the exe's name, lower case; a Teams meeting by "ms-teams:call".</summary>
    static readonly Dictionary<string, AppSet> BuiltIn = new()
    {
        ["code"] = new AppSet(0x2F9BF0,
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
            .. ClaudeItems,
        ], ["goto-file", "goto-symbol", "terminal", "search", "claude-approve", "claude-model"]),
        ["claude"] = new AppSet(0xD97757,
        [
            .. ClaudeItems,
            K("new-chat", "New chat", "plus", "Ctrl+N"), K("stop", "Stop", "stop", "Esc"), K("search-chats", "Search chats", "search", "Ctrl+K"),
        ], ["claude-approve", "claude-deny", "claude-model", "claude-effort", "claude-usage", "new-chat"]),
        ["chrome"] = new AppSet(0x34A853, BrowserItems, BrowserDefault),
        ["msedge"] = new AppSet(0x2B9FD9, BrowserItems, BrowserDefault),
        ["windowsterminal"] = new AppSet(0xE8A33D,
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
            .. ClaudeItems,
        ], ["prev-cmd", "new-tab", "prev-tab", "next-tab", "split-right", "split-down"]),
        ["explorer"] = new AppSet(null,
        [
            K("back", "Back", "back", "Alt+Left"), K("forward", "Forward", "forward", "Alt+Right"), K("up", "Up", "up", "Alt+Up"),
            K("address", "Address bar", "link", "Alt+D"), K("search", "Search", "search", "Ctrl+E"),
            K("new-folder", "New folder", "folder-plus", "Ctrl+Shift+N"), K("rename", "Rename", "cursor", "F2"),
            K("copy-path", "Copy path", "copy", "Ctrl+Shift+C"), K("properties", "Properties", "list", "Alt+Enter"),
            K("new-tab", "New tab", "plus", "Ctrl+T"), K("close-tab", "Close tab", "close", "Ctrl+W"),
            K("preview", "Preview pane", "eye", "Alt+P"), K("details", "Details view", "line", "Ctrl+Shift+6"),
            K("icons", "Large icons", "image", "Ctrl+Shift+2"), K("refresh", "Refresh", "reload", "F5"),
            K("delete", "Delete", "trash", "Delete"),
            F("terminal-here", "Terminal here", "terminal", "terminal"), F("vscode-here", "VS Code here", "code", "vscode"),
            F("claude-here", "Claude here", "chat", "claude"), F("copy-folder", "Copy folder", "copy", "copy"),
        ], ["up", "new-folder", "rename", "copy-path", "terminal-here", "vscode-here"]),
        ["obsidian"] = new AppSet(0x8B5CF6,
        [
            A("daily", "Daily note", "calendar", "obs.today"), K("search", "Search", "search", "Ctrl+Shift+F"),
            K("quick-open", "Quick open", "folder", "Ctrl+O"), K("commands", "Commands", "keyboard", "Ctrl+P"),
            K("back", "Back", "back", "Ctrl+Alt+Left"), K("forward", "Forward", "forward", "Ctrl+Alt+Right"),
            K("graph", "Graph", "graph", "Ctrl+G"), K("new-note", "New note", "plus", "Ctrl+N"), K("reading", "Reading view", "eye", "Ctrl+E"),
            K("link", "Insert link", "link", "Ctrl+K"), K("checkbox", "Checkbox", "check", "Ctrl+L"), K("find", "Find in note", "search", "Ctrl+F"),
            K("bold", "Bold", "format", "Ctrl+B"), K("italic", "Italic", "cursor", "Ctrl+I"), K("close-tab", "Close tab", "close", "Ctrl+W"),
        ], ["daily", "search", "quick-open", "commands", "back", "graph"]),
        ["spotify"] = new AppSet(0x1DB954,
        [
            K("prev", "Previous", "prev-track", "Ctrl+Left"), K("play", "Play/pause", "play", "Space"), K("next", "Next", "next-track", "Ctrl+Right"),
            K("like", "Like", "heart", "Alt+Shift+B"), K("shuffle", "Shuffle", "shuffle", "Ctrl+S"), K("repeat", "Repeat", "reload", "Ctrl+R"),
            K("search", "Search", "search", "Ctrl+K"), K("back5", "Back 5 s", "back", "Shift+Left"), K("ahead5", "Ahead 5 s", "forward", "Shift+Right"),
            K("louder", "Louder", "volume", "Ctrl+Up"), K("quieter", "Quieter", "minus", "Ctrl+Down"),
        ], ["prev", "play", "next", "like", "shuffle", "search"]),
        ["ms-teams"] = new AppSet(0x6264A7, TeamsItems, ["activity", "chat", "calendar", "new-chat", "search"]),
        ["teams"] = new AppSet(0x6264A7, TeamsItems, ["activity", "chat", "calendar", "new-chat", "search"]),
        ["ms-teams:call"] = new AppSet(0x6264A7, [.. TeamsCallItems, .. TeamsItems], ["mute", "camera", "hand", "react-like", "share", "leave"]),
        ["teams:call"] = new AppSet(0x6264A7, [.. TeamsCallItems, .. TeamsItems], ["mute", "camera", "hand", "react-like", "share", "leave"]),
        ["discord"] = new AppSet(0x5865F2,
        [
            K("switch", "Switch", "switch", "Ctrl+K"), K("prev-channel", "Prev channel", "up", "Alt+Up"),
            K("next-channel", "Next channel", "down", "Alt+Down"), K("unread", "Unread", "unread", "Alt+Shift+Down"),
            K("prev-server", "Prev server", "up", "Ctrl+Alt+Up"), K("next-server", "Next server", "down", "Ctrl+Alt+Down"),
            K("read", "Mark read", "check", "Esc"), K("search", "Search", "search", "Ctrl+F"), K("mute", "Mute", "mic-off", "Ctrl+Shift+M"),
            K("deafen", "Deafen", "headphones", "Ctrl+Shift+D"), K("inbox", "Inbox", "bell", "Ctrl+I"), K("pins", "Pins", "pin", "Ctrl+P"),
            K("upload", "Upload", "upload", "Ctrl+Shift+U"), K("emoji", "Emoji", "smile", "Ctrl+E"), K("members", "Members", "users", "Ctrl+U"),
            K("answer", "Answer call", "phone", "Ctrl+Enter"),
        ], ["switch", "prev-channel", "next-channel", "unread", "mute", "deafen"]),
    };

    const int Neutral = 0x8A94A6;  // an icon without a color of its own

    static readonly string LayoutsPath = Path.Combine(Path.GetDirectoryName(Settings.FilePath)!, "context-bar.json");

    readonly WindowSource _windows;
    readonly Func<CallState> _call;
    readonly Action<string> _act;
    readonly Func<long, (string Model, string Effort)> _claude;  // a window → its Claude session's model and effort
    readonly Func<string, bool> _vscode;                          // runs a VS Code command; false without an extension that can
    readonly object _gate = new();
    readonly Dictionary<string, List<string>> _layouts;  // app key → action ids, as arranged on the deck
    DeckWindow? _front;  // the app window in front last (kept while the desktop or the taskbar has it)
    string _exe = "";
    string _site = "";
    string _config = "\0";
    bool _inCall;
    Bar _bar = Bar.None;

    public ContextBar(WindowSource windows, Func<CallState> call, Action<string> act,
                      Func<long, (string Model, string Effort)> claude, Func<string, bool> vscode)
    {
        _windows = windows;
        _call = call;
        _act = act;
        _claude = claude;
        _vscode = vscode;
        _layouts = LoadLayouts();
    }

    public Bar Current
    {
        get
        {
            lock (_gate) return _bar;
        }
    }

    /// <summary>Raised when <see cref="Current"/> changes (another app in front, a meeting starting, settings, an edit, Claude's model).</summary>
    public event Action<Bar>? Changed;

    /// <summary>A pick key was tapped: its id, a title, the choices (id, name) and the one in effect.</summary>
    public event Action<string, string, IReadOnlyList<(string Id, string Name)>, string>? Pick;

    public event Action<Toast>? Toast;

    /// <summary>From the window list's poll: the window in front (null for the desktop, the taskbar, a menu).</summary>
    public void OnPolled(IReadOnlyList<DeckWindow> all, DeckWindow? front)
    {
        if (front == null) return;  // keep the last app's keys: they bring it back
        lock (_gate)
        {
            var site = SiteOf(_front?.Id == front.Id ? _exe : Exe(front), front.Title);
            if (_front != null && front.Id == _front.Id && front.Icon == _front.Icon && front.App == _front.App && site == _site) return;
            _exe = Exe(front);
            _site = SiteOf(_exe, front.Title);
            _front = front;
        }
        Update();
    }

    static string Exe(DeckWindow window) => Path.GetFileNameWithoutExtension(WindowSource.ExePath(window.Id)).ToLowerInvariant();

    static string SiteOf(string exe, string title) =>
        exe is "chrome" or "msedge" ? Sites.FirstOrDefault(s => s.Match(title)).Site ?? "" : "";

    /// <summary>Settings, a call or a Claude session changed: the same window's keys again, sent only if they differ.</summary>
    public void Refresh()
    {
        var config = Settings.ContextBar?.ToJsonString() ?? "";
        lock (_gate) _config = config;
        Update();
    }

    void Update()
    {
        Bar bar;
        lock (_gate)
        {
            _inCall = _call().Active;
            bar = _front == null ? Bar.None : Make(_front, _exe, _site);
            if (Same(bar, _bar)) return;
            _bar = bar;
        }
        Changed?.Invoke(bar);
    }

    Bar Make(DeckWindow front, string exe, string site)
    {
        var config = Settings.ContextBar;
        if (config is JsonValue value && value.TryGetValue(out bool on) && !on) return Bar.None;
        var key = exe;
        var app = front.App;
        AppSet? builtIn;
        if (Sites.FirstOrDefault(s => s.Site == site) is { Set: { } siteSet } match)
        {
            key = $"{exe}:{site}";
            app = match.Name;
            builtIn = siteSet with { Items = [.. siteSet.Items, .. BuiltIn[exe].Items.Where(i => siteSet.Items.All(s => s.Id != i.Id))] };
        }
        else
        {
            if (BuiltIn.ContainsKey($"{exe}:call") && _call() is { Active: true, App: "teams" }) key = $"{exe}:call";
            builtIn = BuiltIn.GetValueOrDefault(key);
        }
        var mine = FromSettings(config as JsonObject, exe, front.App);
        if (mine == null && builtIn == null) mine = FromSettings(config as JsonObject, "*", "*");
        var own = (mine?.Items ?? []).Concat(builtIn?.Items ?? []).DistinctBy(i => i.Id).ToList();
        var names = own.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bank = own.Concat(Everyday.Where(i => !names.Contains(i.Name))).Select(i => Live(i, front)).ToList();
        var layout = _layouts.GetValueOrDefault(key) ?? (IReadOnlyList<string>?)mine?.Default ?? builtIn?.Default ?? EverydayDefault;
        var keys = layout.Select(id => bank.FirstOrDefault(i => i.Id == id)).OfType<Item>().Take(MaxKeys).ToList();
        var color = mine?.Color ?? builtIn?.Color ?? IconColor(_windows.FindIcon(front.Icon));
        return new Bar(app, key, front.Icon, color, keys, bank, own.Count);
    }

    /// <summary>Model and Effort show the Claude session's own, in the window the bar is for.</summary>
    Item Live(Item item, DeckWindow front)
    {
        if (item.Kind != "pick") return item;
        var (model, effort) = _claude(front.Id);
        return item.Target switch
        {
            "model" when model.Length > 0 => item with { Name = ModelName(model) },
            "effort" when effort.Length > 0 => item with { Name = Efforts.FirstOrDefault(e => e.Id == effort).Name ?? effort },
            _ => item,
        };
    }

    /// <summary>"claude-opus-5-5[1m]" → "Opus 5.5"; "claude-sonnet-4-5-20250929" → "Sonnet 4.5".</summary>
    public static string ModelName(string id)
    {
        var name = id.Replace("[1m]", "", StringComparison.OrdinalIgnoreCase);
        if (name.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) name = name[7..];
        var parts = name.Split('-').Where(p => !(p.Length == 8 && p.All(char.IsDigit))).ToList();  // no release date
        if (parts.Count == 0) return id;
        var family = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
        return parts.Count > 1 ? $"{family} {string.Join('.', parts.Skip(1))}" : family;
    }

    /// <summary>An app's actions from settings.json, by exe name or the app's name on the deck; null if it has none.</summary>
    static AppSet? FromSettings(JsonObject? config, string exe, string app)
    {
        if (config == null) return null;
        var node = config.FirstOrDefault(p => string.Equals(p.Key, exe, StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(p.Key, app, StringComparison.OrdinalIgnoreCase)).Value;
        var list = node as JsonArray ?? node?["keys"] as JsonArray;
        if (list == null) return null;
        int? color = null;
        if ((string?)node?["color"] is { } hex && int.TryParse(hex.TrimStart('#'), System.Globalization.NumberStyles.HexNumber, null, out var c)) color = c;
        var items = new List<Item>();
        foreach (var k in list.OfType<JsonObject>())
        {
            var name = (string?)k["name"] ?? "";
            var glyph = (string?)k["glyph"] ?? "";
            Item? item = null;
            if ((string?)k["keys"] is { Length: > 0 } combo)
            {
                if (!KeyInput.CanPress(combo)) Log.Write($"context: can't read the keys \"{combo}\" (write them like Ctrl+Shift+Esc or Alt+Left)");
                else item = new Item("", name.Length > 0 ? name : combo, glyph, "keys", combo);
            }
            else if ((string?)k["text"] is { Length: > 0 } text)
            {
                item = new Item("", name.Length > 0 ? name : text.Trim(), glyph, "text", text);
            }
            else if ((string?)k["action"] is { Length: > 0 } action)
            {
                if (action == "close") item = new Item("", name.Length > 0 ? name : "Close", glyph.Length > 0 ? glyph : "close", "close", "");
                else if (Shortcuts.Actions.ContainsKey(action)) item = new Item("", name.Length > 0 ? name : action, glyph, "action", action);
                else Log.Write($"context: \"{action}\" isn't an action a key can run");
            }
            if (item != null) items.Add(item with { Id = "my-" + Slug(item.Name) });
        }
        var unique = items.DistinctBy(i => i.Id).ToArray();
        return new AppSet(color, unique, unique.Select(i => i.Id).ToArray());
    }

    static string Slug(string name) => new(name.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());

    static bool Same(Bar a, Bar b) =>
        a.App == b.App && a.Key == b.Key && a.Icon == b.Icon && a.Color == b.Color && a.Own == b.Own &&
        a.Keys.Select(k => (k.Id, k.Name)).SequenceEqual(b.Keys.Select(k => (k.Id, k.Name))) &&
        a.Bank.Select(k => (k.Id, k.Name)).SequenceEqual(b.Bank.Select(k => (k.Id, k.Name)));

    // ---- editing from the deck

    /// <summary>
    /// The action <paramref name="id"/> into place <paramref name="slot"/> of the app's bar
    /// (past the last key: a new one at the end). One already on the bar swaps places with
    /// the one there; added again, it moves to the end.
    /// </summary>
    public static List<string> Place(IReadOnlyList<string> layout, int slot, string id)
    {
        var list = layout.ToList();
        var at = list.IndexOf(id);
        if (slot < 0) return list;
        if (slot >= list.Count)
        {
            if (at >= 0) list.RemoveAt(at);
            if (list.Count < MaxKeys) list.Add(id);
        }
        else if (at >= 0 && at != slot)
        {
            (list[slot], list[at]) = (list[at], list[slot]);
        }
        else
        {
            list[slot] = id;
        }
        return list;
    }

    public static List<string> Remove(IReadOnlyList<string> layout, int slot)
    {
        var list = layout.ToList();
        if (slot >= 0 && slot < list.Count) list.RemoveAt(slot);
        return list;
    }

    /// <summary>A long press's choice on the deck: <paramref name="id"/> into <paramref name="slot"/>, for the app the bar was showing.</summary>
    public void Set(string app, int slot, string id) => Edit(app, "set", bar =>
        bar.Bank.Any(i => i.Id == id) ? Place(bar.Keys.Select(k => k.Id).ToList(), slot, id) : null);

    public void Remove(string app, int slot) => Edit(app, "remove", bar => Remove(bar.Keys.Select(k => k.Id).ToList(), slot));

    /// <summary>Back to the app's default keys.</summary>
    public void Reset(string app)
    {
        lock (_gate)
        {
            if (!_layouts.Remove(app)) return;
            SaveLayouts();
        }
        Log.Write($"context: {app} back to its default keys");
        Update();
    }

    void Edit(string app, string what, Func<Bar, List<string>?> change)
    {
        lock (_gate)
        {
            if (app != _bar.Key || app.Length == 0)
            {
                Log.Write($"context: {what} for {app}, but the bar is {_bar.Key} now; ignored");
                return;
            }
            if (change(_bar) is not { } layout) return;
            _layouts[app] = layout;
            SaveLayouts();
            Log.Write($"context: {app} keys {string.Join(", ", layout)}");
        }
        Update();
    }

    static Dictionary<string, List<string>> LoadLayouts()
    {
        try
        {
            if (File.Exists(LayoutsPath))
            {
                return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(LayoutsPath)) ?? [];
            }
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            Log.Write($"context: can't read {LayoutsPath}: {e.Message}");
        }
        return [];
    }

    void SaveLayouts()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LayoutsPath)!);
            File.WriteAllText(LayoutsPath, JsonSerializer.Serialize(_layouts, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException e)
        {
            Log.Write($"context: can't save {LayoutsPath}: {e.Message}");
        }
    }

    // ---- running

    /// <summary>A key tapped on the deck.</summary>
    public void Run(string id)
    {
        Item? key;
        long hwnd;
        string exe, title;
        lock (_gate)
        {
            key = _bar.Keys.FirstOrDefault(k => k.Id == id);
            hwnd = _front?.Id ?? 0;
            exe = _exe;
            title = _front?.Title ?? "";
        }
        if (key == null) return;
        switch (key.Kind)
        {
            case "action":
                _act(key.Target);
                return;
            case "url":
                Process.Start(new ProcessStartInfo(key.Target) { UseShellExecute = true });
                return;
            case "pick":
                var (model, effort) = _claude(hwnd);
                if (key.Target == "model") Pick?.Invoke(key.Id, "Model", Models, Models.FirstOrDefault(m => ModelName(model).StartsWith(m.Name, StringComparison.OrdinalIgnoreCase)).Id ?? "");
                else Pick?.Invoke(key.Id, "Effort", Efforts, effort);
                return;
        }
        // VS Code runs its own commands (whatever the keybindings, and with no window focus
        // needed), through the flex-screen extension; an older extension gets the keys.
        if (exe == "code" && key.Command.Length > 0 && _vscode(key.Command)) return;
        if (key.Kind == "keys" && key.Target.Length == 0)
        {
            Toast?.Invoke(new Toast("error", $"{key.Name} needs the flex-screen VS Code extension 0.3"));
            return;
        }
        if (key.Kind == "folder")
        {
            OpenFolder(hwnd, title, key);
            return;
        }
        // The keys are for the window the bar names; something else may have the front now
        // (the desktop, the taskbar, a menu that closed).
        if (!Front(hwnd)) return;
        if (key.Kind == "close") _windows.Close(hwnd);
        else if (key.Kind == "text") KeyInput.TypeLines(key.Target.Replace("\\n", "\n"));
        else KeyInput.Sequence(key.Target);
    }

    /// <summary>The window to the front, if it isn't; false if it's gone.</summary>
    static bool Front(long hwnd)
    {
        if (hwnd == 0 || !WindowSource.Exists(hwnd)) return false;
        if (WindowSource.Foreground() != (IntPtr)hwnd)
        {
            WindowFocus.Activate((IntPtr)hwnd);
            Thread.Sleep(120);
        }
        return true;
    }

    /// <summary>
    /// A choice from a pick key's list: Claude's model or effort, typed as /model or /effort
    /// into the prompt of the Claude session in that window (in VS Code, Claude's input box
    /// gets the focus first).
    /// </summary>
    public void Choose(string id, string choice)
    {
        Item? key;
        long hwnd;
        string exe;
        lock (_gate)
        {
            key = _bar.Keys.FirstOrDefault(k => k.Id == id);
            hwnd = _front?.Id ?? 0;
            exe = _exe;
        }
        if (key is not { Kind: "pick" }) return;
        var choices = key.Target == "model" ? Models : Efforts;
        if (choices.All(c => c.Id != choice)) return;
        if (!Front(hwnd)) return;
        if (exe == "code")
        {
            if (!_vscode("claude-vscode.focus"))
            {
                Toast?.Invoke(new Toast("error", "Changing Claude's model in VS Code needs the flex-screen extension 0.3"));
                return;
            }
            Thread.Sleep(350);
        }
        var command = key.Target == "model" ? $"/model {choice}" : $"/effort {choice}";
        Log.Write($"context: {command} in {exe}");
        KeyInput.TypeLines(command + "\n");
    }

    /// <summary>Explorer's open folder: a terminal, VS Code or Claude there, or its path copied.</summary>
    void OpenFolder(long hwnd, string title, Item key)
    {
        var folder = ShellApps.OnSta(() => ShellApps.ExplorerFolder(hwnd, title));
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            Toast?.Invoke(new Toast("error", "This Explorer window isn't showing a folder"));
            return;
        }
        try
        {
            switch (key.Target)
            {
                case "terminal":
                    Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{folder}\"") { UseShellExecute = true });
                    break;
                case "claude":
                    Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{folder}\" claude") { UseShellExecute = true });
                    break;
                case "vscode":
                    Process.Start(new ProcessStartInfo("cmd.exe", $"/c code \"{folder}\"") { UseShellExecute = false, CreateNoWindow = true });
                    break;
                case "copy":
                    ShellApps.OnSta(() =>
                    {
                        System.Windows.Forms.Clipboard.SetText(folder);
                        return true;
                    });
                    Toast?.Invoke(new Toast("ok", $"Copied {folder}"));
                    break;
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            Log.Write($"context: {key.Name} in {folder}: {e.Message}");
            Toast?.Invoke(new Toast("error", $"Couldn't open {key.Name.Replace(" here", "")} there"));
        }
    }

    /// <summary>
    /// The icon's main color: its most common saturated hue, averaged and brightened. Grey
    /// for an icon that has none (black, white, grey).
    /// </summary>
    static int IconColor(WindowIcon? icon)
    {
        if (icon == null) return Neutral;
        const int n = WindowSource.IconSize * WindowSource.IconSize;
        var pixels = icon.Pixels;
        var weight = new double[12];
        var sums = new double[12, 3];
        for (var i = 0; i < n; i++)
        {
            int a = pixels[n * 2 + i];
            if (a < 128) continue;
            var v = pixels[i * 2] | pixels[i * 2 + 1] << 8;
            int r = (v >> 11) << 3, g = ((v >> 5) & 63) << 2, b = (v & 31) << 3;
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            if (max < 60) continue;
            var sat = (max - min) / (double)max;
            if (sat < 0.3) continue;
            double hue = max == r ? (g - b) / (double)(max - min) : max == g ? 2 + (b - r) / (double)(max - min) : 4 + (r - g) / (double)(max - min);
            var bin = ((int)Math.Floor(hue * 2) % 12 + 12) % 12;  // 30° each
            var w = sat * a / 255.0;
            weight[bin] += w;
            sums[bin, 0] += r * w;
            sums[bin, 1] += g * w;
            sums[bin, 2] += b * w;
        }
        var best = Array.IndexOf(weight, weight.Max());
        if (weight[best] < 8) return Neutral;
        double rr = sums[best, 0] / weight[best], gg = sums[best, 1] / weight[best], bb = sums[best, 2] / weight[best];
        var scale = 220 / Math.Max(rr, Math.Max(gg, bb));
        return (int)Math.Min(255, rr * scale) << 16 | (int)Math.Min(255, gg * scale) << 8 | (int)Math.Min(255, bb * scale);
    }
}
