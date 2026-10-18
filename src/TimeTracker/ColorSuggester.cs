namespace TimeTracker;

/// <summary>
/// Suggests a color for a new task: candidates at a fixed OKLCH lightness and chroma
/// (readable with white text, never gray), keeping the one whose smallest perceptual
/// distance (OKLab) to the colors to avoid is the largest.
/// </summary>
static class ColorSuggester
{
    const double Lightness = 0.55, Chroma = 0.12, DefaultHue = 255;

    record Candidate(Color Rgb, double Hue, double L, double A, double B);

    static readonly List<Candidate> Candidates = BuildCandidates();

    public static Color Suggest(IEnumerable<Color> avoid)
    {
        var used = avoid.Select(ToOklab).ToList();
        if (used.Count == 0)
            return Candidates.MinBy(c => Math.Abs(c.Hue - DefaultHue))!.Rgb;
        return Candidates.MaxBy(c => used.Min(u => Distance(c, u)))!.Rgb;
    }

    static double Distance(Candidate c, (double L, double A, double B) u) =>
        Math.Sqrt(Math.Pow(c.L - u.L, 2) + Math.Pow(c.A - u.A, 2) + Math.Pow(c.B - u.B, 2));

    static List<Candidate> BuildCandidates()
    {
        var list = new List<Candidate>();
        for (int hue = 0; hue < 360; hue += 5)
        {
            double a = Chroma * Math.Cos(hue * Math.PI / 180), b = Chroma * Math.Sin(hue * Math.PI / 180);
            var (r, g, bl) = OklabToLinear(Lightness, a, b);
            if (new[] { r, g, bl }.Any(v => v < 0 || v > 1)) continue; // outside sRGB
            var rgb = Color.FromArgb(ToByte(r), ToByte(g), ToByte(bl));
            if (ContrastWithWhite(rgb) < 4.5) continue;
            var lab = ToOklab(rgb);
            list.Add(new Candidate(rgb, hue, lab.L, lab.A, lab.B));
        }
        return list;
    }

    public static double ContrastWithWhite(Color c)
    {
        double y = 0.2126 * ToLinear(c.R) + 0.7152 * ToLinear(c.G) + 0.0722 * ToLinear(c.B);
        return 1.05 / (y + 0.05);
    }

    static (double L, double A, double B) ToOklab(Color c)
    {
        double r = ToLinear(c.R), g = ToLinear(c.G), b = ToLinear(c.B);
        double l = Math.Cbrt(0.4122214708 * r + 0.5363288310 * g + 0.0514492058 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    static (double R, double G, double B) OklabToLinear(double L, double a, double b)
    {
        double l = Math.Pow(L + 0.3963377774 * a + 0.2158037573 * b, 3);
        double m = Math.Pow(L - 0.1055613458 * a - 0.0638541728 * b, 3);
        double s = Math.Pow(L - 0.0894841775 * a - 1.2914855480 * b, 3);
        return (4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
                -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
                -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }

    static double ToLinear(byte v)
    {
        double c = v / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    static int ToByte(double linear)
    {
        double c = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (int)Math.Round(Math.Clamp(c, 0, 1) * 255);
    }
}
