using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FlexScreen.Companion;

/// <summary>
/// Counts the shortcuts pressed on the PC's own keyboards, per app (the context bar's key:
/// "code", "chrome:gmail"), so the bar's long-press menu can offer the ones used most there.
/// Only key combinations with Ctrl, Alt or Win, and the F-keys, are counted: never typing. A
/// key without those modifiers goes no further than the hook's first check, and nothing but
/// the combination's name ("Ctrl+Shift+P") is kept. Keys a program presses (the deck's own
/// context bar keys among them) are left out, and so is AltGr (Windows reports it as Ctrl
/// with right Alt), which types characters on some layouts. The counts live in
/// %LOCALAPPDATA%\flex-screen\shortcut-counts.json; one not pressed for 30 days is dropped.
/// </summary>
sealed class ShortcutCounter : IDisposable
{
    /// <summary>The id of a context bar key made from a combination the bank has no action for: "used-ctrl+shift+p".</summary>
    public const string Prefix = "used-";
    const int MinCount = 5;  // pressed this often in the app before it's offered
    const int MaxSuggestions = 5;
    static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(2);
    static readonly TimeSpan Forget = TimeSpan.FromDays(30);
    static readonly string CountsPath = Path.Combine(AppPaths.Data, "shortcut-counts.json");

    sealed class Entry
    {
        public int Count { get; set; }
        public DateTime Last { get; set; }
    }

    readonly Func<string> _app;
    readonly object _gate = new();
    readonly Dictionary<string, Dictionary<string, Entry>> _counts;  // app key → combination → count
    readonly HashSet<uint> _down = [];  // keys held, so a key's auto-repeat counts once (the hook's thread only)
    // The hook only notes the keys: every key on the PC waits for it, so the counting (which
    // asks the context bar for the app and takes locks) is done on a thread of its own.
    readonly System.Collections.Concurrent.BlockingCollection<List<ushort>> _pressed = new(256);
    readonly System.Threading.Timer _saver;
    HookProc? _proc;  // kept alive: the hook calls it
    uint _threadId;
    bool _dirty;

    /// <param name="app">The context bar's key for the app in front ("" for none).</param>
    public ShortcutCounter(Func<string> app)
    {
        _app = app;
        _counts = Load();
        _saver = new System.Threading.Timer(_ => Save(), null, SaveEvery, SaveEvery);
        // A low-level hook is called on the thread that set it, which needs a message loop.
        new Thread(Run) { IsBackground = true, Name = "shortcut hook" }.Start();
        new Thread(Count) { IsBackground = true, Name = "shortcut counter" }.Start();
    }

