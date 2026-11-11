using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meridian.Core.CodeSync;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

/// <summary>How a relation map is drawn: rings around the root, or a tree growing right or down.</summary>
public enum RelationMapLayout { Radial, TreeRight, TreeDown }

/// <summary>Which relationships of an element a relation map follows.</summary>
public enum RelationDirection { Both, Outgoing, Incoming }

/// <summary>
/// What a relation map shows (Cameo's Relation Map): the root, how many levels, which relationship and element kinds, the
/// direction, containment, library and code-synced elements, the layout, and the nodes the user expanded or collapsed.
/// Kept on the view tab and, saved, as JSON in the <see cref="RelationMaps.Field"/> of a <see cref="ElementKind.RelationMap"/>.
/// </summary>
public sealed class RelationMapSettings
{
    public const int MinDepth = 1, MaxDepth = 6;
    /// <summary>Default node cap: beyond it, "+N more" stubs stand in for the rest.</summary>
    public const int DefaultMaxNodes = 500;
    /// <summary>Default neighbours shown per node before a "+N more" stub.</summary>
    public const int DefaultFanOut = 40;
    /// <summary>How many more neighbours one click on a stub shows.</summary>
    public const int StubStep = 40;

    public string? RootId { get; set; }
    /// <summary>The root's name when the map was made (for the tab title; the element itself is looked up by id).</summary>
    public string RootName { get; set; } = "";
    public int Depth { get; set; } = 2;
    /// <summary>Relationship kinds to follow; empty = every kind.</summary>
    public List<ElementKind> RelationshipKinds { get; set; } = new();
    /// <summary>Element kinds to show (the root always shows); empty = every kind.</summary>
    public List<ElementKind> ElementKinds { get; set; } = new();
    public RelationDirection Direction { get; set; } = RelationDirection.Both;
    /// <summary>Follow containment down: an element's owned elements.</summary>
    public bool IncludeOwned { get; set; }
    /// <summary>Follow containment up: an element's owner.</summary>
    public bool IncludeOwner { get; set; }
    /// <summary>Show elements of used projects (read-only, with their badge).</summary>
    public bool IncludeLibrary { get; set; } = true;
    /// <summary>Show elements that came from code sync.</summary>
    public bool IncludeCodeSynced { get; set; } = true;
    public RelationMapLayout Layout { get; set; } = RelationMapLayout.Radial;
    public int MaxNodes { get; set; } = DefaultMaxNodes;
    public int FanOut { get; set; } = DefaultFanOut;
    /// <summary>Nodes opened beyond the depth (ids).</summary>
    public List<string> Expanded { get; set; } = new();
    /// <summary>Nodes closed within the depth (ids).</summary>
    public List<string> Collapsed { get; set; } = new();
    /// <summary>Extra neighbours shown under a node after clicks on its "+N more" stub (node id → count).</summary>
    public Dictionary<string, int> More { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads saved settings; anything unreadable gives the defaults (a saved map is model data from anyone).</summary>
    public static RelationMapSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new RelationMapSettings();
        try { return (JsonSerializer.Deserialize<RelationMapSettings>(json, Json) ?? new RelationMapSettings()).Normalized(); }
        catch (JsonException) { return new RelationMapSettings(); }
    }

    public RelationMapSettings Clone() => FromJson(ToJson());

    /// <summary>Clamps numbers into their ranges (depth 1–6, cap 10–5000, fan-out 5–1000).</summary>
    public RelationMapSettings Normalized()
    {
        Depth = Math.Clamp(Depth, MinDepth, MaxDepth);
        MaxNodes = Math.Clamp(MaxNodes, 10, 5000);
        FanOut = Math.Clamp(FanOut, 5, 1000);
        RelationshipKinds ??= new(); ElementKinds ??= new(); Expanded ??= new(); Collapsed ??= new(); More ??= new();
        RelationshipKinds = RelationshipKinds.Where(Metamodel.IsRelationship).Distinct().ToList();
        return this;
    }

    /// <summary>Settings for a map of <paramref name="root"/>; a requirement starts on its traceability (satisfy, verify, derive, refine …).</summary>
    public static RelationMapSettings For(Element root)
    {
        var s = new RelationMapSettings { RootId = root.Id, RootName = root.DisplayName };
        if (root.Kind == ElementKind.Requirement) s.RelationshipKinds = RelationMapPresets.Traceability.ToList();
        return s;
    }

