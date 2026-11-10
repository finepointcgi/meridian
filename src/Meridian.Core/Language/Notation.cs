using Meridian.Core.Model;

namespace Meridian.Core.Language;

public sealed record Compartment(string? Title, IReadOnlyList<string> Lines);

/// <summary>Renderer-independent description of what a shape displays.</summary>
public sealed record NodeContent(string? Keyword, string Name, bool Italic, IReadOnlyList<Compartment> Compartments);

/// <summary>
/// Notation logic shared by every renderer (Avalonia canvas, SVG export, HTML report) so that
/// what is on screen and what is exported are always the same.
/// </summary>
public static class Notation
{
    // ------------------------------------------------------------------ element-aware shape / style / size

    public static NodeShape ShapeOf(Element e) =>
        e.Kind == ElementKind.BpmnNode ? Subtypes.BpmnShape(e.Get(FieldKeys.Subtype))
        : e.Kind == ElementKind.Shape ? Subtypes.ShapeOutline(e.Get(FieldKeys.Subtype))
        : e.Kind == ElementKind.Pseudostate ? Pseudostates.Shape(Pseudostates.KindOf(e))
        : IsConstraintParameter(e) ? NodeShape.SmallSquare
        : Metamodel.Shape(e.Kind);

    /// <summary>A parameter of a constraint block (drawn as a small square on a constraint property's border).</summary>
    public static bool IsConstraintParameter(Element e) => e.Kind == ElementKind.Property && e.Owner?.Kind == ElementKind.ConstraintBlock;

    public static bool IsBorderShape(Element e) => IsBorderShape(e.Kind) || IsConstraintParameter(e)
        || Pseudostates.IsConnectionPoint(e) && e.Owner?.Kind == ElementKind.State;

    public static EdgeStyle StyleOf(Relationship r) => r.Kind switch
    {
        ElementKind.BpmnFlow => Subtypes.BpmnFlowStyle(r.Get(FieldKeys.Subtype)),
        ElementKind.ArchiRelation => Subtypes.ArchiStyle(r.Get(FieldKeys.Subtype)),
        ElementKind.Line => Subtypes.LineStyle(r.Get(FieldKeys.Subtype)),
        ElementKind.FeatureDecomposition => VariabilityNotation.DecompositionStyle(r),
        ElementKind.ForeignKey => DataModeling.DbNotation.ForeignKeyStyle(r),
        _ => Metamodel.Style(r.Kind),
    };

    public static (double W, double H) DefaultSizeOf(Element e) => DefaultSizeOf(e.Kind, e.Get(FieldKeys.Subtype));

    public static (double W, double H) DefaultSizeOf(ElementKind kind, string? subtype) =>
        kind == ElementKind.BpmnNode && !string.IsNullOrEmpty(subtype) ? Subtypes.BpmnSize(subtype)
        : kind == ElementKind.Shape ? Subtypes.ShapeSize(subtype ?? "")
        : kind == ElementKind.Pseudostate ? Pseudostates.Size(string.IsNullOrEmpty(subtype) ? Pseudostates.Choice : subtype)
        : kind == ElementKind.ObjectNode && subtype == Subtypes.DataStore ? (160, 70)
        : Metamodel.DefaultSize(kind);

    /// <summary>Snaps a small shape (port, pin) so its centre lies on the nearest border of its container.</summary>
    public static Bounds SnapToBorder(Bounds child, Bounds parent)
    {
        var c = child.Center;
        var dl = Math.Abs(c.X - parent.X); var dr = Math.Abs(c.X - parent.Right);
        var dt = Math.Abs(c.Y - parent.Y); var db = Math.Abs(c.Y - parent.Bottom);
        var m = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
        double cx = Math.Clamp(c.X, parent.X, parent.Right), cy = Math.Clamp(c.Y, parent.Y, parent.Bottom);
        if (m == dl) cx = parent.X; else if (m == dr) cx = parent.Right; else if (m == dt) cy = parent.Y; else cy = parent.Bottom;
        return child with { X = cx - child.Width / 2, Y = cy - child.Height / 2 };
    }

    /// <summary>Shapes that sit on the border of their container rather than inside it.</summary>
    public static bool IsBorderShape(ElementKind k) => k is ElementKind.Port or ElementKind.InputPin or ElementKind.OutputPin
;

    public static NodeContent Content(ModelRepository model, Element e, DiagramType diagramType)
    {
        var keywords = new List<string>();
        var kw = Metamodel.Keyword(e.Kind);
        if (e.Kind == ElementKind.ObjectNode && e.Get(FieldKeys.Subtype) is Subtypes.DataStore or Subtypes.CentralBuffer) kw = e.Get(FieldKeys.Subtype);
        if (e.Kind == ElementKind.Table) kw = DataModeling.DbModel.TableKeyword(e);
        if (kw != null) keywords.Add(kw);
        keywords.AddRange(e.Stereotypes);
        var keyword = keywords.Count > 0 ? string.Join(", ", keywords.Select(k => $"«{k}»")) : null;

        var comps = new List<Compartment>();
        var name = e.DisplayName;
        if (Metamodel.IsTypedFeature(e.Kind))
        {
            var t = model.TypeDisplayName(e);
            var pk = e.Kind == ElementKind.Port && e.Get(FieldKeys.PortKind) is { Length: > 0 } pkk ? $"«{pkk}» " : "";
            name = pk + (string.IsNullOrEmpty(t) ? e.Name : $"{e.Name} : {t}");
            if (model.ResolveType(e) is { Kind: ElementKind.ConstraintBlock } cb)
            {
                keyword = "«constraint»";
                var eqs = cb.Get(FieldKeys.Body).Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => "{" + x.Trim('{', '}', ' ') + "}").ToList();
                if (eqs.Count > 0) comps.Add(new Compartment(null, eqs));
            }
            if (!string.IsNullOrEmpty(e.Multiplicity) && e.Multiplicity != "1") name += $" [{e.Multiplicity}]";
        }

