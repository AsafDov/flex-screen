using static FlexScreen.Companion.BarItem;

namespace FlexScreen.Companion;

/// <summary>
/// A web browser (Chrome, Edge): back, tabs, history, zoom…, and on a site with keys of its
/// own (<see cref="WebSites"/>: Gmail, YouTube, Google Calendar, Jira, GitHub, and the
/// profile's own sites) that site's keys, in its color, with the browser's after them. The site
/// is told by the tab's address when the flex-screen extension reports it
/// (<see cref="BrowserTabs"/>), by the installed web app a window is (its name, from its
/// AppUserModelID), else by the tab's title.
/// </summary>
abstract class BrowserApp(string exe, int color) : IAppIntegration
{
    static readonly BarItem[] Items =
    [
        K("back", "Back", "back", "Alt+Left"), K("forward", "Forward", "forward", "Alt+Right"), K("reload", "Reload", "reload", "F5"),
        K("prev-tab", "Prev tab", "prev", "Ctrl+Shift+Tab"), K("next-tab", "Next tab", "next", "Ctrl+Tab"),
        K("new-tab", "New tab", "plus", "Ctrl+T"), K("reopen", "Reopen", "history", "Ctrl+Shift+T"),
        K("close-tab", "Close tab", "close", "Ctrl+W"), K("find", "Find", "search", "Ctrl+F"),
        K("address", "Address bar", "link", "Ctrl+L"), K("bookmark", "Bookmark", "bookmark", "Ctrl+D"),
        K("history", "History", "list", "Ctrl+H"), K("downloads", "Downloads", "download", "Ctrl+J"),
        K("new-window", "New window", "window", "Ctrl+N"), K("private", "Private", "eye", "Ctrl+Shift+N"),
        K("zoom-in", "Zoom in", "zoom-in", "Ctrl+plus"), K("zoom-out", "Zoom out", "zoom-out", "Ctrl+minus"),
        K("fullscreen", "Full screen", "expand", "F11"), K("devtools", "DevTools", "code", "F12"),
    ];

    static readonly string[] Default = ["back", "reload", "prev-tab", "next-tab", "new-tab", "reopen", "find"];

    readonly AppBank _own = new("", color, Items, Default);

    public IReadOnlyList<string> Exes { get; } = [exe];

    /// <summary>
    /// The site's id ("gmail"), or "". An installed web app is named on the deck by its own
    /// name (WindowSource), so a window whose app isn't the browser's is one.
    /// </summary>
    public string Context(AppWindow window)
    {
        var webApp = window.App is "Chrome" or "Edge" ? "" : window.App;
        return WebSites.Of(window.Title, BrowserTabs.UrlFor(exe, window.Title), webApp)?.Id ?? "";
    }

    public AppBank? Bank(string context) =>
        context.Length == 0 ? _own : WebSites.Find(context)?.Bank.Over(_own);
}

sealed class ChromeApp() : BrowserApp("chrome", 0x34A853);

sealed class EdgeApp() : BrowserApp("msedge", 0x2B9FD9);
