using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlexScreen.Companion;

/// <summary>
/// GitHub issues as a task source, to see and open, not to change: what a search finds (by
/// default the open issues assigned to me, in every repository), with the repository, the
/// number, a priority and a status label if it has them, and its milestone's due date. The
/// deck opens one in the browser. github.com or GitHub Enterprise (settings.json's "github":
/// "host", "query"); signs in with a token kept encrypted (<see cref="Secrets"/>), or without
/// one, with the GitHub CLI's own sign-in (gh auth token). The tray's Task sources › GitHub sets them.
/// </summary>
sealed class GitHubIssues : ITaskProvider
{
    public const string TokenName = "github-token";
    public const string DefaultQuery = "is:open is:issue assignee:@me archived:false";
    const int MaxIssues = 50;
    const string List = "assigned";
    static readonly TimeSpan CliTokenFor = TimeSpan.FromMinutes(30);

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly Regex PriorityLabel = new(@"^(p[0-4]|priority\W*\w+|sev(erity)?\W*\w+|critical|urgent|high|medium|low)$", RegexOptions.IgnoreCase);
    static readonly Regex StatusLabel = new(@"in.?progress|in.?review|review|blocked|wip|doing|to.?do|ready|triage|backlog", RegexOptions.IgnoreCase);

    string? _cliToken;
    DateTime _cliTokenAt;

    public string Id => "github";
    public string Name => "GitHub";

    public async Task<TaskRead> ReadAsync()
    {
        if (Settings.Source("github") is not { } settings) return new TaskRead("setup", [], []);
        var host = HostOf(settings);
        if (await TokenAsync(host) is not { Length: > 0 } token) return new TaskRead("signin", [], []);
        var query = (string?)settings["query"] is { Length: > 0 } q ? q : DefaultQuery;
        JsonNode? json;
        try
        {
            json = await GetAsync(host, token, $"/search/issues?q={Uri.EscapeDataString(query)}&sort=updated&order=desc&per_page={MaxIssues}");
        }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized)
        {
            Log.Write("tasks: github: 401, sign in again");
            _cliToken = null;
            return new TaskRead("signin", [], []);
        }
        var name = (string?)settings["name"] is { Length: > 0 } n ? n : "GitHub";
        var tasks = (json?["items"] as JsonArray ?? []).Select(Issue).OfType<TaskItem>().ToList();
        return new TaskRead("ok", [new TaskList(Id, List, name, Add: false, Tick: false)], tasks);
    }

    TaskItem? Issue(JsonNode? item)
    {
        var number = (int?)item?["number"] ?? 0;
        var title = ((string?)item?["title"] ?? "").Trim();
        var url = (string?)item?["html_url"] ?? "";
        if (number == 0 || title.Length == 0) return null;
        // ".../repos/owner/name"
        var repo = string.Join("/", ((string?)item?["repository_url"] ?? "").Split('/').TakeLast(2));
        var labels = (item?["labels"] as JsonArray ?? []).Select(l => (string?)l?["name"] ?? "").Where(l => l.Length > 0).ToList();
        var priority = labels.FirstOrDefault(l => PriorityLabel.IsMatch(l)) ?? "";
        var status = labels.FirstOrDefault(l => StatusLabel.IsMatch(l)) ?? "";
        var pull = item?["pull_request"] != null;
        var due = (string?)item?["milestone"]?["due_on"] is { Length: >= 10 } d ? d[..10] : "";
        var shortRepo = repo.Split('/').Last();
        return new TaskItem(Id, List, $"{repo}#{number}", title, (string?)item?["body"] ?? "", due, false)
        {
            Url = url,
            Key = $"#{number}",
            Project = repo,
            Priority = priority,
            Status = status.Length > 0 ? status : pull ? "Pull request" : "Open",
            Summary = string.Join("  ·  ", new[] { $"{shortRepo}#{number}", priority, status }.Where(p => p.Length > 0)),
        };
    }

    static string HostOf(JsonObject? settings) =>
        ((string?)settings?["host"] is { Length: > 0 } h ? h : "github.com").Trim().Replace("https://", "").TrimEnd('/');

    static string ApiOf(string host) => host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ? "https://api.github.com" : $"https://{host}/api/v3";

    /// <summary>The kept token, else the GitHub CLI's (read again every half hour).</summary>
    async Task<string?> TokenAsync(string host)
    {
        if (Secrets.Load(TokenName) is { Length: > 0 } kept) return kept;
        if (_cliToken != null && DateTime.UtcNow - _cliTokenAt < CliTokenFor) return _cliToken;
        _cliToken = await CliTokenAsync(host);
        _cliTokenAt = DateTime.UtcNow;
        return _cliToken;
    }

    /// <summary>gh's token for the host, or null without gh or its sign-in.</summary>
    public static async Task<string?> CliTokenAsync(string host)
    {
        try
        {
            using var gh = Process.Start(new ProcessStartInfo("gh", ["auth", "token", "--hostname", host])
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            });
            if (gh == null) return null;
            var output = gh.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await gh.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                gh.Kill(entireProcessTree: true);  // hung (waiting on a prompt, say)
                return null;
            }
            var token = (await output).Trim();
            return gh.ExitCode == 0 && token.Length > 0 ? token : null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return null;  // no gh on this PC
        }
    }

    static async Task<JsonNode?> GetAsync(string host, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiOf(host) + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("flex-screen", "1"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var reply = await Http.SendAsync(request);
        var text = await reply.Content.ReadAsStringAsync();
        if (!reply.IsSuccessStatusCode)
        {
            string? reason = null;
            try
            {
                reason = (string?)JsonNode.Parse(text)?["message"];
            }
            catch (System.Text.Json.JsonException)
            {
            }
            throw new HttpRequestException($"GitHub: {(int)reply.StatusCode} {reason ?? reply.ReasonPhrase}", null, reply.StatusCode);
        }
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    /// <summary>Checks a host and token (blank: gh's) before they're kept: the user's login.</summary>
    public static async Task<string> WhoAmIAsync(string host, string token)
    {
        host = HostOf(new JsonObject { ["host"] = host });
        if (token.Length == 0) token = await CliTokenAsync(host) ?? throw new HttpRequestException("no token, and the GitHub CLI isn't signed in");
        var me = await GetAsync(host, token, "/user");
        return (string?)me?["login"] ?? "";
    }

    public Task SetDoneAsync(string list, string id, bool done) => throw new InvalidOperationException("GitHub issues are closed on GitHub");

    public Task<string> AddAsync(string list, string title, string due) => throw new InvalidOperationException("GitHub issues are opened on GitHub");
}
