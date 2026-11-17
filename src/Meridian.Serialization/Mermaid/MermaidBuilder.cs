using System.Text.RegularExpressions;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization.Mermaid;

/// <summary>Turns parsed Mermaid diagrams into model elements, relationships and laid-out diagrams.</summary>
internal sealed class MermaidBuilder(ModelRepository m, Element target, MermaidImportOptions options, List<string> warnings)
{
    private static readonly ElementKind[] StructuralKinds =
    [
        ElementKind.Block, ElementKind.InterfaceBlock, ElementKind.ValueType, ElementKind.Enumeration, ElementKind.Class, ElementKind.Interface,
        ElementKind.DataType, ElementKind.Component, ElementKind.ConstraintBlock, ElementKind.Actor,
    ];

    /// <summary>Structural elements made by this import, by name (shared between the diagrams of one input).</summary>
    private readonly Dictionary<string, List<Element>> _made = new(StringComparer.Ordinal);

    /// <summary>The model's elements by name (match-by-name mode only), built on first use.</summary>
    private Dictionary<string, List<Element>>? _existing;

    private readonly Dictionary<MermaidDiagramKind, int> _numbers = new();

    /// <summary>Relationships made by this import (kind, source, target), so a link stated twice is made once.</summary>
    private readonly HashSet<(ElementKind, Element, Element)> _links = new();

    /// <summary>Where the diagrams come from, for the diagram's purpose text ("Mermaid", "draw.io").</summary>
    public string Source { get; init; } = "Mermaid";

    /// <summary>The elements the last <see cref="Build"/> made for each node id (flowcharts, state and class diagrams).</summary>
    public Dictionary<string, Element>? LastMade { get; private set; }

