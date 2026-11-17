using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// The calendar sources there are now, from the profile's accounts: each Google account with its
/// calendar on, each Microsoft 365 account, each iCalendar link. The same object stays for an
/// account while the profile has it (it keeps its sign-in and its last feed).
/// </summary>
sealed class CalendarSources(GoogleSource google)
{
    readonly object _gate = new();
    readonly Dictionary<string, ICalendarProvider> _made = [];

    public IReadOnlyList<ICalendarProvider> Now()
    {
        var googles = google.Accounts.Where(a => a.Calendar).ToList();
        lock (_gate)
        {
            var list = new List<ICalendarProvider>();
            foreach (var account in googles)
            {
                if (!(_made.GetValueOrDefault(account.Id) is GoogleCalendar g && g.Account == account)) _made[account.Id] = new GoogleCalendar(account);
                list.Add(_made[account.Id]);
            }
            foreach (var (id, config) in Settings.Accounts("microsoft"))
            {
                if (_made.GetValueOrDefault(id) is MicrosoftCalendar m) m.Config = config;
                else _made[id] = new MicrosoftCalendar(id, config);
                list.Add(_made[id]);
            }
            foreach (var (id, config) in Settings.Accounts("ics"))
            {
                if (_made.GetValueOrDefault(id) is IcsCalendar i) i.Config = config;
                else _made[id] = new IcsCalendar(id, config);
                list.Add(_made[id]);
            }
            return list;
        }
    }
}

/// <summary>
/// The tray's Calendars menu: each calendar source with its calendars to tick for the deck, its
/// sign-in, link or removal; the profile's filters (declined, all-day, free, by title); and
/// adding a Google account, a Microsoft 365 calendar or a calendar link. Runs on the UI thread.
/// </summary>
sealed class CalendarSetup(CalendarSource calendar, CalendarSources sources, GoogleSetup googleSetup)
{
    public ToolStripMenuItem Menu()
    {
        var menu = new ToolStripMenuItem("Calendars");
        menu.DropDownItems.Add(new ToolStripMenuItem());  // filled on opening; a submenu needs an item to open
        menu.DropDownOpening += (_, _) => Fill(menu);
        return menu;
    }

