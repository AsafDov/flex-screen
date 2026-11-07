using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Net;
using System.Drawing.Drawing2D;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// The tray icon and the glue between the deck and Windows: pushes state to the deck and
/// carries out the deck's actions.
/// </summary>
sealed class TrayApp : ApplicationContext
{
    static readonly TimeSpan TimeResync = TimeSpan.FromMinutes(10);

    /// <summary>Raw bytes per image part: 4 KB of base64, well under the deck's line limit.</summary>
    const int ArtChunk = 3072;

    /// <summary>
    /// Pause between image parts. The deck drains its 32 KB receive buffer once per loop
    /// pass, and a full redraw holds the loop for up to ~180 ms; at 15 ms a part of album
    /// art arrived corrupted, at 25 ms the buffer stays under 30 KB.
    /// </summary>
    const int ArtChunkGapMs = 25;

    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _status;
    readonly System.Windows.Forms.Timer _tick;
    readonly DeckLink _link = new();
    readonly MediaSource _media = new();
    readonly VolumeSource _volume = new();
    readonly MixerSource _mixer = new();
    readonly DeviceSource _devices = new();
    readonly LocalServer _server = new();
    readonly ClaudeSource _claude = new();
    readonly VsCodeHub _vscode = new();
    readonly DevSource _dev = new();
    readonly GitSource _git = new();
    readonly WindowSource _windows = new();
    readonly KeyboardSource _keyboard = new();
    readonly ClipboardSource _clipboard = new();
    readonly DeckShot _shot = new();
    readonly ObsidianSource _obsidian = new();
    readonly GoogleSource _google = new();
    readonly CallSource _calls;
    readonly ZoneSource _zones;
    readonly HashSet<string> _iconsSent = [];
    readonly object _artGate = new();
    DateTime _timeSentAt = DateTime.MinValue;
    string _loggedMedia = "";
    string _loggedClaude = "";
    string _loggedWindows = "";
    DateTime _settingsSent = DateTime.MinValue;
    bool _locked;
    string _loggedZones = "";
    HashSet<string> _claudeWaiting = [];

    public TrayApp()
    {
        _status = new ToolStripMenuItem("Starting…") { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Connect Google Calendar and Tasks…", null, (_, _) => ConnectGoogle());
        var colors = new ToolStripMenuItem("Deck colors");
        foreach (var (name, label) in new[] { ("base", "Base"), ("vibrant", "Vibrant") })
        {
            var item = new ToolStripMenuItem(label) { Tag = name };
            item.Click += (_, _) => Settings.Set("palette", name);  // the next tick sends it; the deck restarts into it
            colors.DropDownItems.Add(item);
        }
        colors.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem item in colors.DropDownItems) item.Checked = (string?)item.Tag == Settings.Palette;
        };
        menu.Items.Add(colors);
        menu.Items.Add("Open log", null, (_, _) => OpenLog());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        _icon = new NotifyIcon { Icon = MakeIcon(), Text = "flex-screen", ContextMenuStrip = menu, Visible = true };

