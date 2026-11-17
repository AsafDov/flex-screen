using static FlexScreen.Companion.BarItem;

namespace FlexScreen.Companion;

/// <summary>Spotify's own shortcuts (what's playing comes from Windows' media session, for any player).</summary>
sealed class SpotifyApp : IAppIntegration
{
    static readonly AppBank Own = new("", 0x1DB954,
    [
        K("prev", "Previous", "prev-track", "Ctrl+Left"), K("play", "Play/pause", "play", "Space"), K("next", "Next", "next-track", "Ctrl+Right"),
        K("like", "Like", "heart", "Alt+Shift+B"), K("shuffle", "Shuffle", "shuffle", "Ctrl+S"), K("repeat", "Repeat", "reload", "Ctrl+R"),
        K("search", "Search", "search", "Ctrl+K"), K("back5", "Back 5 s", "back", "Shift+Left"), K("ahead5", "Ahead 5 s", "forward", "Shift+Right"),
        K("louder", "Louder", "plus", "Ctrl+Up"), K("quieter", "Quieter", "minus", "Ctrl+Down"),
    ], ["prev", "play", "next", "like", "shuffle", "search"]);

    public IReadOnlyList<string> Exes { get; } = ["spotify"];

    public AppBank? Bank(string context) => context.Length == 0 ? Own : null;
}
