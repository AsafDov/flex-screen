namespace FlexScreen.Companion;

/// <summary>
/// What a QMK keycode (v6 and later, which Vial protocol 6 uses) says on the deck's layer
/// cheat sheet: "top\nmain\nbottom", or just "main". The top line is a modifier combined
/// with the key (Ctrl for Ctrl+C), the bottom line what holding it does (NAV for a
/// layer-tap Space, Ctrl for a mod-tap). Arrows are LVGL's symbol glyphs; keys are named
/// as a US layout types them, as QMK sends them.
/// </summary>
static class KeyLabels
{
    const string Left = "", Right = "", Up = "", Down = "";
    const string Mute = "", VolDown = "", VolUp = "";
    const string Play = "", Prev = "", Next = "", Stop = "";

    static readonly string[] Basic = BuildBasic();

    static readonly Dictionary<char, string> Shifted = new()
    {
        ['1'] = "!", ['2'] = "@", ['3'] = "#", ['4'] = "$", ['5'] = "%", ['6'] = "^", ['7'] = "&", ['8'] = "*",
        ['9'] = "(", ['0'] = ")", ['-'] = "_", ['='] = "+", ['['] = "{", [']'] = "}", ['\\'] = "|", [';'] = ":",
        ['\''] = "\"", ['`'] = "~", [','] = "<", ['.'] = ">", ['/'] = "?",
    };

    // Ctrl + a letter that does the same thing everywhere.
    static readonly Dictionary<string, string> Shortcuts = new()
    {
        ["C"] = "Copy", ["V"] = "Paste", ["X"] = "Cut", ["Z"] = "Undo", ["Y"] = "Redo", ["S"] = "Save",
        ["A"] = "All", ["F"] = "Find",
    };

    /// <param name="layerName">A layer's name, from the settings ("NAV").</param>
    public static string Of(ushort kc, Func<int, string> layerName, IReadOnlyList<string> custom)
    {
        switch (kc)
        {
            case <= 0x00FF:
                return Basic[kc];
            case <= 0x1FFF:  // a key with modifiers held: LSFT(KC_1) is "!", LCTL(KC_C) is Copy
            {
                var mods = (kc >> 8) & 0x1F;
                var key = Basic[kc & 0xFF];
                if ((mods & 0x0F) == 0x02 && key.Length == 1 && Shifted.TryGetValue(key[0], out var symbol)) return symbol;
                if ((mods & 0x0F) == 0x01 && Shortcuts.TryGetValue(key, out var name)) return name;
                if ((mods & 0x0F) == 0x03 && key == "Z") return "Redo";
                // Windows' input language switch (Left Alt+Shift, or Win+Space).
                if (((mods & 0x0F) == 0x02 && key == "Alt") || ((mods & 0x0F) == 0x04 && key == "Shift") || ((mods & 0x0F) == 0x08 && key == "Space")) return "Lang";
                return Lines(Mods(mods), Middle(key), "");
            }
            case <= 0x3FFF:  // mod-tap: the key on a tap, the modifier held
                return Lines("", Middle(Basic[kc & 0xFF]), Mods((kc >> 8) & 0x1F));
            case <= 0x4FFF:  // layer-tap
                return Lines("", Middle(Basic[kc & 0xFF]), layerName((kc >> 8) & 0x0F));
            case <= 0x51FF:  // layer-mod: a layer with modifiers, while held
                return Lines(Mods(kc & 0x1F), layerName((kc >> 5) & 0x0F), "hold");
        }
        var n = kc & 0x1F;
        return kc switch
        {
            < 0x5220 => Lines("", layerName(n), "go to"),
            < 0x5240 => Lines("", layerName(n), "hold"),
            < 0x5260 => Lines("", layerName(n), "default"),
            < 0x5280 => Lines("", layerName(n), "toggle"),
            < 0x52A0 => Lines("", layerName(n), "one shot"),
            < 0x52C0 => Lines("", Mods(kc & 0x1F), "one shot"),
            < 0x52E0 => Lines("", layerName(n), "tap-tog"),
            < 0x5300 => Lines("", layerName(n), "default"),
            >= 0x5700 and <= 0x57FF => $"TD{kc & 0xFF}",
            >= 0x7700 and <= 0x777F => $"M{kc & 0x7F}",
            >= 0x7800 and <= 0x78FF => Lighting(kc),
            0x7C00 => "Boot",
            0x7C01 => "Reboot",
            0x7C03 => Lines("", "Clear", "EEPROM"),
            0x7C16 => Lines("", "Esc", "`"),
            0x7C73 => Lines("", "Caps", "word"),
            0x7C77 => layerName(1),
            0x7C78 => layerName(2),
            0x7C79 => "Repeat",
            0x7C7A => Lines("", "Repeat", "alt"),
            >= 0x7E00 and <= 0x7FFF when kc - 0x7E00 < custom.Count && custom[kc - 0x7E00].Length > 0 => custom[kc - 0x7E00],
            >= 0x7E00 and <= 0x7FFF => $"U{kc - 0x7E00}",
            _ => "?",
        };
    }

