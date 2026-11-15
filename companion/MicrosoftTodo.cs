using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlexScreen.Companion;

/// <summary>
/// Microsoft To Do as a task source, through Microsoft Graph: every list's open tasks,
/// ticking off and adding (not into Flagged email, which only Outlook fills). Signs in with
/// the user's own app registration (settings.json's "microsoftTodo": "clientId", and
/// "tenant", "common" unless set: "consumers" for an app that takes personal accounts
/// only), a one-time browser consent from the tray's Task sources › Microsoft To Do. The
/// refresh token is kept encrypted (<see cref="Secrets"/>).
/// </summary>
sealed class MicrosoftTodo : ITaskProvider
{
    public const string TokenName = "microsoft-token";
    const string Scopes = "openid offline_access Tasks.ReadWrite";
    const string Api = "https://graph.microsoft.com/v1.0/me/todo";
    const string PersonalTenant = "9188040d-6c67-4c5b-b112-36a304b66dad";  // every personal Microsoft account's
    const int MaxPages = 5;  // 100 tasks a page, per list

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly Regex Tags = new("<[^>]+>");

    readonly SemaphoreSlim _tokenGate = new(1, 1);
    string? _access;
    DateTime _expires;
    bool _personal = true;  // to-do.live.com links; a work account's open to-do.office.com
    List<(TaskList List, string Known)> _lists = [];

    public string Id => "mstodo";
    public string Name => "Microsoft To Do";

    public event Action<Toast>? Toast;

    /// <summary>Raised after a sign-in, so the tasks are read again.</summary>
    public event Action? SignedIn;

    static (string ClientId, string Tenant)? Client() =>
        Settings.Source("microsoftTodo") is { } s && (string?)s["clientId"] is { Length: > 0 } id
            ? (id.Trim(), (string?)s["tenant"] is { Length: > 0 } t ? t.Trim() : "common")
            : null;

    public bool HasClient => Client() != null;

