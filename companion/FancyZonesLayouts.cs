using System.Drawing;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <param name="Type">"priority-grid", "columns", "rows", "grid", "focus", "blank" or "custom".</param>
/// <param name="Custom">The custom-layouts.json entry when <paramref name="Type"/> is "custom".</param>
record ZoneLayout(string Type, int Count, bool ShowSpacing, int Spacing, JsonNode? Custom = null);

/// <summary>
/// FancyZones' zone rectangles, computed the way PowerToys does (FancyZonesLib/
/// LayoutConfigurator.cpp), relative to a monitor's work area.
/// </summary>
static class FancyZonesLayouts
{
    const int Multiplier = 10000;  // percentages are stored ×100

    public static List<Rectangle> Zones(ZoneLayout layout, int width, int height)
    {
        var s = layout.ShowSpacing ? layout.Spacing : 0;
        var n = layout.Count;
        if (n <= 0 && layout.Type != "custom") return [];
        return layout.Type switch
        {
            "columns" => Columns(width, height, n, s),
            "rows" => Rows(width, height, n, s),
            "grid" => Grid(width, height, n, s),
            "priority-grid" => n < PriorityGrid.Length + 1 ? FromGrid(PriorityGrid[n - 1], width, height, s) : Grid(width, height, n, s),
            "focus" => Focus(width, height, n),
            "custom" => Custom(layout.Custom, width, height),
            _ => [],
        };
    }

    static List<Rectangle> Columns(int w, int h, int n, int s)
    {
        var total = w - s * (n + 1);
        var zones = new List<Rectangle>();
        var left = s;
        for (var i = 0; i < n; i++)
        {
            var right = left + (i + 1) * total / n - i * total / n;
            zones.Add(Rectangle.FromLTRB(left, s, right, h - s));
            left = right + s;
        }
        return zones;
    }

    static List<Rectangle> Rows(int w, int h, int n, int s)
    {
        var total = h - s * (n + 1);
        var zones = new List<Rectangle>();
        var top = s;
        for (var i = 0; i < n; i++)
        {
            var bottom = top + (i + 1) * total / n - i * total / n;
            zones.Add(Rectangle.FromLTRB(s, top, w - s, bottom));
            top = bottom + s;
        }
        return zones;
    }

    static List<Rectangle> Focus(int w, int h, int n)
    {
        var step = n <= 1 ? 0 : 50;
        var size = new Size((int)(w * 0.4), (int)(h * 0.4));
        return Enumerable.Range(0, n).Select(i => new Rectangle(new Point(100 + i * step, 100 + i * step), size)).ToList();
    }

    record GridInfo(int[] Rows, int[] Columns, int[][] Cells);

    static List<Rectangle> Grid(int w, int h, int n, int s)
    {
        var rows = 1;
        while (n / rows >= rows) rows++;
        rows--;
        var columns = n / rows + (n % rows != 0 ? 1 : 0);
        var cells = new int[rows][];
        var index = 0;
        for (var r = 0; r < rows; r++)
        {
            cells[r] = new int[columns];
            for (var c = 0; c < columns; c++)
            {
                cells[r][c] = index++;
                if (index == n) index--;  // leftover cells merge into the last zone
            }
        }
        return FromGrid(new GridInfo(Even(rows), Even(columns), cells), w, h, s);
    }

    static int[] Even(int parts) =>
        Enumerable.Range(0, parts).Select(i => Multiplier * (i + 1) / parts - Multiplier * i / parts).ToArray();

