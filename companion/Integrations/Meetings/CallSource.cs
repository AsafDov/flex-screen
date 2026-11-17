using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace FlexScreen.Companion;

/// <param name="App">"teams", "zoom", "discord", "webex", "browser", or "" when there's no call.</param>
/// <param name="Camera">1 on, 0 off, -1 unknown.</param>
/// <param name="Hand">1 raised, 0 not, -1 the app can't say.</param>
/// <param name="Can">What the deck can do in this app's call: "mic", "camera", "hand", "leave", "react", "deafen",
/// "whiteboard", "annotate".</param>
/// <param name="Note">A setup hint for the deck, or "".</param>
/// <param name="Draw">Zoom: the whiteboard or annotation tools while they're open.</param>
record CallState(string App, string Name, DateTime Since, bool MicMuted, int Camera, int Hand, bool Deafened,
                 IReadOnlyList<string> Can, string Note, DrawState? Draw = null)
{
    public static readonly CallState None = new("", "", DateTime.MinValue, false, -1, -1, false, [], "");
    public bool Active => App.Length > 0;
}

/// <summary>
/// A call in Teams, Zoom, Discord, Webex or a browser, and its controls. A call is an app
/// using the microphone right now, as Windows records it (the same data as the taskbar's
/// microphone icon), so no app has to cooperate to be noticed. Controls per app:
/// <list type="bullet">
/// <item>Teams: its local API (<see cref="TeamsApi"/>; Settings › Privacy › Manage API), the
/// one Stream Deck uses: real mute, camera and hand state, leave and reactions. The first
/// action asks to pair in Teams; the token is kept encrypted.</item>
/// <item>Zoom: its own toolbar buttons, pressed through accessibility (<see cref="ZoomControl"/>):
/// mute and video with their real state, whiteboard, annotation and every drawing tool. A
/// meeting is also noticed by its window, so one joined without audio still counts. Raise
/// hand is Alt+Y, sent to the meeting window, then the focus goes back (<see cref="ZoomShortcuts"/>).</item>
/// <item>Discord: Windows' microphone mute, and Deafen also mutes Discord in the mixer.</item>
/// </list>
/// </summary>
sealed class CallSource
{
    static readonly TimeSpan Every = TimeSpan.FromSeconds(1.5);

    // App, its name on the deck, and how Windows lists it (Store package or exe name).
    static readonly (string App, string Name, string[] Keys)[] Apps =
    [
        ("teams", "Teams", ["MSTeams_", "ms-teams.exe", "Teams.exe"]),
        ("zoom", "Zoom", ["Zoom.exe"]),
        ("discord", "Discord", ["Discord.exe"]),
        ("webex", "Webex", ["CiscoCollabHost.exe", "webex.exe"]),
        ("browser", "Browser call", ["chrome.exe", "msedge.exe", "firefox.exe", "brave.exe"]),
    ];

    readonly DeviceSource _devices;
    readonly MixerSource _mixer;
    readonly ZoomControl _zoom = new();
    readonly TeamsApi _teams = new();
    readonly System.Threading.Timer _timer;
    readonly object _gate = new();
    DateTime _since = DateTime.MinValue;
    string _app = "";
    bool _deafened;
    string _key = "";

    public CallState Current { get; private set; } = CallState.None;

    /// <summary>Raised on a timer thread when a call starts, ends or changes.</summary>
    public event Action<CallState>? Changed;

    public event Action<Toast>? Toast;

    public CallSource(DeviceSource devices, MixerSource mixer)
    {
        _devices = devices;
        _mixer = mixer;
        _timer = new System.Threading.Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
        _devices.MicChanged += _ => Poll();
        _zoom.Changed += Poll;
        _teams.Changed += Poll;
    }

    public void Start() => _timer.Change(TimeSpan.Zero, Every);

    void Poll()
    {
        try
        {
            var mic = InUse("microphone");
            var camera = InUse("webcam");
            var app = Apps.FirstOrDefault(a => a.Keys.Any(k => mic.Any(m => m.StartsWith(k, StringComparison.OrdinalIgnoreCase) ||
                                                                            m.EndsWith(k, StringComparison.OrdinalIgnoreCase))));
            _zoom.Poll();
            if (app.App == null && _zoom.Current.InMeeting) app = Apps.First(a => a.App == "zoom");  // joined without audio
            var cameraOn = app.App != null && app.Keys.Any(k => camera.Any(c => c.StartsWith(k, StringComparison.OrdinalIgnoreCase) ||
                                                                             c.EndsWith(k, StringComparison.OrdinalIgnoreCase)));
            if (Process.GetProcessesByName("ms-teams").Length > 0) _ = _teams.EnsureAsync();
            Publish(Build(app.App ?? "", app.Name ?? "", cameraOn));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Write($"call: {e.Message}");
        }
    }

