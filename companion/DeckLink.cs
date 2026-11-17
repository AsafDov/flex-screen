using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace FlexScreen.Companion;

/// <summary>
/// The serial link to the deck (protocol/v0.md): finds its USB serial port, keeps it open,
/// reconnects after unplugs and reflashes, and reads newline-delimited JSON.
/// </summary>
sealed class DeckLink
{
    // The deck's USB ID (pid.codes test ID), set in firmware/src/link.cpp.
    const string DeckUsbId = "VID_1209&PID_0001";
    const string UsbEnumKey = @"SYSTEM\CurrentControlSet\Enum\USB";

    /// <summary>
    /// For testing: FLEX_SCREEN_DECK=tcp:127.0.0.1:48160 talks to the deck simulator
    /// (simulator/README.md) instead of the deck's USB port.
    /// </summary>
    internal static readonly string? Simulator = Environment.GetEnvironmentVariable("FLEX_SCREEN_DECK") is { } deck &&
                                        deck.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ? deck[4..] : null;

    // The deck reads between frames, and a busy stretch (page builds, the keymap, icons)
    // let its 32 KB receive buffer overflow when everything came at once at startup:
    // album art, eight window icons and every topic from several threads. Writes are held
    // to about 64 KB/s after an 8 KB burst, so the deck can stall for half a second
    // without losing a byte.
    const double BytesPerSecond = 64 * 1024;
    const double BurstBytes = 8 * 1024;

    readonly object _writeGate = new();
    double _budget = BurstBytes;
    long _budgetAt = System.Diagnostics.Stopwatch.GetTimestamp();
    IPort? _port;
    volatile bool _held;
    int _connection;

    /// <summary>Raised on the link thread for every message from the deck.</summary>
    public event Action<JsonNode>? Message;

    public string? PortName => _port is { IsOpen: true } p ? p.Name : null;

    /// <summary>Counts connections: a firmware update notices the link went (and came back) meanwhile.</summary>
    public int Connection => _connection;

    public void Start() => new Thread(Run) { IsBackground = true, Name = "deck-link" }.Start();

    /// <summary>
    /// While a firmware update runs, only its own messages (<see cref="SendForUpdate"/>) go
    /// to the deck: state pushed meanwhile could overflow the deck's receive buffer while it
    /// writes flash. Ends with <see cref="Release"/>, or when the connection ends.
    /// </summary>
    public void Hold() => _held = true;

    public void Release() => _held = false;

    public void Send(object message)
    {
        if (!_held) Write(message);
    }

    /// <summary>
    /// A firmware update's own message: goes even while the link is held, and without the
    /// pacing (each part waits for the deck's ack, so its buffer never holds more than one).
    /// False if it couldn't be written.
    /// </summary>
    public bool SendForUpdate(object message) => Write(message, pace: false);

    bool Write(object message, bool pace = true)
    {
        var port = _port;
        if (port is not { IsOpen: true }) return false;
        var line = JsonSerializer.Serialize(message) + "\n";
        lock (_writeGate)
        {
            if (pace) Pace(Encoding.UTF8.GetByteCount(line));
            try
            {
                port.Write(line);
                return true;
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or SocketException)
            {
                Log.Write($"link: write failed: {e.Message}");
                return false;
            }
        }
    }

