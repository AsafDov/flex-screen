using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace FlexScreen.Companion;

/// <summary>
/// The taskbar's app buttons, left to right (the main taskbar's, then other monitors'), as
/// UI Automation names them ("Appid: …"), and which button a window belongs to, so the deck's
/// window tiles come in the taskbar's order. A button's id is the app's AppUserModelID (set on
/// the window, or the packaged app's), its exe path under a known folder
/// ("{6D809377-…}\Prusa3D\…\prusa-slicer.exe"), or "*PID…" for a process with neither.
/// </summary>
sealed class TaskbarOrder
{
    static readonly TimeSpan Every = TimeSpan.FromSeconds(2);

    List<string> _ids = [];
    DateTime _readAt = DateTime.MinValue;
    bool _failed;
    readonly Dictionary<long, string> _windowIds = new();
    readonly Dictionary<uint, string> _packagedIds = new();
    readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Re-reads the taskbar at most every 2 s, or now when <paramref name="force"/>.</summary>
    public void Refresh(bool force)
    {
        if (!force && DateTime.UtcNow - _readAt < Every) return;
        _readAt = DateTime.UtcNow;
        try
        {
            _ids = Read();
            if (_failed) Log.Write("windows: reading the taskbar works again");
            _failed = false;
        }
        catch (Exception e)
        {
            // Explorer restarting, or busy: keep the order last read.
            if (!_failed) Log.Write($"windows: can't read the taskbar's order: {e.GetType().Name}: {e.Message}");
            _failed = true;
        }
        if (_windowIds.Count > 500) _windowIds.Clear();
        if (_packagedIds.Count > 500) _packagedIds.Clear();
    }

    /// <summary>The position of the window's button on the taskbar; int.MaxValue when none matches.</summary>
    public int IndexOf(IntPtr hwnd, uint pid, string exePath)
    {
        var weak = int.MaxValue;
        for (var i = 0; i < _ids.Count; i++)
        {
            var match = Match(_ids[i], hwnd, pid, exePath);
            if (match == 2) return i;
            if (match == 1 && weak == int.MaxValue) weak = i;
        }
        return weak;
    }

    /// <summary>
    /// Whether the window belongs to the app with this id (a taskbar button's, or a Start menu
    /// app's): 2 for sure (the window's or its package's id, or its exe), 1 probably (the exe's
    /// name is in the id), 0 not.
    /// </summary>
    public int Match(string id, IntPtr hwnd, uint pid, string exePath)
    {
        if (id.Length == 0) return 0;
        if (Same(id, WindowAppId(hwnd)) || Same(id, PackagedAppId(pid)) || Same(KnownFolderPath(id), exePath) || Same(id, exePath) || id == $"*PID{pid:x8}") return 2;
        // Apps that set their id on the process ("md.obsidian", "Microsoft.Windows.Explorer"):
        // the exe's name in it is the best there is.
        var exe = Path.GetFileNameWithoutExtension(exePath);
        return exe.Length >= 3 && id.Contains(exe, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    static bool Same(string a, string b) => a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static List<string> Read()
    {
        var cache = new CacheRequest();
        cache.Add(AutomationElement.AutomationIdProperty);
        cache.Add(AutomationElement.ClassNameProperty);
        var ids = new List<string>();
        using (cache.Activate())
        {
            var trays = AutomationElement.RootElement.FindAll(TreeScope.Children, new OrCondition(
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"),
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_SecondaryTrayWnd")));
            var buttons = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
            foreach (var tray in trays.Cast<AutomationElement>().OrderBy(t => t.Cached.ClassName == "Shell_TrayWnd" ? 0 : 1))
            {
                foreach (AutomationElement button in tray.FindAll(TreeScope.Descendants, buttons))
                {
                    var id = button.Cached.AutomationId;
                    if (!id.StartsWith("Appid: ", StringComparison.Ordinal)) continue;  // Start, Task View, tray icons
                    id = id["Appid: ".Length..];
                    if (!ids.Contains(id)) ids.Add(id);
                }
            }
        }
        return ids;
    }

    /// <summary>"{GUID}\rest\app.exe" → the known folder's path plus the rest; "" for other ids.</summary>
    string KnownFolderPath(string id)
    {
        if (id.Length < 39 || id[0] != '{' || id[37] != '}') return "";
        if (_paths.TryGetValue(id, out var known)) return known;
        var path = "";
        if (Guid.TryParse(id[..38], out var folder) && SHGetKnownFolderPath(folder, 0, IntPtr.Zero, out var p) == 0)
        {
            path = Path.Combine(Marshal.PtrToStringUni(p) ?? "", id[38..].TrimStart('\\'));
            Marshal.FreeCoTaskMem(p);
        }
        _paths[id] = path;
        return path;
    }

    /// <summary>The window's own AppUserModelID (Chrome sets one per profile), or "".</summary>
    string WindowAppId(IntPtr hwnd)
    {
        if (_windowIds.TryGetValue((long)hwnd, out var known)) return known;
        var id = "";
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) == 0 && store != null)
        {
            try
            {
                var key = AppUserModelIdKey;
                if (store.GetValue(ref key, out var value) == 0)
                {
                    if (value.Type == VtLpwstr) id = Marshal.PtrToStringUni(value.Pointer) ?? "";
                    PropVariantClear(ref value);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        _windowIds[(long)hwnd] = id;
        return id;
    }

    /// <summary>A Store app's id ("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"), or "".</summary>
    string PackagedAppId(uint pid)
    {
        if (_packagedIds.TryGetValue(pid, out var known)) return known;
        var id = "";
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process != IntPtr.Zero)
        {
            try
            {
                var length = 512;
                var buffer = new StringBuilder(length);
                if (GetApplicationUserModelId(process, ref length, buffer) == 0) id = buffer.ToString();
            }
            finally
            {
                CloseHandle(process);
            }
        }
        _packagedIds[pid] = id;
        return id;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
        [FieldOffset(16)] public IntPtr Rest;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    static readonly PropertyKey AppUserModelIdKey = new() { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
    const ushort VtLpwstr = 31;
    const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid folder, uint flags, IntPtr token, out IntPtr path);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetApplicationUserModelId(IntPtr process, ref int length, StringBuilder id);
}
