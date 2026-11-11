using System.Globalization;
using System.Xml.Linq;
using Meridian.Core.Instances;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>
/// UML instance specifications, slots and links in XMI (shared by <see cref="XmiExchange"/> and the Cameo importer).
/// <para>
/// Export: an instance is a <c>uml:InstanceSpecification</c> with <c>classifier</c> and one <c>uml:Slot</c> per slot whose
/// value is a <c>LiteralReal</c> / <c>LiteralInteger</c> / <c>LiteralBoolean</c> / <c>LiteralString</c>, an
/// <c>InstanceValue</c> (instances, enumeration literals) or an <c>OpaqueExpression</c>. UML instance specifications don't
/// nest, so nested part instances are written next to their whole, marked <c>meridian:owner</c>. A link is an instance
/// specification classified by its association with one slot per association end.
/// </para>
/// </summary>
internal static class InstanceXmi
{
    private static XNamespace Xmi => XmiExchange.Xmi;
    private static XNamespace Meridian => XmiExchange.Meridian;
    private static XAttribute Type(string t) => new(Xmi + "type", "uml:" + t);

    // ================================================================== export

    public static void WriteInstance(ModelRepository m, XElement x, Element instance)
    {
        var ids = InstanceModel.ClassifierIds(instance).Where(id => m.Find(id) != null).ToList();
        if (ids.Count > 0) x.Add(new XAttribute("classifier", string.Join(" ", ids)));
        if (instance.Owner is { Kind: ElementKind.InstanceSpecification } whole) x.Add(new XAttribute(Meridian + "owner", whole.Id));
    }

    public static void WriteSlot(ModelRepository m, XElement x, Element slot)
    {
        if (InstanceModel.FeatureOf(m, slot) is { } f) x.Add(new XAttribute("definingFeature", f.Id));
        var i = 0;
        string NextId() => slot.Id + "_v" + (i++).ToString(CultureInfo.InvariantCulture);
        switch (InstanceModel.KindOf(slot))
        {
            case SlotValueKind.Instance or SlotValueKind.Enum:
                foreach (var id in InstanceModel.ValueIds(slot))
                    x.Add(new XElement("value", Type("InstanceValue"), new XAttribute(Xmi + "id", NextId()), new XAttribute("instance", id)));
                break;
            case SlotValueKind.Expression:
                x.Add(new XElement("value", Type("OpaqueExpression"), new XAttribute(Xmi + "id", NextId()), new XElement("body", slot.Get(InstanceFields.Value))));
                break;
            default:
                x.Add(Literal(m, slot, NextId()));
                break;
        }
    }

    /// <summary>A literal as the most specific UML literal: numbers (without their unit, which is the feature's) and booleans typed.</summary>
    private static XElement Literal(ModelRepository m, Element slot, string id)
    {
        var raw = slot.Get(InstanceFields.Value);
        var feature = InstanceModel.FeatureOf(m, slot);
        if (feature?.TypeRef == "Boolean" && bool.TryParse(raw, out var b))
            return new XElement("value", Type("LiteralBoolean"), new XAttribute(Xmi + "id", id), new XAttribute("value", b ? "true" : "false"));
        if (InstanceModel.NumericValue(m, slot) is { } n && (feature == null || InstanceModel.UnitOf(m, feature) != null || feature.TypeRef is "Real" or "Integer" || !raw.Any(char.IsLetter)))
        {
            var integer = feature?.TypeRef == "Integer" && Math.Abs(n - Math.Round(n)) < 1e-9;
            return new XElement("value", Type(integer ? "LiteralInteger" : "LiteralReal"), new XAttribute(Xmi + "id", id),
                new XAttribute("value", integer ? ((long)Math.Round(n)).ToString(CultureInfo.InvariantCulture) : InstanceModel.FormatNumber(n)));
        }
        return new XElement("value", Type("LiteralString"), new XAttribute(Xmi + "id", id), new XAttribute("value", raw));
    }

    /// <summary>A link: an instance specification classified by its association, with a slot per end (source first).</summary>
    public static XElement WriteLink(ModelRepository? m, Relationship link)
    {
        var x = new XElement("packagedElement", Type("InstanceSpecification"), new XAttribute(Xmi + "id", link.Id), new XAttribute(Meridian + "kind", nameof(ElementKind.Link)));
        var assoc = m != null ? InstanceModel.AssociationOf(m, link) : null;
        if (assoc != null) x.Add(new XAttribute("classifier", assoc.Id));
        // The association's ends are exported as <id>_src (typed by its source) and <id>_tgt (typed by its target).
        var sourceIsAssocSource = assoc == null || m == null || InstanceModel.Classifiers(m, link.Source).Any(c => InstanceModel.Conforms(m, c, assoc.Source));
        var (srcEnd, tgtEnd) = assoc == null ? ((string?)null, (string?)null) : sourceIsAssocSource ? (assoc.Id + "_src", assoc.Id + "_tgt") : (assoc.Id + "_tgt", assoc.Id + "_src");
        XElement Slot(string suffix, string? end, Element value)
        {
            var s = new XElement("slot", Type("Slot"), new XAttribute(Xmi + "id", link.Id + suffix));
            if (end != null) s.Add(new XAttribute("definingFeature", end));
            s.Add(new XElement("value", Type("InstanceValue"), new XAttribute(Xmi + "id", link.Id + suffix + "_v"), new XAttribute("instance", value.Id)));
            return s;
        }
        x.Add(Slot("_s1", srcEnd, link.Source), Slot("_s2", tgtEnd, link.Target));
        return x;
    }