    /// <summary>Builds one diagram. <paramref name="fallbackName"/> names a diagram without a title (else "Flowchart 1"…).</summary>
    public Diagram? Build(MGraph g, string? fallbackName = null)
    {
        LastMade = null;
        var name = DiagramName(g, fallbackName);
        try
        {
            return g.Kind switch
            {
                MermaidDiagramKind.Flowchart => Flowchart(g, name),
                MermaidDiagramKind.State => State(g, name),
                MermaidDiagramKind.Class => Class(g, name),
                MermaidDiagramKind.Sequence => Sequence(g, name),
                MermaidDiagramKind.Requirement => Requirement(g, name),
                MermaidDiagramKind.EntityRelationship => Er(g, name),
                MermaidDiagramKind.C4 => C4(g, name),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            warnings.Add($"{g.Describe()} could not be imported completely: {ex.Message}");
            return null;
        }
    }

    /// <summary>The front matter / accTitle title, else "Flowchart 1", "Flowchart 2"… unique among the model's diagrams.</summary>
    private string DiagramName(MGraph g, string? fallback = null)
    {
        if (g.Title is { Length: > 0 } t) return MermaidSource.Name(t);
        var used = m.Diagrams.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        if (fallback is { Length: > 0 })
        {
            var baseName = MermaidSource.Name(fallback);
            var candidateName = baseName;
            for (var i = 2; used.Contains(candidateName); i++) candidateName = $"{baseName} {i}";
            return candidateName;
        }
        var type = MermaidImporter.TypeName(g.Kind);
        var n = _numbers.GetValueOrDefault(g.Kind);
        string candidate;
        do candidate = $"{type} {++n}"; while (used.Contains(candidate));
        _numbers[g.Kind] = n;
        return candidate;
    }

    // ================================================================== shared helpers

    /// <summary>The package new elements go to; a folder import moves it from folder to folder.</summary>
    public Element Target { get => target; set => target = value; }

    /// <summary>For other importers sharing this one's elements by name (draw.io).</summary>
    internal Element? FindShared(string name, params ElementKind[] kinds) => Find(name, kinds);
    internal Element MakeShared(Element owner, ElementKind kind, string? name) => Make(owner, kind, name);
    internal Relationship? ConnectShared(MGraph g, ElementKind kind, Element a, Element b, Element? owner = null) => Connect(g, 0, kind, a, b, owner);

    private Element? Find(string name, params ElementKind[] kinds)
    {
        if (name.Length == 0) return null;
        if (_made.TryGetValue(name, out var mine) && mine.FirstOrDefault(e => kinds.Contains(e.Kind) && m.Contains(e)) is { } own) return own;
        if (options.Match != MermaidMatchMode.MatchByName) return null;
        _existing ??= m.AllElements.Where(e => e is not Relationship && e is not Diagram && e.Name.Length > 0)
            .GroupBy(e => e.Name, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
        if (!_existing.TryGetValue(name, out var list)) return null;
        // Prefer an element inside the import target.
        return list.Where(e => kinds.Contains(e.Kind) && m.Contains(e)).OrderBy(e => target.IsAncestorOf(e) ? 0 : 1).FirstOrDefault();
    }

    private Element Make(Element owner, ElementKind kind, string? name)
    {
        var e = m.Create(owner, kind, string.IsNullOrEmpty(name) ? null : name);
        if (!string.IsNullOrEmpty(name) && !Metamodel.IsRelationship(kind))
        {
            if (!_made.TryGetValue(name, out var l)) _made[name] = l = new List<Element>();
            l.Add(e);
        }
        return e;
    }

    /// <summary>Connects two elements, or reports why it could not; in match mode an existing relationship of the same kind is reused.</summary>
    private Relationship? Connect(MGraph g, int line, ElementKind kind, Element a, Element b, Element? owner = null)
    {
        // The same structural or trace link stated twice (or already in the model, when matching by name) is one relationship;
        // flows, transitions and messages may repeat.
        if (kind is not (ElementKind.Message or ElementKind.ControlFlow or ElementKind.ObjectFlow or ElementKind.Transition) && (options.Match == MermaidMatchMode.MatchByName || _links.Contains((kind, a, b)))
            && m.Outgoing(a, kind).FirstOrDefault(r => ReferenceEquals(r.Target, b)) is { } existing) return existing;
        if (Metamodel.CheckEnds(kind, a.Kind, b.Kind) is { } problem)
        {
            g.Warn(line, $"{a.DisplayName} → {b.DisplayName}: {problem}; skipped.");
            return null;
        }
        try
        {
            var r = m.Connect(kind, a, b, owner);
            _links.Add((kind, a, b));
            return r;
        }
        catch (InvalidOperationException ex)
        {
            g.Warn(line, $"{a.DisplayName} → {b.DisplayName}: {ex.Message}; skipped.");
            return null;
        }
    }

    /// <summary>Lays out with the auto-diagrammer (layered along the Mermaid direction) and creates the diagram.</summary>
    private Diagram Layout(MGraph g, DiagramType type, Element owner, string name, IReadOnlyDictionary<string, Element> elements, IReadOnlyList<(MNote Note, Element Comment)>? notes = null)
    {
        var plan = new PlannedDiagram
        {
            Type = type, Name = name, Owner = owner, Elements = elements.Values.Distinct().ToList(), LeftToRight = g.LeftToRight,
            Purpose = $"Imported from {Source} ({g.Keyword}).",
        };
        var d = AutoDiagrammer.Build(m, plan);
        if (g.Reversed) Mirror(d, g.LeftToRight);
        if (notes is { Count: > 0 }) PlaceNotes(d, notes, elements);
        EdgeRouter.Invalidate(d);
        return d;
    }

    /// <summary>BT / RL: flips the top-level shapes (with their contents) along the layout direction.</summary>
    private void Mirror(Diagram d, bool horizontal)
    {
        var tops = d.Nodes.Where(n => n.ParentId == null).ToList();
        if (tops.Count < 2) return;
        var lo = horizontal ? tops.Min(n => n.Bounds.X) : tops.Min(n => n.Bounds.Y);
        var hi = horizontal ? tops.Max(n => n.Bounds.Right) : tops.Max(n => n.Bounds.Bottom);
        var moves = new Dictionary<DiagramNode, Bounds>();
        foreach (var n in tops)
        {
            var b = n.Bounds;
            var delta = horizontal ? lo + hi - b.Right - b.X : lo + hi - b.Bottom - b.Y;
            foreach (var x in d.DescendantsOf(n).Prepend(n))
                moves[x] = horizontal ? x.Bounds.Offset(delta, 0) : x.Bounds.Offset(0, delta);
        }
        m.SetBounds(d, moves, "Layout");
    }

    /// <summary>Notes go to the right of the shape they belong to; floating notes in a row under the diagram.</summary>
    private void PlaceNotes(Diagram d, IReadOnlyList<(MNote Note, Element Comment)> notes, IReadOnlyDictionary<string, Element> byId)
    {
        var bottom = d.Nodes.Count > 0 ? d.Nodes.Max(n => n.Bounds.Bottom) + 40 : 40;
        var right = d.Nodes.Count > 0 ? d.Nodes.Max(n => n.Bounds.Right) + 40 : 40;
        var x = 40.0;
        var stack = new Dictionary<DiagramNode, double>();
        foreach (var (note, comment) in notes)
        {
            var lines = Math.Clamp(note.Text.Split('\n').Sum(l => 1 + l.Length / 28), 1, 8);
            var h = 24 + lines * 16.0;
            if (note.Target is { } t && byId.TryGetValue(t, out var te) && d.NodeFor(te) is { } anchor)
            {
                var y = stack.GetValueOrDefault(anchor, anchor.Bounds.Y);
                m.AddNode(d, comment, new Bounds(Math.Max(anchor.Bounds.Right + 30, right - 200 > anchor.Bounds.Right ? anchor.Bounds.Right + 30 : right), y, 180, h), showRelationships: false);
                stack[anchor] = y + h + 10;
            }
            else
            {
                m.AddNode(d, comment, new Bounds(x, bottom, 180, h), showRelationships: false);
                x += 200;
            }
        }
    }

    private List<(MNote, Element)> Notes(MGraph g, Element owner)
    {
        var list = new List<(MNote, Element)>();
        foreach (var n in g.Notes.Take(500))
        {
            var c = m.Create(owner, ElementKind.Comment);
            m.Set(c, FieldKeys.Body, n.Text);
            list.Add((n, c));
        }
        return list;
    }

    private static string Lower(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    // ================================================================== flowchart → activity

    private Diagram Flowchart(MGraph g, string name)
    {
        var act = Make(target, ElementKind.Activity, name);
        var edges = g.Edges.Where(e => e.Kind != "invisible").ToList();
        var groupIds = g.Groups.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var members = g.Groups.ToDictionary(x => x.Id, x => g.Nodes.Where(n => n.Group == x.Id && n.Shape != "subgraph").ToList(), StringComparer.Ordinal);

        // Links to a subgraph go to its first node; links from a subgraph leave from its last node.
        string? Resolve(string id, bool asTarget)
        {
            if (!groupIds.Contains(id) || g.ById.TryGetValue(id, out var n) && n.Shape != "subgraph") return id;
            var inside = Members(id);
            if (inside.Count == 0) return null;
            g.WarnOnce("subgraphlink", g.Line, "links to or from a subgraph were connected to its first / last node.");
            return (asTarget ? inside[0] : inside[^1]).Id;
        }
        List<MNode> Members(string groupId)
        {
            var list = members.GetValueOrDefault(groupId) ?? new List<MNode>();
            foreach (var child in g.Groups.Where(x => x.Parent == groupId)) list = list.Concat(Members(child.Id)).ToList();
            return list;
        }

        var resolved = new List<(MEdge E, string From, string To)>();
        foreach (var e in edges)
            if (Resolve(e.From, false) is { } f && Resolve(e.To, true) is { } t) resolved.Add((e, f, t));
            else g.Warn(e.Line, $"the link {e.From} → {e.To} touches an empty subgraph and was skipped.");
        var indeg = resolved.GroupBy(x => x.To).ToDictionary(x => x.Key, x => x.Count());
        var outdeg = resolved.GroupBy(x => x.From).ToDictionary(x => x.Key, x => x.Count());

        var made = new Dictionary<string, Element>(StringComparer.Ordinal);
        foreach (var n in g.Nodes.Where(n => n.Shape != "subgraph"))
        {
            var ins = indeg.GetValueOrDefault(n.Id);
            var outs = outdeg.GetValueOrDefault(n.Id);
            var kind = n.Shape switch
            {
                "diamond" => ElementKind.DecisionNode,
                "fork" => ElementKind.ForkNode,
                "db" or "buffer" or "object" => ElementKind.ObjectNode,
                "initial" => ins == 0 ? ElementKind.InitialNode : ElementKind.Action,
                "final" => outs == 0 ? ElementKind.ActivityFinalNode : ElementKind.Action,
                "circle" => ins == 0 && outs > 0 ? ElementKind.InitialNode : outs == 0 && ins > 0 ? ElementKind.ActivityFinalNode : ElementKind.Action,
                "dcircle" => outs == 0 ? ElementKind.ActivityFinalNode : ElementKind.Action,
                _ => ElementKind.Action,
            };
            if (n.Shape is "initial" or "final" && kind == ElementKind.Action)
                g.Warn(n.Line, $"{n.Id} has links on both sides, so it was imported as an action rather than an {n.Shape} node.");
            var label = MermaidSource.Name(n.Label);
            string? nm = kind switch
            {
                ElementKind.InitialNode or ElementKind.ActivityFinalNode or ElementKind.ForkNode =>
                    label.Length == 0 || label == n.Id && !n.Declared || IsDefaultEndLabel(label) ? null : label,
                ElementKind.DecisionNode => label == n.Id && !n.Declared ? null : label,
                _ => label.Length == 0 ? n.Id : label,
            };
            made[n.Id] = m.Create(act, kind, nm);
            if (n.Shape is "db" or "buffer") m.Set(made[n.Id], FieldKeys.Subtype, n.Shape == "db" ? Subtypes.DataStore : Subtypes.CentralBuffer);
        }

        foreach (var (e, from, to) in resolved)
        {
            if (!made.TryGetValue(from, out var a) || !made.TryGetValue(to, out var b)) continue;
            var kind = a.Kind == ElementKind.ObjectNode || b.Kind == ElementKind.ObjectNode ? ElementKind.ObjectFlow : ElementKind.ControlFlow;
            if (Connect(g, e.Line, kind, a, b) is not { } r) continue;
            if (e.Label.Length > 0) m.Set(r, FieldKeys.Guard, MermaidSource.Name(e.Label).Trim('[', ']').Trim());
            if (e.Bidirectional)
            {
                g.WarnOnce("bidi", e.Line, "two-way links (<-->) were imported as two flows.");
                Connect(g, e.Line, kind, b, a);
            }
        }

        LastMade = made;
        Diagram d;
        if (g.Groups.Count == 0) d = Layout(g, DiagramType.Activity, act, name, made);
        else d = Swimlanes(g, act, name, made, resolved.Select(x => (x.From, x.To)).ToList());
        return d;
    }

    private static bool IsDefaultEndLabel(string s) =>
        s.Trim().ToLowerInvariant() is "start" or "begin" or "end" or "stop" or "finish" or "done" or "initial" or "final" or " " or "";

    /// <summary>Subgraphs as swimlanes: one vertical lane per subgraph, rows by flow rank shared across lanes.</summary>
    private Diagram Swimlanes(MGraph g, Element act, string name, Dictionary<string, Element> made, List<(string From, string To)> links)
    {
        if (g.LeftToRight || g.Reversed) g.WarnOnce("lanesdir", g.Line, "flowcharts with subgraphs are laid out top to bottom, one swimlane per subgraph.");
        if (g.Groups.Any(x => x.Parent != null)) g.WarnOnce("nested", g.Line, "nested subgraphs became separate swimlanes.");
        var d = m.CreateDiagram(act, DiagramType.Activity, name);
        m.Set(d, FieldKeys.Documentation, $"Imported from {Source} ({g.Keyword}).");

        // Ranks: longest path over the flow graph with back edges (cycles) ignored.
        var ids = made.Keys.ToList();
        var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, StringComparer.Ordinal);
        var outs = ids.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (f, t) in links) if (outs.ContainsKey(f) && outs.ContainsKey(t) && f != t && !outs[f].Contains(t)) outs[f].Add(t);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var dag = new List<(string, string)>();
        var hasIn = links.Select(l => l.To).ToHashSet(StringComparer.Ordinal);
        foreach (var start in ids.OrderBy(x => hasIn.Contains(x) ? 1 : 0).ThenBy(x => order[x]))
        {
            if (state.ContainsKey(start)) continue;
            var stack = new Stack<(string V, int I)>();
            stack.Push((start, 0));
            state[start] = 1;
            while (stack.Count > 0)
            {
                var (v, i) = stack.Pop();
                if (i < outs[v].Count)
                {
                    stack.Push((v, i + 1));
                    var w = outs[v][i];
                    if (!state.TryGetValue(w, out var s)) { dag.Add((v, w)); state[w] = 1; stack.Push((w, 0)); }
                    else if (s == 2) dag.Add((v, w));
                }
                else state[v] = 2;
            }
        }
        var rank = ids.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);
        for (var iter = 0; iter <= ids.Count; iter++)
        {
            var changed = false;
            foreach (var (u, v) in dag) if (rank[v] < rank[u] + 1) { rank[v] = rank[u] + 1; changed = true; }
            if (!changed) break;
        }

        (double W, double H) Size(Element e)
        {
            var (w, h) = Notation.DefaultSizeOf(e);
            if (e.Kind is ElementKind.Action or ElementKind.ObjectNode) w = Math.Clamp(Math.Ceiling((e.DisplayName.Length * 7.2 + 40) / 10) * 10, w, 300);
            return (w, h);
        }

        // Lanes: nodes outside every subgraph first (no lane shape), then one per subgraph.
        var lanes = new List<(MGroup? Group, List<string> Nodes)>();
        var loose = ids.Where(id => g.ById[id].Group == null).ToList();
        if (loose.Count > 0) lanes.Add((null, loose));
        foreach (var grp in g.Groups) lanes.Add((grp, ids.Where(id => g.ById[id].Group == grp.Id).ToList()));

        const double gap = 40, rowGap = 50, pad = ContainerLayout.Padding + 10, top = 40, header = 44;
        var maxRank = ids.Count == 0 ? 0 : rank.Values.Max();
        var rowH = new double[maxRank + 1];
        foreach (var id in ids) rowH[rank[id]] = Math.Max(rowH[rank[id]], Size(made[id]).H);
        var rowY = new double[maxRank + 1];
        var y = top + header;
        for (var r = 0; r <= maxRank; r++) { rowY[r] = y; y += rowH[r] + rowGap; }
        var laneHeight = y - top;

        var x = 40.0;
        foreach (var (grp, nodes) in lanes)
        {
            var width = 0.0;
            for (var r = 0; r <= maxRank; r++)
            {
                var row = nodes.Where(id => rank[id] == r).ToList();
                if (row.Count > 0) width = Math.Max(width, row.Sum(id => Size(made[id]).W) + gap * (row.Count - 1));
            }
            width = Math.Max(width + 2 * pad, 200);
            DiagramNode? laneNode = null;
            if (grp != null)
            {
                var lane = Make(act, ElementKind.Partition, MermaidSource.Name(grp.Label));
                laneNode = m.AddNode(d, lane, new Bounds(x, top, width, laneHeight), showRelationships: false);
            }
            for (var r = 0; r <= maxRank; r++)
            {
                var row = nodes.Where(id => rank[id] == r).OrderBy(id => order[id]).ToList();
                if (row.Count == 0) continue;
                var rowWidth = row.Sum(id => Size(made[id]).W) + gap * (row.Count - 1);
                var nx = x + (width - rowWidth) / 2;
                foreach (var id in row)
                {
                    var (w, h) = Size(made[id]);
                    m.AddNode(d, made[id], new Bounds(Math.Round(nx), Math.Round(rowY[r] + (rowH[r] - h) / 2), w, h), showRelationships: false, laneNode);
                    nx += w + gap;
                }
            }
            x += width + (grp == null ? 40 : 0);
        }
        foreach (var e in made.Values)
            foreach (var r in m.RelationshipsOf(e).ToList())
                if (r.Kind is ElementKind.ControlFlow or ElementKind.ObjectFlow) m.AddEdge(d, r);
        EdgeRouter.Invalidate(d);
        return d;
    }

