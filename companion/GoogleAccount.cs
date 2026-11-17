using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// One Google sign-in: an entry of the profile's "accounts" of type "google" (see
/// <see cref="Settings.Accounts"/>). Each has its own refresh token on this PC (Secrets'
/// "&lt;id&gt;-token": the first account, "google", keeps the google-token.bin from before
/// accounts) and reads with an OAuth client: google-client.json, shared by every account,
/// or the account's own "client" file (a work domain that only allows its own client, say).
/// <code>
/// "google-work": { "type": "google", "label": "Work", "client": "google-client-work.json", "calendar": true, "tasks": false }
/// </code>
/// "calendar" and "tasks" (both on unless false) say whether its events go on the deck's
/// calendar and its task lists on the Tasks card.
/// </summary>
sealed class GoogleAccount
{
    public const string Scopes = "https://www.googleapis.com/auth/calendar.readonly https://www.googleapis.com/auth/tasks";
    public static readonly string DefaultClientPath = Path.Combine(AppPaths.Data, "google-client.json");
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    readonly SemaphoreSlim _tokenGate = new(1, 1);  // calendars and lists are read side by side; one token refresh for all
    string? _accessToken;
    DateTime _accessExpires;

    public GoogleAccount(string id, JsonObject config)
    {
        Id = id;
        Configure(config);
    }

    /// <summary>The account's id in the profile ("google", "google-work"); also the task source's id on the deck.</summary>
    public string Id { get; }

    /// <summary>The profile's name for it ("Work"), or "".</summary>
    public string Label { get; private set; } = "";

    /// <summary>The OAuth client file it signs in with.</summary>
    public string ClientPath { get; private set; } = DefaultClientPath;

    /// <summary>Its events on the deck's calendar.</summary>
    public bool Calendar { get; private set; } = true;

    /// <summary>Its task lists on the deck's Tasks card.</summary>
    public bool Tasks { get; private set; } = true;

    /// <summary>The Google account signed in (its primary calendar's id), once the calendars have been read; "" before.</summary>
    public string Email { get; set; } = "";

    /// <summary>For people: "Google", or "Google (Work)".</summary>
    public string Name => Label.Length > 0 ? $"Google ({Label})" : "Google";

    public bool HasClient => File.Exists(ClientPath);

    /// <summary>Whether there's a sign-in on this PC (it may have been revoked since).</summary>
    public bool HasSignIn => Secrets.Load(TokenName) != null;

    string TokenName => $"{Id}-token";  // "google" → google-token.bin, the one from before accounts

    /// <summary>The profile's settings for it, again (they can change while it's in use).</summary>
    public void Configure(JsonObject config)
    {
        Label = ((string?)config["label"] ?? "").Trim();
        var client = (string?)config["client"] is { Length: > 0 } c
            ? Path.GetFullPath(Path.Combine(AppPaths.Data, Environment.ExpandEnvironmentVariables(c)))
            : DefaultClientPath;
        if (client != ClientPath) _accessToken = null;  // the old client's
        ClientPath = client;
        Calendar = Flag(config["calendar"]);
        Tasks = Flag(config["tasks"]);
    }

    static bool Flag(JsonNode? node) => node is not JsonValue v || !v.TryGetValue(out bool on) || on;