        Log.Write("companion: starting");
        // State goes out on the deck's hello, not when the port opens: a deck that just booted
        // isn't reading yet, and a burst (album art is ~87 KB of base64) would overflow its buffer.
        _link.Message += OnMessage;
        _media.Changed += m =>
        {
            // Position updates arrive every few seconds; log only track and play/pause changes.
            var summary = m.Present ? $"{m.App} {(m.Playing ? "playing" : "paused")}: {m.Artist} - {m.Title}"
                                    : "nothing playing";
            if (summary != _loggedMedia) Log.Write($"media: {summary}");
            _loggedMedia = summary;
            _link.Send(MediaMessage(m));
        };
        _media.ArtReady += SendArt;
        _volume.Changed += v => _link.Send(VolumeMessage(v));
        _mixer.Changed += apps => _link.Send(MixerMessage(apps));
        _devices.DevicesChanged += d =>
        {
            Log.Write($"devices: output {d.Outputs.FirstOrDefault(x => x.Selected)?.Name ?? "none"}, " +
                      $"input {d.Inputs.FirstOrDefault(x => x.Selected)?.Name ?? "none"}");
            _link.Send(DevicesMessage(d));
        };
        _devices.MicChanged += m =>
        {
            Log.Write($"mic: {(m.Muted ? "muted" : "live")}");
            _link.Send(MicMessage(m));
        };
        _server.ClaudeEvent += _claude.Handle;
        _claude.Changed += sessions =>
        {
            // Tool calls arrive constantly while Claude works; log only state changes.
            var summary = string.Join(", ", sessions.Select(s => $"{s.Project} {s.State}"));
            if (summary != _loggedClaude) Log.Write($"claude: {(summary.Length > 0 ? summary : "no sessions")}");
            _loggedClaude = summary;
            _link.Send(ClaudeMessage(sessions));
            // A session that just stopped for an approval pops up on the deck whatever page it's on.
            var waiting = sessions.Where(s => s.State == "input").ToList();
            var fresh = waiting.Where(s => !_claudeWaiting.Contains(s.Id)).ToList();
            _claudeWaiting = waiting.Select(s => s.Id).ToHashSet();
            foreach (var s in fresh) SendToast(new Toast("attention", $"Claude needs your approval · {s.Project}", "claude.open", s.Id));
        };
        _server.VsCodeConnected += _vscode.Serve;
        _server.TestMessage += OnTestMessage;
        _server.DeckCommand += OnDeckCommand;
        _vscode.Message += _dev.OnMessage;
        _vscode.WindowsChanged += () =>
        {
            // Git follows the VS Code window used last.
            _git.SetFolder(_vscode.Current?.Root);
        };
        _dev.TermChanged += t => _link.Send(TermMessage(t));
        _dev.BuildChanged += b => _link.Send(BuildMessage(b));
        _dev.RecentChanged += r => _link.Send(RecentMessage(r));
        _dev.Toast += SendToast;
        _git.Changed += g => _link.Send(GitMessage(g));
        _git.Toast += SendToast;
        _zones = new ZoneSource(_windows);
        _zones.Toast += SendToast;
        _keyboard.Changed += k => _link.Send(KeyboardMessage(k));
        _keyboard.MapChanged += m => _link.Send(KeymapMessage(m));
        _clipboard.Changed += c => _link.Send(ClipsMessage(c));
        _obsidian.Changed += o => _link.Send(ObsidianMessage(o));
        _obsidian.Toast += SendToast;
        _google.Changed += g => _link.Send(GoogleMessage(g));
        _calls = new CallSource(_devices, _mixer);
        _calls.Changed += c => _link.Send(CallMessage(c));
        _calls.Toast += SendToast;
        _mixer.Changed += _ => { if (_calls.Current.Active) _link.Send(CallMessage(_calls.Current)); };
        _google.Toast += SendToast;
        _windows.Changed += windows =>
        {
            // Titles change all the time (terminals, players); log only which apps are open.
            var summary = string.Join(", ", windows.Select(w => w.Active ? $"[{w.App}]" : w.App));
            if (summary != _loggedWindows) Log.Write($"windows: {summary}");
            _loggedWindows = summary;
            SendWindows(windows);
        };
        _zones.Changed += z =>
        {
            var summary = string.Join(", ", z.Monitors.Select(m =>
                $"{m.Name} {m.Work.Width}x{m.Work.Height} {m.Layout.Type} {m.Layout.Count}: " +
                string.Join(" ", m.Zones.Select(r => $"{r.Rect.X},{r.Rect.Y},{r.Rect.Width}x{r.Rect.Height}{(r.App.Length > 0 ? "=" + r.App : "")}"))));
            if (summary != _loggedZones) Log.Write($"zones: {summary}");
            _loggedZones = summary;
            _link.Send(new { t = "state", topic = "zones", d = ZoneSource.Message(z) });
        };

        Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is not (Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.SessionUnlock)) return;
            _locked = e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock;
            Log.Write($"pc: {(_locked ? "locked" : "unlocked")}");
            _link.Send(PcMessage());
        };
        _server.Start();
        _volume.Start();
        _devices.Start();
        _mixer.Start();
        _ = StartMediaAsync();
        _windows.Start();
        _keyboard.Start();
        _clipboard.Start();
        _obsidian.Start();
        _google.Start();
        _calls.Start();
        _link.Start();

        // UI-thread timer: keeps the deck's "companion" light on and the clock in sync.
        _tick = new System.Windows.Forms.Timer { Interval = 2000 };
        _tick.Tick += (_, _) => Tick();
        _tick.Start();
    }

    async Task StartMediaAsync()
    {
        try
        {
            await _media.StartAsync();
        }
        catch (Exception e)
        {
            Log.Write($"media: unavailable: {e.Message}");
        }
    }

    void Tick()
    {
        _link.Send(new { t = "ping" });
        _link.Send(PcMessage());
        if (Settings.Version != _settingsSent) SendSettings();
        if (DateTime.UtcNow - _timeSentAt > TimeResync) SendTime();
        var port = _link.PortName;
        var text = port != null ? $"Deck connected on {port}" : "Deck not connected";
        _status.Text = text;
        _icon.Text = $"flex-screen: {text}";
    }

    void SendAll()
    {
        SendSettings();
        SendTime();
        if (_media.CurrentArt is { } art) SendArt(art);
        _link.Send(MediaMessage(_media.Current));
        _link.Send(VolumeMessage(_volume.Current));
        _link.Send(MixerMessage(_mixer.Current));
        _link.Send(DevicesMessage(_devices.Devices));
        _link.Send(MicMessage(_devices.Mic));
        _link.Send(ClaudeMessage(_claude.Current));
        _link.Send(TermMessage(_dev.Term));
        _link.Send(BuildMessage(_dev.Build));
        _link.Send(RecentMessage(_dev.Recent));
        _link.Send(GitMessage(_git.Current));
        _link.Send(KeymapMessage(_keyboard.Map));
        _link.Send(KeyboardMessage(_keyboard.Current));
        _link.Send(ClipsMessage(_clipboard.Shown));
        _link.Send(ObsidianMessage(_obsidian.Current));
        _link.Send(GoogleMessage(_google.Current));
        _link.Send(CallMessage(_calls.Current));
        lock (_iconsSent) _iconsSent.Clear();
        SendWindows(_windows.Shown);
        _link.Send(new { t = "state", topic = "zones", d = ZoneSource.Message(_zones.Current) });
    }

    /// <summary>Icons the deck hasn't been sent yet go first, so the tiles never show a placeholder.</summary>
    void SendWindows(IReadOnlyList<DeckWindow> windows)
    {
        foreach (var w in windows)
        {
            bool fresh;
            lock (_iconsSent) fresh = w.Icon.Length > 0 && _iconsSent.Add(w.Icon);
            if (fresh && _windows.FindIcon(w.Icon) is { } icon) SendIcon(icon);
        }
        _link.Send(new
        {
            t = "state",
            topic = "windows",
            d = new
            {
                list = windows.Select(w => new { id = w.Id.ToString(), app = w.App, title = w.Title, icon = w.Icon, active = w.Active }),
            },
        });
    }

    void SendIcon(WindowIcon icon)
    {
        lock (_artGate)
        {
            _link.Send(new
            {
                t = "img",
                kind = "icon",
                id = icon.Id,
                w = WindowSource.IconSize,
                h = WindowSource.IconSize,
                fmt = "rgb565a8",
                part = 0,
                parts = 1,
                off = 0,
                b64 = Convert.ToBase64String(icon.Pixels),
            });
            Thread.Sleep(ArtChunkGapMs);
        }
    }

    void SendToast(Toast toast)
    {
        Log.Write($"toast: {toast.Level}: {toast.Text}");
        _link.Send(new { t = "toast", level = toast.Level, text = toast.Text, action = toast.Action, id = toast.Id });
    }

    static int SecondsSince(DateTime at) => at == DateTime.MinValue ? -1 : (int)(DateTime.UtcNow - at).TotalSeconds;

    static object TermMessage(TermState t) => new
    {
        t = "state",
        topic = "term",
        d = new { repo = t.Repo, running = t.Running, cmd = t.Cmd, secs = SecondsSince(t.Since), exit = t.Exit, ms = t.Ms, lines = t.Lines },
    };

    static object BuildMessage(BuildState b) => new
    {
        t = "state",
        topic = "build",
        d = new { state = b.State, cmd = b.Cmd, ms = b.Ms, secs = SecondsSince(b.At), history = b.History },
    };

    static object RecentMessage(IReadOnlyList<RecentCommand> recent) => new
    {
        t = "state",
        topic = "recent",
        d = new
        {
            list = recent.Take(4).Select(c => new { cmd = OneLine(c.Cmd, 160), running = c.Running, exit = c.Exit, ms = c.Ms, secs = SecondsSince(c.At) }),
        },
    };

    static object GitMessage(GitState g) => new
    {
        t = "state",
        topic = "git",
        d = new
        {
            repo = g.Repo,
            branch = g.Branch,
            ahead = g.Ahead,
            behind = g.Behind,
            m = g.Modified,
            a = g.Added,
            del = g.Deleted,
            u = g.Untracked,
            last = g.Last,
            last_secs = SecondsSince(g.LastAt),
            busy = g.Busy,
            error = g.Error,
        },
    };

    /// <summary>Brings the VS Code window the deck is following to the front.</summary>
    void FocusVsCode()
    {
        var name = _vscode.Current?.Name ?? "";
        if (!WindowFocus.Focus(name, "Code")) Log.Write($"vscode: no window found for {name}");
    }

    static void OpenUrl(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    object CallMessage(CallState c) => new
    {
        t = "state",
        topic = "call",
        d = new
        {
            app = c.App,
            name = c.Name,
            secs = c.Active ? SecondsSince(c.Since) : 0,
            mic_muted = c.MicMuted,
            camera = c.Camera,
            hand = c.Hand,
            deafened = c.Deafened,
            can = c.Can,
            note = c.Note,
            volume = c.Active ? _calls.Volume : -1,
        },
    };

    static object ClipsMessage(IReadOnlyList<Clip> clips) => new
    {
        t = "state",
        topic = "clips",
        d = new
        {
            list = clips.Select(c => new
            {
                id = c.Id,
                text = OneLine(c.Text, 160),
                kind = c.Kind,
                pinned = c.Pinned,
                secs = SecondsSince(c.At),
            }),
        },
    };

    static object ObsidianMessage(ObsidianState o) => new
    {
        t = "state",
        topic = "obsidian",
        d = new { found = o.Found, vault = o.Vault, recent = o.Recent.Select(n => new { name = n.Name, path = n.Path }) },
    };

    static object GoogleMessage(GoogleState g) => new
    {
        t = "state",
        topic = "google",
        d = new
        {
            status = g.Status,
            events = g.Events.Select(e => new
            {
                id = e.Id,
                title = e.Title,
                start = e.Start.ToUnixTimeSeconds(),
                end = e.End.ToUnixTimeSeconds(),
                allday = e.AllDay,
                join = e.Join.Length > 0,
            }),
            tasks = g.Tasks.Select(t => new { list = t.List, id = t.Id, title = t.Title, from = t.From, due = t.Due, done = t.Done }),
            open = g.Tasks.Count(t => !t.Done),
        },
    };

    static string OneLine(string text, int max)
    {
        var line = string.Join(" ", text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > max ? line[..(max - 1)] + "…" : line;
    }

    /// <summary>Starts the Google sign-in, or explains where the OAuth client file goes.</summary>
    void ConnectGoogle()
    {
        if (!_google.HasClient)
        {
            MessageBox.Show(
                "flex-screen reads Google Calendar and Tasks with your own Google OAuth client.\n\n" +
                "In Google Cloud Console (APIs & Services), enable the Google Calendar API and the Google Tasks API, " +
                "create an OAuth client of type \"Desktop app\", download its JSON and save it as:\n\n" +
                GoogleSource.ClientPath + "\n\nThen choose this menu item again to sign in.",
                "flex-screen: connect Google", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await _google.SignInAsync();
            }
            catch (Exception e) when (e is TimeoutException or HttpListenerException or InvalidOperationException)
            {
                Log.Write($"google: sign-in didn't finish: {e.Message}");
            }
        });
    }

    /// <summary>The deck's palette and idle timings, from settings.json.</summary>
    void SendSettings()
    {
        _settingsSent = Settings.Version;
        var (clock, off) = Settings.Idle;
        _link.Send(new { t = "settings", palette = Settings.Palette, idle_clock_s = (int)(clock * 60), idle_off_s = (int)(off * 60) });
    }

    /// <summary>Whether the PC is locked and how long since its last keyboard or mouse input: the deck's idle screen.</summary>
    object PcMessage()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        var idle = GetLastInputInfo(ref info) ? (uint)Environment.TickCount - info.Time : 0;
        return new { t = "state", topic = "pc", d = new { locked = _locked, idle = idle / 1000 } };
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll")] static extern bool LockWorkStation();

    static object KeyboardMessage(KeyboardState k) => new
    {
        t = "state",
        topic = "kbd",
        d = new
        {
            present = k.Present,
            name = k.Name,
            layer = k.Layer,
            label = k.Label,
            // How long the layer's key has been held, so the deck can wait out a second from the press.
            held_ms = k.HeldSince is { } since ? (int)Math.Max(0, (DateTime.UtcNow - since).TotalMilliseconds) : -1,
            key = k.HeldKey,
        },
    };

    /// <summary>
    /// The layer cheat sheet: each key's place (in hundredths of a key, from the keyboard's
    /// top-left) and every layer's labels. Empty without a Vial keyboard.
    /// </summary>
    static object KeymapMessage(KeyboardMap? map)
    {
        if (map == null || map.Keys.Count == 0) return new { t = "state", topic = "keymap", d = new { keys = Array.Empty<int[]>(), layers = Array.Empty<object>() } };

        // A split keyboard's halves move to half a key apart, so the deck can draw bigger keys.
        var spans = map.Keys.Select(k => (Start: k.X - k.W / 2, End: k.X + k.W / 2)).OrderBy(s => s.Start).ToList();
        var gaps = new List<(double At, double By)>();
        var reach = spans[0].End;
        foreach (var (start, end) in spans.Skip(1))
        {
            if (start - reach > 0.75) gaps.Add((start, start - reach - 0.5));
            reach = Math.Max(reach, end);
        }
        double X(KeyPlace k) => k.X - gaps.Where(g => k.X - k.W / 2 >= g.At - 1e-6).Sum(g => g.By);

        var left = map.Keys.Min(k => X(k) - k.W / 2);
        var top = map.Keys.Min(k => k.Y - k.H / 2);
        return new
        {
            t = "state",
            topic = "keymap",
            d = new
            {
                keys = map.Keys.Select(k => new[]
                {
                    (int)Math.Round((X(k) - left) * 100), (int)Math.Round((k.Y - top) * 100), (int)Math.Round(k.W * 100), (int)Math.Round(k.H * 100),
                }),
                layers = map.Layers.Select(l => new { n = l.Index, name = l.Name, keys = l.Labels, dim = l.Dim }),
            },
        };
    }

    static object ClaudeMessage(IReadOnlyList<ClaudeSession> sessions) => new
    {
        t = "state",
        topic = "claude",
        d = new
        {
            sessions = sessions.Select(s => new
            {
                id = s.Id,
                project = s.Project,
                state = s.State,
                detail = s.Detail,
                secs = (int)(DateTime.UtcNow - s.Since).TotalSeconds,  // the deck counts on from here
            }),
        },
    };

    /// <summary>Brings up the window a Claude session runs in: VS Code, the Claude app, or a terminal.</summary>
    void OpenClaudeSession(string id)
    {
        var session = _claude.Current.FirstOrDefault(s => s.Id == id);
        if (session == null) return;
        // The app the session's hooks came from; else guess from the project name.
        if (_claude.WindowOf(id) is var owner and > 0 && ProcessTree.Activate(owner, session.Project)) return;
        if (!WindowFocus.Focus(session.Project, "Code", "WindowsTerminal") && !WindowFocus.Focus("", "Claude"))
        {
            Log.Write($"claude: no window found for {session.Project}");
        }
    }

    void SendTime()
    {
        var now = DateTimeOffset.Now;
        _link.Send(new { t = "time", epoch = now.ToUnixTimeSeconds(), tz = (int)now.Offset.TotalMinutes });
        _timeSentAt = DateTime.UtcNow;
    }

    /// <summary>Sends album art as several "img" parts the deck reassembles (protocol/v0.md).</summary>
    void SendArt(Art art)
    {
        lock (_artGate)
        {
            var parts = (art.Pixels.Length + ArtChunk - 1) / ArtChunk;
            for (var part = 0; part < parts; part++)
            {
                var offset = part * ArtChunk;
                var length = Math.Min(ArtChunk, art.Pixels.Length - offset);
                _link.Send(new
                {
                    t = "img",
                    id = art.Id,
                    w = art.Width,
                    h = art.Height,
                    fmt = "rgb565",
                    part,
                    parts,
                    off = offset,
                    b64 = Convert.ToBase64String(art.Pixels, offset, length),
                });
                Thread.Sleep(ArtChunkGapMs);
            }
        }
    }

    static object MediaMessage(MediaState m) => new
    {
        t = "state",
        topic = "media",
        d = new
        {
            present = m.Present,
            title = m.Title,
            artist = m.Artist,
            app = m.App,
            playing = m.Playing,
            pos = m.Position,
            len = m.Length,
            shuffle = m.Shuffle,
            repeat = m.Repeat,
            art = m.ArtId,
        },
    };

    static object VolumeMessage(VolumeState v) => new
    {
        t = "state",
        topic = "vol",
        d = new { v = v.Level, muted = v.Muted },
    };

    static object MixerMessage(IReadOnlyList<AppVolume> apps) => new
    {
        t = "state",
        topic = "mixer",
        d = new { apps = apps.Select(a => new { id = a.Id, name = a.Name, v = a.Level, muted = a.Muted }) },
    };

    static object DevicesMessage(DeviceLists d) => new
    {
        t = "state",
        topic = "devices",
        d = new
        {
            @out = d.Outputs.Select(x => new { id = x.Id, name = x.Name, detail = x.Detail, sel = x.Selected }),
            @in = d.Inputs.Select(x => new { id = x.Id, name = x.Name, detail = x.Detail, sel = x.Selected }),
        },
    };

    static object MicMessage(MicState m) => new
    {
        t = "state",
        topic = "mic",
        d = new { available = m.Available, muted = m.Muted },
    };

    void OnMessage(JsonNode message)
    {
        switch ((string?)message["t"])
        {
            case "hello":
                Log.Write($"deck: hello {message.ToJsonString()}");
                SendAll();
                break;
            case "act":
                Act((string?)message["a"] ?? "", message);
                break;
            case "log":
                Log.Write($"deck: {(string?)message["msg"]}");
                break;
            case "shot":
                _shot.Add(message);
                break;
        }
    }

    // Deck actions a test may trigger: none of them runs anything.
    static readonly HashSet<string> TestActions = ["win.focus", "win.close", "icon.need", "zone.snap", "zone.layout"];

    /// <summary>For testing without touching the deck: a deck action, or "bench" passed on to the deck.</summary>
    void OnTestMessage(JsonNode message)
    {
        var t = (string?)message["t"];
        var action = (string?)message["a"] ?? "";
        Log.Write($"test: {message.ToJsonString()}");
        if (t == "bench") _link.Send(new { t = "bench" });
        else if (t == "act" && TestActions.Contains(action)) Act(action, message);
    }

    /// <summary>From /deck: switch the deck's page, or take a picture of its screen.</summary>
    async Task<byte[]?> OnDeckCommand(JsonNode command)
    {
        var t = (string?)command["t"];
        Log.Write($"test: deck {command.ToJsonString()}");
        if (t == "page") _link.Send(new { t = "page", i = (int?)command["i"] ?? 0 });
        if (t == "shot") return await _shot.TakeAsync(() => _link.Send(new { t = "shot" }));
        return null;
    }

    void Act(string action, JsonNode message)
    {
        // Slider drags send a stream of these; logging each would drown everything else.
        if (action is not ("vol.set" or "mixer.set")) Log.Write($"act: {action}");
        var app = (string?)message["app"] ?? "";
        var id = (string?)message["id"] ?? "";
        switch (action)
        {
            case "media.toggle": _ = _media.ToggleAsync(); break;
            case "media.next": _ = _media.NextAsync(); break;
            case "media.prev": _ = _media.PreviousAsync(); break;
            case "media.shuffle": _ = _media.ToggleShuffleAsync(); break;
            case "media.repeat": _ = _media.CycleRepeatAsync(); break;
            case "vol.set": _volume.SetLevel((int?)message["v"] ?? 0); break;
            case "vol.mute": _volume.ToggleMute(); break;
            case "mixer.set": _mixer.SetLevel(app, (int?)message["v"] ?? 0); break;
            case "mixer.mute": _mixer.ToggleMute(app); break;
            case "dev.out": _devices.RequestDefault(id, output: true); break;
            case "dev.in": _devices.RequestDefault(id, output: false); break;
            case "mic.mute": _devices.ToggleMicMute(); break;
            case "claude.open": OpenClaudeSession(id); break;
            // The extension forgets the last command when its window reloads; the deck's is the fallback.
            case "term.replay":
                // In the window that ran it; a command on the deck is cut to one line, so the window's own copy is used.
                var replay = new { t = "replay", cmd = _dev.Recent.FirstOrDefault(c => OneLine(c.Cmd, 160) == (string?)message["cmd"])?.Cmd ?? _dev.Term.Cmd };
                if (_dev.RecentWindow is { } w && _vscode.IsOpen(w)) _vscode.Send(w, replay);
                else _vscode.SendToCurrent(replay);
                break;
            case "term.interrupt": _vscode.SendToCurrent(new { t = "interrupt" }); break;
            case "term.focus":
                _vscode.SendToCurrent(new { t = "terminal" });
                FocusVsCode();
                break;
            case "git.commit":
                // Commit messages are typed, so this hands over to VS Code's Source Control box.
                _vscode.SendToCurrent(new { t = "scm" });
                FocusVsCode();
                break;
            case "git.fetch" or "git.pull" or "git.push" or "git.stash": _git.Run(action[4..]); break;
            case "win.focus": if (long.TryParse(id, out var focus)) WindowSource.Focus(focus); break;
            case "win.close": if (long.TryParse(id, out var close)) WindowSource.Close(close); break;
            case "icon.need": if (_windows.FindIcon(id) is { } icon) SendIcon(icon); break;
            case "key.text": KeyInput.Type((string?)message["text"] ?? ""); break;
            case "pc.lock": LockWorkStation(); break;
            case "call.mic": _calls.ToggleMic(); break;
            case "call.camera": _ = Task.Run(_calls.ToggleCamera); break;  // Zoom's shortcuts wait on focus changes
            case "call.hand": _ = Task.Run(_calls.ToggleHand); break;
            case "call.leave": _ = Task.Run(_calls.Leave); break;
            case "call.deafen": _calls.ToggleDeafen(); break;
            case "call.react": _calls.React((string?)message["type"] ?? "like"); break;
            case "call.volume": _calls.SetVolume((int?)message["v"] ?? 0); break;
            case "call.focus": _calls.Focus(); break;
            case "clip.paste": _clipboard.Paste(id); break;
            case "clip.pin": _clipboard.TogglePin(id); break;
            case "clip.clear": _clipboard.Clear(); break;
            case "obs.today": _obsidian.OpenToday(); break;
            case "obs.open": _obsidian.OpenNote((string?)message["path"] ?? ""); break;
            case "task.done": _ = _google.SetDoneAsync((string?)message["list"] ?? "", id, (bool?)message["done"] ?? true); break;
            case "cal.join": OpenUrl(_google.Current.Events.FirstOrDefault(e => e.Id == id)?.Join ?? ""); break;
            case "mail.open": OpenUrl("https://mail.google.com/"); break;
            case "zone.snap": _zones.Snap((int?)message["mon"] ?? -1, (int?)message["zone"] ?? -1, (long?)message["hwnd"] ?? 0); break;
            case "zone.layout": _zones.SetLayout((int?)message["mon"] ?? -1, (string?)message["id"] ?? (string?)message["type"] ?? ""); break;
            default: Log.Write($"act: unknown action {action}"); break;
        }
    }

    static void OpenLog() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.FilePath) { UseShellExecute = true });

    void Quit()
    {
        Log.Write("companion: quitting");
        _tick.Stop();
        _icon.Visible = false;
        ExitThread();
    }

    /// <summary>A small rounded "deck" in the design's accent blue.</summary>
    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            var r = new Rectangle(2, 7, 28, 18);
            const int d = 8;
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(0x5A, 0xB4, 0xFF));
            g.FillPath(fill, path);
            using var bar = new SolidBrush(Color.FromArgb(0x0D, 0x0F, 0x12));
            g.FillRectangle(bar, 6, 11, 20, 3);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
