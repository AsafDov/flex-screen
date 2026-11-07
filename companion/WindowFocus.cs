using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FlexScreen.Companion;

/// <summary>Finds top-level windows and brings one to the front from the background.</summary>
static class WindowFocus
{
    /// <summary>
    /// Brings forward the first visible window of one of these processes whose title
    /// contains <paramref name="titlePart"/> (any title if it's empty). Returns false if none.
    /// </summary>
    public static bool Focus(string titlePart, params string[] processNames)
    {
        var hwnd = Find(titlePart, processNames);
        if (hwnd == IntPtr.Zero) return false;
        Activate(hwnd);
        return true;
    }

    static IntPtr Find(string titlePart, string[] processNames)
    {
        var pids = new HashSet<uint>(processNames.SelectMany(n => Process.GetProcessesByName(n)).Select(p => (uint)p.Id));
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains(pid)) return true;
            var title = new StringBuilder(512);
            GetWindowText(hwnd, title, title.Capacity);
            if (title.Length == 0) return true;
            if (titlePart.Length > 0 && title.ToString().IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) < 0) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Windows only lets the app that had the last input hand the focus to another window.
    /// A mouse move of zero pixels counts as input; tapping Alt did too, but the window
    /// receiving the focus took the Alt as "open the menu". If Windows still refuses,
    /// sharing the foreground thread's input state does it.
    /// </summary>
    public static void Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
        var nudge = new Input { Type = InputMouse, Mouse = new MouseInput { Flags = MouseeventfMove } };
        SendInput(1, [nudge], Marshal.SizeOf<Input>());
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return;
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var thisThread = GetCurrentThreadId();
        AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            AttachThreadInput(thisThread, foregroundThread, false);
        }
    }

    const int SwRestore = 9;
    const uint InputMouse = 0;
    const uint MouseeventfMove = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Input
    {
        public uint Type;
        public MouseInput Mouse;  // the largest member of the native union
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
