using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlexScreen.Companion;

/// <summary>
/// Jira issues as a task source, to see and open, not to change: what a JQL query finds (by
/// default the open issues assigned to me), with each one's project, key, priority and
/// status. The deck opens an issue in the browser; ticking off and adding stay in Jira.
/// Jira Cloud signs in with an email and an API token, Jira Server and Data Center with a
/// personal access token (no email). The site, email and query are the profile's "jira";
/// the token is kept encrypted (<see cref="Secrets"/>). The tray's Task sources › Jira sets them.
/// </summary>
sealed class JiraIssues : ITaskProvider
{
    public const string TokenName = "jira-token";
    public const string DefaultJql = "assignee = currentUser() AND statusCategory != Done ORDER BY priority DESC, updated DESC";
    const string Fields = "summary,status,priority,project,duedate,description";
    const int MaxIssues = 50;
    const string List = "assigned";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <param name="Cloud">Signs in with an email and an API token (Jira Cloud), else with a personal access token.</param>
    record Site(string Url, string Email, bool Cloud);

    public string Id => "jira";
    public string Name => "Jira";

    public async Task<TaskRead> ReadAsync()
    {
        var settings = Settings.Source("jira");
        if (SiteOf(settings) is not { } site) return new TaskRead("setup", [], []);
        if (Secrets.LoadFor(TokenName, TokenSite(site.Url)) is not { Length: > 0 } token) return new TaskRead("signin", [], []);
        var jql = (string?)settings?["jql"] is { Length: > 0 } q ? q : DefaultJql;
        // Jira Cloud retired /search in 2025 for /search/jql; Server and Data Center have only /search.
        var path = site.Cloud ? "/rest/api/2/search/jql" : "/rest/api/2/search";
        JsonNode? json;
        try
        {
            json = await GetAsync(site, token, $"{site.Url}{path}?jql={Uri.EscapeDataString(jql)}&fields={Fields}&maxResults={MaxIssues}");
        }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            Log.Write($"tasks: jira: {(int)e.StatusCode!}, sign in again");
            return new TaskRead("signin", [], []);
        }
        var name = (string?)settings?["name"] is { Length: > 0 } n ? n : "Jira";
        var tasks = (json?["issues"] as JsonArray ?? []).Select(i => Issue(site, i)).OfType<TaskItem>().ToList();
        return new TaskRead("ok", [new TaskList(Id, List, name, Add: false, Tick: false)], tasks);
    }

    TaskItem? Issue(Site site, JsonNode? issue)
    {
        var key = (string?)issue?["key"] ?? "";
        var f = issue?["fields"];
        var title = ((string?)f?["summary"] ?? "").Trim();
        if (key.Length == 0 || title.Length == 0) return null;
        var priority = (string?)f?["priority"]?["name"] ?? "";
        if (priority == "None") priority = "";
        var status = (string?)f?["status"]?["name"] ?? "";
        var due = (string?)f?["duedate"] is { Length: >= 10 } d ? d[..10] : "";
        return new TaskItem(Id, List, key, title, Description(f?["description"]), due, false)
        {
            Url = $"{site.Url}/browse/{Uri.EscapeDataString(key)}",
            Key = key,
            Project = (string?)f?["project"]?["name"] ?? "",
            Priority = priority,
            Status = status,
            Summary = string.Join("  ·  ", new[] { key, priority, status }.Where(p => p.Length > 0)),
        };
    }

    /// <summary>The description as plain text: API v2 gives wiki markup, v3 a document tree.</summary>
    static string Description(JsonNode? description)
    {
        if (description is JsonValue value && value.TryGetValue(out string? wiki)) return PlainWiki(wiki);
        if (description is not JsonObject doc) return "";
        var text = new StringBuilder();
        void Walk(JsonNode? node)
        {
            if ((string?)node?["text"] is { } t) text.Append(t);
            foreach (var child in node?["content"] as JsonArray ?? []) Walk(child);
            if ((string?)node?["type"] is "paragraph" or "heading" or "listItem" or "codeBlock" or "hardBreak") text.Append('\n');
        }
        Walk(doc);
        return text.ToString().Trim();
    }

    static readonly Regex WikiNoise = new(@"\{(code|noformat|panel|quote|color)[^}]*\}|^h[1-6]\.\s*|!\S+?!", RegexOptions.Multiline);
    static readonly Regex WikiLink = new(@"\[([^|\]]+)\|[^\]]+\]");

    static string PlainWiki(string wiki) => WikiLink.Replace(WikiNoise.Replace(wiki, ""), "$1").Replace("\r", "").Trim();

    static Site? SiteOf(JsonObject? settings)
    {
        if ((string?)settings?["url"] is not { Length: > 0 } url) return null;
        url = url.Trim().TrimEnd('/');
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        var email = ((string?)settings?["email"] ?? "").Trim();
        return new Site(url, email, email.Length > 0);
    }

    /// <summary>What a site's token is kept under (<see cref="Secrets.LoadFor"/>): "acme.atlassian.net", or with Jira's path on a server; "" without a site.</summary>
    public static string TokenSite(string url) =>
        SiteOf(new JsonObject { ["url"] = url }) is { } site && Uri.TryCreate(site.Url, UriKind.Absolute, out var uri)
            ? uri.Authority + uri.AbsolutePath.TrimEnd('/')
            : "";

    static async Task<JsonNode?> GetAsync(Site site, string token, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = site.Cloud
            ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{site.Email}:{token}")))
            : new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var reply = await Http.SendAsync(request);
        var text = await reply.Content.ReadAsStringAsync();
        if (!reply.IsSuccessStatusCode)
        {
            // Jira says why (a bad JQL query, say): into the log, without the token.
            string? reason = null;
            try
            {
                reason = string.Join("; ", (JsonNode.Parse(text)?["errorMessages"] as JsonArray ?? []).Select(m => (string?)m));
            }
            catch (System.Text.Json.JsonException)
            {
            }
            throw new HttpRequestException($"Jira: {(int)reply.StatusCode} {(reason is { Length: > 0 } ? reason : reply.ReasonPhrase)}", null, reply.StatusCode);
        }
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    /// <summary>Checks a site and token before they're kept (the tray's Connect): the user's display name.</summary>
    public static async Task<string> WhoAmIAsync(string url, string email, string token)
    {
        var site = SiteOf(new JsonObject { ["url"] = url, ["email"] = email })
                   ?? throw new HttpRequestException("no Jira site");
        var me = await GetAsync(site, token, $"{site.Url}/rest/api/2/myself");
        return (string?)me?["displayName"] ?? (string?)me?["name"] ?? "";
    }

    public Task SetDoneAsync(string list, string id, bool done) => throw new InvalidOperationException("Jira issues are moved on in Jira");

    public Task<string> AddAsync(string list, string title, string due) => throw new InvalidOperationException("Jira issues are created in Jira");
}