    void Count()
    {
        foreach (var codes in _pressed.GetConsumingEnumerable())
        {
            try
            {
                if (Name(codes) is not { } combo) continue;  // a key the bar couldn't press (the numpad's)
                var app = _app();
                if (app.Length == 0) continue;
                lock (_gate)
                {
                    if (!_counts.TryGetValue(app, out var counts)) _counts[app] = counts = [];
                    if (!counts.TryGetValue(combo, out var entry)) counts[combo] = entry = new Entry();
                    entry.Count++;
                    entry.Last = DateTime.UtcNow;
                    _dirty = true;
                }
            }
            catch (Exception e)
            {
                Log.Write($"shortcuts: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    void Run()
    {
        _threadId = GetCurrentThreadId();
        _proc = OnKey;
        var hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            Log.Write($"shortcuts: can't watch the keyboard ({new Win32Exception(Marshal.GetLastWin32Error()).Message})");
            return;
        }
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        UnhookWindowsHookEx(hook);
    }

    IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0) Note((int)wParam, Marshal.PtrToStructure<KbdLlHookStruct>(lParam));
        }
        catch (Exception e)
        {
            Log.Write($"shortcuts: {e.GetType().Name}: {e.Message}");
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    void Note(int message, KbdLlHookStruct key)
    {
        var vk = key.VkCode;
        if (message is WmKeyUp or WmSysKeyUp)
        {
            _down.Remove(vk);
            return;
        }
        if (message is not (WmKeyDown or WmSysKeyDown) || (key.Flags & LlkhfInjected) != 0 || IsModifier(vk)) return;
        var ctrl = Down(VkControl);
        var alt = Down(VkMenu);
        var win = Down(VkLWin) || Down(VkRWin);
        var fkey = vk is >= 0x70 and <= 0x87;
        if (!ctrl && !alt && !win && !fkey) return;  // typing: not looked at any further
        if (ctrl && Down(VkRMenu) && Down(VkLControl)) return;  // AltGr
        if (!_down.Add(vk)) return;  // held: auto-repeat
        var codes = new List<ushort>(5);
        if (ctrl) codes.Add(KeyInput.Control);
        if (alt) codes.Add(KeyInput.Alt);
        if (Down(VkShift)) codes.Add(VkShift);
        if (win) codes.Add(VkLWin);
        codes.Add((ushort)vk);
        _pressed.TryAdd(codes);  // never waits: a full queue drops it
    }

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    static bool IsModifier(uint vk) => vk is 0x10 or 0x11 or 0x12 or (>= 0xA0 and <= 0xA5) or 0x5B or 0x5C;

    /// <summary>
    /// The context bar keys to offer for an app: the combinations pressed there most (at least
    /// <see cref="MinCount"/> times) that aren't on its bar yet, as the bank's own action when
    /// it has one for those keys, else as a key of their own.
    /// </summary>
    public List<BarItem> Suggest(string app, IEnumerable<BarItem> bank, IEnumerable<BarItem> onBar)
    {
        List<string> top;
        lock (_gate)
        {
            if (!_counts.TryGetValue(app, out var counts)) return [];
            top = counts.Where(c => c.Value.Count >= MinCount).OrderByDescending(c => c.Value.Count).ThenBy(c => c.Key)
                .Select(c => c.Key).ToList();
        }
        var bar = onBar.Select(KeysOf).ToHashSet();
        var actions = bank.ToList();
        var list = new List<BarItem>();
        foreach (var combo in top)
        {
            if (list.Count == MaxSuggestions) break;
            if (bar.Contains(combo)) continue;
            var item = actions.FirstOrDefault(i => KeysOf(i) == combo) ?? Item(Prefix + combo.ToLowerInvariant());
            if (item != null && list.All(i => i.Id != item.Id)) list.Add(item);
        }
        return list;
    }

    /// <summary>The key a "used-…" id stands for (null for any other id, or keys that can't be read).</summary>
    public static BarItem? Item(string id)
    {
        if (!id.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var keys = id[Prefix.Length..];
        if (keys.Contains(',') || KeyInput.Parse(keys) is not { } codes || Name(codes) is not { } name) return null;
        return BarItem.K(id, name, "keyboard", name);
    }

    /// <summary>A key's combination as the counts name it ("Ctrl+Shift+P"); "" for anything but a single combination.</summary>
    static string KeysOf(BarItem item) =>
        item.Kind == "keys" && !item.Target.Contains(',') && KeyInput.Parse(item.Target) is { } codes ? Name(codes) ?? "" : "";

    /// <summary>
    /// Key codes as one name, modifiers first in a fixed order: "Ctrl+Alt+Shift+Win+K". Null
    /// without exactly one key that isn't a modifier, or one <see cref="KeyInput"/> has no name for.
    /// </summary>
    static string? Name(IEnumerable<ushort> codes)
    {
        var parts = new List<string>();
        var set = codes.ToHashSet();
        if (set.Contains(KeyInput.Control)) parts.Add("Ctrl");
        if (set.Contains(KeyInput.Alt)) parts.Add("Alt");
        if (set.Contains(VkShift)) parts.Add("Shift");
        if (set.Contains(VkLWin)) parts.Add("Win");
        var keys = set.Where(vk => !IsModifier(vk)).ToList();
        if (keys.Count != 1 || KeyName(keys[0]) is not { } key) return null;
        parts.Add(key);
        return string.Join("+", parts);
    }

    /// <summary>A key's name as <see cref="KeyInput"/> reads it back (never "," or "+": those separate).</summary>
    static string? KeyName(ushort vk) => vk switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        0x1B => "Esc", 0x09 => "Tab", 0x0D => "Enter", 0x20 => "Space", 0x08 => "Backspace", 0x2E => "Delete", 0x2D => "Insert",
        0x24 => "Home", 0x23 => "End", 0x21 => "PageUp", 0x22 => "PageDown",
        0x26 => "Up", 0x28 => "Down", 0x25 => "Left", 0x27 => "Right", 0x2C => "PrintScreen", 0x13 => "Pause",
        0xBD => "-", 0xBB => "=", 0xBC => "comma", 0xBE => ".", 0xBF => "/", 0xBA => ";", 0xDE => "'",
        0xDB => "[", 0xDD => "]", 0xDC => "\\", 0xC0 => "`",
        _ => null,
    };

    static Dictionary<string, Dictionary<string, Entry>> Load()
    {
        try
        {
            if (File.Exists(CountsPath))
            {
                var counts = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Entry>>>(File.ReadAllText(CountsPath)) ?? [];
                Prune(counts);
                return counts;
            }
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            Log.Write($"shortcuts: can't read {CountsPath}: {e.Message}");
        }
        return [];
    }

    static void Prune(Dictionary<string, Dictionary<string, Entry>> counts)
    {
        var old = DateTime.UtcNow - Forget;
        foreach (var app in counts.Keys.ToList())
        {
            foreach (var combo in counts[app].Where(c => c.Value.Last < old).Select(c => c.Key).ToList()) counts[app].Remove(combo);
            if (counts[app].Count == 0) counts.Remove(app);
        }
    }

    void Save()
    {
        string json;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            Prune(_counts);
            json = JsonSerializer.Serialize(_counts, new JsonSerializerOptions { WriteIndented = true });
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CountsPath)!);
            File.WriteAllText(CountsPath, json);
        }
        catch (IOException e)
        {
            Log.Write($"shortcuts: can't save {CountsPath}: {e.Message}");
        }
    }

    public void Dispose()
    {
        _saver.Dispose();
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _pressed.CompleteAdding();
        Save();
    }

    const int WhKeyboardLl = 13;
    const int WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105, WmQuit = 0x12;
    const uint LlkhfInjected = 0x10;
    const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkLWin = 0x5B, VkRWin = 0x5C, VkLControl = 0xA2, VkRMenu = 0xA5;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KbdLlHookStruct
    {
        public uint VkCode, ScanCode, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref Msg msg);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
