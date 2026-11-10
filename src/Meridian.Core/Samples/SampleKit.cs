using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Samples;

/// <summary>
/// Small building helpers shared by the domain samples (<see cref="CubeSatSample"/>, <see cref="AdaptiveCruiseSample"/>,
/// <see cref="InfusionPumpSample"/>). Everything goes through the public <see cref="ModelRepository"/> API, so the
/// samples are plain model content: undoable, replicable, exportable.
/// </summary>
internal sealed class SampleKit(ModelRepository m)
{
    public ModelRepository M { get; } = m;

    public Element Pkg(Element owner, string name, string? doc = null)
    {
        var p = M.Create(owner, ElementKind.Package, name);
        if (doc != null) M.Set(p, FieldKeys.Documentation, doc);
        return p;
    }

    /// <summary>A requirement with id, text and optional attributes. Set everything before linking it (name and text edits make links suspect).</summary>
    public Element Req(Element owner, string id, string name, string text, params (string Key, string Value)[] attrs)
    {
        var r = M.Create(owner, ElementKind.Requirement, name);
        M.Set(r, FieldKeys.RequirementId, id);
        M.Set(r, FieldKeys.Text, text);
        foreach (var (k, v) in attrs) M.Set(r, k, v);
        return r;
    }

    public Element Block(Element owner, string name, string? doc = null, string? stereotype = null, ElementKind kind = ElementKind.Block)
    {
        var b = M.Create(owner, kind, name);
        if (doc != null) M.Set(b, FieldKeys.Documentation, doc);
        if (stereotype != null) M.Set(b, FieldKeys.Stereotypes, stereotype);
        return b;
    }

    public Element Part(Element owner, string name, Element type, string? multiplicity = null)
    {
        var p = M.Create(owner, ElementKind.Property, name);
        M.Set(p, FieldKeys.Type, type.Id);
        if (multiplicity != null) M.Set(p, FieldKeys.Multiplicity, multiplicity);
        return p;
    }

    /// <summary>A value property: <paramref name="type"/> is a primitive name or a value type element id.</summary>
    public Element Value(Element owner, string name, string type = "Real", string? value = null, string? unit = null, string? distribution = null)
    {
        var p = M.Create(owner, ElementKind.Property, name);
        M.Set(p, FieldKeys.Type, type);
        if (value != null) M.Set(p, FieldKeys.DefaultValue, value);
        if (unit != null) M.Set(p, ExtFieldKeys.Unit, unit);
        if (distribution != null) M.Set(p, ExtFieldKeys.Distribution, distribution);
        return p;
    }

    public Element Port(Element owner, string name, Element type, string direction, string? doc = null)
    {
        var p = M.Create(owner, ElementKind.Port, name);
        M.Set(p, FieldKeys.Type, type.Id);
        M.Set(p, FieldKeys.Direction, direction);
        M.Set(p, FieldKeys.PortKind, "proxy");
        if (doc != null) M.Set(p, FieldKeys.Documentation, doc);
        return p;
    }

    /// <summary>A test case with its last verdict ("" = not run yet) that verifies the given requirements.</summary>
    public Element Test(Element owner, string name, string verdict, string doc, params Element[] verifies)
    {
        var t = M.Create(owner, ElementKind.TestCase, name);
        if (verdict.Length > 0) M.Set(t, ReqFields.Verdict, verdict);
        foreach (var r in verifies) M.Connect(ElementKind.Verify, t, r);
        M.Set(t, FieldKeys.Documentation, doc);
        return t;
    }

    /// <summary>A constraint block with its equation(s) and one Real parameter per name.</summary>
    public (Element Block, Dictionary<string, Element> Params) Constraint(Element owner, string name, string body, params string[] parameters)
    {
        var cb = M.Create(owner, ElementKind.ConstraintBlock, name);
        M.Set(cb, FieldKeys.Body, body);
        var ps = new Dictionary<string, Element>(StringComparer.Ordinal);
        foreach (var p in parameters)
        {
            var e = M.Create(cb, ElementKind.Property, p);
            M.Set(e, FieldKeys.Type, "Real");
            ps[p] = e;
        }
        return (cb, ps);
    }

