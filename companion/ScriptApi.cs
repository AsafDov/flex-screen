using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// The deck for scripts: the same note, question and progress Claude gets from
/// <see cref="DeckTools"/>, for anything on this PC (or a Remote-SSH server, through the
/// hooks' tunnel) that can POST JSON to http://localhost:48151/api/…. The `flexctl` command
/// (install/flexctl.ps1, and the server's ~/.local/bin/flexctl) is the usual way in.
/// <list type="bullet">
/// <item>/api/notify {text, level, from}: a note that stays until OK is tapped (or 30 minutes).</item>
/// <item>/api/ask {question, options, wait_minutes, from}: 2-4 buttons; answered when one is tapped,
///   with {outcome: picked, index, answer}, or dismissed, timeout, gone.</item>
/// <item>/api/progress {label, done, total, from} or {clear: true}: "Uploading 3/12" in the
///   status line, until done reaches total, it's cleared, or no word comes for 30 minutes.</item>
/// </list>
/// Requests with an Origin header are refused (<see cref="LocalServer"/>), so no web page can use it.
/// </summary>
sealed class ScriptApi
{
    public sealed record Job(string Label, string From, int Done, int Total);

    const int DefaultWaitMinutes = 15, MaxWaitMinutes = 24 * 60;
    const string DefaultFrom = "Script";
    static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(10);
    static readonly TimeSpan JobLife = TimeSpan.FromMinutes(30);

    readonly DeckQuestions _questions;
    readonly Func<bool> _deckConnected;
    readonly object _gate = new();
    readonly System.Threading.Timer _expiry;
    Job? _job;

    public ScriptApi(DeckQuestions questions, Func<bool> deckConnected)
    {
        _questions = questions;
        _deckConnected = deckConnected;
        _expiry = new System.Threading.Timer(_ => SetJob(null, "no word for 30 minutes"));
    }

    /// <summary>The newest script's progress, or null for none.</summary>
    public Job? Current
    {
        get
        {
            lock (_gate) return _job;
        }
    }

    /// <summary>Raised when <see cref="Current"/> changes.</summary>
    public event Action<Job?>? JobChanged;

    public async Task Serve(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        if (request.HttpMethod != "POST")
        {
            response.StatusCode = 405;
            response.AddHeader("Allow", "POST");
            response.Close();
            return;
        }
        JsonObject args;
        using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
        {
            var body = await reader.ReadToEndAsync();
            args = (body.Trim().Length == 0 ? new JsonObject() : JsonNode.Parse(body)) as JsonObject ?? new JsonObject();
        }
        var from = Clip((string?)args["from"], 24);
        if (from.Length == 0) from = DefaultFrom;
        switch (request.Url?.AbsolutePath)
        {
            case "/api/notify":
                await Notify(response, args, from);
                return;
            case "/api/ask":
                await Ask(response, args, from);
                return;
            case "/api/progress":
                await Progress(response, args, from);
                return;
            default:
                await Reply(response, 404, new JsonObject { ["ok"] = false, ["error"] = "Unknown command; there are notify, ask and progress" });
                return;
        }
    }

    async Task Notify(HttpListenerResponse response, JsonObject args, string from)
    {
        var text = Clip((string?)args["text"], 120);
        if (text.Length == 0)
        {
            await Reply(response, 400, Failed("notify needs a text"));
            return;
        }
        if (!_deckConnected())
        {
            await Reply(response, 503, Failed("the deck isn't connected"));
            return;
        }
        var level = (string?)args["level"] is "done" or "warning" or "error" ? (string)args["level"]! : "info";
        _questions.Note("", "", level, text, from);
        await Reply(response, 200, new JsonObject { ["ok"] = true });
    }