    void Fill(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        foreach (var source in sources.Now()) menu.DropDownItems.Add(SourceItem(source));
        if (menu.DropDownItems.Count == 0)
            menu.DropDownItems.Add(new ToolStripMenuItem($"No calendar in the {Settings.Profile} profile") { Enabled = false });

        menu.DropDownItems.Add(new ToolStripSeparator());
        var filter = Settings.Source("calendarFilter");
        menu.DropDownItems.Add(Toggle("Hide events I declined", CalendarFilter.HideDeclined(filter), "hideDeclined", true));
        menu.DropDownItems.Add(Toggle("Hide all-day events", CalendarFilter.HideAllDay(filter), "hideAllDay", false));
        menu.DropDownItems.Add(Toggle("Hide events marked free", CalendarFilter.HideFree(filter), "hideFree", false));
        var titles = CalendarFilter.HideTitles(filter);
        menu.DropDownItems.Add(new ToolStripMenuItem(titles.Count > 0 ? $"Hide events titled… ({string.Join(", ", titles)})" : "Hide events titled…",
            null, (_, _) => EditTitles()) { Checked = titles.Count > 0 });

        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("Add a Google account…", null, (_, _) => googleSetup.Add()));
        menu.DropDownItems.Add(new ToolStripMenuItem("Add a Microsoft 365 or Outlook calendar…", null, (_, _) => AddMicrosoft()));
        menu.DropDownItems.Add(new ToolStripMenuItem("Add a calendar link (iCal)…", null, (_, _) => AddLink()));
    }

    /// <summary>A source: its state, then its calendars to tick, then what can be done with it.</summary>
    ToolStripMenuItem SourceItem(ICalendarProvider source)
    {
        var (status, calendars) = calendar.Of(source.Id);
        var who = source switch
        {
            GoogleCalendar g => g.Account.Email,
            MicrosoftCalendar m => m.Email,
            _ => "",
        };
        var state = status switch
        {
            "ok" => who.Length > 0 ? who : calendars.Count == 1 ? "1 calendar" : $"{calendars.Count} calendars",
            "signin" => "not signed in",
            "setup" => source switch
            {
                GoogleCalendar => "no OAuth client yet",
                MicrosoftCalendar => "no app registration",
                _ => "no link on this PC",
            },
            "error" => "can't be read now",
            _ => "not read yet",
        };
        var item = new ToolStripMenuItem($"{source.Name}  ·  {state}") { Checked = status == "ok" };
        if (calendars.Count > 1 || source is not IcsCalendar)
        {
            foreach (var c in calendars)
            {
                var pick = new ToolStripMenuItem(c.Name) { Checked = c.Shown, CheckOnClick = false };
                pick.Click += (_, _) => Pick(source, c, !c.Shown);
                item.DropDownItems.Add(pick);
            }
            if (calendars.Count > 0) item.DropDownItems.Add(new ToolStripSeparator());
        }
        switch (source)
        {
            case GoogleCalendar g:
                item.DropDownItems.Add(new ToolStripMenuItem(g.Account.HasSignIn ? "Sign in again…" : "Sign in…", null, (_, _) => googleSetup.Connect(g.Account)));
                break;
            case MicrosoftCalendar m:
                item.DropDownItems.Add(new ToolStripMenuItem(m.HasSignIn ? "Sign in again…" : "Sign in…", null, (_, _) => SignIn(m)));
                if (m.HasSignIn) item.DropDownItems.Add(new ToolStripMenuItem("Sign out on this PC", null, (_, _) =>
                {
                    m.SignOut();
                    Log.Write($"calendar: {m.Id}: signed out on this PC");
                    _ = calendar.RefreshAsync();
                }));
                item.DropDownItems.Add(Remove(m.Id, m.Name, () => m.SignOut()));
                break;
            case IcsCalendar i:
                item.DropDownItems.Add(new ToolStripMenuItem("Change the link…", null, (_, _) => EditLink(i.Id, i.Label)));
                item.DropDownItems.Add(Remove(i.Id, i.Name, () => Secrets.Delete(i.UrlName)));
                break;
        }
        return item;
    }

    void Pick(ICalendarProvider source, CalendarInfo c, bool show)
    {
        var type = source switch { GoogleCalendar => "google", MicrosoftCalendar => "microsoft", _ => "ics" };
        var config = Settings.Accounts(type).FirstOrDefault(a => a.Id == source.Id).Config ?? new JsonObject();
        var updated = CalendarChoice.With(config, c.Id, c.Name, c.Default, show);
        if (!updated.ContainsKey("type")) updated["type"] = type;
        Settings.SetAccount(source.Id, updated);  // the accounts changed: TrayApp reads the calendar again
        Log.Write($"calendar: {source.Id}: a calendar {(show ? "on" : "off")} the deck");
    }

    ToolStripMenuItem Toggle(string text, bool on, string key, bool byDefault)
    {
        var item = new ToolStripMenuItem(text) { Checked = on };
        item.Click += (_, _) => SetFilter(key, on == byDefault ? JsonValue.Create(!on) : null);
        return item;
    }

    /// <summary>One of the profile's calendarFilter settings (null: back to its default), and the calendar read again.</summary>
    void SetFilter(string key, JsonNode? value)
    {
        var filter = Settings.Source("calendarFilter")?.DeepClone() as JsonObject ?? [];
        if (value == null) filter.Remove(key);
        else filter[key] = value;
        Settings.Set("calendarFilter", filter.Count > 0 ? filter : null);
        _ = calendar.RefreshAsync();
    }

    void EditTitles()
    {
        var values = InputDialog.Ask(
            "flex-screen: hide events by title",
            "Events whose title has any of these words or phrases stay off the deck, from every calendar (Lunch, Focus time, " +
            "Commute). Separate them with commas; any case matches. A /regular expression/ between slashes works too.\n\n" +
            "Leave it empty to hide none.",
            "https://github.com/AsafDov/flex-screen#settings",
            [new("Titles", string.Join(", ", CalendarFilter.HideTitles(Settings.Source("calendarFilter"))), Hint: "Lunch, Focus time")]);
        if (values == null) return;
        var titles = values[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        SetFilter("hideTitles", titles.Length > 0 ? new JsonArray(titles.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()) : null);
    }

    ToolStripMenuItem Remove(string id, string name, Action forget) => new($"Remove from the {Settings.Profile} profile…", null, (_, _) =>
    {
        if (MessageBox.Show($"Take {name} out of the {Settings.Profile} profile, and forget its sign-in or link on this PC?",
                "flex-screen", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Settings.RemoveAccount(id);
        forget();
        Log.Write($"calendar: {id}: removed from the {Settings.Profile} profile");
    });

    async void SignIn(MicrosoftCalendar m)
    {
        try
        {
            if (await m.SignInAsync())
            {
                Log.Write($"calendar: {m.Id}: connected");
                await calendar.RefreshAsync();
            }
            else
            {
                MessageBox.Show($"{m.Name} didn't connect. The companion's log (tray icon, Open log) says why.", "flex-screen",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception e)  // async void: anything left uncaught would end the companion
        {
            Log.Write($"calendar: {m.Id}: sign-in didn't finish: {e.Message}");
        }
    }

    void AddMicrosoft()
    {
        var todo = Settings.Source("microsoftTodo");
        var values = InputDialog.Ask(
            "flex-screen: add a Microsoft 365 or Outlook calendar",
            "flex-screen reads Outlook calendars through Microsoft Graph, with your own app registration. In Microsoft Entra " +
            "(App registrations), register an app (or use Microsoft To Do's), add the platform \"Mobile and desktop applications\" " +
            "with the redirect URI http://localhost, and under API permissions add Microsoft Graph's delegated Calendars.Read and " +
            "User.Read. Paste its Application (client) ID here; Microsoft's sign-in follows.\n\n" +
            "A work account whose organization doesn't let you consent to apps can still publish its calendar: Outlook on the " +
            "web, Settings, Calendar, Shared calendars, Publish a calendar; then Add a calendar link with the ICS address.",
            "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade",
            [new("Name", "", Hint: "Work"),
             new("Application (client) ID", (string?)todo?["clientId"] ?? ""),
             new("Tenant", (string?)todo?["tenant"] ?? "common", Hint: "common; organizations for work accounts only; or your organization's")]);
        if (values == null) return;
        var (name, clientId, tenant) = (values[0].Trim(), values[1].Trim(), values[2].Trim());
        if (clientId.Length == 0)
        {
            MessageBox.Show("It needs the app registration's Application (client) ID.", "flex-screen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var id = NewId("microsoft", name);
        var config = new JsonObject { ["type"] = "microsoft", ["label"] = name, ["clientId"] = clientId };
        if (tenant.Length > 0 && tenant != "common") config["tenant"] = tenant;
        Settings.SetAccount(id, config);
        Log.Write($"calendar: {id}: added to the {Settings.Profile} profile");
        if (sources.Now().FirstOrDefault(s => s.Id == id) is MicrosoftCalendar m) SignIn(m);
    }

    void AddLink() => EditLink(null, "");

    /// <summary>A new link (id null), or another link for one there is. The link is kept on this PC, as a sign-in is.</summary>
    async void EditLink(string? id, string label)
    {
        var values = InputDialog.Ask(
            id == null ? "flex-screen: add a calendar link" : $"flex-screen: {label}",
            "A calendar's iCalendar address (it ends in .ics, or starts with webcal://): a published Outlook calendar (Outlook on " +
            "the web, Settings, Calendar, Shared calendars, Publish a calendar: the ICS link), Google Calendar's secret address in " +
            "iCal format (the calendar's settings, Integrate calendar), an iCloud calendar shared as public, a school's or a team's.\n\n" +
            "The address works as a password for that calendar, so it's kept on this PC with the sign-ins, not in the profile: " +
            "another PC asks for it once.",
            "https://support.microsoft.com/office/share-your-calendar-in-outlook-on-the-web-7ecef8ae-139c-40d9-bae2-a23977ee58d5",
            id == null
                ? [new("Name", "", Hint: "School"), new("Link", "", Hint: "https://…/calendar.ics")]
                : [new("Link", "", Hint: "the new address")]);
        if (values == null) return;
        var (name, link) = id == null ? (values[0].Trim(), values[1].Trim()) : (label, values[0].Trim());
        if (IcsCalendar.Normalize(link) == null)
        {
            MessageBox.Show("That isn't a web address (https://… or webcal://…).", "flex-screen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // Read it before keeping it, so a wrong link is said now rather than as "can't be read".
        var test = new IcsCalendar(id ?? "ics-new", new JsonObject { ["url"] = link });
        try
        {
            var read = await test.ReadAsync(new DateTimeOffset(DateTime.Today), new DateTimeOffset(DateTime.Today.AddDays(2)));
            id ??= NewId("ics", name.Length > 0 ? name : "calendar");
            if (!Settings.Accounts("ics").Any(a => a.Id == id))
            {
                var config = new JsonObject { ["type"] = "ics" };
                if (name.Length > 0) config["label"] = name;
                Settings.SetAccount(id, config);
            }
            Secrets.Save($"{id}-url", link);
            Log.Write($"calendar: {id}: link kept ({read.Events.Count} events today and tomorrow)");
            MessageBox.Show($"{(name.Length > 0 ? name : "The calendar")} has {read.Events.Count} event{(read.Events.Count == 1 ? "" : "s")} today and tomorrow.",
                "flex-screen", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await calendar.RefreshAsync();
        }
        catch (Exception e)  // async void, and any reply the link gives
        {
            Log.Write($"calendar: link not added: {e.Message}");
            MessageBox.Show($"That link didn't give a calendar: {e.Message}", "flex-screen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>"ics-school" for School; "ics-school-2" when that's taken.</summary>
    static string NewId(string type, string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        if (slug.Length == 0) slug = "calendar";
        if (slug.Length > 24) slug = slug[..24].TrimEnd('-');
        var taken = Settings.Accounts(type).Select(a => a.Id).ToHashSet();
        var id = $"{type}-{slug}";
        for (var n = 2; taken.Contains(id); n++) id = $"{type}-{slug}-{n}";
        return id;
    }
}
