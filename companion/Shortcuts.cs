using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// The deck's shortcuts launcher: up to 12 tiles from the profile's "shortcuts", each one of
/// <list type="bullet">
/// <item><c>{ "name": "Gmail", "url": "https://mail.google.com" }</c>: opens in the default browser</item>
/// <item><c>{ "name": "Spotify", "app": "Spotify" }</c>: a Start menu app, by its name there; brought to the front if it's open</item>
/// <item><c>{ "name": "Projects", "path": "C:\\Projects" }</c>: a file, folder or program (with "args")</item>
/// <item><c>{ "name": "Today's note", "action": "obs.today" }</c>: something the deck already does</item>
/// <item><c>{ "name": "Snip", "keys": "Win+Shift+S" }</c>: a key combination, into the window in front</item>
/// </list>
/// Optional on any: "glyph" (the symbol shown without an icon) and "icon" (a picture file, or
/// a program whose icon to use). Apps, files and programs show their own icon; links the
/// site's (favicon).
/// </summary>
sealed class Shortcuts
{
    public const int Max = 12;

    /// <param name="Kind">url, app, path, action or keys.</param>
    /// <param name="Target">The url, the app's id (once found), the path, the action or the keys.</param>
    /// <param name="Look">The deck's symbol when there's no icon: mail, calendar, note, terminal, code, music, media, volume, volume-up, volume-down, screen-off, taskmgr, lock, desktop, folder, keys, git, call, or "" for the name's first letter.</param>
    /// <param name="Icon">The icon's id, once the deck can be sent one.</param>
    public record Item(string Id, string Name, string Kind, string Target, string Args, string AppName, string Look, string Icon);

    /// <summary>Deck actions a tile may run: the ones that need nothing more said.</summary>
    public static readonly Dictionary<string, string> Actions = new()
    {
        ["obs.today"] = "note", ["mail.open"] = "mail", ["pc.lock"] = "lock", ["desktop.show"] = "desktop",
        ["media.toggle"] = "media", ["media.next"] = "media", ["media.prev"] = "media", ["vol.mute"] = "volume",
        ["vol.up"] = "volume-up", ["vol.down"] = "volume-down", ["screens.off"] = "screen-off", ["taskmgr.open"] = "taskmgr",
        ["mic.mute"] = "call", ["term.focus"] = "terminal", ["term.interrupt"] = "terminal", ["git.commit"] = "git",
        ["git.fetch"] = "git", ["git.pull"] = "git", ["git.push"] = "git", ["git.stash"] = "git",
        ["call.mic"] = "call", ["call.camera"] = "call", ["call.hand"] = "call", ["call.leave"] = "call", ["call.focus"] = "call",
    };

    /// <summary>A tile's name when its entry gives none.</summary>
    static readonly Dictionary<string, string> ActionNames = new()
    {
        ["vol.up"] = "Volume up", ["vol.down"] = "Volume down", ["screens.off"] = "Screens off", ["taskmgr.open"] = "Task Manager",
    };

    /// <summary>What a PC gets before its settings say otherwise.</summary>
    public static JsonArray Defaults() =>
    [
        new JsonObject { ["name"] = "Gmail", ["url"] = "https://mail.google.com/" },
        new JsonObject { ["name"] = "Calendar", ["url"] = "https://calendar.google.com/", ["glyph"] = "calendar" },
        new JsonObject { ["name"] = "Today's note", ["action"] = "obs.today" },
        new JsonObject { ["name"] = "Spotify", ["app"] = "Spotify" },
        new JsonObject { ["name"] = "VS Code", ["app"] = "Visual Studio Code" },
        new JsonObject { ["name"] = "Terminal", ["app"] = "Terminal" },
    ];

    static readonly HttpClient Http = MakeHttp();
    static readonly TimeSpan BringUpFor = TimeSpan.FromSeconds(8);

    readonly WindowSource _windows;
    readonly Action<string> _act;
    readonly object _gate = new();
    readonly Dictionary<string, Bitmap?> _favicons = new(StringComparer.OrdinalIgnoreCase);  // host → icon
    List<Item> _items = [];
    string _config = "\0";
    int _generation;

