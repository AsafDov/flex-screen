using System.Security.Cryptography;
using System.Text;

namespace FlexScreen.Companion;

/// <summary>
/// Sign-in tokens (Google's and Microsoft's refresh tokens, Jira's API token, GitHub's), each
/// in its own file in %LOCALAPPDATA%\flex-screen, encrypted for this Windows user (DPAPI).
/// Per PC: never in a profile, never logged and never sent to the deck. A site's token (a
/// Jira site, a GitHub host, a Microsoft app registration) is kept under the site's name
/// (<see cref="LoadFor"/>), so profiles that use different sites each have their own.
/// </summary>
static class Secrets
{
    static readonly string Folder = AppPaths.Data;

    static string PathOf(string name) => Path.Combine(Folder, name + ".bin");

    public static void Save(string name, string value)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(PathOf(name), ProtectedBytes.Protect(Encoding.UTF8.GetBytes(value)));
    }

    /// <summary>The value, or null when there's none (or it was saved by another Windows user).</summary>
    public static string? Load(string name)
    {
        try
        {
            var path = PathOf(name);
            return File.Exists(path) ? Encoding.UTF8.GetString(ProtectedBytes.Unprotect(File.ReadAllBytes(path))) : null;
        }
        catch (Exception e) when (e is IOException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// A site's token: "jira-token" for acme.atlassian.net is jira-token.acme.atlassian.net.bin.
    /// The one kept under the plain name before tokens were per site becomes the site's the
    /// first time one is asked for (the site that was set up then is the one still set up).
    /// </summary>
    public static string? LoadFor(string name, string site)
    {
        var keyed = Keyed(name, site);
        if (Load(keyed) is { } value) return value;
        if (Load(name) is not { } old) return null;
        Save(keyed, old);
        Delete(name);
        return old;
    }

    public static void SaveFor(string name, string site, string value) => Save(Keyed(name, site), value);

    public static void DeleteFor(string name, string site)
    {
        Delete(Keyed(name, site));
        Delete(name);  // one from before tokens were per site, if it's still there
    }

    /// <summary>The site in a file name: lower case, letters, digits, "." and "-".</summary>
    static string Keyed(string name, string site)
    {
        var safe = new string(site.Trim().ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' ? c : '-').ToArray());
        return $"{name}.{(safe.Length > 80 ? safe[..80] : safe)}";
    }

    public static void Delete(string name)
    {
        try
        {
            File.Delete(PathOf(name));
        }
        catch (IOException)
        {
        }
    }
}
