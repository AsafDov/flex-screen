using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// The tray's Task sources menu: connecting Google Tasks, Microsoft To Do, Jira and GitHub,
/// and the Obsidian daily note (on or off, and which vault). Site addresses and choices go in
/// settings.json; tokens only in <see cref="Secrets"/>. Runs on the UI thread.
/// </summary>
sealed class TaskSourceSetup(TaskSource tasks, MicrosoftTodo todo, ObsidianSource obsidian, Action connectGoogle)
{
    public ToolStripMenuItem Menu()
    {
        var menu = new ToolStripMenuItem("Task sources");
        var google = new ToolStripMenuItem("Google Tasks…", null, (_, _) => connectGoogle());
        var microsoft = new ToolStripMenuItem("Microsoft To Do…", null, (_, _) => ConnectMicrosoft());
        var jira = new ToolStripMenuItem("Jira…", null, (_, _) => ConnectJira());
        var github = new ToolStripMenuItem("GitHub…", null, (_, _) => ConnectGitHub());
        var daily = ObsidianMenu();
        menu.DropDownItems.AddRange([google, microsoft, jira, github, daily]);
        menu.DropDownOpening += (_, _) =>
        {
            // A tick on each source that's set up on this PC.
            var connected = tasks.Current.Lists.Select(l => l.Source).ToHashSet();
            google.Checked = connected.Contains("google");
            microsoft.Checked = connected.Contains("mstodo");
            jira.Checked = connected.Contains("jira");
            github.Checked = connected.Contains("github");
            daily.Checked = Settings.ObsidianTasks;
        };
        return menu;
    }

    /// <summary>
    /// Obsidian daily note ›  its tasks on or off; then the vaults Obsidian knows on this PC,
    /// the one it has open, or another folder. A PC with several vaults (home and work) picks
    /// one here; it's settings.json's "obsidianVault".
    /// </summary>
    ToolStripMenuItem ObsidianMenu()
    {
        var menu = new ToolStripMenuItem("Obsidian daily note");
        menu.DropDownItems.Add(new ToolStripMenuItem());  // filled on opening; a submenu needs an item to open
        menu.DropDownOpening += (_, _) =>
        {
            menu.DropDownItems.Clear();
            var show = new ToolStripMenuItem("Its tasks on the deck") { Checked = Settings.ObsidianTasks };
            show.Click += (_, _) =>
            {
                Settings.Set("obsidianTasks", !Settings.ObsidianTasks);
                _ = tasks.RefreshAsync();
            };
            menu.DropDownItems.Add(show);
            menu.DropDownItems.Add(new ToolStripSeparator());
            var chosen = Settings.ObsidianVault is { } c ? Path.GetFullPath(c).TrimEnd('\\') : null;
            var vaults = ObsidianSource.KnownVaults().ToList();
            if (chosen != null && !vaults.Contains(chosen, StringComparer.OrdinalIgnoreCase)) vaults.Insert(0, chosen);
            foreach (var vault in vaults)
            {
                // A vault's name; with its parent folder too when two have the same name.
                var name = Path.GetFileName(vault);
                if (vaults.Count(v => string.Equals(Path.GetFileName(v), name, StringComparison.OrdinalIgnoreCase)) > 1)
                    name += $"  ({Path.GetFileName(Path.GetDirectoryName(vault))})";
                var item = new ToolStripMenuItem($"Vault: {name}")
                {
                    Checked = string.Equals(vault, obsidian.Root, StringComparison.OrdinalIgnoreCase),
                    ToolTipText = vault,
                };
                item.Click += (_, _) => UseVault(vault);
                menu.DropDownItems.Add(item);
            }
            var auto = new ToolStripMenuItem("The vault Obsidian has open") { Checked = chosen == null };
            auto.Click += (_, _) => UseVault(null);
            menu.DropDownItems.Add(auto);
            menu.DropDownItems.Add(new ToolStripMenuItem("Another vault folder…", null, (_, _) => PickVaultFolder()));
        };
        return menu;
    }

