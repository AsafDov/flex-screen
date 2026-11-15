namespace FlexScreen.Companion;

/// <summary>
/// The deck's two task messages. The tasks line is kept small (the deck takes lines up to
/// 8 KB, and redraws the card on each): titles and one short line per task. Everything else
/// about a task (its whole title, notes, project, priority, status, whether it can be opened)
/// goes only in the task line, when the deck opens that task (task.open).
/// </summary>
static class TaskMessages
{
    const int MaxDeckTasks = 40;
    const int MaxBytes = 7000;
    const int MaxNoteChars = 1500;

    /// <summary>
    /// Capture's Tasks card: the lists (those chosen for the deck first, with show set; what can
    /// be added to and ticked off), and as many tasks (soonest due first) as fit in a line:
    /// every task in a chosen list, and the dated ones from the rest, for Scheduled.
    /// </summary>
    public static object Tasks(TasksState s, int revision)
    {
        var hidden = Settings.HiddenTaskLists;
        bool Shown(string src, string list) => !hidden.Contains($"{src}/{list}");
        var lists = s.Lists.OrderBy(l => Shown(l.Source, l.Id) ? 0 : 1)
            .Select(l => new { src = l.Source, id = l.Id, name = OneLine(l.Name, 40), show = Shown(l.Source, l.Id), add = l.Add, tick = l.Tick })
            .ToList();
        var wanted = s.Tasks.Where(t => t.Due.Length > 0 || Shown(t.Source, t.List)).ToList();
        var tasks = wanted.Take(MaxDeckTasks).Select(t => new
        {
            src = t.Source, list = t.List, id = t.Id, title = OneLine(t.Title, 90),
            sub = t.Summary.Length > 0 ? OneLine(t.Summary, 60) : Subtitle(t.Notes),
            due = t.Due, done = t.Done, child = t.Parent.Length > 0,
        }).ToList();
        object Message() => new
        {
            t = "state",
            topic = "tasks",
            d = new
            {
                rev = revision, status = s.Status, from = s.From, lists, tasks,
                open = wanted.Count(t => !t.Done), sched = wanted.Count(t => !t.Done && t.Due.Length > 0),
            },
        };
        // Hebrew goes as \uXXXX, six bytes a letter: drop tasks from the end until it fits.
        while (tasks.Count > 0 && System.Text.Json.JsonSerializer.Serialize(Message()).Length > MaxBytes) tasks.RemoveAt(tasks.Count - 1);
        return Message();
    }

    /// <summary>
    /// One task in full, for the deck's task popup: the whole title and the notes (without the
    /// todo skill's "[todo src=…]" tag), "meta" (project, key, priority and status, as the
    /// source has them) and "link" (the Open button's text, "" when it can't be opened). Cut to
    /// fit a line (Hebrew is six bytes a letter).
    /// </summary>
    public static object Detail(TaskSource source, string src, string list, string id)
    {
        var task = source.Find(src, list, id);
        var title = task == null ? "" : OneLine(task.Title, 300);
        var notes = task == null ? "" : Notes(task.Notes);
        var meta = task == null ? "" : OneLine(string.Join("  ·  ", new[] { task.Project, task.Key, task.Priority, task.Status }
            .Where(p => p.Length > 0).Distinct()), 160);
        var link = task != null && source.CanOpen(task) ? $"Open in {source.SourceName(src)}" : "";
        object Message() => new { t = "state", topic = "task", d = new { src, list, id, found = task != null, title, notes, meta, link } };
        while (notes.Length > 0 && System.Text.Json.JsonSerializer.Serialize(Message()).Length > MaxBytes)
            notes = notes[..(notes.Length * 3 / 4)].TrimEnd() + "…";
        return Message();
    }

    static string Notes(string notes)
    {
        var lines = notes.Replace("\r", "").Split('\n').Where(l => !l.TrimStart().StartsWith("[todo", StringComparison.Ordinal));
        var text = string.Join("\n", lines).Trim();
        return text.Length > MaxNoteChars ? text[..(MaxNoteChars - 1)] + "…" : text;
    }

    /// <summary>The notes' first line worth showing: the todo skill's "[todo src=…]" tag is left out.</summary>
    static string Subtitle(string notes)
    {
        var line = notes.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("[todo", StringComparison.Ordinal));
        return line == null ? "" : OneLine(line, 60);
    }

    static string OneLine(string text, int max)
    {
        var line = string.Join(" ", text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > max ? line[..(max - 1)] + "…" : line;
    }
}
