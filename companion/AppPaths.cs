namespace FlexScreen.Companion;

/// <summary>Where the companion keeps its files.</summary>
static class AppPaths
{
    /// <summary>
    /// %LOCALAPPDATA%\flex-screen: settings, profiles, sign-ins, clipboard pins, recent
    /// actions. FLEX_SCREEN_DATA names another folder instead, so a companion started for
    /// testing (beside the simulator, say) leaves this PC's own files alone.
    /// </summary>
    public static readonly string Data = Environment.GetEnvironmentVariable("FLEX_SCREEN_DATA") is { Length: > 0 } folder
        ? Path.GetFullPath(folder)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "flex-screen");

    /// <summary>One of them in the user's editor (Notepad when nothing is set for .json), for the tray's Open … items.</summary>
    public static void Open(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            System.Diagnostics.Process.Start("notepad.exe", $"\"{path}\"");
        }
    }
}
