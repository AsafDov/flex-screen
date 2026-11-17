using System.Diagnostics;

namespace FlexScreen.Companion;

/// <summary>
/// The tray's Profile menu: the profiles to switch between, a new one, the profile's file and
/// this PC's, and where the profiles are kept (a synced folder gives every PC the same ones).
/// Switching takes effect at the next tick: TrayApp sees the settings change and reads the
/// accounts and task sources again. Runs on the UI thread.
/// </summary>
static class ProfileSetup
{
    public static ToolStripMenuItem Menu()
    {
        var menu = new ToolStripMenuItem("Profile");
        menu.DropDownItems.Add(new ToolStripMenuItem());  // filled on opening; a submenu needs an item to open
        menu.DropDownOpening += (_, _) => Fill(menu);
        return menu;
    }

    /// <summary>The menu item's text, for the tray menu as it opens: "Profile: work".</summary>
    public static string Title => $"Profile: {Settings.Profile}";

    static void Fill(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        var current = Settings.Profile;
        foreach (var name in Settings.Profiles)
        {
            var item = new ToolStripMenuItem(name) { Checked = name.Equals(current, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => Use(name);
            menu.DropDownItems.Add(item);
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("New profile…", null, (_, _) => Create()));
        menu.DropDownItems.Add(new ToolStripMenuItem("Open profile file", null, (_, _) => OpenProfile()));
        menu.DropDownItems.Add(new ToolStripMenuItem("Open this PC's settings", null, (_, _) => OpenMachine()));
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("Keep the profiles in…", null, (_, _) => PickFolder()) { ToolTipText = Settings.ProfilesFolder });
        menu.DropDownItems.Add(new ToolStripMenuItem("Open the profiles folder", null, (_, _) => OpenFolder()));
    }

    static void Use(string name)
    {
        try
        {
            Settings.UseProfile(name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show($"Couldn't switch to {name}: {e.Message}", "flex-screen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    static void Create()
    {
        var current = Settings.Profile;
        var values = InputDialog.Ask(
            "flex-screen: new profile",
            "A profile is a set of settings to switch between: its Google accounts and task sources, shortcuts, context bar " +
            "keys, colors. Work and home, say. Sign-ins stay on each PC: a profile names the accounts, and each PC signs in to them once.",
            "https://github.com/AsafDov/flex-screen#profiles",
            [new("Name", "", Hint: "work")]);
        if (values == null) return;
        var name = values[0].Trim();
        if (!Settings.IsProfileName(name))
        {
            MessageBox.Show("A profile's name is its file's: letters, digits, spaces, \"-\" and \"_\", up to 40.", "flex-screen",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var copy = MessageBox.Show($"Start {name} as a copy of {current}?\n\nNo starts it with the defaults.", "flex-screen",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (copy == DialogResult.Cancel) return;
        try
        {
            if (!Settings.CreateProfile(name, copy == DialogResult.Yes))
                MessageBox.Show($"There's a profile called {name} already.", "flex-screen", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show($"Couldn't make {name}: {e.Message}", "flex-screen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// The profile's file in the user's editor, with the everyday settings it doesn't have yet
    /// written in at their current values, so they can be seen and changed. Saving applies them.
    /// </summary>
    public static void OpenProfile()
    {
        try
        {
            Settings.AddMissing();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log.Write($"settings: opened as it is ({e.Message})");  // a mistake in the file: opened to be fixed
        }
        AppPaths.Open(Settings.ProfilePath);
    }

    static void OpenMachine()
    {
        try
        {
            Settings.AddMachineFile();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"settings: machine.json: {e.Message}");
        }
        AppPaths.Open(Settings.MachinePath);
    }

    static void PickFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "A folder for the profiles. A synced one (OneDrive, Dropbox) gives every PC the same profiles: pick the same folder on each.",
            UseDescriptionForTitle = true,
            InitialDirectory = Settings.ProfilesFolder,
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        try
        {
            Settings.UseProfilesFolder(dialog.SelectedPath);
            MessageBox.Show($"The profiles are kept in {Settings.ProfilesFolder} now. Any that weren't there were copied in.",
                "flex-screen", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Couldn't use that folder: {e.Message}", "flex-screen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    static void OpenFolder()
    {
        var folder = Settings.ProfilesFolder;
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Write($"settings: can't open {folder}: {e.Message}");
        }
    }
}
