using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// The deck as a tool server for Claude Code (MCP over HTTP, http://localhost:48151/mcp):
/// deck_ask puts a question with buttons on the deck and returns the one tapped,
/// deck_notify leaves a note there, and deck_progress shows how far a long job has got.
/// <see cref="ClaudeHooks"/> registers it with Claude Code; a server reached over SSH gets it
/// through the same tunnel as its hooks.
/// </summary>
/// <remarks>
/// Stateless: no session ids, no stream of its own (GET is refused). A question can wait
/// many minutes, longer than an HTTP client waits for headers, so its answer comes as an
/// event stream that opens at once and sends a comment every few seconds until the tap; a
/// failed write means Claude stopped waiting, and the question leaves the deck.
/// </remarks>
sealed class DeckTools
{
    /// <summary>The name Claude Code knows the server by: its tools are mcp__flex-screen__deck_ask….</summary>
    public const string ServerName = "flex-screen";

    /// <summary>How long Claude Code may wait for a call (set when it's registered): a question's longest wait, and some.</summary>
    public const int CallTimeoutMs = 60 * 60 * 1000;

    const int DefaultWaitMinutes = 15, MaxWaitMinutes = 50;
    static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(10);
    static readonly TimeSpan CallerWait = TimeSpan.FromMilliseconds(1500);
    static readonly string[] Versions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    const string Instructions =
        "flex-screen is a touchscreen deck on the user's desk, beside the keyboard. Its tools reach the user when they may " +
        "be away from this chat: deck_ask for a decision you need during long unattended work (or whenever the user asks " +
        "you to ask on the deck), deck_notify to tell them something finished or needs them, deck_progress for long " +
        "multi-step jobs. Don't use them for the ordinary back-and-forth of the chat.";

    readonly DeckQuestions _questions;
    readonly ClaudeSource _claude;
    readonly Func<bool> _deckConnected;
    readonly object _gate = new();
    readonly List<(string Tool, JsonNode? Input, string Session, string Cwd, DateTime At)> _calls = [];

    public DeckTools(DeckQuestions questions, ClaudeSource claude, Func<bool> deckConnected)
    {
        _questions = questions;
        _claude = claude;
        _deckConnected = deckConnected;
    }

    /// <summary>
    /// The session calling, from its PreToolUse hook (which carries the session and folder a
    /// tool call doesn't): noted here, and matched by tool and arguments when the call comes.
    /// </summary>
    public void OnHook(JsonNode hook)
    {
        if ((string?)hook["hook_event_name"] != "PreToolUse" || (string?)hook["tool_name"] is not { } tool || ToolOf(tool) == null) return;
        lock (_gate)
        {
            _calls.RemoveAll(c => DateTime.UtcNow - c.At > TimeSpan.FromSeconds(30));
            _calls.Add((ToolOf(tool)!, hook["tool_input"]?.DeepClone(), (string?)hook["session_id"] ?? "", (string?)hook["cwd"] ?? "", DateTime.UtcNow));
        }
    }

    /// <summary>"deck_ask" for "mcp__flex-screen__deck_ask" (whatever the server was named), else null.</summary>
    public static string? ToolOf(string tool) =>
        tool.StartsWith("mcp__", StringComparison.Ordinal) && tool.LastIndexOf("__", StringComparison.Ordinal) is var at and > 4 &&
        tool[(at + 2)..] is "deck_ask" or "deck_notify" or "deck_progress" ? tool[(at + 2)..] : null;

    async Task<(string Session, string Project)> Caller(string tool, JsonNode? args)
    {
        var until = DateTime.UtcNow + CallerWait;
        while (true)
        {
            lock (_gate)
            {
                var i = _calls.FindIndex(c => c.Tool == tool && JsonNode.DeepEquals(c.Input ?? new JsonObject(), args ?? new JsonObject()));
                if (i >= 0)
                {
                    var call = _calls[i];
                    _calls.RemoveAt(i);
                    return (call.Session, _claude.ProjectFor(call.Session, call.Cwd));
                }
            }
            if (DateTime.UtcNow >= until) break;
            await Task.Delay(50);
        }
        // No hook (a server without them): the one session at work, if there's just one.
        var working = _claude.Current.Where(s => s.State is "working" or "input").ToList();
        return working.Count == 1 ? (working[0].Id, working[0].Project) : ("", "");
    }

