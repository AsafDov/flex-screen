using static FlexScreen.Companion.BarItem;

namespace FlexScreen.Companion;

/// <summary>
/// Claude Code's keys, the same wherever it runs (the Claude app, VS Code, a terminal):
/// Approve and Deny answer the newest permission request; Model and Effort open a list, and
/// show the session's; Usage shows the 5-hour figure (Sign in while Claude Code is signed
/// out). A choice from a list, like Usage, is typed into the session's prompt as a slash
/// command.
/// </summary>
sealed class ClaudeKeys(AppServices services)
{
    public static readonly BarItem[] Items =
    [
        A("claude-approve", "Approve", "approve", "claude.approve"), A("claude-deny", "Deny", "deny", "claude.deny"),
        new("claude-model", "Model", "brain", "pick", "model"), new("claude-effort", "Effort", "gauge", "pick", "effort"),
        new("claude-usage", "Usage", "chart", "slash", "/usage"),
    ];

    static readonly (string Id, string Name)[] Models =
        [("default", "Default"), ("opus", "Opus"), ("sonnet", "Sonnet"), ("haiku", "Haiku"), ("fable", "Fable"), ("opusplan", "Opus plan")];
    static readonly (string Id, string Name)[] Efforts =
        [("low", "Low"), ("medium", "Medium"), ("high", "High"), ("xhigh", "Extra high"), ("max", "Max"), ("auto", "Auto")];

    /// <summary>Model and Effort show the Claude session's own, in the window the bar is for; Usage the 5-hour window's.</summary>
    public BarItem Live(BarItem item, AppWindow window)
    {
        if (item.Id == "claude-usage")
        {
            if (services.Usage() is { Length: > 0 } used) return item with { Name = used };
            // Nothing to read the figure with: the key signs Claude Code in instead.
            return services.ClaudeSignedOut() ? item with { Name = "Sign in", Glyph = "lock", Kind = "action", Target = "claude.login" } : item;
        }
        if (item.Kind != "pick") return item;
        var (model, effort) = services.Claude(window.Hwnd);
        return item.Target switch
        {
            "model" when model.Length > 0 => item with { Name = ModelName(model) },
            "effort" when effort.Length > 0 => item with { Name = Efforts.FirstOrDefault(e => e.Id == effort).Name ?? effort },
            _ => item,
        };
    }

    /// <summary>Model and Effort show their list on the deck; Usage is typed into Claude's prompt.</summary>
    /// <param name="ready">Gets the prompt ready to type into, once the window is in front (VS Code: Claude's input box); false stops.</param>
    public bool Run(BarItem key, AppWindow window, Func<string, bool>? ready = null)
    {
        switch (key.Kind)
        {
            case "pick":
                var (model, effort) = services.Claude(window.Hwnd);
                var shown = ModelName(model);
                if (key.Target == "model")
                    services.Pick(key.Id, "Model", Models, shown.Length > 0 ? Models.FirstOrDefault(m => shown.StartsWith(m.Name, StringComparison.OrdinalIgnoreCase)).Id ?? "" : "");
                else services.Pick(key.Id, "Effort", Efforts, effort);
                return true;
            case "slash":
                TypeIntoClaude(window, key.Target, ready);
                return true;
        }
        return false;
    }

    /// <summary>A choice from Model's or Effort's list, typed as /model or /effort into the prompt of the Claude session in that window.</summary>
    public void Choose(BarItem key, string choice, AppWindow window, Func<string, bool>? ready = null)
    {
        var choices = key.Target == "model" ? Models : Efforts;
        if (choices.All(c => c.Id != choice)) return;
        TypeIntoClaude(window, key.Target == "model" ? $"/model {choice}" : $"/effort {choice}", ready);
    }

    /// <summary>A slash command typed into the prompt of the Claude session in that window and sent.</summary>
    static void TypeIntoClaude(AppWindow window, string command, Func<string, bool>? ready)
    {
        if (!window.ToFront()) return;
        if (ready != null && !ready(command)) return;
        Log.Write($"context: {command} in {window.Exe}");
        KeyInput.TypeLines(command + "\n");
    }

    /// <summary>"claude-opus-5-5[1m]" → "Opus 5.5"; "claude-sonnet-4-5-20250929" → "Sonnet 4.5"; "" stays "".</summary>
    public static string ModelName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var name = id.Replace("[1m]", "", StringComparison.OrdinalIgnoreCase);
        if (name.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) name = name[7..];
        var parts = name.Split('-').Where(p => p.Length > 0 && !(p.Length == 8 && p.All(char.IsDigit))).ToList();  // no release date
        if (parts.Count == 0) return id;
        var family = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
        return parts.Count > 1 ? $"{family} {string.Join('.', parts.Skip(1))}" : family;
    }
}
