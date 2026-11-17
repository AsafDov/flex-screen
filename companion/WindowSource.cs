using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FlexScreen.Companion;

/// <param name="Id">The window handle.</param>
/// <param name="Bounds">What's visible on screen (DWM frame bounds), in physical pixels.</param>
record DeckWindow(long Id, string App, string Title, string Icon, bool Active, bool Minimized, Rectangle Bounds);

/// <summary>An app icon for the deck: 32×32 RGB565 (little-endian) followed by an 8-bit alpha plane.</summary>
record WindowIcon(string Id, byte[] Pixels);

/// <summary>
/// The open app windows, the way Alt+Tab sees them: visible, not cloaked (other virtual
/// desktops, suspended Store apps), not tool windows or owned popups.
/// </summary>
sealed class WindowSource
{
    public const int MaxShown = 8;  // the deck's grid, next to Show desktop
    public const int IconSize = 32;
    const int PollMs = 300;
    // Titles change all the time (terminal spinners, players, page loads); a title-only
    // change goes out at most this often, anything else at once.
    static readonly TimeSpan TitleEvery = TimeSpan.FromSeconds(2);

    readonly System.Threading.Timer _timer;
    readonly object _gate = new();
    readonly Dictionary<long, long> _firstSeen = new();  // hwnd → order it first appeared
    long _seen;
    // hwnd → when it was last in front (polls × 1000). Picking the shown windows by the
    // screen's front-to-back order dropped a minimized window (Windows sends it to the back),
    // so tapping the window in front, which minimizes it, made its tile vanish.
    readonly Dictionary<long, long> _used = new();
    long _polls;
    // hwnd → when the deck asked it to close: left off the list meanwhile, so the tile goes at
    // once; back after a few seconds if it's still open (it asked about unsaved work, say).
    readonly Dictionary<long, DateTime> _closing = new();
    static readonly TimeSpan ClosingFor = TimeSpan.FromSeconds(5);
    readonly Dictionary<string, (string App, string[] Suffixes)> _apps = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, WindowIcon?> _iconCache = new();  // exe path + icon handle → icon
    readonly Dictionary<string, WindowIcon> _icons = new();       // icon id → icon
    readonly uint _ownPid = (uint)Environment.ProcessId;
    readonly TaskbarOrder _taskbar = new();
    string _shownLayout = "";
    string _shownTitles = "";
    DateTime _shownAt = DateTime.MinValue;

    /// <summary>Up to <see cref="MaxShown"/> of the most recently used windows, in the taskbar's order.</summary>
    public IReadOnlyList<DeckWindow> Shown { get; private set; } = [];

    /// <summary>Every app window as of the last poll, front to back.</summary>
    public IReadOnlyList<DeckWindow> All { get; private set; } = [];

    public static bool Exists(long id) => IsWindow((IntPtr)id);

    /// <summary>The exe behind a window ("" if it can't be read).</summary>
    public static string ExePath(long id) => ProcessPath((IntPtr)id);

    /// <summary>Raised on a timer thread when <see cref="Shown"/> changes.</summary>
    public event Action<IReadOnlyList<DeckWindow>>? Changed;

    /// <summary>Every poll: all app windows, front to back, and the foreground one (if it's an app window).</summary>
    public event Action<IReadOnlyList<DeckWindow>, DeckWindow?>? Polled;

