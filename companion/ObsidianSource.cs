using System.Diagnostics;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <param name="Path">Relative to the vault, with forward slashes ("Daily Log/2026-11-07.md").</param>
record ObsidianNote(string Name, string Path);

record ObsidianState(bool Found, string Vault, IReadOnlyList<ObsidianNote> Recent);

/// <summary>
/// The Obsidian vault: today's note and recently opened notes. The vault is settings.json's
/// "obsidianVault", else the one Obsidian has open. If that folder moved (Google Drive
/// reorganizes folders), a folder of the same name is looked for near where it was, and
/// Obsidian's own vault list is pointed at it, since Obsidian can't open notes otherwise.
/// </summary>
sealed class ObsidianSource
{
    const int MaxRecent = 5;
    static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);
    static readonly string Registry = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obsidian", "obsidian.json");

    readonly System.Threading.Timer _timer;
    string? _root;
    string _key = "";

    public ObsidianState Current { get; private set; } = new(false, "", []);

    /// <summary>Raised on a timer thread when the vault or its recent notes change.</summary>
    public event Action<ObsidianState>? Changed;

    public event Action<Toast>? Toast;

    public ObsidianSource()
    {
        _timer = new System.Threading.Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(TimeSpan.Zero, Poll);

    void Refresh()
    {
        try
        {
            if (_root == null || !Directory.Exists(Path.Combine(_root, ".obsidian"))) _root = FindVault();
            if (_root != null) RepairRegistry(_root);
            var state = _root == null ? new ObsidianState(false, "", []) : new ObsidianState(true, Path.GetFileName(_root), Recent(_root));
            var key = $"{state.Found}|{state.Vault}|{string.Join("|", state.Recent.Select(n => n.Path))}";
            if (key == _key) return;
            _key = key;
            Current = state;
            Log.Write(state.Found ? $"obsidian: vault {_root}" : "obsidian: no vault found");
            Changed?.Invoke(state);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log.Write($"obsidian: {e.Message}");
        }
    }

    /// <summary>The vault's folder, or null while none is found.</summary>
    public string? Root => _root;

    /// <summary>Today's daily note, as the Daily notes plugin names it; created if it isn't there yet.</summary>
    public void OpenToday()
    {
        if (_root == null || !ObsidianKnowsVault()) return;
        Open(CreateDailyNote(_root, DateTime.Now));
    }

    /// <summary>The Daily notes plugin's folder, file name format (Moment's) and template.</summary>
    static (string Folder, string Format, string? Template) DailySettings(string root)
    {
        var settings = ReadJson(Path.Combine(root, ".obsidian", "daily-notes.json"));
        var template = (string?)settings?["template"] is { Length: > 0 } t ? Path.Combine(root, t.EndsWith(".md") ? t : t + ".md") : null;
        return ((string?)settings?["folder"] ?? "", (string?)settings?["format"] is { Length: > 0 } f ? f : "YYYY-MM-DD", template);
    }

    static string DailyNotePath(string root, DateTime day, (string Folder, string Format, string? Template) daily) =>
        Path.Combine(root, daily.Folder, MomentFormat(day, daily.Format) + ".md");

    /// <summary>The note for <paramref name="day"/>, made from the plugin's template if it isn't there yet.</summary>
    public static string CreateDailyNote(string root, DateTime day)
    {
        var daily = DailySettings(root);
        var file = DailyNotePath(root, day, daily);
        if (File.Exists(file)) return file;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, daily.Template != null && File.Exists(daily.Template) ? File.ReadAllText(daily.Template) : "");
        return file;
    }

    /// <summary>The newest daily note up to today (looking back a quarter of a year), or null.</summary>
    public static string? LatestDailyNote(string root)
    {
        var daily = DailySettings(root);
        for (var day = DateTime.Today; day > DateTime.Today.AddDays(-92); day = day.AddDays(-1))
        {
            var file = DailyNotePath(root, day, daily);
            if (File.Exists(file)) return file;
        }
        return null;
    }

    /// <summary>Opens a note of the vault (a full path) in Obsidian.</summary>
    public void OpenFile(string file)
    {
        if (_root == null || !ObsidianKnowsVault()) return;
        if (!Path.GetFullPath(file).StartsWith(_root, StringComparison.OrdinalIgnoreCase)) return;
        Open(file);
    }

    public void OpenNote(string relative)
    {
        if (_root == null || !ObsidianKnowsVault()) return;
        var file = Path.GetFullPath(Path.Combine(_root, relative));
        if (!file.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) return;  // only notes in the vault
        Open(file);
    }

    // By path, which works whichever name Obsidian knows the vault by.
    void Open(string file)
    {
        Launch($"obsidian://open?path={Uri.EscapeDataString(file)}");
        WindowFocus.Focus("", "Obsidian");
    }

    void Launch(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Toast?.Invoke(new Toast("error", "Obsidian isn't installed"));
        }
    }

    /// <summary>
    /// Obsidian finds a vault only by its own list. After a move that list points at the old
    /// folder: fix it, but only while Obsidian is closed, since it keeps the list in memory
    /// and writes it back. Adds the vault if Obsidian has never had it.
    /// </summary>
    void RepairRegistry(string root)
    {
        var json = ReadJson(Registry) as JsonObject;
        if (json?["vaults"] is not JsonObject vaults) return;
        var name = Path.GetFileName(root);
        var known = vaults.Any(v => SamePath((string?)v.Value?["path"], root));
        if (known) return;
        if (Process.GetProcessesByName("Obsidian").Length > 0) return;  // try again once it's closed
        var stale = vaults.FirstOrDefault(v => (string?)v.Value?["path"] is { } p && !Directory.Exists(p) &&
                                               Path.GetFileName(p.TrimEnd('\\', '/')) == name);
        if (stale.Value is JsonObject entry) entry["path"] = root;
        else vaults[Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()] =
            new JsonObject { ["path"] = root, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        var temp = Registry + ".flex-screen.tmp";
        File.WriteAllText(temp, json.ToJsonString());
        File.Move(temp, Registry, overwrite: true);
        Log.Write($"obsidian: pointed Obsidian's vault list at {root}");
    }

    /// <summary>False (with a toast) while Obsidian still has the vault's old folder and is open.</summary>
    bool ObsidianKnowsVault()
    {
        var vaults = ReadJson(Registry)?["vaults"] as JsonObject;
        if (vaults == null || vaults.Any(v => SamePath((string?)v.Value?["path"], _root!))) return true;
        RepairRegistry(_root!);
        if ((ReadJson(Registry)?["vaults"] as JsonObject ?? []).Any(v => SamePath((string?)v.Value?["path"], _root!))) return true;
        Toast?.Invoke(new Toast("attention", "Obsidian has the vault's old folder. Close Obsidian once, then tap again"));
        return false;
    }

    static bool SamePath(string? a, string b) =>
        a != null && string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    static List<ObsidianNote> Recent(string root)
    {
        var workspace = ReadJson(Path.Combine(root, ".obsidian", "workspace.json"));
        return (workspace?["lastOpenFiles"] as JsonArray ?? [])
            .Select(n => (string?)n ?? "")
            .Where(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(root, p)))
            .Take(MaxRecent)
            .Select(p => new ObsidianNote(Path.GetFileNameWithoutExtension(p), p))
            .ToList();
    }

    /// <summary>The open vault from Obsidian's own list, or one that still exists; if its folder moved, a same-named one near where it was.</summary>
    static string? FindVault()
    {
        if (Settings.ObsidianVault is { } chosen && Directory.Exists(Path.Combine(chosen, ".obsidian"))) return chosen;
        var vaults = (ReadJson(Registry)?["vaults"] as JsonObject ?? [])
            .Select(v => (Path: (string?)v.Value?["path"] ?? "", Open: (bool?)v.Value?["open"] ?? false))
            .Where(v => v.Path.Length > 0)
            .OrderByDescending(v => v.Open)
            .ToList();
        foreach (var v in vaults)
        {
            if (Directory.Exists(Path.Combine(v.Path, ".obsidian"))) return v.Path;
        }
        foreach (var v in vaults)
        {
            var name = Path.GetFileName(v.Path.TrimEnd('\\', '/'));
            for (var dir = Path.GetDirectoryName(v.Path); dir != null; dir = Path.GetDirectoryName(dir))
            {
                if (!Directory.Exists(dir)) continue;
                var found = Search(dir, name, depth: 3);
                if (found != null) return found;
                break;  // only under the nearest folder that still exists
            }
        }
        return null;
    }

    static string? Search(string dir, string name, int depth)
    {
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (Path.GetFileName(sub) == name && Directory.Exists(Path.Combine(sub, ".obsidian"))) return sub;
            }
            if (depth == 0) return null;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (Path.GetFileName(sub).StartsWith('.') || Path.GetFileName(sub).StartsWith('$')) continue;
                if (Search(sub, name, depth - 1) is { } found) return found;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable folder; skip it.
        }
        return null;
    }

    internal static JsonNode? ReadJson(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The Moment.js date tokens daily-note formats use, in .NET terms.</summary>
    static string MomentFormat(DateTime date, string format)
    {
        var map = new (string Moment, string Net)[]
        {
            ("YYYY", "yyyy"), ("YY", "yy"), ("MMMM", "MMMM"), ("MMM", "MMM"), ("MM", "MM"), ("M", "%M"),
            ("DD", "dd"), ("D", "%d"), ("dddd", "dddd"), ("ddd", "ddd"),
        };
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < format.Length;)
        {
            if (format[i] == '[' && format.IndexOf(']', i) is > 0 and var close)
            {
                result.Append(format, i + 1, close - i - 1);  // Moment's escaped text
                i = close + 1;
                continue;
            }
            if (string.CompareOrdinal(format, i, "Do", 0, 2) == 0)
            {
                var d = date.Day;
                result.Append(d).Append(d is 11 or 12 or 13 ? "th" : (d % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
                i += 2;
                continue;
            }
            var token = map.FirstOrDefault(t => string.CompareOrdinal(format, i, t.Moment, 0, t.Moment.Length) == 0);
            if (token.Moment != null)
            {
                result.Append(date.ToString(token.Net, System.Globalization.CultureInfo.InvariantCulture));  // Moment's names are English
                i += token.Moment.Length;
            }
            else
            {
                result.Append(format[i++]);
            }
        }
        return result.ToString();
    }
}
