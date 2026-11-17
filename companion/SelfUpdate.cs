using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// Keeps this PC's companion up to date with flex-screen's newest GitHub release: looks now
/// and then, and on the user's OK downloads the release's install kit (flex-screen.zip),
/// checks it, and runs the kit's own install.ps1, as install.cmd would. That puts the new
/// companion, extension and hooks in place and starts the new companion, which then offers
/// the deck its firmware (<see cref="DeckFirmware"/>).
/// </summary>
sealed class SelfUpdate
{
    const string Releases = "https://api.github.com/repos/AsafDov/flex-screen/releases/latest";
    const string KitName = "flex-screen.zip";
    static readonly TimeSpan FirstLook = TimeSpan.FromMinutes(1);
    static readonly TimeSpan LookEvery = TimeSpan.FromHours(6);
    static readonly TimeSpan MarkerFresh = TimeSpan.FromHours(1);

    /// <summary>For testing: FLEX_SCREEN_UPDATE_FEED names another URL that answers like GitHub's latest-release API.</summary>
    static readonly string Feed = Environment.GetEnvironmentVariable("FLEX_SCREEN_UPDATE_FEED") is { Length: > 0 } feed ? feed : Releases;

    sealed record Release(string Version, string Tag, string Name, string Page, string ZipUrl, long Size, string? Sha256);

    readonly Action<string, string, ToolTipIcon, Action?> _notify;
    readonly Action _quit;
    readonly Func<bool> _deckBusy;
    readonly SynchronizationContext _ui;
    readonly System.Threading.Timer _timer;
    Release? _newer;
    string _offered = "";
    volatile bool _busy;
    DateTime _lookedAt = DateTime.MinValue;

    public SelfUpdate(Action<string, string, ToolTipIcon, Action?> notify, Action quit, Func<bool> deckBusy)
    {
        _notify = notify;
        _quit = quit;
        _deckBusy = deckBusy;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        MenuItem = new ToolStripMenuItem("Check for updates");
        MenuItem.Click += (_, _) => OnMenu();
        Log.Write($"update: this is flex-screen {Current}{(IsDevBuild ? " (a development build: no update checks)" : "")}");
        ReportLastUpdate();
        _timer = new System.Threading.Timer(_ => _ = LookAsync(asked: false), null, IsDevBuild ? Timeout.InfiniteTimeSpan : FirstLook, LookEvery);
    }

    /// <summary>This companion's version ("0.21.0"), from build-dist.ps1's -Version; "0.0.0-dev" for a build from the repo.</summary>
    public static string Current { get; } = DeckFirmware.Plain(
        typeof(SelfUpdate).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev");

    static readonly HttpClient Http = MakeHttp();  // after Current, which its User-Agent names

    static bool IsDevBuild => Current.Contains('-') || DeckFirmware.Compare(Current, "0.0.1") is null or < 0;

    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "flex-screen", "update");
    static string MarkerPath => Path.Combine(Folder, "update.json");
    static string InstallLog => Path.Combine(Folder, "install.log");

    /// <summary>The tray menu's line while it downloads ("Downloading the update: 42%"), else null.</summary>
    public string? Status { get; private set; }

    public ToolStripMenuItem MenuItem { get; }

    /// <summary>Call as the tray menu opens.</summary>
    public void RefreshMenu()
    {
        MenuItem.Enabled = !_busy && !IsDevBuild;
        MenuItem.Text = _busy ? Status ?? "Updating…"
                      : IsDevBuild ? "Updates: off in a development build"
                      : _newer is { } r ? $"Update flex-screen to {r.Version}…"
                      : "Check for updates";
    }

    /// <summary>Looks again soon, unless it has lately (the deck runs newer firmware than this companion knows).</summary>
    public void LookSoon()
    {
        if (!IsDevBuild && DateTime.UtcNow - _lookedAt > TimeSpan.FromMinutes(10)) _ = LookAsync(asked: false);
    }

    /// <summary>The tray's "Check for updates" (also the "update.check" action): looks now, and offers what it finds.</summary>
    public void LookNow() => _ = LookAsync(asked: true);

    void OnMenu()
    {
        if (_newer != null) Confirm();
        else LookNow();
    }

    async Task LookAsync(bool asked)
    {
        if (_busy) return;
        _lookedAt = DateTime.UtcNow;
        Release? latest;
        try
        {
            latest = await LatestAsync();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            Log.Write($"update: couldn't look for updates: {e.Message}");
            if (asked) _notify("flex-screen updates", $"Couldn't reach GitHub to look for updates: {e.Message}", ToolTipIcon.Warning, null);
            return;
        }
        if (latest == null || DeckFirmware.Compare(latest.Version, Current) <= 0)
        {
            _newer = null;
            Log.Write($"update: {Current} is the newest{(latest == null ? " (no release with an install kit)" : "")}");
            if (asked) _notify("flex-screen updates", $"flex-screen {Current} is the newest.", ToolTipIcon.Info, null);
            return;
        }
        _newer = latest;
        if (_offered == latest.Version && !asked) return;  // said already; the menu item stays
        Log.Write($"update: {latest.Tag} is out ({latest.Name}), this PC has {Current}");
        _offered = latest.Version;
        if (asked) _ui.Post(_ => Confirm(), null);
        else _notify("flex-screen update", $"flex-screen {latest.Version} is out; this PC has {Current}. Click to update.", ToolTipIcon.Info, Confirm);
    }

