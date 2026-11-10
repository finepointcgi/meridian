using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;

namespace Meridian.Web.Services;

[Flags]
public enum Mods { None = 0, Shift = 1, Ctrl = 2, Alt = 4, Meta = 8 }

/// <summary>
/// UI-independent diagram editing logic: selection, move, resize, connect, waypoints, marquee, pan and zoom.
/// The Blazor component only translates browser events into calls on this class.
/// </summary>
public sealed partial class DiagramController
{
    public const double Grid = 10;
    public const double HandleSize = 8;

    public enum Gesture { None, Move, Resize, Connect, Marquee, Pan, Waypoint, QuickConnect, EdgeDrag }

    /// <summary>An alignment guide shown while dragging (a line where edges or centres line up).</summary>
    public readonly record struct Guide(bool Vertical, double Pos, double From, double To);

    private readonly Workspace _ws;
    private readonly HashSet<DiagramNode> _selNodes = new();
    private readonly HashSet<DiagramEdge> _selEdges = new();
    private (List<DiagramNode> Nodes, List<DiagramEdge> Edges) _selAtPress = ([], []);
    private readonly Dictionary<DiagramNode, Bounds> _preview = new();
    private Dictionary<DiagramNode, Bounds> _origin = new();
    private Pt _pressScreen;
    private Pt _pressModel;
    private (double X, double Y) _panAtPress;
    private int _waypointIndex = -1;
    private int _handle = -1;
    private int _quickDir = -1;
    private DiagramEdge? _dragEdge;
    private readonly List<Guide> _guides = new();

    public DiagramController(Workspace ws, Diagram diagram)
    {
        _ws = ws;
        Diagram = diagram;
    }

    public ModelRepository Model => _ws.Model;
    public Diagram Diagram { get; }
    public double Zoom { get; private set; } = 1;
    public double PanX { get; private set; } = 30;
    public double PanY { get; private set; } = 40;
    public double ViewWidth { get; set; } = 1000;
    public double ViewHeight { get; set; } = 700;
    public bool NeedsFit { get; set; } = true;

    /// <summary>Screen width covered by the floating shapes panel; fitting keeps content clear of it.</summary>
    public double LeftInset { get; set; } = 60;

    public ElementKind? Tool { get; set; }

    /// <summary>Subtype for the active tool (BPMN / ArchiMate palettes).</summary>
    public string? ToolSubtype { get; set; }

    /// <summary>Stereotype applied by the active tool (UAF viewpoint palettes).</summary>
    public string? ToolStereotype { get; set; }

    /// <summary>Is <paramref name="item"/> the active tool?</summary>
    public bool IsTool(PaletteItem item) => Tool == item.Kind && ToolSubtype == item.Subtype && ToolStereotype == item.Stereotype;

    public void SetTool(PaletteItem? item)
    {
        Tool = item?.Kind;
        ToolSubtype = item?.Subtype;
        ToolStereotype = item?.Stereotype;
    }
    public Gesture Current { get; private set; }
    public Pt PressModel => _pressModel;
    public Pt CurrentModel { get; private set; }
    public DiagramNode? ConnectFrom { get; private set; }
    public IReadOnlyList<Pt>? WaypointPreview { get; private set; }
    public IReadOnlyCollection<DiagramNode> SelectedNodes => _selNodes;

    /// <summary>Every selected connector (Shift/⌘-click, rubber band, ⌘A).</summary>
    public IReadOnlyCollection<DiagramEdge> SelectedEdges => _selEdges;

    /// <summary>The connector when it is the only thing selected (bends, routing, label editing), else null. Setting it selects just that connector's edges set.</summary>
    public DiagramEdge? SelectedEdge
    {
        get => _selNodes.Count == 0 && _selEdges.Count == 1 ? _selEdges.First() : null;
        private set { _selEdges.Clear(); if (value != null) _selEdges.Add(value); }
    }

    /// <summary>Shapes plus connectors selected.</summary>
    public int SelectionCount => _selNodes.Count + _selEdges.Count;

    /// <summary>The model elements behind the selection: shapes' elements, then connectors' relationships.</summary>
    public IReadOnlyList<Element> SelectedElements =>
        _selNodes.Select(n => n.Element).Concat(_selEdges.Select(e => (Element)e.Relationship)).Distinct().ToList();

    /// <summary>Set by input handling when the user asks to open an element's sub-diagram.</summary>
    public Element? DrillRequest { get; set; }

    /// <summary>Set when a shape's hyperlink is followed (its link icon, or Ctrl/⌘-click); the view opens it.</summary>
    public string? LinkRequest { get; set; }

    /// <summary>Layer that new shapes are put on (the default layer unless the user picks another in the layers panel).</summary>
    public string ActiveLayerId { get; set; } = DiagramExtras.DefaultLayerId;

    /// <summary>Node whose name is being edited inline, if any.</summary>
    public DiagramNode? Editing { get; set; }

    /// <summary>Connector whose label is being edited inline, if any.</summary>
    public DiagramEdge? EditingEdge { get; set; }

    /// <summary>When editing was started by typing, the typed text replaces the label.</summary>
    public string? EditSeed { get; set; }

    /// <summary>Shape under the pointer (shows the quick-connect arrows).</summary>
    public DiagramNode? Hover { get; private set; }
    public int HoverArrow { get; private set; } = -1;

    /// <summary>Space bar held: dragging pans the canvas.</summary>
    public bool SpaceHeld { get; set; }

    /// <summary>Screen point where a double-click on empty canvas asked for the quick-add picker.</summary>
    public Pt? QuickAddRequest { get; set; }

    public IReadOnlyList<Guide> Guides => _guides;

    public Element? PrimarySelection => SelectionCount != 1 ? null : SelectedEdge?.Relationship ?? _selNodes.First().Element;

    // ---------------------------------------------------------------- coordinates & geometry

    public Pt ToModel(double sx, double sy) => new((sx - PanX) / Zoom, (sy - PanY) / Zoom);
    public Pt ToScreen(Pt p) => new(p.X * Zoom + PanX, p.Y * Zoom + PanY);
    /// <summary>Rounds to the grid unless the user turned snapping off (Settings ▸ Appearance).</summary>
    private double Snap(double v) => _ws.Preferences.SnapToGrid ? Math.Round(v / Grid) * Grid : Math.Round(v);

    public Bounds BoundsOf(DiagramNode n) =>
        Notation.EffectiveBounds(Model, n, Diagram.DiagramType, _preview.TryGetValue(n, out var b) ? b : n.Bounds);

    public IReadOnlyList<Pt>? WaypointsFor(DiagramEdge e) => WaypointPreview != null && ReferenceEquals(e, SelectedEdge) ? WaypointPreview : null;

    // ---------------------------------------------------------------- selection

    public void Select(Element? e)
    {
        _selNodes.Clear();
        SelectedEdge = null;
        if (e is Relationship r && Diagram.EdgeFor(r) is { } edge && Pickable(edge)) SelectedEdge = edge;
        else if (e != null && Diagram.NodeFor(e) is { } node && Pickable(node)) _selNodes.Add(node);
    }

    public void SelectAll()
    {
        foreach (var n in Diagram.Nodes.Where(Pickable)) _selNodes.Add(n);
        foreach (var e in Diagram.Edges.Where(Pickable)) _selEdges.Add(e);
        Notify();
    }

    /// <summary>Selects exactly these elements' shapes and connectors on this diagram.</summary>
    public void SelectElements(IEnumerable<Element> elements)
    {
        ClearSelection();
        foreach (var e in elements)
        {
            if (e is Relationship r && Diagram.EdgeFor(r) is { } edge && Pickable(edge)) _selEdges.Add(edge);
            else if (Diagram.NodeFor(e) is { } node && Pickable(node)) _selNodes.Add(node);
        }
        Notify();
    }

    public void ClearSelection()
    {
        _selNodes.Clear();
        _selEdges.Clear();
    }

    public bool IsSelected(DiagramEdge e) => _selEdges.Contains(e);

    /// <summary>Adds a shape to the selection, or takes it out if it is already in.</summary>
    public void Toggle(DiagramNode n)
    {
        if (!_selNodes.Remove(n) && Pickable(n)) _selNodes.Add(n);
        Notify();
    }

    /// <summary>Adds a connector to the selection, or takes it out if it is already in.</summary>
    public void Toggle(DiagramEdge e)
    {
        if (!_selEdges.Remove(e) && Pickable(e)) _selEdges.Add(e);
        Notify();
    }

    /// <summary>
    /// Touch long-press while editing: the press already went through <see cref="PointerDown"/>, which may have replaced the
    /// selection. Puts back what was selected before the press, then adds (or removes) the shape or connector held.
    /// Returns false when nothing is there.
    /// </summary>
    public bool LongPressToggle(double sx, double sy)
    {
        var p = ToModel(sx, sy);
        var n = HitNode(p);
        var e = n == null ? HitEdge(p) : null;
        if (n == null && e == null) return false;
        Current = Gesture.None;
        _preview.Clear();
        WaypointPreview = null;
        _dragEdge = null;
        ClearSelection();
        foreach (var x in _selAtPress.Nodes.Where(Diagram.Nodes.Contains)) _selNodes.Add(x);
        foreach (var x in _selAtPress.Edges.Where(Diagram.Edges.Contains)) _selEdges.Add(x);
        if (n != null) Toggle(n); else Toggle(e!);
        return true;
    }

    // ---------------------------------------------------------------- layers

    /// <summary>Shapes on hidden or locked layers can't be selected, moved or hit.</summary>
    public bool Pickable(DiagramNode n) => !DiagramExtras.IsHidden(Diagram, n) && !DiagramExtras.IsLocked(Diagram, n);
    public bool Pickable(DiagramEdge e) => !DiagramExtras.IsHidden(Diagram, e) && !DiagramExtras.IsLocked(Diagram, e);

    /// <summary>Shapes drawn, in drawing order (hidden layers left out).</summary>
    public IReadOnlyList<DiagramNode> VisibleNodes => DiagramExtras.VisibleView(Diagram).Nodes;

    /// <summary>Puts the selected shapes and connector on a layer.</summary>
    public void MoveSelectionToLayer(string layerId)
    {
        if (_ws.ReadOnly || SelectionCount == 0) return;
        try
        {
            Model.MoveToLayer(Diagram, _selNodes.ToList(), _selEdges.ToList(), layerId);
            var name = DiagramExtras.FindLayer(Diagram, layerId)?.Name ?? "Default";
            _ws.SetStatus($"Moved to layer “{name}”");
            ClearInvalidSelection();
            Notify();
        }
        catch (InvalidOperationException ex) { _ws.SetStatus(ex.Message); }
    }