    /// <summary>
    /// The browser consent, with Google's account picker first (each account here is a
    /// different Google account). Listens on localhost for Google's redirect.
    /// </summary>
    /// <returns>The scopes not granted ("Calendar", "Tasks"; none when all were), or null when it didn't sign in.</returns>
    public async Task<List<string>?> SignInAsync()
    {
        if (ReadClient() is not { } client) return null;
        var (clientId, clientSecret) = client;
        var pending = OAuthLoopback.Prepare();
        var hint = Email.Length > 0 ? $"&login_hint={Uri.EscapeDataString(Email)}" : "";
        var url = "https://accounts.google.com/o/oauth2/v2/auth?response_type=code&access_type=offline" +
                  $"&prompt={Uri.EscapeDataString("select_account consent")}{hint}" +
                  $"&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(pending.Redirect)}" +
                  $"&scope={Uri.EscapeDataString(Scopes)}&code_challenge={pending.Challenge}&code_challenge_method=S256&state={pending.State}";
        if (await OAuthLoopback.CodeAsync(pending, url, Name) is not { } code) return null;
        var tokens = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = pending.Redirect,
            ["client_id"] = clientId, ["client_secret"] = clientSecret, ["code_verifier"] = pending.Verifier,
        });
        if ((string?)tokens?["refresh_token"] is not { Length: > 0 } refresh)
        {
            Log.Write($"google: {Id}: no refresh token in the reply");
            return null;
        }
        Secrets.Save(TokenName, refresh);
        _accessToken = (string?)tokens["access_token"];
        _accessExpires = DateTime.UtcNow.AddSeconds(((int?)tokens["expires_in"] ?? 3600) - 60);
        Email = "";  // maybe another Google account than before: read again
        // Google's consent page has a box per scope, and unticked ones aren't granted.
        var granted = ((string?)tokens["scope"] ?? Scopes).Split(' ');
        return Scopes.Split(' ').Where(s => !granted.Contains(s)).Select(s => s.EndsWith("tasks") ? "Tasks" : "Calendar").ToList();
    }

    /// <summary>Forgets the sign-in on this PC.</summary>
    public void SignOut()
    {
        Secrets.Delete(TokenName);
        _accessToken = null;
        Email = "";
    }

    /// <summary>"ok" when signed in, else "setup" (no OAuth client file) or "signin".</summary>
    public async Task<string> AuthStatusAsync()
    {
        if (!HasClient) return "setup";
        return await EnsureTokenAsync() ? "ok" : "signin";
    }

    /// <summary>An authorized Google API request (signed in first, if need be); the reply's
    /// JSON, or null for an empty reply. Throws HttpRequestException, with Google's reason.</summary>
    public async Task<JsonNode?> SendAsync(HttpMethod method, string url, JsonNode? body = null)
    {
        if (!await EnsureTokenAsync()) throw new HttpRequestException($"not signed in to {Name}");
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var reply = await Http.SendAsync(request);
        var text = await reply.Content.ReadAsStringAsync();
        if (!reply.IsSuccessStatusCode)
        {
            // Google says why (the API isn't enabled in the Cloud project, a scope wasn't
            // granted, a quota…): put that in the log, not just the status code.
            var api = url.Contains("tasks.googleapis.com") ? "Tasks" : "Calendar";
            string? reason = null;
            try
            {
                var error = JsonNode.Parse(text)?["error"];
                reason = (string?)error?["message"] ?? (string?)error?["status"];
            }
            catch (System.Text.Json.JsonException)
            {
            }
            if (reply.StatusCode == HttpStatusCode.Unauthorized) _accessToken = null;
            throw new HttpRequestException($"{api} API: {(int)reply.StatusCode} {reason ?? reply.ReasonPhrase}", null, reply.StatusCode);
        }
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    async Task<bool> EnsureTokenAsync()
    {
        await _tokenGate.WaitAsync();
        try
        {
            if (_accessToken != null && DateTime.UtcNow < _accessExpires) return true;
            if (Secrets.Load(TokenName) is not { } refresh || ReadClient() is not { } client) return false;
            var (clientId, clientSecret) = client;
            var tokens = await PostTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = clientId, ["client_secret"] = clientSecret,
            });
            if ((string?)tokens?["access_token"] is not { Length: > 0 } access) return false;
            _accessToken = access;
            _accessExpires = DateTime.UtcNow.AddSeconds(((int?)tokens["expires_in"] ?? 3600) - 60);
            return true;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    async Task<JsonNode?> PostTokenAsync(Dictionary<string, string> form)
    {
        using var reply = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form));
        var json = JsonNode.Parse(await reply.Content.ReadAsStringAsync());
        if (!reply.IsSuccessStatusCode)
        {
            Log.Write($"google: {Id}: token request failed: {(string?)json?["error"]}");
            if ((string?)json?["error"] == "invalid_grant") Secrets.Delete(TokenName);  // revoked: sign in again
            return null;
        }
        return json;
    }

    /// <summary>The OAuth client (a "Desktop app" client from Google Cloud Console, as downloaded).</summary>
    (string Id, string Secret)? ReadClient()
    {
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(ClientPath));
            var c = json?["installed"] ?? json?["web"];
            return (string?)c?["client_id"] is { } id && (string?)c?["client_secret"] is { } secret ? (id, secret) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
