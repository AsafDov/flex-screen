using System.Security.Cryptography;
using System.Text;

namespace FlexScreen.Companion;

/// <summary>
/// Sign-in tokens for the task sources (Jira's API token, GitHub's, Microsoft's refresh
/// token), each in its own file in %LOCALAPPDATA%\flex-screen, encrypted for this Windows
/// user (DPAPI). Never logged and never sent to the deck.
/// </summary>
static class Secrets
{
    static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "flex-screen");

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
