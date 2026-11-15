using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// What Claude asks or tells you on the deck. A question has 2–4 options and waits for a tap:
/// from the deck_ask tool (<see cref="DeckTools"/>), or Claude Code's own AskUserQuestion,
/// answered through the PermissionRequest hook while its prompt shows at the PC too. A note
/// (deck_notify) waits for nothing and stays until you tap OK, or send that session your
/// next message. The deck shows the newest question, else the newest note.
/// </summary>
sealed class DeckQuestions
{
    public record Option(string Label, string Note);

    /// <param name="Kind">"ask" (wait for a tap) or "note".</param>
    /// <param name="Level">A note's: "info", "done", "warning" or "error".</param>
    /// <param name="Step">This question's place among the ones asked together (1 of <paramref name="Steps"/>).</param>
    /// <param name="FromHook">AskUserQuestion's, answered at the PC too: settled when the session moves on.</param>
    public sealed record Item(string Id, string Session, string Project, string Kind, string Level, string Text,
                              IReadOnlyList<Option> Options, bool Multi, int Step, int Steps, bool FromHook, DateTime At,
                              TaskCompletionSource<int[]?> Answer);

    public enum Outcome { Picked, Dismissed, TimedOut, Gone }

    const int MaxNotes = 3;
    static readonly TimeSpan NoteLife = TimeSpan.FromMinutes(30);

    readonly object _gate = new();
    readonly List<Item> _items = [];
    int _next;

    /// <summary>Raised when a question or note comes or goes.</summary>
    public event Action? Changed;

    /// <summary>What the deck shows: the newest question, else the newest note; null for nothing.</summary>
    public (Item Item, int Waiting)? Shown
    {
        get
        {
            lock (_gate)
            {
                _items.RemoveAll(i => i.Kind == "note" && DateTime.UtcNow - i.At > NoteLife);
                var asks = _items.Where(i => i.Kind == "ask").ToList();
                var shown = asks.LastOrDefault() ?? _items.LastOrDefault();
                return shown == null ? null : (shown, _items.Count);
            }
        }
    }

    public bool Asking(string session)
    {
        lock (_gate) return _items.Any(i => i.Kind == "ask" && i.Session == session);
    }

    /// <summary>
    /// Shows a question and waits for the tap. <paramref name="cancel"/> ends it early (Claude
    /// gave up waiting, or the question was answered at the PC): <see cref="Outcome.Gone"/>.
    /// </summary>
    public async Task<(Outcome Outcome, int[] Picks)> Ask(string session, string project, string text, IReadOnlyList<Option> options,
                                                         bool multi, int step, int steps, bool fromHook, TimeSpan wait,
                                                         CancellationToken cancel)
    {
        var item = new Item($"q{Interlocked.Increment(ref _next)}", session, project, "ask", "", text, options, multi, step, steps,
                            fromHook, DateTime.UtcNow, new TaskCompletionSource<int[]?>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_gate) _items.Add(item);
        Log.Write($"claude: asked on the deck ({item.Id}): {project}: {text}");
        Changed?.Invoke();
        var timeout = Task.Delay(wait, CancellationToken.None);
        var gone = Task.Delay(Timeout.Infinite, cancel);
        var first = await Task.WhenAny(item.Answer.Task, timeout, gone);
        lock (_gate) _items.Remove(item);
        Changed?.Invoke();
        if (first == item.Answer.Task)
        {
            var picks = item.Answer.Task.Result;
            return picks is { Length: > 0 } ? (Outcome.Picked, picks) : (Outcome.Dismissed, []);
        }
        return (first == timeout ? Outcome.TimedOut : Outcome.Gone, []);
    }

