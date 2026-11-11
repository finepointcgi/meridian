using System.Globalization;
using System.Text.RegularExpressions;
using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Instances;

/// <summary>
/// Field keys of instance specifications, slots and links. They live in the ordinary field bag, so they replicate, undo,
/// save, diff and round-trip through the Meridian XMI extension like every other field.
/// </summary>
public static class InstanceFields
{
    /// <summary>Instance specifications: comma separated ids of the classifiers (blocks, value types, signals …).</summary>
    public const string Classifier = "instance.classifier";

    /// <summary>Slots: id of the defining structural feature (a property of one of the instance's classifiers).</summary>
    public const string Feature = "slot.feature";

    /// <summary>Slots: how <see cref="Value"/> reads — literal (default), enum, instance or expression.</summary>
    public const string ValueKind = "slot.kind";

    /// <summary>
    /// Slots: the value. A literal with an optional unit ("1500 kg", "true", "Blue paint"), the id of an enumeration literal,
    /// comma separated ids of instance specifications, or an expression ("mass * 9.81").
    /// </summary>
    public const string Value = "slot.value";

    /// <summary>Links: id of the association (association, composition or aggregation) the link is an instance of.</summary>
    public const string Association = "link.association";

    public static readonly IReadOnlyList<string> All = [Classifier, Feature, ValueKind, Value, Association];
}

/// <summary>What a slot's value is.</summary>
public enum SlotValueKind { Literal, Enum, Instance, Expression }

/// <summary>
/// Instance specifications (Cameo-style): objects classified by one or more classifiers, with one slot per structural
/// feature they give a value to, nested instances for part properties ("vehicle1" owns "engine" and "tank"), and links
/// typed by associations. Every mutator goes through <see cref="ModelRepository"/>, so it is undoable and replicates.
/// </summary>
public static partial class InstanceModel
{
    // ================================================================== classifiers and features

    /// <summary>Kinds an instance may be classified by.</summary>
    public static bool CanClassify(ElementKind k) => Metamodel.IsStructuralClassifier(k) && k != ElementKind.ConstraintBlock;

    public static bool IsInstance(Element? e) => e?.Kind == ElementKind.InstanceSpecification;

    public static IReadOnlyList<string> ClassifierIds(Element instance) =>
        instance.Get(InstanceFields.Classifier).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The classifiers that still exist, in the order given.</summary>
    public static IReadOnlyList<Element> Classifiers(ModelRepository m, Element instance) =>
        ClassifierIds(instance).Select(m.Find).OfType<Element>().ToList();

    /// <summary>The first classifier that is a block or class (the subject of analyses), else the first one.</summary>
    public static Element? MainClassifier(ModelRepository m, Element instance)
    {
        var cs = Classifiers(m, instance);
        return cs.FirstOrDefault(c => c.Kind is ElementKind.Block or ElementKind.Class or ElementKind.Component) ?? cs.FirstOrDefault();
    }

    public static void SetClassifiers(ModelRepository m, Element instance, IEnumerable<Element> classifiers) =>
        m.Set(instance, InstanceFields.Classifier, string.Join(",", classifiers.Select(c => c.Id).Distinct()));

    /// <summary>"vehicle1 : Vehicle" (UML instance label; drawn underlined).</summary>
    public static string Label(ModelRepository m, Element instance)
    {
        var types = string.Join(", ", Classifiers(m, instance).Select(c => c.Name));
        return types.Length == 0 ? instance.Name : $"{instance.Name} : {types}";
    }

