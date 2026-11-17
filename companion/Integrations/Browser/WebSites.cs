using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlexScreen.Companion;

/// <summary>A site with keys of its own in a browser, as data (a "pack").</summary>
/// <param name="Id">The browser's context for it: the bar's key is "chrome:gmail", which saved layouts go by.</param>
/// <param name="Urls">Addresses it's at, as "host/path" with * for anything ("mail.google.com/*", "*.atlassian.net/browse/*"); known when the browser extension reports the tab.</param>
/// <param name="Titles">Tab titles, with * for anything ("* - Gmail"): the window's title is all the companion knows without the extension.</param>
/// <param name="Apps">Installed web apps (Chrome's or Edge's "Install as app") by their name ("Gmail").</param>
record WebSite(string Id, IReadOnlyList<Regex> Urls, IReadOnlyList<Regex> Titles, IReadOnlyList<string> Apps, AppBank Bank);

/// <summary>
/// The sites a browser's bar has keys for: the built-in ones (Integrations/Browser/sites.json,
/// in the exe: Gmail, YouTube, Google Calendar, Jira, GitHub) and the profile's own "sites",
/// which come first and replace a built-in one with the same id:
/// <code>
/// "sites": [
///   { "id": "notion", "name": "Notion", "color": "#FFFFFF",
///     "match": { "url": ["www.notion.so/*"], "title": ["* | Notion"], "app": ["Notion"] },
///     "keys": [ { "id": "notion-search", "name": "Search", "glyph": "search", "keys": "Ctrl+P" } ],
///     "default": ["notion-search"] }
/// ]
/// </code>
/// A key is written as for the profile's "contextBar" (<see cref="ContextBar.Item"/>).
/// </summary>
static class WebSites
{
    static readonly Lazy<JsonArray> BuiltInJson = new(() =>
    {
        using var stream = typeof(WebSites).Assembly.GetManifestResourceStream("sites.json")
                           ?? throw new InvalidOperationException("sites.json isn't in the companion");
        return JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!.AsArray();
    });

    static readonly object Gate = new();
    static string _seen = "\0";
    static IReadOnlyList<WebSite> _all = [];

    /// <summary>The profile's sites, then the built-in ones it doesn't replace; read again when the profile's change.</summary>
    public static IReadOnlyList<WebSite> All
    {
        get
        {
            var mine = Settings.Sites;
            var stamp = mine?.ToJsonString() ?? "";
            lock (Gate)
            {
                if (stamp == _seen) return _all;
                _seen = stamp;
                var sites = new List<WebSite>();
                foreach (var node in (mine ?? []).Concat(BuiltInJson.Value))
                {
                    if (node is JsonObject o && Parse(o) is { } site && sites.All(s => s.Id != site.Id)) sites.Add(site);
                }
                _all = sites;
                return sites;
            }
        }
    }

    /// <summary>
    /// The site a browser window shows: by the tab's address when it's known (the extension
    /// reports it), else by the installed web app it is, else by the tab's title. The first
    /// site that matches wins, the profile's before the built-in ones.
    /// </summary>
    public static WebSite? Of(string title, string url, string app)
    {
        var sites = All;
        var address = Address(url);
        if (address.Length > 0 && sites.FirstOrDefault(s => s.Urls.Any(u => u.IsMatch(address))) is { } byUrl) return byUrl;
        if (app.Length > 0 && sites.FirstOrDefault(s => s.Apps.Any(a => a.Equals(app, StringComparison.OrdinalIgnoreCase))) is { } byApp) return byApp;
        return title.Length > 0 ? sites.FirstOrDefault(s => s.Titles.Any(t => t.IsMatch(title))) : null;
    }

    public static WebSite? Find(string id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>"https://mail.google.com/mail/u/0/#inbox" → "mail.google.com/mail/u/0/"; "" for one that isn't a web address.</summary>
    public static string Address(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.Host.ToLowerInvariant() + uri.AbsolutePath : "";

    /// <summary>A site as written, or null (and a line in the log) for one without an id or keys.</summary>
    public static WebSite? Parse(JsonObject o)
    {
        var id = ((string?)o["id"] ?? "").Trim().ToLowerInvariant();
        if (id.Length == 0 || !id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
        {
            Log.Write($"context: a site needs an \"id\" of letters, digits and \"-\": {(string?)o["name"] ?? "(no name)"}");
            return null;
        }
        var keys = (o["keys"] as JsonArray ?? []).OfType<JsonObject>().Select(ContextBar.Item).OfType<BarItem>().DistinctBy(k => k.Id).ToArray();
        if (keys.Length == 0) return null;
        var defaults = (o["default"] as JsonArray ?? []).Select(n => (string?)n ?? "").Where(d => keys.Any(k => k.Id == d)).ToArray();
        var match = o["match"] as JsonObject;
        IReadOnlyList<string> List(string key) => (match?[key] as JsonArray ?? []).Select(n => ((string?)n ?? "").Trim()).Where(s => s.Length > 0).ToList();
        var bank = new AppBank(((string?)o["name"] ?? id).Trim(), ContextBar.Color(o["color"]), keys, defaults.Length > 0 ? defaults : keys.Take(6).Select(k => k.Id).ToArray());
        return new WebSite(id, List("url").Select(Glob).ToList(), List("title").Select(Glob).ToList(), List("app"), bank);
    }

    /// <summary>A pattern with * for anything, whole, in any case.</summary>
    static Regex Glob(string pattern) =>
        new("^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
