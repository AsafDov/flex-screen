using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// What the flex-screen browser extension (chrome-ext/, for Chrome and Edge) says about the tab
/// in front: its address and title, sent over a WebSocket on /browser whenever another tab or
/// window comes to the front or the tab goes somewhere else. The context bar then knows the
/// site by its address, not only by a title that changes with every page. Kept in memory only:
/// the last addresses, by tab title (a window's title is its tab's), never logged and never
/// sent to the deck (only the site's id is). An incognito tab says nothing.
/// </summary>
static class BrowserTabs
{
    const int Kept = 30;

    record Tab(string Browser, string Title, string Url, DateTime At);

    static readonly object Gate = new();
    static readonly List<Tab> Tabs = [];  // newest last
    static int _connected;

    /// <summary>A tab came to the front or changed address: the bar may have other keys now.</summary>
    public static event Action? Changed;

    /// <summary>Whether an extension is connected now.</summary>
    public static bool Connected => Volatile.Read(ref _connected) > 0;

    /// <summary>
    /// The address of the tab a browser window shows, if the extension said: the newest report
    /// from that browser ("chrome", "msedge") whose title the window's title starts with;
    /// "" when there's none (no extension, or a tab it hasn't seen).
    /// </summary>
    public static string UrlFor(string exe, string windowTitle)
    {
        if (windowTitle.Length == 0) return "";
        lock (Gate)
        {
            for (var i = Tabs.Count - 1; i >= 0; i--)
            {
                var t = Tabs[i];
                if (t.Browser.Length > 0 && t.Browser != exe) continue;
                if (t.Title.Length > 0 && windowTitle.StartsWith(t.Title, StringComparison.Ordinal)) return t.Url;
            }
        }
        return "";
    }

    /// <summary>One report: {"t": "tab", "browser": "chrome" or "msedge", "url", "title"}.</summary>
    public static void Report(JsonNode message)
    {
        if ((string?)message["t"] != "tab") return;
        var url = (string?)message["url"] ?? "";
        var title = ((string?)message["title"] ?? "").Trim();
        if (title.Length == 0 || WebSites.Address(url).Length == 0) return;  // only web pages
        var browser = (string?)message["browser"] is "chrome" or "msedge" ? (string)message["browser"]! : "";
        lock (Gate)
        {
            Tabs.RemoveAll(t => t.Browser == browser && t.Title == title);
            Tabs.Add(new Tab(browser, title, url, DateTime.UtcNow));
            if (Tabs.Count > Kept) Tabs.RemoveAt(0);
        }
        Changed?.Invoke();
    }

    /// <summary>One extension's connection, until it closes. It only sends; nothing goes back but the close.</summary>
    public static async Task Serve(WebSocket socket)
    {
        Interlocked.Increment(ref _connected);
        Log.Write("browser: the extension connected");
        var buffer = new byte[16 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 64 * 1024) return;  // a tab report is small; something else is talking
                } while (!result.EndOfMessage);
                try
                {
                    if (JsonNode.Parse(Encoding.UTF8.GetString(message.ToArray())) is { } json) Report(json);
                }
                catch (System.Text.Json.JsonException)
                {
                }
            }
        }
        catch (WebSocketException)
        {
            // The browser closed, or the extension reloaded.
        }
        finally
        {
            Interlocked.Decrement(ref _connected);
            Log.Write("browser: the extension disconnected");
            try
            {
                // Its close answered (CloseReceived), or ours sent: the other side isn't left waiting.
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException)
            {
            }
            socket.Dispose();
        }
    }
}
