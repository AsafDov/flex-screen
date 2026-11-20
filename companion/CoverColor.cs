namespace FlexScreen.Companion;

/// <summary>
/// The color an album cover is known for, for Now Playing on the deck: the hue its vivid
/// pixels share most, brought to a saturation and lightness that read on the deck's dark
/// cards. 0 for a cover that's grey, black and white, or nearly so: the deck then keeps its
/// theme's own accent.
/// </summary>
static class CoverColor
{
    const int Bins = 24;  // 15° of hue each

    /// <param name="bgra">The cover's pixels, 8-bit BGRA (the alpha is ignored).</param>
    /// <returns>0xRRGGBB, or 0.</returns>
    public static int Pick(byte[] bgra)
    {
        var weight = new double[Bins];
        var sumR = new double[Bins];
        var sumG = new double[Bins];
        var sumB = new double[Bins];
        var pixels = bgra.Length / 4;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            double b = bgra[i] / 255.0, g = bgra[i + 1] / 255.0, r = bgra[i + 2] / 255.0;
            var max = Math.Max(r, Math.Max(g, b));
            var chroma = max - Math.Min(r, Math.Min(g, b));
            if (max < 0.15 || chroma < 0.12) continue;  // near black, or grey
            var saturation = chroma / max;
            if (saturation < 0.25) continue;
            var bin = (int)(Hue(r, g, b, max, chroma) / 360 * Bins) % Bins;
            // Vivid, bright pixels count most: a red title on a brown cover still says red.
            var w = saturation * saturation * max;
            weight[bin] += w;
            sumR[bin] += r * w;
            sumG[bin] += g * w;
            sumB[bin] += b * w;
        }

        // A hue that straddles two bins counts with its neighbours.
        int best = -1;
        double bestWeight = 0;
        for (var k = 0; k < Bins; k++)
        {
            var around = weight[k] + 0.5 * (weight[(k + Bins - 1) % Bins] + weight[(k + 1) % Bins]);
            if (around > bestWeight) (best, bestWeight) = (k, around);
        }
        if (best < 0 || bestWeight < pixels * 0.02) return 0;  // no color worth the name

        double total = 0, red = 0, green = 0, blue = 0;
        foreach (var k in new[] { (best + Bins - 1) % Bins, best, (best + 1) % Bins })
        {
            total += weight[k];
            red += sumR[k];
            green += sumG[k];
            blue += sumB[k];
        }
        var (h, s, l) = ToHsl(red / total, green / total, blue / total);
        s = Math.Clamp(s, 0.45, 0.85);
        l = Math.Clamp(l, 0.55, 0.72);
        // Blues and purples look darker than yellows at the same lightness: raise them until
        // they read on a dark card.
        var (r2, g2, b2) = ToRgb(h, s, l);
        while (Luminance(r2, g2, b2) < 0.22 && l < 0.8)
        {
            l += 0.02;
            (r2, g2, b2) = ToRgb(h, s, l);
        }
        return (Byte(r2) << 16) | (Byte(g2) << 8) | Byte(b2);
    }

    static double Hue(double r, double g, double b, double max, double chroma)
    {
        double h = max == r ? (g - b) / chroma : max == g ? 2 + (b - r) / chroma : 4 + (r - g) / chroma;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var chroma = max - min;
        if (chroma <= 0) return (0, 0, l);
        var s = chroma / (1 - Math.Abs(2 * l - 1));
        return (Hue(r, g, b, max, chroma), s, l);
    }

    static (double R, double G, double B) ToRgb(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = (h / 60) switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return (r + m, g + m, b + m);
    }

    // Relative luminance (sRGB), 0 black to 1 white.
    static double Luminance(double r, double g, double b)
    {
        static double Linear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
    }

    static int Byte(double c) => (int)Math.Round(Math.Clamp(c, 0, 1) * 255);
}
