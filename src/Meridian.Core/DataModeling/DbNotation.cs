using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.DataModeling;

/// <summary>One row of a table shape: the column and its markers.</summary>
public sealed record DbColumnRow(Element Column, bool PrimaryKey, bool ForeignKey, bool Unique, bool Nullable, string Type)
{
    public string Markers => (PrimaryKey ? "PK" : "") + (PrimaryKey && ForeignKey ? "," : "") + (ForeignKey ? "FK" : "") + (Unique && !PrimaryKey ? (PrimaryKey || ForeignKey ? ",UQ" : "UQ") : "");
}

/// <summary>
/// Crow's-foot notation for database diagrams, shared by every renderer: what a table shape lists (all columns, keys only
/// or none), where each column row sits, the cardinality ends of foreign keys and column-to-column routing.
/// </summary>
public static class DbNotation
{
    /// <summary>Header height of a table shape (name, plus a «view» keyword line) — the same formula as <see cref="Notation.PreferredHeight"/>.</summary>
    public static double HeaderHeight(bool hasKeyword) => (1 + (hasKeyword ? 1 : 0)) * Notation.LineHeight + 14;

    /// <summary>Y of the centre of row <paramref name="index"/> of a table drawn in <paramref name="b"/>.</summary>
    public static double RowCenter(Bounds b, bool hasKeyword, int index) => b.Y + HeaderHeight(hasKeyword) + 4 + index * Notation.LineHeight + Notation.LineHeight / 2;

    /// <summary>The rows a table shape shows for <paramref name="mode"/> ("all", "keys", "none").</summary>
    public static IReadOnlyList<DbColumnRow> Rows(Element table, ISet<string> fkColumns, string mode = "all")
    {
        if (mode == "none") return [];
        var rows = new List<DbColumnRow>();
        foreach (var c in DbModel.Columns(table))
        {
            var pk = DbModel.IsPk(c);
            var fk = fkColumns.Contains(c.Name);
            var uq = DbModel.IsUnique(c);
            if (mode == "keys" && !pk && !fk) continue;
            rows.Add(new DbColumnRow(c, pk, fk, uq, DbModel.IsNullable(c), c.Get(FieldKeys.SqlType) is { Length: > 0 } t ? t : DbModel.IsView(table) ? "" : "text"));
        }
        return rows;
    }

    /// <summary>The text of a row, for renderers that draw plain lines (HTML reports, size estimates): "PK id : integer".</summary>
    public static string Line(DbColumnRow r)
    {
        var markers = r.Markers;
        return $"{(markers.Length > 0 ? markers + " " : "")}{r.Column.Name} : {r.Type}{(r.Nullable || r.PrimaryKey ? "" : " NN")}";
    }

    /// <summary>Columns a shape of <paramref name="table"/> lists, as lines.</summary>
    public static IReadOnlyList<string> Lines(ModelRepository model, Element table, string mode = "all") =>
        Rows(table, DbModel.ForeignKeyColumns(model, table), mode).Select(Line).ToList();

