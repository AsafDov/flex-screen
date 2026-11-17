namespace FlexScreen.Companion;

/// <summary>One task from any source.</summary>
/// <param name="Source">The source's id ("google").</param>
/// <param name="List">The list's id within its source.</param>
/// <param name="Notes">The task's notes as the source has them ("" for none).</param>
/// <param name="Due">"yyyy-MM-dd", or "".</param>
/// <param name="Parent">The parent task's id for a subtask, else "".</param>
record TaskItem(string Source, string List, string Id, string Title, string Notes, string Due, bool Done, string Parent = "")
{
    /// <summary>Where the task lives (a web page, an obsidian:// link), opened on the PC from the deck's popup; "" for none.</summary>
    public string Url { get; init; } = "";

    /// <summary>The issue's key ("ACME-142", "flex-screen#12"), or "".</summary>
    public string Key { get; init; } = "";

    /// <summary>A Jira project, a GitHub repository, the heading a note's task is under; or "".</summary>
    public string Project { get; init; } = "";

    /// <summary>As the source names it ("High", "P1"), or "".</summary>
    public string Priority { get; init; } = "";

    /// <summary>The source's own status ("In Progress"), or "".</summary>
    public string Status { get; init; } = "";

    /// <summary>The row's second line on the deck, when the source has a better one than the notes' first line.</summary>
    public string Summary { get; init; } = "";
}

/// <summary>A list (Google Tasks' task lists; a project, a query, a note… for other sources).</summary>
/// <param name="Add">Tasks can be added to it from the deck.</param>
/// <param name="Tick">Its tasks can be ticked off from the deck (not a shared tracker's issues).</param>
record TaskList(string Source, string Id, string Name, bool Add = true, bool Tick = true);

/// <param name="Status">"ok", or why there's nothing: "setup" (the source isn't set up), "signin", "error".</param>
record TaskRead(string Status, IReadOnlyList<TaskList> Lists, IReadOnlyList<TaskItem> Tasks);

/// <summary>Everything the deck's Tasks card (on Capture) shows.</summary>
/// <param name="Status">"ok" when any source read; else the likeliest reason ("error", "signin", then "setup"); "" before the first read.</param>
/// <param name="From">The source <paramref name="Status"/> is about when it isn't "ok" ("Google Tasks"), else "".</param>
/// <param name="Warn">With "ok": the sources that need signing in again ("Google Tasks and Jira"), else "".</param>
record TasksState(string Status, IReadOnlyList<TaskList> Lists, IReadOnlyList<TaskItem> Tasks, string From = "", string Warn = "");

/// <summary>
/// A place tasks come from: Google Tasks (one per Google account), Microsoft To Do, the
/// latest Obsidian daily note, Jira, GitHub. Each one is a class of its own (GoogleTasks.cs,
/// MicrosoftTodo.cs…) listed for <see cref="TaskSource"/> in TrayApp; nothing else needs to
/// know about it. A source that isn't set up (in this profile, or on this PC) reads as
/// "setup" and is left out.
/// </summary>
interface ITaskProvider
{
    /// <summary>Short and stable: it's in the deck's messages ("google").</summary>
    string Id { get; }

    /// <summary>For people ("Google Tasks").</summary>
    string Name { get; }

    /// <summary>The open tasks. Throws when it can't be read (that source then keeps its last tasks).</summary>
    Task<TaskRead> ReadAsync();

    /// <summary>Only called for a list with <see cref="TaskList.Tick"/>.</summary>
    Task SetDoneAsync(string list, string id, bool done);

    /// <summary>Adds a task; <paramref name="list"/> "" is the source's default list. Only called for a list with <see cref="TaskList.Add"/>.</summary>
    /// <returns>The list's name, for the deck's toast.</returns>
    Task<string> AddAsync(string list, string title, string due);
}

/// <summary>A source that opens its tasks itself (in its own app) rather than at <see cref="TaskItem.Url"/> in the browser.</summary>
interface ITaskOpener
{
    /// <returns>False when it can't, so the link opens instead.</returns>
    bool Open(TaskItem task);
}

/// <summary>
/// Every source's tasks, merged for the deck: open tasks, soonest due first. Tasks ticked
/// off from the deck stay on it, ticked, until the end of the day (so a slip of the finger
/// can be undone). Reads every 2 minutes, and right after any change from the deck.
/// </summary>
sealed class TaskSource
{
    static readonly TimeSpan Every = TimeSpan.FromMinutes(2);

