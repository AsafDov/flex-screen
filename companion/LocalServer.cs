using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// The companion's endpoint on this PC only (http://localhost:48151): Claude Code hooks
/// POST their events to /claude, Claude Code calls the deck's tools on /mcp, scripts (flexctl)
/// theirs on /api/…, the VS Code extension keeps a WebSocket open on /vscode, and the
/// browser extension one on /browser.
/// </summary>
sealed class LocalServer
{
    public const int Port = 48151;

    /// <summary>A companion playing with the simulator runs beside the real one, one port up.</summary>
    static readonly int Listening = DeckLink.Simulator != null ? Port + 1 : Port;

    readonly HttpListener _listener = new();

    /// <summary>A Claude Code hook event (the JSON the hook received on stdin) and where the request came from.</summary>
    public event Action<JsonNode, IPEndPoint?>? ClaudeEvent;

    /// <summary>A PermissionRequest hook waiting for a decision: the JSON to answer with, or null for none.</summary>
    public event Func<JsonNode, Task<string?>>? ClaudePermission;

    /// <summary>The stop hook asking whether its session is to stop (the deck's Stop): true to stop it now.</summary>
    public event Func<string, bool>? ClaudeStop;

    /// <summary>The Claude plan's usage, percent of the 5-hour window and of the week, read by a server's hooks.</summary>
    public event Action<double, double?>? ClaudeUsage;

    /// <summary>A VS Code window connected; the handler owns the socket until it closes.</summary>
    public event Func<WebSocket, Task>? VsCodeConnected;

    /// <summary>A message posted to /test as if the deck had sent it (see <see cref="FromLocalTool"/>).</summary>
    public event Action<JsonNode>? TestMessage;

    /// <summary>Claude Code calling the deck's tools (MCP over HTTP, <see cref="DeckTools"/>); the handler answers.</summary>
    public event Func<HttpListenerContext, Task>? Mcp;

    /// <summary>A script's note, question or progress (<see cref="ScriptApi"/>); the handler answers.</summary>
    public event Func<HttpListenerContext, Task>? Api;

    /// <summary>A command for the deck from /deck; a screen shot comes back as PNG bytes.</summary>
    public event Func<JsonNode, Task<byte[]?>>? DeckCommand;

    /// <summary>
    /// Requests from tools on this PC (curl, the hooks), never from a web page: browsers add
    /// an Origin header to cross-site requests, and can't send a custom header without a
    /// CORS preflight this server never approves.
    /// </summary>
    static bool FromLocalTool(HttpListenerRequest request, bool needsTestHeader) =>
        request.IsLocal && request.Headers["Origin"] == null && (!needsTestHeader || request.Headers["X-Flex-Screen-Test"] == "1");

