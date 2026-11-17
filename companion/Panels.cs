using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <param name="Open">An http(s) address a tap on the deck opens on the PC, or "".</param>
record PanelRow(string Id, string Text, string Sub, string Badge, string Open);

/// <param name="Status">"ok", "error" (<paramref name="Note"/> says why; the last rows stay), or "" before the first read.</param>
record PanelState(string Id, string Name, string Glyph, string Status, string Note, IReadOnlyList<PanelRow> Rows);

/// <summary>
/// The deck's Panels page: lists from anywhere, without a new companion or firmware. Each of
/// the "panels" is read every so often from a web address (JSON) or, on this PC only, from a
/// command (its output):
/// <code>
/// "panels": [
///   { "id": "reviews", "name": "To review", "glyph": "branch", "url": "http://localhost:8000/reviews.json", "every": 300 },
///   { "id": "parcels", "name": "Parcels", "glyph": "download", "command": "python C:\\scripts\\parcels.py", "every": 900 }
/// ]
/// </code>
/// A command runs code on the PC, so it's taken only from this PC's machine.json, never
/// from a profile (which can come from a synced folder); a profile's panel with a command is
/// left out, with a line in the log. machine.json's panels join the profile's (the same id
/// replaces it).
///
/// What they give: JSON, a list of rows or <c>{ "rows": [...], "note": "…" }</c>, each row a
/// line of text or <c>{ "text", "sub", "badge", "open": "https://…" }</c>; or plain text, a row
/// per line. Up to 4 panels and 12 rows each go to the deck.
/// </summary>
sealed class PanelSource
{
    public const int MaxPanels = 4, MaxRows = 12;
    static readonly TimeSpan Look = TimeSpan.FromSeconds(5);  // how often it looks for a panel that's due
    static readonly TimeSpan CommandFor = TimeSpan.FromSeconds(30);
    static readonly HttpClient Http = MakeHttp();

    /// <param name="Every">How often it's read again.</param>
    record Config(string Id, string Name, string Glyph, string Url, string Command, TimeSpan Every);

    readonly System.Threading.Timer _timer;
    readonly object _gate = new();
    readonly Dictionary<string, PanelState> _states = [];
    readonly Dictionary<string, DateTime> _readAt = [];
    readonly HashSet<string> _reading = [];
    string _configSeen = "\0";
    List<Config> _configs = [];

    public PanelSource()
    {
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised on a worker thread when a panel's rows or status changed.</summary>
    public event Action? Changed;

    public void Start() => _timer.Change(TimeSpan.Zero, Look);

    /// <summary>Whether the profile (or this PC) has any panels: the deck shows the Panels page only then.</summary>
    public bool Configured => Configs().Count > 0;

    /// <summary>The panels as last read, in the settings' order.</summary>
    public IReadOnlyList<PanelState> Current
    {
        get
        {
            var configs = Configs();
            lock (_gate)
            {
                return configs.Select(c => _states.TryGetValue(c.Id, out var s) ? s with { Name = c.Name, Glyph = c.Glyph }
                                                                                : new PanelState(c.Id, c.Name, c.Glyph, "", "", [])).ToList();
            }
        }
    }

    /// <summary>A row tapped on the deck: its address opened on the PC (only http and https).</summary>
    public void Open(string panel, string row)
    {
        PanelRow? r;
        lock (_gate) r = _states.GetValueOrDefault(panel)?.Rows.FirstOrDefault(x => x.Id == row);
        if (r == null || !Uri.TryCreate(r.Open, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Log.Write($"panels: {panel}: can't open a row: {e.Message}");
        }
    }

    /// <summary>Reads every panel again now (the settings changed, say).</summary>
    public void RefreshAll()
    {
        lock (_gate) _readAt.Clear();
        Tick();
    }

    void Tick()
    {
        var configs = Configs();
        foreach (var c in configs)
        {
            lock (_gate)
            {
                if (_reading.Contains(c.Id) || (_readAt.TryGetValue(c.Id, out var at) && DateTime.UtcNow - at < c.Every)) continue;
                _reading.Add(c.Id);
                _readAt[c.Id] = DateTime.UtcNow;
            }
            _ = ReadAsync(c);
        }
    }

    async Task ReadAsync(Config c)
    {
        PanelState state;
        try
        {
            var text = c.Command.Length > 0 ? await RunAsync(c.Command) : await GetAsync(c.Url);
            var (rows, note) = Parse(text);
            state = new PanelState(c.Id, c.Name, c.Glyph, "ok", note, rows);
        }
        catch (Exception e)  // a panel is anybody's script or page: whatever it does, the others go on
        {
            Log.Write($"panels: {c.Id}: {e.Message}");
            lock (_gate) state = new PanelState(c.Id, c.Name, c.Glyph, "error", Strings.OneLine(e.Message, 120), _states.GetValueOrDefault(c.Id)?.Rows ?? []);
        }
        bool changed;
        lock (_gate)
        {
            _reading.Remove(c.Id);
            var before = _states.GetValueOrDefault(c.Id);
            changed = before == null || before.Status != state.Status || before.Note != state.Note || !before.Rows.SequenceEqual(state.Rows);
            _states[c.Id] = state;
        }
        if (changed) Changed?.Invoke();
    }

    static async Task<string> GetAsync(string url)
    {
        using var reply = await Http.GetAsync(url);
        if (!reply.IsSuccessStatusCode) throw new HttpRequestException($"the address answered {(int)reply.StatusCode} {reply.ReasonPhrase}");
        return await reply.Content.ReadAsStringAsync();
    }

    /// <summary>The command's output (through cmd, so it's written as at a prompt); its error's first line when it fails.</summary>
    static async Task<string> RunAsync(string command)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/d /s /c \"{command}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("the command didn't start");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(CommandFor);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"the command took more than {CommandFor.TotalSeconds:0} s");
        }
        if (process.ExitCode != 0)
        {
            var why = (await errors).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            throw new InvalidOperationException($"the command ended with {process.ExitCode}{(why.Length > 0 ? ": " + why : "")}");
        }
        return await output;
    }