    readonly Func<IReadOnlyList<ITaskProvider>> _sources;
    readonly System.Threading.Timer _timer;
    readonly SemaphoreSlim _busy = new(1, 1);
    readonly Dictionary<string, TaskItem> _doneHere = [];  // by source/list/id: ticked from the deck today
    DateTime _doneDay = DateTime.Today;
    bool _again;  // a change came in mid-read: read once more after it

    /// <param name="sources">The sources there are now: they follow the profile (its Google accounts).</param>
    public TaskSource(Func<IReadOnlyList<ITaskProvider>> sources)
    {
        _sources = sources;
        _timer = new System.Threading.Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public TasksState Current { get; private set; } = new("", [], []);

    /// <summary>Counts the changes; in the deck's message so a snapshot sent again unchanged on
    /// purpose (to undo the deck's own early tick) isn't skipped as a repeat.</summary>
    public int Revision { get; private set; }

    /// <summary>Raised on a worker thread after each refresh that changed something.</summary>
    public event Action<TasksState>? Changed;

    public event Action<Toast>? Toast;

    public void Start() => _timer.Change(TimeSpan.Zero, Every);

    public async Task RefreshAsync()
    {
        if (!await _busy.WaitAsync(0))
        {
            _again = true;
            return;
        }
        try
        {
            do
            {
                _again = false;
                await ReadAllAsync();
            } while (_again);
        }
        finally
        {
            _busy.Release();
        }
    }

    async Task ReadAllAsync()
    {
        if (_doneDay != DateTime.Today)
        {
            _doneHere.Clear();
            _doneDay = DateTime.Today;
        }
        var providers = _sources();
        var reads = await Task.WhenAll(providers.Select(ReadOneAsync));
        var lists = new List<TaskList>();
        var tasks = new List<TaskItem>();
        for (var i = 0; i < reads.Length; i++)
        {
            var read = reads[i];
            if (read.Status != "ok")
            {
                // Can't be read just now: keep showing what it last had.
                if (read.Status == "error")
                {
                    lists.AddRange(Current.Lists.Where(l => l.Source == providers[i].Id));
                    tasks.AddRange(Current.Tasks.Where(t => t.Source == providers[i].Id));
                }
                continue;
            }
            lists.AddRange(read.Lists);
            tasks.AddRange(read.Tasks);
        }
        // Ticked off from the deck today: still shown, ticked; one ticked back on comes back by
        // itself. Not once its source is disconnected.
        var live = providers.Where((_, i) => reads[i].Status is "ok" or "error").Select(p => p.Id).ToHashSet();
        foreach (var key in _doneHere.Where(d => !live.Contains(d.Value.Source)).Select(d => d.Key).ToList()) _doneHere.Remove(key);
        tasks.RemoveAll(t => _doneHere.ContainsKey(Key(t)));
        tasks.AddRange(_doneHere.Values);
        var ok = reads.Any(r => r.Status == "ok") || (reads.Any(r => r.Status == "error") && Current.Status == "ok");
        // Nothing read: the reason worth telling first. A source just not set up on this PC
        // (Jira at home) says nothing while another one is only signed out.
        var why = ok ? -1 : Array.FindIndex(reads, r => r.Status == "error") is >= 0 and var e ? e
                          : Array.FindIndex(reads, r => r.Status == "signin") is >= 0 and var s ? s : reads.Length > 0 ? 0 : -1;
        var status = ok ? "ok" : why >= 0 ? reads[why].Status : "setup";
        var from = why >= 0 && status != "setup" ? providers[why].Name : "";
        // Read, but a source that was set up needs signing in again (the Google sign-in was
        // revoked, say): the deck says so above the others' tasks, which would hide it otherwise.
        var signedOut = ok ? providers.Where((_, i) => reads[i].Status == "signin").Select(p => p.Name).ToList() : [];
        var warn = signedOut.Count switch
        {
            0 => "",
            1 => signedOut[0],
            _ => string.Join(", ", signedOut[..^1]) + " and " + signedOut[^1],
        };
        Publish(new TasksState(status, lists, Sorted(tasks), from, warn));
    }

    async Task<TaskRead> ReadOneAsync(ITaskProvider provider)
    {
        try
        {
            return await provider.ReadAsync();
        }
        catch (Exception e)
        {
            // Whatever went wrong in one source (a mistyped site in the profile, a reply of
            // an unexpected shape), the others are still read and shown.
            Log.Write($"tasks: {provider.Id}: {e.GetType().Name}: {e.Message}");
            return new TaskRead("error", [], []);
        }
    }

    /// <summary>Open first; then overdue and today, the next days, and undated last; then by list.</summary>
    static List<TaskItem> Sorted(IEnumerable<TaskItem> tasks) =>
        tasks.OrderBy(t => t.Done).ThenBy(t => t.Due.Length == 0).ThenBy(t => t.Due, StringComparer.Ordinal)
             .ThenBy(t => t.Source).ThenBy(t => t.List).ToList();

    static string Key(TaskItem t) => $"{t.Source}/{t.List}/{t.Id}";

    ITaskProvider? Provider(string source)
    {
        var providers = _sources();
        return providers.FirstOrDefault(p => p.Id == source) ?? (source.Length == 0 ? providers.FirstOrDefault() : null);
    }

    /// <summary>"Google Tasks" for "google"; the id itself for a source not known.</summary>
    public string SourceName(string source) => _sources().FirstOrDefault(p => p.Id == source)?.Name ?? source;

    public TaskItem? Find(string source, string list, string id) =>
        Current.Tasks.FirstOrDefault(t => t.Source == source && t.List == list && t.Id == id);

    TaskList? ListOf(string source, string list) => Current.Lists.FirstOrDefault(l => l.Source == source && l.Id == list);

    /// <summary>Whether the deck's popup offers to open the task where it lives.</summary>
    public bool CanOpen(TaskItem task) => task.Url.Length > 0 || Provider(task.Source) is ITaskOpener;

    /// <summary>Opens a task where it lives (its source's app, or its page in the browser), from the deck.</summary>
    public void Open(string source, string list, string id)
    {
        if (Find(source, list, id) is not { } task || Provider(source) is not { } provider) return;
        if (provider is ITaskOpener opener && opener.Open(task)) return;
        // Only web pages: the link came from the source's server.
        if (!Uri.TryCreate(task.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            Log.Write($"tasks: opened a {provider.Id} task");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Log.Write($"tasks: can't open a {provider.Id} task: {e.Message}");
        }
    }

    /// <summary>Ticks a task off (or back on), from the deck.</summary>
    public async Task SetDoneAsync(string source, string list, string id, bool done)
    {
        if (Provider(source) is not { } provider) return;
        var task = Current.Tasks.FirstOrDefault(t => t.Source == provider.Id && t.List == list && t.Id == id);
        void Undo()
        {
            // Sent again with a new revision, so the deck takes it and drops its own tick.
            Revision++;
            Changed?.Invoke(Current);
        }
        if (ListOf(provider.Id, list) is { Tick: false })
        {
            // A deck older than read-only lists still has a box on the row.
            Toast?.Invoke(new Toast("attention", $"{provider.Name} items are ticked off in {provider.Name} itself"));
            Undo();
            return;
        }
        try
        {
            await provider.SetDoneAsync(list, id, done);
            Log.Write($"tasks: {(done ? "done" : "not done")}: {provider.Id} task");
        }
        catch (Exception e)  // run from the deck and not awaited: anything left uncaught would leave its tick standing
        {
            Log.Write($"tasks: can't update a {provider.Id} task: {e.Message}");
            Toast?.Invoke(new Toast("error", $"Couldn't update the task in {provider.Name}"));
            Undo();
            return;
        }
        if (task != null)
        {
            var key = Key(task);
            if (done) _doneHere[key] = task with { Done = true };
            else _doneHere.Remove(key);
            Publish(Current with { Tasks = Sorted(Current.Tasks.Select(t => Key(t) == key ? t with { Done = done } : t)) });
        }
        await RefreshAsync();
    }

    /// <summary>A new task, from the deck. <paramref name="due"/> is "yyyy-MM-dd" or "".</summary>
    public async Task AddAsync(string source, string list, string title, string due)
    {
        title = title.Trim();
        if (title.Length == 0 || Provider(source) is not { } provider) return;
        if ((list.Length > 0 ? ListOf(provider.Id, list) : null) is { Add: false })
        {
            Toast?.Invoke(new Toast("attention", $"New tasks can't go into that list"));
            return;
        }
        try
        {
            var name = await provider.AddAsync(list, title, due);
            Log.Write($"tasks: added a {provider.Id} task");
            Toast?.Invoke(new Toast("ok", name.Length > 0 ? $"Added to {name}" : "Task added"));
        }
        catch (Exception e)  // run from the deck and not awaited
        {
            Log.Write($"tasks: can't add a {provider.Id} task: {e.Message}");
            Toast?.Invoke(new Toast("error", $"Couldn't add the task to {provider.Name}"));
            return;
        }
        await RefreshAsync();
    }

    void Publish(TasksState state)
    {
        var changed = state.Status != Current.Status || state.From != Current.From || state.Warn != Current.Warn ||
                      !state.Lists.SequenceEqual(Current.Lists) ||
                      !state.Tasks.SequenceEqual(Current.Tasks);
        Current = state;
        if (!changed) return;
        Revision++;
        Changed?.Invoke(state);
    }
}
