using System.Globalization;
using System.Text;

namespace Meridian.Core.Analysis;

/// <summary>Physical dimension as exponents of the SI base quantities (length, mass, time, current, temperature, amount, luminosity).</summary>
public readonly record struct Dimension(int L, int M, int T, int I, int K, int N, int J)
{
    public static readonly Dimension None = default;
    public bool IsNone => this == None;

    public static Dimension operator *(Dimension a, Dimension b) => new(a.L + b.L, a.M + b.M, a.T + b.T, a.I + b.I, a.K + b.K, a.N + b.N, a.J + b.J);
    public static Dimension operator /(Dimension a, Dimension b) => new(a.L - b.L, a.M - b.M, a.T - b.T, a.I - b.I, a.K - b.K, a.N - b.N, a.J - b.J);
    public Dimension Pow(int p) => new(L * p, M * p, T * p, I * p, K * p, N * p, J * p);

    /// <summary>Square root, or null when an exponent is odd.</summary>
    public Dimension? Sqrt() =>
        new[] { L, M, T, I, K, N, J }.All(x => x % 2 == 0) ? new Dimension(L / 2, M / 2, T / 2, I / 2, K / 2, N / 2, J / 2) : null;

    /// <summary>SI base-unit spelling, e.g. "kg·m/s^2" ("1" when dimensionless).</summary>
    public override string ToString()
    {
        var num = new List<string>();
        var den = new List<string>();
        void Add(string sym, int e)
        {
            if (e == 0) return;
            var a = Math.Abs(e);
            (e > 0 ? num : den).Add(a == 1 ? sym : $"{sym}^{a}");
        }
        Add("kg", M); Add("m", L); Add("s", T); Add("A", I); Add("K", K); Add("mol", N); Add("cd", J);
        var top = num.Count == 0 ? "1" : string.Join("·", num);
        return den.Count == 0 ? top : $"{top}/{string.Join("·", den)}";
    }
}

/// <summary>A measurement unit: SI value = value × <see cref="Scale"/> + <see cref="Offset"/> (offset only for °C / °F).</summary>
public sealed record Unit(string Symbol, double Scale, Dimension Dimension, double Offset = 0)
{
    public double ToSi(double v) => v * Scale + Offset;
    public double FromSi(double si) => (si - Offset) / Scale;

    /// <summary>A difference (margin) expressed in this unit: offsets cancel.</summary>
    public double DeltaFromSi(double si) => si / Scale;
}

/// <summary>
/// Units of measure for verification criteria and value properties: SI base and derived units with prefixes, common
/// non-SI units (km/h, min, h, L, bar, psi, hp, lb, °C, °F, rpm, %) and products / quotients / powers of them such as
/// <c>m/s^2</c>, <c>kg·m²</c>, <c>W/(m*K)</c> or <c>km/L</c>. Parsing is case-sensitive (mW ≠ MW).
/// </summary>
public static class Units
{
    private static readonly Dimension Len = new(1, 0, 0, 0, 0, 0, 0), Mass = new(0, 1, 0, 0, 0, 0, 0), Time = new(0, 0, 1, 0, 0, 0, 0),
        Cur = new(0, 0, 0, 1, 0, 0, 0), Temp = new(0, 0, 0, 0, 1, 0, 0), Amount = new(0, 0, 0, 0, 0, 1, 0), Lum = new(0, 0, 0, 0, 0, 0, 1);

    private static readonly Dimension Force = Mass * Len / Time.Pow(2), Energy = Force * Len, Power = Energy / Time,
        Pressure = Force / Len.Pow(2), Charge = Cur * Time, Volt = Power / Cur, Freq = Dimension.None / Time;

    /// <summary>Symbols that take SI prefixes, with their scale and dimension.</summary>
    private static readonly Dictionary<string, (double Scale, Dimension Dim)> Prefixable = new(StringComparer.Ordinal)
    {
        ["m"] = (1, Len), ["g"] = (1e-3, Mass), ["s"] = (1, Time), ["A"] = (1, Cur), ["K"] = (1, Temp), ["mol"] = (1, Amount), ["cd"] = (1, Lum),
        ["N"] = (1, Force), ["J"] = (1, Energy), ["W"] = (1, Power), ["Pa"] = (1, Pressure), ["Hz"] = (1, Freq), ["V"] = (1, Volt),
        ["C"] = (1, Charge), ["Ω"] = (1, Volt / Cur), ["ohm"] = (1, Volt / Cur), ["L"] = (1e-3, Len.Pow(3)), ["l"] = (1e-3, Len.Pow(3)),
        ["Wh"] = (3600, Energy), ["Ah"] = (3600, Charge), ["bar"] = (1e5, Pressure), ["t"] = (1000, Mass), ["eV"] = (1.602176634e-19, Energy),
        ["rad"] = (1, Dimension.None), ["B"] = (8, Dimension.None), ["bit"] = (1, Dimension.None),
    };