    /// <summary>The browser consent, from the tray menu.</summary>
    public async Task SignInAsync()
    {
        if (Client() is not { } client) return;
        var pending = OAuthLoopback.Prepare();
        var url = $"https://login.microsoftonline.com/{Uri.EscapeDataString(client.Tenant)}/oauth2/v2.0/authorize?response_type=code" +
                  $"&response_mode=query&prompt=select_account&client_id={Uri.EscapeDataString(client.ClientId)}" +
                  $"&redirect_uri={Uri.EscapeDataString(pending.Redirect)}&scope={Uri.EscapeDataString(Scopes)}" +
                  $"&code_challenge={pending.Challenge}&code_challenge_method=S256&state={pending.State}";
        if (await OAuthLoopback.CodeAsync(pending, url, Name) is not { } code) return;
        var tokens = await PostTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = pending.Redirect,
            ["code_verifier"] = pending.Verifier, ["scope"] = Scopes,
        });
        if (tokens == null)
        {
            Toast?.Invoke(new Toast("error", "Microsoft To Do didn't connect. The companion's log says why"));
            return;
        }
        Log.Write("tasks: mstodo: connected");
        Toast?.Invoke(new Toast("ok", "Connected to Microsoft To Do"));
        SignedIn?.Invoke();
    }

    public async Task<TaskRead> ReadAsync()
    {
        if (!HasClient) return new TaskRead("setup", [], []);
        if (!await EnsureTokenAsync()) return new TaskRead("signin", [], []);
        var json = await SendAsync(HttpMethod.Get, $"{Api}/lists");
        var lists = (json?["value"] as JsonArray ?? [])
            .Select(l => (Id: (string?)l?["id"] ?? "", Name: (string?)l?["displayName"] ?? "", Known: (string?)l?["wellknownListName"] ?? ""))
            .Where(l => l.Id.Length > 0)
            .Select(l => (new TaskList(Id, l.Id, l.Name, Add: l.Known != "flaggedEmails"), l.Known))
            .ToList();
        var reads = await Task.WhenAll(lists.Select(l => ListTasksAsync(l.Item1.Id)));
        _lists = lists;
        return new TaskRead("ok", lists.Select(l => l.Item1).ToList(), reads.SelectMany(t => t).ToList());
    }

    async Task<List<TaskItem>> ListTasksAsync(string list)
    {
        var tasks = new List<TaskItem>();
        string? url = $"{Api}/lists/{Uri.EscapeDataString(list)}/tasks?$top=100&$filter={Uri.EscapeDataString("status ne 'completed'")}";
        for (var i = 0; i < MaxPages && url != null; i++)
        {
            var json = await SendAsync(HttpMethod.Get, url);
            foreach (var t in json?["value"] as JsonArray ?? [])
            {
                var title = ((string?)t?["title"] ?? "").Trim();
                var id = (string?)t?["id"] ?? "";
                if (title.Length == 0 || id.Length == 0) continue;
                // In this PC's time zone (the Prefer header), so a date doesn't slip to the day before.
                var due = (string?)t?["dueDateTime"]?["dateTime"] is { Length: >= 10 } d ? d[..10] : "";
                var body = (string?)t?["body"]?["content"] ?? "";
                if ((string?)t?["body"]?["contentType"] == "html") body = WebUtility.HtmlDecode(Tags.Replace(body.Replace("<br>", "\n"), ""));
                var status = (string?)t?["status"] switch
                {
                    "inProgress" => "In progress", "waitingOnOthers" => "Waiting on others", "deferred" => "Deferred", _ => "",
                };
                var priority = (string?)t?["importance"] == "high" ? "High importance" : "";
                tasks.Add(new TaskItem(Id, list, id, title, body.Trim(), due, false)
                {
                    Url = $"https://to-do.{(_personal ? "live" : "office")}.com/tasks/id/{Uri.EscapeDataString(id)}/details",
                    Priority = priority,
                    Status = status,
                    Summary = priority.Length > 0 || status.Length > 0
                        ? string.Join("  ·  ", new[] { priority.Length > 0 ? "High" : "", status, FirstLine(body) }.Where(p => p.Length > 0))
                        : "",
                });
            }
            url = (string?)json?["@odata.nextLink"];
        }
        return tasks;
    }

    static string FirstLine(string text) => text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    public async Task SetDoneAsync(string list, string id, bool done) =>
        await SendAsync(HttpMethod.Patch, $"{Api}/lists/{Uri.EscapeDataString(list)}/tasks/{Uri.EscapeDataString(id)}",
                        new JsonObject { ["status"] = done ? "completed" : "notStarted" });

    public async Task<string> AddAsync(string list, string title, string due)
    {
        if (list.Length == 0) list = _lists.FirstOrDefault(l => l.Known == "defaultList").List?.Id ?? _lists.FirstOrDefault().List?.Id ?? "";
        if (list.Length == 0) throw new HttpRequestException("no Microsoft To Do list yet");
        var body = new JsonObject { ["title"] = title };
        if (due.Length == 10) body["dueDateTime"] = new JsonObject { ["dateTime"] = due + "T00:00:00", ["timeZone"] = TimeZoneInfo.Local.Id };
        await SendAsync(HttpMethod.Post, $"{Api}/lists/{Uri.EscapeDataString(list)}/tasks", body);
        return _lists.FirstOrDefault(l => l.List.Id == list).List?.Name ?? "";
    }

    async Task<JsonNode?> SendAsync(HttpMethod method, string url, JsonNode? body = null)
    {
        if (!await EnsureTokenAsync()) throw new HttpRequestException("not signed in to Microsoft");
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _access);
        request.Headers.Add("Prefer", $"outlook.timezone=\"{TimeZoneInfo.Local.Id}\"");
        if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var reply = await Http.SendAsync(request);
        var text = await reply.Content.ReadAsStringAsync();
        if (!reply.IsSuccessStatusCode)
        {
            string? reason = null;
            try
            {
                reason = (string?)JsonNode.Parse(text)?["error"]?["message"];
            }
            catch (System.Text.Json.JsonException)
            {
            }
            if (reply.StatusCode == HttpStatusCode.Unauthorized) _access = null;
            throw new HttpRequestException($"Microsoft Graph: {(int)reply.StatusCode} {reason ?? reply.ReasonPhrase}", null, reply.StatusCode);
        }
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    async Task<bool> EnsureTokenAsync()
    {
        await _tokenGate.WaitAsync();  // lists are read side by side; one refresh for all of them
        try
        {
            if (_access != null && DateTime.UtcNow < _expires) return true;
            if (Secrets.Load(TokenName) is not { Length: > 0 } refresh || Client() is not { } client) return false;
            var tokens = await PostTokenAsync(client, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["scope"] = Scopes,
            });
            return tokens != null;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    /// <summary>A token request; keeps the new access token and the refresh token Microsoft sends with it (it rotates).</summary>
    async Task<JsonNode?> PostTokenAsync((string ClientId, string Tenant) client, Dictionary<string, string> form)
    {
        form["client_id"] = client.ClientId;
        using var reply = await Http.PostAsync($"https://login.microsoftonline.com/{Uri.EscapeDataString(client.Tenant)}/oauth2/v2.0/token",
                                               new FormUrlEncodedContent(form));
        var json = JsonNode.Parse(await reply.Content.ReadAsStringAsync());
        if (!reply.IsSuccessStatusCode || (string?)json?["access_token"] is not { Length: > 0 } access)
        {
            var error = (string?)json?["error"];
            Log.Write($"tasks: mstodo: token request failed: {error}: {((string?)json?["error_description"])?.Split('\n')[0]}");
            if (error == "invalid_grant") Secrets.Delete(TokenName);  // revoked or expired: sign in again
            return null;
        }
        _access = access;
        _expires = DateTime.UtcNow.AddSeconds(((int?)json["expires_in"] ?? 3600) - 60);
        if ((string?)json["refresh_token"] is { Length: > 0 } refresh) Secrets.Save(TokenName, refresh);
        if (TenantOf((string?)json["id_token"]) is { } tenant) _personal = tenant == PersonalTenant;
        return json;
    }

    /// <summary>The "tid" claim of an ID token (its middle part is base64url JSON), or null.</summary>
    static string? TenantOf(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: >= 2 }) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            return (string?)JsonNode.Parse(Convert.FromBase64String(payload))?["tid"];
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
