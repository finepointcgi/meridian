using System.Globalization;
using System.Net;
using System.Text;
using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization.Fmi;

namespace Meridian.Web.Services;

/// <summary>
/// FMI co-simulation settings. Co-simulation runs the FMU's native code on the machine that hosts Meridian (in a child
/// process: crash isolation, not a sandbox), so on a team server it is off unless <c>Meridian:Fmi:Enabled</c> is true,
/// and only server administrators may run FMUs or attach .fmu files (<c>Meridian:Fmi:AllowProjectOwners=true</c> also lets
/// the owner of the open project; any user can create and own a project). The desktop app (local mode) has it on by
/// default. See docs/FMI.md.
/// </summary>
public sealed class FmiCoSimSettings
{
    public bool Enabled { get; init; }
    public bool LocalMode { get; init; }
    public bool AllowProjectOwners { get; init; }
    public required FmuStore Store { get; init; }
    public required FmuRuntimeOptions Runtime { get; init; }

    public static FmiCoSimSettings From(IConfiguration? c, MeridianHostInfo? host)
    {
        var local = host?.LocalMode ?? false;
        var enabled = bool.TryParse(c?["Meridian:Fmi:Enabled"], out var e) ? e : local;
        var store = c?["Meridian:Fmi:StorePath"] is { Length: > 0 } sp ? sp
            : host != null ? Path.Combine(host.DataPath, "fmus") : Path.Combine(Path.GetTempPath(), "meridian-fmus");
        var timeout = double.TryParse(c?["Meridian:Fmi:CallTimeoutSeconds"], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : 60;
        return new FmiCoSimSettings
        {
            Enabled = enabled,
            LocalMode = local,
            AllowProjectOwners = string.Equals(c?["Meridian:Fmi:AllowProjectOwners"], "true", StringComparison.OrdinalIgnoreCase),
            Store = new FmuStore(store),
            Runtime = new FmuRuntimeOptions { HostPath = c?["Meridian:Fmi:HostPath"] is { Length: > 0 } hp ? hp : null, CallTimeout = TimeSpan.FromSeconds(timeout) },
        };
    }

    /// <summary>Why this user cannot run FMUs here, or null when they can.</summary>
    public string? WhyNotAllowed(Workspace ws) =>
        !Enabled ? "Running FMUs is turned off on this server. An administrator can enable it with Meridian:Fmi:Enabled = true (it runs the FMUs' native code on the server; see docs/FMI.md)."
        : LocalMode ? (ws.Role != ProjectRole.Owner ? "Only project owners can run FMUs (they execute native code on this computer)." : null)
        : ws.IsServerAdmin || AllowProjectOwners && ws.Role == ProjectRole.Owner ? null
        : AllowProjectOwners ? "Only server administrators and project owners can run FMUs on this server (they execute native code on the server)."
        : "Only server administrators can run FMUs on this server (they execute native code on the server).";

    /// <summary>Re-reads the user's administrator state (it can change without a new sign-in) before a native-code action.</summary>
    public async Task RefreshAdminAsync(Workspace ws, IServiceProvider services)
    {
        if (LocalMode || ws.User is not { } user || services.GetService(typeof(Admin.AdminAccess)) is not Admin.AdminAccess access) return;
        ws.IsServerAdmin = await access.StateAsync(user.Id, fresh: true) is { IsAdmin: true, Disabled: false };
    }

    /// <summary>The stored .fmu for an imported FMU block, or null.</summary>
    public string? FmuPath(Element block) => Store.Find(block.Get(FieldKeys.TagPrefix + "fmi.sha256"));
}

/// <summary>Multi-series line chart for co-simulation results (inline SVG, themed through the app's CSS variables).</summary>
public static class CoSimChart
{
    public const int Width = 780, Height = 280, Left = 58, Right = 14, Top = 12, Bottom = 30;
    public const int PlotWidth = Width - Left - Right, PlotHeight = Height - Top - Bottom;

    public static readonly string[] Palette = ["#4e79a7", "#f28e2b", "#e15759", "#59a14f", "#b07aa1", "#76b7b2", "#edc948", "#9c755f", "#ff9da7", "#bab0ac"];

