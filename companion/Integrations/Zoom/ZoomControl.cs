using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Accessibility;

namespace FlexScreen.Companion;

/// <summary>One of the current tool's kinds on the deck: a shape, a stamp, an eraser.</summary>
record DrawKind(string Id, string Name);

/// <summary>Drawing in a Zoom meeting, as the deck shows it.</summary>
/// <param name="Mode">"annotate" (on a shared screen) or "whiteboard".</param>
/// <param name="Tools">This mode's tool ids, in the deck's order.</param>
/// <param name="Colors">The palette, as "RRGGBB".</param>
/// <param name="Color">Index into Colors; -1 when Zoom hasn't shown it yet.</param>
/// <param name="Width">Index into the four line widths (thin, regular, thick, bulk); -1 unknown.</param>
/// <param name="Kinds">The current tool's kinds, or none.</param>
/// <param name="CanColor">Color applies to the current tool (else picking one switches to the pen).</param>
/// <param name="CanWidth">The four line widths do (annotation).</param>
/// <param name="Weight">The whiteboard tool's weight slider, from WeightMin to WeightMax; WeightMax 0: it has none.</param>
record DrawState(string Mode, IReadOnlyList<string> Tools, string Tool, IReadOnlyList<string> Colors, int Color, int Width,
                 IReadOnlyList<DrawKind> Kinds, string Kind, bool CanColor, bool CanWidth, bool Undo, bool Redo, bool Save,
                 int Weight = -1, int WeightMin = 0, int WeightMax = 0)
{
    public string Key => $"{Mode}|{Tool}|{Color}|{Width}|{Kind}|{CanColor}|{CanWidth}|{Undo}|{Redo}|{Save}|{Weight}|{WeightMax}|{string.Join(",", Colors)}";
}

/// <summary>Zoom's meeting window as read from its own toolbars.</summary>
/// <param name="Muted">Zoom's mute: 1 muted, 0 live, -1 unknown.</param>
/// <param name="Video">1 on, 0 off, -1 unknown.</param>
record ZoomMeeting(bool InMeeting, int Muted, int Video, bool CanWhiteboard, bool CanAnnotate, DrawState? Draw)
{
    public static readonly ZoomMeeting None = new(false, -1, -1, false, false, null);
    public string Key => $"{InMeeting}|{Muted}|{Video}|{CanWhiteboard}|{CanAnnotate}|{Draw?.Key}";
}

/// <summary>
/// Zoom's meeting controls, annotation tools and whiteboard, pressed the way a screen reader
/// presses them. Zoom has no API for any of this and no shortcuts for the drawing tools, but
/// its own toolbars name every button, with its state, for accessibility:
/// <list type="bullet">
/// <item>The meeting toolbars (<c>ZPControlPanelClass</c>, or <c>ZPFloatToolbarClass</c> while
/// sharing) and the annotation toolbar (<c>ZPAnnotatePanelClass</c>) and its menus
/// (<c>ZPMenuClass</c>, by title: "Draw Menu", "Color Menu"…) through MSAA. A button is
/// pressed by posting a click to its window at its spot, which leaves the mouse where it is
/// and doesn't take the focus.</item>
/// <item>The whiteboard is a web page (WebView2) inside the meeting window, through UI
/// Automation: its tools expand to panels with the colors, the weight slider and the eraser's
/// kind.</item>
/// </list>
/// Everything runs on one thread, one step at a time, so a poll never reads a menu half open.
/// Checked on Zoom Workplace 7.1.9 (2026-11-15).
/// </summary>
sealed class ZoomControl
{
    // ---- the annotation toolbar

    // The deck's tools, in its order. Zoom's Spotlight is its laser pointer.
    static readonly string[] AnnotateTools = ["spotlight", "pen", "highlighter", "vanish", "eraser", "select"];

    // The Color menu, in its order (every pen shares it). Sampled from the menu on screen.
    static readonly (string Name, string Hex)[] AnnotateColors =
    [
        ("White", "FFFFFF"), ("Red", "F42E2E"), ("Yellow", "FFD43C"), ("Green", "3BC255"), ("Blue", "1A73E8"),
        ("Light purple", "B57CF2"), ("Pink", "FF5BB4"), ("Orange", "FF8A1F"), ("Lime-tree green", "A6D93A"), ("Ice blue", "62D3F5"),
        ("Dark grey", "5A5A5A"), ("Dark red", "9E1B1B"), ("Brown", "8A5A2B"), ("Dark green", "1E7A35"), ("Dark blue", "1D3E9E"),
    ];

    static readonly string[] Widths = ["Thin", "Regular", "Thick", "Bulk"];

    // The Draw menu's pens, by tool id.
    static readonly Dictionary<string, string> Pens = new()
    {
        ["pen"] = "Draw Grouping Pen", ["vanish"] = "Draw Grouping Vanishing pen", ["highlighter"] = "Draw Grouping Highlighter",
    };

    static readonly (string Id, string Name, string Item)[] Spotlights = [("spotlight", "Spotlight", "Spotlight"), ("arrow", "Arrow", "Arrow")];

    // ---- the whiteboard

    static readonly string[] BoardTools = ["laser", "pen", "highlighter", "eraser", "grab", "select"];