    /// <summary>The preset these relationship kinds are, or null for a custom choice.</summary>
    public string? Preset => RelationMapPresets.NameOf(RelationshipKinds);

    /// <summary>A change of root, depth or filters starts the exploration afresh (expanded / collapsed nodes are forgotten).</summary>
    public void ResetExploration() { Expanded.Clear(); Collapsed.Clear(); More.Clear(); }
}

/// <summary>Relationship-kind presets of the relation map's filter.</summary>
public static class RelationMapPresets
{
    public static readonly IReadOnlyList<ElementKind> Traceability =
        [ElementKind.Satisfy, ElementKind.Verify, ElementKind.DeriveReqt, ElementKind.Refine, ElementKind.Trace, ElementKind.Copy];
    public static readonly IReadOnlyList<ElementKind> Structure =
        [ElementKind.Association, ElementKind.Composition, ElementKind.Aggregation, ElementKind.Generalization, ElementKind.Realization,
         ElementKind.Dependency, ElementKind.Connector, ElementKind.Binding];
    public static readonly IReadOnlyList<ElementKind> Allocation = [ElementKind.Allocate];

    /// <summary>Name → kinds; "All" is the empty list (every relationship kind).</summary>
    public static readonly IReadOnlyList<(string Name, IReadOnlyList<ElementKind> Kinds)> All =
        [("Traceability", Traceability), ("Structure", Structure), ("Allocation", Allocation), ("All", [])];

    public static IReadOnlyList<ElementKind> Kinds(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Kinds ?? [];

    public static string? NameOf(IReadOnlyCollection<ElementKind> kinds)
    {
        foreach (var (name, k) in All)
            if (k.Count == kinds.Count && k.All(kinds.Contains)) return name;
        return null;
    }
}

/// <summary>A box on a relation map: an element, or a "+N more" stub standing for neighbours not shown.</summary>
public sealed class RelationMapNode
{
    /// <summary>The element id, or "more:&lt;parent id&gt;" for a stub.</summary>
    public required string Id { get; init; }
    public Element? Element { get; init; }
    public int Depth { get; init; }
    /// <summary>The node it was reached from (the tree parent), null for the root.</summary>
    public RelationMapNode? Parent { get; init; }
    public List<RelationMapNode> Children { get; } = new();
    /// <summary>Its neighbours were followed (it can be collapsed).</summary>
    public bool Explored { get; internal set; }
    /// <summary>Neighbours not on the map (expanding it shows them).</summary>
    public int Hidden { get; internal set; }
    public bool IsRoot => Parent == null && !IsStub;
    public bool IsStub { get; init; }
    /// <summary>For a stub: how many neighbours of <see cref="Parent"/> it stands for.</summary>
    public int MoreCount { get; internal set; }
    /// <summary>The used project this element comes from, or null.</summary>
    public string? Library { get; init; }
    public bool CodeSynced { get; init; }
    public double X { get; internal set; }
    public double Y { get; internal set; }
    public double W { get; internal set; }
    public double H { get; internal set; }
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;