    private static readonly Dictionary<string, double> Prefixes = new(StringComparer.Ordinal)
    {
        ["T"] = 1e12, ["G"] = 1e9, ["M"] = 1e6, ["k"] = 1e3, ["h"] = 1e2, ["da"] = 10, ["d"] = 1e-1, ["c"] = 1e-2, ["m"] = 1e-3,
        ["µ"] = 1e-6, ["μ"] = 1e-6, ["u"] = 1e-6, ["n"] = 1e-9, ["p"] = 1e-12,
    };

    /// <summary>Units looked up before prefixes are tried ("min" is minutes, not milli-inch; "cd" is candela).</summary>
    private static readonly Dictionary<string, Unit> Named = new(StringComparer.Ordinal)
    {
        ["1"] = new("1", 1, Dimension.None), ["-"] = new("1", 1, Dimension.None), ["%"] = new("%", 0.01, Dimension.None),
        ["ppm"] = new("ppm", 1e-6, Dimension.None),
        ["min"] = new("min", 60, Time), ["h"] = new("h", 3600, Time), ["hr"] = new("h", 3600, Time), ["d"] = new("d", 86400, Time),
        ["day"] = new("day", 86400, Time), ["yr"] = new("yr", 365.25 * 86400, Time), ["sec"] = new("s", 1, Time),
        ["in"] = new("in", 0.0254, Len), ["ft"] = new("ft", 0.3048, Len), ["yd"] = new("yd", 0.9144, Len), ["mi"] = new("mi", 1609.344, Len),
        ["nmi"] = new("nmi", 1852, Len), ["au"] = new("au", 1.495978707e11, Len),
        ["lb"] = new("lb", 0.45359237, Mass), ["lbm"] = new("lbm", 0.45359237, Mass), ["oz"] = new("oz", 0.028349523125, Mass),
        ["lbf"] = new("lbf", 4.4482216152605, Force), ["psi"] = new("psi", 6894.757293168, Pressure), ["atm"] = new("atm", 101325, Pressure),
        ["hp"] = new("hp", 745.69987158227, Power), ["PS"] = new("PS", 735.49875, Power),
        ["kph"] = new("km/h", 1000.0 / 3600, Len / Time), ["mph"] = new("mph", 1609.344 / 3600, Len / Time), ["kn"] = new("kn", 1852.0 / 3600, Len / Time),
        ["rpm"] = new("rpm", 1.0 / 60, Freq), ["gal"] = new("gal", 3.785411784e-3, Len.Pow(3)),
        ["g0"] = new("g0", 9.80665, Len / Time.Pow(2)), ["gn"] = new("gn", 9.80665, Len / Time.Pow(2)),
        ["deg"] = new("deg", Math.PI / 180, Dimension.None), ["°"] = new("°", Math.PI / 180, Dimension.None),
        ["degC"] = new("°C", 1, Temp, 273.15), ["°C"] = new("°C", 1, Temp, 273.15), ["℃"] = new("°C", 1, Temp, 273.15),
        ["degF"] = new("°F", 5.0 / 9, Temp, 273.15 - 32 * 5.0 / 9), ["°F"] = new("°F", 5.0 / 9, Temp, 273.15 - 32 * 5.0 / 9),
        ["cal"] = new("cal", 4.184, Energy), ["kcal"] = new("kcal", 4184, Energy),
    };

    /// <summary>Parses a unit expression; null when it is not a unit. Empty text parses as dimensionless "1".</summary>
    public static Unit? Parse(string? text) => TryParse(text, out var u, out _) ? u : null;

    public static bool TryParse(string? text, out Unit unit, out string? error)
    {
        unit = Named["1"];
        error = null;
        var s = (text ?? "").Trim();
        if (s.StartsWith('[') && s.EndsWith(']')) s = s[1..^1].Trim();
        if (s.Length == 0) return true;
        if (Named.TryGetValue(s, out var n)) { unit = n; return true; }
        try
        {
            var p = new UnitParser(s);
            var (scale, dim, affine) = p.Parse();
            if (affine != null && affine.Symbol.Length > 0 && p.Factors == 1) { unit = affine; return true; }
            unit = new Unit(s, scale, dim);
            return true;
        }
        catch (FormatException ex) { error = ex.Message; return false; }
    }

    /// <summary>True when both units measure the same kind of quantity (so values convert).</summary>
    public static bool Compatible(Unit a, Unit b) => a.Dimension == b.Dimension;

