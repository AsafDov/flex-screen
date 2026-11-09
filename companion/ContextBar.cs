using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// The deck's context bar: keys for the app in front, in that app's color. Built-in sets for
/// VS Code, Chrome and Edge, Windows Terminal, Obsidian, Teams (another set during a
/// meeting) and Discord; everyday keys (copy, paste, undo, save, find, close) for anything
/// else. settings.json's "contextBar" changes or adds sets, by exe name ("chrome") or the
/// name the deck shows ("Chrome"), "*" for everything else, or false turns the bar off:
/// <code>
/// "contextBar": {
///   "Chrome": { "color": "#34A853", "keys": [ { "name": "Back", "keys": "Alt+Left", "glyph": "back" } ] },
///   "Spotify": [ { "name": "Like", "keys": "Alt+Shift+B", "glyph": "heart" } ]
/// }
/// </code>
/// A key is "keys" (pressed in that app's window, brought back to the front first if
/// something else took it), "action" (a deck action, as for shortcuts) or "action": "close"
/// (closes the window, as its X button does).
/// </summary>
sealed class ContextBar
{
    public const int MaxKeys = 8;

    /// <param name="Kind">keys, action or close.</param>
    public record Key(string Id, string Name, string Glyph, string Kind, string Target);

    /// <param name="Color">The bar's color, 0xRRGGBB.</param>
    public record Bar(string App, string Icon, int Color, IReadOnlyList<Key> Keys)
    {
        public static readonly Bar None = new("", "", 0, []);
    }

    record Set(int? Color, (string Name, string Glyph, string Kind, string Target)[] Keys);

    static (string, string, string, string) K(string name, string glyph, string keys) => (name, glyph, "keys", keys);
    static (string, string, string, string) A(string name, string glyph, string action) => (name, glyph, "action", action);

    static readonly Set Browser = new(null,
    [
        K("Back", "back", "Alt+Left"), K("Reload", "reload", "F5"), K("Prev tab", "prev", "Ctrl+Shift+Tab"),
        K("Next tab", "next", "Ctrl+Tab"), K("New tab", "plus", "Ctrl+T"), K("Reopen", "history", "Ctrl+Shift+T"),
        K("Find", "search", "Ctrl+F"),
    ]);

    static readonly Set Teams = new(0x6264A7,
    [
        K("Activity", "bell", "Ctrl+1"), K("Chat", "chat", "Ctrl+2"), K("Calendar", "calendar", "Ctrl+4"),
        K("New chat", "edit", "Ctrl+N"), K("Search", "search", "Ctrl+E"),
    ]);

    // In a meeting: the call panel's own controls (Teams' API), and sharing.
    static readonly Set TeamsCall = new(0x6264A7,
    [
        A("Mute", "mic-off", "call.mic"), A("Camera", "video", "call.camera"), A("Raise hand", "hand", "call.hand"),
        K("Share", "screen", "Ctrl+Shift+E"), A("Leave", "leave", "call.leave"),
    ]);

    /// <summary>By the exe's name, lower case.</summary>
    static readonly Dictionary<string, Set> BuiltIn = new()
    {
        ["code"] = new(0x2F9BF0,
        [
            K("Build", "play", "Ctrl+Shift+B"), K("Terminal", "terminal", "Ctrl+`"), K("Commands", "keyboard", "Ctrl+Shift+P"),
            K("Go to file", "file", "Ctrl+P"), K("Search", "search", "Ctrl+Shift+F"), K("Split", "split-right", "Ctrl+\\"),
        ]),
        ["chrome"] = Browser with { Color = 0x34A853 },
        ["msedge"] = Browser with { Color = 0x2B9FD9 },
        ["windowsterminal"] = new(0xE8A33D,
        [
            K("Prev cmd", "up", "Up"), K("New tab", "plus", "Ctrl+Shift+T"), K("Prev tab", "prev", "Ctrl+Shift+Tab"),
            K("Next tab", "next", "Ctrl+Tab"), K("Split right", "split-right", "Alt+Shift+plus"),
            K("Split down", "split-down", "Alt+Shift+minus"),
        ]),
        ["obsidian"] = new(0x8B5CF6,
        [
            A("Daily note", "calendar", "obs.today"), K("Search", "search", "Ctrl+Shift+F"), K("Quick open", "folder", "Ctrl+O"),
            K("Commands", "keyboard", "Ctrl+P"), K("Back", "back", "Ctrl+Alt+Left"), K("Graph", "graph", "Ctrl+G"),
        ]),
        ["ms-teams"] = Teams,
        ["teams"] = Teams,
        ["discord"] = new(0x5865F2,
        [
            K("Switch", "switch", "Ctrl+K"), K("Prev channel", "up", "Alt+Up"), K("Next channel", "down", "Alt+Down"),
            K("Unread", "unread", "Alt+Shift+Down"), K("Mute", "mic-off", "Ctrl+Shift+M"), K("Deafen", "headphones", "Ctrl+Shift+D"),
        ]),
        ["*"] = new(null,
        [
            K("Copy", "copy", "Ctrl+C"), K("Paste", "paste", "Ctrl+V"), K("Undo", "undo", "Ctrl+Z"),
            K("Save", "save", "Ctrl+S"), K("Find", "search", "Ctrl+F"), ("Close", "close", "close", ""),
        ]),
    };

