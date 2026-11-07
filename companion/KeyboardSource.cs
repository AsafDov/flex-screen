using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FlexScreen.Companion;

/// <param name="Present">A VIA/Vial keyboard answers on its raw HID interface.</param>
/// <param name="Layer">The highest active layer.</param>
/// <param name="HeldSince">When the key holding that layer on went down; null when it's toggled on, or the base layer.</param>
/// <param name="HeldKey">That key, as an index into <see cref="KeyboardMap.Keys"/>; -1 for none.</param>
record KeyboardState(bool Present, string Name, int Layer, string Label, DateTime? HeldSince = null, int HeldKey = -1);

/// <summary>What the deck's layer cheat sheet draws: where the keys are, and what each layer has on them.</summary>
/// <param name="Keys">Centres and sizes in key units, from the top-left of the keyboard.</param>
record KeyboardMap(IReadOnlyList<KeyPlace> Keys, IReadOnlyList<MapLayer> Layers);

/// <param name="Labels">One per key, as <see cref="KeyLabels"/> writes them.</param>
/// <param name="Dim">'1' for a key the layer leaves transparent (its label is the base layer's).</param>
record MapLayer(int Index, string Name, IReadOnlyList<string> Labels, string Dim);

/// <summary>
/// A VIA/Vial keyboard's active layer (the Corne). Vial has no "current layer" query, so
/// this asks which keys are held (VIA's switch matrix query, which Vial answers while the
/// keyboard is unlocked) and what those keys are in the keymap, and works the layer out the
/// way QMK does: MO/TT/LM held, LT held past the tapping term, tri-layer keys, and TG/TO/DF
/// presses. It only ever reads from the keyboard, and keeps nothing but the layer.
/// A Vial keyboard also carries its own description (vial.json), which gives the matrix
/// size and where each key sits: with the whole keymap, that's the deck's cheat sheet.
/// </summary>
sealed class KeyboardSource
{
    const ushort RawUsagePage = 0xFF60;
    // A query takes ~1 ms; a toggle (TG) has to be seen while it's down, and a quick tap is
    // ~30-60 ms. 10 ms asks for Windows' shortest sleep, ~15 ms in practice.
    const int PollMs = 10;
    const int ReplyTimeoutMs = 300;
    static readonly TimeSpan Rediscover = TimeSpan.FromSeconds(5);
    static readonly TimeSpan KeymapLife = TimeSpan.FromMinutes(2);  // picks up remaps from Vial
    static readonly TimeSpan TappingTerm = TimeSpan.FromMilliseconds(200);

    // QMK keycodes (v6 and later, which Vial protocol 6 uses).
    const ushort Transparent = 0x0001;
    const ushort LayerTap = 0x4000, LayerTapMax = 0x4FFF;
    const ushort LayerMod = 0x5000, LayerModMax = 0x51FF;
    const ushort To = 0x5200, Momentary = 0x5220, DefLayer = 0x5240, Toggle = 0x5260, OneShot = 0x5280, TapToggle = 0x52C0;
    const ushort TriLower = 0x7C77, TriUpper = 0x7C78;

    readonly Thread _thread;
    readonly Dictionary<(int Layer, int Row, int Col), ushort> _keymap = new();
    readonly Dictionary<(int Row, int Col), DateTime> _held = new();
    readonly HashSet<(int Row, int Col)> _interrupted = [];  // held keys that a later key was tapped inside
    readonly HashSet<int> _toggled = [];
    DateTime _keymapAt = DateTime.MinValue;
    int _default;
    int _layers = 1;
    int _rowBytes = 1;  // VIA sends each matrix row in (columns + 7) / 8 bytes, high byte first
    bool _matrixSeen;
    Device? _device;
    VialLayout? _layout;
    string _mapSignature = "";
    DateTime _namesVersion;

    public KeyboardState Current { get; private set; } = new(false, "", 0, "");

    /// <summary>The cheat sheet's keys and labels; null without a Vial keyboard.</summary>
    public KeyboardMap? Map { get; private set; }

    /// <summary>Raised on the polling thread when the keyboard comes or goes or its layer changes.</summary>
    public event Action<KeyboardState>? Changed;

    /// <summary>Raised on the polling thread when <see cref="Map"/> changes (a remap in Vial shows within two minutes).</summary>
    public event Action<KeyboardMap?>? MapChanged;

