using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <param name="Key">What makes two actions the same one: "win:&lt;hwnd&gt;", "obs:today", "term:&lt;cmd&gt;"…</param>
/// <param name="Kind">How the deck draws it: window, note, command, meeting, mail, claude, device.</param>
/// <param name="Name">The tile's first line: the app, or what kind of thing it is.</param>
/// <param name="Title">Its second line: the window's title, the note, the command…</param>
/// <param name="Icon">A window's icon id ("" for the rest).</param>
/// <param name="Act">The deck action that runs it again (a window's also holds its exe, for once it's closed).</param>
sealed record RecentAction(string Key, string Kind, string Name, string Title, string Icon, JsonObject Act)
{
    /// <summary>A short id for the deck, the same for the same action.</summary>
    public string Id => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Key)))[..10].ToLowerInvariant();

    public bool ShowsSame(RecentAction other) =>
        Kind == other.Kind && Name == other.Name && Title == other.Title && Icon == other.Icon &&
        JsonNode.DeepEquals(Act, other.Act);
}

/// <summary>
/// The Today page's Recent card: the last few things done from the deck that can be done
/// again (a window brought up, a note opened, a command run…), most recent first, each
/// once: doing one that's already listed moves it to the front instead of adding it
/// again, and the oldest drops off. Kept across restarts, DPAPI-encrypted for the Windows
/// user, since window titles and commands can be private.
/// </summary>
sealed class RecentActions
{
    public const int Max = 5;

    static readonly string FilePath = Path.Combine(AppPaths.Data, "recent-actions.bin");

    readonly object _gate = new();
    readonly List<RecentAction> _list = Load();

    public IReadOnlyList<RecentAction> Current
    {
        get
        {
            lock (_gate) return _list.ToList();
        }
    }

    /// <summary>Raised on the caller's thread when the list or what a tile shows changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// Puts an action first. One already listed (by key, or one <paramref name="replaces"/>
    /// picks, such as a closed window of the same app) moves there rather than appearing twice.
    /// </summary>
    public void Note(RecentAction action, Func<RecentAction, bool>? replaces = null)
    {
        lock (_gate)
        {
            var i = _list.FindIndex(a => a.Key == action.Key);
            if (i < 0 && replaces != null) i = _list.FindIndex(a => replaces(a));
            if (i == 0 && _list[0].Key == action.Key && _list[0].ShowsSame(action)) return;
            if (i >= 0) _list.RemoveAt(i);
            _list.Insert(0, action);
            if (_list.Count > Max) _list.RemoveRange(Max, _list.Count - Max);
            Save(_list);
        }
        Changed?.Invoke();
    }

    public RecentAction? Find(string id)
    {
        lock (_gate) return _list.FirstOrDefault(a => a.Id == id);
    }

    static List<RecentAction> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var json = JsonNode.Parse(Encoding.UTF8.GetString(ProtectedBytes.Unprotect(File.ReadAllBytes(FilePath))));
            return (json as JsonArray ?? [])
                .Select(n => n is JsonObject o && o["act"] is JsonObject act
                    ? new RecentAction((string?)o["key"] ?? "", (string?)o["kind"] ?? "", (string?)o["name"] ?? "",
                                       (string?)o["title"] ?? "", (string?)o["icon"] ?? "", (JsonObject)act.DeepClone())
                    : null)
                .OfType<RecentAction>()
                .Where(a => a.Key.Length > 0)
                .Take(Max)
                .ToList();
        }
        catch (Exception e) when (e is IOException or CryptographicException or JsonException)
        {
            Log.Write($"recent: can't read the saved list: {e.Message}");
            return [];
        }
    }

    static void Save(List<RecentAction> list)
    {
        try
        {
            var json = new JsonArray(list.Select(a => (JsonNode)new JsonObject
            {
                ["key"] = a.Key, ["kind"] = a.Kind, ["name"] = a.Name, ["title"] = a.Title, ["icon"] = a.Icon,
                ["act"] = a.Act.DeepClone(),
            }).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllBytes(FilePath, ProtectedBytes.Protect(Encoding.UTF8.GetBytes(json.ToJsonString())));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Log.Write($"recent: can't save the list: {e.Message}");
        }
    }
}