    // Each tool's colors by name, sampled from its panel on screen (the same name is another
    // color in another tool: the marker's are see-through, the laser's dark). A color Zoom
    // adds later is sampled when its panel is in view, else shown grey.
    static readonly Dictionary<string, Dictionary<string, string>> KnownColors = new()
    {
        ["pen"] = new()
        {
            ["White"] = "FFFFFF", ["Light gray"] = "EDEDED", ["Gray"] = "DEDEDE", ["Medium gray"] = "939393", ["Dark gray"] = "585858",
            ["Black"] = "000000", ["Blue"] = "0072C6", ["Green"] = "00B342", ["Yellow"] = "FFFF00", ["Orange"] = "FF9000", ["Red"] = "FF0000",
            ["Purple"] = "792BA6", ["Cyan"] = "00B3F6", ["Light green"] = "7ED235", ["Light yellow"] = "EBFF54", ["Dark yellow"] = "FFBE00",
            ["Light red"] = "FF32B4", ["Dark purple"] = "E100FF", ["Light blue"] = "B2EAFF", ["Light green (pale)"] = "CCFF9F",
            ["Light yellow (pale)"] = "F7FFBB", ["Light orange"] = "FFE599", ["Light purple (pink)"] = "FFB6E4",
            ["Light purple (violet)"] = "F6B3FF",
        },
        ["highlighter"] = new()
        {
            ["Yellow"] = "FBFFA2", ["Green"] = "D8FFBA", ["Blue"] = "C4FFFA", ["Purple"] = "E6D3FF", ["pink"] = "FFD9F4", ["Orange"] = "FFE8C8",
        },
        ["laser"] = new()
        {
            ["Dark red"] = "D70F1E", ["Dark blue"] = "005AC8", ["Dark yellow"] = "D08D00", ["Dark green"] = "339951",
            ["Dark purple"] = "5300F9", ["Medium gray"] = "6E7680",
        },
    };

    static readonly DrawKind[] Erasers = [new("stroke", "Whole stroke"), new("pixel", "Pixel")];

    // Each tool's group on the toolbar (the button inside it; Grab's has no name of its own).
    static readonly Dictionary<string, string> BoardGroups = new()
    {
        ["laser"] = "Laser", ["pen"] = "Pen", ["highlighter"] = "Highlighter", ["grab"] = "Grab", ["select"] = "Object select",
    };

    // Tools whose button opens a panel of settings: colors, the weight slider, the eraser's kind.
    // The eraser's panel differs by kind (only the pixel eraser has a weight), so it's noted per kind.
    static readonly HashSet<string> Panelled = ["laser", "pen", "highlighter", "eraser"];

    /// <summary>A whiteboard tool's settings, which Zoom shows only in its open panel.</summary>
    sealed class Panel
    {
        public List<(string Name, string Hex)> Colors = [];
        public int Color = -1;
        public int Min, Max, Weight = -1;  // Max 0: no weight slider
    }

    readonly BlockingCollection<Action> _work = new();
    int _pollQueued;
    volatile ZoomMeeting _current = ZoomMeeting.None;
    // Last seen, for while a toolbar is hidden or a panel is closed.
    int _muted = -1, _video = -1;
    readonly Dictionary<string, Panel> _panels = [];  // by whiteboard tool, learned while each panel is open
    readonly HashSet<string> _learning = [];

    public ZoomMeeting Current => _current;

    /// <summary>Raised on Zoom's thread when what the deck shows changes.</summary>
    public event Action? Changed;