    public sealed record Line(string Name, string Color, IReadOnlyList<double> Y);

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string E(string s) => WebUtility.HtmlEncode(s);
    public static string N(double v) => double.IsNaN(v) ? "NaN" : Math.Abs(v) >= 1e5 || Math.Abs(v) < 1e-3 && v != 0 ? v.ToString("0.###e0", CultureInfo.InvariantCulture) : v.ToString("G5", CultureInfo.InvariantCulture);

    /// <summary>Data time at a horizontal pixel offset within the chart.</summary>
    public static double TimeAt(double offsetX, double x0, double x1) => x0 + Math.Clamp((offsetX - Left) / PlotWidth, 0, 1) * (x1 - x0);

    public static int NearestIndex(IReadOnlyList<double> time, double t)
    {
        if (time.Count == 0) return -1;
        var lo = 0; var hi = time.Count - 1;
        while (hi - lo > 1) { var mid = (lo + hi) / 2; if (time[mid] < t) lo = mid; else hi = mid; }
        return Math.Abs(time[lo] - t) <= Math.Abs(time[hi] - t) ? lo : hi;
    }

    public static string Svg(IReadOnlyList<double> time, IReadOnlyList<Line> lines, double x0, double x1, int hover = -1,
        double? threshold = null, (double From, double To)? selection = null)
    {
        var sb = new StringBuilder($"<svg class=\"chart\" width=\"{Width}\" height=\"{Height}\" viewBox=\"0 0 {Width} {Height}\" xmlns=\"http://www.w3.org/2000/svg\" font-family=\"inherit\" font-size=\"10.5\" style=\"pointer-events:none; display:block\">");
        if (time.Count == 0 || lines.Count == 0 || !(x1 > x0)) { sb.Append("</svg>"); return sb.ToString(); }
        var i0 = Math.Max(0, NearestIndex(time, x0) - 1);
        var i1 = Math.Min(time.Count - 1, NearestIndex(time, x1) + 1);

        // Y range over the visible window (and the threshold).
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var l in lines)
            for (var i = i0; i <= i1 && i < l.Y.Count; i++)
                if (double.IsFinite(l.Y[i])) { lo = Math.Min(lo, l.Y[i]); hi = Math.Max(hi, l.Y[i]); }
        if (threshold is { } th && double.IsFinite(th)) { lo = Math.Min(lo, th); hi = Math.Max(hi, th); }
        if (!double.IsFinite(lo)) { lo = 0; hi = 1; }
        if (hi - lo < 1e-12) { lo -= 0.5 * Math.Max(1e-9, Math.Abs(lo)); hi += 0.5 * Math.Max(1e-9, Math.Abs(hi)); if (hi - lo < 1e-12) { lo -= 1; hi += 1; } }
        var pad = (hi - lo) * 0.05; lo -= pad; hi += pad;

        double X(double t) => Left + (t - x0) / (x1 - x0) * PlotWidth;
        double Y(double v) => Top + (1 - (v - lo) / (hi - lo)) * PlotHeight;

        // Grid and axes.
        foreach (var v in Ticks(lo, hi, 5))
        {
            sb.Append($"<line x1=\"{Left}\" y1=\"{F(Y(v))}\" x2=\"{Left + PlotWidth}\" y2=\"{F(Y(v))}\" stroke=\"var(--border)\" stroke-dasharray=\"2 3\"/>");
            sb.Append($"<text x=\"{Left - 6}\" y=\"{F(Y(v) + 3.5)}\" text-anchor=\"end\" fill=\"var(--text-3)\">{N(v)}</text>");
        }
        foreach (var t in Ticks(x0, x1, 7))
            sb.Append($"<text x=\"{F(X(t))}\" y=\"{Height - 10}\" text-anchor=\"middle\" fill=\"var(--text-3)\">{N(t)}</text>");
        sb.Append($"<line x1=\"{Left}\" y1=\"{Top + PlotHeight}\" x2=\"{Left + PlotWidth}\" y2=\"{Top + PlotHeight}\" stroke=\"var(--border-strong)\"/>");
        sb.Append($"<line x1=\"{Left}\" y1=\"{Top}\" x2=\"{Left}\" y2=\"{Top + PlotHeight}\" stroke=\"var(--border-strong)\"/>");
        sb.Append($"<text x=\"{Left + PlotWidth}\" y=\"{Height - 1}\" text-anchor=\"end\" fill=\"var(--text-3)\">time [s]</text>");

