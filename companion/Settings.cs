using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// The companion's settings, in two layers over the built-in defaults:
/// <list type="bullet">
/// <item>A profile, profiles\&lt;name&gt;.json: who you are and what the deck shows (colors,
/// shortcuts, context bar keys, accounts, task sources and their choices). Nothing in it is
/// tied to one PC, so the profiles folder can be a synced one (OneDrive, Dropbox, a git repo)
/// and every PC reads the same profiles. A PC can have several (work, home) and switch.</item>
/// <item>This PC's machine.json: which profile it uses, where the profiles are, and what only
/// holds on this PC (the Obsidian vault's folder). Any setting written here wins over the
/// profile's, on this PC only; <c>"jira": null</c> turns the profile's Jira off here.</item>
/// </list>
/// Sign-ins are never in either: they're in <see cref="Secrets"/>, per PC. Both files are in
/// %LOCALAPPDATA%\flex-screen (see <see cref="AppPaths"/>), re-read when they change, and
/// only need what differs from the defaults. The settings.json of companions before
/// profiles becomes profiles\default.json and machine.json the first time this one starts.
/// <code>
/// machine.json:
/// {
///   "profile": "work",                                         // profiles\work.json; "default" when not set
///   "profilesFolder": "C:\\Users\\me\\OneDrive\\flex-screen",   // where the profiles are; else %LOCALAPPDATA%\flex-screen\profiles
///   "obsidianVault": "C:\\Users\\me\\Notes\\MyVault"            // Task sources › Obsidian daily note picks it; else the vault Obsidian has open
/// }
///
/// profiles\work.json:
/// {
///   "palette": "base",                                        // the deck's colors: "base", "vibrant", "ember" or "violet"
///   "idle": { "clockAfterMinutes": 10, "screenOffAfterMinutes": 30 },  // 0 turns either off
///   "layerNames": ["BASE", "NAV", "SYMBOL"],                  // the keyboard's layers, from 0
///   "cheatSheetSeconds": 10,                                  // a layer key held this long shows the cheat sheet; 0: only from the pill
///   "accounts": {                                             // Google sign-ins, by id; without it, one: "google"
///     "google": { "type": "google", "label": "Personal" },
///     "google-work": { "type": "google", "label": "Work", "client": "google-client-work.json", "tasks": false }
///   },
///   "hiddenTaskLists": ["google/MTIzNDU2"],                   // task lists left off the deck (the tray's Task lists on the deck)
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
    public const string DefaultProfile = "default";

    /// <summary>The profile files' JSON schema, for completion and checks in editors that read "$schema".</summary>
    public const string SchemaUrl = "https://raw.githubusercontent.com/AsafDov/flex-screen/master/companion/profile.schema.json";

    public static readonly string MachinePath = Path.Combine(AppPaths.Data, "machine.json");
    static readonly string LegacyPath = Path.Combine(AppPaths.Data, "settings.json");
    static readonly string DefaultProfilesFolder = Path.Combine(AppPaths.Data, "profiles");

    /// <summary>This PC's own: always in machine.json, never in a profile.</summary>
    static readonly HashSet<string> MachineKeys = ["profile", "profilesFolder", "obsidianVault"];

    static readonly string[] DefaultLayerNames = ["BASE", "NAV", "SYMBOL"];
    static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    const long LookEveryMs = 500;  // how often the files' times are looked at, at most
    static readonly TimeSpan RecentFor = TimeSpan.FromSeconds(2);  // a file written this lately is read again each look

    static readonly object Gate = new();
    static JsonObject _machine = [], _profile = [];
    static DateTime _machineTime = DateTime.MinValue, _profileTime = DateTime.MinValue;
    static string? _machineText, _profileText;  // as last read; null for no file
    static string _profilePath = "";
    static long _lookedAt;  // 0: look now
    static long _version;
    static bool _migrated;
    static DateTime _unmigratable = DateTime.MinValue;  // settings.json's time when it last couldn't be read

    public static IReadOnlyList<string> LayerNames =>
        (Get("layerNames") as JsonArray)?.Select(n => (string?)n ?? "").ToArray() is { Length: > 0 } names ? names : DefaultLayerNames;

    public static string? ObsidianVault => (string?)Get("obsidianVault") is { Length: > 0 } path ? path : null;

    /// <summary>Whether the latest Obsidian daily note's tasks are a task source (on unless set false).</summary>
    public static bool ObsidianTasks => Get("obsidianTasks") is JsonValue v && v.TryGetValue(out bool on) ? on : true;

    /// <summary>A task source's own settings ("jira", "github", "microsoftTodo"), or null when it isn't set up.</summary>
    public static JsonObject? Source(string key) => Get(key) as JsonObject;

    /// <summary>The launcher's tiles as written (see <see cref="FlexScreen.Companion.Shortcuts"/>); null when the settings have none.</summary>
    public static JsonArray? Shortcuts => Get("shortcuts") as JsonArray;

    /// <summary>Whether a Claude permission request waits for Approve or Deny on the deck (on unless set false).</summary>
    public static bool ApproveFromDeck => Get("approveFromDeck") is JsonValue v && v.TryGetValue(out bool on) ? on : true;

    /// <summary>The context bar's own key sets, or false (see <see cref="FlexScreen.Companion.ContextBar"/>); null when the settings say nothing.</summary>
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

    /// <summary>Changes whenever the settings do (either file, or another profile), so the deck can be sent the new ones.</summary>
    public static long Version
    {
        get
        {
            lock (Gate)
            {
                Load();
                return _version;
            }
        }
    }

    // ---- profiles

    /// <summary>The profile in use on this PC.</summary>
    public static string Profile
    {
        get
        {
            lock (Gate)
            {
                Load();
                return ProfileOf(_machine);
            }
        }
    }

    /// <summary>The folder the profiles are in: machine.json's "profilesFolder", else %LOCALAPPDATA%\flex-screen\profiles.</summary>
    public static string ProfilesFolder
    {
        get
        {
            lock (Gate)
            {
                Load();
                return FolderOf(_machine);
            }
        }
    }

    /// <summary>The file of the profile in use (it may not exist yet: a profile with nothing set).</summary>
    public static string ProfilePath => Path.Combine(ProfilesFolder, Profile + ".json");

    /// <summary>The file a setting is in, or would be written to: machine.json for this PC's own and the ones it overrides, else the profile's.</summary>
    public static string FileOf(string key)
    {
        lock (Gate)
        {
            Load();
            return MachineKeys.Contains(key) || _machine.ContainsKey(key) ? MachinePath : ProfilePathOf(_machine);
        }
    }

    /// <summary>Every profile in the profiles folder, and the one in use even if it has no file yet.</summary>
    public static IReadOnlyList<string> Profiles
    {
        get
        {
            var folder = ProfilesFolder;
            var names = new List<string>();
            try
            {
                if (Directory.Exists(folder))
                    names.AddRange(Directory.GetFiles(folder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Where(IsProfileName));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Write($"settings: can't list the profiles: {e.Message}");
            }
            var current = Profile;
            if (!names.Contains(current, StringComparer.OrdinalIgnoreCase)) names.Add(current);
            return names.Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>A name a profile file can have: letters, digits, spaces, "-" and "_", up to 40.</summary>
    public static bool IsProfileName(string name) =>
        name.Length is > 0 and <= 40 && name.Trim() == name && name.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_');

    /// <summary>Uses that profile on this PC from now on.</summary>
    public static void UseProfile(string name)
    {
        if (!IsProfileName(name)) throw new ArgumentException($"\"{name}\" isn't a profile name");
        Set("profile", name == DefaultProfile ? null : name);
        Log.Write($"settings: profile {name}");
    }

    /// <summary>A new profile, a copy of the one in use or empty (the defaults), used from now on. False if it exists already.</summary>
    public static bool CreateProfile(string name, bool copyCurrent)
    {
        if (!IsProfileName(name)) throw new ArgumentException($"\"{name}\" isn't a profile name");
        lock (Gate)
        {
            Load(force: true);
            var path = Path.Combine(FolderOf(_machine), name + ".json");
            if (File.Exists(path)) return false;
            var profile = copyCurrent ? (JsonObject)_profile.DeepClone() : [];
            profile.Remove("$schema");
            Write(path, WithSchema(profile));
        }
        UseProfile(name);
        return true;
    }

    /// <summary>
    /// Keeps the profiles in <paramref name="folder"/> from now on (null: %LOCALAPPDATA%\flex-screen\profiles).
    /// The ones there are used as they are; the others are copied in (and left where they were).
    /// </summary>
    public static void UseProfilesFolder(string? folder)
    {
        var target = folder == null ? DefaultProfilesFolder : Path.GetFullPath(folder);
        var source = ProfilesFolder;
        if (string.Equals(source.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(target);
        if (Directory.Exists(source))
        {
            foreach (var file in Directory.GetFiles(source, "*.json"))
            {
                var copy = Path.Combine(target, Path.GetFileName(file));
                if (!File.Exists(copy)) File.Copy(file, copy);
            }
        }
        Set("profilesFolder", folder == null ? null : target);
        Log.Write($"settings: profiles in {target}");
    }

    // ---- accounts

    /// <summary>
    /// The profile's accounts of a type ("google"), each with its settings: "accounts", by id,
    /// in the order written. An account without a "type" is of the type its id starts with
    /// ("google-work"). A profile without "accounts" has one Google account, "google": the
    /// sign-in from before accounts.
    /// </summary>
    public static IReadOnlyList<(string Id, JsonObject Config)> Accounts(string type)
    {
        if (Get("accounts") is not JsonObject all) return type == "google" ? [("google", new JsonObject())] : [];
        return all.Where(a => a.Value is JsonObject && IsAccountId(a.Key) && TypeOf(a.Key, (JsonObject)a.Value) == type)
                  .Select(a => (a.Key, (JsonObject)a.Value!)).ToList();
    }

    /// <summary>Lower-case letters, digits and "-", up to 40: an account's id is in its sign-in's file name.</summary>
    public static bool IsAccountId(string id) => id.Length is > 0 and <= 40 && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    /// <summary>Adds an account to the profile (or replaces the one with that id).</summary>
    public static void SetAccount(string id, JsonObject config)
    {
        if (!IsAccountId(id)) throw new ArgumentException($"\"{id}\" isn't an account id");
        var all = AccountsForWriting();
        all[id] = config.DeepClone();
        Set("accounts", all);
    }

    /// <summary>Takes an account out of the profile (its sign-in on this PC is the caller's to delete).</summary>
    public static void RemoveAccount(string id)
    {
        var all = AccountsForWriting();
        if (all.Remove(id)) Set("accounts", all);
    }

    /// <summary>What the accounts are now, to tell when they change (by hand, or another profile).</summary>
    public static string AccountsStamp => $"{Profile}|{Get("accounts")?.ToJsonString()}";

    /// <summary>The accounts as they are, with the implicit Google one written out when there are none.</summary>
    static JsonObject AccountsForWriting() =>
        Get("accounts")?.DeepClone() as JsonObject ?? new JsonObject { ["google"] = new JsonObject { ["type"] = "google" } };

    static string TypeOf(string id, JsonObject config) => (string?)config["type"] ?? id.Split('-')[0];

    // ---- reading and writing

    /// <summary>
    /// Writes the everyday settings the profile doesn't have, at their current values (the
    /// defaults), so the tray's Open profile file shows them. Keeps everything else.
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
        // A profile that doesn't exist yet (nothing was ever set) is written now, so it opens.
        lock (Gate)
        {
            if (!File.Exists(ProfilePathOf(_machine))) Write(ProfilePathOf(_machine), WithSchema([]));
        }
    }

    /// <summary>machine.json written (with nothing in it) if this PC has none yet, so the tray's Open this PC's settings opens it.</summary>
    public static void AddMachineFile()
    {
        lock (Gate)
        {
            Load(force: true);
            if (!File.Exists(MachinePath)) Write(MachinePath, []);
        }
    }

    /// <summary>
    /// Sets one top-level value, keeping the rest of the file: in machine.json for this PC's
    /// own settings and for any machine.json already sets (it would win anyway), else in the
    /// profile. Null takes the setting out (in machine.json, an override it holds stays, as
    /// null: off on this PC). A file that has a mistake in it is left alone.
    /// </summary>
    public static void Set(string key, JsonNode? value)
    {
        lock (Gate)
        {
            Load(force: true);
            var machine = MachineKeys.Contains(key) || _machine.ContainsKey(key);
            var path = machine ? MachinePath : ProfilePathOf(_machine);
            JsonObject root;
            try
            {
                root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) as JsonObject ?? [] : [];
            }
            catch (Exception e) when (e is IOException or JsonException)
            {
                Log.Write($"settings: {key} not saved: {Path.GetFileName(path)} can't be read ({e.Message})");
                return;
            }
            if (value == null && (!machine || MachineKeys.Contains(key))) root.Remove(key);
            else root[key] = value?.DeepClone();
            Write(path, machine ? root : WithSchema(root));
            Load(force: true);  // read back at once
        }
    }

    static JsonNode? Get(string key)
    {
        lock (Gate)
        {
            Load();
            return _machine.TryGetPropertyValue(key, out var mine) ? mine : MachineKeys.Contains(key) ? null : _profile[key];
        }
    }

    /// <summary>Reads the files again if either changed (or another profile is in use). Under the gate.</summary>
    static void Load(bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && _lookedAt != 0 && now - _lookedAt < LookEveryMs) return;
        _lookedAt = now;
        Migrate();
        var changed = Look(MachinePath, ref _machineTime, ref _machineText, ref _machine);
        var profilePath = ProfilePathOf(_machine);
        if (profilePath != _profilePath)
        {
            // Another profile starts from nothing if its file can't be read; the same one keeps
            // what it had (a synced folder can be caught mid-write).
            _profilePath = profilePath;
            _profileTime = DateTime.MinValue;
            _profileText = null;
            _profile = [];
            changed = true;
        }
        changed |= Look(profilePath, ref _profileTime, ref _profileText, ref _profile);
        if (changed) _version++;
    }

    /// <summary>
    /// Reads the file again when its time moved, and while that time is recent: a file's time
    /// only moves every 15 ms or so, so two writes close together (the companion's and a sync
    /// client's) can look like one. True when what it says changed; when it can't be read, the
    /// settings it had stay.
    /// </summary>
    static bool Look(string path, ref DateTime time, ref string? text, ref JsonObject settings)
    {
        var now = TimeOf(path);
        if (now == time && DateTime.UtcNow - now > RecentFor) return false;
        time = now;
        string? read;
        try
        {
            read = File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"settings: {Path.GetFileName(path)}: {e.Message}");
            time = DateTime.MinValue;  // try again next time
            return false;
        }
        if (read == text) return false;
        text = read;
        if (read == null)
        {
            settings = [];
            return true;
        }
        try
        {
            settings = JsonNode.Parse(read, documentOptions: ReadOptions) as JsonObject ?? [];
        }
        catch (JsonException e)
        {
            Log.Write($"settings: {Path.GetFileName(path)}: {e.Message}");
        }
        return true;
    }

    static DateTime TimeOf(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>Whole, or not at all: written beside and moved over, so a reader (or a sync) never sees half a file.</summary>
    static void Write(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = root.ToJsonString(WriteOptions);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"settings: {Path.GetFileName(path)} written in place ({e.Message})");  // a sync client holding it, say
            File.WriteAllText(path, text);
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
        }
    }

    static JsonObject WithSchema(JsonObject profile)
    {
        if (profile.ContainsKey("$schema")) return profile;
        var ordered = new JsonObject { ["$schema"] = SchemaUrl };
        foreach (var (key, value) in profile.ToList())
        {
            profile.Remove(key);
            ordered[key] = value;
        }
        return ordered;
    }

    static string ProfileOf(JsonObject machine) => (string?)machine["profile"] is { } name && IsProfileName(name) ? name : DefaultProfile;

    static string FolderOf(JsonObject machine) =>
        (string?)machine["profilesFolder"] is { Length: > 0 } folder ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder)) : DefaultProfilesFolder;

    static string ProfilePathOf(JsonObject machine) => Path.Combine(FolderOf(machine), ProfileOf(machine) + ".json");

    /// <summary>
    /// Once: settings.json from before profiles becomes the default profile, with this PC's
    /// own settings (the vault's folder) in machine.json. The old file is kept beside, renamed.
    /// </summary>
    static void Migrate()
    {
        if (_migrated) return;
        if (File.Exists(MachinePath) || !File.Exists(LegacyPath))
        {
            _migrated = true;
            return;
        }
        // One with a mistake in it is tried again once it's saved.
        var time = TimeOf(LegacyPath);
        if (time == _unmigratable) return;
        JsonObject legacy;
        try
        {
            legacy = JsonNode.Parse(File.ReadAllText(LegacyPath), documentOptions: ReadOptions) as JsonObject ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            _unmigratable = time;
            Log.Write($"settings: settings.json not moved into a profile yet: {e.Message}");
            return;
        }
        _migrated = true;
        var machine = new JsonObject();
        var profile = new JsonObject();
        foreach (var (key, value) in legacy.ToList())
        {
            legacy.Remove(key);
            (MachineKeys.Contains(key) ? machine : profile)[key] = value;
        }
        try
        {
            var profilePath = Path.Combine(DefaultProfilesFolder, DefaultProfile + ".json");
            if (!File.Exists(profilePath)) Write(profilePath, WithSchema(profile));
            Write(MachinePath, machine);
            File.Move(LegacyPath, LegacyPath + ".before-profiles", overwrite: true);
            Log.Write("settings: settings.json is now profiles\\default.json and machine.json (the old file is kept as settings.json.before-profiles)");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"settings: settings.json not moved into a profile: {e.Message}");
        }
    }
}
