using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FlexScreen.Companion;

/// <summary>
/// The latest Obsidian daily note's tasks as a task source: its open "- [ ]" lines (and
/// "- [/]", in progress), subtasks by indentation, a task's indented bullets as its notes, the
/// heading it's under, and the Tasks plugin's 📅 due date (⏳ scheduled when there's none) and
/// priority. Ticking one off rewrites its box to [x], with the Tasks plugin's ✅ date when
/// that plugin is on; unticking puts it back. A task added from the deck goes at the end of
/// the latest daily note (today's, from the template, when there's none yet). Only the latest
/// note: the way of working where each day's note carries the open tasks over. Off with
/// settings.json's "obsidianTasks": false.
/// </summary>
sealed class ObsidianTasks(ObsidianSource obsidian) : ITaskProvider, ITaskOpener
{
    const string List = "daily";
    const string ListName = "Daily note";
    const string Vs = "️?";  // an emoji's optional variation selector
    const string AnySignifier = "📅|📆|🗓|⏳|⌛|🛫|➕|✅|❌|🔁|🆔|⛔|🔺|⏫|🔼|🔽|⏬";

    // "- [ ] text", "* [x] text", "1. [/] text": the part up to the box, its mark, and the rest.
    static readonly Regex TaskLine = new(@"^(?<lead>[ \t]*(?:[-*+]|\d+[.)])[ \t]+\[)(?<mark>[^\]])(?<rest>\](?:[ \t]+(?<text>.*))?)$");
    static readonly Regex Heading = new(@"^#{1,6}[ \t]+(?<text>.+?)[ \t#]*$");
    static readonly Regex Fence = new(@"^[ \t]*(```|~~~)");
    static readonly Regex Bullet = new(@"^[ \t]*(?:[-*+]|\d+[.)])[ \t]+");
    static readonly Regex DueDate = new($@"(?:📅|📆|🗓){Vs}\s*(?<d>\d{{4}}-\d{{2}}-\d{{2}})|\[due::\s*(?<d>\d{{4}}-\d{{2}}-\d{{2}})\]");
    static readonly Regex ScheduledDate = new($@"(?:⏳|⌛){Vs}\s*(?<d>\d{{4}}-\d{{2}}-\d{{2}})|\[scheduled::\s*(?<d>\d{{4}}-\d{{2}}-\d{{2}})\]");
    static readonly Regex DoneDate = new($@"[ \t]*✅{Vs}[ \t]*\d{{4}}-\d{{2}}-\d{{2}}");
    static readonly Regex BlockId = new(@"[ \t]+\^[\w-]+[ \t]*$");
    static readonly Regex Signifiers = new(
        $@"(?:📅|📆|🗓|⏳|⌛|🛫|➕|✅|❌){Vs}\s*\d{{4}}-\d{{2}}-\d{{2}}|🔁{Vs}.*?(?=(?:{AnySignifier})|$)|(?:🆔|⛔){Vs}\s*\S+|" +
        $@"(?:🔺|⏫|🔼|🔽|⏬){Vs}|\[\w[\w ]*::[^\]]*\]|[ \t]+\^[\w-]+[ \t]*$");
    static readonly Regex WikiAlias = new(@"\[\[[^\]|]+\|([^\]]+)\]\]");
    static readonly Regex Wiki = new(@"\[\[([^\]]+)\]\]");
    static readonly Regex MdLink = new(@"!?\[([^\]]*)\]\([^)]*\)");
    static readonly Regex Emphasis = new(@"(\*\*|__|==|~~|`)(.+?)\1");
    static readonly (string Mark, string Name)[] Priorities =
        [("🔺", "Highest"), ("⏫", "High"), ("🔼", "Medium"), ("🔽", "Low"), ("⏬", "Lowest")];

    string? _note;          // the daily note the tasks were read from last, full path
    DateTime? _latestDay;   // its day, so the next look starts there
    // Which note each task id was read from, today's and before: a task ticked in yesterday's
    // note stays on the deck until midnight, and unticking it goes back to that note.
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _noteOf = new();

    public string Id => "obsidian";
    public string Name => "Obsidian";

    public Task<TaskRead> ReadAsync() => Task.Run(Read);  // file reads, off the timer

