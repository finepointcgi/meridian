using System.Globalization;

namespace Meridian.Web.Settings;

/// <summary>WCAG contrast arithmetic for theme colours (sRGB hex and rgba() over a background).</summary>
public static class ColorMath
{
    public readonly record struct Rgb(double R, double G, double B)
    {
        public string Hex => $"#{To(R)}{To(G)}{To(B)}";
        private static string To(double v) => ((int)Math.Round(Math.Clamp(v, 0, 255))).ToString("x2", CultureInfo.InvariantCulture);
    }

    public static Rgb Parse(string hex)
    {
        var h = hex.Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(c => $"{c}{c}"));
        if (h.Length < 6) throw new FormatException($"Not a hex colour: {hex}");
        int P(int i) => int.Parse(h.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new(P(0), P(2), P(4));
    }

    /// <summary>A CSS colour (#hex or rgb/rgba(...)) flattened onto an opaque background.</summary>
    public static Rgb Resolve(string css, Rgb background)
    {
        css = css.Trim();
        if (css.StartsWith('#')) return Parse(css);
        var open = css.IndexOf('(');
        var parts = css[(open + 1)..css.LastIndexOf(')')].Split(',', StringSplitOptions.TrimEntries);
        double N(int i) => double.Parse(parts[i], CultureInfo.InvariantCulture);
        var a = parts.Length > 3 ? N(3) : 1;
        return Mix(new Rgb(N(0), N(1), N(2)), background, a);
    }

    /// <summary><paramref name="a"/> painted with opacity <paramref name="amount"/> over <paramref name="b"/>.</summary>
    public static Rgb Mix(Rgb a, Rgb b, double amount) =>
        new(a.R * amount + b.R * (1 - amount), a.G * amount + b.G * (1 - amount), a.B * amount + b.B * (1 - amount));

    public static double Luminance(Rgb c)
    {
        static double L(double v) { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    public static double Contrast(Rgb a, Rgb b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static double Contrast(string a, string b) => Contrast(Parse(a), Parse(b));

    /// <summary>
    /// The colour nearest to <paramref name="color"/> (mixed toward black or white, whichever the background calls for)
    /// that reaches <paramref name="ratio"/> against <paramref name="background"/>.
    /// </summary>
    public static string EnsureContrast(string color, string background, double ratio = 4.5)
    {
        var c = Parse(color);
        var bg = Parse(background);
        if (Contrast(c, bg) >= ratio) return c.Hex;
        var toward = Luminance(bg) > 0.4 ? new Rgb(0, 0, 0) : new Rgb(255, 255, 255);
        for (var t = 0.02; t <= 1.0001; t += 0.02)
        {
            var m = Mix(toward, c, t);
            if (Contrast(m, bg) >= ratio) return m.Hex;
        }
        return toward.Hex;
    }

    /// <summary>"r, g, b" for rgba(var(--x-rgb), a) tokens.</summary>
    public static string Channels(string hex)
    {
        var c = Parse(hex);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)c.R}, {(int)c.G}, {(int)c.B}");
    }
}
