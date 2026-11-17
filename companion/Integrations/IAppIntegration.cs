namespace FlexScreen.Companion;

/// <summary>
/// One app the deck knows. Each lives in its own folder under Integrations/ and says:
/// <list type="bullet">
/// <item>Detection: which windows are the app's (<see cref="Exes"/>);</item>
/// <item>Context: what the window is doing that has keys of its own: Gmail in a browser,
/// Claude's tab in VS Code, a meeting in Teams or Zoom (<see cref="Context"/>);</item>
/// <item>Actions: its context bar keys and the ones on the bar by default (<see cref="Bank"/>),
/// what they show right now (<see cref="Live"/>) and how its own kinds of key run
/// (<see cref="Run"/>, <see cref="Choose"/>);</item>
/// <item>State: the deck topics its data goes out on, if any (<see cref="Topics"/>).</item>
/// </list>
/// <see cref="AppIntegrations"/> lists them; <see cref="ContextBar"/> asks the one for the
/// window in front.
/// </summary>
interface IAppIntegration
{
    /// <summary>
    /// The exe names of the app's windows, lower case without ".exe" ("code", "ms-teams"). They
    /// start the bar's key, which saved layouts (context-bar.json) go by: keep them.
    /// </summary>
    IReadOnlyList<string> Exes { get; }

    /// <summary>
    /// What the window is doing that has keys of its own: a site ("gmail"), Claude's tab
    /// ("claude"), a meeting ("call"); "" for nothing in particular. The bar's key is then
    /// "exe:context" ("chrome:gmail"), so these stay as they are too.
    /// </summary>
    string Context(AppWindow window) => "";

    /// <summary>The keys in a context ("" for the app's own); null for a context it hasn't.</summary>
    AppBank? Bank(string context);

    /// <summary>A key as it shows right now: a meeting's countdown, the mic's state, Claude's model.</summary>
    BarItem Live(BarItem item, AppWindow window) => item;

    /// <summary>
    /// A key tapped that the app runs itself (a VS Code command, Explorer's open folder,
    /// Claude's lists); false leaves it to the bar, which presses its keys, types its text or
    /// closes the window. Deck actions ("action") and links ("url") never get here.
    /// </summary>
    bool Run(BarItem key, AppWindow window) => false;

    /// <summary>A choice from the list a "pick" key showed.</summary>
    void Choose(BarItem key, string choice, AppWindow window) { }

    /// <summary>
    /// The deck topics the app's data goes out on ("call", "obsidian"); TrayApp's sources send
    /// them. For the record: the log lists them at start.
    /// </summary>
    IReadOnlyList<string> Topics => [];
}

/// <summary>A key on the context bar, or one its bank offers.</summary>
/// <param name="Id">Stable: saved layouts and the deck's messages use it.</param>
/// <param name="Kind">keys, text, action, close, folder (Explorer's open folder), pick (a list to
/// choose from), slash (typed into Claude's prompt), url.</param>
/// <param name="Target">The keys ("Ctrl+Shift+T", "g, i"), the text ("\n" for Enter), the deck action ("call.mic", "meet.next:zoom")…</param>
/// <param name="Command">For VS Code: the command run through the flex-screen extension instead of the keys.</param>
record BarItem(string Id, string Name, string Glyph, string Kind, string Target, string Command = "")
{
    /// <summary>Keys pressed in the app's window.</summary>
    public static BarItem K(string id, string name, string glyph, string keys, string command = "") => new(id, name, glyph, "keys", keys, command);

    /// <summary>A deck action, with its type after a colon ("call.react:like").</summary>
    public static BarItem A(string id, string name, string glyph, string action) => new(id, name, glyph, "action", action);

    /// <summary>Text typed into the app's window.</summary>
    public static BarItem T(string id, string name, string glyph, string text) => new(id, name, glyph, "text", text);

    /// <summary>Something done with Explorer's open folder.</summary>
    public static BarItem F(string id, string name, string glyph, string what) => new(id, name, glyph, "folder", what);
}

/// <summary>An app's keys in one context.</summary>
/// <param name="Name">The bar's name on the deck in this context ("Gmail"); "" for the window's app name.</param>
/// <param name="Color">The bar's color, 0xRRGGBB; null for the icon's.</param>
/// <param name="Items">The app's own actions, in the bank's order.</param>
/// <param name="Default">On the bar until it's arranged on the deck.</param>
record AppBank(string Name, int? Color, IReadOnlyList<BarItem> Items, IReadOnlyList<string> Default)
{
    /// <summary>This context's keys first, then the app's own it doesn't have (by id); this one's name, color and default bar.</summary>
    public AppBank Over(AppBank app) => this with { Items = [.. Items, .. app.Items.Where(i => Items.All(s => s.Id != i.Id))] };
}

/// <summary>The app window the bar is for.</summary>
/// <param name="Exe">The exe's name, lower case, without ".exe".</param>
/// <param name="App">Its name on the deck ("Visual Studio Code").</param>
record AppWindow(long Hwnd, string Exe, string Title, string App)
{
    /// <summary>
    /// The window to the front, if it isn't; false if it's gone. Keys are for the window the
    /// bar names, and something else may have the front now (the desktop, the taskbar, a menu
    /// that closed).
    /// </summary>
    public bool ToFront()
    {
        if (Hwnd == 0 || !WindowSource.Exists(Hwnd)) return false;
        if (WindowSource.Foreground() != (IntPtr)Hwnd)
        {
            WindowFocus.Activate((IntPtr)Hwnd);
            Thread.Sleep(120);
        }
        return true;
    }
}

/// <summary>What the integrations use from the rest of the companion; TrayApp fills it in.</summary>
/// <param name="Act">Runs a deck action, its type after a colon ("call.react:like").</param>
/// <param name="Pick">Shows a list on the deck: the key's id, a title, the choices (id, name), the one in effect.</param>
/// <param name="Call">The call going on, if any.</param>
/// <param name="NextMeeting">An app's next meeting on the calendar ("zoom", by its link), not over yet.</param>
/// <param name="MicMuted">Windows' microphone mute.</param>
/// <param name="Claude">A window → its Claude session's model and effort.</param>
/// <param name="Usage">Claude's 5-hour usage, "5h 42%", or "" while unknown.</param>
/// <param name="ClaudeSignedOut">Claude Code has no sign-in to read usage with.</param>
/// <param name="VsCode">Runs a VS Code command through the flex-screen extension; false without an extension that can.</param>
/// <param name="ClaudeInCode">VS Code shows Claude Code's chat tab.</param>
sealed record AppServices(
    Action<string> Act, Action<Toast> Toast, Action<string, string, IReadOnlyList<(string Id, string Name)>, string> Pick,
    Func<CallState> Call, Func<string, CalendarEvent?> NextMeeting, Func<bool> MicMuted,
    Func<long, (string Model, string Effort)> Claude, Func<string> Usage, Func<bool> ClaudeSignedOut,
    Func<string, bool> VsCode, Func<bool> ClaudeInCode);

/// <summary>Every app with an integration. A new one gets a folder under Integrations/ and a line here.</summary>
static class AppIntegrations
{
    public static IReadOnlyList<IAppIntegration> All(AppServices services)
    {
        var claude = new ClaudeKeys(services);  // the same keys wherever Claude Code runs
        return
        [
            new VsCodeApp(services, claude), new ClaudeApp(claude), new TerminalApp(claude),
            new ChromeApp(), new EdgeApp(), new ExplorerApp(services), new ObsidianApp(), new SpotifyApp(),
            new TeamsApp(services), new ZoomApp(services), new DiscordApp(services),
        ];
    }
}