    /// <summary>
    /// The structural features of a classifier that slots can give values to: its own properties, then inherited ones
    /// (a redefinition by name in a specialization hides the general one). Constraint usages are not features of instances.
    /// </summary>
    public static IReadOnlyList<Element> Features(ModelRepository m, Element classifier)
    {
        var list = new List<Element>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<Element>([classifier]);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (!seen.Add(c)) continue;
            foreach (var p in c.OwnedElements.Where(p => p.Kind == ElementKind.Property))
            {
                if (m.ResolveType(p)?.Kind == ElementKind.ConstraintBlock) continue;
                if (names.Add(p.Name)) list.Add(p);
            }
            foreach (var g in m.General(c)) queue.Enqueue(g);
        }
        return list;
    }

    /// <summary>The features of all the instance's classifiers (no duplicates by name).</summary>
    public static IReadOnlyList<Element> InstanceFeatures(ModelRepository m, Element instance)
    {
        var list = new List<Element>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Classifiers(m, instance))
            foreach (var f in Features(m, c))
                if (names.Add(f.Name)) list.Add(f);
        return list;
    }

    /// <summary>Is a classifier <paramref name="c"/> the same as, or a specialization of, <paramref name="general"/>?</summary>
    public static bool Conforms(ModelRepository m, Element c, Element general)
    {
        var seen = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<Element>([c]);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            if (ReferenceEquals(x, general)) return true;
            if (!seen.Add(x)) continue;
            foreach (var g in m.General(x)) stack.Push(g);
        }
        return false;
    }

    /// <summary>Does the feature hold instances (a part or reference property typed by a block, class, component …)?</summary>
    public static bool IsInstanceValued(ModelRepository m, Element feature) =>
        m.ResolveType(feature) is { } t && t.Kind is ElementKind.Block or ElementKind.Class or ElementKind.Component or ElementKind.InterfaceBlock or ElementKind.Actor or ElementKind.Interface;

    public static bool IsEnumValued(ModelRepository m, Element feature) => m.ResolveType(feature)?.Kind == ElementKind.Enumeration;

    /// <summary>The feature's unit (its own, else its value type's).</summary>
    public static string? UnitOf(ModelRepository m, Element feature) => CoSimulation.UnitOf(m, feature);

    /// <summary>(lower, upper) of a multiplicity; upper is null for "*". "1" when empty.</summary>
    public static (int Lower, int? Upper) Bounds(string? multiplicity)
    {
        var s = string.IsNullOrWhiteSpace(multiplicity) ? "1" : multiplicity.Trim();
        var parts = s.Split("..", StringSplitOptions.TrimEntries);
        static int? N(string x) => x == "*" ? null : int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (parts.Length == 1) return parts[0] == "*" ? (0, null) : (N(parts[0]) ?? 1, N(parts[0]) ?? 1);
        return (N(parts[0]) ?? 0, N(parts[1]));
    }

    // ================================================================== slots

    public static IEnumerable<Element> Slots(Element instance) => instance.OwnedElements.Where(c => c.Kind == ElementKind.Slot);

    public static IEnumerable<Element> NestedInstances(Element instance) => instance.OwnedElements.Where(c => c.Kind == ElementKind.InstanceSpecification);

    public static Element? FeatureOf(ModelRepository m, Element slot) => m.Find(slot.Get(InstanceFields.Feature));

    /// <summary>The slot of an instance for a feature (matched by id, then by name for features inherited from a copy).</summary>
    public static Element? SlotFor(Element instance, Element feature) =>
        Slots(instance).FirstOrDefault(s => s.Get(InstanceFields.Feature) == feature.Id);

    public static string FeatureName(ModelRepository m, Element slot) => FeatureOf(m, slot)?.Name ?? "<missing feature>";

    public static SlotValueKind KindOf(Element slot) => slot.Get(InstanceFields.ValueKind) switch
    {
        "enum" => SlotValueKind.Enum,
        "instance" => SlotValueKind.Instance,
        "expression" => SlotValueKind.Expression,
        _ => SlotValueKind.Literal,
    };

    public static string KindCode(SlotValueKind k) => k switch
    {
        SlotValueKind.Enum => "enum",
        SlotValueKind.Instance => "instance",
        SlotValueKind.Expression => "expression",
        _ => "",
    };

    /// <summary>Referenced element ids (instances or an enumeration literal).</summary>
    public static IReadOnlyList<string> ValueIds(Element slot) =>
        KindOf(slot) is SlotValueKind.Instance or SlotValueKind.Enum
            ? slot.Get(InstanceFields.Value).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

    public static IReadOnlyList<Element> ReferencedInstances(ModelRepository m, Element slot) =>
        KindOf(slot) == SlotValueKind.Instance ? ValueIds(slot).Select(m.Find).OfType<Element>().ToList() : [];

    /// <summary>The value as shown in tables and on shapes: "1500 kg", "Red", "engine, tank", "= mass * 2".</summary>
    public static string DisplayValue(ModelRepository m, Element slot)
    {
        var raw = slot.Get(InstanceFields.Value);
        return KindOf(slot) switch
        {
            SlotValueKind.Enum or SlotValueKind.Instance => string.Join(", ", ValueIds(slot).Select(id => m.Find(id)?.Name ?? "<missing>")),
            SlotValueKind.Expression => "= " + raw,
            _ => raw,
        };
    }

    /// <summary>"mass = 1500 kg" for slot compartments.</summary>
    public static string SlotLine(ModelRepository m, Element slot) => $"{FeatureName(m, slot)} = {DisplayValue(m, slot)}";

    /// <summary>The value an instance has for a feature: its slot's, else the feature's default ("" when none).</summary>
    public static string EffectiveDisplay(ModelRepository m, Element instance, Element feature) =>
        SlotFor(instance, feature) is { } s ? DisplayValue(m, s) : "";

    /// <summary>A literal slot's number in the feature's unit (null when the value is not numeric or the units don't convert).</summary>
    public static double? NumericValue(ModelRepository m, Element slot)
    {
        if (KindOf(slot) != SlotValueKind.Literal) return null;
        var f = FeatureOf(m, slot);
        return ParseNumber(slot.Get(InstanceFields.Value), f == null ? null : UnitOf(m, f), out _);
    }

    [GeneratedRegex(@"^\s*(?<v>[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*(?<u>.*?)\s*$")]
    private static partial Regex NumberRe();

    /// <summary>
    /// Reads "number [unit]" and converts it to <paramref name="unit"/> when both have units. Null when the text is not a number;
    /// <paramref name="error"/> says why when it has a unit that is unknown or does not convert.
    /// </summary>
    public static double? ParseNumber(string text, string? unit, out string? error)
    {
        error = null;
        var match = NumberRe().Match(text ?? "");
        if (!match.Success || !double.TryParse(match.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
        var u = match.Groups["u"].Value.Trim();
        if (u.StartsWith('[') && u.EndsWith(']')) u = u[1..^1].Trim();
        if (u.Length == 0) return v;
        if (Units.Parse(u) is null) { error = $"unknown unit '{u}'"; return null; }
        if (string.IsNullOrEmpty(unit)) return Units.Parse(u)!.ToSi(v);
        if (Units.Convert(v, u, unit) is { } c) return c;
        error = $"{u} does not convert to {unit}";
        return null;
    }

    public static string FormatNumber(double v) => v.ToString("G15", CultureInfo.InvariantCulture);

    private static bool IsNumericType(ModelRepository m, Element feature) =>
        feature.TypeRef is "Real" or "Integer" or "UnlimitedNatural" || UnitOf(m, feature) != null;

    /// <summary>
    /// Reads typed text into a slot value for <paramref name="feature"/>: "=expr" is an expression, enumeration literals by
    /// name, instances by name (comma separated) for parts and references, and literals with units converted to the feature's
    /// unit ("1.5 t" for a mass in kg becomes "1500 kg"). Throws <see cref="InvalidOperationException"/> with the reason when
    /// the text doesn't fit.
    /// </summary>
    public static (SlotValueKind Kind, string Value) Parse(ModelRepository m, Element feature, string text, Element? context = null)
    {
        text = text.Trim();
        if (text.StartsWith('='))
        {
            var expr = text[1..].Trim();
            if (expr.Length == 0) throw new InvalidOperationException("Empty expression");
            return (SlotValueKind.Expression, expr);
        }
        if (IsEnumValued(m, feature))
        {
            var en = m.ResolveType(feature)!;
            var lit = en.OwnedElements.FirstOrDefault(l => l.Kind == ElementKind.EnumerationLiteral && l.Name == text)
                      ?? en.OwnedElements.FirstOrDefault(l => l.Kind == ElementKind.EnumerationLiteral && string.Equals(l.Name, text, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException($"'{text}' is not a literal of {en.Name} ({string.Join(", ", en.OwnedElements.Where(l => l.Kind == ElementKind.EnumerationLiteral).Select(l => l.Name))})");
            return (SlotValueKind.Enum, lit.Id);
        }
        if (IsInstanceValued(m, feature))
        {
            var type = m.ResolveType(feature)!;
            var ids = new List<string>();
            foreach (var name in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var candidates = InstancesOf(m, type).ToList();
                var hit = (context != null ? candidates.FirstOrDefault(i => ReferenceEquals(i.Owner, context) && i.Name == name) : null)
                          ?? candidates.FirstOrDefault(i => i.Name == name)
                          ?? candidates.FirstOrDefault(i => string.Equals(i.QualifiedName, name, StringComparison.Ordinal))
                          ?? candidates.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))
                          ?? throw new InvalidOperationException($"No instance of {type.Name} called '{name}'");
                ids.Add(hit.Id);
            }
            return (SlotValueKind.Instance, string.Join(",", ids.Distinct()));
        }
        var unit = UnitOf(m, feature);
        var number = ParseNumber(text, unit, out var error);
        if (error != null) throw new InvalidOperationException(error);
        if (number is { } v)
        {
            if (feature.TypeRef == "Integer" && Math.Abs(v - Math.Round(v)) > 1e-9) throw new InvalidOperationException($"{feature.Name} is an Integer");
            return (SlotValueKind.Literal, unit != null ? $"{FormatNumber(v)} {unit}" : NumberRe().Match(text).Groups["u"].Value.Trim().Length == 0 ? FormatNumber(v) : text);
        }
        if (IsNumericType(m, feature)) throw new InvalidOperationException($"{feature.Name} expects a number{(unit != null ? $" in {unit}" : "")} (or =expression)");
        if (feature.TypeRef == "Boolean")
        {
            if (!bool.TryParse(text, out var b)) throw new InvalidOperationException($"{feature.Name} expects true or false");
            return (SlotValueKind.Literal, b ? "true" : "false");
        }
        return (SlotValueKind.Literal, text);
    }

    /// <summary>
    /// Sets the value an instance gives a feature (creating the slot when needed); empty text removes the slot. One undo step.
    /// Returns the slot, or null when it was removed.
    /// </summary>
    public static Element? SetValue(ModelRepository m, Element instance, Element feature, string? text)
    {
        var existing = SlotFor(instance, feature);
        if (string.IsNullOrWhiteSpace(text))
        {
            if (existing != null) m.Delete(existing);
            return null;
        }
        var (kind, value) = Parse(m, feature, text, instance);
        return SetSlot(m, instance, feature, kind, value);
    }

    /// <summary>Writes a slot as given (no parsing). One undo step.</summary>
    public static Element SetSlot(ModelRepository m, Element instance, Element feature, SlotValueKind kind, string value)
    {
        var slot = SlotFor(instance, feature);
        if (slot != null && slot.Get(InstanceFields.Value) == value && KindOf(slot) == kind) return slot;
        using (m.History.Transaction($"Set {instance.DisplayName}.{feature.Name}"))
        {
            if (slot == null)
            {
                slot = m.Create(instance, ElementKind.Slot);
                m.Set(slot, InstanceFields.Feature, feature.Id);
            }
            m.Set(slot, InstanceFields.ValueKind, KindCode(kind));
            m.Set(slot, InstanceFields.Value, value);
        }
        return slot;
    }

    /// <summary>Points a slot at another feature (after its classifier changed). One undo step.</summary>
    public static void Remap(ModelRepository m, Element slot, Element feature)
    {
        using (m.History.Transaction($"Remap slot to {feature.Name}"))
        {
            m.Set(slot, InstanceFields.Feature, feature.Id);
            // A literal keeps its text, re-read for the new feature when possible (unit conversion, enum by name).
            if (KindOf(slot) == SlotValueKind.Literal && slot.Owner is { } inst)
            {
                try
                {
                    var (k, v) = Parse(m, feature, slot.Get(InstanceFields.Value), inst);
                    m.Set(slot, InstanceFields.ValueKind, KindCode(k));
                    m.Set(slot, InstanceFields.Value, v);
                }
                catch (InvalidOperationException) { /* kept as written: validation reports the mismatch */ }
            }
        }
    }

    /// <summary>Features a stale slot could be remapped to: the instance's features without a slot, same name first.</summary>
    public static IReadOnlyList<Element> RemapCandidates(ModelRepository m, Element slot)
    {
        if (slot.Owner is not { } inst) return [];
        var used = Slots(inst).Where(s => !ReferenceEquals(s, slot)).Select(s => s.Get(InstanceFields.Feature)).ToHashSet(StringComparer.Ordinal);
        var name = FeatureOf(m, slot)?.Name;
        return InstanceFeatures(m, inst).Where(f => !used.Contains(f.Id))
            .OrderByDescending(f => name != null && f.Name == name).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Is the slot's feature gone, or no longer a feature of the instance's classifiers?</summary>
    public static bool IsStale(ModelRepository m, Element slot)
    {
        if (slot.Owner is not { } inst || FeatureOf(m, slot) is not { } f) return true;
        return !InstanceFeatures(m, inst).Contains(f);
    }

    // ================================================================== instances

    /// <summary>Instances classified by <paramref name="classifier"/> (or, with specializations, by a subtype of it).</summary>
    public static IEnumerable<Element> InstancesOf(ModelRepository m, Element classifier, bool specializations = true) =>
        m.OfKind(ElementKind.InstanceSpecification).Where(i => Classifiers(m, i).Any(c => ReferenceEquals(c, classifier) || specializations && Conforms(m, c, classifier)))
         .OrderBy(i => i.QualifiedName, StringComparer.OrdinalIgnoreCase);

    /// <summary>A free instance name in <paramref name="owner"/>: "vehicle1", "vehicle2" …</summary>
    public static string NextName(Element owner, Element classifier)
    {
        var stem = classifier.Name.Length == 0 ? "instance" : char.ToLowerInvariant(classifier.Name[0]) + classifier.Name[1..].Replace(" ", "");
        var names = owner.OwnedElements.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = 1; ; i++)
            if (!names.Contains(stem + i)) return stem + i;
    }

    /// <summary>Where new instances of a classifier go: next to existing ones, else the classifier's package.</summary>
    public static Element DefaultOwner(ModelRepository m, Element classifier) =>
        InstancesOf(m, classifier, specializations: false).FirstOrDefault(i => i.Owner?.Kind is ElementKind.Package or ElementKind.Model)?.Owner
        ?? classifier.AncestorsAndSelf().Skip(1).FirstOrDefault(a => a.Kind is ElementKind.Package or ElementKind.Model) ?? m.Root;

    /// <summary>Upper bound on nested part instances one "Create instance" builds (very deep or wide structures are cut off).</summary>
    public const int MaxTreeInstances = 500;

    /// <summary>
    /// "Create instance of…": an instance of <paramref name="classifier"/> with a slot for every feature with a default value,
    /// a nested instance per part (as many as the part's lower multiplicity, at least one when it is exactly 1) built the same
    /// way, the parts' slots pointing at them, and links between whole and part typed by the composition when exactly one
    /// association connects their classifiers. One undo step.
    /// </summary>
    public static Element CreateInstanceTree(ModelRepository m, Element classifier, Element? owner = null, string? name = null)
    {
        if (!CanClassify(classifier.Kind)) throw new InvalidOperationException($"A {Metamodel.DisplayName(classifier.Kind)} can't classify an instance");
        owner ??= DefaultOwner(m, classifier);
        if (!Metamodel.CanOwn(owner.Kind, ElementKind.InstanceSpecification)) owner = m.DefaultOwnerFor(owner, ElementKind.InstanceSpecification);
        var budget = MaxTreeInstances;
        using (m.History.Transaction($"Create instance of {classifier.DisplayName}"))
        {
            var root = m.Create(owner, ElementKind.InstanceSpecification, name ?? NextName(owner, classifier));
            m.Set(root, InstanceFields.Classifier, classifier.Id);
            budget--;
            Fill(root, classifier, [classifier]);
            return root;
        }

        void Fill(Element inst, Element type, HashSet<Element> path)
        {
            foreach (var f in Features(m, type))
            {
                if (IsInstanceValued(m, f))
                {
                    var partType = m.ResolveType(f)!;
                    if (partType.Kind is not (ElementKind.Block or ElementKind.Class or ElementKind.Component) || path.Contains(partType)) continue;
                    var (lower, upper) = Bounds(f.Multiplicity);
                    var count = Math.Min(lower, upper ?? 20);
                    var children = new List<Element>();
                    for (var i = 0; i < count && budget > 0; i++, budget--)
                    {
                        var childName = count == 1 ? f.Name : f.Name + (i + 1);
                        var child = m.Create(inst, ElementKind.InstanceSpecification, childName);
                        m.Set(child, InstanceFields.Classifier, partType.Id);
                        Fill(child, partType, [.. path, partType]);
                        children.Add(child);
                    }
                    if (children.Count == 0) continue;
                    SetSlot(m, inst, f, SlotValueKind.Instance, string.Join(",", children.Select(c => c.Id)));
                    var assocs = AssociationsBetween(m, type, partType);
                    if (assocs.Count == 1)
                        foreach (var child in children) Connect(m, inst, child, assocs[0]);
                }
                else if (f.Get(FieldKeys.DefaultValue) is { Length: > 0 } dv)
                {
                    try
                    {
                        var (k, v) = Parse(m, f, dv, inst);
                        SetSlot(m, inst, f, k, v);
                    }
                    catch (InvalidOperationException)
                    {
                        // A default that isn't a plain value (e.g. an expression over other values) is kept as an expression.
                        SetSlot(m, inst, f, SlotValueKind.Expression, dv);
                    }
                }
            }
        }
    }

    /// <summary>Deep copy of an instance (nested instances, slots, links among the copies) with references to copied instances remapped. One undo step.</summary>
    public static Element Duplicate(ModelRepository m, Element instance)
    {
        using (m.History.Transaction($"Duplicate {instance.DisplayName}"))
        {
            var map = m.Duplicate([instance], instance.Owner!, "");
            var copy = map[instance];
            if (copy.Owner != null && Metamodel.NeedsName(copy.Kind) && copy.Owner.OwnedElements.Count(c => c.Name == copy.Name) > 1
                && ClassifierIds(instance).FirstOrDefault() is { } cid && m.Find(cid) is { } cls)
                m.Rename(copy, NextName(copy.Owner, cls));
            var byId = map.ToDictionary(kv => kv.Key.Id, kv => kv.Value.Id, StringComparer.Ordinal);
            foreach (var slot in map.Values.Where(e => e.Kind == ElementKind.Slot && KindOf(e) == SlotValueKind.Instance))
            {
                var ids = ValueIds(slot).Select(id => byId.GetValueOrDefault(id, id)).ToList();
                m.Set(slot, InstanceFields.Value, string.Join(",", ids));
            }
            return copy;
        }
    }

    /// <summary>Deletes an instance (with its nested instances, slots and links) and drops references to it from other slots. One undo step.</summary>
    public static void Delete(ModelRepository m, Element instance)
    {
        var gone = instance.Descendants().Prepend(instance).Where(e => e.Kind == ElementKind.InstanceSpecification).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        using (m.History.Transaction($"Delete {instance.DisplayName}"))
        {
            foreach (var slot in m.OfKind(ElementKind.Slot).Where(s => KindOf(s) == SlotValueKind.Instance && !gone.Contains(s.Owner?.Id ?? "") && ValueIds(s).Any(gone.Contains)).ToList())
            {
                var rest = ValueIds(slot).Where(id => !gone.Contains(id)).ToList();
                if (rest.Count == 0) m.Delete(slot);
                else m.Set(slot, InstanceFields.Value, string.Join(",", rest));
            }
            m.Delete(instance);
        }
    }

    // ================================================================== links

    /// <summary>Associations (association, composition, aggregation) connecting the two classifiers or their generalizations, either way round.</summary>
    public static IReadOnlyList<Relationship> AssociationsBetween(ModelRepository m, Element a, Element b) =>
        m.Relationships.Where(r => r.Kind is ElementKind.Association or ElementKind.Composition or ElementKind.Aggregation
                                   && (Conforms(m, a, r.Source) && Conforms(m, b, r.Target) || Conforms(m, b, r.Source) && Conforms(m, a, r.Target)))
         .Distinct().ToList();

    /// <summary>The associations a link between two instances could be an instance of.</summary>
    public static IReadOnlyList<Relationship> MatchingAssociations(ModelRepository m, Element a, Element b) =>
        Classifiers(m, a).SelectMany(ca => Classifiers(m, b).SelectMany(cb => AssociationsBetween(m, ca, cb))).Distinct().ToList();

    public static Relationship? AssociationOf(ModelRepository m, Element link) => m.Find(link.Get(InstanceFields.Association)) as Relationship;

    /// <summary>Types a link by the matching association when exactly one exists; returns the candidates (the caller asks when there are several).</summary>
    public static IReadOnlyList<Relationship> TypeLink(ModelRepository m, Relationship link)
    {
        var candidates = MatchingAssociations(m, link.Source, link.Target);
        if (candidates.Count == 1 && link.Get(InstanceFields.Association) != candidates[0].Id) m.Set(link, InstanceFields.Association, candidates[0].Id);
        return candidates;
    }

    /// <summary>A link between two instances typed by <paramref name="association"/> (null = untyped). One undo step.</summary>
    public static Relationship Connect(ModelRepository m, Element a, Element b, Relationship? association)
    {
        using (m.History.Transaction("Create link"))
        {
            var link = m.Connect(ElementKind.Link, a, b);
            if (association != null) m.Set(link, InstanceFields.Association, association.Id);
            return link;
        }
    }

    /// <summary>"engine : Vehicle–Engine" style label of a link: its name, then its association's.</summary>
    public static string LinkLabel(ModelRepository m, Relationship link)
    {
        var a = AssociationOf(m, link);
        var an = a == null ? "" : a.Name.Length > 0 ? a.Name : $"{a.Source.Name}–{a.Target.Name}";
        return link.Name.Length == 0 ? an : an.Length == 0 ? link.Name : $"{link.Name} : {an}";
    }
}
