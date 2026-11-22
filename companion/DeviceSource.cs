using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace FlexScreen.Companion;

record AudioDevice(string Id, string Name, string Detail, bool Selected);

record DeviceLists(IReadOnlyList<AudioDevice> Outputs, IReadOnlyList<AudioDevice> Inputs)
{
    public static readonly DeviceLists None = new([], []);

    public string Key => string.Join("|", Outputs.Concat(Inputs).Select(d => $"{d.Id}:{d.Selected}"));
}

record MicState(bool Available, bool Muted);

/// <summary>
/// Playback and recording devices: lists them, switches the Windows default, and mutes
/// every microphone at once, so no app (Teams, Zoom, Discord) can still hear you.
/// </summary>
sealed class DeviceSource : IMMNotificationClient
{
    /// <summary>The deck keeps at most this many outputs, and as many inputs (model::Devices::kMax); its list scrolls.</summary>
    const int MaxPerFlow = 10;

    readonly object _gate = new();
    readonly MMDeviceEnumerator _enumerator = new();
    readonly List<MMDevice> _mics = new();
    readonly System.Threading.Timer _debounce;
    readonly AutoResetEvent _switchRequested = new(false);
    string? _wantedOutput;
    string? _wantedInput;

    public DeviceLists Devices { get; private set; } = DeviceLists.None;
    public MicState Mic { get; private set; } = new(false, false);

    /// <summary>Raised on a thread-pool thread when the device lists or defaults change.</summary>
    public event Action<DeviceLists>? DevicesChanged;

    /// <summary>Raised on a thread-pool thread when microphone mute changes.</summary>
    public event Action<MicState>? MicChanged;

    public DeviceSource()
    {
        _debounce = new System.Threading.Timer(_ => Refresh());
    }

    public void Start()
    {
        _enumerator.RegisterEndpointNotificationCallback(this);
        Refresh();
        new Thread(SwitchLoop) { IsBackground = true, Name = "device-switch" }.Start();
    }

    /// <summary>
    /// Asks for the device to become the Windows default for every role, communications
    /// included. Returns at once: Windows takes up to a couple of seconds per switch, and
    /// only the latest request per direction matters if taps pile up meanwhile.
    /// </summary>
    public void RequestDefault(string deviceId, bool output)
    {
        if (output) _wantedOutput = deviceId;
        else _wantedInput = deviceId;
        _switchRequested.Set();
    }

    void SwitchLoop()
    {
        while (true)
        {
            _switchRequested.WaitOne();
            foreach (var (id, flow) in new[] { (Interlocked.Exchange(ref _wantedOutput, null), DataFlow.Render),
                                               (Interlocked.Exchange(ref _wantedInput, null), DataFlow.Capture) })
            {
                if (id != null) Switch(id, flow);
            }
        }
    }

    void Switch(string deviceId, DataFlow flow)
    {
        var started = DateTime.UtcNow;
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
            {
                if (IsDefault(deviceId, flow, role)) continue;  // each switch makes Windows reconfigure audio
                var hr = policy.SetDefaultEndpoint(deviceId, role);
                if (hr != 0) Log.Write($"devices: SetDefaultEndpoint({role}) failed: 0x{hr:X8}");
            }
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
        Log.Write($"devices: switch took {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");
    }

    bool IsDefault(string deviceId, DataFlow flow, Role role)
    {
        lock (_gate)
        {
            try
            {
                using var current = _enumerator.GetDefaultAudioEndpoint(flow, role);
                return current.ID == deviceId;
            }
            catch (COMException)
            {
                return false;
            }
        }
    }

    /// <summary>Mutes every active microphone if any is live; otherwise unmutes them all.</summary>
    public void ToggleMicMute()
    {
        lock (_gate)
        {
            var mute = _mics.Any(m => !m.AudioEndpointVolume.Mute);
            foreach (var mic in _mics) mic.AudioEndpointVolume.Mute = mute;
        }
        PublishMic();
    }

    void Refresh()
    {
        try
        {
            lock (_gate)
            {
                foreach (var mic in _mics)
                {
                    mic.AudioEndpointVolume.OnVolumeNotification -= OnMicVolume;
                    mic.Dispose();
                }
                _mics.Clear();
                foreach (var mic in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    mic.AudioEndpointVolume.OnVolumeNotification += OnMicVolume;
                    _mics.Add(mic);
                }

                var state = new DeviceLists(List(DataFlow.Render), List(DataFlow.Capture));
                if (state.Key != Devices.Key)
                {
                    Devices = state;
                    DevicesChanged?.Invoke(state);
                }
            }
            PublishMic();
        }
        catch (COMException e)
        {
            Log.Write($"devices: refresh failed: {e.Message}");
        }
    }

    List<AudioDevice> List(DataFlow flow)
    {
        string? selected = null;
        try
        {
            using var current = _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            selected = current.ID;
        }
        catch (COMException)
        {
            // No device of this kind.
        }

        var all = new List<AudioDevice>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                var (name, detail) = SplitName(device.FriendlyName);
                all.Add(new AudioDevice(device.ID, name, detail, device.ID == selected));
            }
        }
        // Stable order so buttons don't jump when the default changes: the built-in
        // speakers first, headphones right after (on request), then by name. Never drop the
        // selected device off the end.
        static int Rank(AudioDevice d) =>
            d.Name.StartsWith("Speakers", StringComparison.OrdinalIgnoreCase) ? 0
            : d.Name.StartsWith("Headphones", StringComparison.OrdinalIgnoreCase) ? 1
            : 2;
        var shown = all.OrderBy(Rank)
                       .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                       .Take(MaxPerFlow).ToList();
        var chosen = all.FirstOrDefault(d => d.Selected);
        if (chosen != null && !shown.Contains(chosen)) shown[^1] = chosen;
        return shown;
    }

    void PublishMic()
    {
        MicState state;
        lock (_gate)
        {
            state = new MicState(_mics.Count > 0, _mics.Count > 0 && _mics.All(m => m.AudioEndpointVolume.Mute));
        }
        if (state == Mic) return;
        Mic = state;
        MicChanged?.Invoke(state);
    }

    // Core Audio forbids calling back into it from its callbacks, so hand off to the pool.
    void OnMicVolume(AudioVolumeNotificationData data) => ThreadPool.QueueUserWorkItem(_ => PublishMic());

    void ScheduleRefresh() => _debounce.Change(300, Timeout.Infinite);

    /// <summary>"Speakers (2- Realtek(R) Audio)" → ("Speakers", "2- Realtek(R) Audio").</summary>
    static (string Name, string Detail) SplitName(string friendlyName)
    {
        var open = friendlyName.IndexOf(" (", StringComparison.Ordinal);
        if (open < 0 || !friendlyName.EndsWith(')')) return (friendlyName, "");
        return (friendlyName[..open], friendlyName[(open + 2)..^1]);
    }

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => ScheduleRefresh();
    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => ScheduleRefresh();
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => ScheduleRefresh();
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => ScheduleRefresh();
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    // Windows has no public API for changing the default audio device. IPolicyConfig is the
    // undocumented interface the Sound control panel uses; EarTrumpet and SoundSwitch use it too.
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        // Only SetDefaultEndpoint is used; the rest hold its place in the vtable.
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int useDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int useDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