    /// <summary>Keeps <paramref name="vault"/> (null: the one Obsidian has open) and moves the deck to it now.</summary>
    void UseVault(string? vault)
    {
        Settings.Set("obsidianVault", vault);
        Log.Write(vault == null ? "obsidian: vault: the one Obsidian has open" : $"obsidian: vault chosen: {vault}");
        _ = Task.Run(async () =>
        {
            obsidian.Refresh();
            await tasks.RefreshAsync();
        });
    }

    void PickVaultFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "The Obsidian vault whose daily notes the deck shows",
            UseDescriptionForTitle = true,
            InitialDirectory = obsidian.Root ?? "",
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        if (!Directory.Exists(Path.Combine(dialog.SelectedPath, ".obsidian")))
        {
            MessageBox.Show("That folder isn't an Obsidian vault: it has no .obsidian folder. Pick the vault's own folder, the one Obsidian opens.",
                "flex-screen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        UseVault(Path.GetFullPath(dialog.SelectedPath).TrimEnd('\\'));
    }

    async void ConnectMicrosoft()
    {
        var current = Settings.Source("microsoftTodo");
        var values = InputDialog.Ask(
            "flex-screen: Microsoft To Do",
            "flex-screen reads Microsoft To Do with your own app registration. In Microsoft Entra (App registrations), " +
            "register an app for \"Accounts in any organizational directory and personal Microsoft accounts\", add the " +
            "platform \"Mobile and desktop applications\" with the redirect URI http://localhost, and under API permissions " +
            "add Microsoft Graph's delegated Tasks.ReadWrite. Paste its Application (client) ID here; a browser sign-in follows.\n\n" +
            "Leave it empty to disconnect Microsoft To Do.",
            "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade",
            [new("Application (client) ID", (string?)current?["clientId"] ?? ""),
             new("Tenant", (string?)current?["tenant"] ?? "common", Hint: "common; consumers for personal accounts only; or your organization's")]);
        if (values == null) return;
        var (clientId, tenant) = (values[0].Trim(), values[1].Trim());
        if (clientId.Length == 0)
        {
            Settings.Set("microsoftTodo", null);
            Secrets.Delete(MicrosoftTodo.TokenName);
            Log.Write("tasks: mstodo: disconnected");
            await tasks.RefreshAsync();
            return;
        }
        Settings.Set("microsoftTodo", new JsonObject { ["clientId"] = clientId, ["tenant"] = tenant.Length > 0 ? tenant : "common" });
        try
        {
            await todo.SignInAsync();
        }
        catch (Exception e)  // async void: anything left uncaught would end the companion
        {
            Log.Write($"tasks: mstodo: sign-in didn't finish: {e.Message}");
            MessageBox.Show($"Microsoft To Do didn't connect: {e.Message}", "flex-screen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    async void ConnectJira()
    {
        var current = Settings.Source("jira");
        var has = Secrets.Load(JiraIssues.TokenName) != null;
        var values = InputDialog.Ask(
            "flex-screen: Jira",
            "The deck shows the Jira issues assigned to you that aren't done, and opens one in the browser. It doesn't change them.\n\n" +
            "Jira Cloud: your site, your Atlassian email and an API token (Atlassian account settings, Security, API tokens). " +
            "Jira Server or Data Center: your site and a personal access token (your Jira profile), with no email.\n\n" +
            "Leave the site empty to disconnect Jira. The query (JQL) can be changed in settings.json.",
            "https://id.atlassian.com/manage-profile/security/api-tokens",
            [new("Site", (string?)current?["url"] ?? "", Hint: "https://yourcompany.atlassian.net"),
             new("Email", (string?)current?["email"] ?? "", Hint: "Jira Cloud only"),
             new("Token", "", Secret: true, Hint: has ? "kept; type a new one to change it" : "")]);
        if (values == null) return;
        var (url, email, token) = (values[0].Trim(), values[1].Trim(), values[2].Trim());
        if (url.Length == 0)
        {
            Settings.Set("jira", null);
            Secrets.Delete(JiraIssues.TokenName);
            Log.Write("tasks: jira: disconnected");
            await tasks.RefreshAsync();
            return;
        }
        if (token.Length == 0) token = Secrets.Load(JiraIssues.TokenName) ?? "";
        try
        {
            var me = await JiraIssues.WhoAmIAsync(url, email, token);
            var settings = current?.DeepClone() as JsonObject ?? [];
            settings["url"] = url;
            settings["email"] = email;
            Settings.Set("jira", settings);
            Secrets.Save(JiraIssues.TokenName, token);
            Log.Write("tasks: jira: connected");
            MessageBox.Show($"Connected to Jira{(me.Length > 0 ? $" as {me}" : "")}.", "flex-screen", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await tasks.RefreshAsync();
        }
        catch (Exception e)  // async void: anything left uncaught would end the companion
        {
            Log.Write($"tasks: jira: can't connect: {e.Message}");
            MessageBox.Show($"Jira didn't accept that: {e.Message}", "flex-screen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    async void ConnectGitHub()
    {
        var current = Settings.Source("github");
        var has = Secrets.Load(GitHubIssues.TokenName) != null;
        var values = InputDialog.Ask(
            "flex-screen: GitHub",
            "The deck shows the open GitHub issues assigned to you, in every repository, and opens one in the browser. It doesn't change them.\n\n" +
            "The token needs read access to issues (a fine-grained token with Issues: Read, or a classic one with repo). " +
            "Leave it empty to use the GitHub CLI's sign-in (gh auth login) instead.\n\n" +
            "Leave the host empty to disconnect GitHub. The search can be changed in settings.json.",
            "https://github.com/settings/personal-access-tokens",
            [new("Host", (string?)current?["host"] ?? "github.com", Hint: "github.com, or your GitHub Enterprise server"),
             new("Token", "", Secret: true, Hint: has ? "kept; type a new one to change it" : "empty: the GitHub CLI's")]);
        if (values == null) return;
        var (host, token) = (values[0].Trim(), values[1].Trim());
        if (host.Length == 0)
        {
            Settings.Set("github", null);
            Secrets.Delete(GitHubIssues.TokenName);
            Log.Write("tasks: github: disconnected");
            await tasks.RefreshAsync();
            return;
        }
        if (token.Length == 0) token = Secrets.Load(GitHubIssues.TokenName) ?? "";
        try
        {
            var me = await GitHubIssues.WhoAmIAsync(host, token);
            var settings = current?.DeepClone() as JsonObject ?? [];
            settings["host"] = host;
            Settings.Set("github", settings);
            if (token.Length > 0) Secrets.Save(GitHubIssues.TokenName, token);
            Log.Write("tasks: github: connected");
            MessageBox.Show($"Connected to GitHub{(me.Length > 0 ? $" as {me}" : "")}.", "flex-screen", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await tasks.RefreshAsync();
        }
        catch (Exception e)  // async void: anything left uncaught would end the companion
        {
            Log.Write($"tasks: github: can't connect: {e.Message}");
            MessageBox.Show($"GitHub didn't accept that: {e.Message}", "flex-screen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

/// <summary>A small form of labelled text boxes, for the tray's connect dialogs.</summary>
static class InputDialog
{
    public record Field(string Label, string Value, bool Secret = false, string Hint = "");

    /// <returns>The values, in order, or null when cancelled.</returns>
    public static string[]? Ask(string title, string intro, string help, Field[] fields)
    {
        using var form = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12), TopMost = true, Font = SystemFonts.MessageBoxFont,
        };
        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        var text = new Label { Text = intro, AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 0, 0, 8) };
        layout.Controls.Add(text, 0, 0);
        layout.SetColumnSpan(text, 2);
        var link = new LinkLabel { Text = help, AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 0, 0, 12) };
        link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(help) { UseShellExecute = true });
        layout.Controls.Add(link, 0, 1);
        layout.SetColumnSpan(link, 2);
        var boxes = new List<TextBox>();
        var row = 2;
        foreach (var field in fields)
        {
            layout.Controls.Add(new Label { Text = field.Label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, row);
            var box = new TextBox { Text = field.Value, UseSystemPasswordChar = field.Secret, Width = 370, PlaceholderText = field.Hint };
            layout.Controls.Add(box, 1, row++);
            boxes.Add(box);
        }
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.AddRange([cancel, ok]);
        layout.Controls.Add(buttons, 0, row);
        layout.SetColumnSpan(buttons, 2);
        form.Controls.Add(layout);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK ? boxes.Select(b => b.Text).ToArray() : null;
    }
}
