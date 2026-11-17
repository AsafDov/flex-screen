namespace FlexScreen.Companion;

/// <summary>
/// The profile's Google accounts (<see cref="GoogleAccount"/>; a personal and a work one, say):
/// which there are, and signing each in and out. Each one's calendars are a calendar source
/// (<see cref="GoogleCalendar"/>) and its Google Tasks a task source (<see cref="GoogleTasks"/>).
/// Signing in is a one-time browser consent per account, from the tray menu; the refresh
/// tokens are kept encrypted for this Windows user (DPAPI).
/// </summary>
sealed class GoogleSource
{
    readonly object _gate = new();
    List<GoogleAccount> _accounts = [];
    string _accountsSeen = "\0";

    public event Action<Toast>? Toast;

    /// <summary>Raised after a sign-in or a sign-out, so the calendar and the tasks are read again.</summary>
    public event Action? SignedIn;

    /// <summary>The profile's Google accounts, in its order. The same object stays for an account while the profile has it.</summary>
    public IReadOnlyList<GoogleAccount> Accounts
    {
        get
        {
            lock (_gate)
            {
                var stamp = Settings.AccountsStamp;
                if (stamp == _accountsSeen) return _accounts;
                _accountsSeen = stamp;
                var accounts = new List<GoogleAccount>();
                foreach (var (id, config) in Settings.Accounts("google"))
                {
                    var account = _accounts.FirstOrDefault(a => a.Id == id);
                    if (account != null) account.Configure(config);
                    else account = new GoogleAccount(id, config);
                    accounts.Add(account);
                }
                _accounts = accounts;
                return accounts;
            }
        }
    }

    public GoogleAccount? Account(string id) => Accounts.FirstOrDefault(a => a.Id == id);

    /// <summary>The browser consent for one account, from the tray menu.</summary>
    public async Task SignInAsync(GoogleAccount account)
    {
        if (await account.SignInAsync() is not { } missing) return;
        Log.Write(missing.Count == 0 ? $"google: {account.Id}: connected" : $"google: {account.Id}: connected without {string.Join(" and ", missing)} access");
        Toast?.Invoke(missing.Count == 0
            ? new Toast("ok", $"Connected to {account.Name}")
            : new Toast("error", $"Google didn't grant {string.Join(" and ", missing)} access. Connect again and tick every box."));
        // Each account is meant to be another Google account: the same one twice shows its
        // tasks twice.
        try
        {
            await account.LearnEmailAsync();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Write($"google: {account.Id}: who signed in isn't known yet: {e.Message}");
        }
        if (account.Email.Length > 0 && Accounts.FirstOrDefault(a => a != account && a.Email == account.Email) is { } twin)
            Toast?.Invoke(new Toast("attention", $"{account.Name} is the same Google account as {twin.Name}"));
        SignedIn?.Invoke();
    }

    /// <summary>Forgets one account's sign-in on this PC.</summary>
    public void SignOut(GoogleAccount account)
    {
        account.SignOut();
        Log.Write($"google: {account.Id}: signed out on this PC");
        SignedIn?.Invoke();  // its events and tasks go
    }
}
