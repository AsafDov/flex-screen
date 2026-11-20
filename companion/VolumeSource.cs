using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace FlexScreen.Companion;

record VolumeState(int Level, bool Muted);

/// <summary>Master volume and mute of the default playback device, following device switches.</summary>
sealed class VolumeSource : IMMNotificationClient
{
    readonly object _gate = new();
    readonly MMDeviceEnumerator _enumerator = new();
    readonly System.Threading.Timer _reattach;
    readonly System.Threading.Timer _reread;
    int _rereadPending;
    MMDevice? _device;

    public VolumeState Current { get; private set; } = new(-1, false);

    /// <summary>Raised on a thread-pool thread, in order, whenever <see cref="Current"/> changes.</summary>
    public event Action<VolumeState>? Changed;

    public VolumeSource()
    {
        _reattach = new System.Threading.Timer(_ => AttachDefault());
        _reread = new System.Threading.Timer(_ => Reread());
    }

    public void Start()
    {
        _enumerator.RegisterEndpointNotificationCallback(this);
        AttachDefault();
    }

    public void SetLevel(int level)
    {
        lock (_gate)
        {
            var volume = _device?.AudioEndpointVolume;
            if (volume == null) return;
            volume.MasterVolumeLevelScalar = Math.Clamp(level, 0, 100) / 100f;
            if (volume.Mute && level > 0) volume.Mute = false;  // dragging up means "I want to hear it"
        }
    }

    /// <summary>
    /// Up or down by <paramref name="delta"/> points from where it is, as the keyboard's volume
    /// keys do (and like them, unmuting). The deck repeats it while a tile is held.
    /// </summary>
    public void Step(int delta)
    {
        lock (_gate)
        {
            var volume = _device?.AudioEndpointVolume;
            if (volume == null) return;
            var level = (int)Math.Round(volume.MasterVolumeLevelScalar * 100);
            // To the next multiple of the step, as Windows' keys do: 41 goes to 42 or 40.
            var step = Math.Max(1, Math.Abs(delta));
            var next = delta > 0 ? (level / step + 1) * step : ((level + step - 1) / step - 1) * step;
            volume.MasterVolumeLevelScalar = Math.Clamp(next, 0, 100) / 100f;
            if (volume.Mute) volume.Mute = false;
        }
    }

    public void ToggleMute()
    {
        lock (_gate)
        {
            var volume = _device?.AudioEndpointVolume;
            if (volume != null) volume.Mute = !volume.Mute;
        }
    }

    void AttachDefault()
    {
        lock (_gate)
        {
            if (_device != null)
            {
                _device.AudioEndpointVolume.OnVolumeNotification -= OnVolume;
                _device.Dispose();
                _device = null;
            }
            try
            {
                _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _device.AudioEndpointVolume.OnVolumeNotification += OnVolume;
                Log.Write($"volume: following {_device.FriendlyName}");
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Log.Write("volume: no default playback device");
            }
        }
        Reread();
    }

    /// <summary>
    /// Reads the device's actual level and publishes it. Notifications arrive on several
    /// threads during a drag and can overtake each other, so their payload isn't trusted;
    /// the last publish always matches what Windows has.
    /// </summary>
    void Reread()
    {
        Interlocked.Exchange(ref _rereadPending, 0);
        lock (_gate)
        {
            var volume = _device?.AudioEndpointVolume;
            var state = volume == null
                ? new VolumeState(-1, false)
                : new VolumeState((int)Math.Round(volume.MasterVolumeLevelScalar * 100), volume.Mute);
            if (state == Current) return;
            Current = state;
            Changed?.Invoke(state);  // under the lock, so the deck sees changes in order
        }
    }

    // Core Audio forbids calling back into it from its callbacks, and a drag fires dozens
    // of these, so read the level from a timer thread at most every 30 ms. (Restarting the
    // timer on each notification instead would hold every update back until the drag ends.)
    void OnVolume(AudioVolumeNotificationData data)
    {
        if (Interlocked.Exchange(ref _rereadPending, 1) == 0) _reread.Change(30, Timeout.Infinite);
    }

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        // Fires once per role (and sometimes more) for one switch: re-attach once.
        if (flow == DataFlow.Render) _reattach.Change(250, Timeout.Infinite);
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) { }
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) { }
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
}