    /// <summary>
    /// Not from a web page: a browser lets any page open a WebSocket to localhost, but says
    /// which page with an http(s) Origin. The VS Code extension sends none (or not a web one).
    /// </summary>
    static bool NotFromWebPage(HttpListenerRequest request) =>
        request.IsLocal && !(request.Headers["Origin"] is { } origin &&
                             (origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase)));

    /// <summary>From a browser extension: its Origin is chrome-extension://its-id (Chrome and Edge).</summary>
    static bool FromBrowserExtension(HttpListenerRequest request) =>
        request.IsLocal && request.Headers["Origin"] is { } origin &&
        (origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) || origin.StartsWith("extension://", StringComparison.OrdinalIgnoreCase));

    public void Start()
    {
        // "localhost" prefixes need no URL reservation, so this runs without admin rights.
        _listener.Prefixes.Add($"http://localhost:{Listening}/");
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException e)
        {
            Log.Write($"server: can't listen on {Listening}: {e.Message}");
            return;
        }
        Log.Write($"server: listening on localhost:{Listening}");
        _ = Task.Run(AcceptLoop);
    }

    async Task AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(() => Handle(context));
        }
    }

    async Task Handle(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            switch (request.Url?.AbsolutePath)
            {
                case "/claude" when request.HttpMethod == "POST" && FromLocalTool(request, needsTestHeader: false):
                    using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                    {
                        var body = await reader.ReadToEndAsync();
                        if (JsonNode.Parse(body) is { } hook)
                        {
                            // A server's hooks (install/remote-claude-hooks.sh) send the model of
                            // Claude's last reply, read from a transcript the PC can't open.
                            if (request.Headers["X-Claude-Model"] is { Length: > 0 } model && hook is JsonObject fields) fields["flex_reply_model"] = model;
                            ClaudeEvent?.Invoke(hook, request.RemoteEndPoint);  // before replying: the connection names the caller
                        }
                    }
                    response.StatusCode = 204;
                    response.Close();
                    return;

                // From a server's hooks (install/remote-claude-hooks.sh): Claude Code's own /usage
                // run there, {"five_hour": 42, "seven_day": 17}. Hooks don't carry the figure.
                case "/claude/usage" when request.HttpMethod == "POST" && FromLocalTool(request, needsTestHeader: false):
                    using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                    {
                        if (JsonNode.Parse(await reader.ReadToEndAsync()) is JsonObject usage &&
                            usage["five_hour"] is JsonValue five && five.TryGetValue(out double fiveHour) && fiveHour is >= 0 and <= 1000)
                        {
                            double? week = usage["seven_day"] is JsonValue w && w.TryGetValue(out double sevenDay) ? sevenDay : null;
                            ClaudeUsage?.Invoke(fiveHour, week);
                        }
                    }
                    response.StatusCode = 204;
                    response.Close();
                    return;

                // Claude waits for this one before each tool call: answer at once.
                case "/claude/stop" when request.HttpMethod == "POST" && FromLocalTool(request, needsTestHeader: false):
                {
                    string? session;
                    using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                        session = (string?)JsonNode.Parse(await reader.ReadToEndAsync())?["session_id"];
                    if (session is { Length: > 0 } && ClaudeStop?.Invoke(session) == true)
                    {
                        var bytes = Encoding.UTF8.GetBytes("{\"continue\":false,\"stopReason\":\"Stopped from the flex-screen deck\"}");
                        response.ContentType = "application/json";
                        response.ContentLength64 = bytes.Length;
                        await response.OutputStream.WriteAsync(bytes);
                    }
                    else response.StatusCode = 204;
                    response.Close();
                    return;
                }

                // Held open until the deck answers, Claude moves on, or the hook gives up.
                case "/claude/permission" when request.HttpMethod == "POST" && FromLocalTool(request, needsTestHeader: false):
                {
                    string? answer = null;
                    using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                    {
                        if (JsonNode.Parse(await reader.ReadToEndAsync()) is { } hook)
                        {
                            // The request is waiting before the session's state says so, so the
                            // "needs your approval" toast knows the deck's banner has it.
                            var waiting = ClaudePermission?.Invoke(hook);
                            ClaudeEvent?.Invoke(hook, request.RemoteEndPoint);
                            if (waiting != null) answer = await waiting;
                        }
                    }
                    if (answer == null)
                    {
                        response.StatusCode = 204;
                    }
                    else
                    {
                        var bytes = Encoding.UTF8.GetBytes(answer);
                        response.ContentType = "application/json";
                        response.ContentLength64 = bytes.Length;
                        await response.OutputStream.WriteAsync(bytes);
                    }
                    response.Close();
                    return;
                }

                case "/test" when request.HttpMethod == "POST" && FromLocalTool(request, needsTestHeader: true):
                    using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                    {
                        if (JsonNode.Parse(await reader.ReadToEndAsync()) is { } message) TestMessage?.Invoke(message);
                    }
                    response.StatusCode = 204;
                    response.Close();
                    return;

                // For checking the deck from this PC: {"t":"page","i":n} or {"t":"shot"} (answered with a PNG).
                case "/deck" when request.HttpMethod == "POST" && FromLocalTool(request, needsTestHeader: true):
                {
                    using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                    var png = JsonNode.Parse(await reader.ReadToEndAsync()) is { } command && DeckCommand is { } run
                        ? await run(command)
                        : null;
                    if (png == null)
                    {
                        response.StatusCode = 204;
                    }
                    else
                    {
                        response.ContentType = "image/png";
                        response.ContentLength64 = png.Length;
                        await response.OutputStream.WriteAsync(png);
                    }
                    response.Close();
                    return;
                }

                case "/mcp" when FromLocalTool(request, needsTestHeader: false) && Mcp is { } serve:
                    await serve(context);
                    return;

                case { } path when path.StartsWith("/api/", StringComparison.Ordinal) && FromLocalTool(request, needsTestHeader: false) && Api is { } api:
                    await api(context);
                    return;

                case "/vscode" when request.IsWebSocketRequest && NotFromWebPage(request):
                    var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
                    if (VsCodeConnected is { } connected) await connected(ws);
                    return;

                case "/browser" when request.IsWebSocketRequest && FromBrowserExtension(request):
                    await BrowserTabs.Serve((await context.AcceptWebSocketAsync(null)).WebSocket);
                    return;

                default:
                    response.StatusCode = 404;
                    response.Close();
                    return;
            }
        }
        catch (Exception e)
        {
            Log.Write($"server: {request.Url?.AbsolutePath}: {e.GetType().Name}: {e.Message}");
            try
            {
                response.StatusCode = 400;
                response.Close();
            }
            catch (InvalidOperationException)
            {
                // Already upgraded to a WebSocket or closed.
            }
        }
    }
}
