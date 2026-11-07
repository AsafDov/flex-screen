using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace FlexScreen.Companion;

/// <summary>
/// Which app a local HTTP request came from: the process at the client end of the TCP
/// connection (curl, for a Claude Code hook), then up its parents to the first one with a
/// window (the Claude app, VS Code, a terminal). Claude's hooks don't say where they run.
/// </summary>
static class ProcessTree
{
    /// <summary>The process with a visible window that started the client of this connection, or 0.</summary>
    public static int WindowOwnerOf(IPEndPoint client)
    {
        var pid = ClientProcess(client);
        if (pid == 0) return 0;
        var parents = Parents();
        for (var depth = 0; depth < 16 && pid > 4; depth++)
        {
            if (Windows(pid).Count > 0) return pid;
            if (!parents.TryGetValue(pid, out pid)) break;
        }
        return 0;
    }

    /// <summary>Brings forward that process's window, preferring one whose title mentions <paramref name="title"/>.</summary>
    public static bool Activate(int pid, string title)
    {
        var windows = Windows(pid);
        if (windows.Count == 0) return false;
        var best = windows.FirstOrDefault(w => title.Length > 0 && w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        WindowFocus.Activate(best.Handle != IntPtr.Zero ? best.Handle : windows[0].Handle);
        return true;
    }

    static List<(IntPtr Handle, string Title)> Windows(int pid)
    {
        var list = new List<(IntPtr, string)>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != pid || !IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero) return true;
            var text = new StringBuilder(256);
            if (GetWindowText(hwnd, text, text.Capacity) > 0) list.Add((hwnd, text.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The owner of the TCP connection whose local end is this port: the requester, seen from the server.</summary>
    static int ClientProcess(IPEndPoint client)
    {
        var v6 = client.AddressFamily == AddressFamily.InterNetworkV6;
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, v6 ? AfInet6 : AfInet, TcpTableOwnerPidAll, 0);
        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, v6 ? AfInet6 : AfInet, TcpTableOwnerPidAll, 0) != 0) return 0;
            var rows = Marshal.ReadInt32(table);
            var rowSize = v6 ? 56 : 24;  // MIB_TCP6ROW_OWNER_PID / MIB_TCPROW_OWNER_PID
            var portAt = v6 ? 20 : 4;    // dwLocalPort
            var pidAt = v6 ? 52 : 20;    // dwOwningPid
            for (var i = 0; i < rows; i++)
            {
                var row = table + 4 + i * rowSize;
                var port = Marshal.ReadInt32(row + portAt);
                var local = ((port & 0xFF) << 8) | ((port >> 8) & 0xFF);  // network byte order
                if (local == client.Port) return Marshal.ReadInt32(row + pidAt);
            }
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    static Dictionary<int, int> Parents()
    {
        var map = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == new IntPtr(-1)) return map;
        try
        {
            var entry = new ProcessEntry { Size = Marshal.SizeOf<ProcessEntry>() };
            for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
            {
                map[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return map;
    }

    const int AfInet = 2;
    const int AfInet6 = 23;
    const int TcpTableOwnerPidAll = 5;
    const uint Th32csSnapProcess = 0x2;
    const uint GwOwner = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public int Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
}
