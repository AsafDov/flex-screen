using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// The Claude plan's usage (the 5-hour window's percentage and when it resets, and the
/// week's), for the context bar's Usage key. Read from the endpoint Claude Code's own /usage
/// reads (undocumented: GET api.anthropic.com/api/oauth/usage), with the sign-in token Claude
/// Code keeps in %USERPROFILE%\.claude\.credentials.json, on the user's say-so (2026-11-09).
/// The token is read when needed and only sent there: never logged, never stored, never
/// refreshed by the companion. When it has expired, Claude Code's own /usage runs headless
/// instead, which gives the figure and has Claude Code renew the token its usual way.
/// </summary>
sealed class ClaudeUsage
{
    /// <param name="FiveHour">Percent of the 5-hour window used.</param>
    public record Usage(double FiveHour, DateTime? FiveHourResets, double? SevenDay, DateTime At);

    const string Endpoint = "https://api.anthropic.com/api/oauth/usage";
    static readonly TimeSpan Every = TimeSpan.FromMinutes(4);
    static readonly TimeSpan AfterTurn = TimeSpan.FromSeconds(20);  // a turn just used some
    static readonly TimeSpan Refused = TimeSpan.FromMinutes(15);

    static readonly string CredentialsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    static readonly HttpClient Http = MakeHttp();

    readonly System.Threading.Timer _timer;
    readonly object _gate = new();
    DateTime _next = DateTime.MinValue;
    DateTime _lastAsked = DateTime.MinValue;
    string _logged = "\0";  // so the first reading is logged too
    int _busy;

