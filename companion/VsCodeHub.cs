using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>A VS Code window running the flex-screen extension (vscode-ext/).</summary>
sealed class VsCodeWindow
{
    public required WebSocket Socket { get; init; }
    public string Name { get; set; } = "";
    public IReadOnlyList<string> Folders { get; set; } = [];
    public bool Focused { get; set; }
    public DateTime FocusedAt { get; set; } = DateTime.MinValue;
    public SemaphoreSlim SendGate { get; } = new(1, 1);

    /// <summary>The window's main folder, where git looks.</summary>
    public string? Root => Folders.Count > 0 ? Folders[0] : null;
}

/// <summary>
/// All connected VS Code windows. The deck acts on the one used most recently: the focused
/// window, or the last one that had focus.
/// </summary>
sealed class VsCodeHub
{
    readonly object _gate = new();
    readonly List<VsCodeWindow> _windows = new();

    /// <summary>A terminal or task message from a window (term.*, task.*).</summary>
    public event Action<VsCodeWindow, JsonNode>? Message;

    /// <summary>The set of windows, their folders or which one is current changed.</summary>
    public event Action? WindowsChanged;

    public VsCodeWindow? Current
    {
        get
        {
            lock (_gate)
            {
                return _windows.OrderByDescending(w => w.Focused).ThenByDescending(w => w.FocusedAt).FirstOrDefault();
            }
        }
    }

    /// <summary>Serves one window's WebSocket until it closes (LocalServer calls this).</summary>
    public async Task Serve(WebSocket socket)
    {
        var window = new VsCodeWindow { Socket = socket };
        lock (_gate) _windows.Add(window);
        try
        {
            var buffer = new byte[16 * 1024];
            var message = new MemoryStream();
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                if (JsonNode.Parse(text) is { } node) Handle(window, node);
            }
        }
        catch (Exception e) when (e is WebSocketException or JsonException or OperationCanceledException)
        {
            // The window closed or reloaded; it reconnects by itself.
        }
        finally
        {
            lock (_gate) _windows.Remove(window);
            Log.Write($"vscode: {(window.Name.Length > 0 ? window.Name : "a window")} disconnected");
            WindowsChanged?.Invoke();
        }
    }

    void Handle(VsCodeWindow window, JsonNode message)
    {
        switch ((string?)message["t"])
        {
            case "hello":
                window.Name = (string?)message["name"] ?? "";
                window.Folders = (message["folders"] as JsonArray)?.Select(f => (string?)f ?? "").Where(f => f.Length > 0).ToList() ?? [];
                SetFocus(window, (bool?)message["focused"] ?? false);
                Log.Write($"vscode: {window.Name} connected ({string.Join(", ", window.Folders)})");
                WindowsChanged?.Invoke();
                break;
            case "focus":
                SetFocus(window, (bool?)message["focused"] ?? false);
                WindowsChanged?.Invoke();
                break;
            default:
                Message?.Invoke(window, message);
                break;
        }
    }

    static void SetFocus(VsCodeWindow window, bool focused)
    {
        window.Focused = focused;
        if (focused) window.FocusedAt = DateTime.UtcNow;
    }

    public bool IsOpen(VsCodeWindow window)
    {
        lock (_gate) return _windows.Contains(window) && window.Socket.State == WebSocketState.Open;
    }

    /// <summary>Sends a command to one window.</summary>
    public void Send(VsCodeWindow window, object command) => _ = SendAsync(window, command);

    /// <summary>Sends a command (replay, interrupt, terminal, scm) to the current window.</summary>
    public void SendToCurrent(object command)
    {
        var window = Current;
        if (window == null)
        {
            Log.Write("vscode: no window connected");
            return;
        }
        _ = SendAsync(window, command);
    }

    static async Task SendAsync(VsCodeWindow window, object command)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command));
        await window.SendGate.WaitAsync();
        try
        {
            await window.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException)
        {
            Log.Write($"vscode: send failed: {e.Message}");
        }
        finally
        {
            window.SendGate.Release();
        }
    }
}
