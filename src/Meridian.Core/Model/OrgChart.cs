using Meridian.Core.Language;

namespace Meridian.Core.Model;

/// <summary>One person of an org chart, as read from a spreadsheet row.</summary>
/// <param name="Id">The row's id, when the sheet has an id column.</param>
/// <param name="Manager">The manager as written in the sheet: an id or a name. Empty for people at the top.</param>
/// <param name="Row">Sheet row number (1-based) for warnings; 0 when not from a sheet.</param>
public sealed record OrgPerson(string Name, string? Title = null, string? Department = null, string? Manager = null, string? Id = null, string? Email = null, int Row = 0);

/// <summary>People resolved into a forest: who reports to whom, the people at the top, and what could not be resolved.</summary>
public sealed class OrgTree
{
    /// <summary>Everyone, in sheet order, with a unique key each.</summary>
    public required IReadOnlyList<(string Key, OrgPerson Person)> People { get; init; }

    /// <summary>Manager key of each person who has one.</summary>
    public required IReadOnlyDictionary<string, string> ManagerOf { get; init; }

    /// <summary>Direct reports of each manager, in sheet order.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> ReportsOf { get; init; }

    /// <summary>People without a (resolvable) manager, in sheet order.</summary>
    public required IReadOnlyList<string> Roots { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public OrgPerson this[string key] => People.First(p => p.Key == key).Person;
}

/// <summary>What <see cref="OrgChart.Build"/> did.</summary>
public sealed record OrgChartResult(Diagram Diagram, int Added, int Updated, int Removed, bool Replaced);

/// <summary>
/// Org charts on whiteboards: people from a spreadsheet become cards (free shapes with subtype <c>card</c>: name in
/// bold, title and department below, a department colour band) laid out top-down as a tree, with plain lines from each
/// manager to their reports. Everything is ordinary shapes, fields and lines, so the chart edits, exports and
/// replicates like any whiteboard. Importing the same file again updates the same diagram.
/// </summary>
public static class OrgChart
{
    public const string CardSubtype = "card";
    public const string TitleField = "org.title";
    public const string DepartmentField = "org.department";
    public const string EmailField = "org.email";
    /// <summary>The person's key (the sheet's id, else their name) on each card, used to update a chart in place.</summary>
    public const string KeyField = "org.key";
    /// <summary>On the diagram: the file the chart was imported from.</summary>
    public const string SourceField = "org.source";
    /// <summary>Card style: colour of the department band.</summary>
    public const string AccentStyle = "accent";

    public const double CardWidth = 180;
    public const double CardHeight = 70;
    public const double HGap = 24;
    public const double VGap = 56;
    public const double RootGap = 60;
    /// <summary>More direct reports than this, all without reports of their own, are stacked in two columns.</summary>
    public const int CompactAbove = 4;
    private const double TrunkGap = 36;
    private const double RowGap = 16;

    /// <summary>Soft colours for department bands, assigned in order of first appearance.</summary>
    public static readonly IReadOnlyList<string> DepartmentColors =
        ["#4f8cff", "#3fbf7f", "#f5a524", "#e5484d", "#8e4ec6", "#12a594", "#d6409f", "#978365"];

    public static bool IsCard(Element e) => e.Kind == ElementKind.Shape && e.Get(FieldKeys.Subtype) == CardSubtype;

    // ================================================================ resolving managers

