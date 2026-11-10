using Meridian.Core.Model;

namespace Meridian.Core.Language;

/// <summary>Simple layered (Sugiyama-style) layout: assigns layers along relationship direction,
/// orders each layer by the barycentre of its neighbours, then spaces nodes on a grid.</summary>
public static class AutoLayout
{
    public static Dictionary<DiagramNode, Bounds> Layered(Diagram d, double hGap = 60, double vGap = 80, double originX = 40, double originY = 60)
    {
        var nodes = d.Nodes.ToList();
        var result = new Dictionary<DiagramNode, Bounds>();
        if (nodes.Count == 0) return result;

        // Sequence diagrams: lifelines in one row, in their current left-to-right order; messages keep their timing.
        if (d.DiagramType == DiagramType.Sequence)
        {
            var x = originX;
            foreach (var n in nodes.OrderBy(n => n.Bounds.X))
            {
                result[n] = n.Bounds with { X = x, Y = originY };
                x += n.Bounds.Width + hGap;
            }
            return result;
        }

        // "upper" -> "lower" pairs. Generalization / dependencies point up (target above), containment and flows point down.
        var pairs = new List<(DiagramNode up, DiagramNode down)>();
        foreach (var e in d.Edges)
        {
            if (ReferenceEquals(e.Source, e.Target)) continue;
            if (e.Relationship.Kind is ElementKind.Requires or ElementKind.Excludes) continue;   // cross-tree constraints do not shape the feature tree
            var st = Notation.StyleOf(e.Relationship);
            var targetAbove = st.TargetHead == ArrowHead.HollowTriangle || st.Dashed && e.Relationship.Kind is not ElementKind.ObjectFlow;
            pairs.Add(targetAbove ? (e.Target, e.Source) : (e.Source, e.Target));
        }

        var layer = nodes.ToDictionary(n => n, _ => 0);
        for (var iter = 0; iter < nodes.Count; iter++)   // longest path, bounded so cycles terminate
        {
            var changed = false;
            foreach (var (up, down) in pairs)
                if (layer[down] < layer[up] + 1 && layer[up] + 1 < nodes.Count) { layer[down] = layer[up] + 1; changed = true; }
            if (!changed) break;
        }

        var layers = nodes.GroupBy(n => layer[n]).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
        var order = new Dictionary<DiagramNode, double>();
        foreach (var l in layers) for (var i = 0; i < l.Count; i++) order[l[i]] = i;
        for (var sweep = 0; sweep < 4; sweep++)
            foreach (var l in layers.Skip(1))
            {
                double Bary(DiagramNode n)
                {
                    var ns = pairs.Where(p => p.down == n).Select(p => order[p.up]).ToList();
                    return ns.Count == 0 ? order[n] : ns.Average();
                }
                l.Sort((a, b) => Bary(a).CompareTo(Bary(b)));
                for (var i = 0; i < l.Count; i++) order[l[i]] = i;
            }

        var widest = layers.Max(l => l.Sum(n => n.Bounds.Width) + hGap * (l.Count - 1));
        var y = originY;
        foreach (var l in layers)
        {
            var rowWidth = l.Sum(n => n.Bounds.Width) + hGap * (l.Count - 1);
            var x = originX + (widest - rowWidth) / 2;
            var rowHeight = l.Max(n => n.Bounds.Height);
            foreach (var n in l)
            {
                result[n] = n.Bounds with { X = Math.Round(x / 10) * 10, Y = Math.Round((y + (rowHeight - n.Bounds.Height) / 2) / 10) * 10 };
                x += n.Bounds.Width + hGap;
            }
            y += rowHeight + vGap;
        }
        return result;
    }
}
