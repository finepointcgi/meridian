using System.Globalization;
using System.Net;
using System.Text;
using Meridian.Core.Analysis;

namespace Meridian.Web.Services;

/// <summary>Small inline SVG charts for analysis panels, themed through the app's CSS variables.</summary>
public static class SvgCharts
{
    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string E(string s) => WebUtility.HtmlEncode(s);
    private static string N(double v) => Math.Abs(v) >= 1e5 || Math.Abs(v) < 1e-3 && v != 0 ? v.ToString("0.##e0", CultureInfo.InvariantCulture) : v.ToString("G4", CultureInfo.InvariantCulture);

    /// <summary>Horizontal bars (scores, one per alternative), the first highlighted.</summary>
    public static string Bars(IReadOnlyList<(string Label, double Value)> items, double max = 1, int width = 420)
    {
        const int row = 24, labelW = 130, pad = 6;
        var h = items.Count * row + pad * 2;
        var barW = width - labelW - 60;
        var sb = new StringBuilder($"<svg class=\"chart\" width=\"{width}\" height=\"{h}\" viewBox=\"0 0 {width} {h}\" xmlns=\"http://www.w3.org/2000/svg\" font-family=\"inherit\" font-size=\"11.5\">");
        for (var i = 0; i < items.Count; i++)
        {
            var (label, value) = items[i];
            var y = pad + i * row;
            var w = max > 0 ? Math.Max(0, value / max) * barW : 0;
            sb.Append($"<text x=\"{labelW - 8}\" y=\"{y + 16}\" text-anchor=\"end\" fill=\"var(--text-2)\">{E(label.Length > 20 ? label[..19] + "…" : label)}</text>");
            sb.Append($"<rect x=\"{labelW}\" y=\"{y + 4}\" width=\"{barW}\" height=\"16\" rx=\"3\" fill=\"var(--panel-3)\"/>");
            sb.Append($"<rect x=\"{labelW}\" y=\"{y + 4}\" width=\"{F(w)}\" height=\"16\" rx=\"3\" fill=\"{(i == 0 ? "var(--accent)" : "var(--accent-2)")}\" fill-opacity=\"{(i == 0 ? "1" : "0.55")}\"><title>{E(label)}: {N(value)}</title></rect>");
            sb.Append($"<text x=\"{F(labelW + w + 6)}\" y=\"{y + 16}\" fill=\"var(--text)\" font-weight=\"600\">{N(value)}</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>Histogram with an optional threshold line and the samples meeting it shaded.</summary>
    public static string Histogram(double[] edges, int[] counts, double? threshold = null, Func<double, bool>? meets = null, int width = 460, int height = 180)
    {
        if (counts.Length == 0) return "";
        const int left = 40, bottom = 26, top = 10, right = 10;
        var plotW = width - left - right; var plotH = height - top - bottom;
        double lo = edges[0], hi = edges[^1];
        var maxC = Math.Max(1, counts.Max());
        double X(double v) => left + (v - lo) / (hi - lo) * plotW;
        var sb = new StringBuilder($"<svg class=\"chart\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" font-family=\"inherit\" font-size=\"10.5\">");
        sb.Append($"<line x1=\"{left}\" y1=\"{top + plotH}\" x2=\"{left + plotW}\" y2=\"{top + plotH}\" stroke=\"var(--border-strong)\"/>");
        for (var i = 0; i < counts.Length; i++)
        {
            var x0 = X(edges[i]); var x1 = X(edges[i + 1]);
            var bh = (double)counts[i] / maxC * plotH;
            var mid = (edges[i] + edges[i + 1]) / 2;
            var ok = meets?.Invoke(mid);
            var fill = ok is null ? "var(--accent)" : ok.Value ? "var(--ok)" : "var(--danger)";
            sb.Append($"<rect x=\"{F(x0 + 0.5)}\" y=\"{F(top + plotH - bh)}\" width=\"{F(Math.Max(0.5, x1 - x0 - 1))}\" height=\"{F(bh)}\" fill=\"{fill}\" fill-opacity=\"0.75\"><title>{N(edges[i])} – {N(edges[i + 1])}: {counts[i]}</title></rect>");
        }
        foreach (var t in new[] { lo, (lo + hi) / 2, hi })
            sb.Append($"<text x=\"{F(X(t))}\" y=\"{height - 8}\" text-anchor=\"middle\" fill=\"var(--text-3)\">{N(t)}</text>");
        sb.Append($"<text x=\"{left - 6}\" y=\"{top + 8}\" text-anchor=\"end\" fill=\"var(--text-3)\">{maxC}</text>");
        sb.Append($"<text x=\"{left - 6}\" y=\"{top + plotH}\" text-anchor=\"end\" fill=\"var(--text-3)\">0</text>");
        if (threshold is { } th && th >= lo && th <= hi)
        {
            sb.Append($"<line x1=\"{F(X(th))}\" y1=\"{top}\" x2=\"{F(X(th))}\" y2=\"{top + plotH}\" stroke=\"var(--warn)\" stroke-width=\"2\" stroke-dasharray=\"4 3\"/>");
            sb.Append($"<text x=\"{F(X(th) + 4)}\" y=\"{top + 10}\" fill=\"var(--warn)\" font-weight=\"600\">{N(th)}</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>Gantt / sequence-style timeline: one row per action or state machine, bars for activity, arrows for signals.</summary>
    public static string Timeline(IReadOnlyList<TimelineSpan> spans, IReadOnlyList<SignalMark> signals, double clock, int width = 760)
    {
        var rows = spans.Select(s => s.Row).Concat(signals.SelectMany(s => s.ToRows.Prepend(s.FromRow))).Distinct().ToList();
        if (rows.Count == 0) return "";
        const int rowH = 22, left = 170, top = 8, axis = 22;
        var end = Math.Max(clock, spans.Select(s => s.Open ? clock : s.End).DefaultIfEmpty(0).Max());
        if (end <= 0) end = 1;
        var plotW = width - left - 16;
        var h = top + rows.Count * rowH + axis;
        double X(double t) => left + t / end * plotW;
        double Y(string row) => top + rows.IndexOf(row) * rowH;
        var sb = new StringBuilder($"<svg class=\"chart\" width=\"{width}\" height=\"{h}\" viewBox=\"0 0 {width} {h}\" xmlns=\"http://www.w3.org/2000/svg\" font-family=\"inherit\" font-size=\"11\">");
        sb.Append("<defs><marker id=\"tl-arrow\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"7\" markerHeight=\"7\" orient=\"auto-start-reverse\"><path d=\"M0,0 L10,5 L0,10 z\" fill=\"var(--warn)\"/></marker></defs>");
        foreach (var r in rows)
        {
            var y = Y(r);
            sb.Append($"<line x1=\"{left}\" y1=\"{F(y + rowH - 1)}\" x2=\"{left + plotW}\" y2=\"{F(y + rowH - 1)}\" stroke=\"var(--border)\"/>");
            sb.Append($"<text x=\"{left - 8}\" y=\"{F(y + 15)}\" text-anchor=\"end\" fill=\"var(--text-2)\">{E(r.Length > 26 ? r[..25] + "…" : r)}</text>");
        }
        foreach (var s in spans)
        {
            var x0 = X(s.Start); var x1 = X(s.Open ? clock : s.End);
            var y = Y(s.Row);
            var fill = s.Kind switch { "wait" => "var(--warn)", "state" => "var(--accent-2)", _ => "var(--accent)" };
            var label = string.IsNullOrEmpty(s.Element.Name) ? s.Kind : s.Element.Name;
            sb.Append($"<rect x=\"{F(x0)}\" y=\"{F(y + 4)}\" width=\"{F(Math.Max(2, x1 - x0))}\" height=\"{rowH - 9}\" rx=\"3\" fill=\"{fill}\" fill-opacity=\"{(s.Kind == "wait" ? "0.35" : "0.8")}\"{(s.Open ? " stroke=\"var(--text)\" stroke-dasharray=\"3 2\"" : "")}><title>{E(label)}: {N(s.Start)}s – {(s.Open ? "…" : N(s.End) + "s")}</title></rect>");
            if (x1 - x0 > label.Length * 6 + 6)
                sb.Append($"<text x=\"{F(x0 + 4)}\" y=\"{F(y + 15)}\" fill=\"var(--text)\" font-size=\"10.5\">{E(label)}</text>");
        }
        foreach (var sig in signals)
        {
            if (!rows.Contains(sig.FromRow)) continue;
            var x = X(sig.Time);
            var y0 = Y(sig.FromRow) + rowH / 2.0;
            foreach (var to in sig.ToRows.Where(rows.Contains))
            {
                var y1 = Y(to) + rowH / 2.0;
                if (Math.Abs(y1 - y0) < 1) continue;
                sb.Append($"<line x1=\"{F(x)}\" y1=\"{F(y0)}\" x2=\"{F(x)}\" y2=\"{F(y1 + (y1 > y0 ? -4 : 4))}\" stroke=\"var(--warn)\" stroke-width=\"1.5\" marker-end=\"url(#tl-arrow)\"><title>{E(sig.Signal)} at {N(sig.Time)}s</title></line>");
            }
            sb.Append($"<circle cx=\"{F(x)}\" cy=\"{F(y0)}\" r=\"3\" fill=\"var(--warn)\"><title>{E(sig.Signal)} at {N(sig.Time)}s</title></circle>");
        }
        var axisY = top + rows.Count * rowH;
        for (var k = 0; k <= 5; k++)
        {
            var t = end * k / 5;
            sb.Append($"<line x1=\"{F(X(t))}\" y1=\"{axisY}\" x2=\"{F(X(t))}\" y2=\"{axisY + 4}\" stroke=\"var(--border-strong)\"/>");
            sb.Append($"<text x=\"{F(X(t))}\" y=\"{axisY + 15}\" text-anchor=\"middle\" fill=\"var(--text-3)\">{N(t)}s</text>");
        }
        sb.Append($"<line x1=\"{F(X(clock))}\" y1=\"{top}\" x2=\"{F(X(clock))}\" y2=\"{axisY}\" stroke=\"var(--danger)\" stroke-width=\"1.2\"/>");
        sb.Append("</svg>");
        return sb.ToString();
    }
}
