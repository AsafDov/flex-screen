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
    readonly Dictionary<string, (string Model, string Effort)> _info = new();  // session → its model id and effort level
    readonly Dictionary<string, DateTime> _switchedAt = new();  // session → when a hook last named its model
    readonly Dictionary<string, (string Model, DateTime At)> _replied = new();  // session → its last reply's model, and since when
    readonly Dictionary<string, (long Length, string Model)> _transcriptModels = new();  // file → the model of its last reply
    readonly Dictionary<string, string> _asking = new();  // session → a question its turn ends with
    readonly System.Threading.Timer _sweep;

    public IReadOnlyList<ClaudeSession> Current { get; private set; } = [];

    /// <summary>The process of the VS Code window with this folder open (a server's path in Remote-SSH), or 0.</summary>
    public Func<string, int>? WindowForFolder { get; set; }

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

    /// <summary>
    /// The model id ("claude-opus-5-5") and effort level ("high") of the session used last in
    /// that process's windows; "" for what isn't known. The model is the one Claude's last
    /// reply came from (hooks name it only when a session starts, and not always, or when it's
    /// switched), or a switch a hook reported since that reply's model was first seen.
    /// </summary>
    public (string Model, string Effort) InfoIn(int process)
    {
        lock (_gate)
        {
            var session = _sessions.Values.Where(s => _windowOf.GetValueOrDefault(s.Id) == process)
                                   .OrderByDescending(s => s.Since).FirstOrDefault();
            if (session == null) return ("", "");
            (string Model, string Effort) info = _info.GetValueOrDefault(session.Id, ("", ""));
            if (_replied.TryGetValue(session.Id, out var replied) &&
                !(info.Model.Length > 0 && _switchedAt.GetValueOrDefault(session.Id, DateTime.MinValue) > replied.At))
            {
                info.Model = replied.Model;
            }
            return info;
        }
    }

    /// <summary>
    /// The model of the session's last reply, as of this hook: from its transcript, or, for a
    /// session on a server (a transcript the PC can't open), the model the server's hook read
    /// there and sent along (install/remote-claude-hooks.sh). "" if there's no reply yet.
    /// </summary>
    string RepliedModel(JsonNode hook)
    {
        if ((string?)hook["flex_reply_model"] is { Length: > 0 } heard &&
            System.Text.RegularExpressions.Regex.IsMatch(heard, @"^claude-[\w.\-\[\]]{1,60}$"))
        {
            return heard;
        }
        return (string?)hook["transcript_path"] is { Length: > 0 } path && !path.StartsWith('/') ? TranscriptModel(path) : "";
    }

    /// <summary>
    /// The app a session runs in, by the process behind its hook's connection (curl's parents:
    /// VS Code, the Claude app, a terminal). A session on a server (VS Code's Remote-SSH) posts
    /// through the SSH tunnel, whose connection may lead nowhere useful: the VS Code window with
    /// its folder open is the one. Then by folder for a local session the connection didn't place.
    /// </summary>
    int WindowOf(JsonNode hook, System.Net.IPEndPoint? client)
    {
        var cwd = (string?)hook["cwd"] ?? "";
        var remote = cwd.StartsWith('/');
        var owner = remote ? WindowForFolder?.Invoke(cwd) ?? 0 : 0;
        if (owner == 0 && client != null) owner = ProcessTree.WindowOwnerOf(client);
        if (owner == 0 && !remote) owner = WindowForFolder?.Invoke(cwd) ?? 0;
        return owner;
    }

    /// <summary>The model of the last reply in a transcript (its main conversation, not a subagent's); "" if none.</summary>
    string TranscriptModel(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = file.Length;
            lock (_gate)
            {
                if (_transcriptModels.TryGetValue(path, out var known) && known.Length == length) return known.Model;
            }
            var take = (int)Math.Min(length, 256 * 1024);  // the last replies are near the end
            file.Seek(length - take, SeekOrigin.Begin);
            var buffer = new byte[take];
            var read = 0;
            while (read < take && file.Read(buffer, read, take - read) is var n and > 0) read += n;
            var lines = System.Text.Encoding.UTF8.GetString(buffer, 0, read).Split('\n');
            var model = "";
            for (var i = lines.Length - 1; i >= 0 && model.Length == 0; i--)
            {
                var line = lines[i];
                if (!line.Contains("\"type\":\"assistant\"") || line.Contains("\"isSidechain\":true")) continue;
                var match = System.Text.RegularExpressions.Regex.Match(line, "\"model\":\"(claude-[^\"]+)\"");
                if (match.Success) model = match.Groups[1].Value;
            }
            lock (_gate)
            {
                if (_transcriptModels.Count > 50) _transcriptModels.Clear();
                _transcriptModels[path] = (length, model);
            }
            return model;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "";
        }
    }

    /// <summary>Waiting for an answer to a question (or a plan to approve), not a permission.</summary>
    public static bool IsQuestion(ClaudeSession s) => s.State == "input" && (s.Detail.StartsWith("Asks: ") || s.Detail.StartsWith("Plan "));

    /// <summary>
    /// A tool that stops the turn to ask you something: AskUserQuestion and ExitPlanMode wait
    /// in the middle of a turn; the Claude app's end-of-turn question (its wrap-up tool, when it
    /// asks) leaves the turn ending as "needs you" rather than "done".
    /// </summary>
    static string? QuestionOf(JsonNode hook) => (string?)hook["tool_name"] switch
    {
        "AskUserQuestion" => "Asks: " + ((string?)hook["tool_input"]?["questions"]?[0]?["question"] ?? "a question"),
        "ExitPlanMode" => "Plan ready for your approval",
        _ => null,
    };

    static string? EndOfTurnQuestion(JsonNode hook) =>
        (string?)hook["tool_name"] is { } tool && tool.EndsWith("wrap_up", StringComparison.Ordinal) &&
        (string?)hook["tool_input"]?["ask"] is { Length: > 0 } ask ? "Asks: " + ask : null;

    /// <summary>The session's project (its git repo or folder), or the folder's name for one not seen yet.</summary>
    public string ProjectFor(string id, string cwd)
    {
        lock (_gate) return _sessions.TryGetValue(id, out var s) ? s.Project : ProjectOf(cwd).Project;
    }

    public void Handle(JsonNode hook, System.Net.IPEndPoint? client)
    {
        var id = (string?)hook["session_id"];
        if (string.IsNullOrEmpty(id)) return;
        var now = DateTime.UtcNow;
        bool known;
        lock (_gate) known = _windowOf.ContainsKey(id);
        if (!known && WindowOf(hook, client) is var owner and > 0)
        {
            lock (_gate) _windowOf[id] = owner;
            try { Log.Write($"claude: session {id[..Math.Min(8, id.Length)]} runs in {System.Diagnostics.Process.GetProcessById(owner).ProcessName}"); } catch (ArgumentException) { }
        }
        var replied = RepliedModel(hook);
        lock (_gate)
        {
            // The model comes with SessionStart (sometimes) and every switch; the effort level with tool events.
            (string Model, string Effort) info = _info.GetValueOrDefault(id, ("", ""));
            if (((string?)hook["to_model"] ?? (string?)hook["model"]) is { Length: > 0 } model)
            {
                info.Model = model;
                _switchedAt[id] = now;
            }
            // A reply's model counts from when it's first seen, so a switch after it wins until Claude replies again.
            if (replied.Length > 0 && (!_replied.TryGetValue(id, out var before) || before.Model != replied))
            {
                _replied[id] = (replied, now);
                Log.Write($"claude: session {id[..Math.Min(8, id.Length)]} replies with {replied}");
            }
            if ((string?)hook["effort"]?["level"] is { Length: > 0 } effort) info.Effort = effort;
            _info[id] = info;
            _sessions.TryGetValue(id, out var s);
            // The hook reports the session's current folder, which moves when Claude changes
            // directory; keep the project a session started in.
            var (project, cwd) = s != null ? (s.Project, s.Cwd) : ProjectOf((string?)hook["cwd"] ?? "");
            var name = (string?)hook["hook_event_name"];
            if (name is "UserPromptSubmit" or "SessionStart") _asking.Remove(id);
            if (name == "PreToolUse" && EndOfTurnQuestion(hook) is { } ask) _asking[id] = ask;
            var next = name switch
            {
                "SessionStart" => new ClaudeSession(id, project, cwd, "idle", "", now),
                "UserPromptSubmit" => new ClaudeSession(id, project, cwd, "working", Short((string?)hook["prompt"]), now),
                "PreToolUse" when QuestionOf(hook) is { } question => new ClaudeSession(id, project, cwd, "input", Short(question), now),
                "PreToolUse" => new ClaudeSession(id, project, cwd, "working", ToolDetail(hook),
                                                  s?.State == "working" ? s.Since : now),
                "PostToolUse" => s is { State: "working" } ? s
                    : new ClaudeSession(id, project, cwd, "working", s?.Detail ?? "", now),  // an approval or an answer went through
                "Notification" => FromNotification(s, id, project, cwd, (string?)hook["message"] ?? "", now),
                "PermissionRequest" => new ClaudeSession(id, project, cwd, "input", ToolDetail(hook), now),
                "Stop" => _asking.Remove(id, out var asked) ? new ClaudeSession(id, project, cwd, "input", Short(asked), now)
                                                          : new ClaudeSession(id, project, cwd, "done", "Finished", now),
                "SessionEnd" => null,
                _ => s,
            };
            if (next == null)
            {
                _sessions.Remove(id);
                _windowOf.Remove(id);
                _info.Remove(id);
                _switchedAt.Remove(id);
                _replied.Remove(id);
                _asking.Remove(id);
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

    public static string ToolDetail(JsonNode hook)
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
            foreach (var id in stale)
            {
                _sessions.Remove(id);
                _info.Remove(id);
                _switchedAt.Remove(id);
                _replied.Remove(id);
            }
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
