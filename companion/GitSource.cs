using System.Diagnostics;

namespace FlexScreen.Companion;

/// <param name="Busy">The operation running ("fetch", "pull"…), or "".</param>
/// <param name="Error">Why there's nothing to show ("Not a git repository"), or "".</param>
record GitState(string Repo, string Branch, int Ahead, int Behind, int Modified, int Added, int Deleted,
                int Untracked, string Last, DateTime LastAt, string Busy, string Error)
{
    public static readonly GitState None = new("", "", 0, 0, 0, 0, 0, 0, "", DateTime.MinValue, "", "");
}

/// <summary>
/// Git status for the folder the current VS Code window has open, polled every few seconds,
/// and the deck's git actions (fetch, pull, push, stash) run with the git on PATH.
/// </summary>
sealed class GitSource
{
    const int PollMs = 3000;
    static readonly string[] Operations = ["fetch", "pull", "push", "stash"];

    readonly object _gate = new();
    readonly System.Threading.Timer _poll;
    string? _folder;
    string _remote = "";
    string? _root;
    string _busy = "";

    public GitState Current { get; private set; } = GitState.None;

    public event Action<GitState>? Changed;
    public event Action<Toast>? Toast;

    public GitSource()
    {
        _poll = new System.Threading.Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <param name="remote">The VS Code window's remote ("wsl"…), or "": a remote folder is a
    /// path on that machine, which the PC's git can't read, so it's named but not polled.</param>
    public void SetFolder(string? folder, string remote = "")
    {
        lock (_gate)
        {
            if (folder == _folder && remote == _remote) return;
            _folder = folder;
            _remote = remote;
            _root = null;
        }
        if (remote.Length > 0 && folder != null)
        {
            _poll.Change(Timeout.Infinite, Timeout.Infinite);
            var where = remote switch
            {
                "wsl" => "WSL",
                "dev-container" or "attached-container" => "the container",
                "ssh-remote" => "the SSH host",
                "codespaces" => "the codespace",
                _ => remote,
            };
            Publish(GitState.None with { Repo = folder.Split('/', '\\').LastOrDefault(p => p.Length > 0) ?? folder, Error = $"Git runs in {where}" });
            return;
        }
        _poll.Change(0, PollMs);
    }

    public void Run(string operation)
    {
        if (!Operations.Contains(operation)) return;
        string root;
        lock (_gate)
        {
            if (_root == null || _busy.Length > 0) return;
            root = _root;
            _busy = operation;
        }
        Publish(Current with { Busy = operation });
        _ = Task.Run(() =>
        {
            var args = operation switch
            {
                "fetch" => "fetch --prune",
                "pull" => "pull --ff-only",
                "push" => "push",
                _ => "stash push --include-untracked",
            };
            var (code, output, error) = Git(root, args, 120_000);
            var first = (error.Length > 0 ? error : output).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            Log.Write($"git: {args} in {root}: exit {code}{(first.Length > 0 ? $": {first}" : "")}");
            Toast?.Invoke(code == 0
                ? new Toast("ok", $"git {operation} done")
                : new Toast("error", $"git {operation} failed: {first}"));
            lock (_gate) _busy = "";
            Poll();
        });
    }

    void Poll()
    {
        string? folder;
        lock (_gate) folder = _folder;
        if (folder == null)
        {
            Publish(GitState.None);
            return;
        }
        try
        {
            var root = ResolveRoot(folder);
            if (root == null)
            {
                Publish(GitState.None with { Repo = Path.GetFileName(folder), Error = "Not a git repository" });
                return;
            }
            Publish(Read(root));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Publish(GitState.None with { Repo = Path.GetFileName(folder), Error = "git isn't installed" });
        }
    }

    string? ResolveRoot(string folder)
    {
        lock (_gate)
        {
            if (_root != null) return _root;
        }
        var (code, output, _) = Git(folder, "rev-parse --show-toplevel", 5000);
        var root = code == 0 ? Path.GetFullPath(output.Trim()) : null;
        lock (_gate)
        {
            if (folder == _folder) _root = root;
        }
        return root;
    }

    GitState Read(string root)
    {
        var (_, status, _) = Git(root, "status --porcelain=v2 --branch", 10_000);
        string branch = "", busy;
        int ahead = 0, behind = 0, modified = 0, added = 0, deleted = 0, untracked = 0;
        foreach (var line in status.Split('\n'))
        {
            if (line.StartsWith("# branch.head ")) branch = line[14..].Trim();
            else if (line.StartsWith("# branch.ab "))
            {
                var parts = line[12..].Trim().Split(' ');
                if (parts.Length == 2)
                {
                    int.TryParse(parts[0].TrimStart('+'), out ahead);
                    int.TryParse(parts[1].TrimStart('-'), out behind);
                }
            }
            else if (line.StartsWith("? ")) untracked++;
            else if (line.Length > 3 && line[0] is '1' or '2' or 'u')
            {
                var xy = line.Substring(2, 2);
                if (xy.Contains('D')) deleted++;
                else if (xy.Contains('A')) added++;
                else modified++;
            }
        }
        var (logCode, log, _) = Git(root, "log -1 --format=%s%x1f%ct", 5000);
        var last = "";
        var lastAt = DateTime.MinValue;
        if (logCode == 0)
        {
            var parts = log.Trim().Split('\x1f');
            last = parts[0];
            if (parts.Length > 1 && long.TryParse(parts[1], out var epoch)) lastAt = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
        }
        lock (_gate) busy = _busy;
        return new GitState(Path.GetFileName(root), branch, ahead, behind, modified, added, deleted, untracked, last, lastAt, busy, "");
    }

    void Publish(GitState state)
    {
        lock (_gate)
        {
            if (state == Current) return;
            Current = state;
        }
        Changed?.Invoke(state);
    }

    static (int Code, string Output, string Error) Git(string directory, string args, int timeoutMs)
    {
        var start = new ProcessStartInfo("git", $"-C \"{directory}\" {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";  // never wait for a username on a hidden console
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (-1, "", "timed out");
        }
        return (process.ExitCode, output.Result, error.Result);
    }
}
