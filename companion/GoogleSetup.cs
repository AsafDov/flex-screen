using System.Net;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// The tray's Google accounts menu (also Task sources › Google Tasks): each of the profile's
/// Google accounts, to sign in, sign out on this PC, take its calendar or tasks off the deck,
/// or remove; and Add a Google account. Runs on the UI thread.
/// </summary>
sealed class GoogleSetup(GoogleSource google)
{
    const string ConsoleUrl = "https://console.cloud.google.com/apis/credentials";

    /// <summary>A submenu of the accounts, filled each time it opens.</summary>
    public ToolStripMenuItem Menu(string title)
    {
        var menu = new ToolStripMenuItem(title);
        menu.DropDownItems.Add(new ToolStripMenuItem());  // filled on opening; a submenu needs an item to open
        menu.DropDownOpening += (_, _) => Fill(menu);
        return menu;
    }

    /// <summary>Whether any of the profile's Google accounts has task lists on the deck now.</summary>
    public bool HasTasks(IReadOnlySet<string> sources) => google.Accounts.Any(a => sources.Contains(a.Id));

    void Fill(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        foreach (var account in google.Accounts)
        {
            var signedIn = account.HasClient && account.HasSignIn;
            var state = !account.HasClient ? "no OAuth client yet" : !signedIn ? "not signed in" : account.Email.Length > 0 ? account.Email : "signed in";
            var item = new ToolStripMenuItem($"{account.Name}  ·  {state}") { Checked = signedIn };
            item.DropDownItems.Add(new ToolStripMenuItem(signedIn ? "Sign in again…" : "Sign in…", null, (_, _) => Connect(account)));
            item.DropDownItems.Add(new ToolStripSeparator());
            var calendar = new ToolStripMenuItem("Its calendar on the deck") { Checked = account.Calendar };
            calendar.Click += (_, _) => SetFlag(account, "calendar", !account.Calendar);
            var tasks = new ToolStripMenuItem("Its tasks on the deck") { Checked = account.Tasks };
            tasks.Click += (_, _) => SetFlag(account, "tasks", !account.Tasks);
            item.DropDownItems.AddRange([calendar, tasks, new ToolStripSeparator()]);
            if (account.HasSignIn) item.DropDownItems.Add(new ToolStripMenuItem("Sign out on this PC", null, (_, _) => google.SignOut(account)));
            item.DropDownItems.Add(new ToolStripMenuItem($"Remove from the {Settings.Profile} profile…", null, (_, _) => Remove(account)));
            menu.DropDownItems.Add(item);
        }
        if (menu.DropDownItems.Count == 0)
            menu.DropDownItems.Add(new ToolStripMenuItem($"No Google account in the {Settings.Profile} profile") { Enabled = false });
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("Add a Google account…", null, (_, _) => Add()));
    }

    /// <summary>Starts the account's sign-in, or explains where the OAuth client file goes.</summary>
    public void Connect(GoogleAccount account)
    {
        if (!account.HasClient)
        {
            MessageBox.Show(
                "flex-screen reads Google Calendar and Tasks with your own Google OAuth client.\n\n" +
                "In Google Cloud Console (APIs & Services), enable the Google Calendar API and the Google Tasks API, " +
                "create an OAuth client of type \"Desktop app\", download its JSON and save it as:\n\n" +
                account.ClientPath + "\n\nThen choose Sign in again.",
                $"flex-screen: connect {account.Name}", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await google.SignInAsync(account);
            }
            catch (Exception e) when (e is TimeoutException or HttpListenerException or InvalidOperationException or HttpRequestException)
            {
                Log.Write($"google: {account.Id}: sign-in didn't finish: {e.Message}");
            }
        });
    }

    public void Add()
    {
        var values = InputDialog.Ask(
            "flex-screen: add a Google account",
            "Another Google account beside the ones here (a work one beside your own, say): its calendar joins the deck's, and its " +
            "task lists the Tasks card. Google's account picker comes up next, to choose which one.\n\n" +
            "It can sign in with the OAuth client the others use. A work domain that only allows its own apps needs a client from " +
            "its own Google Cloud project: save its JSON in " + AppPaths.Data + " and give the file's name here. While the OAuth " +
            "app is in Testing, add the account as a test user first (Google Auth Platform, Audience).",
            ConsoleUrl,
            [new("Name", "", Hint: "Work"),
             new("OAuth client file", "", Hint: "empty: google-client.json, shared")]);
        if (values == null) return;
        var (name, client) = (values[0].Trim(), values[1].Trim());
        if (name.Length == 0)
        {
            MessageBox.Show("Give the account a name, the one the deck and the tray show (Work, Personal).", "flex-screen",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var id = NewId(name);
        var config = new JsonObject { ["type"] = "google", ["label"] = name };
        if (client.Length > 0) config["client"] = client;
        Settings.SetAccount(id, config);
        Log.Write($"google: {id}: added to the {Settings.Profile} profile");
        if (google.Account(id) is { } account) Connect(account);
    }

    /// <summary>"google-work" for Work; "google-work-2" when that's taken.</summary>
    string NewId(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        if (slug.Length == 0) slug = "account";
        if (slug.Length > 24) slug = slug[..24].TrimEnd('-');
        var taken = google.Accounts.Select(a => a.Id).ToHashSet();
        var id = $"google-{slug}";
        for (var n = 2; taken.Contains(id); n++) id = $"google-{slug}-{n}";
        return id;
    }

    static void SetFlag(GoogleAccount account, string key, bool on)
    {
        if (Settings.Accounts("google").FirstOrDefault(a => a.Id == account.Id).Config?.DeepClone() is not JsonObject config) return;
        if (on) config.Remove(key);  // on unless false
        else config[key] = false;
        if (!config.ContainsKey("type")) config["type"] = "google";
        Settings.SetAccount(account.Id, config);
    }

    void Remove(GoogleAccount account)
    {
        if (MessageBox.Show($"Take {account.Name} out of the {Settings.Profile} profile, and forget its sign-in on this PC?",
                "flex-screen", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Settings.RemoveAccount(account.Id);
        google.SignOut(account);
        Log.Write($"google: {account.Id}: removed from the {Settings.Profile} profile");
    }
}