    // ================================================================== state diagram → state machine

    private Diagram State(MGraph g, string name)
    {
        var sm = Make(target, ElementKind.StateMachine, name);
        var made = new Dictionary<string, Element>(StringComparer.Ordinal);
        int Depth(MNode n)
        {
            var depth = 0;
            for (var p = n.Parent; p != null && depth <= MermaidLimits.MaxDepth && g.ById.TryGetValue(p, out var pn); p = pn.Parent) depth++;
            return depth;
        }
        foreach (var n in g.Nodes.OrderBy(Depth))
        {
            var owner = n.Parent != null && made.TryGetValue(n.Parent, out var po) && po.Kind == ElementKind.State ? po : sm;
            var pseudo = n.Shape switch
            {
                "choice" => Pseudostates.Choice,
                "fork" => Pseudostates.Fork,
                "join" => Pseudostates.Join,
                "history" => Pseudostates.ShallowHistory,
                "deephistory" => Pseudostates.DeepHistory,
                _ => null,
            };
            var kind = pseudo != null ? ElementKind.Pseudostate : n.Shape switch { "initial" => ElementKind.InitialState, "final" => ElementKind.FinalState, _ => ElementKind.State };
            var e = m.Create(owner, kind, kind is ElementKind.State or ElementKind.Pseudostate ? MermaidSource.Name(n.Label.Length > 0 ? n.Label : n.Id) : null);
            if (pseudo != null) m.Set(e, FieldKeys.Subtype, pseudo);
            if (n.Props.TryGetValue("entry", out var en)) m.Set(e, FieldKeys.Entry, en);
            if (n.Props.TryGetValue("exit", out var ex)) m.Set(e, FieldKeys.Exit, ex);
            if (n.Props.TryGetValue("do", out var da)) m.Set(e, FieldKeys.DoActivity, da);
            if (n.Props.TryGetValue("description", out var desc)) m.Set(e, FieldKeys.Documentation, desc);
            made[n.Id] = e;
        }
        foreach (var e in g.Edges)
        {
            if (!made.TryGetValue(e.From, out var a) || !made.TryGetValue(e.To, out var b)) continue;
            if (Connect(g, e.Line, ElementKind.Transition, a, b) is not { } r) continue;
            var (trigger, guard, effect) = TransitionLabel(e.Label);
            if (trigger.Length > 0) m.Set(r, FieldKeys.Name, trigger);
            if (guard.Length > 0) m.Set(r, FieldKeys.Guard, guard);
            if (effect.Length > 0) m.Set(r, FieldKeys.Effect, effect);
        }
        var notes = Notes(g, sm);
        LastMade = made;
        return Layout(g, DiagramType.StateMachine, sm, name, made, notes);
    }

