using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace FlexScreen.Companion;

/// <summary>
/// A browser sign-in for a desktop app (OAuth's authorization code with PKCE): opens the
/// provider's consent page and listens on localhost for its redirect. Google and Microsoft
/// To Do sign in this way.
/// </summary>
static class OAuthLoopback
{
    /// <param name="Redirect">The redirect address to send with the code ("http://localhost:51234").</param>
    /// <param name="Verifier">PKCE's verifier, for the token request.</param>
    public record Pending(string Redirect, string Verifier, string Challenge, string State, int Port);

    public static Pending Prepare()
    {
        var port = FreePort();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return new Pending($"http://localhost:{port}", verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
                           Base64Url(RandomNumberGenerator.GetBytes(16)), port);
    }

    /// <summary>Opens <paramref name="url"/> in the browser and waits (5 minutes at most) for the redirect: its code, or null.</summary>
    /// <param name="what">For the page the browser lands on ("Microsoft To Do").</param>
    public static async Task<string?> CodeAsync(Pending pending, string url, string what)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{pending.Port}/");
        listener.Start();
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(5));
        var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? "");
        var ok = query["state"] == pending.State && query["code"] is { Length: > 0 };
        var page = Encoding.UTF8.GetBytes(ok ? $"<p>flex-screen is connected to {what}. You can close this tab.</p>"
                                             : $"<p>The {what} sign-in didn't complete. Try again from the flex-screen tray menu.</p>");
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.OutputStream.WriteAsync(page);
        context.Response.Close();
        if (!ok) Log.Write($"oauth: {what} sign-in failed ({query["error"] ?? "no code"}: {query["error_description"]})");
        return ok ? query["code"] : null;
    }

    static int FreePort()
    {
        var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
