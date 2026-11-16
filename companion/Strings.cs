namespace FlexScreen.Companion;

/// <summary>Text shaped for the deck.</summary>
static class Strings
{
    /// <summary>One line (line breaks and tabs become spaces), at most <paramref name="max"/> characters, "…" when cut.</summary>
    public static string OneLine(string text, int max)
    {
        var line = string.Join(" ", text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > max ? line[..(max - 1)] + "…" : line;
    }
}
