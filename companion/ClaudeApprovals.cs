using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// Claude Code's permission requests, answered from the deck. The PermissionRequest hook
/// (<see cref="ClaudeHooks"/>) posts each one and waits; Claude's own prompt shows at the same
/// time, and whichever answers first wins. The deck shows the newest request with Approve,
/// Always (Claude's own "don't ask again" suggestion, when it has one) and Deny.
/// </summary>
sealed class ClaudeApprovals
{
    /// <param name="Id">This request's id on the deck.</param>
    /// <param name="Session">The Claude session asking.</param>
    /// <param name="Tool">"Bash", "Edit"…; <paramref name="Input"/> its arguments.</param>
    /// <param name="Suggestions">Claude's suggested permission updates ("always allow this"), or null.</param>
    public record Request(string Id, string Session, string Project, string Tool, string Detail, JsonNode? Input,
                          JsonNode? Suggestions, DateTime At, TaskCompletionSource<string?> Answer);

    /// <summary>Longest a request waits for the deck; the hook itself gives up a little later.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(ClaudeHooks.PermissionSeconds - 15);

    readonly object _gate = new();
    readonly List<Request> _pending = [];
    int _next;

    /// <summary>Raised when a request comes in or goes (answered, or Claude moved on).</summary>
    public event Action? Changed;

    /// <summary>The requests still waiting, oldest first.</summary>
    public IReadOnlyList<Request> Pending
    {
        get
        {
            lock (_gate) return _pending.ToList();
        }
    }

    /// <summary>
    /// A PermissionRequest hook's call: waits for the deck's answer and returns the hook's
    /// JSON output, or null for none (Claude's own prompt decides). Without a deck to answer
    /// on it returns at once.
    /// </summary>
    public async Task<string?> Ask(JsonNode hook, string project, string detail, bool deckConnected)
    {
        if (!deckConnected || !Settings.ApproveFromDeck) return null;
        // A question is answered by picking an option: Approve or Deny can't answer it. The
        // deck shows its options instead (DeckQuestions).
        if ((string?)hook["tool_name"] == "AskUserQuestion") return null;
        var request = new Request(
            $"p{Interlocked.Increment(ref _next)}", (string?)hook["session_id"] ?? "", project, (string?)hook["tool_name"] ?? "tool",
            detail, hook["tool_input"]?.DeepClone(), hook["permission_suggestions"] is JsonArray { Count: > 0 } s ? s.DeepClone() : null,
            DateTime.UtcNow, new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_gate) _pending.Add(request);
        Log.Write($"claude: approval asked: {request.Project}: {request.Detail}");
        Changed?.Invoke();
        var answered = await Task.WhenAny(request.Answer.Task, Task.Delay(Wait)) == request.Answer.Task;
        lock (_gate) _pending.Remove(request);
        Changed?.Invoke();
        return answered ? request.Answer.Task.Result : null;
    }

    /// <summary>The deck's answer. <paramref name="always"/> applies Claude's own "don't ask again" suggestion too.</summary>
    public void Answer(string id, bool allow, bool always = false)
    {
        Request? request;
        lock (_gate) request = _pending.FirstOrDefault(r => r.Id == id);
        if (request == null) return;
        var decision = new JsonObject { ["behavior"] = allow ? "allow" : "deny" };
        if (allow && always && request.Suggestions != null) decision["updatedPermissions"] = request.Suggestions.DeepClone();
        if (!allow) decision["message"] = "Denied on the flex-screen deck.";
        var output = new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision },
        };
        Log.Write($"claude: {(allow ? always ? "always allowed" : "allowed" : "denied")} on the deck: {request.Project}: {request.Detail}");
        request.Answer.TrySetResult(output.ToJsonString());
    }

    /// <summary>
    /// The session went on without the deck (answered at the PC): a tool that ran or the
    /// turn ending settles its requests. With a tool given, only a request for that tool and
    /// input: Claude can ask about several tools at once.
    /// </summary>
    public void Settled(string session, string? tool = null, JsonNode? input = null)
    {
        List<Request> done;
        lock (_gate)
        {
            done = _pending.Where(r => r.Session == session && (tool == null || (r.Tool == tool && SameCall(r.Input, input)))).ToList();
        }
        foreach (var request in done) request.Answer.TrySetResult(null);
    }

    /// <summary>
    /// Whether the input a tool ran with is the one it asked about. What it ran with can carry
    /// more (the answers added to a question's input once you've answered at the PC), so every
    /// field asked about has to match, not the whole input.
    /// </summary>
    static bool SameCall(JsonNode? asked, JsonNode? ran) =>
        asked is JsonObject a && ran is JsonObject r
            ? a.All(field => r.TryGetPropertyValue(field.Key, out var value) && JsonNode.DeepEquals(field.Value, value))
            : JsonNode.DeepEquals(asked, ran);
}