    /// <summary>The newest release, if it has the install kit attached and a version that reads as x.y.z.</summary>
    static async Task<Release?> LatestAsync()
    {
        using var response = await Http.GetAsync(Feed);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;  // no releases yet
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("GitHub's limit for checks from this network is used up for the hour");
        response.EnsureSuccessStatusCode();
        var release = JsonNode.Parse(await response.Content.ReadAsStringAsync()) ?? throw new InvalidDataException("an empty answer");
        var tag = (string?)release["tag_name"] ?? "";
        var version = DeckFirmware.Plain(tag);
        if (DeckFirmware.Compare(version, version) == null || (bool?)release["draft"] == true || (bool?)release["prerelease"] == true) return null;
        // The kit is attached by hand after the release goes up, so it may not be there yet.
        var kit = (release["assets"] as JsonArray ?? []).FirstOrDefault(a => (string?)a?["name"] == KitName && (string?)a?["state"] == "uploaded");
        if (kit == null) return null;
        var digest = (string?)kit["digest"];
        return new Release(version, tag, (string?)release["name"] ?? tag, (string?)release["html_url"] ?? "",
                           (string?)kit["browser_download_url"] ?? throw new InvalidDataException("the kit has no download link"),
                           (long?)kit["size"] ?? 0, digest?.StartsWith("sha256:", StringComparison.Ordinal) == true ? digest[7..] : null);
    }