        if (e.Kind == ElementKind.Requirement)
        {
            comps.Add(new Compartment(null, [$"Id = \"{e.Get(FieldKeys.RequirementId)}\"", $"Text = \"{e.Get(FieldKeys.Text)}\""]));
        }
        else if (e.Kind == ElementKind.Table)
        {
            comps.Add(new Compartment(null, DataModeling.DbNotation.Lines(model, e)));
        }
        else if (e.Kind == ElementKind.Stereotype)
        {
            var meta = e.Get(FieldKeys.Metaclass);
            if (meta.Length > 0) comps.Add(new Compartment("extends", meta.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => $"«metaclass» {x}").ToList()));
            var tags = e.OwnedElements.Where(c => c.Kind == ElementKind.TagDefinition)
                .Select(t => $"{t.Name} : {(t.Get(FieldKeys.TagType) is { Length: > 0 } tt ? tt : "String")}{(t.Get(FieldKeys.DefaultValue) is { Length: > 0 } dv ? $" = {dv}" : "")}").ToList();
            if (tags.Count > 0) comps.Add(new Compartment("tags", tags));
        }
        else if (e.Kind == ElementKind.ArchiElement)
        {
            keyword = null;
        }
        else if (e.Kind == ElementKind.Partition)
        {
            keyword = null;
            if (model.Find(e.Get(FieldKeys.Represents)) is { } rep) name = $"{e.DisplayName} : {rep.DisplayName}";
        }
        else if (e.Kind == ElementKind.State)
        {
            var lines = new List<string>();
            if (e.Get(FieldKeys.Entry) is { Length: > 0 } en) lines.Add($"entry / {en}");
            if (e.Get(FieldKeys.DoActivity) is { Length: > 0 } da) lines.Add($"do / {da}");
            if (e.Get(FieldKeys.Exit) is { Length: > 0 } ex) lines.Add($"exit / {ex}");
            if (lines.Count > 0) comps.Add(new Compartment(null, lines));
        }
        else if (e.Kind == ElementKind.Action && model.Find(e.Get(FieldKeys.Behavior)) is { } called)
        {
            name = $"{e.DisplayName} : {called.DisplayName}";
        }
        else if (e.Kind == ElementKind.CombinedFragment)
        {
            name = string.IsNullOrEmpty(e.Get(FieldKeys.Operator)) ? "alt" : e.Get(FieldKeys.Operator);
            keyword = null;
        }
        else if (e.Kind == ElementKind.InteractionUse)
        {
            name = InteractionNotation.RefName(model, e);
        }
        else if (e.Kind is ElementKind.DurationConstraint or ElementKind.TimeConstraint)
        {
            name = e.Get(InteractionNotation.Constraint) is { Length: > 0 } c ? c : e.Kind == ElementKind.TimeConstraint ? "{t..t+3}" : "{d..3*d}";
        }
        else if (e.Kind == ElementKind.Comment)
        {
            name = e.Get(FieldKeys.Body);
            keyword = null;
        }
        else if (e.Kind == ElementKind.Action && !string.IsNullOrEmpty(e.Get(FieldKeys.Body)))
        {
            comps.Add(new Compartment(null, [e.Get(FieldKeys.Body)]));
        }
        else if (e.Kind == ElementKind.ConstraintBlock)
        {
            if (!string.IsNullOrEmpty(e.Get(FieldKeys.Body))) comps.Add(new Compartment("constraints", [$"{{{e.Get(FieldKeys.Body)}}}"]));
            AddFeatureCompartments(model, e, comps, diagramType);
        }
        else if (Metamodel.IsStructuralClassifier(e.Kind) && e.Kind != ElementKind.Actor)
        {
            AddFeatureCompartments(model, e, comps, diagramType);
        }

        if (ShapeOf(e) == NodeShape.Rectangle) AddTraceCompartments(model, e, comps);
        VariabilityNotation.Decorate(model, e, ref keyword, comps);
        return new NodeContent(keyword, name, e.IsAbstract, comps);
    }

    /// <summary>
    /// SysML "callout" compartments that make traceability visible on the shape itself
    /// (requirement: satisfiedBy / verifiedBy / derivedFrom …; design element: satisfies / verifies / allocatedTo …).
    /// </summary>
    public static IReadOnlyList<(string Title, ElementKind Kind, bool Outgoing)> TraceSlots(ElementKind k) => k == ElementKind.Requirement
        ? [("satisfiedBy", ElementKind.Satisfy, false), ("verifiedBy", ElementKind.Verify, false), ("refinedBy", ElementKind.Refine, false),
           ("derivedFrom", ElementKind.DeriveReqt, true), ("derived", ElementKind.DeriveReqt, false), ("tracedTo", ElementKind.Trace, true)]
        : [("satisfies", ElementKind.Satisfy, true), ("verifies", ElementKind.Verify, true), ("refines", ElementKind.Refine, true),
           ("allocatedTo", ElementKind.Allocate, true), ("allocatedFrom", ElementKind.Allocate, false), ("tracedTo", ElementKind.Trace, true)];

    private static void AddTraceCompartments(ModelRepository model, Element e, List<Compartment> comps)
    {
        foreach (var (title, kind, outgoing) in TraceSlots(e.Kind))
        {
            var others = (outgoing ? model.Outgoing(e, kind).Select(r => r.Target) : model.Incoming(e, kind).Select(r => r.Source)).ToList();
            if (others.Count == 0) continue;
            comps.Add(new Compartment(title, others.Select(o => o.Kind == ElementKind.Requirement
                ? $"«requirement» {o.Get(FieldKeys.RequirementId)} {o.Name}".Replace("  ", " ")
                : Metamodel.Keyword(o.Kind) is { } kw ? $"«{kw}» {o.Name}" : o.Name).ToList()));
        }
    }

    private static void AddFeatureCompartments(ModelRepository model, Element e, List<Compartment> comps, DiagramType dt)
    {
        var sysml = dt is DiagramType.BlockDefinition or DiagramType.InternalBlock || e.Kind is ElementKind.Block or ElementKind.InterfaceBlock or ElementKind.ConstraintBlock;
        var props = e.OwnedElements.Where(c => c.Kind == ElementKind.Property).ToList();
        if (sysml)
        {
            var parts = props.Where(p => IsBlockTyped(model, p)).Select(p => FeatureLine(model, p)).ToList();
            var values = props.Where(p => !IsBlockTyped(model, p)).Select(p => FeatureLine(model, p)).ToList();
            if (parts.Count > 0) comps.Add(new Compartment("parts", parts));
            if (values.Count > 0) comps.Add(new Compartment("values", values));
        }
        else if (props.Count > 0 || e.Kind is ElementKind.Class)
        {
            comps.Add(new Compartment(null, props.Select(p => FeatureLine(model, p)).ToList()));
        }

        var ports = e.OwnedElements.Where(c => c.Kind == ElementKind.Port).Select(p => FeatureLine(model, p)).ToList();
        if (ports.Count > 0) comps.Add(new Compartment("ports", ports));

        var ops = e.OwnedElements.Where(c => c.Kind == ElementKind.Operation).Select(o => $"{Vis(o)}{o.Name}{(string.IsNullOrEmpty(o.Get(FieldKeys.Signature)) ? "()" : o.Get(FieldKeys.Signature))}").ToList();
        if (ops.Count > 0 || (!sysml && e.Kind is ElementKind.Class)) comps.Add(new Compartment(sysml ? "operations" : null, ops));

        var lits = e.OwnedElements.Where(c => c.Kind == ElementKind.EnumerationLiteral).Select(l => l.Name).ToList();
        if (lits.Count > 0) comps.Add(new Compartment(null, lits));
    }

    private static bool IsBlockTyped(ModelRepository model, Element p) =>
        model.ResolveType(p)?.Kind is ElementKind.Block or ElementKind.Class or ElementKind.Component or ElementKind.ConstraintBlock;

    /// <summary>What a shape displays: <see cref="Content(ModelRepository, Element, DiagramType)"/>, with a table's columns limited to what the shape shows.</summary>
    public static NodeContent Content(ModelRepository model, DiagramNode n, DiagramType diagramType)
    {
        var c = Content(model, n.Element, diagramType);
        if (n.Element.Kind != ElementKind.Table) return c;
        var mode = DataModeling.DbModel.ShowColumnsOf(n);
        if (mode == "all") return c;
        var lines = DataModeling.DbNotation.Lines(model, n.Element, mode);
        return c with { Compartments = mode == "none" ? [] : [new Compartment(null, lines)] };
    }

    public static string ColumnLine(Element c)
    {
        var flags = new List<string>();
        if (c.Get(FieldKeys.PrimaryKey) == "true") flags.Add("PK");
        if (c.Get(FieldKeys.Unique) == "true") flags.Add("UQ");
        if (c.Get(FieldKeys.Nullable) != "true" && c.Get(FieldKeys.PrimaryKey) != "true") flags.Add("NN");
        var type = c.Get(FieldKeys.SqlType) is { Length: > 0 } t ? t : "text";
        return $"{(flags.Contains("PK") ? "🔑 " : "   ")}{c.Name} : {type}{(flags.Count > 0 ? "  [" + string.Join(",", flags.Where(f => f != "PK")) + "]" : "")}".Replace("  []", "");
    }

    public static string FeatureLine(ModelRepository model, Element p)
    {
        var dir = p.Kind == ElementKind.Port && !string.IsNullOrEmpty(p.Get(FieldKeys.Direction)) ? p.Get(FieldKeys.Direction) + " " : "";
        var t = model.TypeDisplayName(p);
        var line = $"{Vis(p)}{dir}{p.Name}";
        if (!string.IsNullOrEmpty(t)) line += $" : {t}";
        if (!string.IsNullOrEmpty(p.Multiplicity) && p.Multiplicity != "1") line += $" [{p.Multiplicity}]";
        if (!string.IsNullOrEmpty(p.Get(FieldKeys.DefaultValue))) line += $" = {p.Get(FieldKeys.DefaultValue)}";
        return line;
    }

    private static string Vis(Element e) => e.Get(FieldKeys.Visibility) switch
    {
        "public" => "+", "private" => "-", "protected" => "#", "package" => "~", _ => "",
    };

    public static string? EdgeLabel(Relationship r)
    {
        var style = StyleOf(r);
        var parts = new List<string>();
        if (style.Keyword != null) parts.Add($"«{style.Keyword}»");
        foreach (var st in r.Stereotypes) parts.Add($"«{st}»");
        if (!string.IsNullOrEmpty(r.Name) && r.Kind != ElementKind.ForeignKey) parts.Add(r.Name);   // key names stay off ER diagrams
        if (r.Kind == ElementKind.Message && r.Get(FieldKeys.Signature) is { Length: > 0 } sig) parts.Add(sig);
        if (r.Kind is ElementKind.Transition or ElementKind.ControlFlow or ElementKind.ObjectFlow)
        {
            var g = r.Get(FieldKeys.Guard);
            var fx = r.Get(FieldKeys.Effect);
            var s = (string.IsNullOrEmpty(g) ? "" : $"[{g}]") + (string.IsNullOrEmpty(fx) ? "" : $" / {fx}");
            if (s.Length > 0) parts.Add(s.Trim());
        }
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>
    /// Point where the line from <paramref name="outside"/> to the shape's centre crosses its outline.
    /// </summary>
    public static Pt Clip(Bounds b, NodeShape shape, Pt outside)
    {
        var c = b.Center;
        var dx = outside.X - c.X;
        var dy = outside.Y - c.Y;
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return c;

        if (shape is NodeShape.Ellipse or NodeShape.FilledCircle or NodeShape.BullsEye or NodeShape.Pseudostate)
        {
            var a = b.Width / 2;
            var bb = b.Height / 2;
            var t = 1 / Math.Sqrt(dx * dx / (a * a) + dy * dy / (bb * bb));
            return new Pt(c.X + dx * t, c.Y + dy * t);
        }
        if (shape == NodeShape.Diamond)
        {
            var t = 1 / (Math.Abs(dx) / (b.Width / 2) + Math.Abs(dy) / (b.Height / 2));
            return new Pt(c.X + dx * t, c.Y + dy * t);
        }
        var tx = Math.Abs(dx) > 1e-9 ? (b.Width / 2) / Math.Abs(dx) : double.MaxValue;
        var ty = Math.Abs(dy) > 1e-9 ? (b.Height / 2) / Math.Abs(dy) : double.MaxValue;
        var tt = Math.Min(tx, ty);
        return new Pt(c.X + dx * tt, c.Y + dy * tt);
    }

    /// <summary>Full polyline for an edge, clipped to both shapes' outlines.</summary>
    public static IReadOnlyList<Pt> Route(DiagramEdge e, Func<DiagramNode, Bounds>? boundsOf = null, IReadOnlyList<Pt>? waypoints = null)
    {
        boundsOf ??= n => n.Bounds;
        var sb = boundsOf(e.Source);
        var tb = boundsOf(e.Target);
        var wps = waypoints ?? e.Waypoints;
        var pts = new List<Pt>();
        if (e.Relationship.Kind == ElementKind.Message) return MessageRoute(sb, tb, wps);
        if (ReferenceEquals(e.Source, e.Target) && wps.Count == 0)
        {
            // self loop over the top-right corner
            var r = sb.Right; var t = sb.Y;
            var loop = new[] { new Pt(r - 20, t), new Pt(r - 20, t - 30), new Pt(r + 30, t - 30), new Pt(r + 30, t + 20), new Pt(r, t + 20) };
            return loop;
        }
        if (wps.Count == 0 && UsesOrthogonal(e)) return Orthogonal(sb, tb);
        var first = wps.Count > 0 ? wps[0] : tb.Center;
        var last = wps.Count > 0 ? wps[^1] : sb.Center;
        pts.Add(Clip(sb, ShapeOf(e.Source.Element), first));
        pts.AddRange(wps);
        pts.Add(Clip(tb, ShapeOf(e.Target.Element), last));
        return pts;
    }

    public const double LifelineHeader = 40;
    public const double ActivationWidth = 12;

    public static double MessageY(DiagramEdge e, IReadOnlyList<Pt>? waypoints = null)
    {
        var wps = waypoints ?? e.Waypoints;
        return wps.Count > 0 ? wps[0].Y : e.Source.Bounds.Y + LifelineHeader + 40;
    }

    /// <summary>
    /// Execution specifications (activation bars) derived from the messages: a lifeline is active from a
    /// synchronous call it receives until its reply back to the caller (or a short default if no reply is modeled).
    /// </summary>
    public static IReadOnlyList<(DiagramNode Lifeline, double From, double To)> Activations(Diagram d, Func<DiagramEdge, IReadOnlyList<Pt>?>? waypoints = null)
    {
        var result = new List<(DiagramNode, double, double)>();
        var messages = d.Edges.Where(e => e.Relationship.Kind == ElementKind.Message)
            .Select(e => (edge: e, y: MessageY(e, waypoints?.Invoke(e)), sort: e.Relationship.Get(FieldKeys.MessageSort)))
            .OrderBy(m => m.y).ToList();
        foreach (var call in messages.Where(m => m.sort is "" or "synchCall" && !ReferenceEquals(m.edge.Source, m.edge.Target)))
        {
            var callee = call.edge.Target;
            var reply = messages.FirstOrDefault(m => m.y > call.y && m.sort == "reply" && ReferenceEquals(m.edge.Source, callee) && ReferenceEquals(m.edge.Target, call.edge.Source));
            var end = reply.edge != null ? reply.y : call.y + 30;
            // Extend to cover self messages made while active.
            foreach (var self in messages.Where(m => ReferenceEquals(m.edge.Source, callee) && ReferenceEquals(m.edge.Target, callee) && m.y >= call.y && m.y <= end + 1))
                end = Math.Max(end, self.y + 30);
            result.Add((callee, call.y, end));
        }
        return result;
    }

    /// <summary>
    /// Messages are horizontal arrows between lifeline centres; their vertical position (time) is the first waypoint's Y.
    /// A self message is a small loop on the right of the lifeline.
    /// </summary>
    private static IReadOnlyList<Pt> MessageRoute(Bounds s, Bounds t, IReadOnlyList<Pt> wps)
    {
        var y = wps.Count > 0 ? wps[0].Y : s.Y + LifelineHeader + 40;
        var sx = s.Center.X; var tx = t.Center.X;
        if (Math.Abs(sx - tx) < 1) return [new Pt(sx, y), new Pt(sx + 44, y), new Pt(sx + 44, y + 22), new Pt(sx + 6, y + 22)];
        // Leave a little gap at the activation so the arrow head sits on the line.
        return [new Pt(sx, y), new Pt(tx + (tx > sx ? -1 : 1), y)];
    }

    /// <summary>
    /// Routes an edge in the context of its diagram: orthogonal paths avoid the other shapes on the diagram
    /// (containers holding either end, and the ends themselves, are not obstacles).
    /// </summary>
    public static IReadOnlyList<Pt> RouteIn(Diagram d, DiagramEdge e, Func<DiagramNode, Bounds>? boundsOf = null, IReadOnlyList<Pt>? waypoints = null)
    {
        boundsOf ??= n => n.Bounds;
        // Database diagrams: foreign keys run from column row to column row.
        if (DataModeling.DbNotation.ColumnRoute(d, e, boundsOf, waypoints) is { } columns) return columns;
        // Diagrams with a routing style (new diagrams, or chosen in their properties) use the edge router.
        if (EdgeRouter.TryRoute(d, e, boundsOf, waypoints) is { } routed) return routed;
        var wps = waypoints ?? e.Waypoints;
        if (wps.Count > 0 || e.Relationship.Kind == ElementKind.Message || ReferenceEquals(e.Source, e.Target) || !UsesOrthogonal(e))
            return Route(e, boundsOf, waypoints);
        var exclude = new HashSet<DiagramNode>(ReferenceEqualityComparer.Instance) { e.Source, e.Target };
        for (var p = d.ParentOf(e.Source); p != null && exclude.Add(p); p = d.ParentOf(p)) { }
        for (var p = d.ParentOf(e.Target); p != null && exclude.Add(p); p = d.ParentOf(p)) { }
        var obstacles = d.Nodes.Where(n => !exclude.Contains(n) && !IsBorderShape(n.Element) && ShapeOf(n.Element) is not (NodeShape.Swimlane or NodeShape.Pool or NodeShape.Fragment or NodeShape.Lifeline))
                               .Select(boundsOf).ToList();
        return obstacles.Count == 0 ? Orthogonal(boundsOf(e.Source), boundsOf(e.Target)) : OrthogonalAvoiding(boundsOf(e.Source), boundsOf(e.Target), obstacles);
    }

    /// <summary>Candidate orthogonal routes scored by obstacle crossings, then length and bends.</summary>
    public static IReadOnlyList<Pt> OrthogonalAvoiding(Bounds s, Bounds t, IReadOnlyList<Bounds> obstacles) => OrthogonalAvoiding(s, t, obstacles, false);

    private static IReadOnlyList<Pt> OrthogonalAvoiding(Bounds s, Bounds t, IReadOnlyList<Bounds> obstacles, bool referenceScoring)
    {
        var candidates = new List<IReadOnlyList<Pt>> { Orthogonal(s, t) };
        var sc = s.Center; var tc = t.Center;
        const double gap = 14;

        // Z-routes through horizontal / vertical channels next to obstacles.
        var down = tc.Y > sc.Y;
        double y0 = down ? s.Bottom : s.Y, y1 = down ? t.Y : t.Bottom;
        var ys = new List<double> { (y0 + y1) / 2 };
        foreach (var o in obstacles) { ys.Add(o.Y - gap); ys.Add(o.Bottom + gap); }
        foreach (var y in ys.Where(y => down ? y > y0 + 4 && y < y1 - 4 : y < y0 - 4 && y > y1 + 4))
            candidates.Add([new Pt(sc.X, y0), new Pt(sc.X, y), new Pt(tc.X, y), new Pt(tc.X, y1)]);
        var right = tc.X > sc.X;
        double x0 = right ? s.Right : s.X, x1 = right ? t.X : t.Right;
        var xs = new List<double> { (x0 + x1) / 2 };
        foreach (var o in obstacles) { xs.Add(o.X - gap); xs.Add(o.Right + gap); }
        foreach (var x in xs.Where(x => right ? x > x0 + 4 && x < x1 - 4 : x < x0 - 4 && x > x1 + 4))
            candidates.Add([new Pt(x0, sc.Y), new Pt(x, sc.Y), new Pt(x, tc.Y), new Pt(x1, tc.Y)]);

        // L-routes: leave vertically and enter horizontally, or the reverse.
        if (Math.Abs(tc.Y - sc.Y) > s.Height / 2 && (tc.X < t.X || tc.X > t.Right || true))
        {
            var exitY = tc.Y > sc.Y ? s.Bottom : s.Y;
            var entryX = sc.X < t.X ? t.X : sc.X > t.Right ? t.Right : double.NaN;
            if (!double.IsNaN(entryX)) candidates.Add([new Pt(sc.X, exitY), new Pt(sc.X, tc.Y), new Pt(entryX, tc.Y)]);
            var exitX = tc.X > sc.X ? s.Right : s.X;
            var entryY = sc.Y < t.Y ? t.Y : sc.Y > t.Bottom ? t.Bottom : double.NaN;
            if (!double.IsNaN(entryY) && (tc.X > s.Right || tc.X < s.X)) candidates.Add([new Pt(exitX, sc.Y), new Pt(tc.X, sc.Y), new Pt(tc.X, entryY)]);
        }

        // U-routes around everything between the two shapes (above / below / left / right).
        var span = obstacles.Append(s).Append(t).ToList();
        var top = span.Min(b => b.Y) - 2 * gap; var bottom = span.Max(b => b.Bottom) + 2 * gap;
        var left = span.Min(b => b.X) - 2 * gap; var rightMost = span.Max(b => b.Right) + 2 * gap;
        candidates.Add([new Pt(sc.X, s.Y), new Pt(sc.X, top), new Pt(tc.X, top), new Pt(tc.X, t.Y)]);
        candidates.Add([new Pt(sc.X, s.Bottom), new Pt(sc.X, bottom), new Pt(tc.X, bottom), new Pt(tc.X, t.Bottom)]);
        candidates.Add([new Pt(s.X, sc.Y), new Pt(left, sc.Y), new Pt(left, tc.Y), new Pt(t.X, tc.Y)]);
        candidates.Add([new Pt(s.Right, sc.Y), new Pt(rightMost, sc.Y), new Pt(rightMost, tc.Y), new Pt(t.Right, tc.Y)]);
        return referenceScoring ? candidates.MinBy(c => FullScore(c, obstacles))! : Best(candidates, obstacles);
    }

    /// <summary>The original exhaustive scoring (tests compare <see cref="OrthogonalAvoiding"/> against it).</summary>
    internal static IReadOnlyList<Pt> OrthogonalAvoidingReference(Bounds s, Bounds t, IReadOnlyList<Bounds> obstacles) =>
        OrthogonalAvoiding(s, t, obstacles, referenceScoring: true);

    private static IReadOnlyList<Pt> Best(List<IReadOnlyList<Pt>> candidates, IReadOnlyList<Bounds> obstacles)
    {
        // Same choice as candidates.MinBy(Score): the first candidate with the lowest score. Obstacle hits only add to a score,
        // so a candidate whose hit-free score can't beat the best so far is skipped, and counting stops once it can't win
        // (a 500-shape diagram has ~1,000 candidates per path and 500 obstacles: a full count is 1.5M tests per path).
        IReadOnlyList<Pt>? best = null;
        var bestScore = double.PositiveInfinity;
        var index = ObstacleIndex.For(obstacles);
        foreach (var c in candidates)
        {
            var score = best == null ? FullScore(c, obstacles) : Score(c, obstacles, index, bestScore);
            if (best == null || score < bestScore) { best = c; bestScore = score; }
        }
        return best!;
    }

    /// <summary>The candidate's score, or +∞ as soon as it is known to be no lower than <paramref name="cutoff"/>.</summary>
    private static double Score(IReadOnlyList<Pt> path, IReadOnlyList<Bounds> obstacles, ObstacleIndex? index, double cutoff)
    {
        double len = 0;
        for (var i = 1; i < path.Count; i++) len += Math.Abs(path[i - 1].X - path[i].X) + Math.Abs(path[i - 1].Y - path[i].Y);
        if (0 * 100_000 + len + (path.Count - 2) * 25 >= cutoff) return double.PositiveInfinity;
        var h = 0;
        for (var i = 1; i < path.Count; i++)
        {
            var a = path[i - 1]; var b = path[i];
            if (index != null)
            {
                foreach (var k in index.Near(a, b))
                    if (SegmentHits(a, b, obstacles[k]) && (++h) * 100_000 + len + (path.Count - 2) * 25 >= cutoff) return double.PositiveInfinity;
            }
            else
            {
                foreach (var o in obstacles)
                    if (SegmentHits(a, b, o) && (++h) * 100_000 + len + (path.Count - 2) * 25 >= cutoff) return double.PositiveInfinity;
            }
        }
        return h * 100_000 + len + (path.Count - 2) * 25;
    }

    /// <summary>
    /// Obstacles bucketed by the columns and rows they span. <see cref="SegmentHits"/> only hits a rectangle whose x range (for a
    /// vertical segment) or y range (otherwise) strictly contains the segment's line, so the bucket of that coordinate holds
    /// every rectangle the segment can hit: counts stay exact while most rectangles are never looked at.
    /// </summary>
    private sealed class ObstacleIndex
    {
        private const double Cell = 64;
        private readonly Dictionary<long, List<int>> _cols = new(), _rows = new();

        public static ObstacleIndex? For(IReadOnlyList<Bounds> obstacles)
        {
            if (obstacles.Count < 48) return null;
            var index = new ObstacleIndex();
            for (var i = 0; i < obstacles.Count; i++)
            {
                var o = obstacles[i];
                if (!Add(index._cols, o.X, o.Right, i) || !Add(index._rows, o.Y, o.Bottom, i)) return null;
            }
            return index;
        }

        private static bool Add(Dictionary<long, List<int>> buckets, double from, double to, int i)
        {
            if (!double.IsFinite(from) || !double.IsFinite(to) || Math.Abs(from) > 1e9 || Math.Abs(to) > 1e9) return false;
            if (to < from) return true;   // an empty range never strictly contains a coordinate
            long lo = (long)Math.Floor(from / Cell), hi = (long)Math.Floor(to / Cell);
            if (hi - lo > 4096) return false;
            for (var c = lo; c <= hi; c++)
            {
                if (!buckets.TryGetValue(c, out var list)) buckets[c] = list = new List<int>();
                list.Add(i);
            }
            return true;
        }

        public IReadOnlyList<int> Near(Pt a, Pt b)
        {
            var vertical = Math.Abs(a.X - b.X) < 0.01;
            var v = vertical ? a.X : a.Y;
            if (!double.IsFinite(v) || Math.Abs(v) > 1e9) return [];   // no finite rectangle strictly contains it
            return (vertical ? _cols : _rows).TryGetValue((long)Math.Floor(v / Cell), out var list) ? list : [];
        }
    }

    private static double FullScore(IReadOnlyList<Pt> path, IReadOnlyList<Bounds> obstacles)
    {
        double length = 0; var hits = 0;
        for (var i = 1; i < path.Count; i++)
        {
            var a = path[i - 1]; var b = path[i];
            length += Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
            foreach (var o in obstacles) if (SegmentHits(a, b, o)) hits++;
        }
        return hits * 100_000 + length + (path.Count - 2) * 25;
    }

    /// <summary>Does an axis-aligned segment pass through a rectangle's interior?</summary>
    private static bool SegmentHits(Pt a, Pt b, Bounds r)
    {
        const double m = 2;
        if (Math.Abs(a.X - b.X) < 0.01)
        {
            var y0 = Math.Min(a.Y, b.Y); var y1 = Math.Max(a.Y, b.Y);
            return a.X > r.X + m && a.X < r.Right - m && y1 > r.Y + m && y0 < r.Bottom - m;
        }
        var x0 = Math.Min(a.X, b.X); var x1 = Math.Max(a.X, b.X);
        return a.Y > r.Y + m && a.Y < r.Bottom - m && x1 > r.X + m && x0 < r.Right - m;
    }

    /// <summary>Use cases, actors and messages read better as straight lines; everything else is routed orthogonally.</summary>
    private static bool UsesOrthogonal(DiagramEdge e) =>
        e.Relationship.Kind != ElementKind.Message &&
        ShapeOf(e.Source.Element) is not (NodeShape.Ellipse or NodeShape.StickFigure) &&
        ShapeOf(e.Target.Element) is not (NodeShape.Ellipse or NodeShape.StickFigure);

    /// <summary>
    /// Manhattan routing between two boxes: a straight segment when they overlap on one axis,
    /// otherwise a three-segment "Z" leaving/entering through the faces that face each other.
    /// </summary>
    public static IReadOnlyList<Pt> Orthogonal(Bounds s, Bounds t)
    {
        const double minOverlap = 16;
        var ox0 = Math.Max(s.X, t.X); var ox1 = Math.Min(s.Right, t.Right);
        var oy0 = Math.Max(s.Y, t.Y); var oy1 = Math.Min(s.Bottom, t.Bottom);

        if (ox1 - ox0 >= minOverlap && (s.Bottom <= t.Y || t.Bottom <= s.Y))
        {
            var x = (ox0 + ox1) / 2;
            return s.Bottom <= t.Y ? [new Pt(x, s.Bottom), new Pt(x, t.Y)] : [new Pt(x, s.Y), new Pt(x, t.Bottom)];
        }
        if (oy1 - oy0 >= minOverlap && (s.Right <= t.X || t.Right <= s.X))
        {
            var y = (oy0 + oy1) / 2;
            return s.Right <= t.X ? [new Pt(s.Right, y), new Pt(t.X, y)] : [new Pt(s.X, y), new Pt(t.Right, y)];
        }

        var sc = s.Center; var tc = t.Center;
        var verticalGap = Math.Max(t.Y - s.Bottom, s.Y - t.Bottom);
        var horizontalGap = Math.Max(t.X - s.Right, s.X - t.Right);
        if (verticalGap >= horizontalGap)
        {
            var down = tc.Y > sc.Y;
            var y0 = down ? s.Bottom : s.Y;
            var y1 = down ? t.Y : t.Bottom;
            var mid = (y0 + y1) / 2;
            return [new Pt(sc.X, y0), new Pt(sc.X, mid), new Pt(tc.X, mid), new Pt(tc.X, y1)];
        }
        else
        {
            var right = tc.X > sc.X;
            var x0 = right ? s.Right : s.X;
            var x1 = right ? t.X : t.Right;
            var mid = (x0 + x1) / 2;
            return [new Pt(x0, sc.Y), new Pt(mid, sc.Y), new Pt(mid, tc.Y), new Pt(x1, tc.Y)];
        }
    }

    /// <summary>Points of an arrow head at <paramref name="tip"/>, pointing away from <paramref name="from"/>.</summary>
    public static Pt[] ArrowPolygon(Pt from, Pt tip, ArrowHead head, double size = 12)
    {
        var dx = tip.X - from.X; var dy = tip.Y - from.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return [];
        var ux = dx / len; var uy = dy / len;   // along the line, towards tip
        var px = -uy; var py = ux;              // perpendicular
        var w = head is ArrowHead.FilledDiamond or ArrowHead.HollowDiamond ? size * 0.45 : size * 0.5;
        return head switch
        {
            ArrowHead.Open or ArrowHead.HollowTriangle or ArrowHead.Filled =>
                [new Pt(tip.X - ux * size + px * w, tip.Y - uy * size + py * w), tip, new Pt(tip.X - ux * size - px * w, tip.Y - uy * size - py * w)],
            ArrowHead.FilledDiamond or ArrowHead.HollowDiamond =>
                [tip, new Pt(tip.X - ux * size + px * w, tip.Y - uy * size + py * w), new Pt(tip.X - ux * size * 2, tip.Y - uy * size * 2), new Pt(tip.X - ux * size - px * w, tip.Y - uy * size - py * w)],
            _ => [],
        };
    }

    /// <summary>Midpoint along a polyline (for labels).</summary>
    public static Pt Midpoint(IReadOnlyList<Pt> pts)
    {
        if (pts.Count == 0) return default;
        if (pts.Count == 1) return pts[0];
        var total = 0.0;
        for (var i = 1; i < pts.Count; i++) total += Dist(pts[i - 1], pts[i]);
        var half = total / 2;
        for (var i = 1; i < pts.Count; i++)
        {
            var d = Dist(pts[i - 1], pts[i]);
            if (half <= d && d > 0)
            {
                var t = half / d;
                return new Pt(pts[i - 1].X + (pts[i].X - pts[i - 1].X) * t, pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * t);
            }
            half -= d;
        }
        return pts[^1];
    }

    public static double Dist(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public static double DistanceToSegment(Pt p, Pt a, Pt b)
    {
        var dx = b.X - a.X; var dy = b.Y - a.Y;
        var l2 = dx * dx + dy * dy;
        if (l2 < 1e-9) return Dist(p, a);
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1);
        return Dist(p, new Pt(a.X + t * dx, a.Y + t * dy));
    }

    public const double LineHeight = 16;

    /// <summary>Node bounds grown so that all compartment content fits (rectangles only).</summary>
    public static Bounds EffectiveBounds(ModelRepository model, DiagramNode n, DiagramType dt, Bounds? b = null)
    {
        var bounds = b ?? n.Bounds;
        var shape = ShapeOf(n.Element);
        if (shape != NodeShape.Rectangle) return bounds;
        var wrap = n.Element.Kind == ElementKind.Requirement ? Math.Max(8, (int)((bounds.Width - 12) / 6.4)) : 0;
        var h = PreferredHeight(Content(model, n, dt), shape, LineHeight, wrap);
        return h > bounds.Height ? bounds with { Height = h } : bounds;
    }

    /// <summary>Height a shape needs to display its content, given a line height.</summary>
    public static double PreferredHeight(NodeContent c, NodeShape shape, double lineHeight, int wrapChars = 0)
    {
        if (shape != NodeShape.Rectangle) return 0;
        var lines = 1 + (c.Keyword != null ? 1 : 0);
        var h = lines * lineHeight + 14;
        foreach (var comp in c.Compartments)
        {
            var n = wrapChars > 0 ? comp.Lines.Sum(l => WrappedLineCount(l, wrapChars)) : comp.Lines.Count;
            h += (comp.Title != null ? lineHeight : 0) + Math.Max(1, n) * lineHeight + 8;
        }
        return h;
    }

    /// <summary>Line count of greedy word wrapping (must match the renderers' wrapping).</summary>
    public static int WrappedLineCount(string s, int perLine)
    {
        var count = 0;
        foreach (var para in s.Split('\n'))
        {
            var len = 0;
            count++;
            foreach (var w in para.Split(' '))
            {
                if (len + w.Length + 1 > perLine && len > 0) { count++; len = 0; }
                len += (len > 0 ? 1 : 0) + w.Length;
            }
        }
        return count;
    }
}
