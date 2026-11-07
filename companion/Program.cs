namespace FlexScreen.Companion;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Setup commands for the install scripts; they report through the exit code and the log.
        if (args.Length == 1 && args[0] is "--install-hooks" or "--uninstall-hooks")
        {
            try
            {
                if (args[0] == "--install-hooks") ClaudeHooks.Install();
                else ClaudeHooks.Uninstall();
                return 0;
            }
            catch (Exception e)
            {
                Log.Write($"hooks: {args[0]} failed: {e.Message}");
                return 1;
            }
        }

        // One companion per user: two would fight over the deck's serial port.
        using var single = new Mutex(true, @"Local\flex-screen-companion", out var first);
        if (!first) return 0;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
        return 0;
    }
}
