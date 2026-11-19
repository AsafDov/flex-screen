using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace FlexScreen.Companion;

/// <summary>One app in the mixer. <c>Id</c> is the lower-case process name, or "system".</summary>
record AppVolume(string Id, string Name, int Level, bool Muted);

/// <summary>
/// Per-app volume, like the Windows volume mixer: one level per app. Windows keeps a
/// separate session (and volume) for each output device an app has played on, and its
/// own mixer may show the session on a device the app isn't using, so the deck keeps all
/// of an app's sessions, on every output, at one level: a fader move sets them all, and a
/// change made in Windows (its mixer, or the app itself) to any of them is taken up as the
/// app's level and copied to the rest. Apps are shown if they have a session on the
/// default playback device, and the call apps (Teams, Discord, Zoom) whenever they're running:
/// Windows gives an app a session only once it plays something, so until then their fader
/// holds the level they're to start at, applied to their first session when it comes (and
/// kept in mixer.json, so a restart of the companion doesn't lose it).
/// </summary>
sealed class MixerSource
{
    /// <summary>The deck has room for this many faders next to Master.</summary>
    const int MaxApps = 5;
    const int PollMs = 750;
    const int RunningEveryMs = 3000;  // how often to look for call apps that are open but silent

    /// <summary>Apps with a fader while they run, sound or not (process names, lower case).</summary>
    static readonly HashSet<string> CallApps = new(StringComparer.OrdinalIgnoreCase) { "ms-teams", "teams", "discord", "zoom" };
    static readonly string SavedPath = Path.Combine(AppPaths.Data, "mixer.json");

