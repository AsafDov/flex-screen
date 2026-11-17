using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FlexScreen.Companion;

/// <summary>
/// Zoom's meeting shortcuts (Alt+V, Alt+Y…), for what its toolbar can't press
/// (<see cref="ZoomControl"/>): sent to the meeting window, then the window that had the
/// focus gets it back.
/// </summary>
static class ZoomShortcuts
{
    /// <summary>Alt and <paramref name="key"/> in the meeting window; nothing without a meeting.</summary>
    public static void Send(char key)
    {
        var meeting = MeetingWindow();
        if (meeting == IntPtr.Zero) return;
        var before = GetForegroundWindow();
        WindowFocus.Activate(meeting);
        Thread.Sleep(120);
        KeyInput.Chord(KeyInput.Alt, key);
        Thread.Sleep(80);
        if (before != IntPtr.Zero && before != meeting) WindowFocus.Activate(before);
    }

    static IntPtr MeetingWindow()
    {
        var pids = Process.GetProcessesByName("Zoom").Select(p => (uint)p.Id).ToHashSet();
        IntPtr found = IntPtr.Zero, sharing = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains(pid) || !IsWindowVisible(hwnd)) return true;
            var cls = new StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            // The meeting window (Zoom 7's tabbed one, or the older one), not Zoom's home; while
            // sharing it's hidden and the floating meeting controls take the shortcuts.
            switch (cls.ToString())
            {
                case "ConfMultiTabContentWndClass" or "ZPContentViewWndClass":
                    found = hwnd;
                    return false;
                case "ZPFloatToolbarClass":
                    sharing = hwnd;
                    break;
            }
            return true;
        }, IntPtr.Zero);
        return found != IntPtr.Zero ? found : sharing;
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
}
