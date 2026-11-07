using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace FlexScreen.Companion;

/// <param name="App">"teams", "zoom", "discord", "webex", "browser", or "" when there's no call.</param>
/// <param name="Camera">1 on, 0 off, -1 unknown.</param>
/// <param name="Hand">1 raised, 0 not, -1 the app can't say.</param>
/// <param name="Can">What the deck can do in this app's call: "mic", "camera", "hand", "leave", "react", "deafen".</param>
/// <param name="Note">A setup hint for the deck, or "".</param>
record CallState(string App, string Name, DateTime Since, bool MicMuted, int Camera, int Hand, bool Deafened,
                 IReadOnlyList<string> Can, string Note)
{
    public static readonly CallState None = new("", "", DateTime.MinValue, false, -1, -1, false, [], "");
    public bool Active => App.Length > 0;
}

/// <summary>
/// A call in Teams, Zoom, Discord, Webex or a browser, and its controls. A call is an app
/// using the microphone right now, as Windows records it (the same data as the taskbar's
/// microphone icon), so no app has to cooperate to be noticed. Controls per app:
/// <list type="bullet">
/// <item>Teams: its local API (Settings › Privacy › Manage API), the one Stream Deck uses:
/// real mute, camera and hand state, leave and reactions. The first action asks to pair
/// in Teams; the token is kept encrypted.</item>
/// <item>Zoom: its keyboard shortcuts, sent to the meeting window (Alt+A, Alt+V, Alt+Y,
/// Alt+Q), then the focus goes back. Mute is Windows' microphone mute, which has a state.</item>
/// <item>Discord: Windows' microphone mute, and Deafen also mutes Discord in the mixer.</item>
/// </list>
/// </summary>
sealed class CallSource
{
    static readonly TimeSpan Every = TimeSpan.FromSeconds(1.5);
    const string TeamsPort = "8124";
    static readonly string TokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "flex-screen", "teams-token.bin");

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
    readonly System.Threading.Timer _timer;
    readonly object _gate = new();
    DateTime _since = DateTime.MinValue;
    string _app = "";
    bool _deafened;
    string _key = "";

    // Teams' API
    ClientWebSocket? _teams;
    JsonNode? _teamsState;  // meetingUpdate: meetingState and meetingPermissions
    int _requestId;
    DateTime _teamsTried = DateTime.MinValue;

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
            var cameraOn = app.App != null && app.Keys.Any(k => camera.Any(c => c.StartsWith(k, StringComparison.OrdinalIgnoreCase) ||
                                                                             c.EndsWith(k, StringComparison.OrdinalIgnoreCase)));
            if (Process.GetProcessesByName("ms-teams").Length > 0) _ = EnsureTeamsAsync();
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
            var teams = _teamsState?["meetingState"];
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
                    var can = _teamsState?["meetingPermissions"];
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
                        _teams?.State == WebSocketState.Open ? "Tap a control once, then allow flex-screen in Teams"
                                                             : "For camera, hand and leave: Teams › Settings › Privacy › Manage API › Enable API");
                case "zoom":
                    return new CallState(app, name, _since, systemMuted, cameraOn ? 1 : 0, -1, false, ["mic", "camera", "hand", "leave"], "");
                case "discord":
                    return new CallState(app, name, _since, systemMuted, cameraOn ? 1 : 0, -1, _deafened, ["mic", "deafen"], "");
                default:
                    return new CallState(app, name, _since, systemMuted, cameraOn ? 1 : 0, -1, false, ["mic"], "");
            }
        }
    }

    void Publish(CallState state)
    {
        var key = $"{state.App}|{state.MicMuted}|{state.Camera}|{state.Hand}|{state.Deafened}|{string.Join(",", state.Can)}|{state.Note}";
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
        else _devices.ToggleMicMute();
    }

    public void ToggleCamera()
    {
        switch (Current.App)
        {
            case "teams": TeamsAction("toggle-video"); break;
            case "zoom": ZoomKey('V'); break;
        }
    }

    public void ToggleHand()
    {
        switch (Current.App)
        {
            case "teams": TeamsAction("toggle-hand"); break;
            case "zoom": ZoomKey('Y'); break;
        }
    }

    public void Leave()
    {
        switch (Current.App)
        {
            case "teams": TeamsAction("leave-call"); break;
            case "zoom":
                ZoomKey('Q');  // Zoom asks to confirm; a host also gets "End for all", so it isn't pressed for them
                Toast?.Invoke(new Toast("attention", "Confirm leaving in Zoom"));
                break;
        }
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

    /// <summary>A Zoom shortcut, sent to the meeting window; then the window that had the focus gets it back.</summary>
    static void ZoomKey(char key)
    {
        var meeting = ZoomMeetingWindow();
        if (meeting == IntPtr.Zero) return;
        var before = GetForegroundWindow();
        WindowFocus.Activate(meeting);
        Thread.Sleep(120);
        KeyInput.Chord(KeyInput.Alt, key);
        Thread.Sleep(80);
        if (before != IntPtr.Zero && before != meeting) WindowFocus.Activate(before);
    }

    static IntPtr ZoomMeetingWindow()
    {
        var pids = Process.GetProcessesByName("Zoom").Select(p => (uint)p.Id).ToHashSet();
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains(pid) || !IsWindowVisible(hwnd)) return true;
            var cls = new StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            if (cls.ToString() != "ZPContentViewWndClass") return true;  // the meeting window, not Zoom's home
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

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

    // ---- Teams' third-party API (ws://127.0.0.1:8124, protocol 2.0.0)

    async Task EnsureTeamsAsync()
    {
        lock (_gate)
        {
            if (_teams is { State: WebSocketState.Open or WebSocketState.Connecting }) return;
            if (DateTime.UtcNow - _teamsTried < TimeSpan.FromSeconds(10)) return;
            _teamsTried = DateTime.UtcNow;
        }
        var socket = new ClientWebSocket();
        var token = LoadToken();
        var url = $"ws://127.0.0.1:{TeamsPort}?token={Uri.EscapeDataString(token)}&protocol-version=2.0.0" +
                  "&manufacturer=flex-screen&device=deck&app=flex-screen&app-version=1.0";
        try
        {
            await socket.ConnectAsync(new Uri(url), CancellationToken.None);
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException)
        {
            socket.Dispose();
            return;  // the API is off in Teams, or Teams is starting
        }
        lock (_gate) _teams = socket;
        Log.Write("call: connected to Teams' API");
        _ = ReceiveTeamsAsync(socket);
    }

    async Task ReceiveTeamsAsync(ClientWebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var text = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);
                var message = JsonNode.Parse(text.ToString());
                if (message?["tokenRefresh"] is { } token)
                {
                    SaveToken((string?)token ?? "");
                    Log.Write("call: paired with Teams");
                }
                if (message?["meetingUpdate"] is { } update)
                {
                    lock (_gate) _teamsState = update.DeepClone();
                    Poll();
                }
                if ((string?)message?["errorMsg"] is { Length: > 0 } error) Log.Write($"call: Teams: {error}");
            }
        }
        catch (Exception e) when (e is WebSocketException or System.Text.Json.JsonException or OperationCanceledException)
        {
            // Teams closed or restarted; the next poll reconnects.
        }
        finally
        {
            lock (_gate)
            {
                if (_teams == socket)
                {
                    _teams = null;
                    _teamsState = null;
                }
            }
            socket.Dispose();
        }
    }

    void TeamsAction(string action, JsonObject? parameters = null)
    {
        ClientWebSocket? socket;
        lock (_gate) socket = _teams;
        if (socket is not { State: WebSocketState.Open })
        {
            Toast?.Invoke(new Toast("attention", "Enable Teams' API: Settings › Privacy › Manage API"));
            return;
        }
        var body = new JsonObject { ["action"] = action, ["parameters"] = parameters ?? new JsonObject(), ["requestId"] = Interlocked.Increment(ref _requestId) };
        _ = socket.SendAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    static string LoadToken()
    {
        try
        {
            return File.Exists(TokenPath) ? Encoding.UTF8.GetString(ProtectedBytes.Unprotect(File.ReadAllBytes(TokenPath))) : "";
        }
        catch (Exception e) when (e is IOException or CryptographicException)
        {
            return "";
        }
    }

    static void SaveToken(string token)
    {
        if (token.Length == 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
        File.WriteAllBytes(TokenPath, ProtectedBytes.Protect(Encoding.UTF8.GetBytes(token)));
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
}
