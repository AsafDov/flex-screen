using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// The deck's context bar: keys for the app in front, in that app's color. Each app has a
/// bank of actions (VS Code, Chrome and Edge, Windows Terminal, Obsidian, Teams, with
/// another bank during a meeting, Discord; for any app, an "everywhere" bank: copy, paste,
/// snap, screenshot…) and a few of them on the bar by default. On the deck, a long press
/// on a key picks another action from the bank for that place (one already on the bar
/// swaps places with it), removes the key, or resets the app's bar; "+" adds one. Those
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
/// something else took it), "action" (a deck action, as for shortcuts) or "action": "close"
/// (closes the window, as its X button does).
/// </summary>
sealed class ContextBar
{
    public const int MaxKeys = 8;

    /// <param name="Kind">keys, action or close.</param>
    public record Item(string Id, string Name, string Glyph, string Kind, string Target);

    /// <param name="Key">Whose layout this is: the exe's name ("code"), "ms-teams:call" during a meeting.</param>
    /// <param name="Color">The bar's color, 0xRRGGBB.</param>
    /// <param name="Bank">Every action the bar can hold: the app's own first (<paramref name="Own"/> of them), then the everyday ones.</param>
    public record Bar(string App, string Key, string Icon, int Color, IReadOnlyList<Item> Keys, IReadOnlyList<Item> Bank, int Own)
    {
        public static readonly Bar None = new("", "", "", 0, [], [], 0);
    }

    record AppSet(int? Color, Item[] Items, string[] Default);

    static Item K(string id, string name, string glyph, string keys) => new(id, name, glyph, "keys", keys);
    static Item A(string id, string name, string glyph, string action) => new(id, name, glyph, "action", action);

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

    static readonly Item[] TeamsItems =
    [
        K("activity", "Activity", "bell", "Ctrl+1"), K("chat", "Chat", "chat", "Ctrl+2"), K("teams", "Teams", "users", "Ctrl+3"),
        K("calendar", "Calendar", "calendar", "Ctrl+4"), K("calls", "Calls", "phone", "Ctrl+5"), K("new-chat", "New chat", "edit", "Ctrl+N"),
        K("search", "Search", "search", "Ctrl+E"), K("goto", "Go to", "switch", "Ctrl+G"),
    ];

    // In a meeting: the call panel's own controls (Teams' API), and sharing.
    static readonly Item[] TeamsCallItems =
    [
        A("mute", "Mute", "mic-off", "call.mic"), A("camera", "Camera", "video", "call.camera"), A("hand", "Raise hand", "hand", "call.hand"),
        K("share", "Share", "screen", "Ctrl+Shift+E"), A("leave", "Leave", "leave", "call.leave"), A("show-call", "Show call", "window", "call.focus"),
    ];

