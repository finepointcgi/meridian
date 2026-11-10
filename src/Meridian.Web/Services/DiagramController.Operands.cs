using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Web.Services;

/// <summary>Combined fragment operands: adding one, and dragging the dashed separator between two of them.</summary>
public sealed partial class DiagramController
{
    private DiagramNode? _sepNode;
    private int _sepIndex = -1;

    /// <summary>The separator being dragged: fragment shape, operand index (≥ 1) and its height now; null when none.</summary>
    public (DiagramNode Fragment, int Index, double Y)? SeparatorPreview { get; private set; }

    /// <summary>The separator of a visible fragment within a few pixels of <paramref name="p"/>: fragment shape and operand index.</summary>
    public (DiagramNode Fragment, int Index)? HitSeparator(Pt p)
    {
        var tol = 5 / Zoom;
        foreach (var n in VisibleNodes.Where(n => n.Element.Kind == ElementKind.CombinedFragment && Pickable(n)).Reverse())
        {
            var b = BoundsOf(n);
            if (p.X < b.X || p.X > b.Right) continue;
            foreach (var band in InteractionOperands.Layout(n, b).Skip(1))
                if (Math.Abs(p.Y - band.Top) <= tol) return (n, band.Index);
        }
        return null;
    }

    /// <summary>Grab strips drawn over the separators of the selected fragments (at their preview height while one is dragged).</summary>
    public IReadOnlyList<(DiagramNode Fragment, int Index, double Y, double X0, double X1)> SeparatorHandles()
    {
        var list = new List<(DiagramNode, int, double, double, double)>();
        if (_ws.ReadOnly) return list;
        foreach (var n in _selNodes.Where(n => n.Element.Kind == ElementKind.CombinedFragment && Diagram.Nodes.Contains(n)))
        {
            var b = BoundsOf(n);
            foreach (var band in InteractionOperands.Layout(n, b).Skip(1))
            {
                var y = SeparatorPreview is { } sp && ReferenceEquals(sp.Fragment, n) && sp.Index == band.Index ? sp.Y : band.Top;
                list.Add((n, band.Index, y, b.X, b.Right));
            }
        }
        return list;
    }

    private bool BeginSeparatorDrag(Pt p)
    {
        if (_ws.ReadOnly || HitSeparator(p) is not { } hit) return false;
        _sepNode = hit.Fragment;
        _sepIndex = hit.Index;
        if (!_selNodes.Contains(hit.Fragment)) { ClearSelection(); _selNodes.Add(hit.Fragment); }
        SeparatorPreview = (hit.Fragment, hit.Index, InteractionOperands.Layout(hit.Fragment, BoundsOf(hit.Fragment))[hit.Index].Top);
        Current = Gesture.Separator;
        Notify();
        return true;
    }

    private bool DragSeparator()
    {
        if (_sepNode == null) return false;
        var b = BoundsOf(_sepNode);
        var y = InteractionOperands.ClampSeparator(InteractionOperands.Layout(_sepNode, b), b, _sepIndex, Snap(CurrentModel.Y));
        SeparatorPreview = (_sepNode, _sepIndex, y);
        return true;
    }

    private void EndSeparatorDrag()
    {
        try
        {
            if (_sepNode != null && SeparatorPreview is { } sp && Diagram.Nodes.Contains(_sepNode))
                Model.MoveSeparator(Diagram, _sepNode, _sepIndex, sp.Y);
        }
        finally
        {
            _sepNode = null;
            _sepIndex = -1;
            SeparatorPreview = null;
        }
    }

    /// <summary>Adds an operand to a fragment shape (the selected one when null) and selects the new operand. One undo step.</summary>
    public Element? AddOperand(DiagramNode? fragment = null)
    {
        if (_ws.ReadOnly) return null;
        fragment ??= _selNodes.FirstOrDefault(n => n.Element.Kind == ElementKind.CombinedFragment);
        if (fragment == null) { _ws.SetStatus("Select a combined fragment to add an operand to."); return null; }
        try
        {
            var op = Model.AddOperand(fragment.Element);
            _ws.SetStatus($"Added an operand to the {fragment.Element.Get(FieldKeys.Operator)} fragment (drag the dashed line to move it, ⌘Z to undo)");
            return op;
        }
        catch (InvalidOperationException ex)
        {
            _ws.SetStatus(ex.Message);
            return null;
        }
    }
}