    /// <summary>Binding connector between a value property of <paramref name="block"/> and a parameter of a constraint usage.</summary>
    public Relationship Bind(Element block, Element value, Element usage, Element parameter)
    {
        var b = M.Connect(ElementKind.Binding, value, parameter, block);
        M.Set(b, FieldKeys.TargetPart, usage.Id);
        return b;
    }

    /// <summary>Connector between two ports on two parts of <paramref name="context"/>, optionally carrying an item flow.</summary>
    public Relationship Wire(Element context, Element fromPart, Element fromPort, Element toPart, Element toPort, string? item = null)
    {
        var c = M.Connect(ElementKind.Connector, fromPort, toPort, context);
        M.Set(c, FieldKeys.SourcePart, fromPart.Id);
        M.Set(c, FieldKeys.TargetPart, toPart.Id);
        if (item != null) { M.Set(c, FieldKeys.ItemFlow, item); M.Set(c, FieldKeys.FlowDirection, "forward"); }
        return c;
    }

    public Relationship Flow(Element from, Element to, string? guard = null)
    {
        var f = M.Connect(ElementKind.ControlFlow, from, to);
        if (guard != null) M.Set(f, FieldKeys.Guard, guard);
        return f;
    }

    /// <summary>A transition; <paramref name="trigger"/> is its name (signal, after(…) or when(…)).</summary>
    public Relationship Go(Element from, Element to, string? trigger = null, string? guard = null, string? effect = null)
    {
        var t = M.Connect(ElementKind.Transition, from, to);
        if (trigger != null) M.Set(t, FieldKeys.Name, trigger);
        if (guard != null) M.Set(t, FieldKeys.Guard, guard);
        if (effect != null) M.Set(t, FieldKeys.Effect, effect);
        return t;
    }

    public Element State(Element owner, string name, string? entry = null, string? doActivity = null, string? exit = null)
    {
        var s = M.Create(owner, ElementKind.State, name);
        if (entry != null) M.Set(s, FieldKeys.Entry, entry);
        if (doActivity != null) M.Set(s, FieldKeys.DoActivity, doActivity);
        if (exit != null) M.Set(s, FieldKeys.Exit, exit);
        return s;
    }

    public Element Action(Element activity, string name, string? body = null, string? duration = null)
    {
        var a = M.Create(activity, ElementKind.Action, name);
        if (body != null) M.Set(a, FieldKeys.Body, body);
        if (duration != null) M.Set(a, ExtFieldKeys.Duration, duration);
        return a;
    }

    public Element Stereotype(Element profile, string name, string metaclass, string color, params (string Tag, string Type, string? Values)[] tags)
    {
        var s = M.Create(profile, ElementKind.Stereotype, name);
        M.Set(s, FieldKeys.Metaclass, metaclass);
        M.Set(s, FieldKeys.Color, color);
        foreach (var (tag, type, values) in tags)
        {
            var t = M.Create(s, ElementKind.TagDefinition, tag);
            M.Set(t, FieldKeys.TagType, type == "String" ? null : type);
            M.Set(t, FieldKeys.TagValues, values);
        }
        return s;
    }

    public void Tag(Element e, string tag, string value) => M.Set(e, FieldKeys.TagPrefix + tag, value);

    // ------------------------------------------------------------------ layout

    /// <summary>Shows <paramref name="e"/> at (x, y); the height grows to fit its compartments so nothing overlaps later.</summary>
    public DiagramNode Place(Diagram d, Element e, double x, double y, double w = 0, double h = 0, bool edges = true, DiagramNode? parent = null)
    {
        var (dw, dh) = Notation.DefaultSizeOf(e);
        var n = M.AddNode(d, e, new Bounds(x, y, w > 0 ? w : dw, h > 0 ? h : dh), edges, parent);
        Fit(d, n);
        return n;
    }

