namespace FlexScreen.Companion;

/// <summary>One task from any source.</summary>
/// <param name="Source">The source's id ("google").</param>
/// <param name="List">The list's id within its source.</param>
/// <param name="Notes">The task's notes as the source has them ("" for none).</param>
/// <param name="Due">"yyyy-MM-dd", or "".</param>
/// <param name="Parent">The parent task's id for a subtask, else "".</param>
record TaskItem(string Source, string List, string Id, string Title, string Notes, string Due, bool Done, string Parent = "");

/// <summary>A list (Google Tasks' task lists; a project, a folder… for other sources).</summary>
record TaskList(string Source, string Id, string Name);

/// <param name="Status">"ok", or why there's nothing: "setup" (the source isn't set up), "signin", "error".</param>
record TaskRead(string Status, IReadOnlyList<TaskList> Lists, IReadOnlyList<TaskItem> Tasks);

/// <summary>Everything the deck's Tasks card (on Capture) shows.</summary>
/// <param name="Status">"ok" when any source read; else the first source's reason; "" before the first read.</param>
record TasksState(string Status, IReadOnlyList<TaskList> Lists, IReadOnlyList<TaskItem> Tasks);

/// <summary>
/// A place tasks come from. Google Tasks is the first; another source (a mail triage, a
/// project tracker, a notes app) implements this and is added to <see cref="TaskSource"/>.
/// </summary>
interface ITaskProvider
{
    /// <summary>Short and stable: it's in the deck's messages ("google").</summary>
    string Id { get; }

    /// <summary>For people ("Google Tasks").</summary>
    string Name { get; }

    /// <summary>The open tasks. Throws HttpRequestException/IOException when it can't be read.</summary>
    Task<TaskRead> ReadAsync();

    Task SetDoneAsync(string list, string id, bool done);

    /// <summary>Adds a task; <paramref name="list"/> "" is the source's default list.</summary>
    /// <returns>The list's name, for the deck's toast.</returns>
    Task<string> AddAsync(string list, string title, string due);
}

/// <summary>
/// Every source's tasks, merged for the deck: open tasks, soonest due first. Tasks ticked
/// off from the deck stay on it, ticked, until the end of the day (so a slip of the finger
/// can be undone). Reads every 2 minutes, and right after any change from the deck.
/// </summary>
sealed class TaskSource
{
    static readonly TimeSpan Every = TimeSpan.FromMinutes(2);

    readonly IReadOnlyList<ITaskProvider> _providers;
    readonly System.Threading.Timer _timer;
    readonly SemaphoreSlim _busy = new(1, 1);
    readonly Dictionary<string, TaskItem> _doneHere = [];  // by source/list/id: ticked from the deck today
    DateTime _doneDay = DateTime.Today;
    bool _again;  // a change came in mid-read: read once more after it

    public TaskSource(params ITaskProvider[] providers)
    {
        _providers = providers;
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
        var reads = await Task.WhenAll(_providers.Select(ReadOneAsync));
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
                    lists.AddRange(Current.Lists.Where(l => l.Source == _providers[i].Id));
                    tasks.AddRange(Current.Tasks.Where(t => t.Source == _providers[i].Id));
                }
                continue;
            }
            lists.AddRange(read.Lists);
            tasks.AddRange(read.Tasks);
        }
        // Ticked off from the deck today: still shown, ticked; one ticked back on comes back by itself.
        tasks.RemoveAll(t => _doneHere.ContainsKey(Key(t)));
        tasks.AddRange(_doneHere.Values);
        var ok = reads.Any(r => r.Status == "ok") || (reads.Any(r => r.Status == "error") && Current.Status == "ok");
        var status = ok ? "ok" : reads.Select(r => r.Status).FirstOrDefault() ?? "setup";
        Publish(new TasksState(status, lists, Sorted(tasks)));
    }

    async Task<TaskRead> ReadOneAsync(ITaskProvider provider)
    {
        try
        {
            return await provider.ReadAsync();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            Log.Write($"tasks: {provider.Id}: {e.Message}");
            return new TaskRead("error", [], []);
        }
    }

    /// <summary>Open first; then overdue and today, the next days, and undated last; then by list.</summary>
    static List<TaskItem> Sorted(IEnumerable<TaskItem> tasks) =>
        tasks.OrderBy(t => t.Done).ThenBy(t => t.Due.Length == 0).ThenBy(t => t.Due, StringComparer.Ordinal)
             .ThenBy(t => t.Source).ThenBy(t => t.List).ToList();

    static string Key(TaskItem t) => $"{t.Source}/{t.List}/{t.Id}";

    ITaskProvider? Provider(string source) =>
        _providers.FirstOrDefault(p => p.Id == source) ?? (source.Length == 0 ? _providers.FirstOrDefault() : null);

    /// <summary>Ticks a task off (or back on), from the deck.</summary>
    public async Task SetDoneAsync(string source, string list, string id, bool done)
    {
        if (Provider(source) is not { } provider) return;
        var task = Current.Tasks.FirstOrDefault(t => t.Source == provider.Id && t.List == list && t.Id == id);
        try
        {
            await provider.SetDoneAsync(list, id, done);
            Log.Write($"tasks: {(done ? "done" : "not done")}: {provider.Id} task");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Write($"tasks: can't update a {provider.Id} task: {e.Message}");
            Toast?.Invoke(new Toast("error", $"Couldn't update the task in {provider.Name}"));
            // Sent again with a new revision, so the deck takes it and drops its own tick.
            Revision++;
            Changed?.Invoke(Current);
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
        try
        {
            var name = await provider.AddAsync(list, title, due);
            Log.Write($"tasks: added a {provider.Id} task");
            Toast?.Invoke(new Toast("ok", name.Length > 0 ? $"Added to {name}" : "Task added"));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Write($"tasks: can't add a {provider.Id} task: {e.Message}");
            Toast?.Invoke(new Toast("error", $"Couldn't add the task to {provider.Name}"));
            return;
        }
        await RefreshAsync();
    }

    void Publish(TasksState state)
    {
        var changed = state.Status != Current.Status || !state.Lists.SequenceEqual(Current.Lists) || !state.Tasks.SequenceEqual(Current.Tasks);
        Current = state;
        if (!changed) return;
        Revision++;
        Changed?.Invoke(state);
    }
}