    const int Neutral = 0x8A94A6;  // an icon without a color of its own

    readonly WindowSource _windows;
    readonly Func<CallState> _call;
    readonly Action<string> _act;
    readonly object _gate = new();
    DeckWindow? _front;  // the app window in front last (kept while the desktop or the taskbar has it)
    string _exe = "";
    string _config = "\0";
    Bar _bar = Bar.None;

    public ContextBar(WindowSource windows, Func<CallState> call, Action<string> act)
    {
        _windows = windows;
        _call = call;
        _act = act;
    }

    public Bar Current
    {
        get
        {
            lock (_gate) return _bar;
        }
    }

    /// <summary>Raised when <see cref="Current"/> changes (another app in front, a meeting starting, settings).</summary>
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
            if (config == _config && !InCallChanged()) return;
            _config = config;
        }
        Update();
    }

    bool _inCall;

    bool InCallChanged()
    {
        var inCall = _call().Active;
        if (inCall == _inCall) return false;
        _inCall = inCall;
        return true;
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
        var set = FromSettings(config as JsonObject, exe, front.App) ?? BuiltIn.GetValueOrDefault(exe)
                  ?? FromSettings(config as JsonObject, "*", "*") ?? BuiltIn["*"];
        if (set == Teams && _call() is { Active: true, App: "teams" }) set = TeamsCall;
        var color = set.Color ?? IconColor(_windows.FindIcon(front.Icon));
        var keys = set.Keys.Take(MaxKeys).Select(k => new Key(
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{exe}\n{k.Name}\n{k.Kind}\n{k.Target}")))[..10].ToLowerInvariant(),
            k.Name, k.Glyph, k.Kind, k.Target)).ToList();
        return new Bar(front.App, front.Icon, color, keys);
    }

    /// <summary>A set from settings.json, by exe name or the app's name on the deck; null if it has none.</summary>
    static Set? FromSettings(JsonObject? config, string exe, string app)
    {
        if (config == null) return null;
        var node = config.FirstOrDefault(p => string.Equals(p.Key, exe, StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(p.Key, app, StringComparison.OrdinalIgnoreCase)).Value;
        var list = node as JsonArray ?? node?["keys"] as JsonArray;
        if (list == null) return null;
        int? color = null;
        if ((string?)node?["color"] is { } hex && int.TryParse(hex.TrimStart('#'), System.Globalization.NumberStyles.HexNumber, null, out var c)) color = c;
        var keys = new List<(string, string, string, string)>();
        foreach (var k in list.OfType<JsonObject>())
        {
            var name = (string?)k["name"] ?? "";
            var glyph = (string?)k["glyph"] ?? "";
            if ((string?)k["keys"] is { Length: > 0 } combo)
            {
                if (KeyInput.Parse(combo) == null)
                {
                    Log.Write($"context: can't read the keys \"{combo}\" (write them like Ctrl+Shift+Esc or Alt+Left)");
                    continue;
                }
                keys.Add((name.Length > 0 ? name : combo, glyph, "keys", combo));
            }
            else if ((string?)k["action"] is { Length: > 0 } action)
            {
                if (action == "close") keys.Add((name.Length > 0 ? name : "Close", glyph.Length > 0 ? glyph : "close", "close", ""));
                else if (Shortcuts.Actions.ContainsKey(action)) keys.Add((name.Length > 0 ? name : action, glyph, "action", action));
                else Log.Write($"context: \"{action}\" isn't an action a key can run");
            }
        }
        return new Set(color, keys.ToArray());
    }

    static bool Same(Bar a, Bar b) =>
        a.App == b.App && a.Icon == b.Icon && a.Color == b.Color && a.Keys.Select(k => k.Id).SequenceEqual(b.Keys.Select(k => k.Id));

    /// <summary>A key tapped on the deck.</summary>
    public void Run(string id)
    {
        Key? key;
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