    static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Chrome",
        ["msedge"] = "Edge",
        ["firefox"] = "Firefox",
        ["spotify"] = "Spotify",
        ["ms-teams"] = "Teams",
        ["teams"] = "Teams",
        ["discord"] = "Discord",
        ["zoom"] = "Zoom",
        ["obs64"] = "OBS",
        ["flow.launcher"] = "Flow Launcher",
    };

    /// <summary>Fader order the user asked for; other apps follow, busiest first, then by name.
    /// Teams right after Spotify (asked for on 2026-11-19), then Chrome.</summary>
    static readonly Dictionary<string, int> FaderOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spotify"] = 0,
        ["ms-teams"] = 1,
        ["teams"] = 1,
        ["chrome"] = 2,
        ["discord"] = 3,
        ["system"] = 4,
        ["flow.launcher"] = 5,
    };

    static int Rank(string id) => FaderOrder.TryGetValue(id, out var rank) ? rank : FaderOrder.Count;

    /// <summary>An app's sessions as read this round, on every output device.</summary>
    sealed class Group
    {
        public required string Name;
        public bool OnDefault;  // has a session on the default device: shown on the deck
        public bool Active;     // playing on the default device right now
        public readonly List<Seen> Sessions = [];
    }

    /// <param name="Key">The device and the session's instance id: the same session from round to round.</param>
    sealed record Seen(string Key, AudioSessionControl Session, bool OnDefault, bool Active, Level Level);

    readonly record struct Level(float Volume, bool Muted)
    {
        public bool Same(Level other) => Muted == other.Muted && Math.Abs(Volume - other.Volume) < 0.005f;
    }

    readonly object _gate = new();
    readonly object _publishGate = new();
    readonly Dictionary<uint, string?> _processIds = new();  // pid → mixer id
    readonly Dictionary<string, string> _names = new();      // mixer id → display name
    readonly Dictionary<string, Level> _levels = new();      // mixer id → the app's one level
    Dictionary<string, Level> _last = new();                 // session key → as last read or set
    readonly Dictionary<string, Level> _saved = new();       // call app → its level as in mixer.json
    HashSet<string> _running = [];                           // call apps open right now
    long _runningAt = -RunningEveryMs;  // so the first round looks
    MMDeviceEnumerator? _enumerator;
    string _key = "";

    public IReadOnlyList<AppVolume> Current { get; private set; } = [];

    /// <summary>Raised on the mixer thread when any app's volume, mute or presence changes.</summary>
    public event Action<IReadOnlyList<AppVolume>>? Changed;

    public void Start() => new Thread(Run) { IsBackground = true, Name = "mixer" }.Start();

    // A fader move or mute wakes the mixer thread rather than reading every device's sessions
    // on the caller's: the deck's messages are read one at a time, so a drag (one message every
    // 60 ms) waited on each read, and on a busy PC the backlog made the fader lag. Moves that
    // arrive while a round runs are applied together, at the latest level, in the next.
    readonly AutoResetEvent _wake = new(false);

    public void SetLevel(string appId, int level)
    {
        lock (_gate) _levels[appId] = new Level(ToAmplitude(level), _levels.TryGetValue(appId, out var was) && was.Muted);
        _wake.Set();
    }

    // The numbers Windows' volume mixer shows: a session's volume as stored, times 100.
    // (Faders used to follow master's audio-tapered curve, level^1.75, so 50% on an app
    // sounded like 50% on master, but then the deck and Windows showed different numbers.)
    static float ToAmplitude(int percent) => Math.Clamp(percent, 0, 100) / 100f;

    static int ToPercent(float amplitude) => (int)Math.Round(Math.Clamp(amplitude, 0f, 1f) * 100);

    public void ToggleMute(string appId)
    {
        lock (_gate)
        {
            if (!_levels.TryGetValue(appId, out var was)) return;
            _levels[appId] = was with { Muted = !was.Muted };
        }
        _wake.Set();
    }

    void Run()
    {
        // Polling instead of per-session callbacks: sessions come and go constantly and a
        // sub-second refresh is plenty for a mixer.
        lock (_gate)
        {
            _enumerator = new MMDeviceEnumerator();
            LoadSaved();
        }
        while (true)
        {
            Poll();
            _wake.WaitOne(PollMs);  // or at once, after a fader move
        }
    }

    void Poll()
    {
        // One at a time, so the deck can't receive an older reading after a newer one.
        lock (_publishGate)
        {
            List<AppVolume> apps;
            try
            {
                lock (_gate) apps = Read();
            }
            catch (COMException)
            {
                apps = [];  // no playback device right now
            }
            var key = string.Join("|", apps.Select(a => $"{a.Id}:{a.Level}:{a.Muted}"));
            if (key == _key) return;
            _key = key;
            Current = apps;
            Changed?.Invoke(apps);
        }
    }

    List<AppVolume> Read()
    {
        if (_enumerator == null) return [];
        var open = new List<IDisposable>();  // devices and sessions, released once this round is done
        try
        {
            var groups = ReadSessions(open);
            // Call apps that are open but have no session on the default device yet: a fader
            // all the same, at the level they're to start at (as saved, else full).
            var waiting = RunningCallApps().Where(id => !(groups.TryGetValue(id, out var g) && g.OnDefault)).ToList();
            foreach (var id in waiting)
            {
                if (!_names.ContainsKey(id)) _names[id] = KnownNames.TryGetValue(id, out var known) ? known : id;
                _levels.TryAdd(id, _saved.TryGetValue(id, out var saved) ? saved : new Level(1f, false));
            }
            var shown = groups
                .Where(g => g.Value.OnDefault)
                .Select(g => (Id: g.Key, g.Value.Name, g.Value.Active))
                .Concat(waiting.Select(id => (Id: id, Name: _names[id], Active: false)))
                .OrderBy(a => Rank(a.Id))
                .ThenByDescending(a => a.Active)
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(MaxApps)
                .Select(a => a.Id)
                .ToList();
            foreach (var id in shown)
            {
                if (groups.TryGetValue(id, out var g)) _levels.TryAdd(id, Heard(g));
            }

            // An app whose level is known gets it on every session, a new one too: that's how a
            // call app's first session starts at the level set while it was silent.
            var last = new Dictionary<string, Level>();
            foreach (var (id, g) in groups)
            {
                if (_levels.ContainsKey(id)) Align(id, g, last);
                else foreach (var s in g.Sessions) last[s.Key] = s.Level;
            }
            _last = last;
            Save();
            return shown.Select(id => new AppVolume(id, _names[id], ToPercent(_levels[id].Volume), _levels[id].Muted)).ToList();
        }
        finally
        {
            foreach (var o in open) o.Dispose();
        }
    }

    Dictionary<string, Group> ReadSessions(List<IDisposable> open)
    {
        var groups = new Dictionary<string, Group>();
        var defaultDevice = _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        open.Add(defaultDevice);
        var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (var d = 0; d < devices.Count; d++)
        {
            var device = devices[d];
            open.Add(device);
            var onDefault = device.ID == defaultDevice.ID;
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                open.Add(session);
                if (session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                var id = Identify(session);
                if (id == null) continue;
                if (!groups.TryGetValue(id, out var g)) groups[id] = g = new Group { Name = _names[id] };
                var active = session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive;
                g.OnDefault |= onDefault;
                g.Active |= onDefault && active;
                var level = new Level(session.SimpleAudioVolume.Volume, session.SimpleAudioVolume.Mute);
                g.Sessions.Add(new Seen($"{device.ID}|{session.GetSessionInstanceIdentifier}", session, onDefault, active, level));
            }
        }
        return groups;
    }

    /// <summary>What's heard of an app seen for the first time: its session playing on the
    /// default device, else any of its sessions there.</summary>
    static Level Heard(Group g) =>
        g.Sessions.Where(s => s.OnDefault).OrderByDescending(s => s.Active).Select(s => s.Level).FirstOrDefault(g.Sessions[0].Level);

    /// <summary>
    /// Brings every session of the app to its one level. A session that changed since the
    /// last round was moved in Windows (its mixer, or the app's own volume control), so
    /// that becomes the level first; the default device's session wins a tie.
    /// </summary>
    void Align(string id, Group g, Dictionary<string, Level> last)
    {
        var moved = g.Sessions
            .Where(s => _last.TryGetValue(s.Key, out var was) && !was.Same(s.Level))
            .OrderByDescending(s => s.OnDefault)
            .FirstOrDefault();
        if (moved != null) _levels[id] = moved.Level;
        var level = _levels[id];
        foreach (var s in g.Sessions)
        {
            if (!s.Level.Same(level))
            {
                try
                {
                    if (Math.Abs(s.Level.Volume - level.Volume) >= 0.005f) s.Session.SimpleAudioVolume.Volume = level.Volume;
                    if (s.Level.Muted != level.Muted) s.Session.SimpleAudioVolume.Mute = level.Muted;
                }
                catch (COMException e)
                {
                    Log.Write($"mixer: {id}: {e.Message}");  // the session ended meanwhile
                }
            }
            last[s.Key] = level;
        }
    }

    /// <summary>The call apps open now, looked for every few seconds (not every round).</summary>
    HashSet<string> RunningCallApps()
    {
        if (Environment.TickCount64 - _runningAt < RunningEveryMs) return _running;
        _runningAt = Environment.TickCount64;
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (CallApps.Contains(process.ProcessName)) running.Add(process.ProcessName.ToLowerInvariant());
            }
        }
        return _running = running;
    }

    sealed record SavedLevel(float Volume, bool Muted);

    void LoadSaved()
    {
        try
        {
            if (!File.Exists(SavedPath)) return;
            var saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, SavedLevel>>(File.ReadAllText(SavedPath));
            foreach (var (id, level) in saved ?? new()) _saved[id] = new Level(Math.Clamp(level.Volume, 0f, 1f), level.Muted);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log.Write($"mixer: couldn't read {SavedPath}: {e.Message}");
        }
    }

    /// <summary>Keeps the call apps' levels in mixer.json when they change (from the deck or in Windows).</summary>
    void Save()
    {
        var changed = false;
        foreach (var id in CallApps)
        {
            if (!_levels.TryGetValue(id, out var level)) continue;
            if (_saved.TryGetValue(id, out var was) && was.Same(level)) continue;
            _saved[id] = level;
            changed = true;
        }
        if (!changed) return;
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            var json = System.Text.Json.JsonSerializer.Serialize(_saved.ToDictionary(p => p.Key, p => new SavedLevel(p.Value.Volume, p.Value.Muted)),
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SavedPath, json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"mixer: couldn't save {SavedPath}: {e.Message}");
        }
    }

    /// <summary>The mixer id for a session (null for sessions to hide), caching names by process.</summary>
    string? Identify(AudioSessionControl session)
    {
        if (session.IsSystemSoundsSession)
        {
            _names["system"] = "System";
            return "system";
        }
        var pid = session.GetProcessID;
        if (pid == 0) return null;
        if (_processIds.TryGetValue(pid, out var cached)) return cached;
        if (_processIds.Count > 500) _processIds.Clear();

        string? id = null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            id = process.ProcessName.ToLowerInvariant();
            if (!_names.ContainsKey(id)) _names[id] = FriendlyName(process);
        }
        catch (ArgumentException)
        {
            // Exited between the session listing and now.
        }
        _processIds[pid] = id;
        return id;
    }

    static string FriendlyName(Process process)
    {
        if (KnownNames.TryGetValue(process.ProcessName, out var known)) return known;
        string? description = null;
        try
        {
            description = process.MainModule?.FileVersionInfo.FileDescription;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Elevated or protected process: fall back to its name.
        }
        var name = string.IsNullOrWhiteSpace(description) ? process.ProcessName : description.Trim();
        name = char.ToUpperInvariant(name[0]) + name[1..];
        return name.Length > 14 ? name[..14] : name;
    }
}