    public ClaudeUsage()
    {
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public Usage? Current { get; private set; }

    /// <summary>Raised (on a timer thread) when a new reading arrives.</summary>
    public event Action? Changed;

    public void Start() => _timer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));

    /// <summary>A Claude turn ended: read again shortly (at most once a minute).</summary>
    public void Soon()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _lastAsked < TimeSpan.FromMinutes(1)) return;
            var soon = DateTime.UtcNow + AfterTurn;
            if (soon < _next) _next = soon;
        }
    }

    /// <summary>"5h 42%" for the Usage key, or "" before the first reading (or once the last is a 5-hour window old).</summary>
    public string Label => Current is { } u && DateTime.UtcNow - u.At < TimeSpan.FromHours(5) ? $"5h {Math.Round(u.FiveHour):0}%" : "";

    static readonly TimeSpan ServerFresh = TimeSpan.FromMinutes(15);
    DateTime _reportedAt = DateTime.MinValue;

    /// <summary>
    /// A reading from a server's hooks (install/remote-claude-hooks.sh runs Claude Code's own
    /// /usage there): Claude Code in a Remote-SSH window has no sign-in on this PC to read
    /// with. While those keep coming (they do while Claude works there), this PC's own
    /// reading waits, so two accounts' figures don't take turns on the key.
    /// </summary>
    public void Report(double fiveHour, double? sevenDay)
    {
        lock (_gate) _reportedAt = DateTime.UtcNow;
        var moved = Current == null || Math.Round(Current.FiveHour) != Math.Round(fiveHour);
        Current = new Usage(fiveHour, null, sevenDay, DateTime.UtcNow);
        if (!moved) return;
        Log.Write($"claude: usage {Label} (from a server's Claude Code)");
        Changed?.Invoke();
    }

    void Tick()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow < _next) return;
        }
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        _ = FetchAsync().ContinueWith(_ => Interlocked.Exchange(ref _busy, 0));
    }

    async Task FetchAsync()
    {
        var wait = Every;
        try
        {
            lock (_gate)
            {
                _lastAsked = DateTime.UtcNow;
                if (_lastAsked - _reportedAt < ServerFresh) return;  // a server's reading is the one in use
            }
            if (ReadToken() is not { } token)
            {
                // Expired (the Claude app signs in on its own and leaves the file alone): Claude
                // Code's own /usage, run headless, gives the figure and renews the token as it goes.
                if (DateTime.UtcNow - _lastHeadless >= HeadlessEvery && await HeadlessUsage() is { } read)
                {
                    var moved = Current == null || Math.Round(Current.FiveHour) != Math.Round(read.FiveHour);
                    Current = read;
                    Note("");
                    if (moved) Changed?.Invoke();
                    return;
                }
                Note("no Claude sign-in to read usage with: sign in to Claude Code (claude auth login)");
                wait = TimeSpan.FromMinutes(2);
                return;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using var response = await Http.SendAsync(request);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Note($"usage refused ({(int)response.StatusCode}); trying again later");
                wait = Refused;
                return;
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Note("usage: asked too often; trying again later");
                wait = response.Headers.RetryAfter?.Delta is { } after && after > Refused ? after : Refused;
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                Note($"usage: {(int)response.StatusCode}");
                return;
            }
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            if (json?["five_hour"]?["utilization"] is not { } five)
            {
                Note("usage: no 5-hour figure for this account");
                wait = Refused;
                return;
            }
            DateTime? resets = DateTime.TryParse((string?)json["five_hour"]?["resets_at"], null,
                                                 System.Globalization.DateTimeStyles.AdjustToUniversal, out var r) ? r : null;
            var usage = new Usage((double)five, resets, (double?)json["seven_day"]?["utilization"], DateTime.UtcNow);
            var changed = Current == null || Math.Round(Current.FiveHour) != Math.Round(usage.FiveHour);
            Current = usage;
            Note("");
            if (changed) Changed?.Invoke();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            Note($"usage: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            lock (_gate) _next = DateTime.UtcNow + wait;
        }
    }

    static readonly TimeSpan HeadlessEvery = TimeSpan.FromMinutes(20);
    DateTime _lastHeadless = DateTime.MinValue;

    /// <summary>
    /// Claude Code's /usage, run headless ("Current session: 67% used · resets …"): no model
    /// call, your hooks off for it (the deck would show a session), no transcript kept. It
    /// renews Claude Code's token if it has expired. Null without Claude Code or a sign-in.
    /// </summary>
    async Task<Usage?> HeadlessUsage()
    {
        _lastHeadless = DateTime.UtcNow;
        if (FindClaude() is not { } exe) return null;
        var start = new System.Diagnostics.ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var arg in new[] { "-p", "/usage", "--no-session-persistence", "--settings", "{\"disableAllHooks\":true}" }) start.ArgumentList.Add(arg);
        // As if run from a plain prompt: nothing inherited from a Claude session that started the companion.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase) ||
                                                               k.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase) ||
                                                               k.EndsWith("_OAUTH", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }
        try
        {
            using var process = System.Diagnostics.Process.Start(start);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            var text = await output;
            var session = System.Text.RegularExpressions.Regex.Match(text, @"Current session:\s*(\d+(?:\.\d+)?)%\s*used");
            if (!session.Success)
            {
                Log.Write("claude: headless /usage gave no figure (signed out of Claude Code?)");
                return null;
            }
            var week = System.Text.RegularExpressions.Regex.Match(text, @"Current week[^:]*:\s*(\d+(?:\.\d+)?)%\s*used");
            Log.Write("claude: usage from Claude Code's own /usage");
            return new Usage(double.Parse(session.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), null,
                             week.Success ? double.Parse(week.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null,
                             DateTime.UtcNow);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Write($"claude: headless /usage: {e.Message}");
            return null;
        }
    }

    /// <summary>Claude Code's executable: the native install's, else the newest one VS Code's Claude extension carries, else npm's.</summary>
    static string? FindClaude()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var native = Path.Combine(home, ".local", "bin", "claude.exe");
        if (File.Exists(native)) return native;
        var extensions = Path.Combine(home, ".vscode", "extensions");
        if (Directory.Exists(extensions))
        {
            var bundled = Directory.GetDirectories(extensions, "anthropic.claude-code-*")
                .Select(d => Path.Combine(d, "resources", "native-binary", "claude.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (bundled != null) return bundled;
        }
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd");
        return File.Exists(npm) ? npm : null;
    }

    /// <summary>Claude Code's current access token, or null (no sign-in file, or the token has expired).</summary>
    static string? ReadToken()
    {
        try
        {
            if (!File.Exists(CredentialsPath)) return null;
            string text;
            using (var file = new FileStream(CredentialsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(file))
            {
                text = reader.ReadToEnd();
            }
            var oauth = JsonNode.Parse(text)?["claudeAiOauth"];
            var token = (string?)oauth?["accessToken"];
            var expires = (long?)oauth?["expiresAt"] ?? 0;  // ms since 1970
            if (string.IsNullOrEmpty(token)) return null;
            if (expires > 0 && DateTimeOffset.FromUnixTimeMilliseconds(expires) < DateTimeOffset.UtcNow.AddSeconds(30)) return null;
            return token;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A line in the log when what's happening changes (never the token).</summary>
    void Note(string what)
    {
        if (what == _logged) return;
        _logged = what;
        Log.Write(what.Length > 0 ? $"claude: {what}" : $"claude: usage {Label}");
    }

    static HttpClient MakeHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("flex-screen");
        return http;
    }
}
