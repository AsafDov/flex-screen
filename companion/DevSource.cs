using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlexScreen.Companion;

/// <param name="Exit">null while running, or when the shell couldn't report one.</param>
record TermState(string Repo, bool Running, string Cmd, DateTime Since, int? Exit, long Ms, IReadOnlyList<string> Lines)
{
    public static readonly TermState None = new("", false, "", DateTime.MinValue, null, 0, []);
}

/// <param name="State">"none", "running", "passed" or "failed".</param>
/// <param name="History">Results of the last builds, oldest first: "P" passed, "F" failed.</param>
record BuildState(string State, string Cmd, long Ms, DateTime At, string History)
{
    public static readonly BuildState None = new("none", "", 0, DateTime.MinValue, "");
}

/// <param name="At">When it ended, or started while running.</param>
record RecentCommand(string Cmd, bool Running, int? Exit, long Ms, DateTime At);

/// <param name="Action">A deck action tapping the toast runs ("claude.open"), with <paramref name="Id"/>; "" just dismisses.</param>
record Toast(string Level, string Text, string Action = "", string Id = "");

/// <summary>
/// Terminal commands and builds from the VS Code extension's reports: what's running and how
/// it ended, the last few commands (which the extension keeps per workspace, so they survive
/// restarts), a build history, and a toast when something long finishes or anything fails.
/// </summary>
sealed class DevSource
{
    const int HistoryLength = 10;
    static readonly TimeSpan ToastAfter = TimeSpan.FromSeconds(10);