    public KeyboardSource()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "keyboard" };
    }

    public void Start() => _thread.Start();

    void Run()
    {
        var lastLook = DateTime.MinValue;
        var lastVialCheck = DateTime.MinValue;
        var vialOpen = false;
        while (true)
        {
            try
            {
                // The Vial app talks to the same interface; two talkers would get each other's replies.
                if (DateTime.UtcNow - lastVialCheck > TimeSpan.FromSeconds(2))
                {
                    lastVialCheck = DateTime.UtcNow;
                    vialOpen = Process.GetProcessesByName("Vial").Length > 0;
                }
                if (vialOpen)
                {
                    Close("paused while the Vial app is open");
                    Thread.Sleep(2000);
                    continue;
                }
                if (_device == null)
                {
                    if (DateTime.UtcNow - lastLook < Rediscover)
                    {
                        Thread.Sleep(500);
                        continue;
                    }
                    lastLook = DateTime.UtcNow;
                    _device = Device.Find();
                    if (_device == null) continue;
                    _layers = Math.Max(1, (int)(_device.Ask(0x11)?[1] ?? 1));
                    _keymap.Clear();
                    _layout = ReadLayout();
                    if (_layout != null) LoadKeymap();
                    var columns = _layout?.Cols ?? MatrixColumns();
                    _rowBytes = (columns + 7) / 8;
                    _keymapAt = DateTime.UtcNow;
                    Log.Write($"kbd: {_device.Name} ({_layers} layers, {columns} columns)");
                    UpdateMap();
                }
                Poll();
                Thread.Sleep(PollMs);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or AggregateException)
            {
                Close($"lost ({e.GetType().Name})");
            }
        }
    }

    void Close(string why)
    {
        if (_device == null) return;
        Log.Write($"kbd: {_device.Name} {why}");
        _device.Dispose();
        _device = null;
        _held.Clear();
        _layout = null;
        Publish(new KeyboardState(false, "", 0, ""));
        UpdateMap();
    }

    void Poll()
    {
        var reply = _device!.Ask(0x02, 0x03) ?? throw new IOException("no matrix reply");
        if (DateTime.UtcNow - _keymapAt > KeymapLife)
        {
            _keymap.Clear();
            if (_layout != null) LoadKeymap();
            _keymapAt = DateTime.UtcNow;
            UpdateMap();
        }
        else if (Settings.Version != _namesVersion)
        {
            UpdateMap();  // the layer names may have changed
        }

        // Rows from byte 2, _rowBytes each, high byte first; bit n is column n.
        var now = DateTime.UtcNow;
        var down = new HashSet<(int, int)>();
        for (var row = 0; 2 + (row + 1) * _rowBytes <= reply.Length; row++)
        {
            uint bits = 0;
            for (var b = 0; b < _rowBytes; b++) bits = (bits << 8) | reply[2 + row * _rowBytes + b];
            for (var col = 0; col < _rowBytes * 8; col++)
            {
                if ((bits & (1u << col)) != 0) down.Add((row, col));
            }
        }
        if (down.Count > 0 && !_matrixSeen)
        {
            _matrixSeen = true;
            Log.Write("kbd: the keyboard reports held keys");
        }
        foreach (var gone in _held.Keys.Where(k => !down.Contains(k)).ToList())
        {
            // A key pressed and released inside a held one: QMK's permissive hold makes that
            // a hold. A key that's still down when the held one comes up (rolling) doesn't.
            var pressedAt = _held[gone];
            foreach (var (key, since) in _held)
            {
                if (key != gone && since < pressedAt) _interrupted.Add(key);
            }
            _held.Remove(gone);
            _interrupted.Remove(gone);
        }
        var pressed = down.Where(k => !_held.ContainsKey(k)).ToList();
        foreach (var k in pressed) _held[k] = now;

        // Keys in the order they went down, each read on the layers active when it did.
        var active = new SortedSet<int>(_toggled) { _default };
        var heldBy = new Dictionary<int, ((int Row, int Col) Pos, DateTime Since)>();  // the key holding each layer on
        bool lower = false, upper = false;
        foreach (var (pos, since) in _held.OrderBy(h => h.Value))
        {
            var kc = Resolve(active, pos.Item1, pos.Item2);
            var layer = LayerOf(kc);
            var isNew = pressed.Contains(pos);
            // Layer keys only (never letters): enough to see what the keyboard did.
            if (isNew && Kind(kc) != LayerAction.None) Log.Write($"kbd: {Kind(kc)} {layer} at r{pos.Item1}c{pos.Item2}");
            switch (Kind(kc))
            {
                case LayerAction.Momentary:
                    active.Add(layer);
                    heldBy.TryAdd(layer, (pos, since));
                    break;
                // Held past the tapping term, or another key tapped inside it (permissive
                // hold): the layer. Counting any key pressed meanwhile flashed the layer on
                // while typing fast, rolling from Space onto the next letter.
                case LayerAction.LayerTap when now - since >= TappingTerm || _interrupted.Contains(pos):
                    active.Add(layer);
                    heldBy.TryAdd(layer, (pos, since));
                    break;
                case LayerAction.Toggle when isNew:
                    if (!_toggled.Remove(layer)) _toggled.Add(layer);
                    break;
                case LayerAction.To when isNew:
                    _toggled.Clear();
                    if (layer != _default) _toggled.Add(layer);
                    break;
                case LayerAction.Default when isNew:
                    _default = layer;
                    break;
                case LayerAction.TriLower:
                    lower = true;
                    heldBy.TryAdd(1, (pos, since));
                    break;
                case LayerAction.TriUpper:
                    upper = true;
                    heldBy.TryAdd(2, (pos, since));
                    break;
            }
        }
        if (lower) active.Add(1);
        if (upper) active.Add(2);
        if (lower && upper)
        {
            active.Add(3);
            heldBy.TryAdd(3, heldBy[1].Since > heldBy[2].Since ? heldBy[1] : heldBy[2]);
        }
        foreach (var t in _toggled) active.Add(t);
        var top = active.Max;
        var label = LayerName(top, "LAYER ");
        // Held on (not toggled): the deck shows the cheat sheet once it's been held ten seconds.
        DateTime? heldSince = null;
        var heldKey = -1;
        if (!_toggled.Contains(top) && heldBy.TryGetValue(top, out var holder))
        {
            heldSince = holder.Since;
            heldKey = Map == null ? -1 : Map.Keys.ToList().FindIndex(k => (k.Row, k.Col) == holder.Pos);
        }
        Publish(new KeyboardState(true, _device!.Name, top, label, heldSince, heldKey));
    }

    static string LayerName(int layer, string prefix)
    {
        var names = Settings.LayerNames;
        return layer < names.Count && names[layer].Length > 0 ? names[layer] : $"{prefix}{layer}";
    }

    /// <summary>
    /// The keyboard's description, from Vial's definition commands (they don't echo the
    /// request): its size, then 32-byte blocks of .xz data. Null for a VIA-only keyboard.
    /// </summary>
    VialLayout? ReadLayout()
    {
        var id = _device!.AskRaw(0xFE, 0x00);
        if (id[0] == 0xFF && id[1] == 0x00) return null;  // VIA's "unhandled": not Vial
        var size = BitConverter.ToInt32(_device.AskRaw(0xFE, 0x01), 0);
        if (size <= 0 || size > 1 << 20) return null;
        var data = new byte[size];
        for (var block = 0; block * 32 < size; block++)
        {
            var reply = _device.AskRaw(0xFE, 0x02, (byte)block, (byte)(block >> 8), (byte)(block >> 16), (byte)(block >> 24));
            Array.Copy(reply, 0, data, block * 32, Math.Min(32, size - block * 32));
        }
        try
        {
            var json = Encoding.UTF8.GetString(Xz.Decode(data));
            var options = _device.Ask(0x02, 0x02) is { } o ? (uint)((o[2] << 24) | (o[3] << 16) | (o[4] << 8) | o[5]) : 0;
            var layout = VialLayout.Parse(json, options);
            Log.Write($"kbd: layout from the keyboard, {layout.Keys.Count} keys, {layout.Rows}x{layout.Cols} matrix");
            return layout.Keys.Count > 0 && layout.Rows > 0 && layout.Cols > 0 ? layout : null;
        }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or IndexOutOfRangeException or ArgumentException or InvalidOperationException or FormatException)
        {
            Log.Write($"kbd: can't read the keyboard's layout ({e.Message})");
            return null;
        }
    }

    /// <summary>Every layer's keymap at once (VIA's keymap buffer, 28 bytes a request; ~30 for a Corne).</summary>
    void LoadKeymap()
    {
        var (rows, cols) = (_layout!.Rows, _layout.Cols);
        var total = _layers * rows * cols * 2;
        var bytes = new byte[total];
        for (var at = 0; at < total; at += 28)
        {
            var n = Math.Min(28, total - at);
            var reply = _device!.Ask(0x12, (byte)(at >> 8), (byte)at, (byte)n) ?? throw new IOException("no keymap reply");
            Array.Copy(reply, 4, bytes, at, n);
        }
        for (var layer = 0; layer < _layers; layer++)
        {
            for (var row = 0; row < rows; row++)
            {
                for (var col = 0; col < cols; col++)
                {
                    var i = ((layer * rows + row) * cols + col) * 2;
                    _keymap[(layer, row, col)] = (ushort)((bytes[i] << 8) | bytes[i + 1]);
                }
            }
        }
    }

    /// <summary>The cheat sheet from the layout and keymap: every layer with a key of its own.</summary>
    void UpdateMap()
    {
        _namesVersion = Settings.Version;
        KeyboardMap? map = null;
        if (_layout is { } layout && _device != null)
        {
            var custom = layout.CustomKeycodes;
            string Name(int n) => LayerName(n, "L");
            // On a key a long name won't fit the deck's ~45 px: SYMBOL becomes SYM.
            string Short(int n) => Name(n) is { Length: > 5 } name ? name[..3] : Name(n);
            var layers = new List<MapLayer>();
            for (var layer = 0; layer < _layers; layer++)
            {
                var labels = new List<string>();
                var dim = new StringBuilder();
                var own = false;
                foreach (var key in layout.Keys)
                {
                    var kc = Keycode(layer, key.Row, key.Col);
                    var transparent = layer != _default && kc == Transparent;
                    if (transparent) kc = Keycode(_default, key.Row, key.Col);
                    else if (kc != 0) own = true;
                    labels.Add(KeyLabels.Of(kc, Short, custom));
                    dim.Append(transparent ? '1' : '0');
                }
                if (own || layer == _default) layers.Add(new MapLayer(layer, Name(layer), labels, dim.ToString()));
            }
            map = new KeyboardMap(layout.Keys, layers);
        }
        var signature = map == null ? "" : string.Join("|", map.Layers.Select(l => $"{l.Index}:{l.Name}:{l.Dim}:{string.Join("	", l.Labels)}"));
        if (signature == _mapSignature) return;
        _mapSignature = signature;
        Map = map;
        if (map != null) Log.Write($"kbd: cheat sheet for layers {string.Join(", ", map.Layers.Select(l => l.Name))}");
        MapChanged?.Invoke(map);
    }

    void Publish(KeyboardState state)
    {
        if (state == Current) return;
        if (state.Present && state.Layer != Current.Layer) Log.Write($"kbd: layer {state.Label}");
        Current = state;
        Changed?.Invoke(state);
    }

    /// <summary>
    /// How many columns the matrix has, which VIA doesn't say but the matrix query's layout
    /// depends on: one past the last column with a key on the base layer (a 12-column
    /// Corne sends two bytes a row). The base layer read here stays cached.
    /// </summary>
    int MatrixColumns()
    {
        var last = 0;
        for (var row = 0; row < 16; row++)
        {
            for (var col = 0; col < 24; col++)
            {
                if (Keycode(0, row, col) != 0) last = Math.Max(last, col);
            }
        }
        return last + 1;
    }

    /// <summary>The key at this spot on the highest active layer that isn't transparent.</summary>
    ushort Resolve(SortedSet<int> active, int row, int col)
    {
        foreach (var layer in active.Reverse())
        {
            var kc = Keycode(layer, row, col);
            if (kc != Transparent) return kc;
        }
        return 0;
    }

    ushort Keycode(int layer, int row, int col)
    {
        if (layer >= _layers) return Transparent;
        if (_keymap.TryGetValue((layer, row, col), out var kc)) return kc;
        var reply = _device!.Ask(0x04, (byte)layer, (byte)row, (byte)col);
        kc = reply == null ? (ushort)0 : (ushort)((reply[4] << 8) | reply[5]);
        _keymap[(layer, row, col)] = kc;
        return kc;
    }

    enum LayerAction { None, Momentary, LayerTap, Toggle, To, Default, TriLower, TriUpper }

    static LayerAction Kind(ushort kc) => kc switch
    {
        >= LayerTap and <= LayerTapMax => LayerAction.LayerTap,
        >= LayerMod and <= LayerModMax => LayerAction.Momentary,
        >= To and < To + 0x20 => LayerAction.To,
        >= Momentary and < Momentary + 0x20 => LayerAction.Momentary,
        >= DefLayer and < DefLayer + 0x20 => LayerAction.Default,
        >= Toggle and < Toggle + 0x20 => LayerAction.Toggle,
        >= OneShot and < OneShot + 0x20 => LayerAction.Momentary,  // while held
        >= TapToggle and < TapToggle + 0x20 => LayerAction.Momentary,  // while held
        TriLower => LayerAction.TriLower,
        TriUpper => LayerAction.TriUpper,
        _ => LayerAction.None,
    };

    static int LayerOf(ushort kc) => kc switch
    {
        >= LayerTap and <= LayerTapMax => (kc >> 8) & 0x0F,
        >= LayerMod and <= LayerModMax => (kc >> 5) & 0x0F,
        _ => kc & 0x1F,
    };

    /// <summary>The raw HID interface of a VIA/Vial keyboard (usage page 0xFF60), request and reply.</summary>
    sealed class Device : IDisposable
    {
        readonly FileStream _stream;
        readonly int _length;
        readonly byte[] _buffer;
        Task<int>? _read;  // a read left waiting by a timed-out request: the next one takes it over

        public string Name { get; }

        Device(FileStream stream, int length, string name)
        {
            _stream = stream;
            _length = length;
            _buffer = new byte[length];
            Name = name;
        }

        public static Device? Find()
        {
            foreach (var path in Hid.Paths())
            {
                var handle = Hid.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (handle.IsInvalid) continue;
                if (!Hid.HidD_GetPreparsedData(handle, out var data))
                {
                    handle.Dispose();
                    continue;
                }
                Hid.HidP_GetCaps(data, out var caps);
                Hid.HidD_FreePreparsedData(data);
                if (caps.UsagePage != RawUsagePage || caps.OutputLen < 33)
                {
                    handle.Dispose();
                    continue;
                }
                var name = new char[128];
                var product = Hid.HidD_GetProductString(handle, name, name.Length * 2) ? new string(name).TrimEnd('\0') : "Keyboard";
                var device = new Device(new FileStream(handle, FileAccess.ReadWrite, caps.InputLen, true), caps.OutputLen, product);
                try
                {
                    if (device.Ask(0x01) != null) return device;  // VIA protocol version: it's a VIA keyboard
                }
                catch (Exception e) when (e is IOException or AggregateException)
                {
                    // Not answering: not a VIA keyboard, or asleep.
                }
                device.Dispose();
            }
            return null;
        }

        /// <summary>
        /// Sends a 32-byte VIA packet and returns its reply (without the report id). Every app
        /// with the interface open sees every reply, and VIA echoes the request, so replies
        /// that don't echo this one (a late one, or another app's) are skipped.
        /// </summary>
        public byte[]? Ask(params byte[] command) => Ask(command, Math.Min(command.Length, 4));

        /// <summary>A Vial command: its reply is data, with no echo and no "unhandled" check.</summary>
        public byte[] AskRaw(params byte[] command) => Ask(command, 0)!;

        byte[]? Ask(byte[] command, int echo)
        {
            var report = new byte[_length];  // report id 0, then the packet
            command.CopyTo(report, 1);
            _stream.Write(report, 0, report.Length);
            var deadline = Environment.TickCount64 + ReplyTimeoutMs;
            while (true)
            {
                _read ??= _stream.ReadAsync(_buffer, 0, _buffer.Length);
                var left = deadline - Environment.TickCount64;
                if (left <= 0 || !_read.Wait((int)left)) throw new IOException("no reply");
                _read = null;
                var reply = _buffer[1..];
                if (echo == 0) return reply;
                if (reply[0] == 0xFF) return null;  // VIA's "unhandled"
                if (reply.AsSpan(0, echo).SequenceEqual(command.AsSpan(0, echo))) return reply;
            }
        }

        public void Dispose() => _stream.Dispose();
    }

    static class Hid
    {
        public static List<string> Paths()
        {
            var list = new List<string>();
            HidD_GetHidGuid(out var guid);
            var set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);  // present, device interfaces
            var iface = new DeviceInterface { Size = Marshal.SizeOf<DeviceInterface>() };
            for (var i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref iface); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out var size, IntPtr.Zero);
                var detail = Marshal.AllocHGlobal(size);
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, size, out size, IntPtr.Zero))
                {
                    list.Add(Marshal.PtrToStringUni(detail + 4) ?? "");
                }
                Marshal.FreeHGlobal(detail);
            }
            SetupDiDestroyDeviceInfoList(set);
            return list;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DeviceInterface
        {
            public int Size;
            public Guid Class;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Caps
        {
            public ushort Usage, UsagePage, InputLen, OutputLen, FeatureLen;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps,
                NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr data, out Caps caps);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] public static extern bool HidD_GetProductString(SafeFileHandle handle, char[] buffer, int bytes);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll")]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, int index, ref DeviceInterface data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterface data, IntPtr detail, int size, out int required, IntPtr info);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    }
}
