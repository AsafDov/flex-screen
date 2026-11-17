using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// One Google account's Google Tasks as a task source: every task list's open tasks
/// (subtasks too), ticking off and adding. Signs in through the <see cref="GoogleAccount"/>,
/// whose consent already asks for Tasks (read and write). Its id is the account's ("google",
/// "google-work"), so the first account's lists keep the ids they had before accounts.
/// </summary>
sealed class GoogleTasks(GoogleAccount google) : ITaskProvider
{
    const string Api = "https://tasks.googleapis.com/tasks/v1";
    const int MaxPages = 5;  // 100 tasks a page, per list

    public GoogleAccount Account => google;
    public string Id => google.Id;
    public string Name => google.Label.Length > 0 ? $"Google Tasks ({google.Label})" : "Google Tasks";

    // The lists as last read, for the default list and the toast's list name.
    List<TaskList> _lists = [];

    public async Task<TaskRead> ReadAsync()
    {
        var status = await google.AuthStatusAsync();
        if (status != "ok") return new TaskRead(status, [], []);
        var json = await google.SendAsync(HttpMethod.Get, $"{Api}/users/@me/lists?maxResults=100");
        var lists = (json?["items"] as JsonArray ?? [])
            .Select(l => new TaskList(Id, (string?)l?["id"] ?? "", (string?)l?["title"] ?? ""))
            .Where(l => l.Id.Length > 0)
            .ToList();
        var reads = await Task.WhenAll(lists.Select(l => ListTasksAsync(l.Id)));
        _lists = lists;
        return new TaskRead("ok", lists, reads.SelectMany(t => t).ToList());
    }

    async Task<List<TaskItem>> ListTasksAsync(string list)
    {
        var tasks = new List<TaskItem>();
        string? page = null;
        for (var i = 0; i < MaxPages; i++)
        {
            var url = $"{Api}/lists/{Uri.EscapeDataString(list)}/tasks?maxResults=100&showCompleted=false" +
                      (page != null ? $"&pageToken={Uri.EscapeDataString(page)}" : "");
            var json = await google.SendAsync(HttpMethod.Get, url);
            foreach (var t in json?["items"] as JsonArray ?? [])
            {
                var title = ((string?)t?["title"] ?? "").Trim();
                if (title.Length == 0 || (bool?)t?["deleted"] == true) continue;  // Google keeps blank ones a row was started for
                var due = (string?)t?["due"] is { Length: >= 10 } d ? d[..10] : "";
                tasks.Add(new TaskItem(Id, list, (string?)t?["id"] ?? "", title, (string?)t?["notes"] ?? "", due,
                                       (string?)t?["status"] == "completed", (string?)t?["parent"] ?? "")
                {
                    Url = (string?)t?["webViewLink"] ?? "",  // the task in Google Tasks on the web
                });
            }
            page = (string?)json?["nextPageToken"];
            if (page == null) break;
        }
        return tasks;
    }

    public async Task SetDoneAsync(string list, string id, bool done)
    {
        var body = done ? new JsonObject { ["status"] = "completed" } : new JsonObject { ["status"] = "needsAction", ["completed"] = null };
        await google.SendAsync(HttpMethod.Patch, $"{Api}/lists/{Uri.EscapeDataString(list)}/tasks/{Uri.EscapeDataString(id)}", body);
    }

    public async Task<string> AddAsync(string list, string title, string due)
    {
        // "@default" is Google's own name for the first list (My Tasks).
        if (list.Length == 0) list = _lists.FirstOrDefault()?.Id ?? "@default";
        var body = new JsonObject { ["title"] = title };
        if (due.Length == 10) body["due"] = due + "T00:00:00.000Z";  // Google Tasks keeps the date only
        await google.SendAsync(HttpMethod.Post, $"{Api}/lists/{Uri.EscapeDataString(list)}/tasks", body);
        return _lists.FirstOrDefault(l => l.Id == list)?.Name ?? "";
    }
}