    /// <summary>
    /// Resolves each person's manager by id, then by name (both ignore case). Unknown managers, people managing
    /// themselves and loops become warnings; the people involved are put at the top of the chart.
    /// </summary>
    public static OrgTree Resolve(IReadOnlyList<OrgPerson> people)
    {
        var warnings = new List<string>();
        var list = new List<(string Key, OrgPerson Person)>();
        var byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in people)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) continue;
            var key = string.IsNullOrWhiteSpace(p.Id) ? p.Name.Trim() : p.Id.Trim();
            if (byKey.ContainsKey(key))
            {
                var unique = key;
                for (var i = 2; byKey.ContainsKey(unique); i++) unique = $"{key} ({i})";
                warnings.Add(string.IsNullOrWhiteSpace(p.Id)
                    ? $"{Where(p)}two people are called “{p.Name.Trim()}”; reports naming them go to the first one."
                    : $"{Where(p)}the id “{key}” is used twice; the second one is shown as “{unique}”.");
                key = unique;
            }
            byKey[key] = key;
            byName.TryAdd(p.Name.Trim(), key);
            list.Add((key, p));
        }

        var managerOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, p) in list)
        {
            var m = p.Manager?.Trim();
            if (string.IsNullOrEmpty(m)) continue;
            var found = byKey.TryGetValue(m, out var k) ? k : byName.TryGetValue(m, out var n) ? n : null;
            if (found == null) { warnings.Add($"{Where(p)}the manager “{m}” of {p.Name.Trim()} is not in the sheet; {p.Name.Trim()} is shown at the top."); continue; }
            if (found == key) { warnings.Add($"{Where(p)}{p.Name.Trim()} is listed as their own manager; shown at the top."); continue; }
            managerOf[key] = found;
        }

        // Loops: follow managers from each person; a person met twice on one walk closes a loop. The loop is cut at its
        // first member in sheet order, who goes to the top.
        var order = list.Select((x, i) => (x.Key, i)).ToDictionary(x => x.Key, x => x.i);
        var state = new Dictionary<string, int>();   // 1 = on the current walk, 2 = done
        foreach (var (start, _) in list)
        {
            if (state.ContainsKey(start)) continue;
            var walk = new List<string>();
            var cur = start;
            while (cur != null && !state.ContainsKey(cur))
            {
                state[cur] = 1;
                walk.Add(cur);
                cur = managerOf.TryGetValue(cur, out var next) ? next : null;
            }
            if (cur != null && state[cur] == 1)
            {
                var loop = walk.SkipWhile(w => w != cur).ToList();
                var cut = loop.OrderBy(w => order[w]).First();
                managerOf.Remove(cut);
                var names = loop.Select(w => list[order[w]].Person.Name.Trim()).ToList();
                warnings.Add($"{Where(list[order[cut]].Person)}{Join(names)} report to each other in a loop; {list[order[cut]].Person.Name.Trim()} is shown at the top.");
            }
            foreach (var w in walk) state[w] = 2;
        }

        var reports = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, _) in list)
            if (managerOf.TryGetValue(key, out var mgr))
                (reports.TryGetValue(mgr, out var l) ? l : reports[mgr] = []).Add(key);
        return new OrgTree
        {
            People = list,
            ManagerOf = managerOf,
            ReportsOf = reports.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value),
            Roots = list.Where(x => !managerOf.ContainsKey(x.Key)).Select(x => x.Key).ToList(),
            Warnings = warnings,
        };
    }

    private static string Where(OrgPerson p) => p.Row > 0 ? $"Row {p.Row}: " : "";

    private static string Join(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    // ================================================================ layout

    /// <summary>
    /// Top-down tree layout: each manager centred above their reports, subtrees side by side, trees of several people at
    /// the top side by side. A manager with many reports who have no reports themselves gets a compact two-column stack
    /// instead of one very wide row. Iterative, so very deep charts are fine.
    /// </summary>
    public static Dictionary<string, Bounds> Layout(OrgTree tree, double originX = 40, double originY = 40)
    {
        var children = tree.ReportsOf;
        IReadOnlyList<string> Kids(string k) => children.TryGetValue(k, out var l) ? l : [];
        bool Compact(string k) { var c = Kids(k); return c.Count > CompactAbove && c.All(x => Kids(x).Count == 0); }

        // Breadth-first order from the roots: parents before children.
        var bfs = new List<string>();
        var depth = new Dictionary<string, int>();
        foreach (var r in tree.Roots) { bfs.Add(r); depth[r] = 0; }
        for (var i = 0; i < bfs.Count; i++)
            foreach (var c in Kids(bfs[i]))
                if (depth.TryAdd(c, depth[bfs[i]] + 1)) bfs.Add(c);

        // Children before parents: the width of each subtree, where its manager's centre sits in it (`anchor`), and
        // where its children's block starts (`kidsAt`). A manager is centred between their first and last report.
        var width = new Dictionary<string, double>();
        var anchor = new Dictionary<string, double>();
        var kidsAt = new Dictionary<string, double>();
        for (var i = bfs.Count - 1; i >= 0; i--)
        {
            var k = bfs[i];
            var kids = Kids(k);
            if (kids.Count == 0) { width[k] = CardWidth; anchor[k] = CardWidth / 2; continue; }
            if (Compact(k)) { width[k] = 2 * CardWidth + TrunkGap; anchor[k] = width[k] / 2; continue; }
            var total = kids.Sum(c => width[c]) + HGap * (kids.Count - 1);
            var mid = (anchor[kids[0]] + total - width[kids[^1]] + anchor[kids[^1]]) / 2;
            var shift = Math.Max(0, CardWidth / 2 - mid);
            width[k] = Math.Max(total + shift, mid + shift + CardWidth / 2);
            anchor[k] = mid + shift;
            kidsAt[k] = shift;
        }

        // Positions, parents before children: each subtree gets a horizontal slot starting at `left`.
        var left = new Dictionary<string, double>();
        var result = new Dictionary<string, Bounds>();
        var x = originX;
        foreach (var r in tree.Roots) { left[r] = x; x += width[r] + RootGap; }
        foreach (var k in bfs)
        {
            if (!left.TryGetValue(k, out var slot)) continue;   // placed by a compact stack
            var cx = slot + anchor[k];
            var y = originY + depth[k] * (CardHeight + VGap);
            result[k] = new Bounds(cx - CardWidth / 2, y, CardWidth, CardHeight);
            var kids = Kids(k);
            if (kids.Count == 0) continue;
            if (Compact(k))
            {
                for (var i = 0; i < kids.Count; i++)
                {
                    var row = i / 2;
                    var bx = i % 2 == 0 ? cx - TrunkGap / 2 - CardWidth : cx + TrunkGap / 2;
                    result[kids[i]] = new Bounds(bx, y + CardHeight + VGap + row * (CardHeight + RowGap), CardWidth, CardHeight);
                }
                continue;
            }
            var cl = slot + kidsAt[k];
            foreach (var c in kids) { left[c] = cl; cl += width[c] + HGap; }
        }
        return result;
    }

    // ================================================================ building the diagram

    /// <summary>The org chart imported from <paramref name="source"/> before, under <paramref name="owner"/> (or anywhere when owner is the root).</summary>
    public static Diagram? FindImported(ModelRepository m, Element owner, string source) =>
        m.Diagrams.FirstOrDefault(d => d.DiagramType == DiagramType.Whiteboard && string.Equals(d.Get(SourceField), source, StringComparison.OrdinalIgnoreCase)
            && (ReferenceEquals(owner, m.Root) || owner.IsAncestorOf(d)));

    /// <summary>
    /// Draws <paramref name="tree"/> as a whiteboard in <paramref name="owner"/>, or, when <paramref name="source"/> was
    /// imported before, updates that diagram in place: cards are matched by key, changed people are updated, people no
    /// longer in the sheet are deleted, and the chart is laid out again. One undo step.
    /// </summary>
    public static OrgChartResult Build(ModelRepository m, Element owner, OrgTree tree, string name, string? source = null)
    {
        var existing = source is { Length: > 0 } ? FindImported(m, owner, source) : null;
        var layout = Layout(tree);
        int added = 0, updated = 0, removed = 0;
        using (m.History.Transaction(existing != null ? "Update org chart" : "Import org chart"))
        {
            Diagram d;
            Element pkg;
            if (existing != null)
            {
                d = existing;
                pkg = d.Owner ?? owner;
            }
            else
            {
                pkg = owner;
                d = m.CreateDiagram(owner, DiagramType.Whiteboard, string.IsNullOrWhiteSpace(name) ? "Org chart" : name.Trim());
                m.Set(d, EdgeRouter.DiagramKey, EdgeRouter.Format(EdgeRouting.Orthogonal));
                if (source is { Length: > 0 }) m.Set(d, SourceField, source);
            }

            var cards = d.Nodes.Where(n => IsCard(n.Element) && n.Element.Get(KeyField) is { Length: > 0 }).GroupBy(n => n.Element.Get(KeyField)!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var nodeOf = new Dictionary<string, DiagramNode>(StringComparer.Ordinal);
            var bounds = new Dictionary<DiagramNode, Bounds>();
            foreach (var (key, p) in tree.People)
            {
                if (!layout.TryGetValue(key, out var b)) continue;
                var dept = p.Department?.Trim() ?? "";
                var color = dept.Length == 0 ? null : colors.TryGetValue(dept, out var c) ? c : colors[dept] = DepartmentColors[colors.Count % DepartmentColors.Count];
                DiagramNode node;
                if (cards.Remove(key, out var old))
                {
                    node = old;
                    var e = node.Element;
                    var changed = e.Name != p.Name.Trim() || e.Get(TitleField) != (p.Title?.Trim() ?? "") || e.Get(DepartmentField) != dept;
                    SetPerson(m, e, p, dept);
                    if (changed) updated++;
                    bounds[node] = b;
                }
                else
                {
                    var e = m.Create(pkg, ElementKind.Shape, p.Name.Trim());
                    m.Set(e, FieldKeys.Subtype, CardSubtype);
                    m.Set(e, KeyField, key);
                    SetPerson(m, e, p, dept);
                    node = m.AddNode(d, e, b, showRelationships: false);
                    added++;
                }
                if (node.Style.GetValueOrDefault(AccentStyle) != color) m.SetNodeStyle(d, node, AccentStyle, color);
                nodeOf[key] = node;
            }
            // People who left the sheet.
            if (cards.Count > 0)
            {
                removed = cards.Count;
                m.Delete(cards.Values.Select(n => n.Element).ToList());
            }
            if (bounds.Count > 0) m.SetBounds(d, bounds, "Lay out org chart");

            // Reporting lines: keep the ones still true, delete the rest, add the new ones.
            var wanted = tree.ManagerOf.Where(kv => nodeOf.ContainsKey(kv.Key) && nodeOf.ContainsKey(kv.Value))
                .Select(kv => (Manager: nodeOf[kv.Value], Report: nodeOf[kv.Key])).ToHashSet();
            var stale = new List<Element>();
            foreach (var edge in d.Edges.Where(x => x.Relationship.Kind == ElementKind.Line && IsCard(x.Source.Element) && IsCard(x.Target.Element)).ToList())
            {
                if (wanted.Remove((edge.Source, edge.Target))) continue;
                stale.Add(edge.Relationship);
            }
            if (stale.Count > 0) m.Delete(stale);
            foreach (var (mgr, report) in wanted)
            {
                var line = m.Connect(ElementKind.Line, mgr.Element, report.Element, pkg);
                m.Set(line, FieldKeys.Subtype, "line");
                m.AddEdge(d, line);
            }
            return new OrgChartResult(d, added, updated, removed, existing != null);
        }
    }

    private static void SetPerson(ModelRepository m, Element e, OrgPerson p, string dept)
    {
        void Put(string key, string? value)
        {
            value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (e.Get(key) != (value ?? "")) m.Set(e, key, value);
        }
        Put(FieldKeys.Name, p.Name);
        Put(TitleField, p.Title);
        Put(DepartmentField, dept);
        Put(EmailField, p.Email);
    }
}
