using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlexScreen.Companion;

/// <param name="Kind">"link", "code", "color", "command" or "text", for the deck's icon.</param>
record Clip(string Id, string Text, string Kind, bool Pinned, DateTime At);

/// <summary>
/// The clipboard stack: the last text clips plus pinned ones, kept in %LOCALAPPDATA%\flex-screen
/// (the recent ones encrypted to this Windows account, so they survive the companion
/// restarting). Skips what password managers and other apps mark as private to clipboard
/// history. Pasting puts a clip back on the clipboard and presses Ctrl+V.
/// </summary>
sealed class ClipboardSource : NativeWindow
{
    const int MaxRecent = 20;
    const int MaxShown = 6;  // the deck's list
    const int MaxLength = 100_000;  // longer copies (whole files, logs) aren't kept
    const int MaxTries = 8;  // reading while another app has the clipboard open, ~1 s in all
    static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "flex-screen");
    static readonly string PinsPath = Path.Combine(Folder, "clipboard-pins.json");
    static readonly string RecentPath = Path.Combine(Folder, "clipboard-recent.bin");

    // Formats apps set to keep a copy out of clipboard managers and history.
    static readonly uint[] Private =
    [
        RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"),
        RegisterClipboardFormat("Clipboard Viewer Ignore"),
    ];
    static readonly uint CanIncludeInHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");

    readonly List<Clip> _clips = [];
    readonly ConcurrentQueue<Action> _work = new();  // deck actions, run on the UI thread (the clipboard needs it)
    // A change is read a moment after its notice, and again while another app (often Windows'
    // own clipboard history, which reads every copy too) has the clipboard open. Reading at
    // once and giving up when it was busy lost those copies.
    readonly System.Windows.Forms.Timer _read = new();
    // A change whose notice never came still shows in the clipboard's sequence number.
    readonly System.Windows.Forms.Timer _watch = new() { Interval = 1500 };
    uint _seen;  // the sequence number last read
    int _tries;
    string _pasting = "";  // the text we just put back, so it isn't recorded again as new
    int _next;
    string _logged = "";
    string _lastNote = "";
    DateTime _lastNoteAt;

    public IReadOnlyList<Clip> Shown { get; private set; } = [];

    /// <summary>Raised on the UI thread when the stack changes.</summary>
    public event Action<IReadOnlyList<Clip>>? Changed;

    /// <summary>Call on the UI thread: the listener is a message-only window.</summary>
    public void Start()
    {
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) });  // HWND_MESSAGE
        if (!AddClipboardFormatListener(Handle)) Log.Write("clipboard: can't listen for changes");
        foreach (var text in LoadPins()) _clips.Add(new Clip(NewId(), text, KindOf(text), true, DateTime.UtcNow));
        foreach (var saved in LoadRecent())
        {
            if (_clips.All(c => c.Text != saved.Text)) _clips.Add(new Clip(NewId(), saved.Text, KindOf(saved.Text), false, saved.At));
        }
        _seen = GetClipboardSequenceNumber();  // what's on it already isn't a new copy
        _read.Tick += (_, _) => ReadNow();
        _watch.Tick += (_, _) =>
        {
            if (!_read.Enabled && GetClipboardSequenceNumber() != _seen) ScheduleRead(0);
        };
        _watch.Start();
        Publish(save: false);
    }

    public void Paste(string id) => Post(() => PasteNow(id));
    public void TogglePin(string id) => Post(() => TogglePinNow(id));
    public void Clear() => Post(ClearNow);

    void Post(Action action)
    {
        _work.Enqueue(action);
        PostMessage(Handle, WmRunWork, IntPtr.Zero, IntPtr.Zero);
    }

    void PasteNow(string id)
    {
        var clip = _clips.FirstOrDefault(c => c.Id == id);
        if (clip == null) return;
        _pasting = clip.Text;
        try
        {
            Clipboard.SetText(clip.Text);
        }
        catch (ExternalException e)
        {
            Log.Write($"clipboard: can't set it: {e.Message}");
            return;
        }
        KeyInput.Chord(KeyInput.Control, 'V');
    }

    void TogglePinNow(string id)
    {
        var i = _clips.FindIndex(c => c.Id == id);
        if (i < 0) return;
        _clips[i] = _clips[i] with { Pinned = !_clips[i].Pinned };
        SavePins();
        Publish();
    }

    /// <summary>Drops everything but the pins.</summary>
    void ClearNow()
    {
        _clips.RemoveAll(c => !c.Pinned);
        Publish();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmClipboardUpdate) ScheduleRead(0);
        if (m.Msg == WmRunWork)
        {
            while (_work.TryDequeue(out var action)) action();
        }
        base.WndProc(ref m);
    }

    void ScheduleRead(int tries)
    {
        _tries = tries;
        _read.Stop();
        _read.Interval = tries == 0 ? 50 : 120;
        _read.Start();
    }

    enum Outcome { Text, NoText, Private, TooLong, Busy }

    void ReadNow()
    {
        _read.Stop();
        var sequence = GetClipboardSequenceNumber();
        if (sequence == _seen) return;
        Outcome outcome;
        string text;
        try
        {
            (outcome, text) = TryRead();
        }
        catch (Exception e)
        {
            Log.Write($"clipboard: reading failed: {e.GetType().Name}: {e.Message}");
            _seen = sequence;
            return;
        }
        if (outcome == Outcome.Busy && _tries + 1 < MaxTries)
        {
            ScheduleRead(_tries + 1);
            return;
        }
        _seen = sequence;
        switch (outcome)
        {
            case Outcome.Text: Add(text); break;
            case Outcome.Busy: Note("another app kept the clipboard open, so a copy was missed"); break;
            case Outcome.Private: Note("skipped a copy marked private (a password manager's, say)"); break;
            case Outcome.TooLong: Note($"skipped a copy over {MaxLength / 1000}k characters"); break;
            // NoText: an image or files; nothing to list.
        }
    }

    /// <summary>Why a copy wasn't listed, never what it was; the same reason at most every 10 s.</summary>
    void Note(string why)
    {
        if (why == _lastNote && DateTime.UtcNow - _lastNoteAt < TimeSpan.FromSeconds(10)) return;
        _lastNote = why;
        _lastNoteAt = DateTime.UtcNow;
        Log.Write($"clipboard: {why}");
    }

    void Add(string text)
    {
        if (text == _pasting)
        {
            _pasting = "";
            return;
        }
        var existing = _clips.FindIndex(c => c.Text == text);
        var pinned = existing >= 0 && _clips[existing].Pinned;
        if (existing >= 0) _clips.RemoveAt(existing);
        _clips.Insert(0, new Clip(NewId(), text, KindOf(text), pinned, DateTime.UtcNow));
        foreach (var old in _clips.Where(c => !c.Pinned).Skip(MaxRecent).ToList()) _clips.Remove(old);
        Publish();
    }

    /// <summary>The clipboard's text straight from Windows: one try, no waiting, so a busy clipboard is retried later.</summary>
    (Outcome, string) TryRead()
    {
        if (Private.Any(f => f != 0 && IsClipboardFormatAvailable(f))) return (Outcome.Private, "");
        if (!IsClipboardFormatAvailable(CfUnicodeText)) return (Outcome.NoText, "");
        if (!OpenClipboard(Handle)) return (Outcome.Busy, "");
        try
        {
            // "CanIncludeInClipboardHistory" set to 0, as password managers do.
            if (CanIncludeInHistory != 0 && ReadDword(CanIncludeInHistory) == 0) return (Outcome.Private, "");
            var data = GetClipboardData(CfUnicodeText);
            if (data == IntPtr.Zero) return (Outcome.NoText, "");
            var chars = (long)(ulong)GlobalSize(data) / 2;
            var p = GlobalLock(data);
            if (p == IntPtr.Zero) return (Outcome.NoText, "");
            try
            {
                var text = Marshal.PtrToStringUni(p, (int)Math.Min(chars, MaxLength + 1));
                var end = text.IndexOf('\0');
                if (end >= 0) text = text[..end];
                else if (chars > MaxLength) return (Outcome.TooLong, "");
                return string.IsNullOrWhiteSpace(text) ? (Outcome.NoText, "") : (Outcome.Text, text);
            }
            finally
            {
                GlobalUnlock(data);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    static int? ReadDword(uint format)
    {
        if (!IsClipboardFormatAvailable(format)) return null;
        var data = GetClipboardData(format);
        if (data == IntPtr.Zero || (ulong)GlobalSize(data) < 4) return null;
        var p = GlobalLock(data);
        if (p == IntPtr.Zero) return null;
        try
        {
            return Marshal.ReadInt32(p);
        }
        finally
        {
            GlobalUnlock(data);
        }
    }

    void Publish(bool save = true)
    {
        // Pins first, then the newest.
        Shown = _clips.Where(c => c.Pinned).Concat(_clips.Where(c => !c.Pinned)).Take(MaxShown).ToList();
        var summary = $"{_clips.Count} clips, {_clips.Count(c => c.Pinned)} pinned";  // counts only, never the text
        if (summary != _logged) Log.Write($"clipboard: {summary}");
        _logged = summary;
        if (save) SaveRecent();
        Changed?.Invoke(Shown);
    }

    string NewId() => (++_next).ToString();

    static readonly Regex Color = new(@"^#([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$", RegexOptions.IgnoreCase);
    static readonly Regex Link = new(@"^(https?://|www\.)\S+$", RegexOptions.IgnoreCase);
    static readonly Regex Command = new(@"^(git|npm|npx|pnpm|yarn|dotnet|pio|docker|kubectl|python|pip|cd|ls|curl|ssh|code|winget|choco|cargo|make)\s", RegexOptions.IgnoreCase);
    static readonly Regex Code = new(@"[{};]\s*$|=>|\b(const|let|var|function|def|class|return|public|private|import|#include)\b");

    static string KindOf(string text)
    {
        var t = text.Trim();
        if (Color.IsMatch(t)) return "color";
        if (Link.IsMatch(t)) return "link";
        if (Command.IsMatch(t)) return "command";
        if (Code.IsMatch(t)) return "code";
        return "text";
    }

    static List<string> LoadPins()
    {
        try
        {
            return File.Exists(PinsPath) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(PinsPath)) ?? [] : [];
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return [];
        }
    }

    void SavePins()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PinsPath, JsonSerializer.Serialize(_clips.Where(c => c.Pinned).Select(c => c.Text)));
        }
        catch (IOException e)
        {
            Log.Write($"clipboard: can't save pins: {e.Message}");
        }
    }

    record SavedClip(string Text, DateTime At);

    static List<SavedClip> LoadRecent()
    {
        try
        {
            if (!File.Exists(RecentPath)) return [];
            return JsonSerializer.Deserialize<List<SavedClip>>(ProtectedBytes.Unprotect(File.ReadAllBytes(RecentPath))) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or CryptographicException)
        {
            Log.Write($"clipboard: can't read the saved clips: {e.GetType().Name}");
            return [];
        }
    }

    void SaveRecent()
    {
        try
        {
            var recent = _clips.Where(c => !c.Pinned).Select(c => new SavedClip(c.Text, c.At)).ToList();
            if (recent.Count == 0)
            {
                File.Delete(RecentPath);
                return;
            }
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(RecentPath, ProtectedBytes.Protect(JsonSerializer.SerializeToUtf8Bytes(recent)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Log.Write($"clipboard: can't save the clips: {e.GetType().Name}");
        }
    }

    const int WmClipboardUpdate = 0x031D;
    const int WmRunWork = 0x8001;  // WM_APP + 1
    const uint CfUnicodeText = 13;

    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr mem);
}