    /// <summary>Places shapes left to right starting at (x, y). Returns the bottom of the tallest one.</summary>
    public double Row(Diagram d, double x, double y, double gap, double w, params Element[] elements)
    {
        var bottom = y;
        foreach (var e in elements)
        {
            var n = Place(d, e, x, y, w);
            bottom = Math.Max(bottom, n.Bounds.Bottom);
            x += n.Bounds.Width + gap;
        }
        return bottom;
    }

    /// <summary>A port shape on the border of a part shape, at the given side ("L", "R", "T", "B") and fraction along it.</summary>
    public DiagramNode PortOn(Diagram d, DiagramNode part, Element port, string side, double at = 0.5)
    {
        var (pw, ph) = Metamodel.DefaultSize(ElementKind.Port);
        var b = part.Bounds;
        var (cx, cy) = side switch
        {
            "L" => (b.X, b.Y + b.Height * at),
            "R" => (b.Right, b.Y + b.Height * at),
            "T" => (b.X + b.Width * at, b.Y),
            _ => (b.X + b.Width * at, b.Bottom),
        };
        var bounds = Notation.SnapToBorder(new Bounds(cx - pw / 2, cy - ph / 2, pw, ph), b);
        return M.AddNode(d, port, bounds, showRelationships: false, parent: part);
    }

    /// <summary>A border shape (port, constraint parameter) on the given side of <paramref name="owner"/>; <paramref name="at"/> is the absolute y (L/R) or x (T/B).</summary>
    public DiagramNode BorderAt(Diagram d, DiagramNode owner, Element e, string side, double at)
    {
        var b = owner.Bounds;
        var frac = side is "L" or "R" ? (at - b.Y) / b.Height : (at - b.X) / b.Width;
        return PortOn(d, owner, e, side, frac);
    }

    /// <summary>Places shapes on a grid, <paramref name="cols"/> per row; each row starts below the tallest shape of the previous one. Returns the bottom.</summary>
    public double Grid(Diagram d, double x, double y, int cols, double w, double gapX, double gapY, IEnumerable<Element> elements)
    {
        var list = elements.ToList();
        var bottom = y;
        for (var i = 0; i < list.Count; i += cols)
        {
            bottom = Row(d, x, y, gapX, w, list.Skip(i).Take(cols).ToArray());
            y = bottom + gapY;
        }
        return bottom;
    }

    /// <summary>Grows a shape to the height its content needs (rectangles only).</summary>
    public void Fit(Diagram d, DiagramNode n)
    {
        var eff = Notation.EffectiveBounds(M, n, d.DiagramType);
        if (eff.Height > n.Bounds.Height) M.SetBounds(d, new Dictionary<DiagramNode, Bounds> { [n] = n.Bounds with { Height = Math.Ceiling(eff.Height / 10) * 10 } }, "Fit");
    }

    /// <summary>Fits every shape, then arranges the diagram with the layered auto-layout.</summary>
    public void Arrange(Diagram d, double hGap = 50, double vGap = 70)
    {
        foreach (var n in d.Nodes.ToList()) Fit(d, n);
        M.SetBounds(d, AutoLayout.Layered(d, hGap, vGap), "Arrange");
    }

    /// <summary>Adds paths for every relationship between shapes already on the diagram.</summary>
    public void ShowEdges(Diagram d, params ElementKind[] kinds)
    {
        var shown = d.Nodes.Select(n => n.Element).ToHashSet();
        foreach (var r in M.Relationships.Where(r => (kinds.Length == 0 || kinds.Contains(r.Kind)) && shown.Contains(r.Source) && shown.Contains(r.Target)).ToList())
            M.AddEdge(d, r);
    }
}
