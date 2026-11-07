using System.IO.Ports;
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

    readonly object _writeGate = new();
    SerialPort? _port;

    /// <summary>Raised on the link thread for every message from the deck.</summary>
    public event Action<JsonNode>? Message;

    public string? PortName => _port is { IsOpen: true } p ? p.PortName : null;

    public void Start() => new Thread(Run) { IsBackground = true, Name = "deck-link" }.Start();

    public void Send(object message)
    {
        var port = _port;
        if (port is not { IsOpen: true }) return;
        var line = JsonSerializer.Serialize(message) + "\n";
        lock (_writeGate)
        {
            try
            {
                port.Write(line);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException)
            {
                Log.Write($"link: write failed: {e.Message}");
            }
        }
    }

    void Run()
    {
        var reportedMissing = false;
        while (true)
        {
            var name = FindPort();
            if (name == null)
            {
                if (!reportedMissing) Log.Write("link: deck not found, waiting");
                reportedMissing = true;
                Thread.Sleep(2000);
                continue;
            }
            reportedMissing = false;
            try
            {
                Session(name);
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
                try { port?.Dispose(); } catch (IOException) { }
            }
            Thread.Sleep(1000);
        }
    }

    void Session(string name)
    {
        // Never 1200 baud: the deck treats that as "reboot into the bootloader".
        var port = new SerialPort(name, 115200)
        {
            DtrEnable = true,  // the deck sends its hello when it sees DTR
            RtsEnable = true,
            NewLine = "\n",
            Encoding = new UTF8Encoding(false),
            WriteTimeout = 1000,
        };
        port.Open();
        _port = port;
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
                port.ReadTimeout = SerialPort.InfiniteTimeout;
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

    /// <summary>
    /// The deck only writes while DTR and RTS are both up, and learns about them from the
    /// line-state requests Windows sends when they change. If the port opened while Windows
    /// was still installing the deck's driver, those can be lost, so drop and raise them.
    /// (Firmware 0.1.2+ ignores DTR/RTS patterns; older builds read some as esptool's
    /// "reboot into the bootloader" sequence.)
    /// </summary>
    void Handshake(SerialPort port)
    {
        port.RtsEnable = false;
        port.DtrEnable = false;
        Thread.Sleep(50);
        port.DtrEnable = true;
        port.RtsEnable = true;
        Thread.Sleep(100);  // let the deck see the lines up before it has to answer
        Send(new { t = "sync" });  // the deck says hello only in reply to this
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
}