    /// <summary>The selection's layer when it is the same for everything selected.</summary>
    public string? SelectionLayer()
    {
        var ids = _selNodes.Select(DiagramExtras.LayerIdOf).Concat(_selEdges.Select(DiagramExtras.LayerIdOf)).Distinct().ToList();
        return ids.Count == 1 ? ids[0] : null;
    }

    public void ClearInvalidSelection()
    {
        _selNodes.RemoveWhere(n => !Diagram.Nodes.Contains(n) || !Pickable(n));
        _selEdges.RemoveWhere(e => !Diagram.Edges.Contains(e) || !Pickable(e));
        if (DiagramExtras.FindLayer(Diagram, ActiveLayerId) == null) ActiveLayerId = DiagramExtras.DefaultLayerId;
        if (Editing != null && !Diagram.Nodes.Contains(Editing)) Editing = null;
        if (EditingEdge != null && !Diagram.Edges.Contains(EditingEdge)) EditingEdge = null;
        if (Hover != null && !Diagram.Nodes.Contains(Hover)) Hover = null;
    }

    private void Notify() => _ws.Select(PrimarySelection, syncCanvas: false);

    // ---------------------------------------------------------------- view

    public void SetZoom(double z, double? sx = null, double? sy = null)
    {
        z = Math.Clamp(z, 0.2, 4);
        var cx = sx ?? ViewWidth / 2;
        var cy = sy ?? ViewHeight / 2;
        var m = ToModel(cx, cy);
        Zoom = z;
        PanX = cx - m.X * Zoom;
        PanY = cy - m.Y * Zoom;
    }

    /// <summary>The first view of a diagram: fit, or 100% from the top-left of its content (Settings ▸ Diagrams).</summary>
    public void InitialZoom()
    {
        if (_ws.Preferences.DefaultZoom == Settings.DefaultZoom.Fit || Diagram.Nodes.Count == 0) { ZoomToFit(); return; }
        NeedsFit = false;
        var bs = Diagram.Nodes.Select(BoundsOf).ToList();
        Zoom = 1;
        PanX = LeftInset + 30 - bs.Min(b => b.X);
        PanY = 60 - bs.Min(b => b.Y);
    }

    public void ZoomToFit()
    {
        NeedsFit = false;
        if (Diagram.Nodes.Count == 0) { Zoom = 1; PanX = LeftInset + 30; PanY = 40; return; }   // frame title clear of the shapes panel
        var bs = Diagram.Nodes.Select(BoundsOf).ToList();
        var frame = Diagram.DiagramType == DiagramType.InternalBlock ? 50 : 0;
        double x0 = bs.Min(b => b.X) - 50 - frame, y0 = bs.Min(b => b.Y) - 90 - frame, x1 = bs.Max(b => b.Right) + 40 + frame, y1 = bs.Max(b => b.Bottom) + 30 + frame;
        var usable = Math.Max(200, ViewWidth - LeftInset - 10);
        Zoom = Math.Clamp(Math.Min(usable / (x1 - x0), ViewHeight / (y1 - y0)), 0.4, 1.0);
        PanX = -x0 * Zoom + LeftInset + Math.Max(0, (usable - (x1 - x0) * Zoom) / 2);
        PanY = -y0 * Zoom + 10;
    }

    /// <summary>Pans just enough to bring a model rectangle into view (clear of the shapes panel).</summary>
    public void EnsureVisible(Bounds b)
    {
        const double margin = 40;
        var tl = ToScreen(new Pt(b.X, b.Y));
        var br = ToScreen(new Pt(b.Right, b.Bottom));
        double dx = 0, dy = 0;
        if (br.X > ViewWidth - margin) dx = ViewWidth - margin - br.X;
        if (tl.X + dx < LeftInset + margin) dx = LeftInset + margin - tl.X;
        if (br.Y > ViewHeight - margin) dy = ViewHeight - margin - br.Y;
        if (tl.Y + dy < margin) dy = margin - tl.Y;
        PanBy(dx, dy);
    }

    public void PanBy(double dx, double dy)
    {
        PanX += dx;
        PanY += dy;
    }

    // ---------------------------------------------------------------- sub-diagrams & minimap

    public bool HasSubDiagram(Element e) =>
        e.OwnedElements.Any(x => x is Diagram && !ReferenceEquals(x, Diagram)) ||
        e.Kind == ElementKind.Action && Model.Find(e.Get(FieldKeys.Behavior)) is { } called && called.OwnedElements.OfType<Diagram>().Any();

    public const double BadgeSize = 16;

    /// <summary>Model-space rectangle of the ⧉ badge drawn on shapes that own sub-diagrams.</summary>
    public Bounds BadgeBounds(DiagramNode n)
    {
        var b = BoundsOf(n);
        var s = BadgeSize / Zoom;
        return new Bounds(b.Right - s - 3 / Zoom, b.Y + 6 / Zoom, s, s);
    }

    private DiagramNode? HitBadge(Pt p) =>
        VisibleNodes.LastOrDefault(n => HasSubDiagram(n.Element) && BadgeBounds(n).Contains(p));

    /// <summary>The visible shape whose link icon is under <paramref name="p"/> (locked layers included: links still work there).</summary>
    public DiagramNode? HitLinkBadge(Pt p) =>
        VisibleNodes.LastOrDefault(n => DiagramExtras.LinkOf(n) != null && Inflate(DiagramExtras.LinkBadgeBounds(BoundsOf(n)), 2 / Zoom).Contains(p));

    private static Bounds Inflate(Bounds b, double d) => new(b.X - d, b.Y - d, b.Width + 2 * d, b.Height + 2 * d);

    public Bounds ContentBounds()
    {
        var visible = VisibleNodes;
        if (visible.Count == 0) return new Bounds(0, 0, 800, 600);
        var bs = visible.Select(BoundsOf).ToList();
        double x0 = bs.Min(b => b.X) - 40, y0 = bs.Min(b => b.Y) - 40, x1 = bs.Max(b => b.Right) + 40, y1 = bs.Max(b => b.Bottom) + 40;
        return new Bounds(x0, y0, x1 - x0, y1 - y0);
    }

    public Bounds Viewport => new(-PanX / Zoom, -PanY / Zoom, ViewWidth / Zoom, ViewHeight / Zoom);

    public void CenterOn(Pt p)
    {
        PanX = ViewWidth / 2 - p.X * Zoom;
        PanY = ViewHeight / 2 - p.Y * Zoom;
    }

    // ---------------------------------------------------------------- hit testing