    /// <summary>By the exe's name, lower case; a Teams meeting by "ms-teams:call".</summary>
    static readonly Dictionary<string, AppSet> BuiltIn = new()
    {
        ["code"] = new AppSet(0x2F9BF0,
        [
            K("build", "Build", "play", "Ctrl+Shift+B"), K("run", "Run", "bug", "F5"), K("terminal", "Terminal", "terminal", "Ctrl+`"),
            K("commands", "Commands", "keyboard", "Ctrl+Shift+P"), K("goto-file", "Go to file", "file", "Ctrl+P"),
            K("goto-symbol", "Go to symbol", "at", "Ctrl+Shift+O"), K("goto-line", "Go to line", "line", "Ctrl+G"),
            K("definition", "Definition", "target", "F12"), K("search", "Search", "search", "Ctrl+Shift+F"),
            K("split", "Split", "split-right", "Ctrl+\\"), K("sidebar", "Sidebar", "bars", "Ctrl+B"),
            K("format", "Format", "format", "Shift+Alt+F"), K("rename", "Rename", "cursor", "F2"),
            K("comment", "Comment", "comment", "Ctrl+/"), K("quick-fix", "Quick fix", "bolt", "Ctrl+."),
            K("git", "Source control", "branch", "Ctrl+Shift+G"), K("problems", "Problems", "bell", "Ctrl+Shift+M"),
            K("back", "Back", "back", "Alt+Left"), K("forward", "Forward", "forward", "Alt+Right"),
            K("close-tab", "Close tab", "close", "Ctrl+W"), K("reopen", "Reopen tab", "history", "Ctrl+Shift+T"),
        ], ["build", "terminal", "commands", "goto-file", "search", "split"]),
        ["chrome"] = new AppSet(0x34A853, BrowserItems, BrowserDefault),
        ["msedge"] = new AppSet(0x2B9FD9, BrowserItems, BrowserDefault),
        ["windowsterminal"] = new AppSet(0xE8A33D,
        [
            K("prev-cmd", "Prev cmd", "up", "Up"), K("next-cmd", "Next cmd", "down", "Down"), K("complete", "Complete", "forward", "Tab"),
            K("stop", "Stop", "stop", "Ctrl+C"), K("new-tab", "New tab", "plus", "Ctrl+Shift+T"), K("prev-tab", "Prev tab", "prev", "Ctrl+Shift+Tab"),
            K("next-tab", "Next tab", "next", "Ctrl+Tab"), K("split-right", "Split right", "split-right", "Alt+Shift+plus"),
            K("split-down", "Split down", "split-down", "Alt+Shift+minus"), K("pane-left", "Pane left", "back", "Alt+Left"),
            K("pane-right", "Pane right", "forward", "Alt+Right"), K("close", "Close pane", "close", "Ctrl+Shift+W"),
            K("find", "Find", "search", "Ctrl+Shift+F"), K("copy", "Copy", "copy", "Ctrl+Shift+C"), K("paste", "Paste", "paste", "Ctrl+Shift+V"),
            K("zoom-in", "Bigger", "zoom-in", "Ctrl+plus"), K("zoom-out", "Smaller", "zoom-out", "Ctrl+minus"),
            K("settings", "Settings", "settings", "Ctrl+,"),
        ], ["prev-cmd", "new-tab", "prev-tab", "next-tab", "split-right", "split-down"]),
        ["obsidian"] = new AppSet(0x8B5CF6,
        [
            A("daily", "Daily note", "calendar", "obs.today"), K("search", "Search", "search", "Ctrl+Shift+F"),
            K("quick-open", "Quick open", "folder", "Ctrl+O"), K("commands", "Commands", "keyboard", "Ctrl+P"),
            K("back", "Back", "back", "Ctrl+Alt+Left"), K("forward", "Forward", "forward", "Ctrl+Alt+Right"),
            K("graph", "Graph", "graph", "Ctrl+G"), K("new-note", "New note", "plus", "Ctrl+N"), K("reading", "Reading view", "eye", "Ctrl+E"),
            K("link", "Insert link", "link", "Ctrl+K"), K("checkbox", "Checkbox", "check", "Ctrl+L"), K("find", "Find in note", "search", "Ctrl+F"),
            K("close-tab", "Close tab", "close", "Ctrl+W"),
        ], ["daily", "search", "quick-open", "commands", "back", "graph"]),
        ["ms-teams"] = new AppSet(0x6264A7, TeamsItems, ["activity", "chat", "calendar", "new-chat", "search"]),
        ["teams"] = new AppSet(0x6264A7, TeamsItems, ["activity", "chat", "calendar", "new-chat", "search"]),
        ["ms-teams:call"] = new AppSet(0x6264A7, [.. TeamsCallItems, .. TeamsItems], ["mute", "camera", "hand", "share", "leave"]),
        ["teams:call"] = new AppSet(0x6264A7, [.. TeamsCallItems, .. TeamsItems], ["mute", "camera", "hand", "share", "leave"]),
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
    readonly object _gate = new();
    readonly Dictionary<string, List<string>> _layouts;  // app key → action ids, as arranged on the deck
    DeckWindow? _front;  // the app window in front last (kept while the desktop or the taskbar has it)
    string _exe = "";
    string _config = "\0";
    bool _inCall;
    Bar _bar = Bar.None;

    public ContextBar(WindowSource windows, Func<CallState> call, Action<string> act)
    {
        _windows = windows;
        _call = call;
        _act = act;
        _layouts = LoadLayouts();
    }

    public Bar Current
    {
        get
        {
            lock (_gate) return _bar;
        }
    }

    /// <summary>Raised when <see cref="Current"/> changes (another app in front, a meeting starting, settings, an edit).</summary>
    public event Action<Bar>? Changed;

    /// <summary>From the window list's poll: the window in front (null for the desktop, the taskbar, a menu).</summary>
    public void OnPolled(IReadOnlyList<DeckWindow> all, DeckWindow? front)
    {
        if (front == null) return;  // keep the last app's keys: they bring it back
        lock (_gate)
        {
            if (_front != null && front.Id == _front.Id && front.Icon == _front.Icon && front.App == _front.App) return;
            _front = front;
            _exe = Path.GetFileNameWithoutExtension(WindowSource.ExePath(front.Id)).ToLowerInvariant();
        }
        Update();
    }

    /// <summary>Settings or the call changed: the same window's keys again. Cheap when nothing did.</summary>
    public void Refresh()
    {
        var config = Settings.ContextBar?.ToJsonString() ?? "";
        lock (_gate)
        {
            var inCall = _call().Active;
            if (config == _config && inCall == _inCall) return;
            _config = config;
        }
        Update();
    }

    void Update()
    {
        Bar bar;
        lock (_gate)
        {
            _inCall = _call().Active;
            bar = _front == null ? Bar.None : Make(_front, _exe);
            if (Same(bar, _bar)) return;
            _bar = bar;
        }
        Changed?.Invoke(bar);
    }

    Bar Make(DeckWindow front, string exe)
    {
        var config = Settings.ContextBar;
        if (config is JsonValue value && value.TryGetValue(out bool on) && !on) return Bar.None;
        var key = exe;
        if (BuiltIn.ContainsKey($"{exe}:call") && _call() is { Active: true, App: "teams" }) key = $"{exe}:call";
        var builtIn = BuiltIn.GetValueOrDefault(key);
        var mine = FromSettings(config as JsonObject, exe, front.App);
        if (mine == null && builtIn == null) mine = FromSettings(config as JsonObject, "*", "*");
        var own = (mine?.Items ?? []).Concat(builtIn?.Items ?? []).DistinctBy(i => i.Id).ToList();
        var names = own.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bank = own.Concat(Everyday.Where(i => !names.Contains(i.Name))).ToList();
        var layout = _layouts.GetValueOrDefault(key) ?? (IReadOnlyList<string>?)mine?.Default ?? builtIn?.Default ?? EverydayDefault;
        var keys = layout.Select(id => bank.FirstOrDefault(i => i.Id == id)).OfType<Item>().Take(MaxKeys).ToList();
        var color = mine?.Color ?? builtIn?.Color ?? IconColor(_windows.FindIcon(front.Icon));
        return new Bar(front.App, key, front.Icon, color, keys, bank, own.Count);
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
                if (KeyInput.Parse(combo) == null) Log.Write($"context: can't read the keys \"{combo}\" (write them like Ctrl+Shift+Esc or Alt+Left)");
                else item = new Item("", name.Length > 0 ? name : combo, glyph, "keys", combo);
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
        a.Keys.Select(k => k.Id).SequenceEqual(b.Keys.Select(k => k.Id)) && a.Bank.Select(k => k.Id).SequenceEqual(b.Bank.Select(k => k.Id));

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
        lock (_gate)
        {
            key = _bar.Keys.FirstOrDefault(k => k.Id == id);
            hwnd = _front?.Id ?? 0;
        }
        if (key == null) return;
        if (key.Kind == "action")
        {
            _act(key.Target);
            return;
        }
        // The keys are for the window the bar names; something else may have the front now
        // (the desktop, the taskbar, a menu that closed).
        if (hwnd == 0 || !WindowSource.Exists(hwnd)) return;
        if (WindowSource.Foreground() != (IntPtr)hwnd)
        {
            WindowFocus.Activate((IntPtr)hwnd);
            Thread.Sleep(120);
        }
        if (key.Kind == "close") _windows.Close(hwnd);
        else KeyInput.Combo(key.Target);
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
