using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// Adds and removes the Claude Code hooks that report session state to the companion, in the
/// user's ~/.claude/settings.json, and the deck's tools (<see cref="DeckTools"/>): registered
/// with Claude Code for every project, and allowed without asking. Run as
/// `flex-screen-companion.exe --install-hooks` or `--uninstall-hooks` (the install scripts
/// do). Claude Code on a Linux machine reached over SSH gets the same from
/// install/remote-claude-hooks.sh, run there.
/// </summary>
/// <remarks>
/// Each hook runs curl.exe directly (exec form, no shell, so it works without Git Bash) and
/// asynchronously, so it never slows Claude down and a stopped companion costs nothing.
/// PermissionRequest is the exception: it waits for an answer from the deck (Approve or
/// Deny) while Claude's own prompt shows as usual; whichever answers first wins. It runs
/// through cmd.exe so a stopped companion ends it at once without an error.
/// Everything else in the settings file is left exactly as it was.
/// </remarks>
static class ClaudeHooks
{
    static readonly string[] Events =
        ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd",
         "PermissionRequest", "PostModelSwitch"];

    /// <summary>How long a permission request waits for the deck (Claude's own prompt shows meanwhile).</summary>
    public const int PermissionSeconds = 590;

    static string Url => $"http://localhost:{LocalServer.Port}/claude";

    /// <summary>The permission rule that lets Claude use the deck's tools without asking: they only show things on the deck.</summary>
    const string AllowTools = "mcp__" + DeckTools.ServerName;

    static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    public static void Install()
    {
        var root = Load();
        var hooks = root["hooks"] as JsonObject;
        if (hooks == null) root["hooks"] = hooks = new JsonObject();
        foreach (var name in Events)
        {
            var list = hooks[name] as JsonArray;
            if (list == null) hooks[name] = list = new JsonArray();
            RemoveOurs(list);
            list.Add(Entry(name));
        }
        var permissions = root["permissions"] as JsonObject;
        if (permissions == null) root["permissions"] = permissions = new JsonObject();
        var allow = permissions["allow"] as JsonArray;
        if (allow == null) permissions["allow"] = allow = new JsonArray();
        if (!allow.Any(rule => (string?)rule == AllowTools)) allow.Add(AllowTools);
        Save(root);
        Log.Write($"hooks: installed in {SettingsPath}");
        RegisterTools(add: true);
    }

    public static void Uninstall()
    {
        if (!File.Exists(SettingsPath)) return;
        var root = Load();
        if (root["hooks"] is JsonObject hooks)
        {
            foreach (var name in Events)
            {
                if (hooks[name] is not JsonArray list) continue;
                RemoveOurs(list);
                if (list.Count == 0) hooks.Remove(name);
            }
            if (hooks.Count == 0) root.Remove("hooks");
        }
        if (root["permissions"] is JsonObject permissions && permissions["allow"] is JsonArray allow)
        {
            for (var i = allow.Count - 1; i >= 0; i--)
                if ((string?)allow[i] == AllowTools) allow.RemoveAt(i);
            if (allow.Count == 0) permissions.Remove("allow");
            if (permissions.Count == 0) root.Remove("permissions");
        }
        Save(root);
        Log.Write($"hooks: removed from {SettingsPath}");
        RegisterTools(add: false);
    }

    /// <summary>
    /// The deck's tool server in Claude Code's own list (claude mcp, for every project), with a
    /// call allowed to last an hour: a question on the deck waits for its tap, and Claude Code
    /// gives a call 60 s unless the server says otherwise.
    /// </summary>
    static void RegisterTools(bool add)
    {
        if (ClaudeUsage.FindClaude() is not { } claude)
        {
            Log.Write("hooks: Claude Code not found; the deck's tools aren't registered");
            return;
        }
        Claude(claude, "mcp", "remove", "--scope", "user", DeckTools.ServerName);  // none yet is fine
        if (!add) return;
        var server = new JsonObject
        {
            ["type"] = "http",
            ["url"] = $"http://localhost:{LocalServer.Port}/mcp",
            ["timeout"] = DeckTools.CallTimeoutMs,
        };
        var (code, output) = Claude(claude, "mcp", "add-json", "--scope", "user", DeckTools.ServerName, server.ToJsonString());
        Log.Write(code == 0 ? "hooks: the deck's tools are registered with Claude Code" : $"hooks: claude mcp add-json failed ({code}): {output}");
    }

    static (int Code, string Output) Claude(string exe, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        // As from a plain prompt, not as part of a Claude session that may have run the installer.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase) ||
                                                               k.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }
        try
        {
            using var process = System.Diagnostics.Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                process.Kill();
                return (-1, "timed out");
            }
            return (process.ExitCode, (stdout.Result + stderr.Result).Trim());
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, e.Message);
        }
    }

    static JsonObject Entry(string name)
    {
        var hook = name == "PermissionRequest" ? new JsonObject
        {
            ["type"] = "command",
            ["command"] = "cmd.exe",
            ["args"] = new JsonArray("/c", $"curl.exe -s -m {PermissionSeconds} -X POST -H Content-Type:application/json " +
                                           $"--data-binary @- {Url}/permission || exit /b 0"),
            ["timeout"] = PermissionSeconds + 10,
        } : new JsonObject
        {
            ["type"] = "command",
            ["command"] = "curl.exe",
            ["args"] = new JsonArray("-s", "-m", "2", "-X", "POST", "-H", "Content-Type: application/json",
                                     "--data-binary", "@-", Url),
            ["async"] = true,
            ["timeout"] = 5,
        };
        var entry = new JsonObject();
        if (name is "PreToolUse" or "PostToolUse") entry["matcher"] = "*";
        entry["hooks"] = name == "PreToolUse" ? new JsonArray(hook, StopHook()) : new JsonArray(hook);
        return entry;
    }

    /// <summary>
    /// The deck's Stop for a session: before each tool call Claude waits for this one
    /// (about 70 ms: cmd and curl starting), which answers "don't continue" once the deck asked
    /// that session to stop, and nothing otherwise. A stopped companion ends it at once.
    /// </summary>
    static JsonObject StopHook() => new()
    {
        ["type"] = "command",
        ["command"] = "cmd.exe",
        ["args"] = new JsonArray("/c", $"curl.exe -s -m 2 -X POST -H Content-Type:application/json --data-binary @- {Url}/stop || exit /b 0"),
        ["timeout"] = 5,
    };

    static void RemoveOurs(JsonArray list)
    {
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i]?["hooks"] is JsonArray hs &&
                hs.Any(h => h?["args"] is JsonArray a && a.Any(x => x?.GetValue<string>().Contains(Url) == true)))
            {
                list.RemoveAt(i);
            }
        }
    }

    static JsonObject Load()
    {
        if (!File.Exists(SettingsPath)) return new JsonObject();
        var text = File.ReadAllText(SettingsPath);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        // A file that doesn't parse is left alone rather than overwritten.
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
               ?? throw new InvalidDataException($"{SettingsPath} isn't a JSON object");
    }

    static void Save(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var backup = SettingsPath + ".before-flex-screen";
        if (File.Exists(SettingsPath) && !File.Exists(backup)) File.Copy(SettingsPath, backup);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        File.Move(temp, SettingsPath, overwrite: true);
    }
}