    // ================================================================== import

    /// <summary>The ids of a value specification list: <c>instance</c> attributes / children of InstanceValues.</summary>
    private static IEnumerable<string> InstanceRefs(XElement value)
    {
        if (value.Attribute("instance")?.Value is { Length: > 0 } a) yield return a;
        foreach (var c in value.Elements().Where(c => c.Name.LocalName == "instance"))
            if (MagicDrawImporter.IdRef(c) is { } id) yield return id;
    }

    private static IEnumerable<XElement> Values(XElement slot) => slot.Elements().Where(v => v.Name.LocalName == "value");

    /// <summary>The two instances a link-like instance specification connects (from its slots' instance values), in slot order.</summary>
    public static IReadOnlyList<string> LinkEnds(XElement spec) =>
        spec.Elements().Where(s => s.Name.LocalName == "slot").SelectMany(s => Values(s).SelectMany(InstanceRefs)).Take(2).ToList();

    /// <summary>Is this instance specification a link (marked so, or classified by an association)?</summary>
    public static bool IsLink(XElement spec, Func<string, string?> xmiTypeOf)
    {
        if (spec.Attribute(Meridian + "kind")?.Value == nameof(ElementKind.Link)) return true;
        var cls = MagicDrawImporter.Refs(spec, "classifier");
        return cls.Count == 1 && xmiTypeOf(cls[0]) is "Association" or "AssociationClass" && LinkEnds(spec).Count == 2;
    }

    /// <summary>Classifier ids of an instance specification (attribute or child references).</summary>
    public static List<string> ClassifierRefs(XElement spec) => MagicDrawImporter.Refs(spec, "classifier");

    /// <summary>Reads a slot's value: kind and Meridian value text, with referenced ids mapped by <paramref name="resolve"/>.</summary>
    public static (SlotValueKind Kind, string Value)? ReadValue(XElement slot, Func<string, Element?> resolve)
    {
        var values = Values(slot).ToList();
        if (values.Count == 0) return null;
        var refs = values.SelectMany(InstanceRefs).ToList();
        if (refs.Count > 0)
        {
            var els = refs.Select(resolve).OfType<Element>().ToList();
            if (els.Count == 0) return null;
            var kind = els.All(e => e.Kind == ElementKind.EnumerationLiteral) ? SlotValueKind.Enum : SlotValueKind.Instance;
            return (kind, string.Join(",", els.Where(e => (e.Kind == ElementKind.EnumerationLiteral) == (kind == SlotValueKind.Enum)).Select(e => e.Id)));
        }
        var first = values[0];
        var type = MagicDrawImporter.XmiType(first);
        if (type is "OpaqueExpression" or "Expression" or "StringExpression")
            return (SlotValueKind.Expression, MagicDrawImporter.ValueText(first) ?? "");
        var texts = values.Select(MagicDrawImporter.ValueText).OfType<string>().Where(t => t != "null").ToList();
        if (texts.Count == 0) return null;
        return (SlotValueKind.Literal, string.Join(", ", texts));
    }

    /// <summary>Fills a slot's fields from XMI (feature and value), mapping ids through <paramref name="resolve"/>.</summary>
    public static void ReadSlot(Element slot, XElement x, Func<string, Element?> resolve)
    {
        if (MagicDrawImporter.Ref(x, "definingFeature") is { } fid)
            ModelRepository.SetFieldForLoad(slot, InstanceFields.Feature, resolve(fid)?.Id ?? fid);
        if (ReadValue(x, resolve) is { } v)
        {
            ModelRepository.SetFieldForLoad(slot, InstanceFields.ValueKind, InstanceModel.KindCode(v.Kind));
            ModelRepository.SetFieldForLoad(slot, InstanceFields.Value, v.Value);
        }
    }

    /// <summary>Fills an instance's classifiers from XMI, keeping those that resolve to classifiers.</summary>
    public static void ReadInstance(Element instance, XElement x, Func<string, Element?> resolve)
    {
        var ids = ClassifierRefs(x).Select(resolve).OfType<Element>().Where(e => InstanceModel.CanClassify(e.Kind) || Metamodel.IsClassifier(e.Kind)).Select(e => e.Id).Distinct().ToList();
        ModelRepository.SetFieldForLoad(instance, InstanceFields.Classifier, ids.Count == 0 ? null : string.Join(",", ids));
    }
}