    public void Note(string session, string project, string level, string text)
    {
        var item = new Item($"n{Interlocked.Increment(ref _next)}", session, project, "note", level, text, [], false, 1, 1, false,
                            DateTime.UtcNow, new TaskCompletionSource<int[]?>());
        lock (_gate)
        {
            _items.Add(item);
            var notes = _items.Where(i => i.Kind == "note").ToList();
            foreach (var old in notes.Take(Math.Max(0, notes.Count - MaxNotes))) _items.Remove(old);
        }
        Log.Write($"claude: note on the deck: {project}: {text}");
        Changed?.Invoke();
    }

    /// <summary>The deck's tap: the options picked, or none for the question (or note) dismissed.</summary>
    public void Answer(string id, int[] picks)
    {
        Item? item;
        lock (_gate)
        {
            item = _items.FirstOrDefault(i => i.Id == id);
            if (item?.Kind == "note") _items.Remove(item);
        }
        if (item == null) return;
        if (item.Kind == "note")
        {
            Changed?.Invoke();
            return;
        }
        var valid = picks.Where(p => p >= 0 && p < item.Options.Count).Distinct().Order().ToArray();
        if (!item.Multi && valid.Length > 1) valid = valid[..1];
        Log.Write(valid.Length > 0
            ? $"claude: answered on the deck: {item.Project}: {string.Join(", ", valid.Select(p => item.Options[p].Label))}"
            : $"claude: dismissed on the deck: {item.Project}: {item.Text}");
        item.Answer.TrySetResult(valid);
    }

    /// <summary>
    /// The session went on: its AskUserQuestion was answered at the PC (a tool ran, the turn
    /// ended), and its notes are read once you write to it again.
    /// </summary>
    public void Settled(string session, bool notesToo)
    {
        List<Item> done;
        bool removed;
        lock (_gate)
        {
            done = _items.Where(i => i.Session == session && i.FromHook).ToList();
            removed = notesToo && _items.RemoveAll(i => i.Session == session && i.Kind == "note") > 0;
        }
        foreach (var item in done) item.Answer.TrySetResult(null);
        if (removed) Changed?.Invoke();
    }

    /// <summary>
    /// Claude Code's AskUserQuestion, from its PermissionRequest hook: each of its questions in
    /// turn on the deck, then the hook's answer, which lets the tool run with the answers filled
    /// in. Null (Claude's own prompt decides) for a question the deck can't answer (no options,
    /// or more than 4), one dismissed on the deck, or the session moving on.
    /// </summary>
    public async Task<string?> AnswerAskUserQuestion(JsonNode hook, string project, TimeSpan wait)
    {
        if (hook["tool_input"] is not JsonObject input || input["questions"] is not JsonArray { Count: > 0 } questions) return null;
        var asked = new List<(string Text, List<Option> Options, bool Multi)>();
        foreach (var q in questions)
        {
            var text = (string?)q?["question"] ?? "";
            var options = (q?["options"] as JsonArray ?? [])
                .Select(o => new Option((string?)o?["label"] ?? "", (string?)o?["description"] ?? ""))
                .ToList();
            var kind = (string?)q?["kind"] ?? "choice";
            if (text.Length == 0 || kind != "choice" || options.Count is < 2 or > 4 || options.Any(o => o.Label.Length == 0)) return null;
            asked.Add((text, options, (bool?)q?["multiSelect"] ?? false));
        }
        var session = (string?)hook["session_id"] ?? "";
        var until = DateTime.UtcNow + wait;
        var answers = new JsonObject();
        for (var i = 0; i < asked.Count; i++)
        {
            var (text, options, multi) = asked[i];
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return null;
            var (outcome, picks) = await Ask(session, project, text, options, multi, i + 1, asked.Count, fromHook: true, left,
                                             CancellationToken.None);
            if (outcome != Outcome.Picked) return null;
            answers[text] = string.Join(", ", picks.Select(p => options[p].Label));
        }
        var updated = (JsonObject)input.DeepClone();
        updated["answers"] = answers;
        var output = new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PermissionRequest",
                ["decision"] = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = updated },
            },
        };
        return output.ToJsonString();
    }
}
