using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// Teams' local API for third-party devices (ws://127.0.0.1:8124, protocol 2.0.0; Teams ›
/// Settings › Privacy › Manage API), the one Stream Deck uses: a meeting's real mute, camera
/// and hand state, and leave, reactions and its chat. The first action asks to pair in Teams;
/// the token is kept encrypted. <see cref="CallSource"/> uses it for Teams' calls.
/// </summary>
sealed class TeamsApi
{
    const string Port = "8124";
    static readonly string TokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "flex-screen", "teams-token.bin");

    readonly object _gate = new();
    ClientWebSocket? _socket;
    JsonNode? _state;  // meetingUpdate: meetingState and meetingPermissions
    int _requestId;
    DateTime _tried = DateTime.MinValue;

    /// <summary>Teams' last meetingUpdate (meetingState, meetingPermissions); null until one comes, and while not connected.</summary>
    public JsonNode? State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    public bool Connected
    {
        get
        {
            lock (_gate) return _socket?.State == WebSocketState.Open;
        }
    }

    /// <summary>Raised when Teams sends a meeting update.</summary>
    public event Action? Changed;

    /// <summary>Connects, if it isn't (at most every 10 s); nothing happens while the API is off in Teams.</summary>
    public async Task EnsureAsync()
    {
        lock (_gate)
        {
            if (_socket is { State: WebSocketState.Open or WebSocketState.Connecting }) return;
            if (DateTime.UtcNow - _tried < TimeSpan.FromSeconds(10)) return;
            _tried = DateTime.UtcNow;
        }
        var socket = new ClientWebSocket();
        var token = LoadToken();
        var url = $"ws://127.0.0.1:{Port}?token={Uri.EscapeDataString(token)}&protocol-version=2.0.0" +
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
        lock (_gate) _socket = socket;
        Log.Write("call: connected to Teams' API");
        _ = ReceiveAsync(socket);
    }

    async Task ReceiveAsync(ClientWebSocket socket)
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
                    lock (_gate) _state = update.DeepClone();
                    Changed?.Invoke();
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
                if (_socket == socket)
                {
                    _socket = null;
                    _state = null;
                }
            }
            socket.Dispose();
        }
    }

    /// <summary>An action for the meeting ("toggle-mute", "send-reaction"…); false when the API isn't connected.</summary>
    public bool Send(string action, JsonObject? parameters = null)
    {
        ClientWebSocket? socket;
        lock (_gate) socket = _socket;
        if (socket is not { State: WebSocketState.Open }) return false;
        var body = new JsonObject { ["action"] = action, ["parameters"] = parameters ?? new JsonObject(), ["requestId"] = Interlocked.Increment(ref _requestId) };
        _ = socket.SendAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
        return true;
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
}