        if (threshold is { } th2 && double.IsFinite(th2))
            sb.Append($"<line x1=\"{Left}\" y1=\"{F(Y(th2))}\" x2=\"{Left + PlotWidth}\" y2=\"{F(Y(th2))}\" stroke=\"var(--danger)\" stroke-width=\"1.2\" stroke-dasharray=\"6 4\"/>");

        // Lines, thinned to min / max per pixel column so long runs stay light.
        sb.Append($"<clipPath id=\"cosim-clip\"><rect x=\"{Left}\" y=\"{Top}\" width=\"{PlotWidth}\" height=\"{PlotHeight}\"/></clipPath><g clip-path=\"url(#cosim-clip)\">");
        foreach (var l in lines)
        {
            var pts = new StringBuilder();
            var col = int.MinValue; double cmin = 0, cmax = 0, ct = 0;
            void Flush()
            {
                if (col == int.MinValue) return;
                pts.Append(pts.Length == 0 ? "M" : "L").Append(F(X(ct))).Append(',').Append(F(Y(cmin)));
                if (cmax != cmin) pts.Append('L').Append(F(X(ct))).Append(',').Append(F(Y(cmax)));
            }
            for (var i = i0; i <= i1 && i < l.Y.Count; i++)
            {
                if (!double.IsFinite(l.Y[i])) continue;
                var c = (int)Math.Floor(X(time[i]));
                if (c != col) { Flush(); col = c; cmin = cmax = l.Y[i]; ct = time[i]; }
                else { cmin = Math.Min(cmin, l.Y[i]); cmax = Math.Max(cmax, l.Y[i]); }
            }
            Flush();
            sb.Append($"<path d=\"{pts}\" fill=\"none\" stroke=\"{l.Color}\" stroke-width=\"1.7\" stroke-linejoin=\"round\"/>");
        }
        sb.Append("</g>");

        if (selection is { } s)
        {
            var a = X(Math.Min(s.From, s.To)); var b = X(Math.Max(s.From, s.To));
            sb.Append($"<rect x=\"{F(a)}\" y=\"{Top}\" width=\"{F(Math.Max(1, b - a))}\" height=\"{PlotHeight}\" fill=\"var(--accent)\" fill-opacity=\"0.12\" stroke=\"var(--accent)\" stroke-opacity=\"0.5\"/>");
        }
        if (hover >= 0 && hover < time.Count && time[hover] >= x0 && time[hover] <= x1)
        {
            var hx = X(time[hover]);
            sb.Append($"<line x1=\"{F(hx)}\" y1=\"{Top}\" x2=\"{F(hx)}\" y2=\"{Top + PlotHeight}\" stroke=\"var(--text-3)\" stroke-dasharray=\"3 3\"/>");
            foreach (var l in lines)
                if (hover < l.Y.Count && double.IsFinite(l.Y[hover]))
                    sb.Append($"<circle cx=\"{F(hx)}\" cy=\"{F(Y(l.Y[hover]))}\" r=\"3.2\" fill=\"{l.Color}\" stroke=\"var(--panel)\" stroke-width=\"1\"/>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>About <paramref name="count"/> round tick values between lo and hi.</summary>
    public static IEnumerable<double> Ticks(double lo, double hi, int count)
    {
        var span = hi - lo;
        if (!(span > 0) || !double.IsFinite(span)) yield break;
        var raw = span / count;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1, 2, 2.5, 5, 10 }.Select(f => f * mag).First(s => s >= raw);
        for (var v = Math.Ceiling(lo / step) * step; v <= hi + step * 1e-9; v += step)
            yield return Math.Abs(v) < step * 1e-9 ? 0 : v;
    }
}
