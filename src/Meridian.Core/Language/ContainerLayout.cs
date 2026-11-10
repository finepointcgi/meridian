using Meridian.Core.Model;

namespace Meridian.Core.Language;

/// <summary>
/// Containers (packages, frames, swimlanes, parts with nested parts, composite states) and their contents:
/// growing a container when its children move outside it, and fitting it tightly around them.
/// </summary>
public static class ContainerLayout
{
    public const double Padding = 14;

    /// <summary>Space kept free at the top (or left, for pools) of a container for its title.</summary>
    public static (double Left, double Top) Header(DiagramNode container) => Notation.ShapeOf(container.Element) switch
    {
        NodeShape.Folder => (Padding, 46),
        NodeShape.Swimlane => (Padding, 40),
        NodeShape.Pool => (44, Padding),
        NodeShape.Fragment or NodeShape.InteractionUse or NodeShape.InlineInteraction => (Padding, 30),
        NodeShape.Rounded => (Padding, 36),
        _ => (Padding, 50),
    };

    /// <summary>Children that live inside a container (border shapes such as ports sit on its outline instead).</summary>
    public static IEnumerable<DiagramNode> InnerChildren(Diagram d, DiagramNode container) =>
        d.Nodes.Where(c => c.ParentId == container.Id && !Notation.IsBorderShape(c.Element));

    public static bool IsContainer(Diagram d, DiagramNode n) => InnerChildren(d, n).Any();

    /// <summary>The smallest box around a container's children, with room for its title.</summary>
    public static Bounds? ContentBox(Diagram d, DiagramNode container, Func<DiagramNode, Bounds> boundsOf)
    {
        var kids = InnerChildren(d, container).Select(boundsOf).ToList();
        if (kids.Count == 0) return null;
        var (left, top) = Header(container);
        double x0 = kids.Min(b => b.X) - left, y0 = kids.Min(b => b.Y) - top;
        double x1 = kids.Max(b => b.Right) + Padding, y1 = kids.Max(b => b.Bottom) + Padding;
        return new Bounds(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// Containers of <paramref name="changed"/> (transitively) grown so that their children fit. Containers in
    /// <paramref name="fixedNodes"/> (being dragged themselves) are left alone. Returns only containers that change.
    /// </summary>
    public static Dictionary<DiagramNode, Bounds> GrowToFit(Diagram d, IEnumerable<DiagramNode> changed, Func<DiagramNode, Bounds> boundsOf,
                                                            ICollection<DiagramNode>? fixedNodes = null)
    {
        var result = new Dictionary<DiagramNode, Bounds>(ReferenceEqualityComparer.Instance);
        Bounds Of(DiagramNode n) => result.TryGetValue(n, out var b) ? b : boundsOf(n);
        var queue = new Queue<DiagramNode>(changed);
        var guard = 0;
        while (queue.Count > 0 && guard++ < 10_000)
        {
            var n = queue.Dequeue();
            if (Notation.IsBorderShape(n.Element) || d.ParentOf(n) is not { } parent) continue;
            if (fixedNodes != null && fixedNodes.Contains(parent)) continue;
            if (ContentBox(d, parent, Of) is not { } need) continue;
            var cur = Of(parent);
            double x0 = Math.Min(cur.X, need.X), y0 = Math.Min(cur.Y, need.Y);
            double x1 = Math.Max(cur.Right, need.Right), y1 = Math.Max(cur.Bottom, need.Bottom);
            var grown = new Bounds(x0, y0, x1 - x0, y1 - y0);
            if (grown == cur) continue;
            result[parent] = grown;
            queue.Enqueue(parent);
        }
        SnapBorderChildren(d, result, boundsOf);
        return result;
    }

    /// <summary>Ports and pins of resized containers stay on their outline.</summary>
    private static void SnapBorderChildren(Diagram d, Dictionary<DiagramNode, Bounds> result, Func<DiagramNode, Bounds> boundsOf)
    {
        foreach (var (c, cb) in result.ToList())
            foreach (var port in d.Nodes.Where(x => x.ParentId == c.Id && Notation.IsBorderShape(x.Element)))
            {
                var snapped = Notation.SnapToBorder(result.TryGetValue(port, out var pb) ? pb : boundsOf(port), cb);
                if (snapped != boundsOf(port)) result[port] = snapped;
            }
    }

    /// <summary>
    /// Every container among <paramref name="containers"/> (or all containers of the diagram) fitted tightly around
    /// its children, innermost first so outer containers fit the resized inner ones.
    /// </summary>
    public static Dictionary<DiagramNode, Bounds> FitToContents(Diagram d, Func<DiagramNode, Bounds> boundsOf, IEnumerable<DiagramNode>? containers = null)
    {
        var result = new Dictionary<DiagramNode, Bounds>(ReferenceEqualityComparer.Instance);
        Bounds Of(DiagramNode n) => result.TryGetValue(n, out var b) ? b : boundsOf(n);
        var list = (containers ?? d.Nodes).Where(n => IsContainer(d, n)).Distinct().OrderByDescending(n => Depth(d, n)).ToList();
        foreach (var c in list)
            if (ContentBox(d, c, Of) is { } box && box != Of(c)) result[c] = box;
        // Parents of the fitted containers grow if needed (fitting never pushes content outside its container).
        foreach (var (n, b) in GrowToFit(d, result.Keys.ToList(), Of)) result[n] = b;
        SnapBorderChildren(d, result, boundsOf);
        return result;
    }

    private static int Depth(Diagram d, DiagramNode n)
    {
        var depth = 0;
        for (var p = d.ParentOf(n); p != null && depth < 64; p = d.ParentOf(p)) depth++;
        return depth;
    }
}
