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
/// default playback device.
/// </summary>
sealed class MixerSource
{
    /// <summary>The deck has room for this many faders next to Master.</summary>
    const int MaxApps = 5;
    const int PollMs = 750;

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

    /// <summary>Fader order the user asked for; other apps follow, busiest first, then by name.</summary>
    static readonly Dictionary<string, int> FaderOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spotify"] = 0,
        ["chrome"] = 1,
        ["ms-teams"] = 2,
        ["teams"] = 2,
        ["discord"] = 2,
        ["system"] = 3,
        ["flow.launcher"] = 4,
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
        lock (_gate) _enumerator = new MMDeviceEnumerator();
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
            var shown = groups
                .Where(g => g.Value.OnDefault)
                .OrderBy(g => Rank(g.Key))
                .ThenByDescending(g => g.Value.Active)
                .ThenBy(g => g.Value.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(MaxApps)
                .Select(g => g.Key)
                .ToList();
            foreach (var id in shown) _levels.TryAdd(id, Heard(groups[id]));

            var last = new Dictionary<string, Level>();
            foreach (var (id, g) in groups)
            {
                if (_levels.ContainsKey(id)) Align(id, g, last);
                else foreach (var s in g.Sessions) last[s.Key] = s.Level;
            }
            _last = last;
            return shown.Select(id => new AppVolume(id, groups[id].Name, ToPercent(_levels[id].Volume), _levels[id].Muted)).ToList();
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