    // Terminal commands that count as builds for the build status.
    static readonly Regex BuildLike = new(
        @"\b(build|compile|publish|pio\s+run|make|msbuild|tsc|cargo\s+(build|check)|gradle|mvn|webpack|vite\s+build)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    readonly object _gate = new();
    readonly Dictionary<string, DateTime> _tasksStarted = new();
    int _runId = -1;
    VsCodeWindow? _runWindow;

    public TermState Term { get; private set; } = TermState.None;
    public BuildState Build { get; private set; } = BuildState.None;
    public IReadOnlyList<RecentCommand> Recent { get; private set; } = [];

    /// <summary>The window whose commands <see cref="Recent"/> lists: replays go there.</summary>
    public VsCodeWindow? RecentWindow { get; private set; }

    public event Action<TermState>? TermChanged;
    public event Action<BuildState>? BuildChanged;
    public event Action<IReadOnlyList<RecentCommand>>? RecentChanged;
    public event Action<Toast>? Toast;

    public void OnMessage(VsCodeWindow window, JsonNode m)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            switch ((string?)m["t"])
            {
                case "term.start":
                {
                    _runId = (int?)m["id"] ?? -1;
                    _runWindow = window;
                    var cmd = (string?)m["cmd"] ?? "";
                    SetTerm(new TermState(window.Name, true, cmd, now, null, 0, []));
                    if (BuildLike.IsMatch(cmd)) SetBuild(Build with { State = "running", Cmd = cmd, At = now });
                    Remember(window, new RecentCommand(cmd, true, null, 0, now));
                    break;
                }
                case "term.data" when IsCurrentRun(window, m):
                    SetTerm(Term with { Lines = (m["lines"] as JsonArray)?.Select(l => (string?)l ?? "").ToList() ?? [] });
                    break;
                case "term.end" when IsCurrentRun(window, m):
                {
                    var exit = (int?)m["exit"];
                    var ms = (long?)m["ms"] ?? 0;
                    SetTerm(Term with { Running = false, Exit = exit, Ms = ms });
                    Log.Write($"term: {Describe(Term.Cmd, exit, ms)}" +
                              (exit is not null and not 0 && Term.Lines.Count > 0 ? $": {Term.Lines[^1]}" : ""));
                    if (BuildLike.IsMatch(Term.Cmd)) FinishBuild(Term.Cmd, exit, ms, now);
                    Remember(window, new RecentCommand(Term.Cmd, false, exit, ms, now));
                    if (exit is not null and not 0 || TimeSpan.FromMilliseconds(ms) >= ToastAfter)
                    {
                        Toast?.Invoke(new Toast(exit is null or 0 ? "ok" : "error", Describe(Term.Cmd, exit, ms)));
                    }
                    break;
                }
                case "term.history":
                    OnHistory(window, m["items"] as JsonArray ?? []);
                    break;
                case "task.start" when (string?)m["group"] == "build":
                    _tasksStarted[(string?)m["name"] ?? ""] = now;
                    SetBuild(Build with { State = "running", Cmd = (string?)m["name"] ?? "build task", At = now });
                    break;
                case "task.end" when (string?)m["group"] == "build":
                {
                    var name = (string?)m["name"] ?? "build task";
                    var ms = _tasksStarted.Remove(name, out var started) ? (long)(now - started).TotalMilliseconds : 0;
                    var exit = (int?)m["exit"];
                    FinishBuild(name, exit, ms, now);
                    Toast?.Invoke(new Toast(exit is null or 0 ? "ok" : "error", Describe(name, exit, ms)));
                    break;
                }
            }
        }
    }

    /// <summary>
    /// A window's last commands, newest first, sent when it connects and whenever one starts
    /// or ends. The list follows the window with the newest command; after a restart the newest
    /// one's output comes back from it too.
    /// </summary>
    void OnHistory(VsCodeWindow window, JsonArray items)
    {
        var list = new List<RecentCommand>();
        foreach (var item in items)
        {
            var cmd = (string?)item?["cmd"] ?? "";
            if (cmd.Length == 0) continue;
            var at = DateTimeOffset.FromUnixTimeMilliseconds((long?)item?["at"] ?? 0).UtcDateTime;
            list.Add(new RecentCommand(cmd, (bool?)item?["running"] ?? false, (int?)item?["exit"], (long?)item?["ms"] ?? 0, at));
        }
        var newest = list.Count > 0 ? list[0].At : DateTime.MinValue;
        var shownNewest = Recent.Count > 0 ? Recent[0].At : DateTime.MinValue;
        var stale = RecentWindow == null || RecentWindow == window || RecentWindow.Socket.State != System.Net.WebSockets.WebSocketState.Open;
        if (!stale && newest <= shownNewest) return;
        RecentWindow = window;
        Recent = list;
        RecentChanged?.Invoke(list);

        // Nothing on show since the companion (or the window) started: the newest command's output.
        if (list.Count > 0 && !list[0].Running && !Term.Running && Term.Since < list[0].At - TimeSpan.FromMilliseconds(list[0].Ms))
        {
            var first = list[0];
            var lines = (items[0]?["lines"] as JsonArray)?.Select(l => (string?)l ?? "").ToList() ?? [];
            _runId = -1;
            _runWindow = window;
            SetTerm(new TermState(window.Name, false, first.Cmd, first.At - TimeSpan.FromMilliseconds(first.Ms), first.Exit, first.Ms, lines));
        }
    }

    /// <summary>
    /// A command starting or ending, put first in the list (each command once) without
    /// waiting for the window's own history: extension 0.1.0 never sends one, and a VS Code
    /// window keeps the old extension until it's reloaded. A history that comes after this
    /// (0.2.0 sends one on every start and end) replaces the list with the window's own.
    /// </summary>
    void Remember(VsCodeWindow window, RecentCommand command)
    {
        if (command.Cmd.Length == 0) return;
        var list = RecentWindow == window ? Recent.Where(c => c.Cmd != command.Cmd).ToList() : [];
        list.Insert(0, command);
        if (list.Count > HistoryLength) list.RemoveRange(HistoryLength, list.Count - HistoryLength);
        RecentWindow = window;
        Recent = list;
        RecentChanged?.Invoke(list);
    }

    bool IsCurrentRun(VsCodeWindow window, JsonNode m) => window == _runWindow && (int?)m["id"] == _runId;

    void FinishBuild(string cmd, int? exit, long ms, DateTime now)
    {
        var passed = exit is null or 0;
        var history = (Build.History + (passed ? "P" : "F"))[^Math.Min(HistoryLength, Build.History.Length + 1)..];
        SetBuild(new BuildState(passed ? "passed" : "failed", cmd, ms, now, history));
    }

    void SetTerm(TermState state)
    {
        Term = state;
        TermChanged?.Invoke(state);
    }

    void SetBuild(BuildState state)
    {
        Build = state;
        BuildChanged?.Invoke(state);
    }

    static string Describe(string cmd, int? exit, long ms)
    {
        var outcome = exit switch { null => "finished", 0 => "passed", _ => $"failed (exit {exit})" };
        return $"{cmd} {outcome} · {Duration(ms)}";
    }

    public static string Duration(long ms) =>
        ms < 60_000 ? $"{ms / 1000.0:0.#} s" : $"{ms / 60_000} min {ms / 1000 % 60} s";
}
