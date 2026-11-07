using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <param name="State">"working", "input" (waiting for an approval), "done" or "idle".</param>
/// <param name="Since">When the session entered this state.</param>
record ClaudeSession(string Id, string Project, string Cwd, string State, string Detail, DateTime Since);

/// <summary>
/// Claude Code sessions as reported by its hooks (<see cref="ClaudeHooks"/> installs them;
/// they post every event to the companion's local server).
/// </summary>
sealed class ClaudeSource
{
    const int MaxShown = 4;
    static readonly TimeSpan StaleQuiet = TimeSpan.FromMinutes(30);  // idle or done
    static readonly TimeSpan StaleBusy = TimeSpan.FromHours(2);      // a missed Stop/SessionEnd

    readonly object _gate = new();
    readonly Dictionary<string, ClaudeSession> _sessions = new();
    readonly Dictionary<string, int> _windowOf = new();  // session → the app it runs in (its window's process)
    readonly System.Threading.Timer _sweep;

    public IReadOnlyList<ClaudeSession> Current { get; private set; } = [];

    /// <summary>Raised on a server or timer thread when any session changes.</summary>
    public event Action<IReadOnlyList<ClaudeSession>>? Changed;

    public ClaudeSource()
    {
        _sweep = new System.Threading.Timer(_ => Sweep(), null, 60_000, 60_000);
    }

    /// <summary>The process whose window the session runs in (the Claude app, VS Code, a terminal), or 0.</summary>
    public int WindowOf(string id)
    {
        lock (_gate) return _windowOf.GetValueOrDefault(id);
    }

    public void Handle(JsonNode hook, System.Net.IPEndPoint? client)
    {
        var id = (string?)hook["session_id"];
        if (string.IsNullOrEmpty(id)) return;
        var now = DateTime.UtcNow;
        bool known;
        lock (_gate) known = _windowOf.ContainsKey(id);
        if (!known && client != null && ProcessTree.WindowOwnerOf(client) is var owner and > 0)
        {
            lock (_gate) _windowOf[id] = owner;
            try { Log.Write($"claude: session {id[..Math.Min(8, id.Length)]} runs in {System.Diagnostics.Process.GetProcessById(owner).ProcessName}"); } catch (ArgumentException) { }
        }
        lock (_gate)
        {
            _sessions.TryGetValue(id, out var s);
            // The hook reports the session's current folder, which moves when Claude changes
            // directory; keep the project a session started in.
            var (project, cwd) = s != null ? (s.Project, s.Cwd) : ProjectOf((string?)hook["cwd"] ?? "");
            var next = ((string?)hook["hook_event_name"]) switch
            {
                "SessionStart" => new ClaudeSession(id, project, cwd, "idle", "", now),
                "UserPromptSubmit" => new ClaudeSession(id, project, cwd, "working", Short((string?)hook["prompt"]), now),
                "PreToolUse" => new ClaudeSession(id, project, cwd, "working", ToolDetail(hook),
                                                  s?.State == "working" ? s.Since : now),
                "PostToolUse" => s is { State: "working" } ? s
                    : new ClaudeSession(id, project, cwd, "working", s?.Detail ?? "", now),  // an approval went through
                "Notification" => FromNotification(s, id, project, cwd, (string?)hook["message"] ?? "", now),
                "Stop" => new ClaudeSession(id, project, cwd, "done", "Finished", now),
                "SessionEnd" => null,
                _ => s,
            };
            if (next == null)
            {
                _sessions.Remove(id);
                _windowOf.Remove(id);
            }
            else _sessions[id] = next;
        }
        Publish();
    }

    /// <summary>
    /// Permission prompts mean "needs you". The "waiting for your input" reminder that comes
    /// a minute after Claude finishes doesn't: the session is still just done.
    /// </summary>
    static ClaudeSession? FromNotification(ClaudeSession? s, string id, string project, string cwd, string message, DateTime now)
    {
        var approval = message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
                       message.Contains("approv", StringComparison.OrdinalIgnoreCase);
        if (approval || s is { State: "working" }) return new ClaudeSession(id, project, cwd, "input", Short(message), now);
        return s ?? new ClaudeSession(id, project, cwd, "idle", "", now);
    }

    /// <summary>The git repo the folder is in (its root's name), or else the folder itself.</summary>
    static (string Project, string Root) ProjectOf(string cwd)
    {
        var folder = cwd.TrimEnd('\\', '/');
        for (var dir = folder; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"))) return (Path.GetFileName(dir), dir);
        }
        return (Path.GetFileName(folder), folder);
    }

    static string ToolDetail(JsonNode hook)
    {
        var tool = (string?)hook["tool_name"] ?? "tool";
        var input = hook["tool_input"];
        var detail = tool switch
        {
            "Bash" or "PowerShell" => (string?)input?["command"],
            "Edit" or "Write" or "Read" or "MultiEdit" or "NotebookEdit" =>
                Path.GetFileName((string?)input?["file_path"] ?? (string?)input?["notebook_path"] ?? ""),
            "Grep" or "Glob" => (string?)input?["pattern"],
            _ => null,
        };
        return Short(string.IsNullOrWhiteSpace(detail) ? tool : $"{tool}: {detail}");
    }

    static string Short(string? text)
    {
        var line = (text ?? "").ReplaceLineEndings(" ").Trim();
        return line.Length > 80 ? line[..79] + "…" : line;
    }

    void Sweep()
    {
        var now = DateTime.UtcNow;
        bool removed;
        lock (_gate)
        {
            var stale = _sessions.Values
                .Where(s => now - s.Since > (s.State is "working" or "input" ? StaleBusy : StaleQuiet))
                .Select(s => s.Id).ToList();
            foreach (var id in stale) _sessions.Remove(id);
            removed = stale.Count > 0;
        }
        if (removed) Publish();
    }

    void Publish()
    {
        IReadOnlyList<ClaudeSession> shown;
        lock (_gate)
        {
            shown = _sessions.Values
                .OrderBy(s => s.State switch { "input" => 0, "working" => 1, "done" => 2, _ => 3 })
                .ThenByDescending(s => s.Since)
                .Take(MaxShown)
                .ToList();
            Current = shown;
        }
        Changed?.Invoke(shown);
    }
}