    void Confirm()
    {
        if (_newer is not { } release || _busy) return;
        if (_deckBusy())
        {
            MessageBox.Show("The deck's firmware is being updated. Try again once it's done.", "flex-screen update",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var answer = MessageBox.Show(
            $"Update flex-screen on this PC from {Current} to {release.Version}?\n\n{release.Name}\n\n" +
            $"This downloads the release's install kit from GitHub ({KitName}, {release.Size / 1_048_576.0:0} MB), checks it, " +
            "and installs it as install.cmd does: the companion, the VS Code extension and the Claude Code hooks. " +
            "The companion closes for that and starts again in about a minute. If the deck's firmware is older than " +
            "the new kit's, it then offers to update the deck too.",
            "flex-screen update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes) _ = Task.Run(() => InstallAsync(release));  // off the tray's thread
    }

    async Task InstallAsync(Release release)
    {
        _busy = true;
        try
        {
            Directory.CreateDirectory(Folder);
            var zip = await DownloadAsync(release);
            var kit = Unpack(release, zip);
            Launch(release, kit);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or
                                     System.ComponentModel.Win32Exception)
        {
            Log.Write($"update: {release.Tag} not installed: {e.Message}");
            _notify("flex-screen update", $"The update to {release.Version} didn't go ahead: {e.Message}", ToolTipIcon.Warning, null);
            _busy = false;
            SetStatus(null);
        }
    }

    /// <summary>The kit, checked against the size and SHA-256 GitHub gives for it.</summary>
    async Task<string> DownloadAsync(Release release)
    {
        var path = Path.Combine(Folder, $"flex-screen-{release.Version}.zip");
        var partial = path + ".part";
        SetStatus("Downloading the update…");
        Log.Write($"update: downloading {release.ZipUrl}");
        using (var response = await Http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.Size;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var file = File.Create(partial))
            {
                var buffer = new byte[81920];
                long done = 0;
                var shown = -1;
                int n;
                while ((n = await source.ReadAsync(buffer)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n));
                    sha.AppendData(buffer, 0, n);
                    done += n;
                    var percent = total > 0 ? (int)(done * 100 / total) : 0;
                    if (percent != shown) SetStatus($"Downloading the update: {shown = percent}%");
                }
                if (done != release.Size) throw new InvalidDataException($"the download was {done} bytes, the release says {release.Size}");
            }
            var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            if (release.Sha256 == null) Log.Write("update: GitHub gave no SHA-256 for the kit; checked its size only");
            else if (!string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("the download doesn't match the release's SHA-256");
        }
        File.Move(partial, path, overwrite: true);
        Log.Write($"update: {Path.GetFileName(path)} checks out");
        return path;
    }

    /// <summary>Unzips the kit and checks it's one: install.ps1 and a companion that says it's this release.</summary>
    string Unpack(Release release, string zip)
    {
        SetStatus("Unpacking the update…");
        var into = Path.Combine(Folder, release.Version);
        if (Directory.Exists(into)) Directory.Delete(into, recursive: true);
        ZipFile.ExtractToDirectory(zip, into);
        var kit = Directory.Exists(Path.Combine(into, "flex-screen")) ? Path.Combine(into, "flex-screen") : into;
        var exe = Path.Combine(kit, "companion", "flex-screen-companion.exe");
        if (!File.Exists(Path.Combine(kit, "install.ps1")) || !File.Exists(exe)) throw new InvalidDataException("the zip isn't an install kit");
        var version = DeckFirmware.Plain(FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "");
        if (DeckFirmware.Compare(version, release.Version) != 0)
            throw new InvalidDataException($"the kit's companion is {(version.Length > 0 ? version : "unversioned")}, not {release.Version}");
        return kit;
    }

    /// <summary>
    /// Starts the installer, which waits for this companion to quit, and quits. The marker
    /// file tells the companion that starts next how it went (<see cref="ReportLastUpdate"/>).
    /// </summary>
    void Launch(Release release, string kit)
    {
        var script = Path.Combine(Folder, "update.ps1");
        File.WriteAllText(script, UpdateScript);
        File.WriteAllText(MarkerPath, new JsonObject { ["from"] = Current, ["to"] = release.Version, ["at"] = DateTime.UtcNow }.ToJsonString());
        var self = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "flex-screen-companion.exe");
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Folder,
        };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                                    "-Kit", kit, "-Wait", Environment.ProcessId.ToString(), "-Log", InstallLog, "-Fallback", self })
        {
            start.ArgumentList.Add(arg);
        }
        Process.Start(start);
        Log.Write($"update: installing {release.Tag} from {kit}; quitting so it can replace this companion");
        _ui.Post(_ => _quit(), null);
    }

    /// <summary>After an update: says how it went, once, and clears up the download.</summary>
    void ReportLastUpdate()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return;
            var marker = JsonNode.Parse(File.ReadAllText(MarkerPath));
            File.Delete(MarkerPath);
            var to = (string?)marker?["to"] ?? "";
            if (DateTime.UtcNow - ((DateTime?)marker?["at"] ?? DateTime.MinValue) > MarkerFresh) return;
            if (DeckFirmware.Compare(to, Current) == 0)
            {
                Log.Write($"update: updated from {(string?)marker?["from"]} to {Current}");
                _notify("flex-screen updated", $"This PC runs flex-screen {Current} now. Open VS Code windows pick up the new extension after a reload.",
                        ToolTipIcon.Info, null);
                foreach (var old in Directory.EnumerateFileSystemEntries(Folder).Where(p => p != InstallLog && !p.EndsWith(".ps1")))
                {
                    try
                    {
                        if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
                        else File.Delete(old);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        Log.Write($"update: couldn't clear {old}: {e.Message}");
                    }
                }
            }
            else
            {
                Log.Write($"update: the update to {to} didn't finish; still {Current} (see {InstallLog})");
                _notify("flex-screen update", $"The update to {to} didn't finish, so this PC still has {Current}. Click for the installer's log.",
                        ToolTipIcon.Warning, () => Process.Start(new ProcessStartInfo(InstallLog) { UseShellExecute = true }));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
        {
            Log.Write($"update: couldn't read {MarkerPath}: {e.Message}");
        }
    }

    void SetStatus(string? status)
    {
        Status = status;
        _ui.Post(_ => RefreshMenu(), null);
    }

    static HttpClient MakeHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("flex-screen-companion", Current));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>
    /// Runs the new kit's install.ps1 once this companion has quit; if that fails, starts a
    /// companion again (the installed one, else this one) so the deck isn't left without one.
    /// </summary>
    const string UpdateScript = """
        # Written by the flex-screen companion for an update (companion/SelfUpdate.cs). Waits for
        # the companion that started it to quit, then runs the new kit's install.ps1, as
        # install.cmd would. If that fails, starts a companion again.
        param([string]$Kit, [int]$Wait, [string]$Log, [string]$Fallback)
        Start-Transcript -Path $Log -Force | Out-Null
        $ok = $false
        try {
          $old = Get-Process -Id $Wait -ErrorAction SilentlyContinue
          if ($old) { [void]$old.WaitForExit(15000) }
          & (Join-Path $Kit 'install.ps1')
          $ok = $true
        } catch {
          Write-Host "The update failed: $_"
        } finally {
          if (-not $ok -and -not (Get-Process flex-screen-companion -ErrorAction SilentlyContinue)) {
            $exe = Join-Path $env:LOCALAPPDATA 'Programs\flex-screen\flex-screen-companion.exe'
            if (-not (Test-Path $exe)) { $exe = $Fallback }
            if (Test-Path $exe) { Start-Process $exe -WorkingDirectory (Split-Path $exe) }
          }
          Stop-Transcript | Out-Null
        }
        """;
}
