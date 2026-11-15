using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// Optional settings in %LOCALAPPDATA%\flex-screen\settings.json, re-read when the file
/// changes. Everything has a default, so the file only needs what differs:
/// <code>
/// {
///   "palette": "base",                                        // the deck's colors: "base", "vibrant", "ember" or "violet"
///   "idle": { "clockAfterMinutes": 10, "screenOffAfterMinutes": 30 },  // 0 turns either off
///   "layerNames": ["BASE", "NAV", "SYMBOL"],                  // the keyboard's layers, from 0
///   "cheatSheetSeconds": 10,                                  // a layer key held this long shows the cheat sheet; 0: only from the pill
///   "hiddenTaskLists": ["google/MTIzNDU2"],                   // task lists left off the deck (the tray's Task lists on the deck)
///   "obsidianVault": "C:\\Users\\me\\Notes\\MyVault",           // if it can't be found by itself
///   "obsidianTasks": true,                                    // the latest daily note's "- [ ]" tasks on the deck (on unless false)
///   "microsoftTodo": { "clientId": "…", "tenant": "common" },   // the tray's Task sources › Microsoft To Do sets these
///   "jira": { "url": "https://acme.atlassian.net", "email": "me@acme.com", "jql": "…", "name": "Jira" },  // Task sources › Jira
///   "github": { "host": "github.com", "query": "is:open is:issue assignee:@me", "name": "GitHub" },     // Task sources › GitHub
///   "shortcuts": [ { "name": "Gmail", "url": "https://mail.google.com" }, { "name": "Spotify", "app": "Spotify" } ]
/// }
/// </code>
/// </summary>
static class Settings
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "flex-screen", "settings.json");

    static readonly string[] DefaultLayerNames = ["BASE", "NAV", "SYMBOL"];
    static readonly object Gate = new();
    static JsonNode? _json;
    static DateTime _readTime = DateTime.MinValue;

    public static IReadOnlyList<string> LayerNames =>
        (Get("layerNames") as JsonArray)?.Select(n => (string?)n ?? "").ToArray() is { Length: > 0 } names ? names : DefaultLayerNames;

    public static string? ObsidianVault => (string?)Get("obsidianVault") is { Length: > 0 } path ? path : null;

    /// <summary>Whether the latest Obsidian daily note's tasks are a task source (on unless set false).</summary>
    public static bool ObsidianTasks => Get("obsidianTasks") is JsonValue v && v.TryGetValue(out bool on) ? on : true;

    /// <summary>A task source's own settings ("jira", "github", "microsoftTodo"), or null when it isn't set up.</summary>
    public static JsonObject? Source(string key) => Get(key) as JsonObject;

    /// <summary>The launcher's tiles as written (see <see cref="FlexScreen.Companion.Shortcuts"/>); null when the file has none.</summary>
    public static JsonArray? Shortcuts => Get("shortcuts") as JsonArray;

    /// <summary>Whether a Claude permission request waits for Approve or Deny on the deck (on unless set false).</summary>
    public static bool ApproveFromDeck => Get("approveFromDeck") is JsonValue v && v.TryGetValue(out bool on) ? on : true;

    /// <summary>The context bar's own key sets, or false (see <see cref="FlexScreen.Companion.ContextBar"/>); null when the file says nothing.</summary>
    public static JsonNode? ContextBar => Get("contextBar");

    /// <summary>The deck's color theme: what the tray's Deck colors menu offers.</summary>
    public static readonly (string Name, string Label)[] Palettes = [("base", "Base"), ("vibrant", "Vibrant"), ("ember", "Ember"), ("violet", "Violet")];

    public static string Palette => (string?)Get("palette") is { } name && Palettes.Any(p => p.Name == name) ? name : "base";

    /// <summary>Idle minutes before the deck shows its clock, and before its screen goes off.</summary>
    public static (double Clock, double Off) Idle =>
        ((double?)Get("idle")?["clockAfterMinutes"] ?? 10, (double?)Get("idle")?["screenOffAfterMinutes"] ?? 30);

    /// <summary>How long a layer key is held before the deck shows the cheat sheet; 0: only when the pill is tapped.</summary>
    public static double CheatSheetSeconds => Math.Clamp((double?)Get("cheatSheetSeconds") ?? 10, 0, 600);

    /// <summary>
    /// Task lists kept off the deck's Tasks card ("source/list id"), set from the tray's Task
    /// lists on the deck menu. Their dated tasks still show under Scheduled.
    /// </summary>
    public static IReadOnlySet<string> HiddenTaskLists =>
        (Get("hiddenTaskLists") as JsonArray)?.Select(n => (string?)n ?? "").ToHashSet() ?? [];

    /// <summary>Changes when the file does, so the deck can be sent the new settings.</summary>
    public static DateTime Version
    {
        get
        {
            Get("palette");
            lock (Gate) return _readTime;
        }
    }

    /// <summary>
    /// Writes the everyday settings the file doesn't have, at their current values (the
    /// defaults), so the tray's Open settings file shows them. Keeps everything else.
    /// </summary>
    public static void AddMissing()
    {
        var (clock, off) = Idle;
        (string Key, Func<JsonNode> Value)[] everyday =
        [
            ("palette", () => JsonValue.Create(Palette)),
            ("idle", () => new JsonObject { ["clockAfterMinutes"] = clock, ["screenOffAfterMinutes"] = off }),
            ("layerNames", () => new JsonArray(LayerNames.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray())),
            ("cheatSheetSeconds", () => JsonValue.Create(CheatSheetSeconds)),
        ];
        foreach (var (key, value) in everyday)
        {
            if (Get(key) == null) Set(key, value());
        }
    }

    /// <summary>Sets one top-level value, keeping the rest of the file.</summary>
    public static void Set(string key, JsonNode? value)
    {
        lock (Gate)
        {
            var root = (File.Exists(FilePath) ? JsonNode.Parse(File.ReadAllText(FilePath),
                documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject : null) ?? new JsonObject();
            root[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }

    static JsonNode? Get(string key)
    {
        lock (Gate)
        {
            try
            {
                var time = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
                if (time != _readTime)
                {
                    _readTime = time;
                    _json = time == DateTime.MinValue ? null : JsonNode.Parse(File.ReadAllText(FilePath),
                        documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
                }
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
            {
                Log.Write($"settings: {e.Message}");
                _json = null;
            }
            return _json?[key];
        }
    }
}