    public string Name => Element is { } e ? (e.Kind == ElementKind.Requirement && e.Get(FieldKeys.RequirementId) is { Length: > 0 } id ? $"{id} {e.DisplayName}" : e.DisplayName) : $"+{MoreCount} more";
    public string KindName => Element is { } e ? Metamodel.DisplayName(e.Kind) : "";
}

/// <summary>A line on a relation map: a relationship (with its kind and «stereotype» label) or containment.</summary>
public sealed class RelationMapEdge
{
    public required RelationMapNode Source { get; init; }
    public required RelationMapNode Target { get; init; }
    /// <summary>The relationship, or null for containment (source owns target) and stub lines.</summary>
    public Relationship? Relationship { get; init; }
    public bool Containment { get; init; }
    public string KindName => Relationship is { } r ? Metamodel.DisplayName(r.Kind) : Containment ? "Containment" : "";
    /// <summary>«satisfy», plus any applied stereotypes, plus the relationship's name.</summary>
    public string Label
    {
        get
        {
            if (Relationship is not { } r) return Containment ? "owns" : "";
            var words = new List<string>();
            if (Metamodel.Style(r.Kind).Keyword is { Length: > 0 } kw) words.Add(kw);
            foreach (var st in r.Stereotypes) if (!words.Contains(st, StringComparer.OrdinalIgnoreCase)) words.Add(st);
            var stereo = words.Count > 0 ? "«" + string.Join(", ", words) + "»" : Metamodel.DisplayName(r.Kind);
            return string.IsNullOrEmpty(r.Name) ? stereo : stereo + " " + r.Name;
        }
    }
}

/// <summary>A built and laid-out relation map.</summary>
public sealed class RelationMapGraph
{
    public RelationMapNode? Root { get; internal set; }
    public List<RelationMapNode> Nodes { get; } = new();
    public List<RelationMapEdge> Edges { get; } = new();
    public double Width { get; internal set; }
    public double Height { get; internal set; }
    /// <summary>The node cap was reached: stubs stand for the rest.</summary>
    public bool Capped { get; internal set; }
    public TimeSpan BuildTime { get; internal set; }
    public int ElementCount => Nodes.Count(n => !n.IsStub);
    public RelationMapNode? Find(string id) => _byId.GetValueOrDefault(id);
    internal readonly Dictionary<string, RelationMapNode> _byId = new(StringComparer.Ordinal);
    /// <summary>The elements on the map (no stubs), in map order.</summary>
    public IEnumerable<Element> Elements => Nodes.Where(n => n.Element != null).Select(n => n.Element!);
}

/// <summary>
/// Builds relation maps: breadth-first from the root through the relationship index (<see cref="ModelRepository.RelationshipsOf"/>),
/// so the cost depends on what is shown, not on the model's size; then lays out only those nodes.
/// </summary>
public static class RelationMaps
{
    /// <summary>Field of a <see cref="ElementKind.RelationMap"/> element holding its <see cref="RelationMapSettings"/> as JSON.</summary>
    public const string Field = "relationMap";

    private const double NodeH = 46, StubW = 96, StubH = 28;

    /// <summary>
    /// Builds and lays out the map. <paramref name="isHidden"/> says what this user may not see (package permissions):
    /// such elements and relationships never appear. Returns an empty graph when the root is missing or hidden.
    /// </summary>
    public static RelationMapGraph Build(ModelRepository m, RelationMapSettings s, Func<Element, bool>? isHidden = null)
    {
        var sw = Stopwatch.StartNew();
        s.Normalized();
        var g = new RelationMapGraph();
        if (m.Find(s.RootId) is not { } root || root is Relationship || isHidden?.Invoke(root) == true) return g;
        var f = new Filter(m, s, isHidden);
        var expanded = s.Expanded.ToHashSet(StringComparer.Ordinal);
        var collapsed = s.Collapsed.ToHashSet(StringComparer.Ordinal);
        var cap = s.MaxNodes + s.More.Values.Where(v => v > 0).Sum();

        g.Root = Add(g, m, root, 0, null);
        var queue = new Queue<RelationMapNode>();
        queue.Enqueue(g.Root);
        var overflow = new List<RelationMapNode>();
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            var explore = !collapsed.Contains(n.Id) && (n.Depth < s.Depth || expanded.Contains(n.Id));
            if (!explore) continue;
            n.Explored = true;
            var limit = s.FanOut + Math.Max(0, s.More.GetValueOrDefault(n.Id));
            var added = 0;
            var over = false;
            foreach (var (other, _, _) in f.Neighbours(n.Element!))
            {
                if (g._byId.ContainsKey(other.Id)) continue;
                if (added >= limit || g.Nodes.Count >= cap) { over = true; if (g.Nodes.Count >= cap) g.Capped = true; continue; }
                var child = Add(g, m, other, n.Depth + 1, n);
                n.Children.Add(child);
                queue.Enqueue(child);
                added++;
            }
            if (over) overflow.Add(n);
        }