    public Shortcuts(WindowSource windows, Action<string> act)
    {
        _windows = windows;
        _act = act;
    }

    public IReadOnlyList<Item> Current
    {
        get
        {
            lock (_gate) return _items;
        }
    }

    /// <summary>Raised (on a background thread) when <see cref="Current"/> changes.</summary>
    public event Action? Changed;

    public event Action<Toast>? Toast;

    /// <summary>Reads the list again if the settings changed; cheap otherwise. Call every tick.</summary>
    public void Refresh()
    {
        var config = (Settings.Shortcuts ?? Defaults()).ToJsonString();
        if (config == _config) return;
        _config = config;
        var generation = Interlocked.Increment(ref _generation);
        var thread = new Thread(() => Resolve(config, generation)) { IsBackground = true, Name = "shortcuts" };
        thread.SetApartmentState(ApartmentState.STA);  // the shell's app list and icons
        thread.Start();
    }

    /// <summary>The file the list is in opened for editing (the profile's, or this PC's when it has its own), with the current list written in if it has none.</summary>
    public static void Edit()
    {
        if (Settings.Shortcuts == null) Settings.Set("shortcuts", Defaults());
        AppPaths.Open(Settings.FileOf("shortcuts"));
    }

    void Resolve(string config, int generation)
    {
        try
        {
            var entries = (JsonNode.Parse(config) as JsonArray ?? []).OfType<JsonObject>().ToList();
            List<ShellApps.App>? apps = null;
            var items = new List<Item>();
            var favicons = new List<(int Index, string Host)>();
            foreach (var e in entries)
            {
                if (items.Count == Max) break;
                if (Describe(e, ref apps) is not { } item) continue;
                if (item.Icon.Length == 0 && item.Kind == "url" && Uri.TryCreate(item.Target, UriKind.Absolute, out var uri) && uri.Scheme == "https" && (string?)e["icon"] == null)
                {
                    favicons.Add((items.Count, uri.Host));
                }
                items.Add(item);
            }
            if (!Publish(items, generation)) return;
            Log.Write($"shortcuts: {string.Join(", ", items.Select(i => $"{i.Name} ({i.Kind}{(i.Kind == "app" ? i.Target.Length > 0 ? " " + i.Target : " not found" : "")}{(i.Icon.Length > 0 ? ", icon" : "")})"))}");

            // Sites' own icons, which take a moment, then the list again.
            var found = false;
            foreach (var (index, host) in favicons)
            {
                if (Favicon(host) is not { } image) continue;
                items[index] = items[index] with { Icon = _windows.AddIcon(image).Id };
                found = true;
            }
            if (found && Publish(items, generation))
                Log.Write($"shortcuts: site icons for {string.Join(", ", favicons.Where(f => items[f.Index].Icon.Length > 0).Select(f => f.Host))}");
        }
        catch (Exception e)
        {
            Log.Write($"shortcuts: {e.GetType().Name}: {e.Message}");
        }
    }