    static string Lines(string top, string main, string bottom) =>
        top.Length == 0 && bottom.Length == 0 ? main : $"{top}\n{main}\n{bottom}";

    // A key name used as the middle line of a three-line label: a combined one's own lines go.
    static string Middle(string label) => label.Contains('\n') ? label.Split('\n')[1] : label;

    static string Mods(int mods)
    {
        var right = (mods & 0x10) != 0;
        var names = new List<string>();
        if ((mods & 0x01) != 0) names.Add("Ctrl");
        if ((mods & 0x02) != 0) names.Add("Shift");
        if ((mods & 0x04) != 0) names.Add(right ? "AltGr" : "Alt");
        if ((mods & 0x08) != 0) names.Add("Win");
        if (names.Count == 4) return "Hyper";
        if (names.Count == 3 && (mods & 0x08) == 0) return "Meh";
        return names.Count == 1 ? names[0] : string.Join("+", names.Select(n => n[..1]));
    }

    static string Lighting(ushort kc) => kc switch
    {
        0x7802 => Lines("", "Light", "toggle"),
        0x7803 => "Light-",
        0x7804 => "Light+",
        0x7820 => Lines("", "RGB", "toggle"),
        0x7821 => "Mode+",
        0x7822 => "Mode-",
        0x7823 => "Hue+",
        0x7824 => "Hue-",
        0x7825 => "Sat+",
        0x7826 => "Sat-",
        0x7827 => "Bri+",
        0x7828 => "Bri-",
        0x7829 => "Spd+",
        0x782A => "Spd-",
        _ => "RGB",
    };

    static string[] BuildBasic()
    {
        var b = Enumerable.Repeat("?", 256).ToArray();
        b[0x00] = "";
        b[0x01] = "";  // transparent: the cheat sheet shows what's underneath
        for (var i = 0; i < 26; i++) b[0x04 + i] = ((char)('A' + i)).ToString();
        for (var i = 0; i < 9; i++) b[0x1E + i] = (i + 1).ToString();
        b[0x27] = "0";
        string[] named = ["Enter", "Esc", "Bksp", "Tab", "Space", "-", "=", "[", "]", "\\", "#", ";", "'", "`", ",", ".", "/", "Caps"];
        for (var i = 0; i < named.Length; i++) b[0x28 + i] = named[i];
        for (var i = 0; i < 12; i++) b[0x3A + i] = $"F{i + 1}";
        string[] nav = ["PrtSc", "ScrLk", "Pause", "Ins", "Home", "PgUp", "Del", "End", "PgDn", Right, Left, Down, Up, "NumLk"];
        for (var i = 0; i < nav.Length; i++) b[0x46 + i] = nav[i];
        string[] pad = ["/", "*", "-", "+", "Enter", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "."];
        for (var i = 0; i < pad.Length; i++) b[0x54 + i] = Lines("", pad[i], "pad");
        b[0x64] = "\\";
        b[0x65] = "Menu";
        b[0x66] = "Power";
        b[0x67] = Lines("", "=", "pad");
        for (var i = 0; i < 12; i++) b[0x68 + i] = $"F{13 + i}";
        b[0x74] = "Exec";
        b[0x75] = "Help";
        b[0x76] = "Menu";
        b[0x77] = "Select";
        b[0x78] = "Stop";
        b[0x79] = "Again";
        b[0x7A] = "Undo";
        b[0x7B] = "Cut";
        b[0x7C] = "Copy";
        b[0x7D] = "Paste";
        b[0x7E] = "Find";
        b[0x7F] = Mute;
        b[0x80] = VolUp;
        b[0x81] = VolDown;
        for (var i = 0; i < 9; i++) b[0x90 + i] = $"Lang{i + 1}";
        string[] media =
        [
            "Power", "Sleep", "Wake", Mute, VolUp, VolDown, Next, Prev, Stop, Play, "Media", "Eject", "Mail", "Calc", "PC",
            "Search", "Web", "Back", "Fwd", "Stop", "Reload", "Favs", "FFwd", "Rew", "Bri+", "Bri-", "Panel", "Assist", "Tasks", "Apps",
        ];
        for (var i = 0; i < media.Length; i++) b[0xA5 + i] = media[i];
        string[] mouse = [Up, Down, Left, Right];
        for (var i = 0; i < 4; i++) b[0xCD + i] = Lines("", mouse[i], "mouse");
        string[] clicks = ["Click", "RClick", "MClick", "Btn4", "Btn5", "Btn6", "Btn7", "Btn8"];
        for (var i = 0; i < clicks.Length; i++) b[0xD1 + i] = clicks[i];
        for (var i = 0; i < 4; i++) b[0xD9 + i] = Lines("", mouse[i], "wheel");
        for (var i = 0; i < 3; i++) b[0xDD + i] = $"Acc{i}";
        string[] mods = ["Ctrl", "Shift", "Alt", "Win", "Ctrl", "Shift", "AltGr", "Win"];
        for (var i = 0; i < mods.Length; i++) b[0xE0 + i] = mods[i];
        return b;
    }
}
