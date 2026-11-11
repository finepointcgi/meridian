using System.Text;
using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization.Spreadsheets;

/// <summary>
/// Writes elements to a sheet through the same <see cref="SheetMapping"/> the importer reads, so export → edit in
/// Excel → import is a round trip that only reports what was edited.
/// </summary>
public static class SheetExport
{
    /// <summary>The elements a mapping exports by default: its target kind(s) inside <paramref name="scope"/>, in model order.</summary>
    public static List<Element> DefaultElements(ModelRepository m, SheetMapping map, Element? scope = null)
    {
        scope ??= m.Root;
        var kind = SheetKinds.ById(map.Kind);
        var kinds = kind != null ? [kind]
            : map.Kinds is { Count: > 0 } ks ? ks.Select(SheetKinds.ById).Where(k => k != null).Select(k => k!).ToList()
            : SheetKinds.Common.Where(k => k.Id != "Package").ToList();
        return scope.Descendants().Where(e => !e.IsRelationship && e.Kind is not (ElementKind.Diagram or ElementKind.Model) && e.Name.Length > 0
                                              && kinds.Any(k => k.Matches(m, e))
                                              && (string.IsNullOrEmpty(map.Stereotype) || e.Stereotypes.Contains(map.Stereotype, StringComparer.OrdinalIgnoreCase)))
                    .ToList();
    }

    public static byte[] ToXlsx(ModelRepository m, SheetMapping map, IEnumerable<Element>? elements = null, Element? scope = null, string? sheetName = null) =>
        Xlsx.Write(ToSheet(m, map, elements, scope, sheetName));

    public static string ToCsv(ModelRepository m, SheetMapping map, IEnumerable<Element>? elements = null, Element? scope = null, char delimiter = ',') =>
        ToCsv(ToSheet(m, map, elements, scope), delimiter);