    /// <summary>Waits as long as it takes to stay under <see cref="BytesPerSecond"/> (a token bucket).</summary>
    void Pace(int bytes)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        _budget = Math.Min(BurstBytes, _budget + (now - _budgetAt) * BytesPerSecond / System.Diagnostics.Stopwatch.Frequency);
        _budgetAt = now;
        _budget -= bytes;
        if (_budget < 0) Thread.Sleep((int)Math.Ceiling(-_budget * 1000 / BytesPerSecond));
    }

    void Run()
    {
        var reportedMissing = false;
        if (Simulator != null) Log.Write($"link: talking to the simulator on {Simulator} (FLEX_SCREEN_DECK)");
        while (true)
        {
            var name = Simulator ?? FindPort();
            if (name == null)
            {
                if (!reportedMissing) Log.Write("link: deck not found, waiting");
                reportedMissing = true;
                Thread.Sleep(2000);
                continue;
            }
            try
            {
                Session(name);
            }
            catch (SocketException) when (Simulator != null && _port == null)
            {
                // The simulator isn't running (or is restarting): like an unplugged deck.
                if (!reportedMissing) Log.Write("link: deck not found, waiting");
                reportedMissing = true;
            }
            catch (Exception e)
            {
                // Unplugs and deck reboots surface as IOException, OperationCanceledException
                // and others depending on timing; all of them mean "reconnect".
                Log.Write($"link: {name} closed: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                var port = _port;
                _port = null;
                _held = false;
                try { port?.Dispose(); } catch (IOException) { }
            }
            Thread.Sleep(1000);
        }
    }

    void Session(string name)
    {
        IPort port = Simulator != null ? new TcpPort(name) : new SerialLinkPort(name);
        _port = port;
        Interlocked.Increment(ref _connection);
        Log.Write($"link: connected on {name}");
        Handshake(port);

        // Until the deck has said something valid, a quiet line means it missed the
        // handshake. The deck takes ~3 s to boot after a reset before it answers.
        port.ReadTimeout = 5000;
        var heard = false;
        while (true)
        {
            string line;
            try
            {
                line = port.ReadLine().Trim();
            }
            catch (TimeoutException) when (!heard)
            {
                Log.Write("link: no reply yet, retrying the handshake");
                Handshake(port);
                continue;
            }
            if (line.Length == 0) continue;
            var node = Parse(line);
            if (node == null) continue;
            if (!heard)
            {
                heard = true;
                port.ReadTimeout = Timeout.Infinite;
            }
            Message?.Invoke(node);
        }
    }

    /// <summary>
    /// Parses one line. A handshake that lands while the deck is mid-write cuts that
    /// message short and glues the next one onto it, so fall back to the last message
    /// start in the line.
    /// </summary>
    static JsonNode? Parse(string line)
    {
        try
        {
            return JsonNode.Parse(line);
        }
        catch (JsonException)
        {
        }
        var start = line.LastIndexOf("{\"t\":", StringComparison.Ordinal);
        if (start > 0)
        {
            try
            {
                return JsonNode.Parse(line[start..]);
            }
            catch (JsonException)
            {
            }
        }
        Log.Write($"link: bad line: {(line.Length > 200 ? line[..200] + "…" : line)}");
        return null;
    }

    /// <summary>Asks for the deck's hello (it says hello only in reply to "sync"), after resetting the line state.</summary>
    void Handshake(IPort port)
    {
        port.ResetLines();
        Write(new { t = "sync" });  // never held: a held link is one mid-update, which needs the hello too
    }

    /// <summary>The COM port Windows assigned to the deck's serial interface, if it's plugged in.</summary>
    static string? FindPort()
    {
        var present = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var usb = Registry.LocalMachine.OpenSubKey(UsbEnumKey);
        if (usb == null) return null;
        foreach (var device in usb.GetSubKeyNames())
        {
            if (!device.StartsWith(DeckUsbId, StringComparison.OrdinalIgnoreCase)) continue;
            using var deviceKey = usb.OpenSubKey(device);
            if (deviceKey == null) continue;
            foreach (var instance in deviceKey.GetSubKeyNames())
            {
                using var parameters = deviceKey.OpenSubKey($@"{instance}\Device Parameters");
                if (parameters?.GetValue("PortName") is string port && present.Contains(port)) return port;
            }
        }
        return null;
    }

    /// <summary>What the link reads and writes: the deck's serial port, or the simulator's TCP port.</summary>
    interface IPort : IDisposable
    {
        string Name { get; }
        bool IsOpen { get; }
        int ReadTimeout { set; }  // ms, or Timeout.Infinite
        string ReadLine();        // TimeoutException when nothing came in time
        void Write(string text);
        void ResetLines();        // what the handshake does before it asks for hello
    }

    sealed class SerialLinkPort : IPort
    {
        readonly SerialPort _port;

        public SerialLinkPort(string name)
        {
            // Never 1200 baud: the deck treats that as "reboot into the bootloader".
            _port = new SerialPort(name, 115200)
            {
                DtrEnable = true,  // the deck only writes while DTR and RTS are up
                RtsEnable = true,
                NewLine = "\n",
                Encoding = new UTF8Encoding(false),
                WriteTimeout = 1000,
            };
            _port.Open();
        }

        public string Name => _port.PortName;
        public bool IsOpen => _port.IsOpen;
        public int ReadTimeout { set => _port.ReadTimeout = value == Timeout.Infinite ? SerialPort.InfiniteTimeout : value; }
        public string ReadLine() => _port.ReadLine();
        public void Write(string text) => _port.Write(text);
        public void Dispose() => _port.Dispose();

        /// <summary>
        /// The deck only writes while DTR and RTS are both up, and learns about them from the
        /// line-state requests Windows sends when they change. If the port opened while Windows
        /// was still installing the deck's driver, those can be lost, so drop and raise them.
        /// (Firmware 0.1.2+ ignores DTR/RTS patterns; older builds read some as esptool's
        /// "reboot into the bootloader" sequence.)
        /// </summary>
        public void ResetLines()
        {
            _port.RtsEnable = false;
            _port.DtrEnable = false;
            Thread.Sleep(50);
            _port.DtrEnable = true;
            _port.RtsEnable = true;
            Thread.Sleep(100);  // let the deck see the lines up before it has to answer
        }
    }

    /// <summary>The simulator's link: the same lines over TCP, no line state.</summary>
    sealed class TcpPort : IPort
    {
        readonly TcpClient _client;
        readonly NetworkStream _stream;
        readonly byte[] _buffer = new byte[64 * 1024];
        readonly MemoryStream _line = new();
        int _start, _end;

        public TcpPort(string address)
        {
            var colon = address.LastIndexOf(':');
            _client = new TcpClient { NoDelay = true };
            _client.Connect(address[..colon], int.Parse(address[(colon + 1)..]));
            _stream = _client.GetStream();
            Name = address;
        }

        public string Name { get; }
        public bool IsOpen => _client.Connected;
        public int ReadTimeout { set => _client.ReceiveTimeout = value == Timeout.Infinite ? 0 : value; }
        public void Write(string text) => _stream.Write(Encoding.UTF8.GetBytes(text));
        public void ResetLines() { }

        public void Dispose()
        {
            _stream.Dispose();
            _client.Dispose();
        }

        public string ReadLine()
        {
            while (true)
            {
                if (_start == _end)
                {
                    int n;
                    try
                    {
                        n = _stream.Read(_buffer, 0, _buffer.Length);
                    }
                    catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
                    {
                        throw new TimeoutException("no reply from the simulator");
                    }
                    if (n == 0) throw new IOException("the simulator closed the link");
                    _start = 0;
                    _end = n;
                }
                var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                if (newline < 0)
                {
                    _line.Write(_buffer, _start, _end - _start);
                    _start = _end;
                    continue;
                }
                _line.Write(_buffer, _start, newline - _start);
                _start = newline + 1;
                var text = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length);
                _line.SetLength(0);
                return text;
            }
        }
    }
}
