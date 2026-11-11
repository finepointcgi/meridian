using System.Xml.Linq;
using Meridian.Core.Instances;
using Meridian.Core.Model;

namespace Meridian.Serialization;

public static partial class MagicDrawImporter
{
    private sealed partial class Builder
    {
        /// <summary>uml:InstanceSpecification elements, built once every classifier, feature and association is known.</summary>
        private readonly List<(XElement X, Element Owner, string? Id)> _instances = new();

        private void CollectInstance(XElement c, Element owner, string? id) => _instances.Add((Detach(c), owner, id));

        /// <summary>
        /// Instance specifications become instances with their slots (literal, enumeration literal, instance and expression
        /// values); those classified by an association become links between the two instances in their slots.
        /// </summary>
        private void BuildInstances()
        {
            Element? Resolve(string id) => _map.GetValueOrDefault(id);
            var created = new List<(Element El, XElement X)>();
            var links = new List<(XElement X, Element Owner, string? Id, Relationship Assoc)>();
            foreach (var (x, owner, id) in _instances)
            {
                var cls = InstanceXmi.ClassifierRefs(x);
                if (cls.Count == 1 && Resolve(cls[0]) is Relationship { Kind: ElementKind.Association or ElementKind.Composition or ElementKind.Aggregation } assoc)
                {
                    links.Add((x, owner, id, assoc));
                    continue;
                }
                var el = Create(ElementKind.InstanceSpecification, x, owner, id);
                if (el == null) continue;
                created.Add((el, x));
            }
            // Slots after every instance exists (values refer to other instances).
            foreach (var (el, x) in created)
            {
                InstanceXmi.ReadInstance(el, x, Resolve);
                if (InstanceModel.ClassifierIds(el).Count == 0 && InstanceXmi.ClassifierRefs(x).Count > 0)
                    WarnOnce("instanceClassifier:" + el.Id, $"Instance '{el.Name}': its classifier was not imported");
                foreach (var sx in x.Elements().Where(s => s.Name.LocalName == "slot"))
                {
                    var slot = Create(ElementKind.Slot, sx, el, XmiId(sx));
                    if (slot == null) continue;
                    InstanceXmi.ReadSlot(slot, sx, Resolve);
                }
            }
            foreach (var (x, owner, id, assoc) in links)
            {
                var ends = InstanceXmi.LinkEnds(x).Select(Resolve).ToList();
                if (ends.Count != 2 || ends[0] is not { Kind: ElementKind.InstanceSpecification } s || ends[1] is not { Kind: ElementKind.InstanceSpecification } t)
                {
                    _report.Warn($"Link '{Attr(x, "name") ?? id}' skipped: it needs two instances in its slots");
                    _report.Skip("Links with unresolved ends");
                    continue;
                }
                var link = new Relationship(ElementKind.Link, s, t, id != null && !_map.ContainsKey(id) && _model.Find(id) == null ? id : null);
                ModelRepository.SetFieldForLoad(link, FieldKeys.Name, Attr(x, "name"));
                ModelRepository.SetFieldForLoad(link, InstanceFields.Association, assoc.Id);
                var linkOwner = OwnerFor(owner, ElementKind.Link) ?? _model.Root;
                _model.AttachForLoad(linkOwner, link);
                if (id != null) _map.TryAdd(id, link);
                _report.Count("Link");
            }
        }
    }
}
