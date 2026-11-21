using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace FlexScreen.Companion;

/// <param name="Rect">Relative to the monitor's work area, in physical pixels.</param>
/// <param name="App">The app whose window fills the zone now, or "".</param>
record DeckZone(Rectangle Rect, string App);

/// <param name="Id">FancyZones' "monitor": the EDID code, "DEL4240".</param>
/// <param name="Instance">FancyZones' "monitor-instance": the display's PnP instance.</param>
record DeckMonitor(string Id, string Instance, int Number, string Name, bool Primary, Rectangle Work,
                   ZoneLayout Layout, IReadOnlyList<DeckZone> Zones);

/// <param name="Id">A built-in layout's type ("columns"), or a custom layout's uuid.</param>
/// <param name="Thumb">Its zones in a 40x26 thumbnail.</param>
record LayoutChoice(string Id, string Name, bool Custom, IReadOnlyList<Rectangle> Thumb);

/// <param name="Active">Index of the monitor the foreground window is on.</param>
/// <param name="Layouts">The layouts the deck offers: custom ones first.</param>
record ZoneState(IReadOnlyList<DeckMonitor> Monitors, int Active, string App, IReadOnlyList<LayoutChoice> Layouts);

/// <summary>
/// FancyZones layouts per monitor (read from PowerToys' own files, so the deck matches what
/// FancyZones snaps to), snapping the foreground window into a zone, and switching layouts.
/// Works whether FancyZones is running or not.
/// </summary>
sealed class ZoneSource
{
    const int MaxMonitors = 3;
    static readonly TimeSpan Reload = TimeSpan.FromSeconds(3);
    static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "PowerToys", "FancyZones");
    static readonly string AppliedPath = Path.Combine(Folder, "applied-layouts.json");
    static readonly string CustomPath = Path.Combine(Folder, "custom-layouts.json");
    const string BlankUuid = "{00000000-0000-0000-0000-000000000000}";
    static readonly ZoneLayout Default = new("priority-grid", 3, true, 16);  // what FancyZones gives a new monitor

    // Tapping the layout that's already applied steps through its zone counts.
    static readonly Dictionary<string, int[]> Counts = new()
    {
        ["priority-grid"] = [3, 4, 5, 2], ["columns"] = [3, 2, 4], ["rows"] = [2, 3], ["grid"] = [4, 6, 9],
    };
    static readonly (string Type, string Name)[] Templates =
        [("priority-grid", "Priority"), ("columns", "Columns"), ("rows", "Rows"), ("grid", "Grid")];
    const int MaxChoices = 4;  // the deck's 2x2 of layout buttons
    const int MaxThumbZones = 12;
    const int ThumbW = 40, ThumbH = 26;

    readonly object _gate = new();
    List<DeckMonitor> _monitors = [];
    List<LayoutChoice> _choices = [];
    JsonArray _custom = [];
    DateTime _loadedAt = DateTime.MinValue;
    string _key = "";

    public ZoneState Current { get; private set; } = new([], 0, "", []);

    /// <summary>Raised on the window poll's thread when zones, layouts or who's in them change.</summary>
    public event Action<ZoneState>? Changed;

    /// <summary>A snap Windows or the app refused, and why.</summary>
    public event Action<Toast>? Toast;

    public ZoneSource(WindowSource windows)
    {
        windows.Polled += Update;
        SystemEvents.DisplaySettingsChanged += (_, _) => _loadedAt = DateTime.MinValue;
    }

    void Update(IReadOnlyList<DeckWindow> windows, DeckWindow? foreground)
    {
        List<DeckMonitor> monitors;
        List<LayoutChoice> choices;
        lock (_gate)
        {
            if (DateTime.UtcNow - _loadedAt > Reload)
            {
                _monitors = LoadMonitors();
                _loadedAt = DateTime.UtcNow;
            }
            monitors = _monitors;
            choices = _choices;
        }
        // Label each zone with the frontmost window that fills it.
        var labelled = monitors.Select(m => m with
        {
            Zones = m.Zones.Select(z =>
            {
                var screen = z.Rect with { X = z.Rect.X + m.Work.X, Y = z.Rect.Y + m.Work.Y };
                var app = windows.FirstOrDefault(w => !w.Minimized && Overlap(w.Bounds, screen) > 0.9)?.App ?? "";
                return z with { App = app };
            }).ToList(),
        }).ToList();
        var active = foreground == null ? 0 : Math.Max(0, labelled.FindIndex(m => Contains(m, foreground.Bounds)));
        var state = new ZoneState(labelled, active, foreground?.App ?? "", choices);
        var key = JsonSerializer.Serialize(Message(state));
        if (key == _key) return;
        _key = key;
        Current = state;
        Changed?.Invoke(state);
    }

    /// <summary>The "zones" state topic.</summary>
    public static object Message(ZoneState s) => new
    {
        mons = s.Monitors.Select(m => new
        {
            name = m.Name,
            primary = m.Primary,
            x = m.Work.X,  // where the work area is on the screens, as windows' places are given (the deck's desktop map)
            y = m.Work.Y,
            w = m.Work.Width,
            h = m.Work.Height,
            type = m.Layout.Type,
            n = m.Layout.Count,
            layout = LayoutId(m.Layout),
            zones = m.Zones.Select(z => new object[] { z.Rect.X, z.Rect.Y, z.Rect.Width, z.Rect.Height, z.App }),
        }),
        active = s.Active,
        app = s.App,
        layouts = s.Layouts.Select(c => new
        {
            id = c.Id,
            name = c.Name,
            custom = c.Custom,
            cells = c.Thumb.Select(r => new[] { r.X, r.Y, r.Width, r.Height }),
        }),
    };

    /// <summary>What a layout button matches: the type for built-in layouts, the uuid for custom ones.</summary>
    static string LayoutId(ZoneLayout l) => l.Type == "custom" ? (string?)l.Custom?["uuid"] ?? "" : l.Type;

    /// <summary>
    /// The layout buttons: the user's custom layouts first (from the FancyZones editor), then
    /// the built-in ones that still fit.
    /// </summary>
    static List<LayoutChoice> Choices(JsonArray custom)
    {
        var choices = new List<LayoutChoice>();
        foreach (var c in custom)
        {
            if (choices.Count == MaxChoices) break;
            var uuid = (string?)c?["uuid"];
            if (string.IsNullOrEmpty(uuid)) continue;
            var zones = FancyZonesLayouts.Zones(new ZoneLayout("custom", 0, false, 0, c), 1920, 1080);
            if (zones.Count > 0) choices.Add(new LayoutChoice(uuid, (string?)c?["name"] ?? "Custom", true, Thumb(zones)));
        }
        foreach (var (type, name) in Templates)
        {
            if (choices.Count == MaxChoices) break;
            var zones = FancyZonesLayouts.Zones(new ZoneLayout(type, Counts[type][0], false, 0), 1920, 1080);
            choices.Add(new LayoutChoice(type, name, false, Thumb(zones)));
        }
        return choices;
    }

    /// <summary>Zones scaled from 1920x1080 into the thumbnail, a pixel apart.</summary>
    static List<Rectangle> Thumb(List<Rectangle> zones) => zones.Take(MaxThumbZones).Select(z =>
    {
        int x = z.Left * ThumbW / 1920, y = z.Top * ThumbH / 1080;
        int right = z.Right * ThumbW / 1920 - 1, bottom = z.Bottom * ThumbH / 1080 - 1;
        return Rectangle.FromLTRB(x, y, Math.Max(right, x + 1), Math.Max(bottom, y + 1));
    }).ToList();

    /// <summary>Moves the foreground window (or, for tests, the given one) into a zone, as FancyZones would.</summary>
    public void Snap(int monitor, int zone, long handle = 0)
    {
        var state = Current;
        if (monitor < 0 || monitor >= state.Monitors.Count || zone < 0 || zone >= state.Monitors[monitor].Zones.Count)
        {
            Log.Write($"snap: no zone {zone} on monitor {monitor}");
            return;
        }
        var m = state.Monitors[monitor];
        var hwnd = handle != 0 ? (IntPtr)handle : WindowSource.Foreground();
        if (hwnd == IntPtr.Zero || (handle == 0 && state.App.Length == 0))
        {
            Log.Write("snap: no app window has the focus");  // the desktop or the taskbar does
            return;
        }
        var target = m.Zones[zone].Rect;
        target.Offset(m.Work.Location);
        // Tapping the zone a window already fills maximizes it (and a tap on a zone snaps a
        // maximized window back in).
        if (!IsZoomed(hwnd) && !IsIconic(hwnd) && Overlap(WindowSource.Bounds(hwnd), target) > 0.9)
        {
            ShowWindow(hwnd, SwMaximize);
            Log.Write($"snap: \"{WindowSource.WindowText(hwnd)}\" maximized from {m.Name} zone {zone + 1}");
            lock (_gate) _loadedAt = DateTime.MinValue;
            return;
        }
        if (IsZoomed(hwnd) || IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
        // Twice: a window moved onto a monitor with another scale resizes itself on arrival,
        // and its invisible borders change size with it.
        for (var pass = 0; pass < 2; pass++)
        {
            GetWindowRect(hwnd, out var window);
            var visible = WindowSource.Bounds(hwnd);
            // The window rect includes invisible resize borders; FancyZones leaves the top alone.
            // Always resized: apps that draw their own frame (the Claude app, Windows 11's
            // Notepad) resize fine but don't set WS_THICKFRAME, so checking it left them unsized.
            var left = target.Left - (visible.Left - window.Left);
            var right = target.Right - (visible.Right - window.Right);
            var bottom = target.Bottom - (visible.Bottom - window.Bottom);
            if (!SetWindowPos(hwnd, IntPtr.Zero, left, target.Top, right - left, bottom - target.Top,
                              SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder))
            {
                var error = Marshal.GetLastWin32Error();
                Log.Write($"snap: SetWindowPos failed ({error})");
                if (error == ErrorAccessDenied)
                {
                    // An app running as administrator: Windows doesn't let a normal app move it.
                    Toast?.Invoke(new Toast("error", $"{AppName(state, handle, hwnd)} runs as administrator, so it can't be moved from the deck"));
                    return;
                }
            }
        }
        var after = WindowSource.Bounds(hwnd);
        Log.Write($"snap: \"{WindowSource.WindowText(hwnd)}\" to {m.Name} zone {zone + 1} " +
                  $"({target.X},{target.Y} {target.Width}x{target.Height}), now {after.X},{after.Y} {after.Width}x{after.Height}");
        if (Overlap(after, target) < 0.5)
        {
            // Some apps hold their own size, e.g. Electron apps maximized or full screen by
            // their own title bar (the Claude app): they undo the move as it happens.
            Toast?.Invoke(new Toast("attention", $"{AppName(state, handle, hwnd)} keeps its own size. Restore it in the app, then snap"));
        }
        lock (_gate) _loadedAt = DateTime.MinValue;
    }

    /// <summary>
    /// Moves a window onto a monitor, its top-left at (x, y) of the work area, keeping its size
    /// (made smaller only to fit): the deck's desktop map, a window let go where there's no
    /// zone. A maximized or minimized window is restored first.
    /// </summary>
    public bool Move(long handle, int monitor, int x, int y)
    {
        var state = Current;
        if (monitor < 0 || monitor >= state.Monitors.Count || !WindowSource.Exists(handle)) return false;
        var m = state.Monitors[monitor];
        var hwnd = (IntPtr)handle;
        if (IsZoomed(hwnd) || IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
        // Twice, as for a snap: arriving on a monitor with another scale resizes it.
        for (var pass = 0; pass < 2; pass++)
        {
            GetWindowRect(hwnd, out var window);
            var visible = WindowSource.Bounds(hwnd);
            var w = Math.Min(visible.Width, m.Work.Width);
            var h = Math.Min(visible.Height, m.Work.Height);
            var target = new Rectangle(m.Work.Left + Math.Clamp(x, 0, m.Work.Width - w), m.Work.Top + Math.Clamp(y, 0, m.Work.Height - h), w, h);
            // The window rect is the visible one plus invisible resize borders.
            var left = target.Left - (visible.Left - window.Left);
            var top = target.Top - (visible.Top - window.Top);
            var right = target.Right - (visible.Right - window.Right);
            var bottom = target.Bottom - (visible.Bottom - window.Bottom);
            if (!SetWindowPos(hwnd, IntPtr.Zero, left, top, right - left, bottom - top, SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder))
            {
                var error = Marshal.GetLastWin32Error();
                Log.Write($"move: SetWindowPos failed ({error})");
                if (error == ErrorAccessDenied)
                {
                    Toast?.Invoke(new Toast("error", $"{AppName(state, handle, hwnd)} runs as administrator, so it can't be moved from the deck"));
                    return false;
                }
            }
        }
        var after = WindowSource.Bounds(hwnd);
        Log.Write($"move: \"{WindowSource.WindowText(hwnd)}\" to {m.Name} at {x},{y}, now {after.X},{after.Y} {after.Width}x{after.Height}");
        lock (_gate) _loadedAt = DateTime.MinValue;
        return true;
    }

    /// <summary>
    /// Maximizes a window on a monitor: the deck's desktop map, a window let go on that
    /// monitor's Full screen. On another monitor, or minimized, it's restored and moved into
    /// the middle of this one first (Windows maximizes a window where it is).
    /// </summary>
    public void Maximize(long handle, int monitor)
    {
        var state = Current;
        if (monitor < 0 || monitor >= state.Monitors.Count || !WindowSource.Exists(handle)) return;
        var m = state.Monitors[monitor];
        var hwnd = (IntPtr)handle;
        var bounds = WindowSource.Bounds(hwnd);
        var here = !IsIconic(hwnd) && m.Work.Contains(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        if (!here)
        {
            if (IsZoomed(hwnd) || IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
            var size = WindowSource.Bounds(hwnd);
            if (!Move(handle, monitor, (m.Work.Width - size.Width) / 2, (m.Work.Height - size.Height) / 2)) return;
        }
        ShowWindow(hwnd, SwMaximize);
        Log.Write($"maximize: \"{WindowSource.WindowText(hwnd)}\" on {m.Name}");
        lock (_gate) _loadedAt = DateTime.MinValue;
    }

    static string AppName(ZoneState state, long handle, IntPtr hwnd) =>
        handle == 0 && state.App.Length > 0 ? state.App : WindowSource.WindowText(hwnd) is { Length: > 0 } title ? title : "That window";

    /// <summary>
    /// Applies a layout (a built-in type or a custom layout's uuid) to a monitor in
    /// FancyZones' applied-layouts.json and tells FancyZones to reload it. Choosing the
    /// built-in layout that's already applied steps its zone count.
    /// </summary>
    public void SetLayout(int monitor, string id)
    {
        var state = Current;
        if (monitor < 0 || monitor >= state.Monitors.Count) return;
        var m = state.Monitors[monitor];
        JsonNode? custom = null;
        var count = 0;
        if (Counts.TryGetValue(id, out var counts))
        {
            count = m.Layout.Type == id ? counts[(Array.IndexOf(counts, m.Layout.Count) + 1) % counts.Length] : counts[0];
        }
        else
        {
            lock (_gate) custom = _custom.FirstOrDefault(c => string.Equals((string?)c?["uuid"], id, StringComparison.OrdinalIgnoreCase));
            if (custom == null) return;
            // FancyZones only accepts a custom layout whose zone count matches its own.
            count = FancyZonesLayouts.Zones(new ZoneLayout("custom", 0, false, 0, custom), m.Work.Width, m.Work.Height).Count;
        }
        try
        {
            var root = (File.Exists(AppliedPath) ? JsonNode.Parse(File.ReadAllText(AppliedPath)) as JsonObject : null) ?? new JsonObject();
            if (root["applied-layouts"] is not JsonArray list) root["applied-layouts"] = list = [];
            var desktop = CurrentDesktop();
            var entry = Best(list, m, desktop, sameDesktopOnly: true);
            if (entry == null)
            {
                var serial = list.FirstOrDefault(e => (string?)e?["device"]?["monitor"] == m.Id)?["device"]?["serial-number"]?.GetValue<string>() ?? "";
                entry = new JsonObject
                {
                    ["device"] = new JsonObject
                    {
                        ["monitor"] = m.Id, ["monitor-instance"] = m.Instance, ["serial-number"] = serial,
                        ["monitor-number"] = m.Number, ["virtual-desktop"] = desktop,
                    },
                };
                list.Add(entry);
            }
            var old = entry["applied-layout"];
            var info = custom?["info"];
            entry["applied-layout"] = new JsonObject
            {
                ["uuid"] = custom != null ? id : BlankUuid,
                ["type"] = custom != null ? "custom" : id,
                ["show-spacing"] = (bool?)info?["show-spacing"] ?? (bool?)old?["show-spacing"] ?? true,
                ["spacing"] = (int?)info?["spacing"] ?? (int?)old?["spacing"] ?? 16,
                ["zone-count"] = count,
                ["sensitivity-radius"] = (int?)info?["sensitivity-radius"] ?? (int?)old?["sensitivity-radius"] ?? 20,
            };
            Directory.CreateDirectory(Folder);
            // Written whole and swapped in: FancyZones resets to defaults if it reads a half-written file.
            var temp = AppliedPath + ".flex-screen.tmp";
            File.WriteAllText(temp, root.ToJsonString());
            File.Move(temp, AppliedPath, overwrite: true);
            NotifyFancyZones();
            Log.Write($"zones: {m.Name} → {(string?)custom?["name"] ?? id} ({count})");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Write($"zones: can't change the layout: {e.Message}");
        }
        lock (_gate) _loadedAt = DateTime.MinValue;
    }

    /// <summary>FancyZones reloads applied-layouts.json on this message (it also watches the file's write time, which a rename doesn't touch).</summary>
    static void NotifyFancyZones()
    {
        var window = FindWindow("SuperFancyZones", null);
        if (window == IntPtr.Zero) return;  // FancyZones isn't running; it reads the file when it starts
        PostMessage(window, RegisterWindowMessage("{2ef2c8a7-e0d5-4f31-9ede-52aade2d284d}"), IntPtr.Zero, IntPtr.Zero);
    }

    List<DeckMonitor> LoadMonitors()
    {
        JsonArray applied = [], custom = [];
        try
        {
            if (File.Exists(AppliedPath)) applied = JsonNode.Parse(File.ReadAllText(AppliedPath))?["applied-layouts"] as JsonArray ?? [];
            if (File.Exists(CustomPath)) custom = JsonNode.Parse(File.ReadAllText(CustomPath))?["custom-layouts"] as JsonArray ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            // Mid-write by FancyZones; the next reload gets it.
        }
        _custom = custom;
        _choices = Choices(custom);
        var desktop = CurrentDesktop();
        var monitors = new List<DeckMonitor>();
        foreach (var (hmon, info) in EnumerateMonitors())
        {
            var (id, instance, number) = DeviceIds(info.Device);
            var work = Rectangle.FromLTRB(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
            var m = new DeckMonitor(id, instance, number, FriendlyName(id, instance), (info.Flags & MonitorInfoFPrimary) != 0,
                                    work, Default, []);
            var layout = LayoutFor(Best(applied, m, desktop, sameDesktopOnly: false), custom);
            var zones = FancyZonesLayouts.Zones(layout, work.Width, work.Height).Select(r => new DeckZone(r, "")).ToList();
            monitors.Add(m with { Layout = layout, Zones = zones });
        }
        return monitors.OrderBy(m => m.Work.Left).Take(MaxMonitors).ToList();
    }

    /// <summary>The applied-layouts entry for a monitor: same id, preferably the same instance and virtual desktop.</summary>
    static JsonNode? Best(JsonArray list, DeckMonitor m, string desktop, bool sameDesktopOnly)
    {
        JsonNode? best = null;
        var bestScore = -1;
        foreach (var entry in list)
        {
            var device = entry?["device"];
            if ((string?)device?["monitor"] != m.Id) continue;
            var sameDesktop = string.Equals((string?)device?["virtual-desktop"], desktop, StringComparison.OrdinalIgnoreCase);
            if (sameDesktopOnly && !sameDesktop) continue;
            var score = ((string?)device?["monitor-instance"] == m.Instance ? 2 : 0) + (sameDesktop ? 1 : 0);
            if (score > bestScore) (best, bestScore) = (entry, score);
        }
        return best;
    }

    static ZoneLayout LayoutFor(JsonNode? entry, JsonArray custom)
    {
        var a = entry?["applied-layout"];
        if (a == null) return Default;
        var type = (string?)a["type"] ?? Default.Type;
        var layout = new ZoneLayout(type, (int?)a["zone-count"] ?? 0, (bool?)a["show-spacing"] ?? true, (int?)a["spacing"] ?? 16);
        var uuid = (string?)a["uuid"] ?? BlankUuid;
        if (type == "custom" || uuid != BlankUuid)
        {
            var c = custom.FirstOrDefault(l => string.Equals((string?)l?["uuid"], uuid, StringComparison.OrdinalIgnoreCase));
            if (c != null) layout = layout with { Type = "custom", Custom = c };
        }
        return layout;
    }

    static string CurrentDesktop()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
        if (key?.GetValue("CurrentVirtualDesktop") is byte[] { Length: 16 } bytes) return new Guid(bytes).ToString("B").ToUpperInvariant();
        try
        {
            var last = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "last-used-virtual-desktop.json")));
            return (string?)last?["last-used-virtual-desktop"] ?? "";
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return "";
        }
    }

    /// <summary>FancyZones' ids: "\\?\DISPLAY#DEL4240#4&amp;3b23e44b&amp;0&amp;UID41031#{…}" gives "DEL4240" and the instance; "\\.\DISPLAY1" gives 1.</summary>
    static (string Id, string Instance, int Number) DeviceIds(string adapter)
    {
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevices(adapter, i, ref device, EddGetDeviceInterfaceName); i++)
        {
            if ((device.StateFlags & DisplayDeviceActive) == 0 || (device.StateFlags & DisplayDeviceMirroringDriver) != 0) continue;
            var parts = device.DeviceId.Split('#');
            var number = int.TryParse(new string(adapter.Where(char.IsDigit).ToArray()), out var n) ? n : 0;
            if (parts.Length >= 3) return (parts[1], parts[2], number);
        }
        return (adapter, "", 0);
    }

    /// <summary>The monitor's own name from its EDID ("DELL P2722H"), else its id.</summary>
    static string FriendlyName(string id, string instance)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{id}\{instance}\Device Parameters");
            if (key?.GetValue("EDID") is byte[] edid && edid.Length >= 128)
            {
                for (var d = 54; d <= 108; d += 18)  // the four 18-byte descriptors
                {
                    if (edid[d] == 0 && edid[d + 1] == 0 && edid[d + 3] == 0xFC)
                    {
                        var name = System.Text.Encoding.ASCII.GetString(edid, d + 5, 13).Split('\n')[0].Trim();
                        if (name.Length > 0) return name;
                    }
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException)
        {
            // Fall back to the id.
        }
        return id;
    }

    static List<(IntPtr, MonitorInfoEx)> EnumerateMonitors()
    {
        var list = new List<(IntPtr, MonitorInfoEx)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hmon, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(hmon, ref info)) list.Add((hmon, info));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static bool Contains(DeckMonitor m, Rectangle window)
    {
        var center = new Point(window.X + window.Width / 2, window.Y + window.Height / 2);
        return m.Work.Contains(center);
    }

    static double Overlap(Rectangle a, Rectangle b)
    {
        var i = Rectangle.Intersect(a, b);
        if (i.IsEmpty) return 0;
        double inter = (long)i.Width * i.Height;
        return inter / ((long)a.Width * a.Height + (long)b.Width * b.Height - inter);
    }

    const int SwRestore = 9;
    const int SwMaximize = 3;
    const int ErrorAccessDenied = 5;
    const uint SwpNoZOrder = 0x0004;
    const uint SwpNoActivate = 0x0010;
    const uint SwpNoOwnerZOrder = 0x0200;
    const uint MonitorInfoFPrimary = 1;
    const uint EddGetDeviceInterfaceName = 1;
    const int DisplayDeviceActive = 1;
    const int DisplayDeviceMirroringDriver = 8;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MonitorInfoEx
    {
        public int Size;
        public WindowSource.Rect Monitor;
        public WindowSource.Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice info, uint flags);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out WindowSource.Rect rect);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
