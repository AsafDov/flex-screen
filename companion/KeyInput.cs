using System.Runtime.InteropServices;

namespace FlexScreen.Companion;

/// <summary>
/// Types text into the focused window as Unicode characters, so what arrives doesn't depend
/// on the keyboard layout (Hebrew swaps "(" and ")", for one). Used by the deck's numpad.
/// </summary>
static class KeyInput
{
    public static void Type(string text)
    {
        var inputs = new List<Input>();
        foreach (var ch in text)
        {
            inputs.Add(Key(ch, 0));
            inputs.Add(Key(ch, KeyeventfKeyup));
        }
        if (inputs.Count > 0) SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
    }

    public const ushort Control = 0x11;
    public const ushort Alt = 0x12;

    /// <summary>A shortcut such as Ctrl+V: the modifier, then the key (a letter's key code is its capital).</summary>
    public static void Chord(ushort modifier, char key)
    {
        var vk = (ushort)char.ToUpperInvariant(key);
        Input[] inputs = [Vk(modifier, 0), Vk(vk, 0), Vk(vk, KeyeventfKeyup), Vk(modifier, KeyeventfKeyup)];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    /// <summary>
    /// A key combination written out, as a shortcut tile has it: "Win+Shift+S", "Ctrl+Alt+T",
    /// "F5", "Ctrl+Shift+Esc". Modifiers go down first and come up last. False if a name isn't
    /// known, and then nothing is pressed.
    /// </summary>
    public static bool Combo(string keys)
    {
        if (Parse(keys) is not { } codes) return false;
        var inputs = codes.Select(vk => Vk(vk, Extended(vk))).Concat(codes.AsEnumerable().Reverse().Select(vk => Vk(vk, Extended(vk) | KeyeventfKeyup))).ToArray();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        return true;
    }

    /// <summary>
    /// Combinations one after another, comma-separated: "g, i" (Gmail's go to inbox),
    /// "Ctrl+K, Z". A comma key itself is "comma". False if any part can't be read; then
    /// nothing is pressed.
    /// </summary>
    public static bool Sequence(string keys)
    {
        var parts = keys.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => Parse(p) == null)) return false;
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) Thread.Sleep(40);
            Combo(parts[i]);
        }
        return true;
    }

    /// <summary>Whether <see cref="Sequence"/> can read the keys.</summary>
    public static bool CanPress(string keys)
    {
        var parts = keys.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts.All(p => Parse(p) != null);
    }

    /// <summary>Text typed as characters, each "\n" pressed as Enter (a command line: "git status\n").</summary>
    public static void TypeLines(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > 0) Type(lines[i]);
            if (i < lines.Length - 1)
            {
                Thread.Sleep(30);
                Combo("Enter");
            }
        }
    }

    /// <summary>The key codes of "Ctrl+Shift+Esc", in order; null if a name isn't known.</summary>
    public static List<ushort>? Parse(string keys)
    {
        var codes = new List<ushort>();
        foreach (var part in keys.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (VkFor(part) is not { } vk) return null;
            codes.Add(vk);
        }
        return codes.Count > 0 ? codes : null;
    }

    static ushort? VkFor(string name)
    {
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0])) return char.ToUpperInvariant(name[0]);
        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24) return (ushort)(0x6F + f);
        return name.ToLowerInvariant() switch
        {
            "ctrl" or "control" => Control,
            "shift" => 0x10,
            "alt" => Alt,
            "win" or "windows" or "meta" => 0x5B,
            "esc" or "escape" => 0x1B,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "space" => 0x20,
            "backspace" => 0x08,
            "delete" or "del" => 0x2E,
            "insert" or "ins" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            "printscreen" or "prtsc" => 0x2C,
            "pause" => 0x13,
            "menu" or "apps" => 0x5D,
            "mute" or "volumemute" => 0xAD,
            "volumedown" => 0xAE,
            "volumeup" => 0xAF,
            "next" or "nexttrack" => 0xB0,
            "prev" or "previous" or "prevtrack" => 0xB1,
            "playpause" or "play" => 0xB3,
            "-" or "minus" => 0xBD,
            "=" or "plus" => 0xBB,
            "," or "comma" => 0xBC,
            "." or "period" => 0xBE,
            "/" or "slash" => 0xBF,
            ";" => 0xBA,
            "'" => 0xDE,
            "[" => 0xDB,
            "]" => 0xDD,
            "\\" or "backslash" => 0xDC,
            "`" or "backtick" => 0xC0,
            _ => null,
        };
    }

    // Keys Windows tells apart from their numpad twins (and the Windows and media keys) by this flag.
    static uint Extended(ushort vk) => vk is (>= 0x21 and <= 0x28) or 0x2D or 0x2E or 0x5B or 0x5D or (>= 0xAD and <= 0xB3) ? KeyeventfExtendedkey : 0;

    static Input Vk(ushort vk, uint flags) => new() { Type = InputKeyboard, Keyboard = new KeybdInput { Vk = vk, Flags = flags } };

    static Input Key(char ch, uint flags) =>
        new() { Type = InputKeyboard, Keyboard = new KeybdInput { Scan = ch, Flags = KeyeventfUnicode | flags } };

    const uint InputKeyboard = 1;
    const uint KeyeventfExtendedkey = 0x0001;
    const uint KeyeventfKeyup = 0x0002;
    const uint KeyeventfUnicode = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    struct KeybdInput
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    // The native INPUT is a type tag and a union whose largest member (the mouse) is 32 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeybdInput Keyboard;
    }

    [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);
}