    public async Task Serve(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        if (request.HttpMethod != "POST")
        {
            // No server-to-client stream, and no sessions to end.
            response.StatusCode = 405;
            response.AddHeader("Allow", "POST");
            response.Close();
            return;
        }
        JsonNode? message;
        using (var reader = new StreamReader(request.InputStream, Encoding.UTF8)) message = JsonNode.Parse(await reader.ReadToEndAsync());
        if (message is not JsonObject rpc)
        {
            await Reply(response, Error(null, -32600, "Expected one JSON-RPC message"));
            return;
        }
        var id = rpc["id"]?.DeepClone();
        var method = (string?)rpc["method"];
        if (id == null || method == null)
        {
            // A notification (initialized, cancelled) or a response: nothing to answer.
            response.StatusCode = 202;
            response.Close();
            return;
        }
        var parameters = rpc["params"] as JsonObject;
        switch (method)
        {
            case "initialize":
                var asked = (string?)parameters?["protocolVersion"];
                await Reply(response, Result(id, new JsonObject
                {
                    ["protocolVersion"] = Versions.Contains(asked) ? asked : Versions[1],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["title"] = "flex-screen deck", ["version"] = typeof(DeckTools).Assembly.GetName().Version?.ToString(3) ?? "1.0.0" },
                    ["instructions"] = Instructions,
                }));
                return;
            case "ping":
                await Reply(response, Result(id, new JsonObject()));
                return;
            case "tools/list":
                await Reply(response, Result(id, new JsonObject { ["tools"] = ToolList() }));
                return;
            case "tools/call":
                var name = (string?)parameters?["name"] ?? "";
                var args = parameters?["arguments"] as JsonObject ?? new JsonObject();
                if (name == "deck_ask")
                {
                    await Ask(request, response, id, args);
                    return;
                }
                var (session, project) = await Caller(name, args);
                await Reply(response, name switch
                {
                    "deck_notify" => Result(id, Notify(session, project, args)),
                    "deck_progress" => Result(id, Progress(session, args)),
                    _ => Error(id, -32602, $"Unknown tool: {name}"),
                });
                return;
            default:
                await Reply(response, Error(id, -32601, $"Method not found: {method}"));
                return;
        }
    }

    static JsonArray ToolList()
    {
        static JsonObject Read(string title) => new()
        {
            ["title"] = title, ["readOnlyHint"] = true, ["destructiveHint"] = false, ["idempotentHint"] = false, ["openWorldHint"] = false,
        };
        return
        [
            new JsonObject
            {
                ["name"] = "deck_ask",
                ["title"] = "Ask on the deck",
                ["description"] =
                    "Ask the user a question on their flex-screen deck (a touchscreen on their desk) and wait for the button they tap. " +
                    "Use it when the user asked you to ask on the deck, or during long unattended work when you need a decision and " +
                    "they may be away from the keyboard. The question shows only on the deck, not in the chat. Returns the option " +
                    "tapped, or says the question was dismissed or not answered in time; then carry on as you judge best or ask in the chat.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["question"] = new JsonObject { ["type"] = "string", ["description"] = "One or two short lines, e.g. \"Convert the other 12 files too?\"" },
                        ["options"] = new JsonObject
                        {
                            ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["minItems"] = 2, ["maxItems"] = 4,
                            ["description"] = "2 to 4 button labels of 1 to 3 words, e.g. [\"Yes\", \"No\"]",
                        },
                        ["wait_minutes"] = new JsonObject
                        {
                            ["type"] = "number", ["minimum"] = 1, ["maximum"] = MaxWaitMinutes,
                            ["description"] = $"How long to wait for a tap (default {DefaultWaitMinutes}, at most {MaxWaitMinutes})",
                        },
                    },
                    ["required"] = new JsonArray("question", "options"),
                },
                ["annotations"] = Read("Ask on the deck"),
            },
            new JsonObject
            {
                ["name"] = "deck_notify",
                ["title"] = "Note on the deck",
                ["description"] =
                    "Leave a short note on the user's flex-screen deck (a touchscreen on their desk): when long work finishes, or something " +
                    "needs their eye while they may be away. It stays until they tap OK or send you their next message. Doesn't wait.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["text"] = new JsonObject { ["type"] = "string", ["description"] = "One short line, under 90 characters" },
                        ["level"] = new JsonObject
                        {
                            ["type"] = "string", ["enum"] = new JsonArray("info", "done", "warning", "error"),
                            ["description"] = "How it looks (default info)",
                        },
                    },
                    ["required"] = new JsonArray("text"),
                },
                ["annotations"] = Read("Note on the deck"),
            },
            new JsonObject
            {
                ["name"] = "deck_progress",
                ["title"] = "Progress on the deck",
                ["description"] =
                    "Show how far long multi-step work has got on the user's flex-screen deck (a touchscreen on their desk): done of " +
                    "total, with a short label. Call it again as steps finish. It clears when done reaches total, when your turn " +
                    "ends, or with clear: true.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["label"] = new JsonObject { ["type"] = "string", ["description"] = "What's being done, a few words, e.g. \"Converting icons\"" },
                        ["done"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["description"] = "Steps finished" },
                        ["total"] = new JsonObject { ["type"] = "number", ["minimum"] = 1, ["description"] = "Steps in all" },
                        ["clear"] = new JsonObject { ["type"] = "boolean", ["description"] = "Take the progress off the deck" },
                    },
                },
                ["annotations"] = Read("Progress on the deck"),
            },
        ];
    }

    async Task Ask(HttpListenerRequest request, HttpListenerResponse response, JsonNode id, JsonObject args)
    {
        var text = Clip((string?)args["question"], 200);
        var options = (args["options"] as JsonArray ?? [])
            .Select(o => o is JsonValue v && v.TryGetValue(out string? s) ? Clip(s, 40) : "")
            .Where(s => s.Length > 0)
            .ToList();
        if (text.Length == 0 || options.Count is < 2 or > 4)
        {
            await Reply(response, Result(id, Text("deck_ask needs a question and 2 to 4 options.", error: true)));
            return;
        }
        if (!_deckConnected())
        {
            await Reply(response, Result(id, Text("The deck isn't connected, so the question can't be shown there. Ask in the chat instead.", error: true)));
            return;
        }
        var minutes = args["wait_minutes"] is JsonValue m && m.TryGetValue(out double w) ? Math.Clamp(w, 1, MaxWaitMinutes) : DefaultWaitMinutes;
        var (session, project) = await Caller("deck_ask", args);

        // The answer comes as an event stream opened now, so the client's wait for headers
        // ends at once; without one (a client that only takes JSON), it's held until the tap.
        var stream = request.Headers["Accept"]?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
        using var gone = new CancellationTokenSource();
        if (stream)
        {
            response.StatusCode = 200;
            response.ContentType = "text/event-stream";
            response.AddHeader("Cache-Control", "no-cache");
            response.SendChunked = true;
            if (!await Write(response, ": waiting for the deck\n\n")) return;
        }
        var asking = _questions.Ask(session, project, text, options.Select(o => new DeckQuestions.Option(o, "")).ToList(), false, 1, 1,
                                    fromHook: false, TimeSpan.FromMinutes(minutes), gone.Token);
        while (stream && await Task.WhenAny(asking, Task.Delay(KeepAlive)) != asking)
        {
            if (!await Write(response, ": still waiting\n\n")) gone.Cancel();  // Claude stopped waiting
        }
        var (outcome, picks) = await asking;
        if (outcome == DeckQuestions.Outcome.Gone && stream)
        {
            try { response.Abort(); } catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { }
            return;
        }
        var result = Result(id, outcome switch
        {
            DeckQuestions.Outcome.Picked => Text($"The user tapped \"{options[picks[0]]}\" on the deck."),
            DeckQuestions.Outcome.Dismissed => Text("The user dismissed the question on the deck without choosing an option."),
            DeckQuestions.Outcome.TimedOut => Text($"No answer on the deck after {minutes:0} minutes."),
            _ => Text("The question left the deck before it was answered.", error: true),
        });
        if (stream)
        {
            await Write(response, $"event: message\ndata: {result.ToJsonString()}\n\n");
            response.Close();
        }
        else await Reply(response, result);
    }

    JsonObject Notify(string session, string project, JsonObject args)
    {
        var text = Clip((string?)args["text"], 120);
        if (text.Length == 0) return Text("deck_notify needs a text.", error: true);
        if (!_deckConnected()) return Text("The deck isn't connected, so the note wasn't shown.", error: true);
        var level = (string?)args["level"] is "done" or "warning" or "error" ? (string)args["level"]! : "info";
        _questions.Note(session, project, level, text);
        return Text("The note is on the deck.");
    }

    JsonObject Progress(string session, JsonObject args)
    {
        if (session.Length == 0) return Text("The deck couldn't tell which Claude session this is, so the progress isn't shown.", error: true);
        static double Number(JsonNode? n) => n is JsonValue v && v.TryGetValue(out double d) && double.IsFinite(d) ? d : -1;
        var done = Number(args["done"]);
        var total = Number(args["total"]);
        if ((bool?)args["clear"] == true || (total > 0 && done >= total))
        {
            _claude.SetProgress(session, null);
            return Text("The progress is off the deck.");
        }
        var label = Clip((string?)args["label"], 60);
        if (total <= 0 || done < 0) return Text("deck_progress needs done and total (or clear: true).", error: true);
        _claude.SetProgress(session, new ClaudeProgress(label, (int)Math.Floor(done), (int)Math.Ceiling(total)));
        return Text("Shown on the deck.");
    }

    static string Clip(string? text, int max)
    {
        var line = (text ?? "").ReplaceLineEndings(" ").Trim();
        return line.Length > max ? line[..(max - 1)] + "…" : line;
    }

    static JsonObject Text(string text, bool error = false)
    {
        var result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
        if (error) result["isError"] = true;
        return result;
    }

    static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    static async Task Reply(HttpListenerResponse response, JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        response.StatusCode = 200;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    /// <summary>False when the client has gone.</summary>
    static async Task<bool> Write(HttpListenerResponse response, string text)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await response.OutputStream.WriteAsync(bytes);
            await response.OutputStream.FlushAsync();
            return true;
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }
}
