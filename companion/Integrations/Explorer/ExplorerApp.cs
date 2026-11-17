using System.Diagnostics;
using static FlexScreen.Companion.BarItem;

namespace FlexScreen.Companion;

/// <summary>File Explorer: its keys, and its open folder in a terminal, VS Code or Claude, or its path copied.</summary>
sealed class ExplorerApp(AppServices services) : IAppIntegration
{
    // No color of its own: the bar takes the icon's.
    static readonly AppBank Own = new("", null,
    [
        K("back", "Back", "back", "Alt+Left"), K("forward", "Forward", "forward", "Alt+Right"), K("up", "Up", "up", "Alt+Up"),
        K("address", "Address bar", "link", "Alt+D"), K("search", "Search", "search", "Ctrl+E"),
        K("new-folder", "New folder", "folder-plus", "Ctrl+Shift+N"), K("rename", "Rename", "cursor", "F2"),
        K("copy-path", "Copy path", "copy", "Ctrl+Shift+C"), K("properties", "Properties", "list", "Alt+Enter"),
        K("new-tab", "New tab", "plus", "Ctrl+T"), K("close-tab", "Close tab", "close", "Ctrl+W"),
        K("preview", "Preview pane", "eye", "Alt+P"), K("details", "Details view", "line", "Ctrl+Shift+6"),
        K("icons", "Large icons", "image", "Ctrl+Shift+2"), K("refresh", "Refresh", "reload", "F5"),
        K("delete", "Delete", "trash", "Delete"),
        F("terminal-here", "Terminal here", "terminal", "terminal"), F("vscode-here", "VS Code here", "code", "vscode"),
        F("claude-here", "Claude here", "chat", "claude"), F("copy-folder", "Copy folder", "copy", "copy"),
    ], ["up", "new-folder", "rename", "copy-path", "terminal-here", "vscode-here"]);

    public IReadOnlyList<string> Exes { get; } = ["explorer"];

    public AppBank? Bank(string context) => context.Length == 0 ? Own : null;

    public bool Run(BarItem key, AppWindow window)
    {
        if (key.Kind != "folder") return false;
        OpenFolder(window, key);
        return true;
    }

    /// <summary>The window's open folder: a terminal, VS Code or Claude there, or its path copied.</summary>
    void OpenFolder(AppWindow window, BarItem key)
    {
        var folder = ShellApps.OnSta(() => ShellApps.ExplorerFolder(window.Hwnd, window.Title));
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            services.Toast(new Toast("error", "This Explorer window isn't showing a folder"));
            return;
        }
        try
        {
            switch (key.Target)
            {
                case "terminal":
                    Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{folder}\"") { UseShellExecute = true });
                    break;
                case "claude":
                    Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{folder}\" claude") { UseShellExecute = true });
                    break;
                case "vscode":
                    Process.Start(new ProcessStartInfo("cmd.exe", $"/c code \"{folder}\"") { UseShellExecute = false, CreateNoWindow = true });
                    break;
                case "copy":
                    ShellApps.OnSta(() =>
                    {
                        System.Windows.Forms.Clipboard.SetText(folder);
                        return true;
                    });
                    services.Toast(new Toast("ok", $"Copied {folder}"));
                    break;
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            Log.Write($"context: {key.Name} in {folder}: {e.Message}");
            services.Toast(new Toast("error", $"Couldn't open {key.Name.Replace(" here", "")} there"));
        }
    }
}