    /// <summary>"event [guard] / effect".</summary>
    internal static (string Trigger, string Guard, string Effect) TransitionLabel(string label)
    {
        var s = label.Trim();
        var effect = "";
        var slash = s.IndexOf('/');
        if (slash >= 0) { effect = s[(slash + 1)..].Trim(); s = s[..slash].Trim(); }
        var guard = "";
        var lb = s.IndexOf('[');
        var rb = s.LastIndexOf(']');
        if (lb >= 0 && rb > lb) { guard = s[(lb + 1)..rb].Trim(); s = (s[..lb] + s[(rb + 1)..]).Trim(); }
        return (s, guard, effect);
    }

    // ================================================================== class diagram → BDD

    private static readonly Regex Generic = new(@"~([^~]*)~", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static string Generics(string s) => Generic.Replace(s, "<$1>");

    private Diagram Class(MGraph g, string name)
    {
        var packages = new Dictionary<string, Element>(StringComparer.Ordinal);
        Element PackageFor(string? id, int depth = 0)
        {
            if (id == null || depth > MermaidLimits.MaxDepth) return target;
            if (packages.TryGetValue(id, out var p)) return p;
            var grp = g.Groups.FirstOrDefault(x => x.Id == id);
            var owner = PackageFor(grp?.Parent, depth + 1);
            var existing = options.Match == MermaidMatchMode.MatchByName || _made.ContainsKey(id)
                ? owner.OwnedElements.FirstOrDefault(c => c.Kind == ElementKind.Package && c.Name == id) : null;
            return packages[id] = existing ?? Make(owner, ElementKind.Package, id);
        }

        var made = new Dictionary<string, Element>(StringComparer.Ordinal);
        foreach (var n in g.Nodes)
        {
            var ann = n.Props.GetValueOrDefault("annotation") ?? "";
            var kind = ann switch
            {
                "interface" => ElementKind.InterfaceBlock,
                "enumeration" or "enum" => ElementKind.Enumeration,
                "datatype" or "valuetype" or "struct" or "primitive" => ElementKind.ValueType,
                _ => ElementKind.Block,
            };
            if (ann.Length > 0 && ann is not ("interface" or "enumeration" or "enum" or "datatype" or "valuetype" or "struct" or "primitive" or "abstract"))
                g.Warn(n.Line, $"the annotation «{ann}» of {n.Id} has no SysML equivalent and was not imported.");
            var label = MermaidSource.Name(Generics(n.Label.Length > 0 ? n.Label : n.Id));
            var e = Find(label, kind == ElementKind.Block ? [ElementKind.Block, ElementKind.Class] : [kind]) ?? Make(PackageFor(n.Group), kind, label);
            if (ann == "abstract" && !e.IsAbstract) m.Set(e, FieldKeys.IsAbstract, "true");
            made[n.Id] = e;
        }

        string TypeOf(string type)
        {
            var t = MermaidSource.Name(Generics(type)).Trim();
            if (t.Length == 0) return "";
            if (made.TryGetValue(t, out var c)) return c.Id;
            if (made.Values.FirstOrDefault(x => x.Name == t) is { } byName) return byName.Id;
            return Primitive(t);
        }

        foreach (var n in g.Nodes)
        {
            var e = made[n.Id];
            foreach (var raw in n.Members) Member(g, n, e, raw, TypeOf);
        }

        foreach (var x in g.Edges)
        {
            if (!made.TryGetValue(x.From, out var a) || !made.TryGetValue(x.To, out var b)) continue;
            ClassEdge(g, x, a, b);
        }
        var notes = new List<(MNote, Element)>();
        foreach (var note in g.Notes.Take(500))
        {
            var c = m.Create(target, ElementKind.Comment);
            m.Set(c, FieldKeys.Body, note.Text);
            notes.Add((note, c));
        }
        LastMade = made;
        return Layout(g, DiagramType.BlockDefinition, target, name, made, notes);
    }

    private void ClassEdge(MGraph g, MEdge x, Element a, Element b)
    {
        bool InterfaceLike(Element e) => e.Kind is ElementKind.InterfaceBlock or ElementKind.Interface;
        Relationship? r = null;
        switch (x.Kind)
        {
            case "generalization" or "realization":
            {
                var realize = InterfaceLike(b) && !InterfaceLike(a) || x.Kind == "realization" && InterfaceLike(b);
                if (realize) r = Connect(g, x.Line, ElementKind.Realization, a, b);
                else if (a.Kind == b.Kind) r = Connect(g, x.Line, ElementKind.Generalization, a, b);
                else
                {
                    g.Warn(x.Line, $"{a.Name} and {b.Name} are different kinds of element, so the inheritance became a dependency.");
                    r = Connect(g, x.Line, ElementKind.Dependency, a, b);
                }
                break;
            }
            case "composition":
            {
                r = Connect(g, x.Line, ElementKind.Composition, a, b);
                if (r == null) break;
                if (x.ToCard.Length > 0) m.Set(r, FieldKeys.Multiplicity, x.ToCard);
                // The part property of the whole, unless the class already declares one typed by the part.
                if (!a.OwnedElements.Any(p => p.Kind == ElementKind.Property && p.TypeRef == b.Id) && Metamodel.CanOwn(a.Kind, ElementKind.Property))
                {
                    var partName = x.Label.Length > 0 && Regex.IsMatch(x.Label, @"^[\p{L}_][\p{L}\p{N}_]*$", RegexOptions.None, TimeSpan.FromSeconds(1)) ? x.Label : Lower(b.Name.Replace(" ", ""));
                    var part = m.Create(a, ElementKind.Property, partName);
                    m.Set(part, FieldKeys.Type, b.Id);
                    m.Set(part, FieldKeys.Multiplicity, x.ToCard.Length > 0 ? x.ToCard : "1");
                }
                break;
            }
            case "aggregation":
                r = Connect(g, x.Line, ElementKind.Aggregation, a, b);
                if (r != null && x.ToCard.Length > 0) m.Set(r, FieldKeys.Multiplicity, x.ToCard);
                break;
            case "association" or "link":
                r = Connect(g, x.Line, ElementKind.Association, a, b);
                if (r != null && x.ToCard.Length > 0) m.Set(r, FieldKeys.Multiplicity, x.ToCard);
                break;
            default:
                r = Connect(g, x.Line, ElementKind.Dependency, a, b);
                break;
        }
        if (r != null && x.Label.Length > 0 && x.Kind != "composition" && r.Name.Length == 0) m.Set(r, FieldKeys.Name, x.Label);
        if (r != null && x.FromCard.Length > 0 && x.Kind is "association" or "link" or "aggregation" or "composition")
            m.Set(r, FieldKeys.SourceMultiplicity, x.FromCard);
    }

    private static readonly Regex MethodRx = new(@"^(?<name>[^(]*)\((?<params>[^)]*)\)\s*(?<ret>.*)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private void Member(MGraph g, MNode n, Element owner, string raw, Func<string, string> typeOf)
    {
        var s = raw.Trim();
        if (s.Length == 0) return;
        if (owner.Kind == ElementKind.Enumeration)
        {
            var lit = MermaidSource.Name(s.TrimStart('+', '-', '#', '~'));
            if (!owner.OwnedElements.Any(c => c.Kind == ElementKind.EnumerationLiteral && c.Name == lit)) m.Create(owner, ElementKind.EnumerationLiteral, lit);
            return;
        }
        var visibility = s[0] switch { '+' => "public", '-' => "private", '#' => "protected", '~' => "package", _ => null };
        if (visibility != null) s = s[1..].Trim();
        var isStatic = false;
        var isAbstract = false;
        if (s.EndsWith('$')) { isStatic = true; s = s[..^1].Trim(); }
        if (s.EndsWith('*')) { isAbstract = true; s = s[..^1].Trim(); }
        if (s.Length == 0) { g.Warn(n.Line, $"could not read the member “{raw}” of {n.Id}."); return; }

        var mm = MethodRx.Match(s);
        if (mm.Success)
        {
            var opName = MermaidSource.Name(mm.Groups["name"].Value.Trim());
            var ret = mm.Groups["ret"].Value.Trim();
            if (ret.EndsWith('$')) { isStatic = true; ret = ret[..^1].Trim(); }
            if (ret.EndsWith('*')) { isAbstract = true; ret = ret[..^1].Trim(); }
            ret = ret.TrimStart(':').Trim();
            var pars = Generics(mm.Groups["params"].Value.Trim());
            var sig = $"({pars})" + (ret.Length > 0 ? $" : {Generics(ret)}" : "");
            if (opName.Length == 0) { g.Warn(n.Line, $"could not read the member “{raw}” of {n.Id}."); return; }
            if (owner.OwnedElements.Any(c => c.Kind == ElementKind.Operation && c.Name == opName)) return;
            if (!Metamodel.CanOwn(owner.Kind, ElementKind.Operation)) return;
            var op = m.Create(owner, ElementKind.Operation, opName);
            m.Set(op, FieldKeys.Signature, sig);
            if (visibility != null) m.Set(op, FieldKeys.Visibility, visibility);
            if (isAbstract) m.Set(op, FieldKeys.IsAbstract, "true");
            if (isStatic) g.WarnOnce("static", n.Line, "static members ($) are imported as ordinary members.");
            return;
        }

        string name, type = "", def = "";
        var eq = s.IndexOf('=');
        if (eq > 0) { def = s[(eq + 1)..].Trim(); s = s[..eq].Trim(); }
        var colon = s.IndexOf(':');
        if (colon > 0) { name = s[..colon].Trim(); type = s[(colon + 1)..].Trim(); }
        else
        {
            var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            name = parts[^1];
            if (parts.Length > 1) type = string.Join(" ", parts[..^1]);
        }
        name = MermaidSource.Name(name);
        var mult = "";
        var mb = name.IndexOf('[');
        if (mb > 0 && name.EndsWith(']')) { mult = name[(mb + 1)..^1]; name = name[..mb].Trim(); }
        if (type.EndsWith("[]", StringComparison.Ordinal)) { type = type[..^2]; mult = "*"; }
        if (name.Length == 0) { g.Warn(n.Line, $"could not read the member “{raw}” of {n.Id}."); return; }
        if (owner.OwnedElements.Any(c => c.Kind == ElementKind.Property && c.Name == name)) return;
        if (!Metamodel.CanOwn(owner.Kind, ElementKind.Property)) return;
        var p = m.Create(owner, ElementKind.Property, name);
        if (typeOf(type) is { Length: > 0 } tr) m.Set(p, FieldKeys.Type, tr);
        if (mult.Length > 0) m.Set(p, FieldKeys.Multiplicity, mult);
        if (def.Length > 0) m.Set(p, FieldKeys.DefaultValue, def);
        if (visibility != null) m.Set(p, FieldKeys.Visibility, visibility);
        if (isStatic) g.WarnOnce("static", n.Line, "static members ($) are imported as ordinary members.");
    }

    /// <summary>Common programming type names as SysML primitive types; anything else is kept as written.</summary>
    internal static string Primitive(string t) => t.ToLowerInvariant() switch
    {
        "int" or "integer" or "long" or "short" or "byte" or "int32" or "int64" or "uint" or "bigint" or "smallint" or "tinyint" or "serial" => "Integer",
        "float" or "double" or "decimal" or "real" or "number" or "numeric" or "money" => "Real",
        "bool" or "boolean" or "bit" => "Boolean",
        "string" or "str" or "char" or "text" or "varchar" or "nvarchar" or "uuid" or "guid" => "String",
        _ => t,
    };

    // ================================================================== ER diagram → BDD

    private Diagram Er(MGraph g, string name)
    {
        var made = new Dictionary<string, Element>(StringComparer.Ordinal);
        foreach (var n in g.Nodes)
        {
            var label = MermaidSource.Name(n.Label.Length > 0 ? n.Label : n.Id);
            var e = Find(label, ElementKind.Block) ?? Make(target, ElementKind.Block, label);
            made[n.Id] = e;
            foreach (var attr in n.Members)
            {
                var mm = Regex.Match(attr, @"^(?<type>\S+)\s+(?<name>[^\s""]+)(?<keys>(?:\s+(?:PK|FK|UK)\s*,?)*)\s*(?:""(?<comment>[^""]*)"")?\s*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                if (!mm.Success) { g.Warn(n.Line, $"could not read the attribute “{attr}” of {n.Id}."); continue; }
                var an = MermaidSource.Name(mm.Groups["name"].Value);
                if (e.OwnedElements.Any(c => c.Kind == ElementKind.Property && c.Name == an)) continue;
                var p = m.Create(e, ElementKind.Property, an);
                m.Set(p, FieldKeys.Type, Primitive(Generics(mm.Groups["type"].Value)));
                var keys = string.Join(", ", Regex.Matches(mm.Groups["keys"].Value, "PK|FK|UK", RegexOptions.None, TimeSpan.FromSeconds(1)).Select(k => k.Value));
                var doc = string.Join(" — ", new[] { keys, mm.Groups["comment"].Value.Trim() }.Where(x => x.Length > 0));
                if (doc.Length > 0) m.Set(p, FieldKeys.Documentation, doc);
            }
        }
        foreach (var x in g.Edges)
        {
            if (!made.TryGetValue(x.From, out var a) || !made.TryGetValue(x.To, out var b)) continue;
            if (Connect(g, x.Line, ElementKind.Association, a, b) is not { } r) continue;
            if (x.Label.Length > 0) m.Set(r, FieldKeys.Name, x.Label);
            if (x.ToCard.Length > 0) m.Set(r, FieldKeys.Multiplicity, x.ToCard == "0..*" ? "*" : x.ToCard);
            if (x.FromCard.Length > 0) m.Set(r, FieldKeys.SourceMultiplicity, x.FromCard == "0..*" ? "*" : x.FromCard);
            if (x.Kind == "nonIdentifying") m.Set(r, FieldKeys.Documentation, "Non-identifying relationship.");
        }
        return Layout(g, DiagramType.BlockDefinition, target, name, made);
    }

    // ================================================================== requirement diagram

    private Diagram Requirement(MGraph g, string name)
    {
        var made = new Dictionary<string, Element>(StringComparer.Ordinal);
        foreach (var n in g.Nodes)
        {
            var label = MermaidSource.Name(n.Label.Length > 0 ? n.Label : n.Id);
            if (n.Shape == "element")
            {
                var type = n.Props.GetValueOrDefault("type") ?? "";
                var kind = type.Contains("test", StringComparison.OrdinalIgnoreCase) ? ElementKind.TestCase : ElementKind.Block;
                var e = Find(label, kind == ElementKind.TestCase ? [ElementKind.TestCase] : StructuralKinds) ?? Make(target, kind, label);
                var doc = string.Join("\n", new[] { type.Length > 0 ? "type: " + type : "", n.Props.GetValueOrDefault("docref") is { Length: > 0 } dr ? "docref: " + dr : "" }.Where(x => x.Length > 0));
                if (doc.Length > 0 && e.Documentation.Length == 0) m.Set(e, FieldKeys.Documentation, doc);
                made[n.Id] = e;
                continue;
            }
            if (n.Shape.Length == 0) g.Warn(n.Line, $"{n.Id} is used in a relationship but never declared; imported as a block.");
            if (n.Shape.Length == 0)
            {
                made[n.Id] = Find(label, StructuralKinds) ?? Make(target, ElementKind.Block, label);
                continue;
            }
            var id = n.Props.GetValueOrDefault("id") ?? "";
            var req = (id.Length > 0 && options.Match == MermaidMatchMode.MatchByName
                          ? m.OfKind(ElementKind.Requirement).FirstOrDefault(r => r.Get(FieldKeys.RequirementId) == id) : null)
                      ?? Find(label, ElementKind.Requirement)
                      ?? Make(target, ElementKind.Requirement, label);
            if (id.Length > 0) m.Set(req, FieldKeys.RequirementId, id);
            if (n.Props.GetValueOrDefault("text") is { Length: > 0 } text) m.Set(req, FieldKeys.Text, text);
            if (n.Props.GetValueOrDefault("risk") is { Length: > 0 } risk)
            {
                var r = risk.ToLowerInvariant();
                if (ReqFields.Risks.Contains(r)) m.Set(req, ReqFields.Risk, r);
                else g.Warn(n.Line, $"risk “{risk}” of {n.Id} is not low, medium or high; it was not imported.");
            }
            if (n.Props.GetValueOrDefault("verifymethod") is { Length: > 0 } vm)
            {
                var v = vm.ToLowerInvariant();
                if (ReqFields.VerificationMethods.Contains(v)) m.Set(req, ReqFields.VerificationMethod, v);
                else g.Warn(n.Line, $"verify method “{vm}” of {n.Id} is not analysis, inspection, test or demonstration; it was not imported.");
            }
            if (n.Shape != "requirement") m.Set(req, ReqFields.CustomPrefix + "Type", n.Shape);
            foreach (var key in n.Props.Keys.Where(k => k is not ("id" or "text" or "risk" or "verifymethod")))
                g.Warn(n.Line, $"the field “{key}” of {n.Id} is not a requirement field and was not imported.");
            made[n.Id] = req;
        }
        foreach (var x in g.Edges)
        {
            if (!made.TryGetValue(x.From, out var a) || !made.TryGetValue(x.To, out var b)) continue;
            if (x.Kind == "contains")
            {
                if (a.Kind == ElementKind.Requirement && b.Kind == ElementKind.Requirement && !b.IsAncestorOf(a) && !ReferenceEquals(a, b))
                {
                    if (!ReferenceEquals(b.Owner, a)) m.Move(b, a);
                }
                else g.Warn(x.Line, $"{a.Name} contains {b.Name}: only requirements can contain requirements; skipped.");
                continue;
            }
            var kind = x.Kind switch
            {
                "satisfies" => ElementKind.Satisfy,
                "verifies" => ElementKind.Verify,
                "derives" => ElementKind.DeriveReqt,
                "refines" => ElementKind.Refine,
                "copies" => ElementKind.Copy,
                _ => ElementKind.Trace,
            };
            if (Metamodel.CheckEnds(kind, a.Kind, b.Kind) is { } problem)
            {
                g.Warn(x.Line, $"{a.Name} {x.Kind} {b.Name}: {problem}; imported as a trace.");
                kind = ElementKind.Trace;
            }
            Connect(g, x.Line, kind, a, b);
        }
        LastMade = made;
        return Layout(g, DiagramType.Requirement, target, name, made);
    }

    // ================================================================== C4 → BDD

    private Diagram C4(MGraph g, string name)
    {
        var made = new Dictionary<string, Element>(StringComparer.Ordinal);
        foreach (var n in g.Nodes)
        {
            var kind = n.Shape == "person" ? ElementKind.Actor : ElementKind.Block;
            var label = MermaidSource.Name(n.Label.Length > 0 ? n.Label : n.Id);
            if (n.Shape.Length == 0) g.Warn(n.Line, $"{n.Id} is used in a relationship but never declared; imported as a block.");
            var e = Find(label, kind == ElementKind.Actor ? [ElementKind.Actor] : StructuralKinds) ?? Make(target, kind, label);
            var doc = string.Join("\n", new[]
            {
                n.Props.GetValueOrDefault("description") ?? "",
                n.Props.GetValueOrDefault("technology") is { Length: > 0 } t ? "Technology: " + t : "",
                n.Props.GetValueOrDefault("kind") is { Length: > 0 } k && k != "Person" && k != "System" ? "C4: " + k : "",
            }.Where(x => x.Length > 0));
            if (doc.Length > 0 && e.Documentation.Length == 0) m.Set(e, FieldKeys.Documentation, doc);
            made[n.Id] = e;
        }
        foreach (var x in g.Edges)
        {
            if (!made.TryGetValue(x.From, out var a) || !made.TryGetValue(x.To, out var b)) continue;
            if (Connect(g, x.Line, ElementKind.Dependency, a, b) is not { } r) continue;
            if (x.Label.Length > 0) m.Set(r, FieldKeys.Name, x.Label);
            if (x.ToCard.Length > 0) m.Set(r, FieldKeys.Documentation, "Technology: " + x.ToCard);
            if (x.Bidirectional) Connect(g, x.Line, ElementKind.Dependency, b, a);
        }
        return Layout(g, DiagramType.BlockDefinition, target, name, made);
    }

    // ================================================================== sequence diagram → interaction

    private Diagram Sequence(MGraph g, string name)
    {
        var inter = Make(target, ElementKind.Interaction, name);
        var d = m.CreateDiagram(inter, DiagramType.Sequence, name);
        m.Set(d, FieldKeys.Documentation, $"Imported from {Source} ({g.Keyword}).");
        var lifelines = new Dictionary<string, Element>(StringComparer.Ordinal);
        var x = 40.0;
        var lx = new Dictionary<string, (double X, double W)>(StringComparer.Ordinal);
        foreach (var n in g.Nodes)
        {
            var label = MermaidSource.Name(n.Label.Length > 0 ? n.Label : n.Id);
            var ll = m.Create(inter, ElementKind.Lifeline, label);
            var typeKinds = n.Shape == "actor" ? new[] { ElementKind.Actor } : StructuralKinds;
            if (options.Match == MermaidMatchMode.MatchByName && Find(label, typeKinds) is { } type) m.Set(ll, FieldKeys.Type, type.Id);
            lifelines[n.Id] = ll;
            var w = Math.Clamp(Math.Ceiling((label.Length * 7.5 + 30) / 10) * 10, 150, 260);
            lx[n.Id] = (x, w);
            x += w + 70;
        }
        double Center(string id) => lx[id].X + lx[id].W / 2;

        var y = 110.0;
        var messages = new List<(Relationship R, double Y)>();
        var open = new Stack<(Element F, double Top, HashSet<string> Touched, int Depth, List<(Element Operand, double Y)> Separators)>();
        var fragments = new List<(Element F, Bounds B, List<(Element Operand, double Offset)> Separators)>();
        var notes = new List<(Element C, Bounds B)>();
        foreach (var ev in g.Sequence)
        {
            switch (ev)
            {
                case MSeqMessage msg:
                {
                    if (!lifelines.TryGetValue(msg.From, out var a) || !lifelines.TryGetValue(msg.To, out var b)) break;
                    if (Connect(g, msg.Line, ElementKind.Message, a, b, inter) is not { } r) break;
                    if (msg.Text.Length > 0) m.Set(r, FieldKeys.Name, MermaidSource.Name(msg.Text));
                    m.Set(r, FieldKeys.MessageSort, msg.Sort);
                    messages.Add((r, y));
                    foreach (var f in open) { f.Touched.Add(msg.From); f.Touched.Add(msg.To); }
                    y += msg.From == msg.To ? 64 : 48;
                    break;
                }
                case MSeqFragmentStart fs:
                {
                    var f = m.Create(inter, ElementKind.CombinedFragment);
                    m.Set(f, FieldKeys.Operator, Metamodel.FragmentOperators.Contains(fs.Operator) ? fs.Operator : "alt");
                    if (fs.Guard.Length > 0) m.Set(f, FieldKeys.Guard, fs.Guard);
                    open.Push((f, y - 20, new HashSet<string>(StringComparer.Ordinal), open.Count, new List<(Element, double)>()));
                    y += 34;
                    break;
                }
                case MSeqFragmentElse fe:
                {
                    if (open.Count == 0) break;
                    var (f, _, _, _, separators) = open.Peek();
                    // The first separator turns what came before into the first operand (the fragment's guard moves there).
                    if (separators.Count == 0)
                    {
                        var first = m.Create(f, ElementKind.InteractionOperand);
                        m.Set(first, FieldKeys.Guard, f.Get(FieldKeys.Guard));
                        m.Set(f, FieldKeys.Guard, null);
                    }
                    var operand = m.Create(f, ElementKind.InteractionOperand);
                    m.Set(operand, FieldKeys.Guard, fe.Guard.Length > 0 ? fe.Guard : InteractionOperands.DefaultGuard(f));
                    separators.Add((operand, y - 12));
                    y += 24;
                    break;
                }
                case MSeqFragmentEnd:
                {
                    if (open.Count == 0) break;
                    var (f, topY, touched, depth, separators) = open.Pop();
                    foreach (var outer in open) outer.Touched.UnionWith(touched);
                    var ids = touched.Count > 0 ? touched.ToList() : lx.Keys.Take(1).ToList();
                    var margin = Math.Max(30, 95 - 12 * depth);
                    var left = ids.Count > 0 ? ids.Min(Center) - margin : 40;
                    var right = ids.Count > 0 ? ids.Max(Center) + margin : 300;
                    fragments.Add((f, new Bounds(left, topY, Math.Max(120, right - left), Math.Max(50, y - topY + 4)), separators.Select(s => (s.Operand, s.Y - topY)).ToList()));
                    y += 26;
                    break;
                }
                case MSeqNote note:
                {
                    var c = m.Create(inter, ElementKind.Comment);
                    m.Set(c, FieldKeys.Body, note.Text);
                    var ids = note.Targets.Where(lx.ContainsKey).ToList();
                    var cx = ids.Count > 0 ? ids.Average(Center) : 120;
                    var w = ids.Count > 1 ? Math.Max(160, Math.Abs(Center(ids[0]) - Center(ids[^1])) + 60) : 160;
                    notes.Add((c, new Bounds(cx - w / 2, y - 14, w, 40)));
                    y += 52;
                    break;
                }
            }
        }
        var height = Math.Max(380, y + 40 - 20);
        foreach (var (id, ll) in lifelines) m.AddNode(d, ll, new Bounds(lx[id].X, 20, lx[id].W, height), showRelationships: false);
        foreach (var (f, b, separators) in fragments)
        {
            var node = m.AddNode(d, f, b, showRelationships: false);
            foreach (var (operand, offset) in separators) m.SetNodeStyle(d, node, InteractionOperands.StyleKey(operand), InteractionOperands.Format(offset));
        }
        foreach (var (c, b) in notes) m.AddNode(d, c, b, showRelationships: false);
        foreach (var (r, my) in messages)
            if (m.AddEdge(d, r) is { } edge) m.SetWaypoints(d, edge, [new Pt(0, my)]);
        return d;
    }
}
