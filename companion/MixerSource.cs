using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace FlexScreen.Companion;

/// <summary>One app in the mixer. <c>Id</c> is the lower-case process name, or "system".</summary>
record AppVolume(string Id, string Name, int Level, bool Muted);

/// <summary>
/// Per-app volume on the default playback device, like the Windows volume mixer. Apps with
/// several audio sessions (Chrome, Teams) show as one entry and move together.
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

    sealed class Group
    {
        public required string Name;
        public float Level;
        public bool Muted = true;
        public bool Active;
    }

    readonly object _gate = new();
    readonly object _publishGate = new();
    readonly Dictionary<uint, string?> _processIds = new();  // pid → mixer id
    readonly Dictionary<string, string> _names = new();      // mixer id → display name
    MMDeviceEnumerator? _enumerator;
    string _key = "";

    public IReadOnlyList<AppVolume> Current { get; private set; } = [];

    /// <summary>Raised on the mixer thread when any app's volume, mute or presence changes.</summary>
    public event Action<IReadOnlyList<AppVolume>>? Changed;

    public void Start() => new Thread(Run) { IsBackground = true, Name = "mixer" }.Start();

    public void SetLevel(string appId, int level)
    {
        ForEachSession(appId, s => s.SimpleAudioVolume.Volume = ToAmplitude(level));
        Poll();
    }

    // Master volume follows Windows' audio-tapered curve, but a session's volume is a plain
    // amplitude multiplier, so 50% on an app is far louder than 50% on master. Faders use
    // master's curve (about amplitude = level^1.75) so both feel the same.
    const double Taper = 1.75;

    static float ToAmplitude(int percent) => (float)Math.Pow(Math.Clamp(percent, 0, 100) / 100.0, Taper);

    static int ToPercent(float amplitude) => (int)Math.Round(Math.Pow(Math.Clamp(amplitude, 0f, 1f), 1 / Taper) * 100);

    public void ToggleMute(string appId)
    {
        var app = Current.FirstOrDefault(a => a.Id == appId);
        if (app == null) return;
        ForEachSession(appId, s => s.SimpleAudioVolume.Mute = !app.Muted);
        Poll();
    }

    void Run()
    {
        // Polling instead of per-session callbacks: sessions come and go constantly and a
        // sub-second refresh is plenty for a mixer.
        lock (_gate) _enumerator = new MMDeviceEnumerator();
        while (true)
        {
            Poll();
            Thread.Sleep(PollMs);
        }
    }

    void Poll()
    {
        // The poll thread and the link thread (after a fader move) both publish; one at a
        // time, so the deck can't receive an older reading after a newer one.
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
        var groups = new Dictionary<string, Group>();
        ForEachSessionLocked(session =>
        {
            var id = Identify(session);
            if (id == null) return;
            if (!groups.TryGetValue(id, out var g)) groups[id] = g = new Group { Name = _names[id] };
            g.Level = Math.Max(g.Level, session.SimpleAudioVolume.Volume);
            g.Muted &= session.SimpleAudioVolume.Mute;
            g.Active |= session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive;
        });

        return groups
            .OrderBy(g => Rank(g.Key))
            .ThenByDescending(g => g.Value.Active)
            .ThenBy(g => g.Value.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxApps)
            .Select(g => new AppVolume(g.Key, g.Value.Name, ToPercent(g.Value.Level), g.Value.Muted))
            .ToList();
    }

    void ForEachSession(string appId, Action<AudioSessionControl> action)
    {
        lock (_gate)
        {
            try
            {
                ForEachSessionLocked(s =>
                {
                    if (Identify(s) == appId) action(s);
                });
            }
            catch (COMException e)
            {
                Log.Write($"mixer: {appId}: {e.Message}");
            }
        }
    }

    void ForEachSessionLocked(Action<AudioSessionControl> action)
    {
        if (_enumerator == null) return;
        using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var manager = device.AudioSessionManager;
        manager.RefreshSessions();
        var sessions = manager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            if (session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
            action(session);
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
