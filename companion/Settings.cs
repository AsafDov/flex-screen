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
///   "obsidianVault": "C:\\Users\\me\\Notes\\MyVault"            // if it can't be found by itself
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

    /// <summary>The deck's color theme: what the tray's Deck colors menu offers.</summary>
    public static readonly (string Name, string Label)[] Palettes = [("base", "Base"), ("vibrant", "Vibrant"), ("ember", "Ember"), ("violet", "Violet")];

    public static string Palette => (string?)Get("palette") is { } name && Palettes.Any(p => p.Name == name) ? name : "base";

    /// <summary>Idle minutes before the deck shows its clock, and before its screen goes off.</summary>
    public static (double Clock, double Off) Idle =>
        ((double?)Get("idle")?["clockAfterMinutes"] ?? 10, (double?)Get("idle")?["screenOffAfterMinutes"] ?? 30);

    /// <summary>Changes when the file does, so the deck can be sent the new settings.</summary>
    public static DateTime Version
    {
        get
        {
            Get("palette");
            lock (Gate) return _readTime;
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
