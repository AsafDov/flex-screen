using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using static FlexScreen.Companion.BarItem;

namespace FlexScreen.Companion;

/// <summary>
/// The deck's context bar: keys for the app in front, in that app's color. Each app with an
/// integration (<see cref="IAppIntegration"/>, under Integrations/: VS Code, Chrome and Edge
/// with Gmail, YouTube and Google Calendar sets, Windows Terminal, Explorer, Obsidian,
/// Spotify, Teams and Zoom with another bank during a meeting, Discord, the Claude app) has a
/// bank of actions and a few of them on the bar by default; for any app there's an
/// "everywhere" bank after its own: copy, paste, snap, screenshot… Claude's keys (Approve,
/// Deny, Model, Effort, Usage) are in the Claude app's, VS Code's and Terminal's banks. On the
/// deck, a long press on a key picks another action from the bank for that place (one already
/// on the bar swaps places with it), removes the key, or resets the app's bar; "+" adds one.
/// Those choices are kept per app (by the bar's key: "code", "chrome:gmail") in
/// %LOCALAPPDATA%\flex-screen\context-bar.json.
/// The profile's "contextBar" adds actions to an app's bank (by exe name, "chrome", or
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

    /// <param name="Key">Whose layout this is: the exe's name ("code"), "chrome:gmail" on Gmail, "ms-teams:call" during a meeting.</param>
    /// <param name="Color">The bar's color, 0xRRGGBB.</param>
    /// <param name="Bank">Every action the bar can hold: the app's own first (<paramref name="Own"/> of them), then the everyday ones.</param>
    public record Bar(string App, string Key, string Icon, int Color, IReadOnlyList<BarItem> Keys, IReadOnlyList<BarItem> Bank, int Own)
    {
        public static readonly Bar None = new("", "", "", 0, [], [], 0);
    }

    /// <summary>For any app, after its own.</summary>
    static readonly BarItem[] Everyday =
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

    const int Neutral = 0x8A94A6;  // an icon without a color of its own

    static readonly string LayoutsPath = Path.Combine(AppPaths.Data, "context-bar.json");

    readonly WindowSource _windows;
    readonly Action<string> _act;
    readonly Dictionary<string, IAppIntegration> _apps;  // by exe name
    readonly object _gate = new();
    readonly Dictionary<string, List<string>> _layouts;  // app key → action ids, as arranged on the deck
    DeckWindow? _front;  // the app window in front last (kept while the desktop or the taskbar has it)
    string _exe = "";
    string _context = "";  // what the app in front is doing, as it says: "gmail", "claude", "call"
    string _config = "\0";
    Bar _bar = Bar.None;

    /// <param name="act">Runs a deck action (the "action" keys).</param>
    /// <param name="apps">Every app with keys of its own (<see cref="AppIntegrations.All"/>).</param>
    public ContextBar(WindowSource windows, Action<string> act, IEnumerable<IAppIntegration> apps)
    {
        _windows = windows;
        _act = act;
        var list = apps.ToList();
        _apps = list.SelectMany(a => a.Exes.Select(exe => (exe, a))).ToDictionary(p => p.exe, p => p.a);
        _layouts = LoadLayouts();
        Log.Write("context: apps " + string.Join(", ", list.Select(a =>
            a.Exes[0] + (a.Topics.Count > 0 ? $" ({string.Join(" ", a.Topics)})" : ""))));
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

    /// <summary>From the window list's poll: the window in front (null for the desktop, the taskbar, a menu).</summary>
    public void OnPolled(IReadOnlyList<DeckWindow> all, DeckWindow? front)
    {
        if (front == null) return;  // keep the last app's keys: they bring it back
        lock (_gate)
        {
            if (_front != null && front.Id == _front.Id && front.Icon == _front.Icon && front.App == _front.App &&
                ContextOf(front, _exe) == _context) return;
        }
        Show(front, Exe(front));
    }

    static string Exe(DeckWindow window) => Path.GetFileNameWithoutExtension(WindowSource.ExePath(window.Id)).ToLowerInvariant();

    /// <summary>Another window in front, or the same one doing something else.</summary>
    void Show(DeckWindow front, string exe)
    {
        lock (_gate)
        {
            _exe = exe;
            _front = front;
        }
        Update();
    }

    static AppWindow Window(DeckWindow? front, string exe) => new(front?.Id ?? 0, exe, front?.Title ?? "", front?.App ?? "");

    /// <summary>What the window is doing that has keys of its own, as its app tells: Gmail by the tab's title, Claude's tab in VS Code, a meeting.</summary>
    string ContextOf(DeckWindow front, string exe) => _apps.GetValueOrDefault(exe)?.Context(Window(front, exe)) ?? "";

    /// <summary>Settings, a call, a Claude session or VS Code's tab changed: the same window's keys again, sent only if they differ.</summary>
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
            if (_front != null) _context = ContextOf(_front, _exe);
            bar = _front == null ? Bar.None : Make(_front, _exe, _context);
            if (Same(bar, _bar)) return;
            _bar = bar;
        }
        Changed?.Invoke(bar);
    }

    Bar Make(DeckWindow front, string exe, string context)
    {
        var config = Settings.ContextBar;
        if (config is JsonValue value && value.TryGetValue(out bool on) && !on) return Bar.None;
        var integration = _apps.GetValueOrDefault(exe);
        var window = Window(front, exe);
        var key = exe;
        var builtIn = integration?.Bank("");
        if (context.Length > 0 && integration?.Bank(context) is { } inContext)
        {
            key = $"{exe}:{context}";
            builtIn = inContext;
        }
        var app = builtIn is { Name.Length: > 0 } ? builtIn.Name : front.App;
        var mine = FromSettings(config as JsonObject, exe, front.App);
        if (mine == null && builtIn == null) mine = FromSettings(config as JsonObject, "*", "*");
        var own = (mine?.Items ?? []).Concat(builtIn?.Items ?? []).DistinctBy(i => i.Id).ToList();
        var names = own.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bank = own.Concat(Everyday.Where(i => !names.Contains(i.Name))).Select(i => integration?.Live(i, window) ?? i).ToList();
        var layout = _layouts.GetValueOrDefault(key) ?? mine?.Default ?? builtIn?.Default ?? EverydayDefault;
        var keys = layout.Select(id => bank.FirstOrDefault(i => i.Id == id)).OfType<BarItem>().Take(MaxKeys).ToList();
        var color = mine?.Color ?? builtIn?.Color ?? IconColor(_windows.FindIcon(front.Icon));
        return new Bar(app, key, front.Icon, color, keys, bank, own.Count);
    }

    /// <summary>An app's actions from the settings, by exe name or the app's name on the deck; null if it has none.</summary>
    static AppBank? FromSettings(JsonObject? config, string exe, string app)
    {
        if (config == null) return null;
        var node = config.FirstOrDefault(p => string.Equals(p.Key, exe, StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(p.Key, app, StringComparison.OrdinalIgnoreCase)).Value;
        var list = node as JsonArray ?? node?["keys"] as JsonArray;
        if (list == null) return null;
        int? color = null;
        if ((string?)node?["color"] is { } hex && int.TryParse(hex.TrimStart('#'), System.Globalization.NumberStyles.HexNumber, null, out var c)) color = c;
        var items = new List<BarItem>();
        foreach (var k in list.OfType<JsonObject>())
        {
            var name = (string?)k["name"] ?? "";
            var glyph = (string?)k["glyph"] ?? "";
            BarItem? item = null;
            if ((string?)k["keys"] is { Length: > 0 } combo)
            {
                if (!KeyInput.CanPress(combo)) Log.Write($"context: can't read the keys \"{combo}\" (write them like Ctrl+Shift+Esc or Alt+Left)");
                else item = new BarItem("", name.Length > 0 ? name : combo, glyph, "keys", combo);
            }
            else if ((string?)k["text"] is { Length: > 0 } text)
            {
                item = new BarItem("", name.Length > 0 ? name : text.Trim(), glyph, "text", text);
            }
            else if ((string?)k["action"] is { Length: > 0 } action)
            {
                if (action == "close") item = new BarItem("", name.Length > 0 ? name : "Close", glyph.Length > 0 ? glyph : "close", "close", "");
                else if (Shortcuts.Actions.ContainsKey(action)) item = new BarItem("", name.Length > 0 ? name : action, glyph, "action", action);
                else Log.Write($"context: \"{action}\" isn't an action a key can run");
            }
            if (item != null) items.Add(item with { Id = "my-" + Slug(item.Name) });
        }
        var unique = items.DistinctBy(i => i.Id).ToArray();
        return new AppBank("", color, unique, unique.Select(i => i.Id).ToArray());
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

    /// <summary>
    /// A key tapped on the deck: a deck action or a link; one of the app's own kinds, run by its
    /// integration (a VS Code command, Explorer's folder, Claude's lists); else its keys or text
    /// in the window the bar names, or that window closed.
    /// </summary>
    public void Run(string id)
    {
        BarItem? key;
        AppWindow window;
        IAppIntegration? app;
        lock (_gate)
        {
            key = _bar.Keys.FirstOrDefault(k => k.Id == id);
            window = Window(_front, _exe);
            app = _apps.GetValueOrDefault(_exe);
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
        }
        if (app != null && app.Run(key, window)) return;
        if (!window.ToFront()) return;
        if (key.Kind == "close") _windows.Close(window.Hwnd);
        else if (key.Kind == "text") KeyInput.TypeLines(key.Target.Replace("\\n", "\n"));
        else KeyInput.Sequence(key.Target);
    }

    /// <summary>A choice from a pick key's list (Claude's model or effort), for the app in front to make.</summary>
    public void Choose(string id, string choice)
    {
        BarItem? key;
        AppWindow window;
        IAppIntegration? app;
        lock (_gate)
        {
            key = _bar.Keys.FirstOrDefault(k => k.Id == id);
            window = Window(_front, _exe);
            app = _apps.GetValueOrDefault(_exe);
        }
        if (key is not { Kind: "pick" }) return;
        app?.Choose(key, choice, window);
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
