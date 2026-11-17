using static FlexScreen.Companion.BarItem;

namespace FlexScreen.Companion;

/// <summary>Obsidian: today's daily note (through <see cref="ObsidianSource"/>, as on Capture), search, navigation, formatting.</summary>
sealed class ObsidianApp : IAppIntegration
{
    static readonly AppBank Own = new("", 0x8B5CF6,
    [
        A("daily", "Daily note", "calendar", "obs.today"), K("search", "Search", "search", "Ctrl+Shift+F"),
        K("quick-open", "Quick open", "folder", "Ctrl+O"), K("commands", "Commands", "keyboard", "Ctrl+P"),
        K("back", "Back", "back", "Ctrl+Alt+Left"), K("forward", "Forward", "forward", "Ctrl+Alt+Right"),
        K("graph", "Graph", "graph", "Ctrl+G"), K("new-note", "New note", "plus", "Ctrl+N"), K("reading", "Reading view", "eye", "Ctrl+E"),
        K("link", "Insert link", "link", "Ctrl+K"), K("checkbox", "Checkbox", "check", "Ctrl+L"), K("find", "Find in note", "search", "Ctrl+F"),
        K("bold", "Bold", "format", "Ctrl+B"), K("italic", "Italic", "cursor", "Ctrl+I"), K("close-tab", "Close tab", "close", "Ctrl+W"),
    ], ["daily", "search", "quick-open", "commands", "back", "graph"]);

    public IReadOnlyList<string> Exes { get; } = ["obsidian"];

    public AppBank? Bank(string context) => context.Length == 0 ? Own : null;

    /// <summary>The vault's recent notes, for Capture (<see cref="ObsidianSource"/>).</summary>
    public IReadOnlyList<string> Topics { get; } = ["obsidian"];
}
