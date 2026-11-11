using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Instances;

/// <summary>Object diagrams: placing an instance tree (whole above, parts in a row below) and finding the diagram that shows it.</summary>
public static class InstanceDiagrams
{
    /// <summary>A size that fits the instance's label and slot compartment.</summary>
    public static (double W, double H) SizeOf(ModelRepository m, Element instance)
    {
        var lines = InstanceModel.Slots(instance).Select(s => InstanceModel.SlotLine(m, s)).Prepend(InstanceModel.Label(m, instance)).ToList();
        var w = Math.Clamp(lines.Max(l => l.Length) * 7.2 + 30, 160, 360);
        var slots = lines.Count - 1;
        return (Math.Round(w / 10) * 10, 46 + (slots == 0 ? 0 : 10 + slots * 16));
    }

    /// <summary>
    /// Shows an instance and its nested part instances on <paramref name="d"/> with the instance at (x, y) and each level of
    /// parts in a row below its whole; links among them are drawn. Shapes already there stay where they are. One undo step.
    /// Returns the instance's shape.
    /// </summary>
    public static DiagramNode ShowTree(ModelRepository m, Diagram d, Element instance, double x, double y)
    {
        using (m.History.Transaction($"Show {instance.DisplayName}"))
        {
            var node = Place(instance, x, y);
            return node;
        }

        double Width(Element inst)
        {
            var kids = InstanceModel.NestedInstances(inst).ToList();
            var own = SizeOf(m, inst).W;
            return kids.Count == 0 ? own : Math.Max(own, kids.Sum(Width) + 40 * (kids.Count - 1));
        }

        DiagramNode Place(Element inst, double left, double top)
        {
            var (w, h) = SizeOf(m, inst);
            var span = Width(inst);
            var node = d.NodeFor(inst) ?? m.AddNode(d, inst, new Bounds(left + (span - w) / 2, top, w, h));
            var cx = left;
            foreach (var child in InstanceModel.NestedInstances(inst))
            {
                Place(child, cx, top + h + 70);
                cx += Width(child) + 40;
            }
            return node;
        }
    }

    /// <summary>An object diagram that already shows the instance, if any.</summary>
    public static Diagram? DiagramShowing(ModelRepository m, Element instance) =>
        m.Diagrams.FirstOrDefault(d => d.DiagramType == DiagramType.Object && d.Shows(instance));

    /// <summary>The object diagram for an instance: one that shows it, else a new one next to it with its tree placed. One undo step.</summary>
    public static Diagram ObjectDiagramFor(ModelRepository m, Element instance)
    {
        if (DiagramShowing(m, instance) is { } existing) return existing;
        var root = instance.AncestorsAndSelf().Last(a => a.Kind == ElementKind.InstanceSpecification);
        var owner = root.Owner is { } o && Metamodel.IsValidDiagramContext(DiagramType.Object, o.Kind) ? o : m.Root;
        using (m.History.Transaction($"Object diagram of {root.DisplayName}"))
        {
            var d = m.CreateDiagram(owner, DiagramType.Object, $"{root.Name} objects");
            ShowTree(m, d, root, 40, 40);
            return d;
        }
    }
}
