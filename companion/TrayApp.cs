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
    readonly GoogleSetup _googleSetup;
    readonly CalendarSources _calendarSources;
    readonly CalendarSource _calendar;
    readonly MicrosoftTodo _todo = new();
    readonly TaskSource _tasks;
    readonly ITaskProvider[] _otherTaskSources;
    readonly Dictionary<string, GoogleTasks> _googleTasks = [];  // by account
    readonly CallSource _calls;
    readonly ZoneSource _zones;
    readonly RecentActions _recent = new();
    readonly Shortcuts _shortcuts;
    readonly ContextBar _context;
    readonly System.Threading.Timer _meetingTick;
    readonly ClaudeApprovals _approvals = new();
    readonly DeckQuestions _questions = new();
    readonly DeckTools _tools;
    readonly ClaudeUsage _usage = new();
    volatile string _deckFirmware = "";  // the deck's, from its hello: what it understands
    readonly HashSet<string> _iconsSent = [];
    readonly object _artGate = new();
    readonly object _actionsGate = new();
    string _actionsSent = "";
    string _shortcutsSent = "";
    string _contextSent = "";
    string _approvalSent = "";
    string _questionSent = "";
    DateTime _timeSentAt = DateTime.MinValue;
    string _loggedMedia = "";
    string _loggedClaude = "";
    string _loggedWindows = "";
    long _settingsSent = -1;
    string _profileSeen = Settings.Profile;  // the profile and accounts at start: a change is a switch
    string _accountsSeen = Settings.AccountsStamp;
    bool _locked;
    string _loggedZones = "";
    HashSet<string> _claudeWaiting = [];

    public TrayApp()
    {
        _status = new ToolStripMenuItem("Starting…") { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        var profile = ProfileSetup.Menu();
        menu.Items.Add(profile);
        menu.Opening += (_, _) => profile.Text = ProfileSetup.Title;
        _googleSetup = new GoogleSetup(_google);
        var googleAccounts = _googleSetup.Menu("Google accounts");
        menu.Items.Add(googleAccounts);
        menu.Items.Add("Shortcuts…", null, (_, _) => Shortcuts.Edit());
        var approve = new ToolStripMenuItem("Approve Claude from the deck");
        approve.Click += (_, _) => Settings.Set("approveFromDeck", !Settings.ApproveFromDeck);
        menu.Opening += (_, _) => approve.Checked = Settings.ApproveFromDeck;
        menu.Items.Add(approve);
        var colors = new ToolStripMenuItem("Deck colors");
        foreach (var (name, label) in Settings.Palettes)
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
        // Which task lists get a chip on the deck; the rest show only their dated tasks, under Scheduled.
        var taskLists = new ToolStripMenuItem("Task lists on the deck");
        taskLists.DropDownItems.Add(new ToolStripMenuItem("No task lists yet") { Enabled = false });
        taskLists.DropDownOpening += (_, _) =>
        {
            taskLists.DropDownItems.Clear();
            var hidden = Settings.HiddenTaskLists;
            var sources = _tasks!.Current.Lists.Select(l => l.Source).Distinct().Count();  // set further down; the menu opens later
            foreach (var list in _tasks.Current.Lists)
            {
                var key = $"{list.Source}/{list.Id}";
                var name = sources > 1 ? $"{list.Name} ({_tasks.SourceName(list.Source)})" : list.Name;
                var item = new ToolStripMenuItem(name) { Checked = !hidden.Contains(key) };
                item.Click += (_, _) => ToggleTaskList(key);
                taskLists.DropDownItems.Add(item);
            }
            if (taskLists.DropDownItems.Count == 0) taskLists.DropDownItems.Add(new ToolStripMenuItem("No task lists yet") { Enabled = false });
        };
        menu.Items.Add(taskLists);
        menu.Items.Add("Open log", null, (_, _) => OpenLog());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        _icon = new NotifyIcon { Icon = MakeIcon(), Text = "flex-screen", ContextMenuStrip = menu, Visible = true };

        Log.Write("companion: starting");
        // State goes out on the deck's hello, not when the port opens: a deck that just booted
        // isn't reading yet, and a burst (album art is ~87 KB of base64) would overflow its buffer.
        _link.Message += OnMessage;
        SetUpUpdates(menu);
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
        _tools = new DeckTools(_questions, _claude, () => _link.PortName != null);
        _server.ClaudeEvent += (hook, _) => _tools.OnHook(hook);
        _server.ClaudeEvent += _claude.Handle;
        _server.ClaudeEvent += (hook, _) => SettleApprovals(hook);
        _server.ClaudePermission += hook =>
        {
            var project = _claude.ProjectFor((string?)hook["session_id"] ?? "", (string?)hook["cwd"] ?? "");
            // Claude's own questions get their options on the deck; everything else Approve and Deny.
            if ((string?)hook["tool_name"] == "AskUserQuestion")
            {
                return _link.PortName != null && Settings.ApproveFromDeck
                    ? _questions.AnswerAskUserQuestion(hook, project, ClaudeApprovals.Wait)
                    : Task.FromResult<string?>(null);
            }
            return _approvals.Ask(hook, project, ClaudeSource.ToolDetail(hook), _link.PortName != null);
        };
        _server.Mcp += _tools.Serve;
        _server.ClaudeStop += _claude.ShouldStop;
        _approvals.Changed += SendApproval;
        _questions.Changed += SendQuestion;
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
            // A request the deck can answer shows its own Approve and Deny, or its options,
            // instead. A question's PreToolUse hook comes a moment before the request it
            // makes, so the toast waits a little for it.
            foreach (var s in fresh)
            {
                _ = Task.Delay(1500).ContinueWith(_ =>
                {
                    if (!_claudeWaiting.Contains(s.Id) || _approvals.Pending.Any(r => r.Session == s.Id) || _questions.Asking(s.Id)) return;
                    SendToast(new Toast("attention", ClaudeSource.IsQuestion(s) ? $"Claude has a question · {s.Project}" : $"Claude needs your approval · {s.Project}",
                                        "claude.open", s.Id));
                });
            }
            _context?.Refresh();  // Claude's model and effort on the context bar
        };
        _server.VsCodeConnected += _vscode.Serve;
        _server.TestMessage += OnTestMessage;
        _server.DeckCommand += OnDeckCommand;
        _vscode.Message += _dev.OnMessage;
        _vscode.WindowsChanged += () =>
        {
            // Git follows the VS Code window used last.
            _git.SetFolder(_vscode.Current?.Root, _vscode.Current?.Remote ?? "");
            _context?.Refresh();  // Claude's tab in front, or not
        };
        _claude.WindowForFolder = cwd => _vscode.HasFolder(cwd) ? VsCodeProcess() : 0;
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
        _calendarSources = new CalendarSources(_google);
        _calendar = new CalendarSource(_calendarSources.Now);
        _calendar.Changed += c => _link.Send(CalendarMessage(c));
        menu.Items.Insert(menu.Items.IndexOf(googleAccounts), new CalendarSetup(_calendar, _calendarSources, _googleSetup).Menu());
        // Every task source: Google Tasks for each of the profile's Google accounts, then the
        // others. One not set up (Jira at home) reads as "setup" and is left out.
        _otherTaskSources = [_todo, new ObsidianTasks(_obsidian), new JiraIssues(), new GitHubIssues()];
        _tasks = new TaskSource(TaskProviders);
        _tasks.Changed += t => _link.Send(TasksMessage(t, _tasks.Revision));
        _tasks.Toast += SendToast;
        _google.SignedIn += () =>
        {
            _ = _calendar.RefreshAsync();
            _ = _tasks.RefreshAsync();
        };
        _todo.SignedIn += () => _ = _tasks.RefreshAsync();
        _todo.Toast += SendToast;
        menu.Items.Insert(menu.Items.IndexOf(googleAccounts) + 1, new TaskSourceSetup(_tasks, _todo, _obsidian, _googleSetup).Menu());
        _calls = new CallSource(_devices, _mixer);
        _calls.Changed += c => _link.Send(CallMessage(c));
        _calls.Toast += SendToast;
        _mixer.Changed += _ => { if (_calls.Current.Active) _link.Send(CallMessage(_calls.Current)); };
        var apps = new AppServices(
            Act: RunContextAction, Toast: SendToast,
            Pick: (id, title, choices, current) =>
                _link.Send(new { t = "pick", id, title, current, choices = choices.Select(c => new { id = c.Id, name = c.Name }) }),
            Call: () => _calls.Current, NextMeeting: NextMeeting, MicMuted: () => _devices.Mic.Muted,
            Claude: ClaudeInWindow, Usage: () => _usage.Label, ClaudeSignedOut: () => _usage.SignedOut,
            VsCode: _vscode.RunCommand, ClaudeInCode: () => _vscode.Current?.ClaudeInFront == true);
        _context = new ContextBar(_windows, RunContextAction, AppIntegrations.All(apps));
        _calendar.Changed += _ => _context.Refresh();
        _devices.MicChanged += _ => _context.Refresh();
        // The next meeting's key counts down ("· 12m").
        _meetingTick = new System.Threading.Timer(_ => _context.Refresh(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        _usage.Changed += () => _context.Refresh();
        _usage.Changed += () => _link.Send(UsageMessage(_usage));
        _server.ClaudeUsage += _usage.Report;
        _context.Changed += _ => SendContext();
        _windows.Polled += _context.OnPolled;
        BrowserTabs.Changed += () => _context.Refresh();  // the tab in front went to another site
        _calls.Changed += _ => _context.Refresh();  // Teams' keys change during a meeting
        _google.Toast += SendToast;
        _windows.Changed += windows =>
        {
            // Titles change all the time (terminals, players); log only which apps are open.
            var summary = string.Join(", ", windows.Select(w => w.Active ? $"[{w.App}]" : w.App));
            if (summary != _loggedWindows) Log.Write($"windows: {summary}");
            _loggedWindows = summary;
            SendWindows(windows);
            SendRecentActions();  // a window tile shows its window's title as it is now
        };
        _recent.Changed += () => SendRecentActions();
        _shortcuts = new Shortcuts(_windows, action => Act(action, new JsonObject()));
        _shortcuts.Changed += () => SendShortcuts();
        _shortcuts.Toast += SendToast;
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
        foreach (var icon in _recent.Icons) _windows.AddIcon(icon);  // the Recent tiles' icons, closed windows' too
        _windows.Start();
        _keyboard.Start();
        _clipboard.Start();
        _obsidian.Start();
        _calendar.Start();
        _tasks.Start();
        _calls.Start();
        _usage.Start();
        _shortcuts.Refresh();
        _context.Refresh();
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
        if (Settings.Version != _settingsSent)
        {
            SendSettings();
            _link.Send(TasksMessage(_tasks.Current, _tasks.Revision));  // hiddenTaskLists edited by hand
        }
        // Not only when the version moved: the deck's hello sends the settings too, and one
        // that came just after a switch would hide it.
        OnProfileOrAccounts();
        _shortcuts.Refresh();
        _context.Refresh();
        if (DateTime.UtcNow - _timeSentAt > TimeResync) SendTime();
        var port = _link.PortName;
        var text = _firmware.Status ?? _updates.Status ?? (port != null ? $"Deck connected on {port}" : "Deck not connected");
        _status.Text = text;
        _icon.Text = $"flex-screen: {text}";
    }

    void SendAll()
    {
        SendSettings();
        SendTime();
        SendState();
        if (_media.CurrentArt is { } art) SendArt(art);  // last: the biggest, and the least missed meanwhile
    }

    // Every topic in one burst overflowed the deck's 32 KB receive buffer while it was busy
    // drawing (the keymap and icons are several KB each), and a lost snapshot stayed lost
    // until that topic next changed. So the burst is paced, and when the deck reports a
    // loss, everything goes again: each topic is a whole snapshot, so a repeat is harmless.
    const int BurstGapMs = 15;
    static readonly TimeSpan ResendWait = TimeSpan.FromSeconds(1.5);
    static readonly TimeSpan ResendEvery = TimeSpan.FromSeconds(5);
    int _resendQueued;
    volatile bool _resendArt;
    DateTime _resentAt = DateTime.MinValue;

    /// <summary>Every topic's snapshot, a little apart so the deck keeps up. Icons go again only
    /// with <paramref name="icons"/> (a new hello): otherwise the deck asks for any it lacks.</summary>
    void SendState(bool icons = true)
    {
        object[] messages =
        [
            MediaMessage(_media.Current), VolumeMessage(_volume.Current), MixerMessage(_mixer.Current),
            DevicesMessage(_devices.Devices), MicMessage(_devices.Mic), ClaudeMessage(_claude.Current),
            TermMessage(_dev.Term), BuildMessage(_dev.Build), RecentMessage(_dev.Recent), GitMessage(_git.Current),
            KeymapMessage(_keyboard.Map), KeyboardMessage(_keyboard.Current), ClipsMessage(_clipboard.Shown),
            ObsidianMessage(_obsidian.Current), CalendarMessage(_calendar.Current), TasksMessage(_tasks.Current, _tasks.Revision),
            CallMessage(_calls.Current), UsageMessage(_usage),
        ];
        foreach (var message in messages)
        {
            _link.Send(message);
            Thread.Sleep(BurstGapMs);
        }
        if (icons) lock (_iconsSent) _iconsSent.Clear();
        SendWindows(_windows.Shown);
        SendRecentActions(force: true);
        SendShortcuts(force: true);
        SendContext(force: true);
        SendApproval(force: true);
        SendQuestion(force: true);
        _link.Send(new { t = "state", topic = "zones", d = ZoneSource.Message(_zones.Current) });
    }

    /// <summary>The deck lost part of what it was sent: everything again, shortly (at most every few seconds).</summary>
    void ResendSoon(bool art)
    {
        if (art) _resendArt = true;
        if (Interlocked.Exchange(ref _resendQueued, 1) == 1) return;
        var wait = ResendWait + (_resentAt + ResendEvery - DateTime.UtcNow is { Ticks: > 0 } left ? left : TimeSpan.Zero);
        _ = Task.Delay(wait).ContinueWith(_ =>
        {
            _resentAt = DateTime.UtcNow;
            var withArt = _resendArt;
            _resendArt = false;
            Interlocked.Exchange(ref _resendQueued, 0);
            Log.Write("link: sending the state again, the deck lost some");
            SendState(icons: false);
            if (withArt && _media.CurrentArt is { } current) SendArt(current);
        });
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
                list = windows.Select(w => new { id = w.Id.ToString(), app = w.App, title = w.Title, icon = w.Icon, active = w.Active, min = w.Minimized }),
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
            draw = c.Draw is not { } d ? null : new
            {
                mode = d.Mode,
                tools = d.Tools,
                tool = d.Tool,
                colors = d.Colors,
                color = d.Color,
                width = d.Width,
                kinds = d.Kinds.Select(k => new { id = k.Id, name = k.Name }),
                kind = d.Kind,
                can_color = d.CanColor,
                can_width = d.CanWidth,
                undo = d.Undo,
                redo = d.Redo,
                save = d.Save,
                weight = d.Weight,
                weight_min = d.WeightMin,
                weight_max = d.WeightMax,
            },
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

    /// <summary>
    /// The calendar for the deck: today's events from midnight and tomorrow's, those not over
    /// yet first (a deck that keeps fewer keeps those), each with a short id, its calendar's
    /// name and color; the ones over are dropped first to keep the line small. The topic is
    /// "calendar" for a deck from 0.24.0, "google" (its first name) for one before.
    /// </summary>
    object CalendarMessage(CalendarState c)
    {
        var now = DateTimeOffset.Now;
        var events = c.Events.Where(e => e.End > now).Concat(c.Events.Where(e => e.End <= now)).Select(e => new
        {
            id = CalendarSource.DeckId(e),
            title = OneLine(e.Title, 80),
            cal = OneLine(e.Calendar, 30),
            start = e.Start.ToUnixTimeSeconds(),
            end = e.End.ToUnixTimeSeconds(),
            allday = e.AllDay,
            join = e.Join.Length > 0,
            color = e.Color ?? 0,
        }).ToList();
        var topic = DeckFirmware.Compare(_deckFirmware, "0.24.0") >= 0 ? "calendar" : "google";
        object Message() => new { t = "state", topic, d = new { status = c.Status, events } };
        // Hebrew goes as \uXXXX, six bytes a letter.
        while (events.Count > 0 && System.Text.Json.JsonSerializer.Serialize(Message()).Length > 7000) events.RemoveAt(events.Count - 1);
        return Message();
    }

    void ToggleTaskList(string key)
    {
        var hidden = Settings.HiddenTaskLists.ToHashSet();
        if (!hidden.Remove(key)) hidden.Add(key);
        Settings.Set("hiddenTaskLists", new JsonArray(hidden.Order().Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()));
        _link.Send(TasksMessage(_tasks.Current, _tasks.Revision));
    }

    // The tasks line stays small; a task's details go only when the deck opens it (TaskMessages).
    static object TasksMessage(TasksState s, int revision) => TaskMessages.Tasks(s, revision);

    void SendTaskDetail(string src, string list, string id) => _link.Send(TaskMessages.Detail(_tasks, src, list, id));

    static string OneLine(string text, int max) => Strings.OneLine(text, max);

    /// <summary>The task sources there are now: one Google Tasks per Google account with its tasks on, then the others.</summary>
    IReadOnlyList<ITaskProvider> TaskProviders()
    {
        var accounts = _google.Accounts.Where(a => a.Tasks).ToList();
        lock (_googleTasks)
        {
            var providers = new List<ITaskProvider>();
            foreach (var account in accounts)
            {
                // The same object while the account stays, so it keeps its lists between reads.
                if (!_googleTasks.TryGetValue(account.Id, out var tasks) || tasks.Account != account)
                    _googleTasks[account.Id] = tasks = new GoogleTasks(account);
                providers.Add(tasks);
            }
            providers.AddRange(_otherTaskSources);
            return providers;
        }
    }

    /// <summary>
    /// Another profile, or other accounts (by hand, from the tray): the calendar, the tasks and
    /// the vault are read again now, rather than at their next turn. The deck says which
    /// profile it's on when that changes. Each tick.
    /// </summary>
    void OnProfileOrAccounts()
    {
        var profile = Settings.Profile;
        var accounts = Settings.AccountsStamp;
        var switched = profile != _profileSeen;
        var refresh = switched || accounts != _accountsSeen;
        _profileSeen = profile;
        _accountsSeen = accounts;
        if (switched)
        {
            Log.Write($"settings: now on the {profile} profile");
            SendToast(new Toast("ok", $"Profile: {profile}"));
            _obsidian.Refresh();
        }
        if (!refresh) return;
        _ = Task.Run(async () =>
        {
            await _calendar.RefreshAsync();
            await _tasks.RefreshAsync();
        });
    }

    /// <summary>The deck's palette and idle timings, from the settings.</summary>
    void SendSettings()
    {
        _settingsSent = Settings.Version;
        var (clock, off) = Settings.Idle;
        _link.Send(new { t = "settings", palette = Settings.Palette, idle_clock_s = (int)(clock * 60), idle_off_s = (int)(off * 60), cheat_hold_s = (int)Math.Round(Settings.CheatSheetSeconds) });
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
            // How long the layer's key has been held, so the deck can time the cheat sheet's hold from the press.
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
                progress = s.Progress == null ? null : new { label = s.Progress.Label, done = s.Progress.Done, total = s.Progress.Total },
                app = s.App,
                model = s.Model,
                stopping = s.Stopping,
            }),
        },
    };

    /// <summary>
    /// The plan's 5-hour usage for the session board: the figure, the week's, the window's
    /// start and reset (UTC seconds, 0 if not known), its readings so far (seconds into the
    /// window, percent) and when the limit comes at the recent pace (0: not before the reset).
    /// </summary>
    static object UsageMessage(ClaudeUsage usage)
    {
        var u = usage.Current;
        var forecast = usage.Forecast();
        static long Unix(DateTime? t) => t is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)).ToUnixTimeSeconds() : 0;
        var start = forecast.Start;
        return new
        {
            t = "state",
            topic = "usage",
            d = new
            {
                five = u == null || usage.Label.Length == 0 ? -1 : (int)Math.Round(u.FiveHour),
                week = u?.SevenDay is { } w ? (int)Math.Round(w) : -1,
                start = Unix(start),
                resets = Unix(u?.FiveHourResets),
                eta = Unix(forecast.Limit),
                rate = Math.Round(forecast.PerHour, 1),
                points = start is { } s0
                    ? usage.History.Where(p => p.At >= s0).Select(p => new[] { (long)(p.At - s0).TotalSeconds, (long)Math.Round(p.Percent) }).ToArray()
                    : [],
            },
        };
    }

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
                _deckFirmware = (string?)message["fw"] ?? "";
                SendAll();
                break;
            case "act":
                Act((string?)message["a"] ?? "", message);
                break;
            case "log":
                var text = (string?)message["msg"] ?? "";
                Log.Write($"deck: {text}");
                // Lost bytes may have been album art: a part that still parsed showed as a band of
                // noise across it on the deck. So the art goes again whatever was lost.
                if (text.StartsWith("receive buffer overflowed") || text.StartsWith("bad line") ||
                    (text.StartsWith("img ") && text.Contains("bad part")))
                {
                    ResendSoon(art: true);
                }
                break;
            case "shot":
                _shot.Add(message);
                break;
        }
    }

    // Deck actions a test may trigger: none of them runs anything on the PC. The task ones
    // change the user's own tasks, as the deck's Tasks card does (for checking it end to end);
    // fw.update updates the deck to the companion's own firmware; update.check only looks for
    // a release and asks at the PC before anything is installed.
    static readonly HashSet<string> TestActions = ["win.focus", "win.close", "icon.need", "zone.snap", "zone.layout", "claude.approve", "claude.deny",
                                                   "claude.answer", "claude.stop", "task.done", "task.add", "task.refresh", "task.open", "fw.update", "update.check"];

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
            case "claude.stop": _claude.RequestStop(id); break;
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
            case "win.close": if (long.TryParse(id, out var close)) _windows.Close(close); break;
            case "icon.need": if (_windows.FindIcon(id) is { } icon) SendIcon(icon); break;
            case "key.text": KeyInput.Type((string?)message["text"] ?? ""); break;
            case "pc.lock": LockWorkStation(); break;
            case "call.mic": _calls.ToggleMic(); break;
            case "call.camera" when !_calls.Current.Active: SendToast(new Toast("attention", "Camera: not in a call")); break;
            case "call.camera": _ = Task.Run(_calls.ToggleCamera); break;  // Zoom's shortcuts wait on focus changes
            case "call.hand": _ = Task.Run(_calls.ToggleHand); break;
            case "call.leave": _ = Task.Run(_calls.Leave); break;
            case "call.deafen": _calls.ToggleDeafen(); break;
            case "call.react": _calls.React((string?)message["type"] ?? "like"); break;
            case "call.volume": _calls.SetVolume((int?)message["v"] ?? 0); break;
            case "call.focus": _calls.Focus(); break;
            case "call.whiteboard": _calls.Whiteboard(); break;
            case "call.share": _calls.Share(); break;
            case "call.chat": _calls.Chat(); break;
            case "claude.login": ClaudeUsage.SignIn(); break;
            case "meet.next": _link.Send(new { t = "page", i = 0 }); break;  // Today, with Up next
            case "meet.join":
                var meetingApp = (string?)message["type"] ?? "";
                if (NextMeeting(meetingApp) is { } joinable) Act("cal.join", new JsonObject { ["id"] = joinable.Id });
                else SendToast(new Toast("attention", $"No {meetingApp switch { "zoom" => "Zoom", "teams" => "Teams", "discord" => "Discord", _ => "" }} meeting coming up"));
                break;
            case "meet.new": _calls.NewMeeting((string?)message["type"] ?? ""); break;
            case "meet.chat": _calls.OpenChat((string?)message["type"] ?? ""); break;
            case "call.annotate": _calls.Annotate(); break;
            case "draw.tool" or "draw.color" or "draw.width" or "draw.weight" or "draw.kind" or "draw.undo" or "draw.redo" or "draw.clear"
                or "draw.save" or "draw.close":
                _calls.Draw(action[5..], id);
                break;
            case "clip.paste": _clipboard.Paste(id); break;
            case "clip.pin": _clipboard.TogglePin(id); break;
            case "clip.clear": _clipboard.Clear(); break;
            case "obs.today": _obsidian.OpenToday(); break;
            case "obs.open": _obsidian.OpenNote((string?)message["path"] ?? ""); break;
            case "task.done": _ = _tasks.SetDoneAsync((string?)message["src"] ?? "", (string?)message["list"] ?? "", id, (bool?)message["done"] ?? true); break;
            case "task.add":
                _ = _tasks.AddAsync((string?)message["src"] ?? "", (string?)message["list"] ?? "", (string?)message["title"] ?? "", (string?)message["due"] ?? "");
                break;
            case "task.refresh": _ = _tasks.RefreshAsync(); break;
            case "task.open": SendTaskDetail((string?)message["src"] ?? "", (string?)message["list"] ?? "", id); break;
            case "task.link": _tasks.Open((string?)message["src"] ?? "", (string?)message["list"] ?? "", id); break;
            case "cal.join": OpenUrl(_calendar.Find(id)?.Join ?? ""); break;
            case "mail.open": OpenUrl("https://mail.google.com/"); break;
            case "zone.snap": _zones.Snap((int?)message["mon"] ?? -1, (int?)message["zone"] ?? -1, (long?)message["hwnd"] ?? 0); break;
            case "zone.layout": _zones.SetLayout((int?)message["mon"] ?? -1, (string?)message["id"] ?? (string?)message["type"] ?? ""); break;
            case "recent.run": RunRecent(id); break;
            case "shortcut.run": _shortcuts.Run(id); break;
            case "ctx.run": _context.Run(id); break;
            case "ctx.set": _context.Set((string?)message["app"] ?? "", (int?)message["slot"] ?? -1, id); break;
            case "ctx.remove": _context.Remove((string?)message["app"] ?? "", (int?)message["slot"] ?? -1); break;
            case "ctx.reset": _context.Reset((string?)message["app"] ?? ""); break;
            case "ctx.pick": _context.Choose(id, (string?)message["choice"] ?? ""); break;
            case "claude.approve" or "claude.deny":
                // From the deck's banner (its request), or a context bar key (the newest).
                if ((id.Length > 0 ? id : _approvals.Pending.LastOrDefault()?.Id) is { } request)
                    _approvals.Answer(request, action == "claude.approve", (bool?)message["always"] ?? false);
                else SendToast(new Toast("ok", "Nothing from Claude waiting for approval"));
                break;
            case "claude.answer":
                // A question's option(s) tapped, or none: dismissed (a note: read).
                _questions.Answer(id, (message["picks"] as JsonArray ?? []).Select(p => p is JsonValue v && v.TryGetValue(out int i) ? i : -1).ToArray());
                break;
            case "desktop.show": KeyInput.Combo("Win+D"); break;
            case "fw.update": _firmware.Start(); break;  // the deck's "firmware is ready" toast, tapped
            case "update.check": _updates.LookNow(); break;
            default: Log.Write($"act: unknown action {action}"); break;
        }
        NoteRecent(action, message);
    }

    // ---- the Today page's Recent card

    /// <summary>Lists a deck action on the Recent card, if it's one worth doing again.</summary>
    void NoteRecent(string action, JsonNode message)
    {
        var id = (string?)message["id"] ?? "";
        static JsonObject Run(string a, params (string Key, string Value)[] fields)
        {
            var o = new JsonObject { ["a"] = a };
            foreach (var (key, value) in fields) o[key] = value;
            return o;
        }
        switch (action)
        {
            case "win.focus":
                if (!long.TryParse(id, out var hwnd) || _windows.All.FirstOrDefault(w => w.Id == hwnd) is not { } window) return;
                var exe = WindowSource.ExePath(hwnd);
                // A closed window of the same app gives up its tile (its app was restarted, say).
                _recent.Note(new RecentAction($"win:{hwnd}", "window", window.App, window.Title, window.Icon, Run("win.focus", ("id", id), ("exe", exe))),
                             replaces: r => r.Kind == "window" && (string?)r.Act["exe"] == exe && exe.Length > 0 &&
                                            !(long.TryParse((string?)r.Act["id"], out var old) && WindowSource.Exists(old)),
                             icon: _windows.FindIcon(window.Icon));  // kept with the tile, for after a restart
                break;
            case "obs.today":
                _recent.Note(new RecentAction("obs:today", "note", "Obsidian", "Today's note", "", Run("obs.today")));
                break;
            case "obs.open":
                var path = (string?)message["path"] ?? "";
                if (path.Length > 0) _recent.Note(new RecentAction($"obs:{path}", "note", "Obsidian", Path.GetFileNameWithoutExtension(path), "", Run("obs.open", ("path", path))));
                break;
            case "term.replay":
                var cmd = (string?)message["cmd"] ?? "";
                if (cmd.Length > 0) _recent.Note(new RecentAction($"term:{cmd}", "command", "Terminal", cmd, "", Run("term.replay", ("cmd", cmd))));
                break;
            case "cal.join":
                if (_calendar.Find(id) is { Join.Length: > 0 } meeting)
                    _recent.Note(new RecentAction($"cal:{id}", "meeting", "Meeting", meeting.Title, "", Run("cal.join", ("id", id), ("url", meeting.Join))));
                break;
            case "mail.open":
                _recent.Note(new RecentAction("mail", "mail", "Gmail", "Inbox", "", Run("mail.open")));
                break;
            case "claude.open":
                if (_claude.Current.FirstOrDefault(s => s.Id == id) is { } session)
                    _recent.Note(new RecentAction($"claude:{id}", "claude", "Claude", session.Project, "", Run("claude.open", ("id", id))));
                break;
            case "dev.out":
                if (_devices.Devices.Outputs.FirstOrDefault(d => d.Id == id) is { } device)
                    _recent.Note(new RecentAction($"dev.out:{id}", "device", "Output", device.Name, "", Run("dev.out", ("id", id))));
                break;
        }
    }

    /// <summary>A Recent tile tapped: the same action again.</summary>
    void RunRecent(string id)
    {
        if (_recent.Find(id) is not { } recent) return;
        var act = recent.Act;
        switch ((string?)act["a"])
        {
            case "win.focus":
                // Done again, so first again: the window it brought up, or (started anew) the tile itself.
                if (FocusOrOpen(recent) is not 0 and var focused) NoteRecent("win.focus", new JsonObject { ["id"] = focused.ToString() });
                else _recent.Note(recent);
                break;
            case "cal.join":
                OpenUrl((string?)act["url"] ?? "");  // the event may have left the calendar's view
                break;
            case { } other:
                Act(other, act);
                break;
        }
    }

    /// <summary>
    /// The window, or (closed since) another window of its app, or else the app started anew.
    /// Returns the window brought up, or 0.
    /// </summary>
    long FocusOrOpen(RecentAction recent)
    {
        if (long.TryParse((string?)recent.Act["id"], out var hwnd) && WindowSource.Exists(hwnd))
        {
            WindowSource.Focus(hwnd);
            return hwnd;
        }
        var exe = (string?)recent.Act["exe"] ?? "";
        if (exe.Length == 0) return 0;
        if (_windows.All.FirstOrDefault(w => string.Equals(WindowSource.ExePath(w.Id), exe, StringComparison.OrdinalIgnoreCase)) is { } other)
        {
            WindowSource.Focus(other.Id);
            return other.Id;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Write($"recent: can't start {exe}: {e.Message}");
            SendToast(new Toast("error", $"{recent.Name} isn't open"));
        }
        return 0;
    }

    /// <summary>The Recent card, when it changed (or <paramref name="force"/>, on the deck's hello).</summary>
    void SendRecentActions(bool force = false)
    {
        var open = _windows.All;
        var list = _recent.Current.Select(r =>
        {
            // A window that's still open shows its title and icon as they are now.
            if (r.Kind == "window" && long.TryParse((string?)r.Act["id"], out var hwnd) && open.FirstOrDefault(w => w.Id == hwnd) is { } live)
                return new { id = r.Id, kind = r.Kind, name = live.App, title = OneLine(live.Title, 120), icon = live.Icon };
            // An icon this run can't hand out (a tile saved before tiles kept theirs) isn't
            // named: the deck shows the tile's glyph instead of asking for it again and again.
            var icon = r.Icon.Length > 0 && _windows.FindIcon(r.Icon) != null ? r.Icon : "";
            return new { id = r.Id, kind = r.Kind, name = r.Name, title = OneLine(r.Title, 120), icon };
        }).ToList();
        lock (_actionsGate)
        {
            var key = System.Text.Json.JsonSerializer.Serialize(list);
            if (!force && key == _actionsSent) return;
            _actionsSent = key;
        }
        // Icons the deck hasn't been sent go first, as for the Windows page.
        foreach (var icon in list.Select(a => a.icon).Where(i => i.Length > 0).Distinct())
        {
            bool fresh;
            lock (_iconsSent) fresh = _iconsSent.Add(icon);
            if (fresh && _windows.FindIcon(icon) is { } pixels) SendIcon(pixels);
        }
        _link.Send(new { t = "state", topic = "actions", d = new { list } });
    }

    /// <summary>The launcher's tiles, when they changed (or <paramref name="force"/>, on the deck's hello).</summary>
    void SendShortcuts(bool force = false)
    {
        var list = _shortcuts.Current.Select(s => new { id = s.Id, name = s.Name, icon = s.Icon, glyph = s.Look }).ToList();
        lock (_actionsGate)
        {
            var key = System.Text.Json.JsonSerializer.Serialize(list);
            if (!force && key == _shortcutsSent) return;
            _shortcutsSent = key;
        }
        foreach (var icon in list.Select(s => s.icon).Where(i => i.Length > 0).Distinct())
        {
            bool fresh;
            lock (_iconsSent) fresh = _iconsSent.Add(icon);
            if (fresh && _windows.FindIcon(icon) is { } pixels) SendIcon(pixels);
        }
        _link.Send(new { t = "state", topic = "shortcuts", d = new { list } });
    }

    /// <summary>The calendar's next timed meeting in that app ("zoom", "teams", "discord": by its link) that isn't over.</summary>
    CalendarEvent? NextMeeting(string app)
    {
        var now = DateTimeOffset.UtcNow;
        return _calendar.Current.Events.Where(e => !e.AllDay && e.End > now && MeetingLinks.App(e.Join) == app).MinBy(e => e.Start);
    }

    /// <summary>A context bar action: a deck action, or one with a type ("call.react:like").</summary>
    void RunContextAction(string action)
    {
        var message = new JsonObject();
        if (action.Split(':', 2) is [var name, var type])
        {
            action = name;
            message["type"] = type;
        }
        Act(action, message);
    }

    /// <summary>The model and effort of the Claude session used last in that window's app.</summary>
    (string Model, string Effort) ClaudeInWindow(long hwnd)
    {
        GetWindowThreadProcessId((IntPtr)hwnd, out var pid);
        return _claude.InfoIn((int)pid);
    }

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    /// <summary>VS Code's main process, which owns all its windows (what the context bar's window resolves to); 0 if it isn't running.</summary>
    static int VsCodeProcess()
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("Code"))
        {
            using (process)
            {
                if (process.MainWindowHandle != IntPtr.Zero) return process.Id;
            }
        }
        return 0;
    }

    /// <summary>Claude went on without the deck: a tool that ran, or the turn ending, settles what it had asked.</summary>
    void SettleApprovals(JsonNode hook)
    {
        var session = (string?)hook["session_id"] ?? "";
        switch ((string?)hook["hook_event_name"])
        {
            case "PostToolUse":
                _approvals.Settled(session, (string?)hook["tool_name"], hook["tool_input"]);
                if ((string?)hook["tool_name"] == "AskUserQuestion") _questions.Settled(session, notesToo: false);
                break;
            case "UserPromptSubmit" or "Stop" or "SessionEnd":
                _approvals.Settled(session);
                // Its question was answered at the PC; and once you write to it, you've read its notes.
                _questions.Settled(session, notesToo: (string?)hook["hook_event_name"] == "UserPromptSubmit");
                if ((string?)hook["hook_event_name"] == "Stop") _usage.Soon();  // the turn used some
                break;
        }
    }

    /// <summary>The newest permission request waiting for the deck (an empty id: none).</summary>
    void SendApproval() => SendApproval(force: false);

    void SendApproval(bool force)
    {
        var pending = _approvals.Pending;
        var newest = pending.LastOrDefault();
        var d = newest == null
            ? new { id = "", project = "", tool = "", detail = "", always = false, count = 0 }
            : new { id = newest.Id, project = newest.Project, tool = newest.Tool, detail = newest.Detail, always = newest.Suggestions != null, count = pending.Count };
        lock (_actionsGate)
        {
            var key = System.Text.Json.JsonSerializer.Serialize(d);
            if (!force && key == _approvalSent) return;
            _approvalSent = key;
        }
        _link.Send(new { t = "state", topic = "approval", d });
    }

    /// <summary>What Claude asks or tells you on the deck (an empty id: nothing).</summary>
    void SendQuestion() => SendQuestion(force: false);

    void SendQuestion(bool force)
    {
        var shown = _questions.Shown;
        object d = shown is not { Item: var q, Waiting: var waiting }
            ? new { id = "" }
            : new
            {
                id = q.Id, kind = q.Kind, level = q.Level, session = q.Session, project = q.Project, text = q.Text,
                options = q.Options.Select(o => new { label = o.Label, note = o.Note }),
                multi = q.Multi, step = q.Step, steps = q.Steps, count = waiting,
            };
        lock (_actionsGate)
        {
            var key = System.Text.Json.JsonSerializer.Serialize(d);
            if (!force && key == _questionSent) return;
            _questionSent = key;
        }
        _link.Send(new { t = "state", topic = "question", d });
    }

    /// <summary>The context bar, when it changed (or <paramref name="force"/>, on the deck's hello).</summary>
    void SendContext(bool force = false)
    {
        var bar = _context.Current;
        var d = new
        {
            app = bar.App,
            key = bar.Key,
            icon = bar.Icon,
            color = bar.Color,
            keys = bar.Keys.Select(k => new { id = k.Id, name = k.Name, glyph = k.Glyph }),
            bank = bar.Bank.Select(k => new { id = k.Id, name = k.Name, glyph = k.Glyph }),
            own = bar.Own,
        };
        lock (_actionsGate)
        {
            var key = System.Text.Json.JsonSerializer.Serialize(d);
            if (!force && key == _contextSent) return;
            _contextSent = key;
        }
        if (bar.Icon.Length > 0)
        {
            bool fresh;
            lock (_iconsSent) fresh = _iconsSent.Add(bar.Icon);
            if (fresh && _windows.FindIcon(bar.Icon) is { } pixels) SendIcon(pixels);
        }
        _link.Send(new { t = "state", topic = "context", d });
    }


    static void OpenLog() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.FilePath) { UseShellExecute = true });

    // ---- updates: the deck's firmware from this companion, and the companion from GitHub

    DeckFirmware _firmware = null!;
    SelfUpdate _updates = null!;
    SynchronizationContext _ui = null!;
    Action? _balloonClick;

    /// <summary>The tray's two update items and what they need; called once the menu and the icon exist.</summary>
    void SetUpUpdates(ContextMenuStrip menu)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _icon.BalloonTipClicked += (_, _) => _balloonClick?.Invoke();
        _firmware = new DeckFirmware(_link, SendToast, Notify);
        _updates = new SelfUpdate(Notify, Quit, () => _firmware.Running);
        _firmware.DeckIsNewer += _updates.LookSoon;
        menu.Items.Insert(2, _firmware.MenuItem);  // under the status line, only while there's an update for the deck
        menu.Items.Insert(menu.Items.Count - 1, _updates.MenuItem);  // above Quit
        menu.Opening += (_, _) =>
        {
            _firmware.RefreshMenu();
            _updates.RefreshMenu();
        };
    }

    /// <summary>A notification from the tray icon; <paramref name="onClick"/> runs if it's clicked.</summary>
    void Notify(string title, string text, ToolTipIcon icon, Action? onClick) => _ui.Post(_ =>
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(15000, title, text, icon);
    }, null);

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
