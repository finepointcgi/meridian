using System.Globalization;
using System.Net;
using System.Text;
using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>What the interactive relation map adds to the drawing: selection, search hits and click targets.</summary>
public sealed record RelationMapMarks(IReadOnlySet<string>? Selected = null, IReadOnlySet<string>? Hits = null, bool Interactive = false);

/// <summary>
/// Draws a <see cref="RelationMapGraph"/> as SVG: boxes with the element's icon, name and kind (a used-project badge on
/// library elements, a code badge on code-synced ones), and lines with arrowheads and the relationship's «stereotype» label.
/// The interactive view draws the same body (with click targets); export and reports draw the whole picture.
/// </summary>
public static class RelationMapSvg
{
    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    private static string E(string s) => WebUtility.HtmlEncode(s);

    /// <summary>A standalone SVG document (export, reports).</summary>
    /// <param name="icon">Inner markup of a 24×24 icon per element kind (the web app's Material icons), or null for a lettered badge.</param>
    public static string Render(RelationMapGraph g, SvgTheme? theme = null, Func<ElementKind, string>? icon = null, bool transparent = false, string? title = null)
    {
        var t = theme ?? SvgTheme.Light;
        var w = Math.Max(g.Width, 200); var h = Math.Max(g.Height, 80);
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(w)}\" height=\"{F(h)}\" viewBox=\"0 0 {F(w)} {F(h)}\" font-family=\"Inter, -apple-system, 'Segoe UI', sans-serif\" font-size=\"12\">");
        if (title != null) sb.Append("<title>").Append(E(title)).Append("</title>");
        if (!transparent) sb.Append($"<rect width=\"100%\" height=\"100%\" fill=\"{t.Background}\"/>");
        RenderBody(sb, g, t, icon, null);
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>The map's markup (defs, lines, boxes) for an enclosing &lt;svg&gt; or &lt;g&gt;.</summary>
    public static void RenderBody(StringBuilder sb, RelationMapGraph g, SvgTheme t, Func<ElementKind, string>? icon, RelationMapMarks? marks)
    {
        marks ??= new RelationMapMarks();
        sb.Append("<defs>")
          .Append($"<marker id=\"rm-arrow\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"9\" markerHeight=\"9\" orient=\"auto-start-reverse\"><path d=\"M0,0 L10,5 L0,10 z\" fill=\"{t.Edge}\"/></marker>")
          .Append($"<marker id=\"rm-open\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"9\" markerHeight=\"9\" orient=\"auto-start-reverse\"><path d=\"M0,0 L10,5 L0,10\" fill=\"none\" stroke=\"{t.Edge}\" stroke-width=\"1.4\"/></marker>")
          .Append($"<marker id=\"rm-tri\" viewBox=\"0 0 12 12\" refX=\"11\" refY=\"6\" markerWidth=\"11\" markerHeight=\"11\" orient=\"auto-start-reverse\"><path d=\"M0,0 L12,6 L0,12 z\" fill=\"{t.Background}\" stroke=\"{t.Edge}\" stroke-width=\"1.2\"/></marker>")
          .Append($"<marker id=\"rm-owns\" viewBox=\"0 0 12 12\" refX=\"1\" refY=\"6\" markerWidth=\"11\" markerHeight=\"11\" orient=\"auto-start-reverse\"><circle cx=\"6\" cy=\"6\" r=\"5\" fill=\"{t.Background}\" stroke=\"{t.Edge}\" stroke-width=\"1.1\"/><path d=\"M6,2.5 V9.5 M2.5,6 H9.5\" stroke=\"{t.Edge}\" stroke-width=\"1.1\"/></marker>")
          .Append("</defs>");

        sb.Append("<g class=\"rm-edges\">");
        foreach (var e in g.Edges) Edge(sb, e, t);
        sb.Append("</g><g class=\"rm-labels\">");
        foreach (var e in g.Edges.Where(e => e.Relationship != null || e.Containment)) EdgeLabel(sb, e, t);
        sb.Append("</g><g class=\"rm-nodes\">");
        foreach (var n in g.Nodes) Node(sb, n, t, icon, marks);
        sb.Append("</g>");
    }

    /// <summary>Where the line from the centre of <paramref name="n"/> towards (<paramref name="tx"/>, <paramref name="ty"/>) leaves its box.</summary>
    private static (double X, double Y) Clip(RelationMapNode n, double tx, double ty)
    {
        double cx = n.CenterX, cy = n.CenterY, dx = tx - cx, dy = ty - cy;
        if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6) return (cx, cy);
        var sx = dx == 0 ? double.MaxValue : (n.W / 2) / Math.Abs(dx);
        var sy = dy == 0 ? double.MaxValue : (n.H / 2) / Math.Abs(dy);
        var s = Math.Min(sx, sy);
        return (cx + dx * s, cy + dy * s);
    }

    private static void Edge(StringBuilder sb, RelationMapEdge e, SvgTheme t)
    {
        var (x1, y1) = Clip(e.Source, e.Target.CenterX, e.Target.CenterY);
        var (x2, y2) = Clip(e.Target, e.Source.CenterX, e.Source.CenterY);
        string dash = "", head = "", tail = "";
        var kind = "stub";
        if (e.Relationship is { } r)
        {
            var st = Metamodel.Style(r.Kind);
            kind = r.Kind.ToString();
            if (st.Dashed) dash = " stroke-dasharray=\"6,4\"";
            else if (st.Dotted) dash = " stroke-dasharray=\"2,3\"";
            head = st.TargetHead switch
            {
                ArrowHead.None => "",
                ArrowHead.HollowTriangle => " marker-end=\"url(#rm-tri)\"",
                ArrowHead.Open => " marker-end=\"url(#rm-open)\"",
                _ => " marker-end=\"url(#rm-arrow)\"",
            };
        }
        else if (e.Containment) { kind = "containment"; tail = " marker-start=\"url(#rm-owns)\""; dash = " stroke-dasharray=\"2,3\""; }
        else dash = " stroke-dasharray=\"3,3\"";
        sb.Append($"<line class=\"rm-edge\" data-kind=\"{kind}\" x1=\"{F(x1)}\" y1=\"{F(y1)}\" x2=\"{F(x2)}\" y2=\"{F(y2)}\" stroke=\"{t.Edge}\" stroke-width=\"1.2\"{dash}{head}{tail}/>");
    }

    private static void EdgeLabel(StringBuilder sb, RelationMapEdge e, SvgTheme t)
    {
        var label = e.Label;
        if (label.Length == 0) return;
        if (label.Length > 34) label = label[..33] + "…";
        var mx = (e.Source.CenterX + e.Target.CenterX) / 2;
        var my = (e.Source.CenterY + e.Target.CenterY) / 2;
        var w = label.Length * 5.6 + 8;
        sb.Append($"<g class=\"rm-elabel\"><title>{E(e.KindName)}{(e.Relationship is { } r ? ": " + E(r.Source.DisplayName) + " → " + E(r.Target.DisplayName) : "")}</title>")
          .Append($"<rect x=\"{F(mx - w / 2)}\" y=\"{F(my - 8)}\" width=\"{F(w)}\" height=\"15\" rx=\"3\" fill=\"{t.LabelBg}\" opacity=\"0.9\"/>")
          .Append($"<text x=\"{F(mx)}\" y=\"{F(my + 3.5)}\" text-anchor=\"middle\" font-size=\"10\" fill=\"{t.Muted}\">{E(label)}</text></g>");
    }

    private static void Node(StringBuilder sb, RelationMapNode n, SvgTheme t, Func<ElementKind, string>? icon, RelationMapMarks marks)
    {
        if (n.IsStub)
        {
            var stubAttrs = marks.Interactive ? $" data-stub=\"{E(n.Parent!.Id)}\" style=\"cursor:pointer\"" : "";
            sb.Append($"<g class=\"rm-stub\"{stubAttrs}><title>{n.MoreCount} more related element{(n.MoreCount == 1 ? "" : "s")} — click to show more</title>")
              .Append($"<rect x=\"{F(n.X)}\" y=\"{F(n.Y)}\" width=\"{F(n.W)}\" height=\"{F(n.H)}\" rx=\"14\" fill=\"{t.FrameFill}\" stroke=\"{t.Stroke}\" stroke-dasharray=\"4,3\"/>")
              .Append($"<text x=\"{F(n.CenterX)}\" y=\"{F(n.CenterY + 4)}\" text-anchor=\"middle\" font-weight=\"600\" fill=\"{t.Muted}\">+{n.MoreCount} more</text></g>");
            return;
        }
        var el = n.Element!;
        var accent = t.Accent(el.Kind);
        var selected = marks.Selected?.Contains(n.Id) == true;
        var hit = marks.Hits?.Contains(n.Id) == true;
        var cls = "rm-node" + (n.IsRoot ? " root" : "") + (selected ? " sel" : "") + (hit ? " hit" : "") + (n.Library != null ? " lib" : "");
        var attrs = marks.Interactive ? $" data-node=\"{E(n.Id)}\" style=\"cursor:pointer\"" : "";
        sb.Append($"<g class=\"{cls}\"{attrs}><title>{E(el.QualifiedName)} ({E(n.KindName)}){(n.Library != null ? " — from " + E(n.Library) + ", read-only" : "")}</title>");
        if (hit) sb.Append($"<rect x=\"{F(n.X - 5)}\" y=\"{F(n.Y - 5)}\" width=\"{F(n.W + 10)}\" height=\"{F(n.H + 10)}\" rx=\"10\" fill=\"#f0b429\" opacity=\"0.35\"/>");
        var stroke = selected ? "#4f8cff" : n.IsRoot ? accent : t.Stroke;
        var sw = selected || n.IsRoot ? 2.2 : 1;
        sb.Append($"<rect class=\"rm-box\" x=\"{F(n.X)}\" y=\"{F(n.Y)}\" width=\"{F(n.W)}\" height=\"{F(n.H)}\" rx=\"7\" fill=\"{t.NodeFill}\" stroke=\"{stroke}\" stroke-width=\"{F(sw)}\"/>")
          .Append($"<rect x=\"{F(n.X)}\" y=\"{F(n.Y + 6)}\" width=\"4\" height=\"{F(n.H - 12)}\" rx=\"2\" fill=\"{accent}\"/>");
        // Icon
        var ix = n.X + 10; var iy = n.Y + (n.H - 20) / 2;
        if (icon?.Invoke(el.Kind) is { Length: > 0 } markup)
            sb.Append($"<svg class=\"rm-icon\" x=\"{F(ix)}\" y=\"{F(iy)}\" width=\"20\" height=\"20\" viewBox=\"0 0 24 24\" fill=\"{accent}\">{markup}</svg>");
        else
        {
            var letter = Metamodel.DisplayName(el.Kind) is { Length: > 0 } dn ? dn[..1] : "?";
            sb.Append($"<circle cx=\"{F(ix + 10)}\" cy=\"{F(iy + 10)}\" r=\"9\" fill=\"{t.Header(el.Kind)}\" stroke=\"{accent}\"/>")
              .Append($"<text x=\"{F(ix + 10)}\" y=\"{F(iy + 14)}\" text-anchor=\"middle\" font-size=\"11\" font-weight=\"700\" fill=\"{accent}\">{E(letter)}</text>");
        }
        var textX = n.X + 36;
        var maxChars = (int)Math.Max(6, (n.W - 46 - (n.Library != null || n.CodeSynced ? 26 : 0)) / 6.6);
        var name = n.Name.Length > maxChars ? n.Name[..(maxChars - 1)] + "…" : n.Name;
        sb.Append($"<text class=\"rm-name\" x=\"{F(textX)}\" y=\"{F(n.Y + 19)}\" font-weight=\"600\" fill=\"{t.Text}\">{E(name)}</text>")
          .Append($"<text class=\"rm-kind\" x=\"{F(textX)}\" y=\"{F(n.Y + 34)}\" font-size=\"10.5\" fill=\"{t.Muted}\">{E(n.KindName)}</text>");
        // Badges: used project (read-only) and code-synced.
        var bx = n.X + n.W - 22;
        if (n.Library != null)
        {
            sb.Append($"<g class=\"rm-lib\"><title>From the used project {E(n.Library)} (read-only)</title><rect x=\"{F(bx)}\" y=\"{F(n.Y + 5)}\" width=\"17\" height=\"13\" rx=\"3\" fill=\"#7b4fd6\"/>")
              .Append($"<text x=\"{F(bx + 8.5)}\" y=\"{F(n.Y + 14.5)}\" text-anchor=\"middle\" font-size=\"8\" font-weight=\"700\" fill=\"#fff\">LIB</text></g>");
            bx -= 20;
        }
        if (n.CodeSynced)
            sb.Append($"<g class=\"rm-code\"><title>Synced from code</title><rect x=\"{F(bx)}\" y=\"{F(n.Y + 5)}\" width=\"17\" height=\"13\" rx=\"3\" fill=\"#2e8b57\"/>")
              .Append($"<text x=\"{F(bx + 8.5)}\" y=\"{F(n.Y + 14.5)}\" text-anchor=\"middle\" font-size=\"8\" font-weight=\"700\" fill=\"#fff\">&lt;/&gt;</text></g>");
        // Expand / collapse handle: on explored nodes with children (−), on closed nodes with something behind them (+N).
        var canCollapse = n.Explored && n.Children.Count > 0 && !n.IsRoot;
        if (canCollapse || n.Hidden > 0)
        {
            var hx = n.X + n.W; var hy = n.CenterY;
            var text = canCollapse ? "−" : "+";
            var tip = canCollapse ? "Collapse" : $"Expand: {n.Hidden} more related element{(n.Hidden == 1 ? "" : "s")}";
            var toggle = marks.Interactive ? $" data-toggle=\"{E(n.Id)}\"" : "";
            sb.Append($"<g class=\"rm-toggle\"{toggle}><title>{tip}</title><circle cx=\"{F(hx)}\" cy=\"{F(hy)}\" r=\"8\" fill=\"{t.Background}\" stroke=\"{accent}\" stroke-width=\"1.4\"/>")
              .Append($"<text x=\"{F(hx)}\" y=\"{F(hy + 4)}\" text-anchor=\"middle\" font-size=\"12\" font-weight=\"700\" fill=\"{accent}\">{text}</text></g>");
        }
        sb.Append("</g>");
    }
}