    /// <summary>JSON rows (a list, or an object with "rows" and "note"), else a row per line of text.</summary>
    public static (List<PanelRow> Rows, string Note) Parse(string text)
    {
        text = text.Trim().TrimStart('\uFEFF');
        JsonNode? json = null;
        if (text.StartsWith('[') || text.StartsWith('{'))
        {
            try
            {
                json = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }
        var note = "";
        IEnumerable<JsonNode?> items;
        if (json is JsonArray array) items = array;
        else if (json is JsonObject o)
        {
            items = o["rows"] as JsonArray ?? o["items"] as JsonArray ?? [];
            note = Strings.OneLine((string?)(o["note"] as JsonValue) ?? "", 120);
        }
        else items = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => (JsonNode?)JsonValue.Create(l));
        var rows = new List<PanelRow>();
        foreach (var item in items)
        {
            if (rows.Count == MaxRows) break;
            var row = item switch
            {
                JsonValue v when v.TryGetValue(out string? s) => new PanelRow("", s, "", "", ""),
                JsonObject r => new PanelRow((string?)(r["id"] as JsonValue) ?? "", Str(r, "text") ?? Str(r, "title") ?? "", Str(r, "sub") ?? "",
                                             Str(r, "badge") ?? "", Str(r, "open") ?? Str(r, "url") ?? ""),
                _ => null,
            };
            if (row == null || row.Text.Trim().Length == 0) continue;
            rows.Add(row with
            {
                Id = row.Id.Length > 0 ? Strings.OneLine(row.Id, 40) : $"r{rows.Count}",
                Text = Strings.OneLine(row.Text, 90), Sub = Strings.OneLine(row.Sub, 90), Badge = Strings.OneLine(row.Badge, 16),
            });
        }
        return (rows, note);
    }

    static string? Str(JsonObject o, string key) => o[key] is JsonValue v ? v.ToString() : null;

    /// <summary>The panels in the settings: the profile's (without a command), then this PC's, which replace one with the same id.</summary>
    List<Config> Configs()
    {
        var profile = Settings.FromProfile("panels") as JsonArray;
        var machine = Settings.FromMachine("panels") as JsonArray;
        var stamp = $"{profile?.ToJsonString()}|{machine?.ToJsonString()}";
        lock (_gate)
        {
            if (stamp == _configSeen) return _configs;
            _configSeen = stamp;
            var configs = new List<Config>();
            void Add(JsonArray? list, bool mine)
            {
                foreach (var o in (list ?? []).OfType<JsonObject>())
                {
                    var id = ((string?)(o["id"] as JsonValue) ?? "").Trim();
                    var name = ((string?)(o["name"] as JsonValue) ?? id).Trim();
                    var url = ((string?)(o["url"] as JsonValue) ?? "").Trim();
                    var command = ((string?)(o["command"] as JsonValue) ?? "").Trim();
                    if (id.Length == 0) id = Strings.OneLine(name.ToLowerInvariant().Replace(' ', '-'), 40);
                    if (id.Length == 0) continue;
                    if (command.Length > 0 && !mine)
                    {
                        Log.Write($"panels: {id}: a command is only taken from this PC's machine.json, not from a profile");
                        continue;
                    }
                    if (command.Length == 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"))
                    {
                        Log.Write($"panels: {id}: needs a \"url\" (http or https) or, in machine.json, a \"command\"");
                        continue;
                    }
                    var every = o["every"] is JsonValue e && e.TryGetValue(out double seconds) ? seconds : 300;
                    configs.RemoveAll(c => c.Id == id);
                    configs.Add(new Config(id, name.Length > 0 ? name : id, ((string?)(o["glyph"] as JsonValue) ?? "").Trim(), url, command,
                                           TimeSpan.FromSeconds(Math.Clamp(every, 30, 86400))));
                }
            }
            Add(profile, mine: false);
            Add(machine, mine: true);
            _configs = configs.Take(MaxPanels).ToList();
            foreach (var gone in _states.Keys.Except(_configs.Select(c => c.Id)).ToList()) _states.Remove(gone);
            _readAt.Clear();  // read the new ones (and the changed ones) at once
            return _configs;
        }
    }

    static HttpClient MakeHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("flex-screen-companion");
        return http;
    }
}
