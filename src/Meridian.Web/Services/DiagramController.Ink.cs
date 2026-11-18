using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Web.Services;

/// <summary>
/// Freehand drawing on the canvas: the Pen and Highlighter tools draw a stroke while the pointer is dragged (mouse, pen
/// or one finger), and the Eraser deletes the strokes it touches. Strokes are free shapes (<see cref="Strokes"/>), so
/// selecting, moving, formatting, layers, undo and collaboration work as for any shape.
/// </summary>
public sealed partial class DiagramController
{
    private readonly List<Pt> _ink = new();

    /// <summary>The stroke being drawn (diagram coordinates), or null.</summary>
    public IReadOnlyList<Pt>? InkPreview => Current == Gesture.Draw && _ink.Count > 0 ? _ink : null;

    /// <summary>Is a drawing tool (pen, highlighter, eraser) active?</summary>
    public bool InkTool => Tool == ElementKind.Shape && (Strokes.IsStrokeSubtype(ToolSubtype) || ToolSubtype == Strokes.EraserTool);

    /// <summary>The colour the stroke being drawn will get (the last stroke drawn with the same tool sets it), or null for the default.</summary>
    public string? InkColor => InkStyle(ToolSubtype == Strokes.HighlighterSubtype)?.GetValueOrDefault("stroke");

    /// <summary>The width the stroke being drawn will get.</summary>
    public double InkWidth
    {
        get
        {
            var hl = ToolSubtype == Strokes.HighlighterSubtype;
            return Strokes.WidthOf(InkStyle(hl) ?? new Dictionary<string, string>(), hl);
        }
    }

    private bool HitsStroke(DiagramNode n, Bounds b, Pt p) =>
        Strokes.Distance(n, b, p) <= Math.Max(7 / Zoom, Strokes.WidthOf(n) / 2 + 2 / Zoom);

    private bool BeginInk()
    {
        if (!InkTool || ReadOnly) return false;
        if (ToolSubtype == Strokes.EraserTool)
        {
            Current = Gesture.Erase;
            EraseAt(_pressModel);
            return true;
        }
        Current = Gesture.Draw;
        _ink.Clear();
        _ink.Add(_pressModel);
        return true;
    }

    private bool MoveInk(Mods mods)
    {
        if (Current == Gesture.Erase) return EraseAt(CurrentModel);
        var last = _ink[^1];
        // Skip points closer than about a screen pixel: the simplification would drop them anyway.
        if (Notation.Dist(last, CurrentModel) < 1.2 / Zoom) return false;
        // Shift draws a straight line from where the stroke started.
        if (mods.HasFlag(Mods.Shift)) { _ink.RemoveRange(1, _ink.Count - 1); }
        if (_ink.Count >= Strokes.MaxPoints * 4) return false;
        _ink.Add(CurrentModel);
        return true;
    }

    private void EndInk()
    {
        if (_ink.Count == 0) return;
        var highlighter = ToolSubtype == Strokes.HighlighterSubtype;
        var style = new Dictionary<string, string>(InkStyle(highlighter) ?? new Dictionary<string, string>());
        style.Remove(DiagramExtras.LayerStyle);
        if (ActiveLayerId != DiagramExtras.DefaultLayerId && DiagramExtras.FindLayer(Diagram, ActiveLayerId) != null)
            style[DiagramExtras.LayerStyle] = ActiveLayerId;
        var owner = Model.DefaultOwnerFor(Diagram.Owner ?? Model.Root, ElementKind.Shape);
        Model.AddStroke(Diagram, owner, _ink.ToList(), highlighter, tolerance: 0.8 / Zoom, style: style);
        _ink.Clear();
    }

    /// <summary>Format of the most recent stroke of the same kind: new strokes keep the colour and width last chosen.</summary>
    private IReadOnlyDictionary<string, string>? InkStyle(bool highlighter)
    {
        var sub = highlighter ? Strokes.HighlighterSubtype : Strokes.PenSubtype;
        for (var i = Diagram.Nodes.Count - 1; i >= 0; i--)
            if (Diagram.Nodes[i].Element.Kind == ElementKind.Shape && Diagram.Nodes[i].Element.Get(FieldKeys.Subtype) == sub)
                return Diagram.Nodes[i].Style;
        return null;
    }

    /// <summary>Eraser: deletes the front-most stroke under <paramref name="p"/>. Returns true when one went.</summary>
    private bool EraseAt(Pt p)
    {
        var hit = HitNode(p);
        if (hit == null || !Strokes.IsStroke(hit)) return false;
        try
        {
            using (Model.History.Transaction("Erase"))
                Model.Delete(hit.Element);
        }
        catch (InvalidOperationException ex) { _ws.SetStatus(ex.Message); return false; }
        _selNodes.Remove(hit);
        if (ReferenceEquals(Hover, hit)) Hover = null;
        return true;
    }
}