    TaskRead Read()
    {
        if (!Settings.ObsidianTasks || obsidian.Root is not { } root) return new TaskRead("setup", [], []);
        var note = Latest(root)?.Path;
        if (note != _note)
            Log.Write(note == null ? $"tasks: obsidian: no daily note in {Path.GetFileName(root)} in the last 92 days (today's would be {ObsidianSource.DailyNoteWhere(root)})"
                                   : $"tasks: obsidian: reading {Path.GetFileName(root)}/{Path.GetRelativePath(root, note).Replace('\\', '/')}");
        _note = note;
        var list = new TaskList(Id, List, ListName);
        if (note == null) return new TaskRead("ok", [list], []);
        var lines = ReadNote(note).Lines;
        if (_noteOf.Count > 5000) _noteOf.Clear();
        foreach (var f in TaskLines(lines)) _noteOf[f.Id] = note;
        return new TaskRead("ok", [list], Parse(note, lines));
    }

    /// <summary>The latest daily note, looking back only as far as the one found last time while it's still there.</summary>
    (string Path, DateTime Day)? Latest(string root)
    {
        var found = ObsidianSource.LatestDailyNote(root, _latestDay);
        _latestDay = found?.Day;
        return found;
    }

    /// <summary>One task line as read, with what it's known by.</summary>
    record Found(int Index, string Id, string Mark, string Text, int Indent);

