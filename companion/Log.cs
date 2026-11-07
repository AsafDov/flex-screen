namespace FlexScreen.Companion;

/// <summary>Appends timestamped lines to companion.log next to the executable.</summary>
static class Log
{
    const long MaxBytes = 1_000_000;
    static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "companion.log");

    static Log()
    {
        if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes) File.Delete(FilePath);
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        lock (Gate)
        {
            File.AppendAllText(FilePath, line + Environment.NewLine);
        }
        System.Diagnostics.Debug.WriteLine(line);
    }
}