        // "+N more" stubs: what is still off the map once everything reachable within the cap is placed.
        foreach (var n in overflow)
        {
            var rest = f.Neighbours(n.Element!).Count(x => !g._byId.ContainsKey(x.Other.Id));
            if (rest == 0) continue;
            var stub = new RelationMapNode { Id = "more:" + n.Id, Depth = n.Depth + 1, Parent = n, IsStub = true, MoreCount = rest };
            n.Children.Add(stub);
            g.Nodes.Add(stub);
            g._byId[stub.Id] = stub;
        }
        // Closed nodes say how much is behind them.
        foreach (var n in g.Nodes.Where(n => !n.IsStub && !n.Explored).ToList())
            n.Hidden = f.Neighbours(n.Element!).Count(x => !g._byId.ContainsKey(x.Other.Id));

        Edges(g, f);
        Layout(g, s.Layout);
        g.BuildTime = sw.Elapsed;
        return g;
    }

    private static RelationMapNode Add(RelationMapGraph g, ModelRepository m, Element e, int depth, RelationMapNode? parent)
    {
        var n = new RelationMapNode
        {
            Id = e.Id, Element = e, Depth = depth, Parent = parent,
            Library = m.LibraryOf(e) is { } lib ? $"{lib.Info.SourceProjectName} ({lib.Info.PinLabel})" : null, CodeSynced = IsCodeSynced(e),
        };
        g.Nodes.Add(n);
        g._byId[n.Id] = n;
        return n;
    }

    public static bool IsCodeSynced(Element e) => e.Fields.ContainsKey(CodeFieldKeys.Symbol);

    /// <summary>Every relationship between two elements on the map (once), containment lines, and stub lines.</summary>
    private static void Edges(RelationMapGraph g, Filter f)
    {
        var seen = new HashSet<Relationship>(ReferenceEqualityComparer.Instance);
        var owns = new HashSet<(string, string)>();
        foreach (var n in g.Nodes)
        {
            if (n.IsStub) { g.Edges.Add(new RelationMapEdge { Source = n.Parent!, Target = n }); continue; }
            foreach (var r in f.Relationships(n.Element!))
            {
                if (!seen.Add(r)) continue;
                if (g.Find(r.Source.Id) is { } a && g.Find(r.Target.Id) is { } b && !ReferenceEquals(a, b))
                    g.Edges.Add(new RelationMapEdge { Source = a, Target = b, Relationship = r });
            }
            if (f.Containment && n.Element!.Owner is { } owner && g.Find(owner.Id) is { } o && owns.Add((o.Id, n.Id)))
                g.Edges.Add(new RelationMapEdge { Source = o, Target = n, Containment = true });
        }
    }

    /// <summary>What the map follows from one element, in a stable order.</summary>
    private sealed class Filter(ModelRepository m, RelationMapSettings s, Func<Element, bool>? isHidden)
    {
        private readonly HashSet<ElementKind>? _rel = s.RelationshipKinds.Count > 0 ? s.RelationshipKinds.ToHashSet() : null;
        private readonly HashSet<ElementKind>? _kinds = s.ElementKinds.Count > 0 ? s.ElementKinds.ToHashSet() : null;
        private readonly Dictionary<Element, List<(Element Other, Relationship? Via, bool Containment)>> _cache = new(ReferenceEqualityComparer.Instance);
        public bool Containment => s.IncludeOwned || s.IncludeOwner;

        private bool Shown(Element e) =>
            m.Contains(e) && e is not Relationship && e is not Diagram
            && (_kinds == null || _kinds.Contains(e.Kind))
            && (s.IncludeLibrary || !m.IsLibraryElement(e))
            && (s.IncludeCodeSynced || !IsCodeSynced(e))
            && isHidden?.Invoke(e) != true;

        /// <summary>The relationships of <paramref name="e"/> the map follows (kind, direction, permissions).</summary>
        public IEnumerable<Relationship> Relationships(Element e)
        {
            foreach (var r in m.RelationshipsOf(e))
            {
                if (_rel != null && !_rel.Contains(r.Kind)) continue;
                var outgoing = ReferenceEquals(r.Source, e);
                if (s.Direction == RelationDirection.Outgoing && !outgoing) continue;
                if (s.Direction == RelationDirection.Incoming && outgoing && !ReferenceEquals(r.Target, e)) continue;
                if (isHidden?.Invoke(r) == true) continue;
                yield return r;
            }
        }