    bool Publish(List<Item> items, int generation)
    {
        lock (_gate)
        {
            if (generation != _generation) return false;  // settings changed again meanwhile
            _items = items.ToList();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>One entry of the settings, checked and filled in; null (and a log line) if it can't be used.</summary>
    Item? Describe(JsonObject e, ref List<ShellApps.App>? apps)
    {
        var name = ((string?)e["name"] ?? "").Trim();
        var look = (string?)e["glyph"];
        var args = (string?)e["args"] ?? "";
        string kind, target, appName = "", key;
        Bitmap? image = null;
        if ((string?)e["url"] is { Length: > 0 } url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is "file" or "javascript" or "vbscript")
            {
                Log.Write($"shortcuts: \"{url}\" isn't a link to open");
                return null;
            }
            (kind, target, key) = ("url", uri.ToString(), url);
            look ??= uri.Host switch { "mail.google.com" => "mail", "calendar.google.com" => "calendar", _ => "" };
            if (name.Length == 0) name = uri.Host.StartsWith("www.") ? uri.Host[4..] : uri.Host;
        }
        else if ((string?)e["app"] is { Length: > 0 } app)
        {
            (kind, key, appName) = ("app", app, app);
            apps ??= ShellApps.List();
            var found = ShellApps.Find(apps, app);
            target = found?.Id ?? "";
            if (found != null)
            {
                appName = found.Name;
                image = ShellApps.Image($"shell:AppsFolder\\{found.Id}", WindowSource.IconSize);
            }
            else
            {
                Log.Write($"shortcuts: no app called \"{app}\" in the Start menu");
            }
            if (name.Length == 0) name = found?.Name ?? app;
            look ??= app.Contains("terminal", StringComparison.OrdinalIgnoreCase) ? "terminal" : "";
        }
        else if ((string?)e["path"] is { Length: > 0 } path)
        {
            target = Environment.ExpandEnvironmentVariables(path);
            (kind, key) = ("path", path);
            var folder = Directory.Exists(target);
            if (folder || File.Exists(target)) image = ShellApps.Image(target, WindowSource.IconSize);
            look ??= folder ? "folder" : "";
            if (name.Length == 0) name = Path.GetFileNameWithoutExtension(target.TrimEnd('\\')) is { Length: > 0 } n ? n : target;
        }
        else if ((string?)e["action"] is { Length: > 0 } action)
        {
            if (!Actions.TryGetValue(action, out var actionLook))
            {
                Log.Write($"shortcuts: \"{action}\" isn't an action a shortcut can run ({string.Join(", ", Actions.Keys)})");
                return null;
            }
            (kind, target, key) = ("action", action, action);
            look ??= actionLook;
            if (name.Length == 0) name = ActionNames.GetValueOrDefault(action, action);
            if (action == "taskmgr.open") image = ShellApps.Image(Path.Combine(Environment.SystemDirectory, "Taskmgr.exe"), WindowSource.IconSize);
        }
        else if ((string?)e["keys"] is { Length: > 0 } keys)
        {
            if (KeyInput.Parse(keys) == null)
            {
                Log.Write($"shortcuts: can't read the keys \"{keys}\" (write them like Ctrl+Shift+Esc or Win+D)");
                return null;
            }
            (kind, target, key) = ("keys", keys, keys);
            look ??= "keys";
            if (name.Length == 0) name = keys;
        }
        else
        {
            Log.Write($"shortcuts: {e.ToJsonString()} needs a url, app, path, action or keys");
            return null;
        }
        if ((string?)e["icon"] is { Length: > 0 } iconPath)
        {
            var file = Environment.ExpandEnvironmentVariables(iconPath);
            if (File.Exists(file)) image = ShellApps.Image(file, WindowSource.IconSize, iconOnly: !IsPicture(file)) ?? image;
            else Log.Write($"shortcuts: no icon file {file}");
        }
        var id = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{kind}\n{key}\n{name}\n{args}")))[..10].ToLowerInvariant();
        var icon = image != null ? _windows.AddIcon(image).Id : "";
        image?.Dispose();
        return new Item(id, name, kind, target, args, appName, look ?? "", icon);
    }

    static bool IsPicture(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif";

    /// <summary>A volume tile, which the deck repeats while it's held (so it isn't logged each time).</summary>
    public bool Steps(string id) => Current.FirstOrDefault(i => i.Id == id) is { Kind: "action", Target: "vol.up" or "vol.down" };

    /// <summary>A tile tapped on the deck.</summary>
    public void Run(string id)
    {
        if (Current.FirstOrDefault(i => i.Id == id) is not { } item) return;
        try
        {
            switch (item.Kind)
            {
                case "url":
                    Process.Start(new ProcessStartInfo(item.Target) { UseShellExecute = true });
                    break;
                case "app":
                    OpenApp(item);
                    break;
                case "path":
                    OpenPath(item);
                    break;
                case "action":
                    _act(item.Target);
                    break;
                case "keys":
                    KeyInput.Combo(item.Target);
                    break;
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Write($"shortcuts: can't open {item.Name}: {e.Message}");
            Toast?.Invoke(new Toast("error", $"Can't open {item.Name}"));
        }
    }

    /// <summary>Its window to the front if it's open, else the app started (and its window brought up when it comes).</summary>
    void OpenApp(Item item)
    {
        var target = item.Target;
        if (target.Length == 0)
        {
            // Not in the Start menu when the list was read: installed since, perhaps.
            target = ShellApps.OnSta(() => ShellApps.Find(ShellApps.List(), item.AppName))?.Id ?? "";
            if (target.Length == 0)
            {
                Toast?.Invoke(new Toast("error", $"No app called \"{item.AppName}\" in the Start menu"));
                return;
            }
        }
        if (_windows.FindAppWindow(target, item.AppName) is not 0 and var open)
        {
            WindowFocus.Activate((IntPtr)open);
            return;
        }
        WindowFocus.AllowLaunch();
        Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{target}") { UseShellExecute = false });
        BringUpWhenOpen(() => _windows.FindAppWindow(target, item.AppName));
    }

    void OpenPath(Item item)
    {
        var exe = item.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe && item.Args.Length == 0 && _windows.FindAppWindow(item.Target, "") is not 0 and var open)
        {
            WindowFocus.Activate((IntPtr)open);
            return;
        }
        WindowFocus.AllowLaunch();
        Process.Start(new ProcessStartInfo(item.Target, item.Args)
        {
            UseShellExecute = true,
            WorkingDirectory = exe ? Path.GetDirectoryName(item.Target) ?? "" : "",
        });
        if (exe) BringUpWhenOpen(() => _windows.FindAppWindow(item.Target, ""));
    }

    /// <summary>
    /// An app started from the background can open behind the window in front: once its
    /// window shows up (the window list polls every 300 ms), it's brought forward.
    /// </summary>
    static void BringUpWhenOpen(Func<long> find)
    {
        _ = Task.Run(async () =>
        {
            var until = DateTime.UtcNow + BringUpFor;
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(400);
                if (find() is not 0 and var hwnd)
                {
                    if (WindowSource.Foreground() != (IntPtr)hwnd) WindowFocus.Activate((IntPtr)hwnd);
                    return;
                }
            }
        });
    }

    /// <summary>The site's /favicon.ico, from the site itself; null if it has none (or can't be reached).</summary>
    Bitmap? Favicon(string host)
    {
        lock (_gate)
        {
            if (_favicons.TryGetValue(host, out var known)) return known;
        }
        Bitmap? image = null;
        try
        {
            using var response = Http.GetAsync($"https://{host}/favicon.ico").GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode) image = DecodeIcon(response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Log.Write($"shortcuts: no icon from {host}: {e.Message}");
            return null;  // tried again when the settings next change
        }
        lock (_gate) _favicons[host] = image;
        return image;
    }

    /// <summary>An .ico (the frame nearest 32 px at or above it, PNG or bitmap), or a PNG/GIF/JPEG; null if neither.</summary>
    static Bitmap? DecodeIcon(byte[] data)
    {
        try
        {
            if (data.Length > 6 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0)
            {
                int count = BitConverter.ToUInt16(data, 4), best = -1, bestSize = 0;
                for (var i = 0; i < count && 6 + i * 16 + 16 <= data.Length; i++)
                {
                    var size = data[6 + i * 16] == 0 ? 256 : data[6 + i * 16];
                    var better = best < 0 || (size >= WindowSource.IconSize ? bestSize < WindowSource.IconSize || size < bestSize : size > bestSize);
                    if (better) (best, bestSize) = (i, size);
                }
                if (best < 0) return null;
                var length = (int)BitConverter.ToUInt32(data, 6 + best * 16 + 8);
                var offset = (int)BitConverter.ToUInt32(data, 6 + best * 16 + 12);
                if (offset + 8 <= data.Length && data[offset] == 0x89 && data[offset + 1] == (byte)'P')
                {
                    return Picture(data.AsSpan(offset, Math.Min(length, data.Length - offset)).ToArray());
                }
                using var stream = new MemoryStream(data);
                using var icon = new Icon(stream, WindowSource.IconSize, WindowSource.IconSize);
                return icon.ToBitmap();
            }
            return Picture(data);
        }
        catch (Exception e) when (e is ArgumentException or ExternalException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    static Bitmap Picture(byte[] data)
    {
        using var stream = new MemoryStream(data);
        using var picture = new Bitmap(stream);
        return new Bitmap(picture);  // a copy: the original needs its stream for as long as it lives
    }

    static HttpClient MakeHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("flex-screen");
        return http;
    }
}
