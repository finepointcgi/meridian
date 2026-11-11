namespace Meridian.Core.Model;

public readonly record struct Pt(double X, double Y);

public readonly record struct Bounds(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public Pt Center => new(X + Width / 2, Y + Height / 2);
    public bool Contains(Pt p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
    public bool Intersects(Bounds o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
    public Bounds Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };

    public static Bounds FromPoints(Pt a, Pt b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}

/// <summary>A shape on a diagram that presents one model element.</summary>
public sealed class DiagramNode
{
    public DiagramNode(Element element, Bounds bounds, string? id = null)
    {
        Element = element;
        Bounds = bounds;
        Id = string.IsNullOrEmpty(id) ? Model.Element.NewId() : id;
    }

    public string Id { get; }
    public Element Element { get; }
    public Bounds Bounds { get; internal set; }

    /// <summary>Containing shape (ports on parts, nested parts, substates, pins, swimlane contents); null = top level.</summary>
    public string? ParentId { get; internal set; }

    /// <summary>View-only appearance overrides (fill, accent, text), per shape.</summary>
    public Dictionary<string, string> Style { get; internal set; } = new();
}

/// <summary>A path on a diagram that presents one relationship between two shapes.</summary>
public sealed class DiagramEdge
{
    public DiagramEdge(Relationship relationship, DiagramNode source, DiagramNode target, string? id = null)
    {
        Relationship = relationship;
        Source = source;
        Target = target;
        Id = string.IsNullOrEmpty(id) ? Model.Element.NewId() : id;
    }

    public string Id { get; }
    public Relationship Relationship { get; }
    public DiagramNode Source { get; }
    public DiagramNode Target { get; }
    public List<Pt> Waypoints { get; internal set; } = new();

    /// <summary>View-only appearance overrides (stroke colour, width), per connector.</summary>
    public Dictionary<string, string> Style { get; internal set; } = new();
}

/// <summary>Diagrams are elements so that they live in the containment tree like in Cameo and EA.</summary>
public sealed class Diagram : Element
{
    internal readonly List<DiagramNode> NodeList = new();
    internal readonly List<DiagramEdge> EdgeList = new();

    public Diagram(DiagramType type, string? id = null) : base(ElementKind.Diagram, id)
    {
        DiagramType = type;
    }

    private Diagram(Diagram source) : base(source)
    {
        DiagramType = source.DiagramType;
    }

    /// <summary>Nodes and edges are copied by <see cref="ModelRepository.CreateReplica"/> once every element exists.</summary>
    internal override Element CopyForReplica() => new Diagram(this);

    public DiagramType DiagramType { get; }
    public IReadOnlyList<DiagramNode> Nodes => NodeList;
    public IReadOnlyList<DiagramEdge> Edges => EdgeList;

    public DiagramNode? NodeFor(Element e) => NodeList.FirstOrDefault(n => ReferenceEquals(n.Element, e));
    public DiagramNode? FindNode(string id) => NodeList.FirstOrDefault(n => n.Id == id);
    public DiagramEdge? EdgeFor(Relationship r) => EdgeList.FirstOrDefault(e => ReferenceEquals(e.Relationship, r));
    public IEnumerable<DiagramEdge> EdgesOf(DiagramNode n) =>
        EdgeList.Where(e => ReferenceEquals(e.Source, n) || ReferenceEquals(e.Target, n));

    public bool Shows(Element e) => NodeFor(e) != null;

    public DiagramNode? ParentOf(DiagramNode n) => n.ParentId is { } p ? FindNode(p) : null;

    /// <summary>All shapes nested (transitively) inside <paramref name="n"/>.</summary>
    public IEnumerable<DiagramNode> DescendantsOf(DiagramNode n)
    {
        foreach (var c in NodeList.Where(x => x.ParentId == n.Id))
        {
            yield return c;
            foreach (var d in DescendantsOf(c)) yield return d;
        }
    }
}