        /// <summary>Distinct neighbours (first relationship wins), sorted by relationship kind, element kind, name and id.</summary>
        public List<(Element Other, Relationship? Via, bool Containment)> Neighbours(Element e)
        {
            if (_cache.TryGetValue(e, out var cached)) return cached;
            var list = new List<(Element Other, Relationship? Via, bool Containment, int Order)>();
            if (s.IncludeOwner && e.Owner is { } owner && Shown(owner)) list.Add((owner, null, true, -2));
            if (s.IncludeOwned)
                foreach (var c in e.OwnedElements)
                    if (Shown(c)) list.Add((c, null, true, -1));
            foreach (var r in Relationships(e))
            {
                var other = ReferenceEquals(r.Source, e) ? r.Target : r.Source;
                if (ReferenceEquals(other, e) || !Shown(other)) continue;
                list.Add((other, r, false, (int)r.Kind));
            }
            list.Sort((a, b) =>
            {
                var c = a.Order.CompareTo(b.Order);
                if (c == 0) c = a.Other.Kind.CompareTo(b.Other.Kind);
                if (c == 0) c = string.Compare(a.Other.DisplayName, b.Other.DisplayName, StringComparison.OrdinalIgnoreCase);
                if (c == 0) c = string.CompareOrdinal(a.Other.Id, b.Other.Id);
                return c;
            });
            var seen = new HashSet<Element>(ReferenceEqualityComparer.Instance);
            var result = list.Where(x => seen.Add(x.Other)).Select(x => (x.Other, x.Via, x.Containment)).ToList();
            _cache[e] = result;
            return result;
        }
    }

    // ================================================================== layout

    /// <summary>Box size of a node from its texts (no font metrics on the server: ~6.6 px per character at 12 px).</summary>
    private static void Size(RelationMapNode n)
    {
        if (n.IsStub) { n.W = StubW; n.H = StubH; return; }
        var chars = Math.Max(Math.Min(n.Name.Length, 30), n.KindName.Length + 4);
        n.W = Math.Clamp(46 + chars * 6.6, 130, 250);
        n.H = NodeH;
    }

    private static void Layout(RelationMapGraph g, RelationMapLayout layout)
    {
        if (g.Root is not { } root) return;
        foreach (var n in g.Nodes) Size(n);
        switch (layout)
        {
            case RelationMapLayout.Radial: Radial(g, root); break;
            default: Tree(g, root, layout == RelationMapLayout.TreeRight); break;
        }
        // Move everything to positive coordinates with a margin.
        const double margin = 30;
        var minX = g.Nodes.Min(n => n.X); var minY = g.Nodes.Min(n => n.Y);
        foreach (var n in g.Nodes) { n.X = Math.Round(n.X - minX + margin, 1); n.Y = Math.Round(n.Y - minY + margin, 1); }
        g.Width = g.Nodes.Max(n => n.X + n.W) + margin;
        g.Height = g.Nodes.Max(n => n.Y + n.H) + margin;
    }

    private static int Leaves(RelationMapNode n, Dictionary<RelationMapNode, int> memo)
    {
        if (memo.TryGetValue(n, out var v)) return v;
        var count = n.Children.Count == 0 ? 1 : n.Children.Sum(c => Leaves(c, memo));
        memo[n] = count;
        return count;
    }

    /// <summary>Rings around the root: each subtree gets an angle in proportion to its leaves; the outer ring is wide enough for every leaf.</summary>
    private static void Radial(RelationMapGraph g, RelationMapNode root)
    {
        var memo = new Dictionary<RelationMapNode, int>(ReferenceEqualityComparer.Instance);
        var leaves = Leaves(root, memo);
        var depth = Math.Max(1, g.Nodes.Max(n => n.Depth));
        var widest = g.Nodes.Max(n => n.W);
        var outer = Math.Max(depth * (widest * 0.75 + 90), leaves * 64 / (2 * Math.PI));
        var step = outer / depth;
        void Place(RelationMapNode n, double from, double to)
        {
            var r = n.Depth * step;
            var a = (from + to) / 2;
            n.X = r * Math.Cos(a) - n.W / 2;
            n.Y = r * Math.Sin(a) - n.H / 2;
            var start = from;
            foreach (var c in n.Children)
            {
                var span = (to - from) * Leaves(c, memo) / Math.Max(1, Leaves(n, memo));
                Place(c, start, start + span);
                start += span;
            }
        }
        Place(root, -Math.PI, Math.PI);
    }

