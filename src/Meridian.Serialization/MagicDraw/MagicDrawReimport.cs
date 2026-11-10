using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization;

public static partial class MagicDrawImporter
{
    /// <summary>
    /// Re-imports a project into an existing model, matching elements by their MagicDraw ids: existing elements are updated
    /// (fields, owner), new ones added, diagrams replaced. Elements that are no longer in the file are kept (and counted).
    /// One undoable step.
    /// </summary>
    public static MagicDrawImportReport ImportInto(ModelRepository target, string path)
    {
        var (imported, report) = Import(path);
        MergeInto(target, imported, report);
        return report;
    }

    public static MagicDrawImportReport ImportInto(ModelRepository target, Stream stream, string fileName)
    {
        var (imported, report) = Import(stream, fileName);
        MergeInto(target, imported, report);
        return report;
    }

    /// <summary>Applies <paramref name="imported"/> to <paramref name="target"/> by element id (see <see cref="ImportInto(ModelRepository,string)"/>).</summary>
    public static void MergeInto(ModelRepository target, ModelRepository imported, MagicDrawImportReport report)
    {
        var order = imported.Root.Descendants().ToList();   // pre-order: owners before their children
        Element? Map(Element e) => ReferenceEquals(e, imported.Root) ? target.Root : target.Find(e.Id);
        int added = 0, updated = 0, unchanged = 0;

        using (target.History.Transaction("Re-import MagicDraw project"))
        {
            if (target.Root.Name != imported.Root.Name && !string.IsNullOrEmpty(imported.Root.Name)) target.Rename(target.Root, imported.Root.Name);

            foreach (var e in order.Where(e => e is not Relationship && e is not Diagram))
            {
                var owner = Map(e.Owner!) ?? target.Root;
                var existing = target.Find(e.Id);
                if (existing != null && (existing.Kind != e.Kind || existing is Relationship || existing is Diagram || existing.Owner == null))
                {
                    if (existing.Owner == null) { unchanged++; continue; }   // the target's root
                    target.Delete(existing);
                    existing = null;
                }
                if (!Metamodel.CanOwn(owner.Kind, e.Kind)) owner = target.DefaultOwnerFor(owner, e.Kind);
                if (existing == null)
                {
                    var copy = new Element(e.Kind, e.Id);
                    foreach (var (k, v) in e.Fields) copy.SetRaw(k, v);
                    target.Add(owner, copy);
                    added++;
                    continue;
                }
                var changed = SyncFields(target, existing, e);
                if (!ReferenceEquals(existing.Owner, owner) && Metamodel.CanOwn(owner.Kind, existing.Kind) && !ReferenceEquals(existing, owner) && !existing.IsAncestorOf(owner))
                {
                    target.Move(existing, owner);
                    changed = true;
                }
                if (changed) updated++; else unchanged++;
            }

            foreach (var r in order.OfType<Relationship>())
            {
                var s = Map(r.Source); var t = Map(r.Target);
                if (s == null || t == null) continue;
                var owner = Map(r.Owner!) ?? target.DefaultOwnerFor(s.Owner ?? target.Root, r.Kind);
                if (!Metamodel.CanOwn(owner.Kind, r.Kind)) owner = target.DefaultOwnerFor(owner, r.Kind);
                var existing = target.Find(r.Id);
                if (existing is Relationship er && er.Kind == r.Kind && ReferenceEquals(er.Source, s) && ReferenceEquals(er.Target, t))
                {
                    var changed = SyncFields(target, er, r);
                    if (!ReferenceEquals(er.Owner, owner)) { target.Move(er, owner); changed = true; }
                    if (changed) updated++; else unchanged++;
                    continue;
                }
                if (existing != null && existing.Owner != null) target.Delete(existing);
                var copy = new Relationship(r.Kind, s, t, r.Id);
                foreach (var (k, v) in r.Fields) copy.SetRaw(k, v);
                target.Add(owner, copy);
                if (existing != null) updated++; else added++;
            }

            // Diagrams are replaced wholesale (their layout is MagicDraw's).
            foreach (var d in order.OfType<Diagram>())
            {
                var owner = Map(d.Owner!) ?? target.Root;
                if (!Metamodel.IsValidDiagramContext(d.DiagramType, owner.Kind)) continue;
                var existing = target.Find(d.Id);
                if (existing != null && existing.Owner != null) target.Delete(existing);
                var copy = new Diagram(d.DiagramType, d.Id);
                foreach (var (k, v) in d.Fields) copy.SetRaw(k, v);
                var nodes = new Dictionary<DiagramNode, DiagramNode>();
                foreach (var n in d.Nodes)
                {
                    if (Map(n.Element) is not { } el) continue;
                    var nn = new DiagramNode(el, n.Bounds, n.Id) { ParentId = n.ParentId, Style = new Dictionary<string, string>(n.Style) };
                    copy.NodeList.Add(nn);
                    nodes[n] = nn;
                }
                foreach (var x in d.Edges)
                {
                    if (Map(x.Relationship) is not Relationship rel || !nodes.TryGetValue(x.Source, out var sn) || !nodes.TryGetValue(x.Target, out var tn)) continue;
                    copy.EdgeList.Add(new DiagramEdge(rel, sn, tn, x.Id) { Waypoints = x.Waypoints.ToList(), Style = new Dictionary<string, string>(x.Style) });
                }
                target.Add(owner, copy);
                if (existing != null) updated++; else added++;
            }
        }

        report.Added = added;
        report.Updated = updated;
        report.Unchanged = unchanged;
        report.NotInFile = target.AllElements.Count(e => e.Owner != null && imported.Find(e.Id) == null && imported.Find(e.Owner.Id) != null);
        target.RaiseReset();
    }

    private static bool SyncFields(ModelRepository target, Element existing, Element source)
    {
        var changed = false;
        foreach (var key in existing.Fields.Keys.Union(source.Fields.Keys).ToList())
        {
            var want = source.Get(key);
            if (existing.Get(key) == want) continue;
            target.Set(existing, key, want);
            changed = true;
        }
        return changed;
    }
}