    public ZoomControl()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "zoom" };
        thread.SetApartmentState(ApartmentState.STA);  // as screen readers call MSAA
        thread.Start();
    }

    void Run()
    {
        foreach (var job in _work.GetConsumingEnumerable())
        {
            try
            {
                job();
            }
            catch (Exception e) when (e is COMException or ElementNotAvailableException or InvalidOperationException or ArgumentException
                                          or InvalidCastException or UnauthorizedAccessException or TimeoutException)
            {
                var at = e.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("ZoomControl")) ?? "";
                Log.Write($"zoom: {e.GetType().Name} 0x{e.HResult:X8} {e.Message} {at}");
            }
        }
    }

    /// <summary>Reads Zoom again (once, however often it's asked while a read waits).</summary>
    public void Poll()
    {
        if (Interlocked.Exchange(ref _pollQueued, 1) == 0)
            _work.Add(() =>
            {
                _pollQueued = 0;
                Read();
            });
    }

    void Do(Action action) => _work.Add(() =>
    {
        action();
        Thread.Sleep(120);  // Zoom repaints its toolbars after a click
        Read();
    });

    void Read()
    {
        var next = ReadNow();
        var changed = next.Key != _current.Key;
        _current = next;
        if (changed) Changed?.Invoke();
    }

    // ---- meeting controls

    /// <summary>Presses Zoom's own mute button, or runs the fallback when no toolbar shows one.</summary>
    public void ToggleMic(Action fallback) => Do(() => { if (!Press(b => b.Name.Contains("Alt+A"))) fallback(); });

    public void ToggleVideo(Action fallback) =>
        Do(() => { if (!Press(b => b.Name.Contains("Alt+V") && (b.Name.StartsWith("Stop") || b.Name.StartsWith("Start")))) fallback(); });

    /// <summary>The meeting's chat panel, open or closed.</summary>
    public void ToggleMeetingChat() => Do(() => Press(b => b.Name.Contains("Alt+H")));

    /// <summary>
    /// An instant meeting from Zoom Workplace's Home ("Meet"; "New meeting" in older versions),
    /// Zoom opened or brought up first. Says why not when it can't (already in a meeting).
    /// </summary>
    public void NewMeeting(Action<string> say) => Do(() =>
    {
        if (ZoomWindow() is not { } main)
        {
            say("Zoom didn't open");
            return;
        }
        var pids = Pids();
        var home = HomePage(pids);
        if (home == null)
        {
            Msaa.ClickTab(main.Hwnd, "Home");
            home = WaitFor(() => HomePage(pids) is { } h ? (object)h : null, 3000) as IntPtr?;
        }
        if (home is not { } page)
        {
            say("Zoom's Home didn't come up");
            return;
        }
        var tree = WaitFor(() => Uia.Scan(AutomationElement.FromHandle(page)) is { Count: > 0 } t ? t : null, 3000) ?? [];
        if (tree.Any(e => e.Type == ControlType.Button && e.Name == "Back to meeting"))
        {
            say("Already in a Zoom meeting");
            return;
        }
        var meet = tree.FirstOrDefault(e => e.Type == ControlType.Button && e.Name is "Meet" or "New meeting");
        if (meet == null) say("Zoom's New meeting button wasn't found");
        else Uia.Press(meet.Element);
    });

    /// <summary>Zoom Workplace's Chat (Team Chat), brought up.</summary>
    public void OpenChat() => Do(() =>
    {
        if (ZoomWindow() is { } main) Msaa.ClickTab(main.Hwnd, "Chat");
    });

    /// <summary>Zoom Workplace's main window, in front: started, or brought up from the tray, if need be.</summary>
    static Wins.Win? ZoomWindow()
    {
        Wins.Win? Main() => Wins.Find(Pids(), w => w.Visible && w.Class == "ZPPTMainFrmWndClassEx").FirstOrDefault();
        if (Main() is not { } main)
        {
            // Zoom.exe again brings its window up when it's running in the tray, and starts it when not.
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zoom", "bin", "Zoom.exe");
            if (!File.Exists(exe)) return null;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
            main = WaitFor(Main, 15000);
            if (main == null) return null;
        }
        WindowFocus.Activate(main.Hwnd);
        return main;
    }

    /// <summary>The Home tab's web page in Zoom Workplace's main window.</summary>
    static IntPtr? HomePage(HashSet<uint> pids)
    {
        var home = Wins.Find(pids, w => w.Visible && w.Class == "Chrome_WidgetWin_1" && w.Title == "Home" && w.TopClass == "ZPPTMainFrmWndClassEx")
            .FirstOrDefault();
        return home == null ? null : Wins.Children(home.Hwnd).FirstOrDefault(w => w.Class == "Chrome_RenderWidgetHostHWND")?.Hwnd;
    }

    /// <summary>Share (which opens Zoom's picker), or Stop share while sharing.</summary>
    public void Share() => Do(() => Press(b => b.Name.Contains("Alt+S") && !b.Name.Contains("Alt+Shift")));

    /// <summary>The End or Leave button, which asks to confirm in Zoom (the share toolbar has none: the fallback).</summary>
    public void Leave(Action fallback) => Do(() => { if (!Press(b => b.Name.Contains("Alt+Q"))) fallback(); });

    static bool Press(Func<Msaa.Item, bool> which)
    {
        foreach (var bar in Wins.Find(Pids(), w => w.Visible && w.Class is "ZPControlPanelClass" or "ZPFloatToolbarClass"))
        {
            var item = Msaa.Items(bar.Hwnd).FirstOrDefault(i => i.Visible && i.Role == Msaa.PushButton && which(i));
            if (item == null) continue;
            Msaa.Click(bar.Hwnd, item);
            return true;
        }
        return false;
    }

    /// <summary>Opens a new whiteboard from the meeting toolbar's Whiteboards menu.</summary>
    public void OpenWhiteboard() => Do(() =>
    {
        var pids = Pids();
        var bar = Wins.Find(pids, w => w.Visible && w.Class == "ZPControlPanelClass").FirstOrDefault();
        if (bar == null) return;
        if (!Msaa.ClickNamed(bar.Hwnd, "Whiteboards")) return;
        var menu = WaitFor(() => Wins.Find(pids, w => w.Visible && w.Class == "WCN_ModelessWnd")
                                     .FirstOrDefault(w => Msaa.Items(w.Hwnd).Any(i => i.Name == "New whiteboard")));
        if (menu != null) Msaa.ClickNamed(menu.Hwnd, "New whiteboard");
    });

    /// <summary>Starts annotating the shared screen, or stops.</summary>
    public void ToggleAnnotate() => Do(() =>
    {
        var pids = Pids();
        if (AnnotationBar(pids) is { } bar)
        {
            Msaa.ClickNamed(bar.Hwnd, "Close");
            return;
        }
        foreach (var share in Wins.Find(pids, w => w.Visible && w.Class == "ZPFloatToolbarClass"))
            if (Msaa.ClickNamed(share.Hwnd, "Annotate")) return;
    });

    // ---- drawing

    /// <summary>A deck drawing action: tool, color, width, kind (with its id or index), undo, redo, clear, save, close.</summary>
    public void Draw(string what, string id) => Do(() =>
    {
        var pids = Pids();
        if (AnnotationBar(pids) is { } bar) Annotate(bar, pids, what, id);
        else if (BoardPage(pids) is { } board) Board(board, what, id);
    });

    void Annotate(Wins.Win bar, HashSet<uint> pids, string what, string id)
    {
        var items = Msaa.Items(bar.Hwnd);
        switch (what)
        {
            case "tool":
                AnnotateTool(bar, pids, id);
                break;
            case "kind":  // the laser's: a spotlight or an arrow
                if (Spotlights.FirstOrDefault(s => s.Id == id) is { Item: not null } spot) InMenu(bar, pids, "Spotlight", "Spotlight Menu", spot.Item);
                break;
            case "color":
                if (!int.TryParse(id, out var color) || color < 0 || color >= AnnotateColors.Length) return;
                if (!items.Any(i => Msaa.Is(i.Name, "Color") && i.Available))
                {
                    AnnotateTool(bar, pids, "pen");
                    Thread.Sleep(120);
                }
                InMenu(bar, pids, "Color", "Color Menu", AnnotateColors[color].Name);
                break;
            case "width":
                if (!int.TryParse(id, out var width) || width < 0 || width >= Widths.Length) return;
                if (ReadAnnotation(bar, items, Wins.Find(pids, _ => true)).Tool is not ("pen" or "highlighter" or "vanish"))
                {
                    AnnotateTool(bar, pids, "pen");
                    Thread.Sleep(120);
                }
                InMenu(bar, pids, "Draw", "Draw Menu", "Line width Grouping " + Widths[width]);
                break;
            case "undo": Msaa.ClickNamed(bar.Hwnd, "Undo"); break;
            case "redo": Msaa.ClickNamed(bar.Hwnd, "Redo"); break;
            case "save": Msaa.ClickNamed(bar.Hwnd, "Save"); break;
            case "clear":
                // Everyone's drawings when allowed (the one sharing), else this person's own.
                if (OpenMenu(bar, pids, "Clear", "Clear Drawing Menu") is { } menu)
                {
                    var all = Msaa.Items(menu.Hwnd).FirstOrDefault(i => i.Name == "Clear all drawings" && i.Visible && i.Available);
                    if (all != null) Msaa.Click(menu.Hwnd, all);
                    else Msaa.ClickNamed(menu.Hwnd, "Clear my drawings");
                    CloseMenu(menu.Hwnd);
                }
                break;
            case "close": Msaa.ClickNamed(bar.Hwnd, "Close"); break;
        }
    }

    static void AnnotateTool(Wins.Win bar, HashSet<uint> pids, string tool)
    {
        switch (tool)
        {
            case "select": Msaa.ClickNamed(bar.Hwnd, "Select"); break;
            case "eraser": Msaa.ClickNamed(bar.Hwnd, "Eraser"); break;
            case "pen" or "highlighter" or "vanish": InMenu(bar, pids, "Draw", "Draw Menu", Pens[tool]); break;
            case "spotlight": InMenu(bar, pids, "Spotlight", "Spotlight Menu", null); break;  // pressing it chooses it; its menu picks the kind
        }
    }

    /// <summary>Opens a toolbar button's menu, presses an item in it (or none), and closes it.</summary>
    static void InMenu(Wins.Win bar, HashSet<uint> pids, string button, string title, string? item)
    {
        if (OpenMenu(bar, pids, button, title) is not { } menu) return;
        if (item != null) Msaa.ClickNamed(menu.Hwnd, item);
        CloseMenu(menu.Hwnd);
    }

    static Wins.Win? OpenMenu(Wins.Win bar, HashSet<uint> pids, string button, string title)
    {
        Wins.Win? Menu() => Wins.Find(pids, w => w.Visible && w.Class == "ZPMenuClass" && w.Title == title).FirstOrDefault();
        if (Menu() is { } open) return open;  // pressing its button again would close it
        if (!Msaa.ClickNamed(bar.Hwnd, button)) return null;
        return WaitFor(Menu);
    }

    static void CloseMenu(IntPtr menu)
    {
        Thread.Sleep(100);
        if (IsWindowVisible(menu)) Msaa.Key(menu, VkEscape);  // Escape closes the menu, not the annotation
    }

    void Board(IntPtr page, string what, string id)
    {
        var root = AutomationElement.FromHandle(page);
        var tree = Uia.Scan(root);
        var tool = BoardTool(tree);
        switch (what)
        {
            case "tool":
                if (ToolButton(tree, id) is not { } button) return;
                // Choosing one with a panel opens it, which is also a look at its settings.
                if (Panelled.Contains(id)) WithPanel(root, button, panel => Learn(id, panel, page));
                else Uia.Choose(button);
                break;
            case "kind":
                if (tool == "eraser" && id is "stroke" or "pixel" && ToolButton(tree, "eraser") is { } eraser)
                {
                    WithPanel(root, eraser, panel =>
                    {
                        // One switch: off erases whole strokes, on erases under the eraser (and has a weight).
                        var toggle = panel.FirstOrDefault(e => e.Type == ControlType.Button && e.Class.Contains("MuiSwitch-input"));
                        if (toggle != null && (toggle.Toggle == ToggleState.On) != (id == "pixel"))
                        {
                            Uia.Press(toggle.Element);
                            Thread.Sleep(250);
                            panel = Uia.Scan(root);
                        }
                        Learn("eraser", panel, page);
                    });
                }
                break;
            case "color":
                // An index into the palette the deck was sent: the tool's own, or the pen's when it has none.
                if (!int.TryParse(id, out var color)) return;
                var inkTool = _panels.GetValueOrDefault(PanelKey(tool, tree))?.Colors.Count > 0 ? tool : "pen";
                if (ToolButton(tree, inkTool) is not { } ink) return;
                WithPanel(root, ink, panel =>
                {
                    var p = Learn(inkTool, panel, page);
                    var swatches = Swatches(panel);
                    if (color < 0 || color >= swatches.Count) return;
                    Uia.Press(swatches[color].Element);
                    p.Color = color;
                });
                break;
            case "weight":
                if (!int.TryParse(id, out var weight)) return;
                var weighed = _panels.GetValueOrDefault(PanelKey(tool, tree))?.Max > 0 ? tool : "pen";
                if (ToolButton(tree, weighed) is not { } weightTool) return;
                WithPanel(root, weightTool, panel =>
                {
                    var p = Learn(weighed, panel, page);
                    var slider = panel.FirstOrDefault(e => e.Type == ControlType.Slider);
                    if (slider?.Element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var range) != true) return;
                    p.Weight = Math.Clamp(weight, p.Min, p.Max);
                    ((RangeValuePattern)range).SetValue(p.Weight);
                });
                break;
            case "undo": Invoke(tree, "Undo"); break;
            case "redo": Invoke(tree, "Redo"); break;
            case "clear": Invoke(tree, "Clear page"); break;
            case "close":
                if (tree.FirstOrDefault(e => e.Type == ControlType.Button && e.Name.StartsWith("Close whiteboard")) is not { } close) return;
                Uia.Press(close.Element);
                // A blank board asks whether to keep it ("Discard" / "Save"); there's nothing on it to keep.
                var discard = WaitFor(() =>
                {
                    var now = Uia.Scan(root);
                    return now.Any(e => e.Name.Contains("save this blank whiteboard")) ? now.FirstOrDefault(e => e.Type == ControlType.Button && e.Name == "Discard") : null;
                }, 1500);
                if (discard != null) Uia.Press(discard.Element);
                break;
        }
    }

    static void Invoke(List<Uia.El> tree, string name)
    {
        var button = tree.FirstOrDefault(e => e.Type == ControlType.Button && e.Name == name);
        if (button is { Enabled: true }) Uia.Press(button.Element);
    }

    /// <summary>Opens a tool's panel (choosing the tool), works in it, and closes it again if it was closed.</summary>
    static void WithPanel(AutomationElement root, Uia.El tool, Action<List<Uia.El>> work)
    {
        if (!tool.Element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var p)) return;
        var expand = (ExpandCollapsePattern)p;
        var wasOpen = expand.Current.ExpandCollapseState == ExpandCollapseState.Expanded;
        if (!wasOpen) expand.Expand();
        var panel = WaitFor(() =>
        {
            var tree = Uia.Scan(root);
            return tree.Any(e => e.Type == ControlType.Slider || e.Class.Contains("MuiSwitch-input") || IsSwatch(e)) ? tree : null;
        });
        if (panel != null) work(panel);
        Thread.Sleep(150);
        try
        {
            if (!wasOpen && expand.Current.ExpandCollapseState == ExpandCollapseState.Expanded) expand.Collapse();  // a color closes it by itself
        }
        catch (ElementNotAvailableException)
        {
        }
    }

    /// <summary>A panel's colors in its order: the pens' standard swatches (not the custom-color
    /// slots), or the laser's color menu.</summary>
    static List<Uia.El> Swatches(List<Uia.El> panel) => panel.Where(IsSwatch).ToList();

    static bool IsSwatch(Uia.El e) =>
        e.Name.Length > 0 && ((e.Type == ControlType.Button && e.Class.Contains("Swatch") && e.Toggle != null) ||
                              (e.Type == ControlType.MenuItem && e.Class.Contains("colorMenuItem")));

    static bool IsChosen(Uia.El e) => e.Toggle == ToggleState.On || (e.Type == ControlType.MenuItem && e.Class.Contains("_selected_"));

    /// <summary>Where a tool's settings are noted: the eraser's per kind, as its group names it ("Pixel eraser").</summary>
    static string PanelKey(string tool, List<Uia.El> tree)
    {
        if (tool != "eraser") return tool;
        var group = tree.FirstOrDefault(e => e.Type == ControlType.Group && e.Name.EndsWith("eraser", StringComparison.OrdinalIgnoreCase))?.Name ?? "";
        return group.StartsWith("Pixel", StringComparison.OrdinalIgnoreCase) ? "eraser:pixel" : "eraser:stroke";
    }

    /// <summary>Takes note of a tool's open panel: its palette, the chosen color, the weight slider (or that it has none).</summary>
    Panel Learn(string tool, List<Uia.El> panel, IntPtr page)
    {
        var key = PanelKey(tool, panel);
        if (!_panels.TryGetValue(key, out var p)) _panels[key] = p = new Panel();
        var swatches = Swatches(panel);
        if (swatches.Count > 0)
        {
            var names = swatches.Select(s => s.Name).ToList();
            if (!p.Colors.Select(c => c.Name).SequenceEqual(names))
            {
                var known = KnownColors.GetValueOrDefault(tool) ?? [];
                var drawn = names.All(known.ContainsKey) ? null : Screen.Sample(swatches.Select(s => s.Rect).ToList());
                p.Colors = names.Select((n, i) => (n, known.GetValueOrDefault(n) ?? drawn?[i] ?? "808080")).ToList();
                Log.Write($"zoom: {tool} has {names.Count} colors{(names.All(known.ContainsKey) ? "" : drawn == null ? ", new ones not sampled" : ", new ones sampled")}");
            }
            p.Color = swatches.FindIndex(IsChosen);
        }
        if (panel.FirstOrDefault(e => e.Type == ControlType.Slider) is { Max: > 0 } slider)
            (p.Min, p.Max, p.Weight) = ((int)slider.Min, (int)slider.Max, (int)Math.Round(slider.Value));
        else p.Max = 0;
        return p;
    }

    /// <summary>Opens the chosen tool's panel for a moment to learn its settings.</summary>
    void LearnNow(string tool)
    {
        if (BoardPage(Pids()) is not { } page) return;
        var root = AutomationElement.FromHandle(page);
        var tree = Uia.Scan(root);
        if (BoardTool(tree) == tool && ToolButton(tree, tool) is { } button) WithPanel(root, button, panel => Learn(tool, panel, page));
    }

    static Uia.El? ToolButton(List<Uia.El> tree, string tool)
    {
        if (tool == "eraser") return Uia.InGroup(tree, n => n.EndsWith("eraser", StringComparison.OrdinalIgnoreCase));
        return BoardGroups.TryGetValue(tool, out var group) ? Uia.InGroup(tree, n => n == group) : null;
    }

    static string BoardTool(List<Uia.El> tree)
    {
        foreach (var tool in BoardTools)
            if (ToolButton(tree, tool) is { } b && b.Class.Contains("_selected_")) return tool;
        return "";
    }

    // ---- reading

    ZoomMeeting ReadNow()
    {
        var pids = Pids();
        if (pids.Count == 0) return ZoomMeeting.None;
        var windows = Wins.Find(pids, _ => true);
        var bars = windows.Where(w => w.Visible && w.Class is "ZPControlPanelClass" or "ZPFloatToolbarClass").ToList();
        var meeting = bars.Count > 0 || windows.Any(w => w.Visible && w.Class is "ConfMultiTabContentWndClass" or "ZPContentViewWndClass");
        var annotation = windows.FirstOrDefault(w => w.Visible && w.Class == "ZPAnnotatePanelClass");
        if (!meeting && annotation == null)
        {
            _muted = _video = -1;
            _panels.Clear();
            _learning.Clear();
            return ZoomMeeting.None;
        }
        bool canBoard = false, canAnnotate = false, sawMic = false, sawVideo = false;
        foreach (var bar in bars)
        {
            foreach (var i in Msaa.Items(bar.Hwnd))
            {
                if (!i.Visible || i.Role != Msaa.PushButton) continue;
                if (i.Name.Contains("Alt+A"))
                {
                    _muted = i.Name.StartsWith("Unmute") || i.Name.Contains("currently muted") ? 1 : 0;
                    sawMic = true;
                }
                else if (i.Name.Contains("Alt+V") && (i.Name.StartsWith("Stop") || i.Name.StartsWith("Start")))
                {
                    _video = i.Name.StartsWith("Stop") ? 1 : 0;
                    sawVideo = true;
                }
                else if (i.Name == "Whiteboards") canBoard = true;
                else if (i.Name == "Annotate") canAnnotate = true;
            }
        }
        if (bars.Count > 0 && !sawMic) _muted = -1;  // joined without audio
        if (bars.Count > 0 && !sawVideo) _video = -1;
        DrawState? draw = null;
        if (annotation != null) draw = ReadAnnotation(annotation, Msaa.Items(annotation.Hwnd), windows);
        else if (BoardPage(pids, windows) is { } page) draw = ReadBoard(page);
        return new ZoomMeeting(true, _muted, _video, canBoard, canAnnotate, draw);
    }

    DrawState ReadAnnotation(Wins.Win bar, List<Msaa.Item> items, List<Wins.Win> windows)
    {
        // The menus stay as hidden windows once opened, and keep their checks.
        List<Msaa.Item> Menu(string title) =>
            windows.FirstOrDefault(w => w.Class == "ZPMenuClass" && w.Title == title) is { } m ? Msaa.Items(m.Hwnd) : [];
        string? CheckedIn(List<Msaa.Item> menu, string prefix) =>
            menu.FirstOrDefault(i => i.Role == Msaa.Radio && i.Checked && i.Name.StartsWith(prefix))?.Name;

        var picked = items.FirstOrDefault(i => i.Role == Msaa.Radio && i.Checked && i.Visible)?.Name ?? "";
        // Tools the deck doesn't offer (Mouse, Text, Stamp, shapes) come through by their Zoom names.
        string tool = "mouse", kind = "";
        IReadOnlyList<DrawKind> kinds = [];
        var draw = Menu("Draw Menu");
        if (Msaa.Is(picked, "Select")) tool = "select";
        else if (Msaa.Is(picked, "Text")) tool = "text";
        else if (Msaa.Is(picked, "Eraser")) tool = "eraser";
        else if (Msaa.Is(picked, "Stamp")) tool = "stamp";
        else if (Msaa.Is(picked, "Spotlight"))
        {
            tool = "spotlight";
            var item = CheckedIn(Menu("Spotlight Menu"), "");
            kind = Spotlights.FirstOrDefault(s => s.Item == item).Id ?? "spotlight";
            kinds = Spotlights.Select(s => new DrawKind(s.Id, s.Name)).ToList();
        }
        else if (Msaa.Is(picked, "Draw"))
        {
            var pen = CheckedIn(draw, "Draw Grouping");
            tool = Pens.FirstOrDefault(p => p.Value == pen).Key ?? (pen != null || CheckedIn(draw, "Shape Grouping") != null ? "shape" : "pen");
        }
        var colorItem = CheckedIn(Menu("Color Menu"), "");
        var color = Array.FindIndex(AnnotateColors, c => c.Name == colorItem);
        var widthItem = CheckedIn(draw, "Line width Grouping ");
        var width = widthItem == null ? -1 : Array.IndexOf(Widths, widthItem["Line width Grouping ".Length..]);
        bool Available(string name) => items.Any(i => Msaa.Is(i.Name, name) && i.Visible && i.Available);
        return new DrawState("annotate", AnnotateTools, tool, AnnotateColors.Select(c => c.Hex).ToList(), color, width, kinds, kind,
                             Available("Color"), tool is "pen" or "highlighter" or "vanish", Available("Undo"), Available("Redo"),
                             Available("Save"));
    }

    DrawState ReadBoard(IntPtr page)
    {
        var tree = Uia.Scan(AutomationElement.FromHandle(page));
        var tool = BoardTool(tree);
        // A panel open in Zoom shows its tool's settings as they are now; one never seen is
        // opened for a moment, once.
        var open = Panelled.FirstOrDefault(t => ToolButton(tree, t) is { Expanded: true });
        var key = PanelKey(tool, tree);
        if (open != null) Learn(open, tree, page);
        else if (Panelled.Contains(tool) && !_panels.ContainsKey(key) && _learning.Add(key))
            _work.Add(() =>
            {
                LearnNow(tool);
                Read();
            });
        // The eraser's group is named for its kind: "Stroke eraser", "Pixel eraser".
        var eraser = tree.FirstOrDefault(e => e.Type == ControlType.Group && e.Name.EndsWith("eraser", StringComparison.OrdinalIgnoreCase))?.Name ?? "";
        var eraserKind = eraser.StartsWith("Pixel", StringComparison.OrdinalIgnoreCase) ? "pixel" : "stroke";
        var p = _panels.GetValueOrDefault(key);
        var colors = p?.Colors.Select(c => c.Hex).ToList() ?? [];
        bool Enabled(string name) => tree.Any(e => e.Type == ControlType.Button && e.Name == name && e.Enabled);
        // Zoom leaves the laser off the board's header when the meeting window is narrow.
        var tools = BoardTools.Where(t => ToolButton(tree, t) != null).ToList();
        return new DrawState("whiteboard", tools, tool, colors, p?.Color ?? -1, -1, tool == "eraser" ? Erasers : [],
                             tool == "eraser" ? eraserKind : "", colors.Count > 0, false, Enabled("Undo"), Enabled("Redo"), false,
                             p?.Weight ?? -1, p?.Min ?? 0, p?.Max ?? 0);
    }

    static Wins.Win? AnnotationBar(HashSet<uint> pids) => Wins.Find(pids, w => w.Visible && w.Class == "ZPAnnotatePanelClass").FirstOrDefault();

    /// <summary>The whiteboard's web page in the meeting window (not the Whiteboards tab of Zoom's main window).</summary>
    static IntPtr? BoardPage(HashSet<uint> pids, List<Wins.Win>? windows = null)
    {
        windows ??= Wins.Find(pids, _ => true);
        var board = windows.FirstOrDefault(w => w.Visible && w.Class == "Chrome_WidgetWin_1" && w.Title == "Zoom Whiteboard" &&
                                                w.TopClass is "ConfMultiTabContentWndClass" or "ZPContentViewWndClass");
        if (board == null) return null;
        var page = Wins.Children(board.Hwnd).FirstOrDefault(w => w.Class == "Chrome_RenderWidgetHostHWND");
        return page?.Hwnd;
    }

    static T? WaitFor<T>(Func<T?> find, int ms = 800) where T : class
    {
        var until = Environment.TickCount64 + ms;
        while (true)
        {
            if (find() is { } found) return found;
            if (Environment.TickCount64 > until) return null;
            Thread.Sleep(40);
        }
    }

    static HashSet<uint> Pids()
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName("Zoom"))
        {
            using (p) pids.Add((uint)p.Id);
        }
        return pids;
    }

    const int VkEscape = 0x1B;

    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);

    // ---- windows

    internal static class Wins
    {
        /// <param name="TopClass">The class of the top-level window it's in (its own, for a top-level one).</param>
        internal sealed record Win(IntPtr Hwnd, string Class, string Title, bool Visible, string TopClass);

        /// <summary>Zoom's top-level windows and their child windows, hidden ones too.</summary>
        public static List<Win> Find(HashSet<uint> pids, Func<Win, bool> which)
        {
            var list = new List<Win>();
            if (pids.Count == 0) return list;
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (!pids.Contains(pid)) return true;
                var top = Describe(hwnd, null);
                if (which(top)) list.Add(top);
                foreach (var child in Children(hwnd, top.Class))
                    if (which(child)) list.Add(child);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static List<Win> Children(IntPtr parent, string? topClass = null)
        {
            var list = new List<Win>();
            topClass ??= Describe(parent, null).TopClass;
            EnumChildWindows(parent, (hwnd, _) =>
            {
                list.Add(Describe(hwnd, topClass));
                return true;
            }, IntPtr.Zero);
            return list;
        }

        static Win Describe(IntPtr hwnd, string? topClass)
        {
            var cls = new StringBuilder(128);
            GetClassName(hwnd, cls, cls.Capacity);
            var title = new StringBuilder(128);
            GetWindowText(hwnd, title, title.Capacity);
            return new Win(hwnd, cls.ToString(), title.ToString(), IsWindowVisible(hwnd), topClass ?? cls.ToString());
        }

        delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    }

    // ---- MSAA (Zoom's native toolbars)

    internal static class Msaa
    {
        public const int PushButton = 43, Check = 44, Radio = 45, MenuItem = 12, ButtonMenu = 57, PageTab = 37;
        const int StateUnavailable = 0x1, StateChecked = 0x10, StateInvisible = 0x8000;

        /// <summary>An element of a window's accessibility tree, with its spot on screen.</summary>
        internal sealed record Item(string Name, int Role, int State, int X, int Y, int W, int H)
        {
            public bool Visible => (State & StateInvisible) == 0 && W > 0 && H > 0;
            public bool Available => (State & StateUnavailable) == 0;
            public bool Checked => (State & StateChecked) != 0;
        }

        /// <summary>A toolbar name: "Text" is "Text (T)", "Undo" is "Undo (Ctrl+Z)", "Close" is "Close (ESC)".</summary>
        public static bool Is(string name, string what) => name == what || name.StartsWith(what + " (") || name.StartsWith(what + ",");

        /// <summary>Every element under the window, depth first.</summary>
        public static List<Item> Items(IntPtr hwnd)
        {
            var list = new List<Item>();
            var iid = typeof(IAccessible).GUID;
            if (AccessibleObjectFromWindow(hwnd, ObjidClient, ref iid, out var o) != 0 || o is not IAccessible root) return list;
            Walk(root, list, 0);
            return list;
        }

        static void Walk(IAccessible parent, List<Item> list, int depth)
        {
            if (depth > 8) return;
            int count;
            try
            {
                count = parent.accChildCount;
            }
            catch (COMException)
            {
                return;
            }
            if (count <= 0) return;
            var children = new object[count];
            if (AccessibleChildren(parent, 0, count, children, out var got) != 0) return;
            for (var i = 0; i < got; i++)
            {
                if (children[i] is IAccessible child)
                {
                    list.Add(Describe(child, ChildSelf));
                    Walk(child, list, depth + 1);
                }
                else if (children[i] is int id) list.Add(Describe(parent, id));
            }
        }

        static Item Describe(IAccessible acc, object child)
        {
            string name = "";
            int role = 0, state = 0, x = 0, y = 0, w = 0, h = 0;
            try { name = acc.get_accName(child) ?? ""; } catch (COMException) { }
            try { role = acc.get_accRole(child) is int r ? r : 0; } catch (COMException) { }
            try { state = acc.get_accState(child) is int s ? s : 0; } catch (COMException) { }
            try { acc.accLocation(out x, out y, out w, out h, child); } catch (COMException) { }
            return new Item(name, role, state, x, y, w, h);
        }

        /// <summary>Presses the first visible button of that name in the window.</summary>
        public static bool ClickNamed(IntPtr hwnd, string name)
        {
            var item = Items(hwnd).FirstOrDefault(i => i.Visible && Is(i.Name, name) &&
                                                       i.Role is PushButton or Check or Radio or MenuItem or ButtonMenu);
            if (item == null) return false;
            Click(hwnd, item);
            return true;
        }

        /// <summary>Presses one of Zoom Workplace's navigation tabs, named "Chat 3 of 6".</summary>
        public static bool ClickTab(IntPtr hwnd, string tab)
        {
            var item = Items(hwnd).FirstOrDefault(i => i.Visible && i.Role == PageTab && i.Name.StartsWith(tab + " ") && i.Name.Contains(" of "));
            if (item == null) return false;
            Click(hwnd, item);
            return true;
        }

        /// <summary>A click posted to the window at the item's middle: the mouse stays where it is.</summary>
        public static void Click(IntPtr hwnd, Item item)
        {
            var point = new Point { X = item.X + item.W / 2, Y = item.Y + item.H / 2 };
            ScreenToClient(hwnd, ref point);
            var at = (IntPtr)((point.Y << 16) | (point.X & 0xFFFF));
            PostMessage(hwnd, WmMouseMove, IntPtr.Zero, at);
            PostMessage(hwnd, WmLButtonDown, (IntPtr)MkLButton, at);
            PostMessage(hwnd, WmLButtonUp, IntPtr.Zero, at);
        }

        public static void Key(IntPtr hwnd, int vk)
        {
            PostMessage(hwnd, WmKeyDown, vk, 1);
            PostMessage(hwnd, WmKeyUp, vk, unchecked((int)0xC0000001));
        }

        const uint ObjidClient = 0xFFFFFFFC;
        const int ChildSelf = 0;
        const uint WmMouseMove = 0x0200, WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, WmKeyDown = 0x0100, WmKeyUp = 0x0101;
        const int MkLButton = 1;

        struct Point
        {
            public int X, Y;
        }

        [DllImport("oleacc.dll")]
        static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? acc);

        [DllImport("oleacc.dll")]
        static extern int AccessibleChildren(IAccessible parent, int start, int count, [Out] object[] children, out int obtained);

        [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hwnd, ref Point point);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        static void PostMessage(IntPtr hwnd, uint msg, int wParam, int lParam) => PostMessage(hwnd, msg, (IntPtr)wParam, (IntPtr)lParam);
    }

    // ---- the colors as drawn

    internal static class Screen
    {
        /// <summary>The color at the middle of each rectangle (screen pixels) as shown; null when
        /// another window covers any of them (Zoom's web content can't be printed off screen).</summary>
        public static List<string>? Sample(List<System.Windows.Rect> rects)
        {
            var list = new List<string>();
            var screen = GetDC(IntPtr.Zero);
            try
            {
                foreach (var r in rects)
                {
                    var at = new Spot { X = (int)(r.X + r.Width / 2), Y = (int)(r.Y + r.Height / 2) };
                    if (r.IsEmpty) return null;
                    GetWindowThreadProcessId(WindowFromPoint(at), out var pid);
                    using (var owner = Process.GetProcessById((int)pid))
                    {
                        if (!owner.ProcessName.Equals("Zoom", StringComparison.OrdinalIgnoreCase)) return null;
                    }
                    var c = GetPixel(screen, at.X, at.Y);  // 0x00BBGGRR
                    list.Add($"{c & 0xFF:X2}{(c >> 8) & 0xFF:X2}{(c >> 16) & 0xFF:X2}");
                }
            }
            catch (ArgumentException)
            {
                return null;  // that process is gone
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }
            return list;
        }

        struct Spot
        {
            public int X, Y;
        }

        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Spot point);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
    }

    // ---- UI Automation (the whiteboard's web page)

    internal static class Uia
    {
        internal sealed record El(AutomationElement Element, ControlType Type, string Name, string Class, bool Enabled,
                                  ToggleState? Toggle, bool Expanded, double Value, double Min, double Max, System.Windows.Rect Rect);

        static readonly CacheRequest Request = MakeRequest();
        const int ElementGone = unchecked((int)0x80040201);  // UIA_E_ELEMENTNOTAVAILABLE

        static CacheRequest MakeRequest()
        {
            var request = new CacheRequest { AutomationElementMode = AutomationElementMode.Full };
            request.Add(AutomationElement.NameProperty);
            request.Add(AutomationElement.ClassNameProperty);
            request.Add(AutomationElement.ControlTypeProperty);
            request.Add(AutomationElement.IsEnabledProperty);
            request.Add(TogglePattern.ToggleStateProperty);
            request.Add(ExpandCollapsePattern.ExpandCollapseStateProperty);
            request.Add(RangeValuePattern.ValueProperty);
            request.Add(RangeValuePattern.MinimumProperty);
            request.Add(RangeValuePattern.MaximumProperty);
            request.Add(AutomationElement.BoundingRectangleProperty);
            return request;
        }

        /// <summary>Every element of the page in document order, read in one round trip (about 15 ms).</summary>
        public static List<El> Scan(AutomationElement root)
        {
            AutomationElementCollection all;
            try
            {
                using (Request.Clone().Activate()) all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            }
            catch (COMException e) when (e.HResult == ElementGone)
            {
                // The page changed under the read (a panel opening or closing): once more.
                Thread.Sleep(80);
                using (Request.Clone().Activate()) all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            }
            var list = new List<El>(all.Count);
            foreach (AutomationElement e in all)
            {
                var toggle = e.GetCachedPropertyValue(TogglePattern.ToggleStateProperty, true) is ToggleState t ? t : (ToggleState?)null;
                var expand = e.GetCachedPropertyValue(ExpandCollapsePattern.ExpandCollapseStateProperty, true) is ExpandCollapseState x &&
                             x == ExpandCollapseState.Expanded;
                var value = e.GetCachedPropertyValue(RangeValuePattern.ValueProperty, true) is double v ? v : -1;
                var min = e.GetCachedPropertyValue(RangeValuePattern.MinimumProperty, true) is double lo ? lo : 0;
                var max = e.GetCachedPropertyValue(RangeValuePattern.MaximumProperty, true) is double hi ? hi : 0;
                var rect = e.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty, true) is System.Windows.Rect r ? r : System.Windows.Rect.Empty;
                list.Add(new El(e, (ControlType)e.GetCachedPropertyValue(AutomationElement.ControlTypeProperty),
                                e.GetCachedPropertyValue(AutomationElement.NameProperty) as string ?? "",
                                e.GetCachedPropertyValue(AutomationElement.ClassNameProperty) as string ?? "",
                                e.GetCachedPropertyValue(AutomationElement.IsEnabledProperty) is true, toggle, expand, value, min, max, rect));
            }
            return list;
        }

        /// <summary>The button inside a toolbar group of that name (it comes right after the group).</summary>
        public static El? InGroup(List<El> tree, Func<string, bool> group)
        {
            for (var i = 0; i < tree.Count - 1; i++)
                if (tree[i].Type == ControlType.Group && group(tree[i].Name) && tree[i + 1].Type == ControlType.Button) return tree[i + 1];
            return null;
        }

        /// <summary>Chooses a tool: one with a panel is expanded (which chooses it) and folded again.</summary>
        public static void Choose(El tool)
        {
            if (tool.Element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var p))
            {
                var expand = (ExpandCollapsePattern)p;
                if (expand.Current.ExpandCollapseState != ExpandCollapseState.Expanded) expand.Expand();
                Thread.Sleep(200);
                if (expand.Current.ExpandCollapseState == ExpandCollapseState.Expanded) expand.Collapse();
            }
            else Press(tool.Element);
        }

        public static void Press(AutomationElement e)
        {
            if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) ((InvokePattern)invoke).Invoke();
            else if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle)) ((TogglePattern)toggle).Toggle();
            else if (e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
            {
                var pattern = (ExpandCollapsePattern)expand;
                if (pattern.Current.ExpandCollapseState == ExpandCollapseState.Expanded) pattern.Collapse();
                else pattern.Expand();
            }
        }
    }
}