    /// <summary>
    /// The note's task lines, outside code blocks. A task's id is its text (without the box and
    /// the ✅ date, so ticking it keeps it) and which copy of that text it is, counted from the top.
    /// </summary>
    static IEnumerable<Found> TaskLines(string[] lines)
    {
        var seen = new Dictionary<string, int>();
        var fenced = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (Fence.IsMatch(lines[i])) fenced = !fenced;
            if (fenced || TaskLine.Match(lines[i]) is not { Success: true } m) continue;
            var text = m.Groups["text"].Value;
            var key = DoneDate.Replace(text, "").Trim();
            var nth = seen.GetValueOrDefault(key);
            seen[key] = nth + 1;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{key}\n{nth}"));
            yield return new Found(i, Convert.ToHexString(hash, 0, 8).ToLowerInvariant(), m.Groups["mark"].Value, text, IndentOf(lines[i]));
        }
    }

    List<TaskItem> Parse(string note, string[] lines)
    {
        var found = TaskLines(lines).ToDictionary(f => f.Index);
        var tasks = new List<(Found Line, string Heading, string Parent, List<string> Notes)>();
        var stack = new List<(int Indent, string Id, bool Open)>();
        var heading = "";
        var fenced = false;
        (Found Line, List<string> Notes)? last = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (Fence.IsMatch(line)) fenced = !fenced;
            if (fenced) continue;
            if (Heading.Match(line) is { Success: true } h)
            {
                heading = Clean(h.Groups["text"].Value);
                stack.Clear();
                last = null;
                continue;
            }
            if (line.Trim().Length == 0) continue;
            var indent = IndentOf(line);
            if (found.TryGetValue(i, out var f))
            {
                while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
                var open = f.Mark is " " or "/";
                var parent = stack.Count > 0 && stack[^1].Open ? stack[^1].Id : "";
                stack.Add((indent, f.Id, open));
                var notes = new List<string>();
                last = (f, notes);
                if (open) tasks.Add((f, heading, parent, notes));
                continue;
            }
            // A line under a task, further in: its notes. Anything else ends them.
            if (last is { } l && indent > l.Line.Indent)
            {
                l.Notes.Add(Clean(Bullet.Replace(line, "")));
                continue;
            }
            last = null;
            if (indent == 0 && !Bullet.IsMatch(line)) stack.Clear();
        }
        var url = "obsidian://open?path=" + Uri.EscapeDataString(note);
        return tasks.Select(t =>
        {
            var due = DueDate.Match(t.Line.Text) is { Success: true } d ? d.Groups["d"].Value
                    : ScheduledDate.Match(t.Line.Text) is { Success: true } s ? s.Groups["d"].Value : "";
            var priority = Priorities.FirstOrDefault(p => t.Line.Text.Contains(p.Mark, StringComparison.Ordinal)).Name ?? "";
            var notes = string.Join("\n", t.Notes.Where(n => n.Length > 0));
            var first = t.Notes.FirstOrDefault(n => n.Length > 0) ?? "";
            return new TaskItem(Id, List, t.Line.Id, Clean(t.Line.Text), notes, due, false, t.Parent)
            {
                Url = url,
                Project = t.Heading,
                Priority = priority,
                Status = t.Line.Mark == "/" ? "In progress" : "",
                Summary = string.Join("  ·  ", new[] { t.Heading, priority, first }.Where(p => p.Length > 0)),
            };
        }).Where(t => t.Title.Length > 0).ToList();
    }

    /// <summary>A task's text as a title: no Tasks plugin dates, priorities or ids, no link or emphasis marks.</summary>
    static string Clean(string text)
    {
        text = Signifiers.Replace(text, " ");
        text = WikiAlias.Replace(text, "$1");
        text = Wiki.Replace(text, m => m.Groups[1].Value.Split('#')[0].Split('/').Last());
        text = MdLink.Replace(text, "$1");
        text = Emphasis.Replace(text, "$2");
        return string.Join(" ", text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
    }

    static int IndentOf(string line)
    {
        var n = 0;
        foreach (var c in line)
        {
            if (c == ' ') n++;
            else if (c == '\t') n += 4;
            else break;
        }
        return n;
    }

    /// <summary>A note's lines (the last one "" when it ends in a newline), its newline and whether it starts with a BOM.</summary>
    static (string[] Lines, string Newline, bool Bom) ReadNote(string file)
    {
        var bytes = File.ReadAllBytes(file);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        return (text.Replace("\r\n", "\n").Split('\n'), text.Contains("\r\n") ? "\r\n" : "\n", bom);
    }

    /// <summary>
    /// Changes a note that Obsidian may have open. Obsidian saves a moment after each keystroke,
    /// so this waits until the note has been still for <see cref="Quiet"/>, then reads, changes
    /// and writes it straight away, and only if nothing wrote it in between (else it tries
    /// again), so typing in Obsidian and a tap on the deck don't overwrite each other.
    /// </summary>
    static async Task ChangeNoteAsync(string file, Func<string[], string[]> change)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var still = DateTime.UtcNow - File.GetLastWriteTimeUtc(file);
            if (still < Quiet)
            {
                await Task.Delay(Quiet - still);
                continue;
            }
            var stamp = File.GetLastWriteTimeUtc(file);
            var (lines, newline, bom) = ReadNote(file);
            var text = string.Join(newline, change(lines));
            if (File.GetLastWriteTimeUtc(file) != stamp) continue;
            File.WriteAllText(file, text, new UTF8Encoding(bom));
            return;
        }
        throw new IOException("the note kept changing");
    }

    static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2.5);

    static bool TasksPluginOn(string root) => ObsidianSource.PluginOn(root, "obsidian-tasks-plugin");

    /// <summary>The note a task was read from: yesterday's for one ticked before today's note came, so it can still be unticked.</summary>
    string NoteOf(string id) => _noteOf.TryGetValue(id, out var file) ? file : _note ?? throw new InvalidOperationException("no daily note");

    public Task SetDoneAsync(string list, string id, bool done) => ChangeNoteAsync(NoteOf(id), lines =>
    {
        var f = TaskLines(lines).FirstOrDefault(t => t.Id == id) ?? throw new InvalidOperationException("that task isn't in the note any more");
        var m = TaskLine.Match(lines[f.Index]);
        var rest = DoneDate.Replace(m.Groups["rest"].Value, "");
        if (done && obsidian.Root is { } root && TasksPluginOn(root))
        {
            // As the Tasks plugin writes it: at the end, before a block id.
            var stamp = $" ✅ {DateTime.Today:yyyy-MM-dd}";
            rest = BlockId.Match(rest) is { Success: true } b ? rest[..b.Index] + stamp + b.Value : rest.TrimEnd() + stamp;
        }
        lines[f.Index] = m.Groups["lead"].Value + (done ? "x" : " ") + rest;
        return lines;
    });

    public async Task<string> AddAsync(string list, string title, string due)
    {
        var root = obsidian.Root ?? throw new InvalidOperationException("no Obsidian vault");
        var file = Latest(root)?.Path ?? ObsidianSource.CreateDailyNote(root, DateTime.Today);
        var text = string.Join(" ", title.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        var line = $"- [ ] {text}{(due.Length == 10 ? $" 📅 {due}" : "")}";
        await ChangeNoteAsync(file, lines =>
        {
            // After the last line with something on it; the note's own ending kept.
            var end = lines.Length;
            while (end > 0 && lines[end - 1].Trim().Length == 0) end--;
            var kept = lines.Skip(end).ToList();
            return [.. lines.Take(end), line, .. kept.Count > 0 ? kept : [""]];
        });
        _note = file;
        return ListName;
    }

    /// <summary>Opens the task's note in Obsidian.</summary>
    public bool Open(TaskItem task)
    {
        if ((_noteOf.TryGetValue(task.Id, out var file) ? file : _note) is not { } note) return false;
        obsidian.OpenFile(note);
        return true;
    }
}
