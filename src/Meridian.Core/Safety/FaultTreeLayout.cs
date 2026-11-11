using Meridian.Core.Model;

namespace Meridian.Core.Safety;

/// <summary>
/// Tidy top-down layout for fault tree diagrams: every event or gate is centred over its inputs, subtrees sit side by
/// side, and an input shared by several outputs is placed under the first one. Shapes not in a tree go in a row below.
/// </summary>
public static class FaultTreeLayout
{
    public static Dictionary<DiagramNode, Bounds> Tidy(Diagram d, double hGap = 30, double vGap = 46, double originX = 40, double originY = 60)
    {
        var result = new Dictionary<DiagramNode, Bounds>();
        var byElement = new Dictionary<Element, DiagramNode>(ReferenceEqualityComparer.Instance);
        foreach (var n in d.Nodes) byElement.TryAdd(n.Element, n);
        var links = d.Edges.Where(e => e.Relationship.Kind == ElementKind.FaultTreeLink && !ReferenceEquals(e.Source, e.Target)).ToList();
        var children = links.GroupBy(e => e.Source).ToDictionary(g => g.Key, g => g.Select(e => e.Target).OrderBy(t => t.Bounds.X).ToList());
        var hasParent = links.Select(e => e.Target).ToHashSet();
        var placed = new HashSet<DiagramNode>();

        double Layout(DiagramNode n, double x, double y)
        {
            placed.Add(n);
            var kids = children.TryGetValue(n, out var k) ? k.Where(c => !placed.Contains(c)).ToList() : [];
            foreach (var c in kids) placed.Add(c);   // claim them before descending so a shared input stays with its first parent
            if (kids.Count == 0)
            {
                result[n] = n.Bounds with { X = Snap(x), Y = Snap(y) };
                return n.Bounds.Width;
            }
            var cy = y + n.Bounds.Height + vGap;
            var cx = x;
            var centers = new List<double>();
            foreach (var c in kids)
            {
                placed.Remove(c);
                var w = Layout(c, cx, cy);
                centers.Add(result[c].X + c.Bounds.Width / 2);
                cx += w + hGap;
            }
            var span = cx - hGap - x;
            var mid = (centers[0] + centers[^1]) / 2;
            var nx = mid - n.Bounds.Width / 2;
            if (nx < x)   // the parent is wider than its inputs: shift the subtree right under it
            {
                var shift = x - nx;
                foreach (var c in Subtree(n)) result[c] = result[c] with { X = Snap(result[c].X + shift) };
                nx = x;
            }
            result[n] = n.Bounds with { X = Snap(nx), Y = Snap(y) };
            return Math.Max(span, nx + n.Bounds.Width - x);
        }

        IEnumerable<DiagramNode> Subtree(DiagramNode root)
        {
            var seen = new HashSet<DiagramNode>();
            var stack = new Stack<DiagramNode>(children.TryGetValue(root, out var k) ? k : []);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                if (!seen.Add(c) || !result.ContainsKey(c)) continue;
                yield return c;
                if (children.TryGetValue(c, out var cc)) foreach (var x in cc) stack.Push(x);
            }
        }

        var roots = d.Nodes.Where(n => children.ContainsKey(n) && !hasParent.Contains(n))
            .OrderBy(n => n.Element.Kind == ElementKind.FaultTreeEvent && SafetyFields.EventKind(n.Element) == SafetyFields.Top ? 0 : 1).ThenBy(n => n.Bounds.X).ToList();
        var x0 = originX;
        foreach (var r in roots) x0 += Layout(r, x0, originY) + hGap * 2;
        // Cycles without a root, then loose shapes in a row under everything.
        foreach (var n in d.Nodes.Where(n => children.ContainsKey(n) && !placed.Contains(n)).ToList()) x0 += Layout(n, x0, originY) + hGap * 2;
        var bottom = result.Count == 0 ? originY : result.Values.Max(b => b.Bottom) + vGap * 1.5;
        var lx = originX;
        foreach (var n in d.Nodes.Where(n => !result.ContainsKey(n) && n.ParentId == null))
        {
            result[n] = n.Bounds with { X = Snap(lx), Y = Snap(bottom) };
            lx += n.Bounds.Width + hGap;
        }
        return result;
    }

    private static double Snap(double v) => Math.Round(v / 2) * 2;
}
