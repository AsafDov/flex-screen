using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// The deck firmware that comes with this companion (firmware\ next to the exe, put there by
/// the install kit), and updating the deck to it over the link, the way `pio run -t upload`
/// does (protocol/v0.md, "Firmware updates"). So any PC with the companion can update the
/// deck, PlatformIO or not.
/// </summary>
/// <remarks>
/// Nothing on the deck changes until the whole image is in and checked: the deck writes its
/// spare app slot and only switches to it once the MD5 of what came, the image's checksum
/// and its SHA-256 (read back from flash) all agree. Firmware 0.21.0 and later also stay on
/// trial until they've heard from the PC, so one that can't start goes back to the old.
/// </remarks>
sealed class DeckFirmware
{
    public sealed record Image(string Version, byte[] Data, string Md5);

    const int Chunk = 4096;  // one flash sector per line, as the deck expects
    const ushort Esp32S3 = 9;  // esp_chip_id_t in the image header
    static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(30);   // the deck erases as it goes
    static readonly TimeSpan AckWait = TimeSpan.FromSeconds(10);
    static readonly TimeSpan DoneWait = TimeSpan.FromSeconds(30);    // reads the image back to check it
    static readonly TimeSpan BackWait = TimeSpan.FromSeconds(60);    // restart, USB, the companion's handshake

    readonly DeckLink _link;
    readonly Action<Toast> _toast;
    readonly Action<string, string, ToolTipIcon, Action?> _notify;
    readonly SynchronizationContext _ui;
    readonly BlockingCollection<JsonNode> _replies = new();
    volatile bool _running;
    volatile string? _deckVersion;
    string _offeredFor = "";
    bool _toldNewer;