    /// <summary>Foreign-key columns of a table as far as the diagram shows (the router has no model at hand).</summary>
    public static HashSet<string> FkColumnsOnDiagram(Diagram d, Element table)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in d.Edges)
            if (e.Relationship.Kind == ElementKind.ForeignKey && ReferenceEquals(e.Relationship.Source, table))
                foreach (var n in DbModel.SplitList(e.Relationship.Get(FieldKeys.FkColumns))) set.Add(n);
        return set;
    }

    /// <summary>
    /// Crow's foot ends of a foreign key: many (crow's foot) at the referencing table, one at the referenced one — a bar when
    /// the referencing columns are mandatory, a circle when they are nullable (zero or one); a unique referencing column
    /// makes the relationship one-to-one (a bar at both ends).
    /// </summary>
    public static EdgeStyle ForeignKeyStyle(Relationship fk)
    {
        var cols = DbModel.FkColumns(fk);
        var optional = cols.Count > 0 && cols.Any(DbModel.IsNullable);
        var oneToOne = cols.Count == 1 && (DbModel.IsUnique(cols[0]) || DbModel.IsPk(cols[0]) && DbModel.PrimaryKey(fk.Source).Count == 1);
        return new EdgeStyle(false, optional ? ArrowHead.HollowDot : ArrowHead.Bar, oneToOne ? ArrowHead.Bar : ArrowHead.CrowsFoot, null);
    }

    /// <summary>
    /// A foreign key drawn from the row of its first column to the row of the referenced column, with horizontal ends
    /// (column to column). Null when it does not apply (other connectors, manual bends, shapes overlapping horizontally
    /// with the columns hidden).
    /// </summary>
    public static IReadOnlyList<Pt>? ColumnRoute(Diagram d, DiagramEdge e, Func<DiagramNode, Bounds> boundsOf, IReadOnlyList<Pt>? waypoints)
    {
        if (d.DiagramType != DiagramType.Database || e.Relationship.Kind != ElementKind.ForeignKey || (waypoints ?? e.Waypoints).Count > 0) return null;
        if (e.Source.Element.Kind != ElementKind.Table || e.Target.Element.Kind != ElementKind.Table) return null;
        var fk = e.Relationship;
        var s = boundsOf(e.Source);
        var t = boundsOf(e.Target);
        double? RowY(DiagramNode n, Bounds b, string? column)
        {
            if (column == null) return null;
            var rows = Rows(n.Element, FkColumnsOnDiagram(d, n.Element), DbModel.ShowColumnsOf(n));
            var i = rows.ToList().FindIndex(r => string.Equals(r.Column.Name, column, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return null;
            var y = RowCenter(b, KeywordOf(n.Element), i);
            return y < b.Bottom - 2 ? y : null;
        }
        var srcCol = DbModel.SplitList(fk.Get(FieldKeys.FkColumns)).FirstOrDefault();
        var refCol = DbModel.SplitList(fk.Get(FieldKeys.RefColumns)).FirstOrDefault() ?? DbModel.PrimaryKey(fk.Target).FirstOrDefault()?.Name;
        var y1 = RowY(e.Source, s, srcCol);
        var y2 = RowY(e.Target, t, refCol);
        if (y1 == null && y2 == null) return null;
        var ys = y1 ?? s.Center.Y;
        var yt = y2 ?? t.Center.Y;
        const double stub = 24;
        if (ReferenceEquals(e.Source, e.Target))
        {
            var x = s.Right + stub + 12;
            return [new Pt(s.Right, ys), new Pt(x, ys), new Pt(x, yt == ys ? ys + 18 : yt), new Pt(s.Right, yt == ys ? ys + 18 : yt)];
        }
        if (s.Right + 2 * stub <= t.X)
        {
            var mx = Math.Round((s.Right + t.X) / 2);
            return Clean([new Pt(s.Right, ys), new Pt(mx, ys), new Pt(mx, yt), new Pt(t.X, yt)]);
        }
        if (t.Right + 2 * stub <= s.X)
        {
            var mx = Math.Round((t.Right + s.X) / 2);
            return Clean([new Pt(s.X, ys), new Pt(mx, ys), new Pt(mx, yt), new Pt(t.Right, yt)]);
        }
        // Stacked vertically: leave and enter on the right-hand side.
        var rx = Math.Max(s.Right, t.Right) + stub + 6;
        return Clean([new Pt(s.Right, ys), new Pt(rx, ys), new Pt(rx, yt), new Pt(t.Right, yt)]);
    }

    private static IReadOnlyList<Pt> Clean(Pt[] pts)
    {
        // A straight run when both rows line up.
        if (Math.Abs(pts[1].Y - pts[2].Y) < 0.5 && Math.Abs(pts[0].Y - pts[3].Y) < 0.5) return [pts[0], pts[3]];
        return pts;
    }

    /// <summary>The column whose row of the table shape <paramref name="n"/> (drawn in <paramref name="b"/>) is under <paramref name="p"/>, or null.</summary>
    public static Element? ColumnAt(ModelRepository m, DiagramNode n, Bounds b, Pt p)
    {
        if (n.Element.Kind != ElementKind.Table || !b.Contains(p)) return null;
        var top = b.Y + HeaderHeight(KeywordOf(n.Element)) + 4;
        if (p.Y < top) return null;
        var rows = Rows(n.Element, DbModel.ForeignKeyColumns(m, n.Element), DbModel.ShowColumnsOf(n));
        var i = (int)Math.Floor((p.Y - top) / Notation.LineHeight);
        return i >= 0 && i < rows.Count ? rows[i].Column : null;
    }

    public static bool KeywordOf(Element table) => DbModel.TableKeyword(table) != null || table.Stereotypes.Count > 0;
}