    /// <summary>Converts <paramref name="value"/> from one unit to another; null when the dimensions differ or a unit is unknown.</summary>
    public static double? Convert(double value, string from, string to)
    {
        if (Parse(from) is not { } f || Parse(to) is not { } t || !Compatible(f, t)) return null;
        return t.FromSi(f.ToSi(value));
    }

    /// <summary>Formats a number compactly (4 significant digits, invariant culture).</summary>
    public static string Format(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsInfinity(v)) return v > 0 ? "∞" : "-∞";
        var a = Math.Abs(v);
        if (a != 0 && (a >= 1e7 || a < 1e-4)) return v.ToString("0.###e0", CultureInfo.InvariantCulture);
        var digits = a == 0 ? 0 : Math.Clamp(4 - ((int)Math.Floor(Math.Log10(a)) + 1), 0, 10);
        return Math.Round(v, digits).ToString("0.##########", CultureInfo.InvariantCulture);
    }

    private static (double Scale, Dimension Dim)? Symbol(string sym)
    {
        if (Named.TryGetValue(sym, out var n)) return (n.Scale, n.Dimension);
        if (Prefixable.TryGetValue(sym, out var p)) return p;
        foreach (var (pre, f) in Prefixes)
            if (sym.Length > pre.Length && sym.StartsWith(pre, StringComparison.Ordinal) && Prefixable.TryGetValue(sym[pre.Length..], out var b))
                return (f * b.Scale, b.Dim);
        return null;
    }

    private sealed class UnitParser(string s)
    {
        private int _i;
        public int Factors { get; private set; }
        private Unit? _affine;

        public (double Scale, Dimension Dim, Unit? Affine) Parse()
        {
            var r = Product();
            Skip();
            if (_i < s.Length) throw new FormatException($"Unexpected '{s[_i]}' in unit '{s}'");
            return (r.Scale, r.Dim, _affine);
        }

        private void Skip() { while (_i < s.Length && s[_i] == ' ') _i++; }

        private (double Scale, Dimension Dim) Product()
        {
            var (scale, dim) = Factor();
            while (true)
            {
                Skip();
                if (_i >= s.Length) break;
                var c = s[_i];
                if (c is '*' or '·' or '.' or '⋅') { _i++; var (sc, d) = Factor(); scale *= sc; dim *= d; }
                else if (c == '/') { _i++; var (sc, d) = Factor(); scale /= sc; dim /= d; }
                else break;
            }
            return (scale, dim);
        }

        private (double Scale, Dimension Dim) Factor()
        {
            Skip();
            (double Scale, Dimension Dim) b;
            if (_i < s.Length && s[_i] == '(')
            {
                _i++;
                b = Product();
                Skip();
                if (_i >= s.Length || s[_i] != ')') throw new FormatException($"Missing ')' in unit '{s}'");
                _i++;
            }
            else
            {
                var st = _i;
                while (_i < s.Length && (char.IsLetter(s[_i]) || s[_i] is '°' or '%' or 'Ω' or 'µ' or 'μ' or '℃' or '_' || char.IsDigit(s[_i]) && _i > st && s[_i - 1] == 'g'))
                    _i++;
                if (_i == st && _i < s.Length && s[_i] == '1') { _i++; return (1, Dimension.None); }
                var sym = s[st.._i];
                if (sym.Length == 0) throw new FormatException($"Expected a unit in '{s}'");
                b = Symbol(sym) ?? throw new FormatException($"Unknown unit '{sym}'");
                Factors++;
                if (Named.TryGetValue(sym, out var n) && n.Offset != 0) _affine = n;
            }
            // Exponent: ^n, ^-n, ² or ³.
            var exp = 1;
            if (_i < s.Length && s[_i] == '^')
            {
                _i++;
                var st = _i;
                if (_i < s.Length && s[_i] is '-' or '+') _i++;
                while (_i < s.Length && char.IsDigit(s[_i])) _i++;
                if (!int.TryParse(s[st.._i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exp)) throw new FormatException($"Bad exponent in unit '{s}'");
                Factors++;   // a powered affine unit is a plain scale
            }
            else if (_i < s.Length && s[_i] is '²' or '³') { exp = s[_i] == '²' ? 2 : 3; _i++; Factors++; }
            return (Math.Pow(b.Scale, exp), b.Dim.Pow(exp));
        }
    }

    /// <summary>A unit symbol in canonical form for display ("km/h", "m/s^2").</summary>
    public static string Clean(string symbol)
    {
        var sb = new StringBuilder();
        foreach (var c in symbol.Trim()) if (c != ' ') sb.Append(c);
        return sb.ToString();
    }
}