    CallState Build(string app, string name, bool cameraOn)
    {
        lock (_gate)
        {
            // Teams in a meeting says so itself, even before the mic registers.
            var teamsState = _teams.State;
            var teams = teamsState?["meetingState"];
            if (app.Length == 0 && (bool?)teams?["isInMeeting"] == true) (app, name) = ("teams", "Teams");
            if (app != _app)
            {
                if (_app == "discord" && _deafened) SetDiscordOutput(muted: false);
                _deafened = false;
                _app = app;
                _since = DateTime.UtcNow;
                if (app.Length > 0) Log.Write($"call: {name} call started");
                else Log.Write("call: ended");
            }
            if (app.Length == 0) return CallState.None;
            var systemMuted = _devices.Mic.Muted;
            switch (app)
            {
                case "teams" when teams != null && (bool?)teams["isInMeeting"] == true:
                {
                    var can = teamsState?["meetingPermissions"];
                    var list = new List<string>();
                    if ((bool?)can?["canToggleMute"] == true) list.Add("mic");
                    if ((bool?)can?["canToggleVideo"] == true) list.Add("camera");
                    if ((bool?)can?["canToggleHand"] == true) list.Add("hand");
                    if ((bool?)can?["canLeave"] == true) list.Add("leave");
                    if ((bool?)can?["canReact"] == true) list.Add("react");
                    return new CallState(app, name, _since, (bool?)teams["isMuted"] ?? false, (bool?)teams["isVideoOn"] == true ? 1 : 0,
                                         (bool?)teams["isHandRaised"] == true ? 1 : 0, false, list, "");
                }
                case "teams":
                    return new CallState(app, name, _since, systemMuted, cameraOn ? 1 : 0, -1, false, ["mic"],
                        _teams.Connected ? "Tap a control once, then allow flex-screen in Teams"
                                         : "For camera, hand and leave: Teams › Settings › Privacy › Manage API › Enable API");
                case "zoom":
                {
                    var zoom = _zoom.Current;
                    var list = new List<string> { "mic", "camera", "hand" };
                    if (zoom.CanWhiteboard) list.Add("whiteboard");
                    if (zoom.CanAnnotate || zoom.Draw?.Mode == "annotate") list.Add("annotate");
                    list.Add("leave");
                    // Windows' mute silences Zoom too, so either one counts.
                    return new CallState(app, name, _since, systemMuted || zoom.Muted == 1, zoom.Video >= 0 ? zoom.Video : cameraOn ? 1 : 0, -1,
                                         false, list, "", zoom.Draw);
                }
                case "discord":
                    return new CallState(app, name, _since, systemMuted, cameraOn ? 1 : 0, -1, _deafened, ["mic", "deafen"], "");
                default:
                    return new CallState(app, name, _since, systemMuted, cameraOn ? 1 : 0, -1, false, ["mic"], "");
            }
        }
    }

    void Publish(CallState state)
    {
        var key = $"{state.App}|{state.MicMuted}|{state.Camera}|{state.Hand}|{state.Deafened}|{string.Join(",", state.Can)}|{state.Note}|{state.Draw?.Key}";
        Current = state;
        if (key == _key) return;
        _key = key;
        Changed?.Invoke(state);
    }

    // ---- controls

    public void ToggleMic()
    {
        var c = Current;
        if (c.App == "teams" && c.Can.Contains("mic") && c.Note.Length == 0) TeamsAction("toggle-mute");
        else if (c.App == "zoom" && !_devices.Mic.Muted) _zoom.ToggleMic(_devices.ToggleMicMute);
        else _devices.ToggleMicMute();  // also Zoom muted by Windows: unmuting that is what brings the voice back
    }

    public void ToggleCamera()
    {
        switch (Current.App)
        {
            case "teams": TeamsAction("toggle-video"); break;
            case "zoom": _zoom.ToggleVideo(() => ZoomShortcuts.Send('V')); break;
        }
    }

    public void ToggleHand()
    {
        switch (Current.App)
        {
            case "teams": TeamsAction("toggle-hand"); break;
            case "zoom": ZoomShortcuts.Send('Y'); break;
        }
    }

    public void Leave()
    {
        switch (Current.App)
        {
            case "teams": TeamsAction("leave-call"); break;
            case "zoom":
                _zoom.Leave(() => ZoomShortcuts.Send('Q'));  // Zoom asks to confirm; a host also gets "End for all", so it isn't pressed for them
                Toast?.Invoke(new Toast("attention", "Confirm leaving in Zoom"));
                break;
        }
    }