    public DiagramNode? HitNode(Pt p, DiagramNode? except = null)
    {
        // Front-most first, in layer order; hidden layers aren't drawn and locked ones can't be picked.
        var view = DiagramExtras.VisibleView(Diagram);
        var layers = ReferenceEquals(view, Diagram) ? null : DiagramExtras.StateOf(Diagram);
        var nodes = view.Nodes;
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            var n = nodes[i];
            if (ReferenceEquals(n, except) || layers?.IsLocked(n) == true) continue;
            var b = BoundsOf(n);
            var pad = b.Width < 30 ? 5 : 0;
            if (!new Bounds(b.X - pad, b.Y - pad, b.Width + 2 * pad, b.Height + 2 * pad).Contains(p)) continue;
            var tol = 7 / Zoom;
            switch (Notation.ShapeOf(n.Element))
            {
                // Lifelines: the header box or close to the dashed line, so messages between them stay clickable.
                case NodeShape.Lifeline when p.Y > b.Y + Notation.LifelineHeader && Math.Abs(p.X - b.Center.X) > tol:
                    continue;
                // Fragments: only the frame border and the operator tag; the inside belongs to what it encloses.
                case NodeShape.Fragment when p.X > b.X + tol && p.X < b.Right - tol && p.Y > b.Y + 22 && p.Y < b.Bottom - tol:
                    continue;
            }
            return n;
        }
        return null;
    }

    public DiagramEdge? HitEdge(Pt p)
    {
        var tol = 6 / Zoom;
        var view = DiagramExtras.VisibleView(Diagram);
        var layers = ReferenceEquals(view, Diagram) ? null : DiagramExtras.StateOf(Diagram);
        var routes = EdgeRouter.RouteAll(view, BoundsOf);
        for (var i = view.Edges.Count - 1; i >= 0; i--)
        {
            var e = view.Edges[i];
            if (layers?.IsLocked(e) == true) continue;
            var pts = routes[e];
            for (var k = 1; k < pts.Count; k++)
                if (Notation.DistanceToSegment(p, pts[k - 1], pts[k]) <= tol) return e;
        }
        return null;
    }

    /// <summary>Resize handles clockwise from the top-left corner: NW, N, NE, E, SE, S, SW, W.</summary>
    public IEnumerable<(int Index, Pt At)> Handles(DiagramNode n)
    {
        var b = BoundsOf(n);
        var small = b.Width < 30 || b.Height < 30;
        yield return (4, new Pt(b.Right, b.Bottom));
        if (small) yield break;
        yield return (0, new Pt(b.X, b.Y));
        yield return (1, new Pt(b.Center.X, b.Y));
        yield return (2, new Pt(b.Right, b.Y));
        yield return (3, new Pt(b.Right, b.Center.Y));
        yield return (5, new Pt(b.Center.X, b.Bottom));
        yield return (6, new Pt(b.X, b.Bottom));
        yield return (7, new Pt(b.X, b.Center.Y));
    }

    public static string HandleCursor(int i) => i switch { 0 or 4 => "nwse-resize", 2 or 6 => "nesw-resize", 1 or 5 => "ns-resize", _ => "ew-resize" };

    public int HitResizeHandle(Pt p)
    {
        if (_selNodes.Count != 1 || _ws.ReadOnly) return -1;
        var n = _selNodes.First();
        if (Notation.IsBorderShape(n.Element)) return -1;
        var hs = HandleSize / Zoom;
        foreach (var (i, at) in Handles(n))
            if (Math.Abs(p.X - at.X) <= hs && Math.Abs(p.Y - at.Y) <= hs) return i;
        return -1;
    }

    // ---------------------------------------------------------------- quick connect (draw.io-style hover arrows)

    /// <summary>Directions offered for a shape: 0 up, 1 right, 2 down, 3 left.</summary>
    public IReadOnlyList<int> ArrowDirs(DiagramNode n) => n.Element.Kind == ElementKind.Lifeline ? [1, 3] : [0, 1, 2, 3];

    public Pt ArrowPoint(DiagramNode n, int dir)
    {
        var b = BoundsOf(n);
        var off = 22 / Zoom;
        var cy = n.Element.Kind == ElementKind.Lifeline ? b.Y + Notation.LifelineHeader / 2 : b.Center.Y;
        return dir switch
        {
            0 => new Pt(b.Center.X, b.Y - off),
            1 => new Pt(b.Right + off, cy),
            2 => new Pt(b.Center.X, b.Bottom + off),
            _ => new Pt(b.X - off, cy),
        };
    }

    public bool ShowsQuickArrows(DiagramNode n) =>
        !_ws.ReadOnly && Tool == null && Current is Gesture.None
        && !Notation.IsBorderShape(n.Element) && n.Element.Kind != ElementKind.Comment
        && Notation.ShapeOf(n.Element) is not (NodeShape.Swimlane or NodeShape.Pool or NodeShape.Fragment)
        && Successor(n.Element) is { } s && DefaultLink(n.Element.Kind, s.Kind) != null;

    private int HitArrow(Pt p)
    {
        if (Hover == null || !Diagram.Nodes.Contains(Hover) || !ShowsQuickArrows(Hover)) return -1;
        foreach (var d in ArrowDirs(Hover))
            if (Notation.Dist(p, ArrowPoint(Hover, d)) <= 12 / Zoom) return d;
        return -1;
    }

    /// <summary>What a quick-connect click creates next to <paramref name="e"/>: usually "another one of these".</summary>
    public PaletteItem? Successor(Element e)
    {
        var sub = e.Get(FieldKeys.Subtype);
        return e.Kind switch
        {
            ElementKind.ActivityFinalNode or ElementKind.FinalState => null,
            ElementKind.InitialNode or ElementKind.DecisionNode or ElementKind.ForkNode => new PaletteItem(ElementKind.Action),
            ElementKind.InitialState => new PaletteItem(ElementKind.State),
            ElementKind.Actor when Diagram.DiagramType == DiagramType.UseCase => new PaletteItem(ElementKind.UseCase),
            ElementKind.BpmnNode when sub == "endEvent" => null,
            ElementKind.BpmnNode when Subtypes.BpmnShape(sub) is NodeShape.Event or NodeShape.Gateway or NodeShape.DataObject => new PaletteItem(ElementKind.BpmnNode, "task"),
            ElementKind.Shape when sub == "text" => new PaletteItem(ElementKind.Shape, "rectangle"),
            _ when UafViewpoints.IsUaf(Diagram.DiagramType) && e.Stereotypes.FirstOrDefault() is { } st => new PaletteItem(e.Kind, Stereotype: st),
            _ => new PaletteItem(e.Kind, string.IsNullOrEmpty(sub) ? null : sub),
        };
    }

    /// <summary>The connector a quick-connect gesture draws between two kinds: the first valid one in this diagram's palette.</summary>
    public PaletteItem? DefaultLink(ElementKind source, ElementKind target)
    {
        if (source is ElementKind.Shape or ElementKind.Comment || target is ElementKind.Shape or ElementKind.Comment) return new PaletteItem(ElementKind.Line, "arrow");
        foreach (var l in Metamodel.PaletteLinks(Diagram.DiagramType))
            if (l.Kind != ElementKind.Allocate && Metamodel.CheckEnds(l.Kind, source, target) == null) return l;
        return null;
    }

    /// <summary>Click on a hover arrow: add a connected copy on that side, or link to the shape already there.</summary>
    private void QuickClone(DiagramNode from, int dir)
    {
        if (Successor(from.Element) is not { } next) return;
        var b = BoundsOf(from);
        var (w, h) = Notation.DefaultSizeOf(next.Kind, next.Subtype);
        if (next.Kind == from.Element.Kind && next.Subtype == from.Element.Get(FieldKeys.Subtype) && next.Kind != ElementKind.Lifeline) (w, h) = (b.Width, b.Height);
        const double gap = 70;
        var (ux, uy) = dir switch { 0 => (0.0, -1.0), 1 => (1.0, 0.0), 2 => (0.0, 1.0), _ => (-1.0, 0.0) };
        // Centre of the new shape, beside the source in the arrow's direction.
        var cx = b.Center.X + ux * (b.Width / 2 + gap + w / 2);
        var cy = (from.Element.Kind == ElementKind.Lifeline ? b.Y + h / 2 : b.Center.Y) + uy * (b.Height / 2 + gap + h / 2);
        var target = new Bounds(cx - w / 2, cy - h / 2, w, h);

        // Something already sits there: just connect to it (draw.io behaviour).
        var occupant = Diagram.Nodes.FirstOrDefault(n => !ReferenceEquals(n, from) && !Notation.IsBorderShape(n.Element)
            && Notation.ShapeOf(n.Element) is not (NodeShape.Swimlane or NodeShape.Pool or NodeShape.Fragment) && BoundsOf(n).Intersects(target));
        try
        {
            if (occupant != null && DefaultLink(from.Element.Kind, occupant.Element.Kind) is { } l0)
            {
                Connect(l0.Kind, l0.Subtype, from, occupant, stereotype: l0.Stereotype);
                return;
            }
            using (Model.History.Transaction($"Add connected {next.Title}"))
            {
                // CreateNode positions by (centre x, top + h/3); convert from the centre.
                var node = CreateNode(next.Kind, next.Subtype, new Pt(Snap(cx), Snap(cy - h / 2) + h / 3), size: (w, h), stereotype: next.Stereotype);
                if (node == null) return;
                if (DefaultLink(from.Element.Kind, node.Element.Kind) is { } link)
                {
                    if (from.Element.Kind == ElementKind.Lifeline) _pressModel = new Pt(_pressModel.X, b.Y + Notation.LifelineHeader + 40);
                    Connect(link.Kind, link.Subtype, from, node, select: false, stereotype: link.Stereotype);
                }
                _selNodes.Clear();
                SelectedEdge = null;
                _selNodes.Add(node);
                Hover = node;
            }
            Notify();
        }
        catch (InvalidOperationException ex) { _ws.SetStatus(ex.Message); }
    }

    /// <summary>Drag from a hover arrow: connect to the shape released on, or create a new one there.</summary>
    private void QuickDrop(DiagramNode from, Pt at)
    {
        var to = HitNode(at, from);
        try
        {
            if (to != null)
            {
                var link = DefaultLink(from.Element.Kind, to.Element.Kind);
                if (link == null) { _ws.SetStatus($"Nothing can connect {Metamodel.DisplayName(from.Element.Kind)} to {Metamodel.DisplayName(to.Element.Kind)} here"); return; }
                Connect(link.Kind, link.Subtype, from, to, stereotype: link.Stereotype);
                return;
            }
            if (Successor(from.Element) is not { } next) return;
            using (Model.History.Transaction($"Add connected {next.Title}"))
            {
                var node = CreateNode(next.Kind, next.Subtype, at, stereotype: next.Stereotype);
                if (node == null) return;
                if (DefaultLink(from.Element.Kind, node.Element.Kind) is { } link) Connect(link.Kind, link.Subtype, from, node, select: false, stereotype: link.Stereotype);
                _selNodes.Clear();
                SelectedEdge = null;
                _selNodes.Add(node);
            }
            Notify();
        }
        catch (InvalidOperationException ex) { _ws.SetStatus(ex.Message); }
    }

    private int HitWaypoint(Pt p)
    {
        if (SelectedEdge == null) return -1;
        var hs = HandleSize / Zoom;
        for (var i = 0; i < SelectedEdge.Waypoints.Count; i++)
            if (Notation.Dist(p, HandlePoint(SelectedEdge, SelectedEdge.Waypoints[i])) <= hs) return i;
        return -1;
    }

    /// <summary>Where a waypoint's handle is drawn. Messages only use the Y (time), so the handle sits mid-arrow.</summary>
    public Pt HandlePoint(DiagramEdge e, Pt wp)
    {
        if (e.Relationship.Kind != ElementKind.Message) return wp;
        var sx = BoundsOf(e.Source).Center.X;
        var tx = BoundsOf(e.Target).Center.X;
        return new Pt(Math.Abs(sx - tx) < 1 ? sx + 44 : (sx + tx) / 2, wp.Y);
    }

    // ---------------------------------------------------------------- pointer input (screen coordinates relative to the canvas)

    /// <returns>True if a context menu should open.</returns>
    public bool PointerDown(double sx, double sy, int button, Mods mods, int clicks)
    {
        _pressScreen = new Pt(sx, sy);
        _pressModel = ToModel(sx, sy);
        _selAtPress = (_selNodes.ToList(), _selEdges.ToList());
        CurrentModel = _pressModel;
        Editing = null;
        EditingEdge = null;
        EditSeed = null;
        QuickAddRequest = null;
        _guides.Clear();

        if (button == 1 || (button == 0 && (mods.HasFlag(Mods.Alt) || SpaceHeld)))
        {
            Current = Gesture.Pan;
            _panAtPress = (PanX, PanY);
            return false;
        }
        if (button == 2)
        {
            var n = HitNode(_pressModel);
            var ed = n == null ? HitEdge(_pressModel) : null;
            // Right-clicking something already selected keeps the whole selection, so the menu acts on all of it.
            if (n != null && !_selNodes.Contains(n)) { ClearSelection(); _selNodes.Add(n); }
            if (ed != null && !_selEdges.Contains(ed)) { ClearSelection(); _selEdges.Add(ed); }
            if (n == null && ed == null) ClearSelection();
            Notify();
            return true;
        }
        if (button != 0) return false;

        if (_ws.ReadOnly) { Tool = null; }
        if (Tool is { } tool)
        {
            if (Metamodel.IsRelationship(tool))
            {
                var from = HitNode(_pressModel);
                if (from == null) { _ws.SetStatus($"Start a {Metamodel.DisplayName(tool)} on a shape"); return false; }
                ConnectFrom = from;
                Current = Gesture.Connect;
            }
            else
            {
                CreateNodeAt(tool, _pressModel);
                if (!mods.HasFlag(Mods.Shift)) { Tool = null; ToolSubtype = null; ToolStereotype = null; }
            }
            return false;
        }

        // Hyperlinks: the link icon, or Ctrl/⌘-click anywhere on a linked shape, follows the link.
        if (HitLinkBadge(_pressModel) is { } badgeNode)
        {
            LinkRequest = DiagramExtras.LinkOf(badgeNode);
            return false;
        }
        if ((mods.HasFlag(Mods.Ctrl) || mods.HasFlag(Mods.Meta)) && clicks < 2
            && VisibleNodes.LastOrDefault(n => DiagramExtras.LinkOf(n) != null && BoundsOf(n).Contains(_pressModel)) is { } linked
            && ReferenceEquals(HitNode(_pressModel) ?? linked, linked))
        {
            LinkRequest = DiagramExtras.LinkOf(linked);
            return false;
        }

        if (HitArrow(_pressModel) is >= 0 and var dir)
        {
            Current = Gesture.QuickConnect;
            ConnectFrom = Hover;
            _quickDir = dir;
            return false;
        }

        if (HitBadge(_pressModel) is { } badged)
        {
            DrillRequest = badged.Element;
            return false;
        }

        if (clicks >= 2)
        {
            var dn = HitNode(_pressModel);
            if (dn != null && HasSubDiagram(dn.Element)) { DrillRequest = dn.Element; return false; }
            if (dn != null && !_ws.ReadOnly) { _selNodes.Clear(); _selNodes.Add(dn); Editing = dn; Notify(); return false; }
            var de = dn == null ? HitEdge(_pressModel) : null;
            if (de != null && !_ws.ReadOnly) { _selNodes.Clear(); SelectedEdge = de; EditingEdge = de; Notify(); return false; }
            if (dn == null && de == null && !_ws.ReadOnly) { QuickAddRequest = new Pt(sx, sy); return false; }
        }

        var wp = HitWaypoint(_pressModel);
        if (wp >= 0)
        {
            Current = Gesture.Waypoint;
            _waypointIndex = wp;
            WaypointPreview = SelectedEdge!.Waypoints.ToList();
            return false;
        }

        if (HitResizeHandle(_pressModel) is >= 0 and var handle)
        {
            _handle = handle;
            Current = Gesture.Resize;
            _origin = _selNodes.ToDictionary(n => n, BoundsOf);
            return false;
        }

        var additive = mods.HasFlag(Mods.Shift) || mods.HasFlag(Mods.Ctrl) || mods.HasFlag(Mods.Meta);
        var node = HitNode(_pressModel);
        if (node != null)
        {
            if (additive) { if (!_selNodes.Remove(node)) _selNodes.Add(node); }
            else if (!_selNodes.Contains(node)) { ClearSelection(); _selNodes.Add(node); }
            Current = Gesture.Move;
            _origin = _selNodes.SelectMany(n => Diagram.DescendantsOf(n).Prepend(n)).Distinct().ToDictionary(n => n, n => n.Bounds);
            Notify();
            return false;
        }

        var edge = HitEdge(_pressModel);
        if (edge != null && additive)
        {
            if (!_selEdges.Remove(edge)) _selEdges.Add(edge);
            Notify();
            return false;
        }
        if (edge != null)
        {
            _selNodes.Clear();
            SelectedEdge = edge;
            // Dragging a connector bends it: a new waypoint is inserted where it was grabbed.
            if (!_ws.ReadOnly) { Current = Gesture.EdgeDrag; _dragEdge = edge; }
            Notify();
            return false;
        }

        if (!additive) { _selNodes.Clear(); SelectedEdge = null; }
        Current = Gesture.Marquee;
        Notify();
        return false;
    }

    /// <returns>True if the view needs re-rendering.</returns>
    /// <param name="mods">Modifier keys held: Shift frees an image's aspect ratio while resizing (and locks it for other shapes).</param>
    public bool PointerMove(double sx, double sy, Mods mods = Mods.None)
    {
        CurrentModel = ToModel(sx, sy);
        switch (Current)
        {
            case Gesture.None:
                return UpdateHover();
            case Gesture.QuickConnect:
                return true;
            case Gesture.EdgeDrag when _dragEdge != null:
            {
                if (Notation.Dist(new Pt(sx, sy), _pressScreen) < 4) return false;
                var wps = _dragEdge.Waypoints.ToList();
                if (_dragEdge.Relationship.Kind == ElementKind.Message && wps.Count > 0) _waypointIndex = 0;
                else
                {
                    // With manual bends, work on the polyline through them (a curved connector is drawn smoothed).
                    var pts = wps.Count > 0 ? Notation.Route(_dragEdge, BoundsOf) : Notation.RouteIn(Diagram, _dragEdge, BoundsOf);
                    // Keep the route's existing bends so the grabbed segment is the one that moves (a curve just bends where grabbed).
                    if (wps.Count == 0 && pts.Count > 2 && EdgeRouter.StyleOf(Diagram, _dragEdge) != EdgeRouting.Curved) wps = pts.Skip(1).Take(pts.Count - 2).ToList();
                    if (wps.Count == 0) pts = [pts[0], pts[^1]];
                    var all = new List<Pt> { pts[0] };
                    all.AddRange(wps);
                    all.Add(pts[^1]);
                    var best = 0; var bestD = double.MaxValue;
                    for (var i = 1; i < all.Count; i++)
                    {
                        var d = Notation.DistanceToSegment(_pressModel, all[i - 1], all[i]);
                        if (d < bestD) { bestD = d; best = i - 1; }
                    }
                    wps.Insert(best, _pressModel);
                    _waypointIndex = best;
                }
                wps[_waypointIndex] = new Pt(Snap(CurrentModel.X), Snap(CurrentModel.Y));
                WaypointPreview = wps;
                Current = Gesture.Waypoint;
                return true;
            }
            case Gesture.Pan:
                PanX = _panAtPress.X + sx - _pressScreen.X;
                PanY = _panAtPress.Y + sy - _pressScreen.Y;
                return true;
            case Gesture.Move:
            {
                var dx = CurrentModel.X - _pressModel.X;
                var dy = CurrentModel.Y - _pressModel.Y;
                if (Math.Abs(dx) < 3 / Zoom && Math.Abs(dy) < 3 / Zoom && _preview.Count == 0) return false;
                var (gx, gy) = SmartGuides(dx, dy);
                foreach (var (n, b) in _origin)
                {
                    var moved = b with { X = gx is { } ox ? b.X + ox : Snap(b.X + dx), Y = gy is { } oy ? b.Y + oy : Snap(b.Y + dy) };
                    // A port/pin dragged on its own slides along its container's border.
                    if (Notation.IsBorderShape(n.Element) && Diagram.ParentOf(n) is { } parent && !_origin.ContainsKey(parent))
                        moved = Notation.SnapToBorder(moved, BoundsOf(parent));
                    _preview[n] = moved;
                }
                GrowContainers(_origin.Keys);
                return true;
            }
            case Gesture.Resize:
            {
                var (n, b) = _origin.First();
                var small = Notation.ShapeOf(n.Element) is NodeShape.FilledCircle or NodeShape.BullsEye or NodeShape.SmallSquare;
                double minW = small ? 12 : 30, minH = small ? 12 : 20;
                double x0 = b.X, y0 = b.Y, x1 = b.Right, y1 = b.Bottom;
                var px = Snap(CurrentModel.X); var py = Snap(CurrentModel.Y);
                if (_handle is 0 or 6 or 7) x0 = Math.Min(px, x1 - minW);
                if (_handle is 2 or 3 or 4) x1 = Math.Max(px, x0 + minW);
                if (_handle is 0 or 1 or 2) y0 = Math.Min(py, y1 - minH);
                if (_handle is 4 or 5 or 6) y1 = Math.Max(py, y0 + minH);
                // Pictures keep their proportions (Shift resizes freely); other shapes keep them while Shift is held.
                if (DiagramExtras.IsImage(n) != mods.HasFlag(Mods.Shift) && b.Width > 0 && b.Height > 0)
                    (x0, y0, x1, y1) = KeepAspect(b, _handle, x0, y0, x1, y1);
                _preview[n] = new Bounds(x0, y0, x1 - x0, y1 - y0);
                GrowContainers([n]);
                return true;
            }
            case Gesture.Waypoint when WaypointPreview is List<Pt> wps:
                wps[_waypointIndex] = new Pt(Snap(CurrentModel.X), Snap(CurrentModel.Y));
                return true;
            case Gesture.Connect:
            case Gesture.Marquee:
                return true;
            default:
                return false;
        }
    }

    public void PointerUp(Mods mods)
    {
        var g = Current;
        Current = Gesture.None;
        _guides.Clear();
        try
        {
            switch (g)
            {
                case Gesture.QuickConnect when ConnectFrom != null:
                    if (Notation.Dist(ToScreen(CurrentModel), _pressScreen) < 5) QuickClone(ConnectFrom, _quickDir);
                    else QuickDrop(ConnectFrom, CurrentModel);
                    break;
                case Gesture.Move or Gesture.Resize when _preview.Count > 0:
                {
                    var moved = new Dictionary<DiagramNode, Bounds>(_preview);
                    _preview.Clear();
                    using (Model.History.Transaction(g == Gesture.Move ? "Move" : "Resize"))
                    {
                        if (g == Gesture.Move)
                        {
                            var first = moved.First(kv => _origin.ContainsKey(kv.Key));
                            var dx = first.Value.X - first.Key.Bounds.X;
                            var dy = first.Value.Y - first.Key.Bounds.Y;
                            foreach (var edge in Diagram.Edges.Where(x => x.Waypoints.Count > 0 && _origin.ContainsKey(x.Source) && _origin.ContainsKey(x.Target)).ToList())
                                Model.SetWaypoints(Diagram, edge, edge.Waypoints.Select(p => new Pt(p.X + dx, p.Y + dy)).ToList());
                        }
                        Model.SetBounds(Diagram, moved, g == Gesture.Move ? "Move" : "Resize");
                    }
                    break;
                }
                case Gesture.Waypoint when SelectedEdge != null && WaypointPreview != null:
                    Model.SetWaypoints(Diagram, SelectedEdge, WaypointPreview);
                    break;
                case Gesture.Connect when ConnectFrom != null && Tool is { } kind:
                {
                    var to = HitNode(CurrentModel);
                    if (to != null) Connect(kind, ToolSubtype, ConnectFrom, to, stereotype: ToolStereotype);
                    if (!mods.HasFlag(Mods.Shift)) { Tool = null; ToolSubtype = null; ToolStereotype = null; }
                    break;
                }
                case Gesture.Marquee:
                {
                    var r = Bounds.FromPoints(_pressModel, CurrentModel);
                    if (r.Width > 2 && r.Height > 2)
                        foreach (var n in Diagram.Nodes.Where(Pickable))
                        {
                            var b = BoundsOf(n);
                            if (r.Contains(new Pt(b.X, b.Y)) && r.Contains(new Pt(b.Right, b.Bottom))) _selNodes.Add(n);
                        }
                    // Connectors whose whole path lies inside the box.
                    if (r.Width > 2 && r.Height > 2)
                    {
                        var routes = EdgeRouter.RouteAll(DiagramExtras.VisibleView(Diagram), BoundsOf);
                        foreach (var e in Diagram.Edges.Where(Pickable))
                            if (routes.TryGetValue(e, out var pts) && pts.Count > 0 && pts.All(r.Contains)) _selEdges.Add(e);
                    }
                    Notify();
                    break;
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            _ws.SetStatus(ex.Message);
        }
        finally
        {
            _preview.Clear();
            ConnectFrom = null;
            WaypointPreview = null;
            _dragEdge = null;
            _quickDir = -1;
            _handle = -1;
        }
    }

    /// <summary>Adjusts a resize rectangle to the original's aspect ratio, anchored on the side or corner opposite the handle.</summary>
    public static (double X0, double Y0, double X1, double Y1) KeepAspect(Bounds original, int handle, double x0, double y0, double x1, double y1)
    {
        var ratio = original.Width / original.Height;
        double w = x1 - x0, h = y1 - y0;
        if (handle is 0 or 2 or 4 or 6) { if (w / h > ratio) w = h * ratio; else h = w / ratio; }
        else if (handle is 1 or 5) w = h * ratio;   // top / bottom edge: height leads
        else h = w / ratio;                         // left / right edge: width leads
        if (handle is 0 or 6 or 7) x0 = x1 - w;
        else if (handle is 1 or 5) x0 = original.Center.X - w / 2;
        x1 = x0 + w;
        if (handle is 0 or 1 or 2) y0 = y1 - h;
        else if (handle is 3 or 7) y0 = original.Center.Y - h / 2;
        y1 = y0 + h;
        return (x0, y0, x1, y1);
    }

    /// <summary>Tracks the shape under the pointer. Returns true when the hover adornments change.</summary>
    private bool UpdateHover()
    {
        var p = CurrentModel;
        var h = HitNode(p);
        // Stay on the current shape while the pointer is in the margin where its arrows are.
        if (h == null && Hover != null && Diagram.Nodes.Contains(Hover) && ShowsQuickArrows(Hover))
        {
            var b = BoundsOf(Hover);
            var reach = 36 / Zoom;
            if (new Bounds(b.X - reach, b.Y - reach, b.Width + 2 * reach, b.Height + 2 * reach).Contains(p)) h = Hover;
        }
        var prevArrow = HoverArrow;
        var changed = !ReferenceEquals(h, Hover);
        Hover = h;
        HoverArrow = HitArrow(p);
        return changed || prevArrow != HoverArrow;
    }

    /// <summary>
    /// Alignment guides for the dragged selection: snaps its edges or centre to other shapes' edges or centres
    /// within a few pixels. Returns the corrected offsets (null = no guide on that axis, use the grid).
    /// </summary>
    private (double? Dx, double? Dy) SmartGuides(double dx, double dy)
    {
        _guides.Clear();
        var moving = _selNodes.Where(_origin.ContainsKey).ToList();
        if (moving.Count == 0 || Diagram.Nodes.Count > 600) return (null, null);
        var sel = moving.Select(n => _origin[n]).ToList();
        var box = new Bounds(sel.Min(b => b.X) + dx, sel.Min(b => b.Y) + dy, 0, 0);
        box = box with { Width = sel.Max(b => b.Right) + dx - box.X, Height = sel.Max(b => b.Bottom) + dy - box.Y };
        var others = Diagram.Nodes.Where(n => !_origin.ContainsKey(n) && !Notation.IsBorderShape(n.Element)).Select(BoundsOf).ToList();
        var tol = 6 / Zoom;

        double? bestX = null, bestY = null;
        double bx = tol, by = tol;
        double[] xs = [box.X, box.Center.X, box.Right], ys = [box.Y, box.Center.Y, box.Bottom];
        foreach (var o in others)
        {
            foreach (var ox in new[] { o.X, o.Center.X, o.Right })
                foreach (var x in xs)
                    if (Math.Abs(ox - x) < bx) { bx = Math.Abs(ox - x); bestX = ox - x; }
            foreach (var oy in new[] { o.Y, o.Center.Y, o.Bottom })
                foreach (var y in ys)
                    if (Math.Abs(oy - y) < by) { by = Math.Abs(oy - y); bestY = oy - y; }
        }
        var fx = bestX is { } cx ? box with { X = box.X + cx } : box;
        fx = bestY is { } cy ? fx with { Y = fx.Y + cy } : fx;
        foreach (var o in others)
        {
            if (bestX != null)
                foreach (var ox in new[] { o.X, o.Center.X, o.Right })
                    foreach (var x in new[] { fx.X, fx.Center.X, fx.Right })
                        if (Math.Abs(ox - x) < 0.5) _guides.Add(new Guide(true, ox, Math.Min(o.Y, fx.Y) - 12, Math.Max(o.Bottom, fx.Bottom) + 12));
            if (bestY != null)
                foreach (var oy in new[] { o.Y, o.Center.Y, o.Bottom })
                    foreach (var y in new[] { fx.Y, fx.Center.Y, fx.Bottom })
                        if (Math.Abs(oy - y) < 0.5) _guides.Add(new Guide(false, oy, Math.Min(o.X, fx.X) - 12, Math.Max(o.Right, fx.Right) + 12));
        }
        // Offsets relative to each node's origin: the grid offset is replaced by the exact guided offset.
        return (bestX is { } gx ? dx + gx : null, bestY is { } gy ? dy + gy : null);
    }

    public void Wheel(double sx, double sy, double deltaX, double deltaY, Mods mods)
    {
        if (mods.HasFlag(Mods.Ctrl) || mods.HasFlag(Mods.Meta)) SetZoom(Zoom * (deltaY < 0 ? 1.1 : 1 / 1.1), sx, sy);
        else if (mods.HasFlag(Mods.Shift)) PanBy(-deltaY, 0);
        else PanBy(-deltaX, -deltaY);
    }

    /// <summary>Keyboard handling. Returns true if handled.</summary>
    public bool Key(string key, Mods mods)
    {
        var cmd = mods.HasFlag(Mods.Meta) || mods.HasFlag(Mods.Ctrl);
        switch (key)
        {
            case "Delete":
            case "Backspace" when cmd:
                DeleteSelectionFromModel();
                return true;
            case "Backspace":
                RemoveSelectionFromDiagram();
                return true;
            case "Escape":
                Tool = null; ToolSubtype = null; ToolStereotype = null; Editing = null; EditingEdge = null; QuickAddRequest = null; _selNodes.Clear(); SelectedEdge = null; Notify();
                return true;
            case "a" or "A" when cmd:
                SelectAll();
                return true;
            case "d" or "D" when cmd:
                DuplicateSelection();
                return true;
            case "c" or "C" when cmd:
                CopySelection();
                return true;
            case "v" or "V" when cmd:
                Paste();
                return true;
            case "F2" when _selNodes.Count == 1 && !_ws.ReadOnly:
                Editing = _selNodes.First();
                return true;
            case "F2" when SelectedEdge != null && !_ws.ReadOnly:
                EditingEdge = SelectedEdge;
                return true;
            case "Enter" when _selNodes.Count == 1 && !_ws.ReadOnly:
                Editing = _selNodes.First();
                return true;
            case { Length: 1 } when (Editing != null || EditingEdge != null) && EditSeed != null && !cmd:
                EditSeed += key;
                return true;
            // draw.io: start typing on a selected shape to replace its label.
            case { Length: 1 } when !cmd && !mods.HasFlag(Mods.Alt) && key != " " && !_ws.ReadOnly && (_selNodes.Count == 1 || SelectedEdge != null && _selNodes.Count == 0):
                if (_selNodes.Count == 1) Editing = _selNodes.First(); else EditingEdge = SelectedEdge;
                EditSeed = key;
                return true;
            case "ArrowLeft" or "ArrowRight" or "ArrowUp" or "ArrowDown" when _selNodes.Count > 0:
            {
                var step = mods.HasFlag(Mods.Shift) ? Grid * 5 : Grid;
                var (dx, dy) = key switch { "ArrowLeft" => (-step, 0.0), "ArrowRight" => (step, 0.0), "ArrowUp" => (0.0, -step), _ => (0.0, step) };
                Model.SetBounds(Diagram, _selNodes.ToDictionary(n => n, n => n.Bounds.Offset(dx, dy)), "Nudge");
                return true;
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- editing operations

    public void DeleteSelectionFromModel()
    {
        var els = SelectedElements.ToList();
        if (els.Count == 0) return;
        if (_ws.Preferences.ConfirmDeleteFromModel && _ws.Confirm is { } confirm)
        {
            _ = ConfirmThenDeleteAsync(confirm, els);
            return;
        }
        _selNodes.Clear();
        SelectedEdge = null;
        Model.Delete(els);
        Notify();
    }

    private async Task ConfirmThenDeleteAsync(Func<string, string, Task<bool>> confirm, List<Element> els)
    {
        var what = els.Count == 1 ? els[0].DisplayName : $"{els.Count} elements";
        if (!await confirm("Delete from model", $"Delete {what} from the model? It disappears from every diagram. You can undo this.")) return;
        els = els.Where(Model.Contains).ToList();
        if (els.Count == 0) return;
        _selNodes.Clear();
        SelectedEdge = null;
        Model.Delete(els);
        Notify();
    }

    public void RemoveSelectionFromDiagram()
    {
        if (SelectionCount == 0) return;
        var nodes = _selNodes.ToList();
        var edges = _selEdges.ToList();
        ClearSelection();
        Model.RemoveFromDiagram(Diagram, nodes, edges.Count > 0 ? edges : null);
        Notify();
    }

    public void Arrange()
    {
        using (Model.History.Transaction("Arrange"))
        {
            Model.SetBounds(Diagram, AutoLayout.Layered(Diagram), "Arrange");
            foreach (var e in Diagram.Edges.Where(e => e.Waypoints.Count > 0).ToList()) Model.SetWaypoints(Diagram, e, []);
        }
        ZoomToFit();
    }

    // ---------------------------------------------------------------- duplicate / copy / paste

    public void CopySelection()
    {
        _ws.Clipboard.Clear();
        _ws.Clipboard.AddRange(_selNodes.Select(n => n.Element));
        if (_ws.Clipboard.Count > 0) _ws.SetStatus($"Copied {_ws.Clipboard.Count} element{(_ws.Clipboard.Count == 1 ? "" : "s")}");
    }

    public void DuplicateSelection() => DuplicateOntoDiagram(_selNodes.Select(n => n.Element).ToList(), 30);

    public void Paste()
    {
        var items = _ws.Clipboard.Where(Model.Contains).ToList();
        if (items.Count == 0) { _ws.SetStatus("Nothing to paste"); return; }
        DuplicateOntoDiagram(items, 40);
    }

    /// <summary>Deep-copies elements into this diagram's context and shows the copies offset from the originals.</summary>
    private void DuplicateOntoDiagram(IReadOnlyList<Element> elements, double offset)
    {
        if (elements.Count == 0) return;
        try
        {
            Dictionary<Element, Element> map;
            using (Model.History.Transaction(elements.Count == 1 ? $"Duplicate {elements[0].DisplayName}" : $"Duplicate {elements.Count} elements"))
            {
                var context = Model.DefaultOwnerFor(Diagram.Owner ?? Model.Root, elements[0].Kind);
                map = Model.Duplicate(elements, context);
                _selNodes.Clear();
                SelectedEdge = null;
                foreach (var original in elements)
                {
                    if (!map.TryGetValue(original, out var copy)) continue;
                    var src = Diagram.NodeFor(original);
                    var b = src != null ? src.Bounds.Offset(offset, offset) : (Bounds?)null;
                    var node = b is { } bb ? Model.AddNode(Diagram, copy, bb) : Show(copy);
                    _selNodes.Add(node);
                }
                // Keep message timing / bend points of copied paths.
                foreach (var e in Diagram.Edges.ToList())
                    if (map.TryGetValue(e.Relationship.Source, out _) && map.TryGetValue(e.Relationship.Target, out _) &&
                        Model.RelationshipsOf(map[e.Relationship.Source]).FirstOrDefault(r => r.Kind == e.Relationship.Kind && ReferenceEquals(r.Target, map[e.Relationship.Target])) is { } nr &&
                        Diagram.EdgeFor(nr) is { } ne && e.Waypoints.Count > 0)
                        Model.SetWaypoints(Diagram, ne, e.Waypoints.Select(p => new Pt(p.X + offset, p.Y + offset)).ToList());
            }
            Notify();
            _ws.SetStatus($"Duplicated {map.Count} element{(map.Count == 1 ? "" : "s")}");
        }
        catch (InvalidOperationException ex) { _ws.SetStatus(ex.Message); }
    }

    // ---------------------------------------------------------------- format (draw.io-style format bar)

    private static Dictionary<string, string>? _formatClipboard;

    /// <summary>Style value shared by the selection, or null when unset or mixed.</summary>
    public string? SelectionStyle(string key)
    {
        var values = StyleValues(key);
        return values.Count == 1 ? values[0] : null;
    }

    /// <summary>The distinct values of a style key across the selected items it applies to (null = default).</summary>
    public IReadOnlyList<string?> StyleValues(string key) =>
        // Connectors don't count for shape-only formats (fill), so a selection with lines isn't "Mixed" because of them.
        _selNodes.Select(n => n.Style.GetValueOrDefault(key))
            .Concat(_selEdges.Where(_ => EdgeStyleKeys.Contains(key) || !NodeStyleKeys.Contains(key)).Select(e => e.Style.GetValueOrDefault(key)))
            .Distinct().ToList();

    /// <summary>Do the selected items disagree on a style (shown as "Mixed")?</summary>
    public bool IsMixedStyle(string key) => StyleValues(key).Count > 1;

    /// <summary>Format keys shapes take: fill, line colour, text colour, line width, line pattern, font size.</summary>
    public static readonly IReadOnlySet<string> NodeStyleKeys = new HashSet<string> { "fill", "stroke", "text", "width", "dash", "fontSize" };

    /// <summary>Format keys connectors take: line colour, width and pattern, and their label's colour and size.</summary>
    public static readonly IReadOnlySet<string> EdgeStyleKeys = new HashSet<string> { "stroke", "width", "dash", "text", "fontSize" };

    /// <summary>Sets a style key on every selected shape (fill / stroke / text) and connector (stroke / width).</summary>
    public void SetSelectionStyle(string key, string? value)
    {
        if (_ws.ReadOnly) return;
        using (Model.History.Transaction("Format"))
        {
            if (NodeStyleKeys.Contains(key))
                foreach (var n in _selNodes)
                {
                    Model.SetNodeStyle(Diagram, n, key, value);
                    if (key == "fill") AutoTextColor(n, value);
                    if (key == "text") Model.SetNodeStyle(Diagram, n, "textAuto", null);
                }
            if (EdgeStyleKeys.Contains(key))
                foreach (var e in _selEdges) Model.SetEdgeStyle(Diagram, e, key, value);
        }
    }

    /// <summary>Keeps labels readable on a custom fill: dark text on light fills, light text on dark ones (unless the user chose a text colour).</summary>
    private void AutoTextColor(DiagramNode n, string? fill)
    {
        var auto = n.Style.ContainsKey("textAuto");
        if (n.Style.ContainsKey("text") && !auto) return;
        string? text = null;
        if (fill is { Length: 7 } && fill[0] == '#' && int.TryParse(fill[1..], System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            var lum = (0.299 * (rgb >> 16 & 255) + 0.587 * (rgb >> 8 & 255) + 0.114 * (rgb & 255)) / 255;
            text = lum > 0.6 ? "#1e293b" : lum < 0.4 ? "#f8fafc" : null;
        }
        Model.SetNodeStyle(Diagram, n, "text", text);
        Model.SetNodeStyle(Diagram, n, "textAuto", text != null ? "1" : null);
    }

    /// <summary>Changes the outline of selected free shapes, or the style of selected plain lines.</summary>
    public void SetSelectionSubtype(ElementKind kind, string subtype)
    {
        if (_ws.ReadOnly) return;
        using (Model.History.Transaction("Change style"))
        {
            foreach (var e in SelectedElements.Where(e => e.Kind == kind))
                Model.Set(e, FieldKeys.Subtype, subtype);
        }
    }

    // ---------------------------------------------------------------- hyperlinks & images

    /// <summary>The link shared by the selected shapes, or null.</summary>
    public string? SelectionLink() => _selNodes.Count > 0 ? SelectionStyle(DiagramExtras.LinkStyle) : null;

    /// <summary>Sets (or clears, with null/empty) the hyperlink of every selected shape.</summary>
    public void SetSelectionLink(string? link)
    {
        if (_ws.ReadOnly || _selNodes.Count == 0) return;
        try
        {
            Model.SetLink(Diagram, _selNodes.ToList(), link);
            _ws.SetStatus(string.IsNullOrWhiteSpace(link) ? "Link removed" : "Link set: Ctrl/⌘-click the shape or its link icon to follow it");
        }
        catch (InvalidOperationException ex) { _ws.SetStatus(ex.Message); }
    }

    /// <summary>
    /// Inserts a picture (a data URI already downscaled by the browser) at a screen point, or in a free spot near the
    /// middle of the view, at its natural size capped to 320 px. Returns the new shape, or null when it was refused.
    /// </summary>
    public DiagramNode? InsertImage(string dataUri, double naturalW, double naturalH, double? sx = null, double? sy = null, string? name = null)
    {
        if (_ws.ReadOnly) return null;
        try
        {
            var (w, h) = DiagramExtras.FitImageSize(naturalW, naturalH);
            Pt at;
            if (sx is { } x && sy is { } y)
            {
                var p = ToModel(x, y);
                at = new Pt(Snap(p.X - w / 2), Snap(p.Y - h / 2));
            }
            else
            {
                var vp = Viewport;
                at = new Pt(Snap(vp.X + vp.Width / 2 - w / 2), Snap(vp.Y + vp.Height / 3 - h / 2));
                while (Diagram.Nodes.Any(n => BoundsOf(n).X == at.X && BoundsOf(n).Y == at.Y)) at = new Pt(at.X + 20, at.Y + 20);
            }
            var owner = Model.DefaultOwnerFor(Diagram.Owner ?? Model.Root, ElementKind.Shape);
            DiagramNode node;
            using (Model.History.Transaction("Insert image"))
            {
                node = Model.AddImage(Diagram, owner, dataUri, new Bounds(at.X, at.Y, w, h), name);
                PutOnActiveLayer(node);
            }
            _selNodes.Clear();
            SelectedEdge = null;
            _selNodes.Add(node);
            Tool = null;
            ToolSubtype = null; ToolStereotype = null;
            EnsureVisible(BoundsOf(node));
            Notify();
            _ws.SetStatus("Image inserted: drag a corner to resize (Shift to stretch)");
            return node;
        }
        catch (InvalidOperationException ex)
        {
            _ws.SetStatus(ex.Message);
            return null;
        }
    }

    private void PutOnActiveLayer(DiagramNode node)
    {
        if (ActiveLayerId != DiagramExtras.DefaultLayerId && DiagramExtras.FindLayer(Diagram, ActiveLayerId) != null && node.ParentId == null)
            Model.SetNodeStyle(Diagram, node, DiagramExtras.LayerStyle, ActiveLayerId);
    }

    public bool CanPasteFormat => _formatClipboard != null;

    public void CopyFormat()
    {
        var src = (IReadOnlyDictionary<string, string>?)_selNodes.FirstOrDefault()?.Style ?? _selEdges.FirstOrDefault()?.Style;
        _formatClipboard = src?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new();
        _ws.SetStatus("Format copied: select shapes and choose Paste format");
    }

    public void PasteFormat()
    {
        if (_formatClipboard is not { } f || _ws.ReadOnly) return;
        using (Model.History.Transaction("Paste format"))
        {
            foreach (var key in NodeStyleKeys.Append("textAuto"))
                foreach (var n in _selNodes) Model.SetNodeStyle(Diagram, n, key, f.GetValueOrDefault(key));
            foreach (var key in EdgeStyleKeys)
                foreach (var e in _selEdges) Model.SetEdgeStyle(Diagram, e, key, f.GetValueOrDefault(key));
        }
    }

    public void ClearFormat()
    {
        using (Model.History.Transaction("Clear format"))
        {
            // Links and layers aren't "looks": keep them.
            static bool Look(string key) => key is not (DiagramExtras.LinkStyle or DiagramExtras.LayerStyle);
            foreach (var n in _selNodes) foreach (var key in n.Style.Keys.Where(Look).ToList()) Model.SetNodeStyle(Diagram, n, key, null);
            foreach (var e in _selEdges) foreach (var key in e.Style.Keys.Where(Look).ToList()) Model.SetEdgeStyle(Diagram, e, key, null);
        }
    }

    // ---------------------------------------------------------------- align & distribute

    public void Align(string mode)
    {
        var nodes = _selNodes.ToList();
        if (nodes.Count < 2) return;
        var bs = nodes.ToDictionary(n => n, n => n.Bounds);
        var target = new Dictionary<DiagramNode, Bounds>();
        switch (mode)
        {
            case "left": { var x = bs.Values.Min(b => b.X); foreach (var n in nodes) target[n] = bs[n] with { X = x }; break; }
            case "right": { var r = bs.Values.Max(b => b.Right); foreach (var n in nodes) target[n] = bs[n] with { X = r - bs[n].Width }; break; }
            case "center": { var c = bs.Values.Average(b => b.Center.X); foreach (var n in nodes) target[n] = bs[n] with { X = Snap(c - bs[n].Width / 2) }; break; }
            case "top": { var y = bs.Values.Min(b => b.Y); foreach (var n in nodes) target[n] = bs[n] with { Y = y }; break; }
            case "bottom": { var btm = bs.Values.Max(b => b.Bottom); foreach (var n in nodes) target[n] = bs[n] with { Y = btm - bs[n].Height }; break; }
            case "middle": { var c = bs.Values.Average(b => b.Center.Y); foreach (var n in nodes) target[n] = bs[n] with { Y = Snap(c - bs[n].Height / 2) }; break; }
            case "hdist" when nodes.Count >= 3:
            {
                var ordered = nodes.OrderBy(n => bs[n].X).ToList();
                var gap = (bs[ordered[^1]].Right - bs[ordered[0]].X - ordered.Sum(n => bs[n].Width)) / (ordered.Count - 1);
                var x = bs[ordered[0]].X;
                foreach (var n in ordered) { target[n] = bs[n] with { X = Snap(x) }; x += bs[n].Width + gap; }
                break;
            }
            case "vdist" when nodes.Count >= 3:
            {
                var ordered = nodes.OrderBy(n => bs[n].Y).ToList();
                var gap = (bs[ordered[^1]].Bottom - bs[ordered[0]].Y - ordered.Sum(n => bs[n].Height)) / (ordered.Count - 1);
                var y = bs[ordered[0]].Y;
                foreach (var n in ordered) { target[n] = bs[n] with { Y = Snap(y) }; y += bs[n].Height + gap; }
                break;
            }
            case "width": { var w = bs.Values.Max(b => b.Width); foreach (var n in nodes) target[n] = bs[n] with { Width = w }; break; }
            case "height": { var h = bs.Values.Max(b => b.Height); foreach (var n in nodes) target[n] = bs[n] with { Height = h }; break; }
        }
        using (Model.History.Transaction(mode switch { "hdist" or "vdist" => "Distribute", "width" or "height" => "Match size", _ => "Align" }))
        {
            Model.SetBounds(Diagram, target, "Align");
            ApplyContainerGrowth(target.Keys);
        }
    }

    // ---------------------------------------------------------------- containers

    /// <summary>While dragging or resizing: containers of the changed shapes grow (live) so their children stay inside.</summary>
    private void GrowContainers(IEnumerable<DiagramNode> changed)
    {
        var moving = Current == Gesture.Move ? _origin.Keys.ToHashSet() : null;
        foreach (var (n, b) in ContainerLayout.GrowToFit(Diagram, changed.ToList(), BoundsOf, moving)) _preview[n] = b;
    }

    /// <summary>After a committed change (align, create inside): grows the containers of <paramref name="changed"/>.</summary>
    private void ApplyContainerGrowth(IEnumerable<DiagramNode> changed)
    {
        var grown = ContainerLayout.GrowToFit(Diagram, changed.ToList(), n => n.Bounds);
        if (grown.Count > 0) Model.SetBounds(Diagram, grown, "Grow container");
    }

    /// <summary>Is any selected shape a container (it has shapes nested inside)?</summary>
    public bool SelectionHasContainer => _selNodes.Any(n => ContainerLayout.IsContainer(Diagram, n));

    /// <summary>Fits the selected containers tightly around their contents. One undo step.</summary>
    public void FitSelectionToContents()
    {
        var target = ContainerLayout.FitToContents(Diagram, n => n.Bounds, _selNodes.ToList());
        if (target.Count > 0) Model.SetBounds(Diagram, target, "Fit to contents");
        else _ws.SetStatus("Already fitted to its contents");
    }

    /// <summary>Fits every frame / container on the diagram to its contents, innermost first. One undo step.</summary>
    public void FitFrameToContents()
    {
        var target = ContainerLayout.FitToContents(Diagram, n => n.Bounds);
        if (target.Count > 0) Model.SetBounds(Diagram, target, "Fit frames to contents");
        _ws.SetStatus(target.Count > 0 ? $"Fitted {target.Keys.Count(n => ContainerLayout.IsContainer(Diagram, n))} container(s) to their contents" : "Every container already fits its contents");
    }

    private string UniqueStereotypedName(Element owner, string stereotype)
    {
        var baseName = stereotype.Replace(" ", "");
        var names = owner.OwnedElements.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = 1; ; i++)
            if (!names.Contains(baseName + i)) return baseName + i;
    }

    // ---------------------------------------------------------------- connector routing

    /// <summary>The routing of the selected connector (its own, else the diagram's), or null when it has manual bends.</summary>
    public EdgeRouting? SelectedRouting => SelectedEdge is { } e ? e.Waypoints.Count > 0 ? null : EdgeRouter.StyleOf(Diagram, e) : null;

    /// <summary>Sets the routing style of the selected connector; its manual bends are removed. One undo step.</summary>
    public void SetSelectedRouting(EdgeRouting style)
    {
        if (SelectedEdge is not { } e || _ws.ReadOnly) return;
        using (Model.History.Transaction($"Connector style: {EdgeRouter.Display(style)}"))
        {
            if (e.Waypoints.Count > 0) Model.SetWaypoints(Diagram, e, []);
            // Matching the diagram default needs no override.
            var value = style == EdgeRouter.Parse(Diagram.Get(EdgeRouter.DiagramKey)) ? null : EdgeRouter.Format(style) ?? "classic";
            Model.SetEdgeStyle(Diagram, e, EdgeRouter.EdgeKey, value);
        }
        Notify();
    }


    public void StraightenSelectedEdge()
    {
        if (SelectedEdge != null) Model.SetWaypoints(Diagram, SelectedEdge, []);
    }

    /// <summary>Shows an existing element on this diagram, at a model point or the next free spot.</summary>
    public DiagramNode Show(Element e, Pt? at = null)
    {
        var (w, h) = Notation.DefaultSizeOf(e);
        var p = at ?? NextFreeSpot(w, h);
        var node = Model.AddNode(Diagram, e, new Bounds(Snap(p.X), Snap(p.Y), w, h));
        _selNodes.Clear();
        SelectedEdge = null;
        _selNodes.Add(node);
        Notify();
        return node;
    }

    public void ShowAtScreen(Element e, double sx, double sy)
    {
        if (e is Relationship r)
        {
            if (Model.AddEdge(Diagram, r) == null) _ws.SetStatus("Show both ends of the relationship first");
            return;
        }
        if (e is Diagram || e.Kind == ElementKind.Model) return;
        var (w, h) = Notation.DefaultSizeOf(e);
        var p = ToModel(sx, sy);
        try { Show(e, new Pt(p.X - w / 2, p.Y - h / 3)); }
        catch (InvalidOperationException ex) { _ws.SetStatus(ex.Message); }
    }

    private Pt NextFreeSpot(double w, double h)
    {
        var origin = ToModel(40, 60);
        for (var y = origin.Y; y < origin.Y + 2000; y += 40)
            for (var x = origin.X; x < origin.X + 1200; x += 40)
            {
                var b = new Bounds(x - 20, y - 20, w + 40, h + 40);
                if (!Diagram.Nodes.Any(n => BoundsOf(n).Intersects(b))) return new Pt(x, y);
            }
        return origin;
    }

    public void DisplayRelated(DiagramNode n)
    {
        var related = Model.RelationshipsOf(n.Element).Select(r => ReferenceEquals(r.Source, n.Element) ? r.Target : r.Source)
            .Where(x => !Diagram.Shows(x)).Distinct().ToList();
        if (related.Count == 0) { _ws.SetStatus("All related elements are already shown"); return; }
        using (Model.History.Transaction("Display related elements"))
        {
            var i = 0;
            foreach (var x in related)
            {
                var angle = 2 * Math.PI * i++ / related.Count - Math.PI / 2;
                var c = n.Bounds.Center;
                Show(x, new Pt(c.X + Math.Cos(angle) * 280 - 80, c.Y + Math.Sin(angle) * 220 - 40));
            }
        }
        _ws.SetStatus($"Displayed {related.Count} related elements");
    }

    private void CreateNodeAt(ElementKind kind, Pt p) => CreateNode(kind, ToolSubtype, p, stereotype: ToolStereotype);

    /// <summary>Creates an element from a palette entry dropped or picked at a screen point.</summary>
    public void CreateAtScreen(PaletteItem item, double sx, double sy)
    {
        if (_ws.ReadOnly) return;
        if (Metamodel.IsRelationship(item.Kind)) { SetTool(item); return; }
        CreateNode(item.Kind, item.Subtype, ToModel(sx, sy), stereotype: item.Stereotype);
    }

    /// <summary>Click on a palette tile: insert the shape in a free spot near the middle of the view and start naming it.</summary>
    public void InsertItem(PaletteItem item)
    {
        if (_ws.ReadOnly) return;
        if (Metamodel.IsRelationship(item.Kind)) { SetTool(item); return; }
        var (w, h) = Notation.DefaultSizeOf(item.Kind, item.Subtype);
        var vp = Viewport;
        var start = new Pt(vp.X + vp.Width / 2 - w / 2, vp.Y + vp.Height / 3 - h / 2);
        var at = start;
        for (var ring = 0; ring < 25; ring++)
        {
            var steps = Math.Max(1, ring * 8);
            var found = false;
            for (var k = 0; k < steps && !found; k++)
            {
                var ang = 2 * Math.PI * k / steps;
                var p = new Pt(start.X + Math.Cos(ang) * ring * 45, start.Y + Math.Sin(ang) * ring * 45);
                var b = new Bounds(p.X - 20, p.Y - 20, w + 40, h + 40);
                if (!Diagram.Nodes.Any(n => BoundsOf(n).Intersects(b))) { at = p; found = true; }
            }
            if (found) break;
        }
        CreateNode(item.Kind, item.Subtype, new Pt(at.X + w / 2, at.Y + h / 3), stereotype: item.Stereotype);
    }

    /// <summary>Creates a new element of <paramref name="kind"/> and shows it at <paramref name="p"/> (centre x, top + h/3).</summary>
    private DiagramNode? CreateNode(ElementKind kind, string? subtype, Pt p, (double W, double H)? size = null, string? stereotype = null)
    {
        try
        {
            var owner = Model.DefaultOwnerFor(Diagram.Owner ?? Model.Root, kind);
            DiagramNode? container = null;

            // Creating inside a shape nests the new element: ports/parts on parts (owned by the part's type),
            // pins on actions, substates in states, contents of swimlanes, pools and lanes.
            if (HitNode(p) is { } hit && hit.Element.Kind != ElementKind.Comment)
            {
                var host = hit.Element;
                var typeOfHost = Model.ResolveType(host);
                if (Metamodel.CanOwn(host.Kind, kind) && host.Kind is not (ElementKind.Package or ElementKind.Model)) { owner = host; container = hit; }
                else if (kind is ElementKind.Port or ElementKind.Property && typeOfHost != null && Metamodel.CanOwn(typeOfHost.Kind, kind)) { owner = typeOfHost; container = hit; }
                else if (Notation.ShapeOf(host) is NodeShape.Swimlane or NodeShape.Pool && kind != ElementKind.Comment) container = hit;
            }
            if (kind is ElementKind.InputPin or ElementKind.OutputPin && container?.Element.Kind != ElementKind.Action)
                throw new InvalidOperationException("Pins go on actions: click on an action with the pin tool.");

            DiagramNode node;
            if (kind == ElementKind.Lifeline && Diagram.Nodes.FirstOrDefault(n => n.Element.Kind == ElementKind.Lifeline) is { } first)
                p = new Pt(p.X, first.Bounds.Y + Metamodel.DefaultSize(kind).H / 3);
            using (Model.History.Transaction($"Create {Metamodel.DisplayName(kind)}"))
            {
                var e = Model.Create(owner, kind, subtype != null && kind is ElementKind.BpmnNode && Subtypes.BpmnShape(subtype) is NodeShape.Event or NodeShape.Gateway ? "" : null);
                if (subtype != null) Model.Set(e, FieldKeys.Subtype, subtype);
                if (kind == ElementKind.Comment) Model.Set(e, FieldKeys.Body, "Comment");
                if (stereotype != null)
                {
                    // UAF viewpoints: the profile (or a stereotype missing from an older copy of it) comes along with the element.
                    if (UafViewpoints.IsUaf(Diagram.DiagramType)) Profiles.EnsureUafStereotype(Model, stereotype);
                    Model.Set(e, FieldKeys.Stereotypes, stereotype);
                    if (Metamodel.NeedsName(kind)) Model.Set(e, FieldKeys.Name, UniqueStereotypedName(owner, stereotype));
                }
                if (kind == ElementKind.TimingLifeline)
                {
                    Model.Set(e, InteractionNotation.States, "Idle, Active");
                    Model.Set(e, InteractionNotation.Timeline, "0 Idle; 10 Active; 30 Idle");
                }
                if (kind == ElementKind.InlineInteraction) Model.Set(e, InteractionNotation.Sequence, "A -> B : request\nB -> A : reply");
                if (subtype != null && Metamodel.NeedsName(kind) && string.IsNullOrEmpty(e.Get(FieldKeys.Name)) == false && kind is ElementKind.ArchiElement or ElementKind.BpmnNode)
                    Model.Set(e, FieldKeys.Name, Subtypes.Display(subtype));
                var (w, h) = size ?? Notation.DefaultSizeOf(kind, subtype);
                var bounds = new Bounds(Snap(p.X - w / 2), Snap(p.Y - h / 3), w, h);
                if (Notation.IsBorderShape(kind) && container != null) bounds = Notation.SnapToBorder(new Bounds(p.X - w / 2, p.Y - h / 2, w, h), BoundsOf(container));
                node = Model.AddNode(Diagram, e, bounds, parent: container);
                PutOnActiveLayer(node);
                if (container != null) GrowContainers([node]);
            }
            _selNodes.Clear();
            SelectedEdge = null;
            _selNodes.Add(node);
            if (Metamodel.NeedsName(kind) && !Notation.IsBorderShape(kind) || kind is ElementKind.Comment or ElementKind.Shape) Editing = node;
            EnsureVisible(BoundsOf(node));
            Notify();
            return node;
        }
        catch (InvalidOperationException ex)
        {
            _ws.SetStatus(ex.Message);
            return null;
        }
    }

    /// <summary>IBD: shows the ports of a part's type on the part's border.</summary>
    public void ShowPortsOf(DiagramNode part)
    {
        var type = Model.ResolveType(part.Element) ?? part.Element;
        var ports = type.Kind == ElementKind.ConstraintBlock
            ? type.OwnedElements.Where(x => x.Kind == ElementKind.Property).ToList()     // constraint parameters
            : type.OwnedElements.Where(x => x.Kind == ElementKind.Port).ToList();
        if (ports.Count == 0) { _ws.SetStatus($"{type.DisplayName} has no ports or parameters"); return; }
        using (Model.History.Transaction("Show ports"))
        {
            var b = BoundsOf(part);
            for (var i = 0; i < ports.Count; i++)
            {
                var (w, h) = Metamodel.DefaultSize(ElementKind.Port);
                var y = b.Y + b.Height * (i + 1) / (ports.Count + 1);
                var x = ports[i].Get(FieldKeys.Direction) == "in" ? b.X : b.Right;
                Model.AddNode(Diagram, ports[i], Notation.SnapToBorder(new Bounds(x - w / 2, y - h / 2, w, h), b), parent: part);
            }
        }
    }

    /// <summary>IBD: shows the parts of a part's type nested inside it.</summary>
    public void ShowNestedParts(DiagramNode part)
    {
        var type = Model.ResolveType(part.Element);
        var parts = type?.OwnedElements.Where(x => x.Kind == ElementKind.Property && Model.ResolveType(x)?.Kind is ElementKind.Block or ElementKind.Class).ToList() ?? [];
        if (parts.Count == 0) { _ws.SetStatus("No nested parts to show"); return; }
        using (Model.History.Transaction("Show nested parts"))
        {
            var b = part.Bounds;
            var w = Math.Max(120, (b.Width - 30 - 20 * (parts.Count - 1)) / parts.Count);
            var needH = 110.0;
            if (b.Height < needH + 50) Model.SetBounds(Diagram, new Dictionary<DiagramNode, Bounds> { [part] = b with { Height = needH + 50, Width = Math.Max(b.Width, parts.Count * 140 + 30) } }, "Grow part");
            b = part.Bounds;
            w = Math.Max(110, (b.Width - 30 - 20 * (parts.Count - 1)) / parts.Count);
            for (var i = 0; i < parts.Count; i++)
                Model.AddNode(Diagram, parts[i], new Bounds(Snap(b.X + 15 + i * (w + 20)), Snap(b.Y + 40), w, 56), parent: part);
        }
    }

    private void Connect(ElementKind kind, string? subtype, DiagramNode from, DiagramNode to, bool select = true, string? stereotype = null)
    {
        using (Model.History.Transaction($"Create {Metamodel.DisplayName(kind)}"))
        {
            var r = Model.Connect(kind, from.Element, to.Element);
            if (subtype != null) Model.Set(r, FieldKeys.Subtype, subtype);
            if (stereotype != null)
            {
                if (UafViewpoints.IsUaf(Diagram.DiagramType)) Profiles.EnsureUafStereotype(Model, stereotype);
                Model.Set(r, FieldKeys.Stereotypes, stereotype);
            }
            // Ports belong to a block type, so remember which part they were reached through.
            if (Notation.IsBorderShape(from.Element) && Diagram.ParentOf(from) is { } fp) Model.Set(r, FieldKeys.SourcePart, fp.Element.Id);
            if (Notation.IsBorderShape(to.Element) && Diagram.ParentOf(to) is { } tp) Model.Set(r, FieldKeys.TargetPart, tp.Element.Id);
            var edge = Model.AddEdge(Diagram, r);
            // A message's vertical position is its place in time: use where the user started dragging.
            if (edge != null && kind == ElementKind.Message)
                Model.SetWaypoints(Diagram, edge, [new Pt(0, Snap(Math.Max(_pressModel.Y, from.Bounds.Y + Notation.LifelineHeader + 20)))]);
            if (!select) return;
            _selNodes.Clear();
            SelectedEdge = edge;
        }
        Notify();
    }

    private void InsertWaypoint(DiagramEdge edge, Pt at)
    {
        var pts = edge.Waypoints.Count > 0 ? Notation.Route(edge, BoundsOf) : Notation.RouteIn(Diagram, edge, BoundsOf);
        var best = 0;
        var bestD = double.MaxValue;
        for (var i = 1; i < pts.Count; i++)
        {
            var d = Notation.DistanceToSegment(at, pts[i - 1], pts[i]);
            if (d < bestD) { bestD = d; best = i - 1; }
        }
        var wps = edge.Waypoints.ToList();
        wps.Insert(Math.Clamp(best, 0, wps.Count), new Pt(Snap(at.X), Snap(at.Y)));
        Model.SetWaypoints(Diagram, edge, wps);
        _selNodes.Clear();
        SelectedEdge = edge;
        Notify();
    }

    public void CommitInlineEdit(string? text)
    {
        EditSeed = null;
        if (EditingEdge is { } edge)
        {
            EditingEdge = null;
            if (text != null && Model.Contains(edge.Relationship)) Model.Set(edge.Relationship, FieldKeys.Name, text.Trim());
            return;
        }
        var n = Editing;
        Editing = null;
        if (n == null || !Model.Contains(n.Element) || text == null) return;
        var key = n.Element.Kind == ElementKind.Comment ? FieldKeys.Body : FieldKeys.Name;
        Model.Set(n.Element, key, text.Trim());
    }

    /// <summary>Screen rectangle for the inline label editor of a connector (centred on its midpoint).</summary>
    public (double X, double Y, double W, double H) EditorRect(DiagramEdge e)
    {
        var mid = ToScreen(Notation.Midpoint(Notation.RouteIn(Diagram, e, BoundsOf)));
        return (mid.X - 90, mid.Y - 16, 180, 32);
    }

    /// <summary>Screen rectangle for the inline name editor.</summary>
    public (double X, double Y, double W, double H) EditorRect(DiagramNode n)
    {
        var b = BoundsOf(n);
        var tl = ToScreen(new Pt(b.X, b.Y));
        var shape = Notation.ShapeOf(n.Element);
        var w = Math.Max(160, b.Width * Zoom);
        if (n.Element.Kind == ElementKind.Shape)
            return (tl.X + (b.Width * Zoom - w) / 2, tl.Y + b.Height * Zoom / 2 - 16, w, 32);
        if (shape is NodeShape.StickFigure or NodeShape.SmallSquare or NodeShape.Diamond or NodeShape.Bar)
            return (tl.X - (w - b.Width * Zoom) / 2, tl.Y + b.Height * Zoom + 4, w, 32);
        if (n.Element.Kind == ElementKind.Comment) return (tl.X, tl.Y, w, Math.Max(60, b.Height * Zoom));
        var y = shape == NodeShape.Rectangle && Notation.Content(Model, n.Element, Diagram.DiagramType).Keyword != null ? 18 : 2;
        return (tl.X, tl.Y + y * Zoom, w, 32);
    }
}
