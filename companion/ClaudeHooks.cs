using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// Adds and removes the Claude Code hooks that report session state to the companion, in the
/// user's ~/.claude/settings.json. Run as `flex-screen-companion.exe --install-hooks` or
/// `--uninstall-hooks` (the install scripts do). Claude Code on a Linux machine reached over
/// SSH gets the same hooks from install/remote-claude-hooks.sh, run there.
/// </summary>
/// <remarks>
/// Each hook runs curl.exe directly (exec form, no shell, so it works without Git Bash) and
/// asynchronously, so it never slows Claude down and a stopped companion costs nothing.
/// Everything else in the settings file is left exactly as it was.
/// </remarks>
static class ClaudeHooks
{
    static readonly string[] Events =
        ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd"];

    static string Url => $"http://localhost:{LocalServer.Port}/claude";

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
        Save(root);
        Log.Write($"hooks: installed in {SettingsPath}");
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
        Save(root);
        Log.Write($"hooks: removed from {SettingsPath}");
    }

    static JsonObject Entry(string name)
    {
        var hook = new JsonObject
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
        entry["hooks"] = new JsonArray(hook);
        return entry;
    }

    static void RemoveOurs(JsonArray list)
    {
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i]?["hooks"] is JsonArray hs &&
                hs.Any(h => h?["args"] is JsonArray a && a.Any(x => x?.GetValue<string>() == Url)))
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