    /// <summary>The meeting's chat: Zoom's chat panel, Teams' (through its API).</summary>
    public void Chat()
    {
        switch (Current.App)
        {
            case "zoom": _zoom.ToggleMeetingChat(); break;
            case "teams": TeamsAction("toggle-ui", new JsonObject { ["type"] = "chat" }); break;
        }
    }

    /// <summary>A new meeting in that app: Zoom's instant meeting, Teams' new-meeting form.</summary>
    public void NewMeeting(string app)
    {
        switch (app)
        {
            case "zoom":
                _zoom.NewMeeting(why => Toast?.Invoke(new Toast("attention", why)));
                break;
            case "teams":
                try
                {
                    Process.Start(new ProcessStartInfo("msteams:/l/meeting/new") { UseShellExecute = true })?.Dispose();
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    Toast?.Invoke(new Toast("attention", "Teams isn't installed"));
                }
                break;
        }
    }

    /// <summary>Zoom Workplace's own chat (outside a meeting).</summary>
    public void OpenChat(string app)
    {
        if (app == "zoom") _zoom.OpenChat();
    }

    /// <summary>Zoom: share the screen (Zoom's picker opens), or stop sharing.</summary>
    public void Share()
    {
        if (Current.App == "zoom") _zoom.Share();
    }

    /// <summary>Zoom: a new whiteboard, from the meeting toolbar.</summary>
    public void Whiteboard()
    {
        if (Current.App == "zoom") _zoom.OpenWhiteboard();
    }

    /// <summary>Zoom: annotate the screen being shared, or stop.</summary>
    public void Annotate()
    {
        if (Current.App == "zoom") _zoom.ToggleAnnotate();
    }

    /// <summary>Zoom's drawing tools: "tool", "color", "width", "weight", "kind" (with the id, index or value), "undo", "redo", "clear", "save", "close".</summary>
    public void Draw(string what, string id)
    {
        if (Current.App == "zoom") _zoom.Draw(what, id);
    }

    public void React(string type)
    {
        if (Current.App == "teams") TeamsAction("send-reaction", new JsonObject { ["type"] = type });
    }

    /// <summary>Discord: mutes the mic and Discord's sound in the mixer, or both back on.</summary>
    public void ToggleDeafen()
    {
        if (Current.App != "discord") return;
        bool deafen;
        lock (_gate) deafen = _deafened = !_deafened;
        SetDiscordOutput(deafen);
        if (deafen != _devices.Mic.Muted) _devices.ToggleMicMute();
        Poll();
    }

    void SetDiscordOutput(bool muted)
    {
        var app = _mixer.Current.FirstOrDefault(a => a.Id == "discord");
        if (app != null && app.Muted != muted) _mixer.ToggleMute("discord");
    }

    public void SetVolume(int level) => _mixer.SetLevel(AppProcess(Current.App).ToLowerInvariant(), level);

    /// <summary>The call app's volume in the mixer, or -1.</summary>
    public int Volume => _mixer.Current.FirstOrDefault(a => a.Id == AppProcess(Current.App).ToLowerInvariant())?.Level ?? -1;

    public void Focus()
    {
        var process = AppProcess(Current.App);
        if (process.Length > 0) WindowFocus.Focus("", process);
    }

    static string AppProcess(string app) => app switch
    {
        "teams" => "ms-teams", "zoom" => "Zoom", "discord" => "Discord", "webex" => "CiscoCollabHost", "browser" => "chrome", _ => "",
    };

    // ---- what's using the microphone and camera

    /// <summary>Apps Windows lists as using this capability right now (LastUsedTimeStop 0): Store package names and exe paths.</summary>
    static List<string> InUse(string capability)
    {
        var list = new List<string>();
        using var root = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{capability}");
        if (root == null) return list;
        foreach (var name in root.GetSubKeyNames())
        {
            if (name == "NonPackaged")
            {
                using var exes = root.OpenSubKey(name);
                foreach (var exe in exes?.GetSubKeyNames() ?? [])
                {
                    using var key = exes!.OpenSubKey(exe);
                    if (Active(key)) list.Add(exe.Replace('#', '\\'));
                }
            }
            else
            {
                using var key = root.OpenSubKey(name);
                if (Active(key)) list.Add(name);
            }
        }
        return list;
    }

    static bool Active(RegistryKey? key) =>
        key?.GetValue("LastUsedTimeStart") is long start && start != 0 && key.GetValue("LastUsedTimeStop") is long stop && stop == 0;

    // ---- Teams' API

    void TeamsAction(string action, JsonObject? parameters = null)
    {
        if (!_teams.Send(action, parameters)) Toast?.Invoke(new Toast("attention", "Enable Teams' API: Settings › Privacy › Manage API"));
    }
}