    public WindowSource()
    {
        _timer = new System.Threading.Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(0, Timeout.Infinite);

    public WindowIcon? FindIcon(string id)
    {
        lock (_gate) return _icons.GetValueOrDefault(id);
    }

    /// <summary>An icon from elsewhere (a shortcut's), kept so the deck can ask for it like a window's.</summary>
    public WindowIcon AddIcon(Bitmap image)
    {
        var icon = Encode(image);
        lock (_gate) _icons[icon.Id] = icon;
        return icon;
    }

    /// <summary>
    /// The most recently used open window of a Start menu app (by its id: an AppUserModelID or
    /// an exe path), or failing that of an app the window list names <paramref name="name"/>; 0 if none.
    /// </summary>
    public long FindAppWindow(string appId, string name)
    {
        var all = All;
        lock (_gate)
        {
            var recent = all.OrderByDescending(w => _used.GetValueOrDefault(w.Id)).ToList();
            var best = (Score: 0, Id: 0L);
            foreach (var w in recent)
            {
                var hwnd = (IntPtr)w.Id;
                GetWindowThreadProcessId(hwnd, out var pid);
                var score = _taskbar.Match(appId, hwnd, pid, ProcessPath(hwnd));
                if (score == 0 && name.Length > 0 && string.Equals(w.App, name, StringComparison.OrdinalIgnoreCase)) score = 1;
                if (score > best.Score) best = (score, w.Id);
                if (score == 2) break;
            }
            return best.Id;
        }
    }

    /// <summary>Brings the window to the front, or minimizes it if it's there already (like its taskbar button).</summary>
    public static void Focus(long id)
    {
        var hwnd = (IntPtr)id;
        if (!IsWindow(hwnd)) return;
        if (hwnd == Foreground() && !IsIconic(hwnd)) ShowWindow(hwnd, SwMinimize);
        else WindowFocus.Activate(hwnd);
    }

    /// <summary>
    /// Asks the window to close, as its X button does (apps still get to ask about unsaved
    /// work), and takes its tile off the deck straight away.
    /// </summary>
    public void Close(long id)
    {
        var hwnd = (IntPtr)id;
        if (!IsWindow(hwnd)) return;
        PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        lock (_gate) _closing[id] = DateTime.UtcNow;  // the next poll (≤ 300 ms) drops the tile
    }

    /// <summary>The root window of whatever has the foreground.</summary>
    public static IntPtr Foreground() => GetAncestor(GetForegroundWindow(), GaRootOwner);

    void Poll()
    {
        try
        {
            var all = Enumerate();
            All = all;
            var foreground = all.FirstOrDefault(w => w.Active);
            List<DeckWindow> shown;
            lock (_gate)
            {
                var opened = false;
                foreach (var w in all) opened |= _firstSeen.TryAdd(w.Id, _seen++);
                var closed = _firstSeen.Keys.Where(id => all.All(w => w.Id != id)).ToList();
                foreach (var gone in closed)
                {
                    _firstSeen.Remove(gone);
                    _used.Remove(gone);
                    _closing.Remove(gone);
                }
                _polls++;
                for (var z = 0; z < all.Count; z++)
                {
                    // The one in front is the most recent; one seen for the first time ranks by
                    // its place front to back (at startup, and a new window comes up in front).
                    if (all[z].Active) _used[all[z].Id] = _polls * 1000;
                    else _used.TryAdd(all[z].Id, _polls * 1000 - 1 - z);
                }
                var utc = DateTime.UtcNow;
                foreach (var done in _closing.Where(c => utc - c.Value >= ClosingFor).Select(c => c.Key).ToList()) _closing.Remove(done);
                // The most recently used windows, minimized ones too, in the taskbar's order (a
                // window opened or closed reads it again), and in the order they opened within an
                // app's button.
                _taskbar.Refresh(force: opened || closed.Count > 0);
                shown = all.Where(w => !_closing.ContainsKey(w.Id))
                    .OrderByDescending(w => _used[w.Id])
                    .Take(MaxShown)
                    .OrderBy(w => TaskbarIndex(w))
                    .ThenBy(w => _firstSeen[w.Id])
                    .ToList();
            }
            Polled?.Invoke(all, foreground);
            var layout = string.Join("\n", shown.Select(w => $"{w.Id}|{w.App}|{w.Icon}|{w.Active}|{w.Minimized}"));
            var titles = string.Join("\n", shown.Select(w => w.Title));
            var now = DateTime.UtcNow;
            if (layout != _shownLayout || (titles != _shownTitles && now - _shownAt >= TitleEvery))
            {
                _shownLayout = layout;
                _shownTitles = titles;
                _shownAt = now;
                Shown = shown;
                Changed?.Invoke(shown);
            }
        }
        catch (Exception e)
        {
            Log.Write($"windows: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            _timer.Change(PollMs, Timeout.Infinite);
        }
    }

    int TaskbarIndex(DeckWindow w)
    {
        var hwnd = (IntPtr)w.Id;
        GetWindowThreadProcessId(hwnd, out var pid);
        return _taskbar.IndexOf(hwnd, pid, ProcessPath(hwnd));
    }

    List<DeckWindow> Enumerate()
    {
        var foreground = Foreground();
        var list = new List<DeckWindow>();
        EnumWindows((hwnd, _) =>
        {
            if (IsAppWindow(hwnd)) list.Add(Describe(hwnd, hwnd == foreground));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    bool IsAppWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0) return false;
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        var ex = (long)GetWindowLongPtr(hwnd, GwlExStyle);
        if ((ex & WsExToolWindow) != 0) return false;
        if (GetWindow(hwnd, GwOwner) != IntPtr.Zero && (ex & WsExAppWindow) == 0) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _ownPid) return false;
        var cls = ClassName(hwnd);
        return cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    DeckWindow Describe(IntPtr hwnd, bool active)
    {
        var path = ProcessPath(hwnd);
        var (app, suffixes) = AppNames(path);
        // An installed web app (Chrome's or Edge's "Install as app") is a browser window with an
        // AppUserModelID of its own: named as the Start menu names it ("Gmail"), so the deck and
        // the context bar take it for that app, not for the browser.
        if (app is "Chrome" or "Edge" && WebAppName(_taskbar.WindowAppId(hwnd)) is { } webApp)
        {
            app = webApp;
            suffixes = [webApp, .. suffixes];
        }
        var title = CleanTitle(WindowText(hwnd), app, suffixes);
        var icon = IconFor(hwnd, path);
        return new DeckWindow((long)hwnd, app, title, icon?.Id ?? "", active, IsIconic(hwnd), Bounds(hwnd));
    }

    readonly Dictionary<string, string?> _webApps = new(StringComparer.OrdinalIgnoreCase);  // AppUserModelID → its name in the Start menu
    DateTime _webAppsReadAt = DateTime.MinValue;

    /// <summary>
    /// An installed web app's name, by its window's AppUserModelID ("Chrome._crx_…"), from the
    /// Start menu's list (read again at most every minute, for one installed since); null for a
    /// browser's own window or one the Start menu doesn't list.
    /// </summary>
    string? WebAppName(string appId)
    {
        if (!appId.Contains("_crx_", StringComparison.OrdinalIgnoreCase)) return null;
        lock (_gate)
        {
            if (_webApps.TryGetValue(appId, out var known)) return known;
        }
        if (DateTime.UtcNow - _webAppsReadAt < TimeSpan.FromMinutes(1)) return null;
        _webAppsReadAt = DateTime.UtcNow;
        List<ShellApps.App> apps;
        try
        {
            apps = ShellApps.OnSta(ShellApps.List);
        }
        catch (InvalidOperationException e)
        {
            Log.Write($"windows: the Start menu's apps can't be read: {e.Message}");
            return null;
        }
        lock (_gate)
        {
            foreach (var a in apps.Where(a => a.Id.Contains("_crx_", StringComparison.OrdinalIgnoreCase))) _webApps[a.Id] = a.Name;
            if (!_webApps.ContainsKey(appId))
            {
                // A profile's web app can carry more after the app's own id ("….UserData.Profile_1").
                // Not one found: asked again after the minute, once it may be installed.
                var match = apps.FirstOrDefault(a => appId.StartsWith(a.Id, StringComparison.OrdinalIgnoreCase) || a.Id.StartsWith(appId, StringComparison.OrdinalIgnoreCase));
                if (match == null) return null;
                _webApps[appId] = match.Name;
            }
            return _webApps[appId];
        }
    }

    public static Rectangle Bounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out Rect r, Marshal.SizeOf<Rect>()) != 0) GetWindowRect(hwnd, out r);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>The exe behind the window. Store apps run inside ApplicationFrameHost; their own process owns a child window.</summary>
    static string ProcessPath(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        var path = PathOf(pid);
        if (!path.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) return path;
        var inner = path;
        EnumChildWindows(hwnd, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var childPid);
            if (childPid == pid) return true;
            inner = PathOf(childPid);
            return false;
        }, IntPtr.Zero);
        return inner;
    }

    static string PathOf(uint pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return "";
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : "";
        }
        finally
        {
            CloseHandle(process);
        }
    }

    static readonly Dictionary<string, string> ShortNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Code"] = "VS Code", ["chrome"] = "Chrome", ["msedge"] = "Edge", ["firefox"] = "Firefox",
        ["WindowsTerminal"] = "Terminal", ["explorer"] = "Explorer", ["Spotify"] = "Spotify",
        ["ms-teams"] = "Teams", ["Teams"] = "Teams", ["Discord"] = "Discord", ["Obsidian"] = "Obsidian",
        ["OUTLOOK"] = "Outlook", ["olk"] = "Outlook", ["claude"] = "Claude", ["devenv"] = "Visual Studio",
        ["notepad"] = "Notepad", ["SystemSettings"] = "Settings", ["Taskmgr"] = "Task Manager",
        ["PowerToys.Settings"] = "PowerToys", ["slack"] = "Slack", ["WINWORD"] = "Word", ["EXCEL"] = "Excel",
        ["POWERPNT"] = "PowerPoint", ["WhatsApp"] = "WhatsApp", ["Telegram"] = "Telegram",
    };

    /// <summary>A short app name, plus the names apps append to their window titles (" - Google Chrome").</summary>
    (string App, string[] Suffixes) AppNames(string path)
    {
        lock (_gate)
        {
            if (_apps.TryGetValue(path, out var known)) return known;
        }
        var exe = Path.GetFileNameWithoutExtension(path);
        string description = "", product = "";
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            description = info.FileDescription?.Trim() ?? "";
            product = info.ProductName?.Trim() ?? "";
        }
        catch (Exception e) when (e is FileNotFoundException or ArgumentException)
        {
            // No version info; the exe name will do.
        }
        var app = ShortNames.GetValueOrDefault(exe) ?? (description.Length is > 0 and <= 24 ? description : exe);
        if (app.Length == 0) app = "Window";
        var names = new[] { description, product, app, "Visual Studio Code", "Google Chrome", "Microsoft Edge", "Mozilla Firefox" }
            .Where(n => n.Length > 0).Distinct().ToArray();
        lock (_gate) _apps[path] = (app, names);
        return (app, names);
    }

    /// <summary>"page_dev.cpp - flex-screen - Visual Studio Code" → "page_dev.cpp - flex-screen".</summary>
    static string CleanTitle(string title, string app, string[] suffixes)
    {
        title = title.Replace("​", "").Trim();  // Edge writes "Microsoft​ Edge"
        foreach (var name in suffixes)
        {
            foreach (var sep in new[] { " - ", " — ", " – " })
            {
                var at = title.LastIndexOf(sep + name, StringComparison.OrdinalIgnoreCase);
                if (at > 0) return title[..at].Trim();
            }
        }
        return string.Equals(title, app, StringComparison.OrdinalIgnoreCase) ? "" : title;
    }

    WindowIcon? IconFor(IntPtr hwnd, string path)
    {
        SendMessageTimeout(hwnd, WmGetIcon, (IntPtr)IconBig, IntPtr.Zero, SmtoAbortIfHung, 100, out var handle);
        if (handle == IntPtr.Zero) handle = GetClassLongPtr(hwnd, GclpHIcon);
        var key = $"{path}|{handle}";
        lock (_gate)
        {
            if (_iconCache.TryGetValue(key, out var cached)) return cached;
        }
        WindowIcon? icon = null;
        try
        {
            using var source = handle != IntPtr.Zero ? (Icon)System.Drawing.Icon.FromHandle(handle).Clone()
                             : path.Length > 0 ? System.Drawing.Icon.ExtractAssociatedIcon(path) : null;
            if (source != null) icon = Encode(source);
        }
        catch (Exception e) when (e is ArgumentException or ExternalException or FileNotFoundException)
        {
            // Some windows hand out icons GDI+ can't read; the deck shows initials instead.
        }
        lock (_gate)
        {
            _iconCache[key] = icon;
            if (icon != null) _icons[icon.Id] = icon;
        }
        return icon;
    }

    /// <summary>LVGL's RGB565A8: the RGB565 plane, then the alpha plane. The id is a content hash.</summary>
    static WindowIcon Encode(Icon icon)
    {
        // Icon.ToBitmap keeps 32-bit icons' alpha, which drawing the icon straight into a bitmap loses.
        using var full = icon.ToBitmap();
        return Encode(full);
    }

    static WindowIcon Encode(Bitmap full)
    {
        using var small = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(full, new Rectangle(0, 0, IconSize, IconSize));
        }
        var data = small.LockBits(new Rectangle(0, 0, IconSize, IconSize), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var argb = new byte[IconSize * IconSize * 4];
        try
        {
            for (var y = 0; y < IconSize; y++) Marshal.Copy(data.Scan0 + y * data.Stride, argb, y * IconSize * 4, IconSize * 4);
        }
        finally
        {
            small.UnlockBits(data);
        }
        const int n = IconSize * IconSize;
        var pixels = new byte[n * 3];
        for (var i = 0; i < n; i++)
        {
            int b = argb[i * 4], g = argb[i * 4 + 1], r = argb[i * 4 + 2], a = argb[i * 4 + 3];
            var rgb565 = ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);
            pixels[i * 2] = (byte)rgb565;
            pixels[i * 2 + 1] = (byte)(rgb565 >> 8);
            pixels[n * 2 + i] = (byte)a;
        }
        var id = Convert.ToHexString(SHA1.HashData(pixels))[..12].ToLowerInvariant();
        return new WindowIcon(id, pixels);
    }

    public static string WindowText(IntPtr hwnd)
    {
        var text = new StringBuilder(512);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    static string ClassName(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString();
    }

    const int GwlExStyle = -20;
    const long WsExToolWindow = 0x80;
    const long WsExAppWindow = 0x40000;
    const uint GwOwner = 4;
    const uint GaRootOwner = 3;
    const int DwmwaExtendedFrameBounds = 9;
    const int DwmwaCloaked = 14;
    const uint WmClose = 0x0010;
    const int SwMinimize = 6;
    const uint WmGetIcon = 0x007F;
    const int IconBig = 1;
    const int GclpHIcon = -14;
    const uint SmtoAbortIfHung = 0x0002;
    const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
}