    async Task Ask(HttpListenerResponse response, JsonObject args, string from)
    {
        var text = Clip((string?)args["question"], 200);
        var options = (args["options"] as JsonArray ?? [])
            .Select(o => o is JsonValue v && v.TryGetValue(out string? s) ? Clip(s, 40) : "")
            .Where(s => s.Length > 0)
            .ToList();
        if (text.Length == 0 || options.Count is < 2 or > 4)
        {
            await Reply(response, 400, Failed("ask needs a question and 2 to 4 options"));
            return;
        }
        if (!_deckConnected())
        {
            await Reply(response, 503, Failed("the deck isn't connected"));
            return;
        }
        var minutes = args["wait_minutes"] is JsonValue m && m.TryGetValue(out double w) && double.IsFinite(w)
            ? Math.Clamp(w, 1, MaxWaitMinutes)
            : DefaultWaitMinutes;

        // The answer can take many minutes. The reply starts now and a space goes every few
        // seconds (JSON allows them before the value), so the client's timeouts don't run
        // out, and a write that fails means the script stopped waiting: the question goes.
        response.StatusCode = 200;
        response.ContentType = "application/json; charset=utf-8";
        response.AddHeader("Cache-Control", "no-cache");
        response.SendChunked = true;
        if (!await Write(response, " ")) return;
        using var gone = new CancellationTokenSource();
        var asking = _questions.Ask("", "", text, options.Select(o => new DeckQuestions.Option(o, "")).ToList(), false, 1, 1,
                                    fromHook: false, TimeSpan.FromMinutes(minutes), gone.Token, from);
        while (await Task.WhenAny(asking, Task.Delay(KeepAlive)) != asking)
        {
            if (!await Write(response, " ")) gone.Cancel();
        }
        var (outcome, picks) = await asking;
        if (outcome == DeckQuestions.Outcome.Gone)
        {
            try { response.Abort(); } catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { }
            return;
        }
        var result = outcome switch
        {
            DeckQuestions.Outcome.Picked => new JsonObject { ["outcome"] = "picked", ["index"] = picks[0], ["answer"] = options[picks[0]] },
            DeckQuestions.Outcome.Dismissed => new JsonObject { ["outcome"] = "dismissed" },
            _ => new JsonObject { ["outcome"] = "timeout" },
        };
        await Write(response, result.ToJsonString() + "\n");
        try { response.Close(); } catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { }
    }

    async Task Progress(HttpListenerResponse response, JsonObject args, string from)
    {
        static double Number(JsonNode? n) => n is JsonValue v && v.TryGetValue(out double d) && double.IsFinite(d) ? d : -1;
        var done = Number(args["done"]);
        var total = Number(args["total"]);
        if ((bool?)args["clear"] == true || (total > 0 && done >= total))
        {
            SetJob(null, (bool?)args["clear"] == true ? "cleared" : "done");
            await Reply(response, 200, new JsonObject { ["ok"] = true });
            return;
        }
        if (total <= 0 || done < 0)
        {
            await Reply(response, 400, Failed("progress needs done and total (or clear)"));
            return;
        }
        SetJob(new Job(Clip((string?)args["label"], 40), from, (int)Math.Floor(done), (int)Math.Ceiling(total)), null);
        await Reply(response, 200, new JsonObject { ["ok"] = true, ["shown"] = _deckConnected() });
    }

    void SetJob(Job? job, string? why)
    {
        lock (_gate)
        {
            _expiry.Change(job == null ? Timeout.InfiniteTimeSpan : JobLife, Timeout.InfiniteTimeSpan);
            if (job == _job) return;  // the same again: only its 30 minutes start over
            var started = job != null && (_job == null || _job.Label != job.Label || _job.From != job.From);
            if (job == null && _job != null) Log.Write($"script: {_job.From}'s progress off the deck ({why})");
            else if (started) Log.Write($"script: {job!.From}'s progress on the deck: {job.Label}");
            _job = job;
        }
        JobChanged?.Invoke(job);
    }

    static string Clip(string? text, int max)
    {
        var line = (text ?? "").ReplaceLineEndings(" ").Trim();
        return line.Length > max ? line[..(max - 1)] + "…" : line;
    }

    static JsonObject Failed(string error) => new() { ["ok"] = false, ["error"] = error };

    static async Task Reply(HttpListenerResponse response, int status, JsonObject body)
    {
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString() + "\n");
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
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
