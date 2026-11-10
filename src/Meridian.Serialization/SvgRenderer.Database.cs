using System.Text;
using Meridian.Core.DataModeling;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization;

public static partial class SvgRenderer
{
    private const string PkColor = "#e0a800";
    private const string FkColor = "#4f8cff";

    /// <summary>
    /// A table in crow's-foot style: a header with the name (and «view» / «materialized view»), one row per column with key
    /// markers (gold key for primary key columns, blue for foreign keys, UQ for unique), the type on the right and an
    /// asterisk on mandatory columns. Views have a dashed outline. Rows carry <c>data-column</c> for column-level gestures.
    /// </summary>
    private static void RenderTable(StringBuilder sb, ModelRepository m, Diagram d, DiagramNode n, Bounds b, NodeContent c, string fill, string header, SvgTheme t)
    {
        var table = n.Element;
        var view = DbModel.IsView(table);
        var hasKw = c.Keyword != null;
        var headerH = DbNotation.HeaderHeight(hasKw);
        var stroke = t.Stroke;
        var dash = view ? " stroke-dasharray=\"6,4\"" : "";
        sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" rx=\"4\" fill=\"{fill}\" stroke=\"{stroke}\" filter=\"url(#msh)\"/>");
        sb.Append($"<path d=\"M{F(b.X + 0.5)},{F(b.Y + headerH)} V{F(b.Y + 4)} Q{F(b.X + 0.5)},{F(b.Y + 0.5)} {F(b.X + 4)},{F(b.Y + 0.5)} H{F(b.Right - 4)} Q{F(b.Right - 0.5)},{F(b.Y + 0.5)} {F(b.Right - 0.5)},{F(b.Y + 4)} V{F(b.Y + headerH)} Z\" fill=\"{header}\"{(view ? " fill-opacity=\"0.55\"" : "")}/>");
        sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" rx=\"4\" fill=\"none\" stroke=\"{stroke}\"{dash}/>");
        // Subject-area colours as small chips along the top edge.
        var groups = DbModel.GroupsOf(m, table);
        if (groups.Count > 0)
        {
            var w = Math.Min(28, (b.Width - 8) / groups.Count);
            for (var i = 0; i < groups.Count; i++)
                sb.Append($"<rect x=\"{F(b.X + 4 + i * w)}\" y=\"{F(b.Y + 1)}\" width=\"{F(w - 2)}\" height=\"3\" rx=\"1.5\" fill=\"{SafeColor(DbModel.ColorOf(groups[i])) ?? "#8a93a3"}\"><title>{E(groups[i].Name)}</title></rect>");
        }
        else if (!view) sb.Append($"<rect x=\"{F(b.X + 1)}\" y=\"{F(b.Y + 1)}\" width=\"{F(Math.Max(0, b.Width - 2))}\" height=\"3\" rx=\"1.5\" fill=\"{t.Accent(ElementKind.Table)}\"/>");
        var y = b.Y + Line + 2;
        if (hasKw) { Text(sb, b.Center.X, y, Trunc(c.Keyword!, b.Width - 8), false, "middle", size: 11, color: t.Muted); y += Line; }
        Text(sb, b.Center.X, y, Trunc(c.Name, b.Width - 8, 7.4), true, "middle", view, size: 13.5, color: t.Text);
        var rows = DbNotation.Rows(table, DbModel.ForeignKeyColumns(m, table), DbModel.ShowColumnsOf(n));
        if (rows.Count == 0 && c.Compartments.Count == 0) return;
        sb.Append($"<line x1=\"{F(b.X)}\" y1=\"{F(b.Y + headerH)}\" x2=\"{F(b.Right)}\" y2=\"{F(b.Y + headerH)}\" stroke=\"{t.Divider}\"/>");
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var cy = DbNotation.RowCenter(b, hasKw, i);
            if (cy + Line / 2 > b.Bottom + 1) break;
            var baseline = cy + 4;
            sb.Append($"<g class=\"db-row\" data-column=\"{E(r.Column.Id)}\">");
            sb.Append($"<rect x=\"{F(b.X + 1)}\" y=\"{F(cy - Line / 2)}\" width=\"{F(b.Width - 2)}\" height=\"{F(Line)}\" fill=\"transparent\"/>");
            var x = b.X + 6;
            if (r.PrimaryKey) KeyGlyph(sb, x, cy, PkColor);
            else if (r.ForeignKey) KeyGlyph(sb, x, cy, FkColor);
            if (r.PrimaryKey && r.ForeignKey) sb.Append($"<circle cx=\"{F(x + 15)}\" cy=\"{F(cy - 4)}\" r=\"2.4\" fill=\"{FkColor}\"><title>also a foreign key</title></circle>");
            else if (r.Unique && !r.PrimaryKey && !r.ForeignKey) Text(sb, x, baseline, "UQ", false, "start", size: 8.5, color: t.Muted);
            var nameX = b.X + 24;
            var typeText = r.Type;
            var typeW = Math.Min(typeText.Length * 6.2, (b.Width - 30) * 0.45);
            Text(sb, nameX, baseline, Trunc(r.Column.Name + (r.Nullable ? "" : " *"), b.Width - 34 - typeW), r.PrimaryKey, "start", size: 11.5, color: t.Text);
            Text(sb, b.Right - 7, baseline, Trunc(typeText, typeW + 4), false, "end", italic: false, size: 11, color: t.Muted);
            sb.Append("</g>");
        }
        if (rows.Count == 0 && DbModel.ShowColumnsOf(n) == "all")
            Text(sb, b.X + 8, DbNotation.RowCenter(b, hasKw, 0) + 4, "(no columns)", false, "start", italic: true, size: 11, color: t.Muted);
    }

    /// <summary>A small key: a ring with a shaft and a tooth.</summary>
    private static void KeyGlyph(StringBuilder sb, double x, double cy, string color) =>
        sb.Append($"<g fill=\"none\" stroke=\"{color}\" stroke-width=\"1.6\"><circle cx=\"{F(x + 3.2)}\" cy=\"{F(cy)}\" r=\"2.8\"/><path d=\"M{F(x + 6)},{F(cy)} H{F(x + 12)} M{F(x + 10)},{F(cy)} V{F(cy + 3)}\"/></g>");

    /// <summary>Subject areas on a database diagram: a tinted, labelled frame around the tables of each area shown.</summary>
    private static void RenderGroupFrames(StringBuilder sb, ModelRepository m, Diagram d, Func<DiagramNode, Bounds> bounds, SvgTheme t)
    {
        var frames = DbModel.GroupFrames(m, d);
        for (var i = 0; i < frames.Count; i++)
        {
            var (g, nodes) = frames[i];
            var bs = nodes.Select(bounds).ToList();
            var pad = 14 + (frames.Count - 1 - i) % 3 * 6;   // nested a little when areas overlap
            double x0 = bs.Min(b => b.X) - pad, y0 = bs.Min(b => b.Y) - pad - 20, x1 = bs.Max(b => b.Right) + pad, y1 = bs.Max(b => b.Bottom) + pad;
            var color = SafeColor(DbModel.ColorOf(g)) ?? "#8a93a3";
            sb.Append($"<g class=\"db-area\" data-element=\"{E(g.Id)}\">");
            sb.Append($"<rect x=\"{F(x0)}\" y=\"{F(y0)}\" width=\"{F(x1 - x0)}\" height=\"{F(y1 - y0)}\" rx=\"10\" fill=\"{color}\" fill-opacity=\"0.07\" stroke=\"{color}\" stroke-opacity=\"0.8\" stroke-width=\"1.4\" stroke-dasharray=\"8,4\"/>");
            var label = g.Name;
            var lw = label.Length * 6.6 + 16;
            sb.Append($"<rect x=\"{F(x0 + 10)}\" y=\"{F(y0 - 9)}\" width=\"{F(lw)}\" height=\"18\" rx=\"9\" fill=\"{color}\"/>");
            Text(sb, x0 + 18, y0 + 4, label, true, "start", size: 11, color: "#ffffff");
            sb.Append("</g>");
        }
    }
}
