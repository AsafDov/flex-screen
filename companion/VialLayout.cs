using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <param name="X">The key's centre, in key widths from the left.</param>
/// <param name="Y">The key's centre, in key heights from the top.</param>
record KeyPlace(int Row, int Col, double X, double Y, double W, double H);

/// <summary>
/// A Vial keyboard's own description of itself (its vial.json, which the keyboard carries):
/// the matrix size, where each key sits, and the names of custom keycodes. Key positions
/// come from the keymap's KLE layout, with rotated keys (Corne thumbs) placed at their
/// rotated centre, and layout options resolved the way Vial shows them.
/// </summary>
sealed record VialLayout(int Rows, int Cols, IReadOnlyList<KeyPlace> Keys, IReadOnlyList<string> CustomKeycodes)
{
    /// <param name="options">VIA's layout options value: which choice each option has.</param>
    public static VialLayout Parse(string json, uint options)
    {
        var root = JsonNode.Parse(json) ?? throw new InvalidDataException("empty definition");
        var rows = (int?)root["matrix"]?["rows"] ?? 0;
        var cols = (int?)root["matrix"]?["cols"] ?? 0;
        var kle = root["layouts"]?["keymap"] as JsonArray ?? throw new InvalidDataException("no layout");
        var choices = Choices(root["layouts"]?["labels"] as JsonArray, options);
        var custom = (root["customKeycodes"] as JsonArray)?
            .Select(k => (string?)k?["shortName"] ?? (string?)k?["name"] ?? "")
            .ToArray() ?? [];

        // Keys of a layout option show only for its chosen value, moved to where the first
        // value's keys are (KLE draws the alternatives off to the side).
        var all = Kle(kle);
        var keys = new List<KeyPlace>();
        foreach (var group in all.GroupBy(k => k.Option))
        {
            if (group.Key < 0)
            {
                keys.AddRange(group.Select(k => k.Place));
                continue;
            }
            var chosen = group.Key < choices.Count ? choices[group.Key] : 0;
            var first = group.Where(k => k.Choice == 0).ToList();
            var shown = group.Where(k => k.Choice == chosen).ToList();
            if (shown.Count == 0) continue;
            double dx = 0, dy = 0;
            if (chosen != 0 && first.Count > 0)
            {
                dx = first.Min(k => k.Place.X - k.Place.W / 2) - shown.Min(k => k.Place.X - k.Place.W / 2);
                dy = first.Min(k => k.Place.Y - k.Place.H / 2) - shown.Min(k => k.Place.Y - k.Place.H / 2);
            }
            keys.AddRange(shown.Select(k => k.Place with { X = k.Place.X + dx, Y = k.Place.Y + dy }));
        }
        keys.RemoveAll(k => k.Row >= rows || k.Col >= cols);
        return new VialLayout(rows, cols, keys, custom);
    }

    /// <summary>
    /// Each option's chosen value. VIA packs them with the last option in the lowest bits:
    /// a plain option (a string) takes one bit, a list of values enough bits for them.
    /// </summary>
    static List<int> Choices(JsonArray? labels, uint options)
    {
        var result = new List<int>();
        if (labels == null) return result;
        var widths = labels.Select(l => l is JsonArray values ? (int)Math.Ceiling(Math.Log2(Math.Max(1, values.Count - 1))) : 1).ToList();
        var choices = new int[widths.Count];
        for (var i = widths.Count - 1; i >= 0; i--)
        {
            choices[i] = (int)(options & ((1u << widths[i]) - 1));
            options >>= widths[i];
        }
        result.AddRange(choices);
        return result;
    }

    record KleKey(KeyPlace Place, int Option, int Choice);

    /// <summary>
    /// The keyboard-layout-editor format: rows of keys, each a legend string ("row,col" for
    /// Vial, and "option,choice" as the fourth legend) after optional property objects that
    /// move the cursor (x, y), size the next key (w, h) or rotate the cluster (r, rx, ry).
    /// </summary>
    static List<KleKey> Kle(JsonArray rows)
    {
        var keys = new List<KleKey>();
        double x = 0, y = 0, w = 1, h = 1, r = 0, rx = 0, ry = 0;
        var decal = false;
        foreach (var row in rows)
        {
            if (row is not JsonArray items) continue;  // a leading object is the keyboard's metadata
            foreach (var item in items)
            {
                if (item is JsonObject o)
                {
                    if (o["r"] != null) r = (double)o["r"]!;
                    if (o["rx"] != null)
                    {
                        rx = (double)o["rx"]!;
                        x = rx;
                        y = ry;
                    }
                    if (o["ry"] != null)
                    {
                        ry = (double)o["ry"]!;
                        x = rx;
                        y = ry;
                    }
                    x += (double?)o["x"] ?? 0;
                    y += (double?)o["y"] ?? 0;
                    if (o["w"] != null) w = (double)o["w"]!;
                    if (o["h"] != null) h = (double)o["h"]!;
                    if (o["d"] != null) decal = (bool)o["d"]!;
                    continue;
                }
                var legends = ((string?)item ?? "").Split('\n');
                if (!decal && Pair(legends[0]) is { } position)
                {
                    var (keyRow, keyCol) = position;
                    var (option, choice) = legends.Length > 3 && Pair(legends[3]) is { } chosen ? chosen : (-1, 0);
                    // The centre, turned r degrees (clockwise) about (rx, ry).
                    var angle = r * Math.PI / 180;
                    double cx = x + w / 2 - rx, cy = y + h / 2 - ry;
                    var place = new KeyPlace(keyRow, keyCol,
                        rx + cx * Math.Cos(angle) - cy * Math.Sin(angle),
                        ry + cx * Math.Sin(angle) + cy * Math.Cos(angle), w, h);
                    keys.Add(new KleKey(place, option, choice));
                }
                x += w;
                w = h = 1;
                decal = false;
            }
            y += 1;
            x = rx;
        }
        return keys;
    }

    static (int, int)? Pair(string text)
    {
        var parts = text.Split(',');
        return parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b) ? (a, b) : null;
    }
}