    /// <summary>
    /// A grid's zones: each zone covers the cells carrying its index. Outer edges are inset
    /// by the spacing, shared edges by half of it on each side.
    /// </summary>
    static List<Rectangle> FromGrid(GridInfo grid, int w, int h, int s)
    {
        var (rowStart, rowEnd) = Edges(grid.Rows, h);
        var (colStart, colEnd) = Edges(grid.Columns, w);
        var rows = grid.Rows.Length;
        var columns = grid.Columns.Length;
        var zoneCount = grid.Cells.SelectMany(r => r).DefaultIfEmpty(-1).Max() + 1;
        var zones = new List<Rectangle>();
        for (var zone = 0; zone < zoneCount; zone++)
        {
            int r0 = int.MaxValue, r1 = -1, c0 = int.MaxValue, c1 = -1;
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < columns; c++)
                {
                    if (grid.Cells[r][c] != zone) continue;
                    r0 = Math.Min(r0, r); r1 = Math.Max(r1, r);
                    c0 = Math.Min(c0, c); c1 = Math.Max(c1, c);
                }
            }
            if (r1 < 0) continue;
            zones.Add(Rectangle.FromLTRB(
                colStart[c0] + (c0 == 0 ? s : s / 2),
                rowStart[r0] + (r0 == 0 ? s : s / 2),
                colEnd[c1] - (c1 == columns - 1 ? s : s / 2),
                rowEnd[r1] - (r1 == rows - 1 ? s : s / 2)));
        }
        return zones;
    }

    static (int[] Start, int[] End) Edges(int[] percents, int length)
    {
        var start = new int[percents.Length];
        var end = new int[percents.Length];
        long sum = 0;
        for (var i = 0; i < percents.Length; i++)
        {
            start[i] = (int)(sum * length / Multiplier);
            sum += percents[i];
            end[i] = (int)(sum * length / Multiplier);
        }
        return (start, end);
    }

    /// <summary>A layout from custom-layouts.json: "grid" (percentages and a cell map) or "canvas" (zones in reference pixels).</summary>
    static List<Rectangle> Custom(JsonNode? layout, int w, int h)
    {
        var info = layout?["info"];
        if (info == null) return [];
        switch ((string?)layout?["type"])
        {
            case "grid":
            {
                var rows = Ints(info["rows-percentage"]);
                var columns = Ints(info["columns-percentage"]);
                var cells = (info["cell-child-map"] as JsonArray ?? []).Select(Ints).ToArray();
                if (rows.Length == 0 || columns.Length == 0 || cells.Length != rows.Length || cells.Any(r => r.Length != columns.Length)) return [];
                var spacing = ((bool?)info["show-spacing"] ?? true) ? (int?)info["spacing"] ?? 16 : 0;
                return FromGrid(new GridInfo(rows, columns, cells), w, h, spacing);
            }
            case "canvas":
            {
                var refW = (double?)info["ref-width"] ?? w;
                var refH = (double?)info["ref-height"] ?? h;
                return (info["zones"] as JsonArray ?? []).Select(z =>
                {
                    var x = ((double?)z?["X"] ?? 0) * w / refW;
                    var y = ((double?)z?["Y"] ?? 0) * h / refH;
                    var zw = ((double?)z?["width"] ?? 0) * w / refW;
                    var zh = ((double?)z?["height"] ?? 0) * h / refH;
                    return Rectangle.FromLTRB((int)x, (int)y, (int)(x + zw), (int)(y + zh));
                }).ToList();
            }
            default:
                return [];
        }
    }

    static int[] Ints(JsonNode? node) => (node as JsonArray ?? []).Select(v => (int?)v ?? 0).ToArray();

    // PowerToys' predefined priority grids for 1 to 10 zones (the 11th entry in its table is never used).
    static readonly GridInfo[] PriorityGrid =
    [
        new([10000], [10000], [[0]]),
        new([10000], [6667, 3333], [[0, 1]]),
        new([10000], [2500, 5000, 2500], [[0, 1, 2]]),
        new([5000, 5000], [2500, 5000, 2500], [[0, 1, 2], [0, 1, 3]]),
        new([5000, 5000], [2500, 5000, 2500], [[0, 1, 2], [3, 1, 4]]),
        new([3333, 3334, 3333], [2500, 5000, 2500], [[0, 1, 2], [0, 1, 3], [4, 1, 5]]),
        new([3333, 3334, 3333], [2500, 5000, 2500], [[0, 1, 2], [3, 1, 4], [5, 1, 6]]),
        new([3333, 3334, 3333], [2500, 2500, 2500, 2500], [[0, 1, 2, 3], [4, 1, 2, 5], [6, 1, 2, 7]]),
        new([3333, 3334, 3333], [2500, 2500, 2500, 2500], [[0, 1, 2, 3], [4, 1, 2, 5], [6, 1, 7, 8]]),
        new([3333, 3334, 3333], [2500, 2500, 2500, 2500], [[0, 1, 2, 3], [4, 1, 5, 6], [7, 1, 8, 9]]),
    ];
}