    /// <summary>A tidy tree: leaves one after another, each parent centred on its children; levels in columns (right) or rows (down).</summary>
    private static void Tree(RelationMapGraph g, RelationMapNode root, bool right)
    {
        var depth = g.Nodes.Max(n => n.Depth);
        // Level offsets: the widest (right) or tallest (down) box of each level plus a gap.
        var level = new double[depth + 2];
        for (var d = 0; d <= depth; d++)
        {
            var size = g.Nodes.Where(n => n.Depth == d).Select(n => right ? n.W : n.H).DefaultIfEmpty(0).Max();
            level[d + 1] = level[d] + size + (right ? 90 : 80);
        }
        var cursor = 0.0;
        double Place(RelationMapNode n)
        {
            double mid;
            if (n.Children.Count == 0)
            {
                var extent = right ? n.H : n.W;
                mid = cursor + extent / 2;
                cursor += extent + (right ? 14 : 24);
            }
            else
            {
                var first = Place(n.Children[0]);
                var last = first;
                for (var i = 1; i < n.Children.Count; i++) last = Place(n.Children[i]);
                mid = (first + last) / 2;
            }
            if (right) { n.X = level[n.Depth]; n.Y = mid - n.H / 2; }
            else { n.X = mid - n.W / 2; n.Y = level[n.Depth]; }
            return mid;
        }
        Place(root);
    }

    // ================================================================== saved maps and diagrams

    /// <summary>The saved relation maps of the model, by name.</summary>
    public static IEnumerable<Element> Saved(ModelRepository m) =>
        m.OfKind(ElementKind.RelationMap).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);

    public static RelationMapSettings Load(Element saved) => RelationMapSettings.FromJson(saved.Get(Field));

    /// <summary>Saves the settings as a <see cref="ElementKind.RelationMap"/> element (new, or <paramref name="existing"/>), one undo step.</summary>
    public static Element Save(ModelRepository m, RelationMapSettings s, string name, Element? existing = null, Element? owner = null)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Relation map" : name.Trim();
        var json = s.ToJson();
        using (m.History.Transaction($"Save relation map {name}"))
        {
            Element e;
            if (existing != null && m.Contains(existing) && existing.Kind == ElementKind.RelationMap)
            {
                e = existing;
                if (e.Name != name) m.Rename(e, name);
            }
            else
            {
                var home = owner != null && m.Contains(owner) && Metamodel.CanOwn(owner.Kind, ElementKind.RelationMap) ? owner : SavedViews.DefaultOwner(m);
                e = m.Create(home, ElementKind.RelationMap, name);
            }
            if (e.Get(Field) != json) m.Set(e, Field, json);
            return e;
        }
    }

    /// <summary>The diagram type that suits these elements: a requirement diagram when requirements are among them, else a block definition diagram.</summary>
    public static DiagramType DiagramTypeFor(IEnumerable<Element> elements) =>
        elements.Any(e => e.Kind is ElementKind.Requirement or ElementKind.TestCase) ? DiagramType.Requirement : DiagramType.BlockDefinition;

    /// <summary>
    /// A real diagram of <paramref name="elements"/> and the relationships between them, laid out by the auto-diagrammer,
    /// in <paramref name="owner"/> (one undo step).
    /// </summary>
    public static Diagram CreateDiagram(ModelRepository m, IReadOnlyList<Element> elements, Element owner, string name)
    {
        var shown = elements.Where(e => m.Contains(e) && e is not Relationship && e is not Diagram && e.Kind != ElementKind.Model).Distinct().ToList();
        if (shown.Count == 0) throw new InvalidOperationException("Select at least one element on the map.");
        var plan = new AutoDiagramPlan();
        plan.Diagrams.Add(new PlannedDiagram
        {
            Type = DiagramTypeFor(shown), Owner = owner, Name = name, Elements = shown,
            Purpose = "Made from a relation map.", LeftToRight = false,
        });
        var result = AutoDiagrammer.Apply(m, plan, addDependencies: false);
        return result.All.Single();
    }
}