    public static string ToCsv(Xlsx.Sheet sheet, char delimiter = ',')
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(delimiter, sheet.Header.Select(h => Quote(h, delimiter)))).Append("\r\n");
        foreach (var row in sheet.Rows) sb.Append(string.Join(delimiter, row.Select(c => Quote(c, delimiter)))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>A CSV cell: formula-like text is neutralised (see <see cref="CsvSafety"/>), then quoted when needed.</summary>
    public static string Quote(string s) => Quote(s, ',');

    public static string Quote(string s, char delimiter)
    {
        s = CsvSafety.Neutralize(s);
        return s.IndexOfAny([delimiter, '"', '\n', '\r']) >= 0 || s.Length > 0 && (s[0] == ' ' || s[^1] == ' ') && delimiter != ' '
            ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>Properties by the id of the classifier typing them (for "part of" lookups).</summary>
    public static ILookup<string, Element> PartIndex(ModelRepository m) =>
        m.OfKind(ElementKind.Property).Where(p => p.TypeRef.Length > 0 && p.Owner is { Kind: ElementKind.Block or ElementKind.Class or ElementKind.Component })
         .ToLookup(p => p.TypeRef, StringComparer.Ordinal);

    /// <summary>The blocks that use <paramref name="part"/> as a part, with the part property.</summary>
    public static IEnumerable<(Element Parent, Element Property)> PartUsages(ILookup<string, Element> index, Element part) =>
        index[part.Id].Where(p => p.Owner != null).Select(p => (p.Owner!, p));

    /// <summary>How a target element is written in a relationship / parent / part-of cell (the importer reads all of these back).</summary>
    public static string TargetLabel(ModelRepository m, Element e, SheetTargetMatch match, SheetMapping map) => new SheetLabeler(m, map).Label(e, match);

    public static Xlsx.Sheet ToSheet(ModelRepository m, SheetMapping map, IEnumerable<Element>? elements = null, Element? scope = null, string? sheetName = null)
    {
        var list = (elements ?? DefaultElements(m, map, scope)).Where(e => !e.IsRelationship).Distinct().ToList();
        var columns = map.Columns.Where(c => c.Role != SheetRole.Ignore && c.Column.Trim().Length > 0).ToList();
        var labeler = new SheetLabeler(m, map);
        var parts = PartIndex(m);
        var set = list.ToHashSet();
        var order = DocumentOrder(m);
        list.Sort((a, b) => order.GetValueOrDefault(a, int.MaxValue).CompareTo(order.GetValueOrDefault(b, int.MaxValue)));

        var levelMapped = columns.Any(c => c.Role == SheetRole.Level) || map.IndentHierarchy;
        var parentMapped = columns.Any(c => c.Role == SheetRole.Parent);
        var rows = new List<(Element E, int Level, Element? Parent, Element? Usage)>();
        if (map.Hierarchy == SheetHierarchy.PartOf && (levelMapped || parentMapped))
        {
            var usedIn = list.ToDictionary(e => e, e => PartUsages(parts, e).Where(u => set.Contains(u.Parent)).ToList());
            if (levelMapped)
            {
                // Bill of materials: depth-first from the top assemblies; a part used twice appears under each assembly.
                var emitted = new HashSet<Element>();
                void Walk(Element e, int level, Element? parent, Element? usage, HashSet<Element> path)
                {
                    rows.Add((e, level, parent, usage));
                    emitted.Add(e);
                    if (!path.Add(e)) return;
                    foreach (var prop in e.OwnedElements.Where(c => c.Kind == ElementKind.Property))
                        if (m.ResolveType(prop) is { } child && set.Contains(child) && !path.Contains(child)) Walk(child, level + 1, e, prop, path);
                    path.Remove(e);
                }
                foreach (var e in list.Where(e => usedIn[e].Count == 0)) Walk(e, 1, null, null, []);
                foreach (var e in list.Where(e => !emitted.Contains(e))) Walk(e, 1, null, null, []);
            }
            else
                foreach (var e in list)
                {
                    if (usedIn[e].Count == 0) rows.Add((e, 1, null, null));
                    foreach (var u in usedIn[e]) rows.Add((e, 1, u.Parent, u.Property));
                }
        }
        else if (levelMapped || parentMapped)
        {
            // Containment: children follow their parent, nested by owner.
            Element? ParentOf(Element e) => e.Owner != null && set.Contains(e.Owner) ? e.Owner : null;
            var children = list.Where(e => ParentOf(e) != null).ToLookup(e => ParentOf(e)!);
            void Walk(Element e, int level)
            {
                rows.Add((e, level, ParentOf(e), null));
                foreach (var c in children[e]) Walk(c, level + 1);
            }
            foreach (var e in list.Where(e => ParentOf(e) == null)) Walk(e, 1);
        }
        else rows.AddRange(list.Select(e => (e, 1, (Element?)null, (Element?)null)));

        var unitColumns = columns.Where(c => c.Role == SheetRole.Unit).Select(c => (c.Name ?? "").Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var data = new List<IReadOnlyList<string>>();
        foreach (var (e, level, parent, usage) in rows)
        {
            var cells = new List<string>(columns.Count);
            foreach (var c in columns)
            {
                string v;
                switch (c.Role)
                {
                    case SheetRole.Key: v = labeler.Key(e); break;
                    case SheetRole.Name:
                        v = map.IndentHierarchy && level > 1 ? new string(' ', (level - 1) * 2) + e.Name : e.Name;
                        break;
                    case SheetRole.Documentation: v = e.Documentation; break;
                    case SheetRole.Kind: v = SheetKinds.Of(m, e).Label; break;
                    case SheetRole.Owner: v = e.Owner is { Owner: not null } o ? o.QualifiedName : ""; break;
                    case SheetRole.Parent:
                        var p = parent ?? (map.Hierarchy == SheetHierarchy.Containment && e.Owner != null && set.Contains(e.Owner) ? e.Owner : null);
                        v = p == null ? "" : c.Match == SheetTargetMatch.Auto ? labeler.ParentLabel(p) : labeler.Label(p, c.Match);
                        break;
                    case SheetRole.Level: v = level.ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                    case SheetRole.Type:
                        v = e.Kind == ElementKind.Column ? e.Get(FieldKeys.SqlType) : Metamodel.IsTypedFeature(e.Kind) ? labeler.TypeLabel(e) : "";
                        break;
                    case SheetRole.Multiplicity:
                        v = usage?.Multiplicity ?? (Metamodel.IsTypedFeature(e.Kind) ? e.Multiplicity
                            : map.Hierarchy == SheetHierarchy.PartOf || columns.Any(x => x.Role == SheetRole.PartOf) ? PartUsages(parts, e).Select(u => u.Property.Multiplicity).FirstOrDefault() ?? "" : "");
                        break;
                    case SheetRole.Value:
                    {
                        var target = string.IsNullOrWhiteSpace(c.Name) ? (e.Kind is ElementKind.Property or ElementKind.Port ? e : null) : SheetImporter.OwnedValueProperty(e, c.Name!.Trim());
                        var withUnit = !unitColumns.Contains((c.Name ?? "").Trim());
                        v = target == null ? "" : withUnit && target.Get(ExtFieldKeys.Unit) is { Length: > 0 } u ? $"{target.Get(FieldKeys.DefaultValue)} {u}".Trim() : target.Get(FieldKeys.DefaultValue);
                        break;
                    }
                    case SheetRole.Unit:
                    {
                        var target = string.IsNullOrWhiteSpace(c.Name) ? e : SheetImporter.OwnedValueProperty(e, c.Name!.Trim());
                        v = target?.Get(ExtFieldKeys.Unit) ?? "";
                        break;
                    }
                    case SheetRole.Stereotypes: v = string.Join(", ", e.Stereotypes); break;
                    case SheetRole.Tag: v = TagValue(e, (c.Name ?? "").Trim()); break;
                    case SheetRole.Attribute: v = e.Get(ReqFields.CustomPrefix + (c.Name ?? "").Trim()); break;
                    case SheetRole.Field: v = e.Get((c.Name ?? "").Trim()); break;
                    case SheetRole.Relationship when c.RelationshipKind is { } rk:
                        v = string.Join("; ", ModelValues.Related(m, e, rk, c.Incoming ? RelDirection.In : RelDirection.Out).Distinct()
                                                         .OrderBy(x => order.GetValueOrDefault(x, int.MaxValue)).Select(x => labeler.Label(x, c.Match)));
                        break;
                    case SheetRole.PartOf:
                        v = string.Join("; ", PartUsages(parts, e).Select(u => labeler.Label(u.Parent, c.Match)));
                        break;
                    default: v = ""; break;
                }
                cells.Add(v);
            }
            data.Add(cells);
        }
        return new Xlsx.Sheet(sheetName ?? map.Name, columns.Select(c => c.Column).ToList(), data);
    }

    private static string TagValue(Element e, string name)
    {
        var v = e.Get(FieldKeys.TagPrefix + name);
        if (v.Length > 0) return v;
        foreach (var t in e.TaggedValues)
            if (string.Equals(t.Key, name, StringComparison.OrdinalIgnoreCase)) return t.Value;
        return "";
    }

    private static Dictionary<Element, int> DocumentOrder(ModelRepository m)
    {
        var order = new Dictionary<Element, int>();
        var i = 0;
        order[m.Root] = i++;
        foreach (var e in m.Root.Descendants()) order[e] = i++;
        return order;
    }
}

/// <summary>Writes references to elements the way the importer finds them again (requirement id, key, unique name or qualified name).</summary>
public sealed class SheetLabeler(ModelRepository m, SheetMapping map)
{
    private Dictionary<string, int>? _names;
    private Dictionary<string, int>? _classifierNames;

    private Dictionary<string, int> Names => _names ??= m.AllElements.Where(e => !e.IsRelationship && e.Kind is not (ElementKind.Diagram or ElementKind.Model) && e.Name.Length > 0)
        .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, int> ClassifierNames => _classifierNames ??= m.AllElements.Where(e => Metamodel.IsClassifier(e.Kind) && e.Name.Length > 0)
        .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The row key of an element under the mapping's key rule.</summary>
    public string Key(Element e) => map.KeyMatch switch
    {
        SheetKeyMatch.ElementId => e.Id,
        SheetKeyMatch.ExternalId => e.Get(map.KeyField),
        SheetKeyMatch.QualifiedName => e.QualifiedName,
        _ => e.Name,
    };

    /// <summary>A parent cell: the parent's key when it has one, else a reference.</summary>
    public string ParentLabel(Element e) => Key(e) is { Length: > 0 } k ? k : Label(e);

    public string Label(Element e, SheetTargetMatch match = SheetTargetMatch.Auto)
    {
        switch (match)
        {
            case SheetTargetMatch.ElementId: return e.Id;
            case SheetTargetMatch.QualifiedName: return e.QualifiedName;
            case SheetTargetMatch.Name: return e.Name;
            case SheetTargetMatch.RequirementId: return e.Get(FieldKeys.RequirementId) is { Length: > 0 } rid ? rid : e.Name;
            case SheetTargetMatch.Key: return Key(e) is { Length: > 0 } k ? k : e.Name;
        }
        if (e.Kind == ElementKind.Requirement && e.Get(FieldKeys.RequirementId) is { Length: > 0 } id) return id;
        if (map.KeyMatch == SheetKeyMatch.ExternalId && map.KeyField.Length > 0 && e.Get(map.KeyField) is { Length: > 0 } key) return key;
        return e.Name.Length > 0 && Names.TryGetValue(e.Name, out var n) && n == 1 && !e.Name.Contains("::") ? e.Name : e.QualifiedName;
    }

    /// <summary>A type cell: primitive name, unique classifier name, or qualified name.</summary>
    public string TypeLabel(Element feature)
    {
        if (m.ResolveType(feature) is not { } t) return feature.TypeRef;
        return ClassifierNames.TryGetValue(t.Name, out var n) && n == 1 && !t.Name.Contains("::") && !t.Name.Contains('/') ? t.Name : t.QualifiedName;
    }
}