    public DeckFirmware(DeckLink link, Action<Toast> toast, Action<string, string, ToolTipIcon, Action?> notify)
    {
        _link = link;
        _toast = toast;
        _notify = notify;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Bundled = Load();
        MenuItem = new ToolStripMenuItem { Visible = false };
        MenuItem.Click += (_, _) => Confirm();
        link.Message += OnMessage;
    }

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "firmware");

    /// <summary>The firmware this companion brings, or null (a build without one, or a file that doesn't check out).</summary>
    public Image? Bundled { get; }

    /// <summary>Whether an update is going on.</summary>
    public bool Running => _running;

    /// <summary>The tray menu's line while it runs ("Updating the deck: 42%"), else null.</summary>
    public string? Status { get; private set; }

    /// <summary>The tray menu's item: there when the deck runs older firmware than <see cref="Bundled"/>.</summary>
    public ToolStripMenuItem MenuItem { get; }

    /// <summary>The deck runs newer firmware than this companion brings: worth looking for a newer companion.</summary>
    public event Action? DeckIsNewer;

    /// <summary>Call as the tray menu opens.</summary>
    public void RefreshMenu()
    {
        var deck = _deckVersion;
        var offer = !_running && Bundled != null && deck != null && _link.PortName != null && Compare(deck, Bundled.Version) < 0;
        MenuItem.Visible = _running || offer;
        MenuItem.Enabled = !_running;
        MenuItem.Text = _running ? Status ?? "Updating the deck…" : $"Update the deck's firmware to {Bundled?.Version}…";
    }

    void OnMessage(JsonNode message)
    {
        var t = (string?)message["t"] ?? "";
        if (_running && (t == "hello" || t.StartsWith("ota", StringComparison.Ordinal))) _replies.Add(message);
        if (t != "hello") return;
        _deckVersion = (string?)message["fw"];
        if (!_running) Offer();
    }

    /// <summary>On the deck's hello: if it runs older firmware than this companion brings, says so (once per version).</summary>
    void Offer()
    {
        var deck = _deckVersion;
        if (Bundled == null || deck == null) return;
        var order = Compare(deck, Bundled.Version);
        if (order > 0 && !_toldNewer)
        {
            _toldNewer = true;
            Log.Write($"firmware: the deck runs {deck}, newer than this companion's {Bundled.Version}");
            DeckIsNewer?.Invoke();
        }
        if (order >= 0 || _offeredFor == deck) return;
        _offeredFor = deck;
        Log.Write($"firmware: the deck runs {deck}; {Bundled.Version} came with this companion");
        _notify("Deck firmware update", $"Firmware {Bundled.Version} is ready for the deck, which runs {deck}. Click to update it.",
                ToolTipIcon.Info, Confirm);
        _toast(new Toast("attention", $"Firmware {Bundled.Version} is ready · tap to update", "fw.update"));
    }

    /// <summary>From the tray: asks first, then updates.</summary>
    void Confirm()
    {
        if (Bundled == null || _running) return;
        var deck = _deckVersion ?? "?";
        var answer = MessageBox.Show(
            $"Update the deck's firmware from {deck} to {Bundled.Version}?\n\n" +
            "It goes over the USB cable and takes about half a minute; the deck restarts at the end. " +
            "Keep it plugged in meanwhile. If anything goes wrong on the way, the deck keeps the firmware it has.",
            "flex-screen: update the deck", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes) Start();
    }

    /// <summary>Updates the deck to <see cref="Bundled"/> (the deck's "fw.update", or after <see cref="Confirm"/>).</summary>
    public void Start()
    {
        if (_running) return;
        if (Bundled == null)
        {
            Log.Write("firmware: no firmware came with this companion");
            _toast(new Toast("attention", "No deck firmware came with this companion"));
            return;
        }
        if (_link.PortName == null)
        {
            _notify("Deck firmware update", "The deck isn't connected.", ToolTipIcon.Warning, null);
            return;
        }
        _running = true;
        new Thread(() => Run(Bundled)) { IsBackground = true, Name = "deck-firmware" }.Start();
    }

    sealed class Failed(string message) : Exception(message);

    void Run(Image image)
    {
        var from = _deckVersion ?? "?";
        var session = _link.Connection;
        var sent = false;  // past ota_done: the deck is restarting into the new firmware
        while (_replies.TryTake(out _)) { }
        Log.Write($"firmware: updating the deck from {from} to {image.Version} ({image.Data.Length} bytes)");
        SetStatus("Updating the deck…");
        // Firmware 0.21.0 and later show their own progress screen; older ones only this.
        _toast(new Toast("attention", "Updating the deck's firmware · keep it plugged in"));
        Thread.Sleep(200);
        _link.Hold();
        try
        {
            Send(new { t = "ota", size = image.Data.Length, md5 = image.Md5, version = image.Version });
            Wait("ota_ready", ReadyWait, session);
            var shown = -1;
            for (var off = 0; off < image.Data.Length; off += Chunk)
            {
                var length = Math.Min(Chunk, image.Data.Length - off);
                Send(new { t = "ota_data", off, b64 = Convert.ToBase64String(image.Data, off, length) });
                var ack = Wait("ota_ack", AckWait, session);
                if ((long?)ack["off"] != off + length)
                    throw new Failed($"the deck counted {(long?)ack["off"]} bytes where {off + length} were sent");
                var percent = (int)((off + length) * 100L / image.Data.Length);
                if (percent != shown)
                {
                    shown = percent;
                    SetStatus($"Updating the deck: {percent}%");
                }
            }
            Send(new { t = "ota_end" });
            Wait("ota_done", DoneWait, session);
            sent = true;
            Log.Write("firmware: the deck took the image and is restarting");
            SetStatus("Updating the deck: restarting");
            _link.Release();

            var hello = Wait("hello", BackWait, session: null);
            var now = (string?)hello["fw"] ?? "?";
            if (Compare(now, image.Version) == 0)
            {
                Log.Write($"firmware: the deck runs {now} now");
                _notify("Deck updated", $"The deck runs firmware {now} now.", ToolTipIcon.Info, null);
                _toast(new Toast("ok", $"Firmware {now} installed"));
            }
            else
            {
                Log.Write($"firmware: the deck came back with {now}, not {image.Version}: the new firmware didn't stay");
                _notify("Deck firmware update", $"The new firmware didn't start, so the deck went back to {now}. The companion's log has the details.",
                        ToolTipIcon.Warning, null);
                _toast(new Toast("error", $"Firmware {image.Version} didn't start · back on {now}"));
            }
        }
        catch (Failed e) when (!sent)
        {
            // Nothing changed on the deck: it abandons the update and keeps running what it had.
            Log.Write($"firmware: the update stopped: {e.Message}");
            _link.Release();
            _link.SendForUpdate(new { t = "sync" });  // its hello brings the state held back meanwhile
            _notify("Deck firmware update", $"The update stopped: {e.Message}. Nothing changed on the deck; it still runs {from}. " +
                    "Try again from the tray menu.", ToolTipIcon.Warning, null);
            _toast(new Toast("error", $"Firmware update stopped · still on {from}"));
        }
        catch (Failed e)
        {
            Log.Write($"firmware: the deck didn't come back after the update: {e.Message}");
            _ui.Post(_ => ShowRecovery(from), null);
        }
        finally
        {
            _link.Release();
            _running = false;
            SetStatus(null);
        }
    }

    void Send(object message)
    {
        if (!_link.SendForUpdate(message)) throw new Failed("the link to the deck closed");
    }

    /// <summary>
    /// The deck's next reply of this type. An ota_error, or the link closing on the way
    /// (<paramref name="session"/>: the connection it has to stay on), ends the update.
    /// </summary>
    JsonNode Wait(string type, TimeSpan timeout, int? session)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (session != null && (_link.Connection != session || _link.PortName == null)) throw new Failed("the deck's connection dropped");
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) throw new Failed(type == "hello" ? "the deck didn't come back" : $"the deck didn't answer in time (waiting for {type})");
            if (!_replies.TryTake(out var reply, left < TimeSpan.FromMilliseconds(250) ? left : TimeSpan.FromMilliseconds(250))) continue;
            var t = (string?)reply["t"];
            if (t == type) return reply;
            if (t == "ota_error") throw new Failed($"the deck said \"{(string?)reply["msg"]}\"");
        }
    }

    void SetStatus(string? status)
    {
        Status = status;
        _ui.Post(_ => RefreshMenu(), null);
    }

    /// <summary>The deck took the new firmware but hasn't come back: what to do.</summary>
    static void ShowRecovery(string from) => MessageBox.Show(
        "The deck hasn't come back since its firmware update.\n\n" +
        $"1. Unplug the deck and plug it back in. If the new firmware can't start, the deck goes back to the one it had ({from}).\n\n" +
        "2. If the screen stays dark or the deck doesn't connect, flash it through the chip's own bootloader, from a PC with " +
        "PlatformIO and the flex-screen repo: hold BOOT, tap RESET, let go of BOOT, plug the deck's UART port into the PC, and run\n\n" +
        "    pio run -d firmware -e deck-esptool -t upload\n\n" +
        "Then tap RESET. The README's Setup > Firmware section has the details.",
        "flex-screen: the deck didn't come back", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>
    /// firmware\firmware.json and the image it names, if both check out: its size and SHA-256,
    /// an ESP32 app image (magic 0xE9) for the ESP32-S3, with the app description where
    /// ESP-IDF puts it. Anything else is never sent to the deck.
    /// </summary>
    static Image? Load()
    {
        var manifestPath = Path.Combine(Folder, "firmware.json");
        if (!File.Exists(manifestPath)) return null;
        try
        {
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            var version = (string?)manifest["version"] ?? throw new InvalidDataException("no version");
            var data = File.ReadAllBytes(Path.Combine(Folder, Path.GetFileName((string?)manifest["file"] ?? "deck.bin")));
            if ((long?)manifest["size"] != data.Length) throw new InvalidDataException("the size doesn't match");
            if (!string.Equals((string?)manifest["sha256"], Convert.ToHexStringLower(SHA256.HashData(data)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("the SHA-256 doesn't match");
            if (data.Length < 64 || data[0] != 0xE9) throw new InvalidDataException("not an ESP32 app image");
            if (BitConverter.ToUInt16(data, 12) != Esp32S3) throw new InvalidDataException("not built for the ESP32-S3");
            if (BitConverter.ToUInt32(data, 32) != 0xABCD5432) throw new InvalidDataException("no app description");
            if (Compare(version, version) == null) throw new InvalidDataException($"the version \"{version}\" isn't x.y.z");
            Log.Write($"firmware: {version} came with this companion ({data.Length} bytes)");
            return new Image(version, data, Convert.ToHexStringLower(MD5.HashData(data)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException)
        {
            Log.Write($"firmware: {manifestPath} doesn't check out, so the deck won't be offered it: {e.Message}");
            return null;
        }
    }

    /// <summary>-1, 0 or 1 for two x.y.z versions; null if either isn't one.</summary>
    public static int? Compare(string a, string b) =>
        Version.TryParse(Plain(a), out var x) && Version.TryParse(Plain(b), out var y) ? x.CompareTo(y) : null;

    /// <summary>"v0.21.0+abc123" → "0.21.0".</summary>
    public static string Plain(string version) => version.TrimStart('v', 'V').Split('+')[0].Trim();
}
