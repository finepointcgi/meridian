using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Derivation;

/// <summary>
/// "Create diagrams from diagrams": rule-based MBSE derivation steps (requirements → use cases → activity → sequence →
/// BDD / IBD → state machine, plus satisfy / verify proposals). Each step plans what it would create (<see cref="Plan"/>),
/// the plan is reviewed, and <see cref="Apply"/> makes it one undo step. Derived elements remember their source, the kind
/// of derivation, a stable key and a fingerprint of the source, so a derived diagram knows when its source changed and
/// "update from source" adds, changes and removes only what the derivation itself made.
/// </summary>
public static partial class DiagramDerivation
{
    // ================================================================== what can be derived from an element

    /// <summary>The derivations that can run on <paramref name="e"/> (a diagram stands for the activity / interaction / package it shows).</summary>
    public static IReadOnlyList<DerivationOffer> Offers(ModelRepository m, Element? e)
    {
        if (e == null || !m.Contains(e)) return [];
        var src = e is Diagram d ? d.DiagramType switch
        {
            DiagramType.Activity or DiagramType.Sequence => d.Owner,
            DiagramType.Requirement or DiagramType.UseCase => d.Owner,
            _ => null,
        } : e;
        if (src == null) return [];
        var list = new List<DerivationOffer>();
        void Add(DerivationKind k, Element source, Element? target = null)
        {
            var exists = k != DerivationKind.SatisfyAndVerify && Existing(m, k, source, target).Any();
            var label = DerivationKinds.Label(k);
            if (k is DerivationKind.StateMachineFromSequence or DerivationKind.StateMachineFromActivity)
                label = e.Kind is ElementKind.Block ? $"Create state machine from {source.DisplayName}" : $"Create state machine for {target!.DisplayName}";
            if (exists) label = $"Update {DerivationKinds.Product(k)} from {Metamodel.DisplayName(source.Kind).ToLowerInvariant()}";
            if (exists && k is DerivationKind.StateMachineFromSequence or DerivationKind.StateMachineFromActivity) label += $" ({target!.DisplayName})";
            list.Add(new DerivationOffer(k, source, target, label, exists));
        }
        switch (src.Kind)
        {
            case ElementKind.Requirement:
            case ElementKind.Package or ElementKind.Model when RequirementsToUseCases.Requirements(src).Count > 0:
                Add(DerivationKind.UseCasesFromRequirements, src);
                Add(DerivationKind.SatisfyAndVerify, src);
                break;
            case ElementKind.UseCase:
                Add(DerivationKind.ActivityFromUseCase, src);
                break;
            case ElementKind.Activity:
                Add(DerivationKind.SequenceFromActivity, src);
                foreach (var t in ToStateMachine.Candidates(m, src)) Add(DerivationKind.StateMachineFromActivity, src, t);
                break;
            case ElementKind.Interaction:
                Add(DerivationKind.StructureFromSequence, src);
                foreach (var t in ToStateMachine.Candidates(m, src)) Add(DerivationKind.StateMachineFromSequence, src, t);
                break;
            case ElementKind.Block or ElementKind.Class or ElementKind.Component:
                foreach (var ia in m.OfKind(ElementKind.Interaction).Where(ia => ToStateMachine.Candidates(m, ia).Contains(src)))
                    Add(DerivationKind.StateMachineFromSequence, ia, src);
                foreach (var act in m.OfKind(ElementKind.Activity).Where(a => ToStateMachine.Candidates(m, a).Contains(src)))
                    Add(DerivationKind.StateMachineFromActivity, act, src);
                if (m.OfKind(ElementKind.Requirement).Any()) Add(DerivationKind.SatisfyAndVerify, src);
                break;
        }
        return list;
    }

    // ================================================================== fingerprints

    /// <summary>Hash of the parts of the source the derivation reads; stored on derived items, compared to detect "source changed".</summary>
    public static string SourceFingerprint(ModelRepository m, DerivationKind kind, Element source, Element? target = null) => DeriveFields.Hash(kind switch
    {
        DerivationKind.UseCasesFromRequirements or DerivationKind.SatisfyAndVerify => RequirementsToUseCases.Digest(source),
        DerivationKind.ActivityFromUseCase => UseCaseToActivity.Digest(m, source),
        DerivationKind.SequenceFromActivity => ActivityToSequence.Digest(m, source),
        DerivationKind.StateMachineFromActivity => ActivityToSequence.Digest(m, source) + "\nT " + target?.Id,
        DerivationKind.StructureFromSequence => InteractionReader.Digest(m, source),
        _ => InteractionReader.Digest(m, source, target),
    });

    /// <summary>The derivation that made <paramref name="e"/>: kind, source and target, or null when it was not derived (or its source is gone).</summary>
    public static (DerivationKind Kind, Element Source, Element? Target)? Origin(ModelRepository m, Element e)
    {
        if (!DeriveFields.IsDerived(e) || DerivationKinds.Parse(e.Get(DeriveFields.Kind)) is not { } k || m.Find(e.Get(DeriveFields.Source)) is not { } src) return null;
        return (k, src, m.Find(e.Get(DeriveFields.Target)));
    }

    /// <summary>True when the source of a derived diagram (or element) changed since it was derived or last updated.</summary>
    public static bool IsStale(ModelRepository m, Element derived)
    {
        if (Origin(m, derived) is not { } o || o.Kind == DerivationKind.SatisfyAndVerify) return false;
        return derived.Get(DeriveFields.Fingerprint) != SourceFingerprint(m, o.Kind, o.Source, o.Target);
    }

    /// <summary>Elements an earlier run of this derivation made, by key.</summary>
    public static IEnumerable<Element> Existing(ModelRepository m, DerivationKind kind, Element source, Element? target = null)
    {
        var id = DerivationKinds.Id(kind);
        return m.AllElements.Where(e => e.Get(DeriveFields.Source) == source.Id && e.Get(DeriveFields.Kind) == id && e.Get(DeriveFields.Key).Length > 0
                                        && (target == null || e.Get(DeriveFields.Target) == target.Id));
    }

    /// <summary>The diagrams an earlier run made (the derivation's main views).</summary>
    public static IEnumerable<Diagram> ExistingDiagrams(ModelRepository m, DerivationKind kind, Element source, Element? target = null) =>
        Existing(m, kind, source, target).OfType<Diagram>();

    // ================================================================== planning

    public static DerivationPlan Plan(ModelRepository m, DerivationKind kind, Element source, Element? target = null, DerivationOptions? options = null)
    {
        if (!m.Contains(source)) throw new ArgumentException("The source is not part of this model", nameof(source));
        if (kind is DerivationKind.StateMachineFromSequence or DerivationKind.StateMachineFromActivity && target == null)
            throw new ArgumentException("A state machine is derived for a block: pass the block as the target", nameof(target));
        var o = options ?? new DerivationOptions();
        var plan = new DerivationPlan
        {
            Kind = kind, Source = source, Target = target, ProposalsOnly = kind == DerivationKind.SatisfyAndVerify,
            Title = DerivationKinds.Label(kind) + (target != null ? $" for {target.DisplayName}" : "") + $": {source.DisplayName}",
            Fingerprint = SourceFingerprint(m, kind, source, target),
        };
        var b = new PlanBuilder(plan) { Model = m };
        switch (kind)
        {
            case DerivationKind.UseCasesFromRequirements: RequirementsToUseCases.Build(m, b, o); break;
            case DerivationKind.ActivityFromUseCase: UseCaseToActivity.Build(m, b, o); break;
            case DerivationKind.SequenceFromActivity: ActivityToSequence.Build(m, b, o); break;
            case DerivationKind.StructureFromSequence: SequenceToStructure.Build(m, b, o); break;
            case DerivationKind.StateMachineFromSequence or DerivationKind.StateMachineFromActivity: ToStateMachine.Build(m, b, o); break;
            default: SatisfyAndVerify.Build(m, b, o); break;
        }
        Diff(m, plan);
        return plan;
    }

    /// <summary>
    /// Matches the fresh plan against what an earlier run made (by key): unchanged items become "same", differing ones
    /// "change", and earlier items the plan no longer has "remove". Items the user edited, or that carry things the user
    /// added, are offered but not ticked.
    /// </summary>
    private static void Diff(ModelRepository m, DerivationPlan plan)
    {
        var earlier = Existing(m, plan.Kind, plan.Source, plan.Target).GroupBy(e => e.Get(DeriveFields.Key)).ToDictionary(g => g.Key, g => g.First());
        plan.IsUpdate = earlier.Count > 0;
        if (!plan.IsUpdate) return;
        var byKey = plan.Items.ToDictionary(i => i.Key);
        string? Resolve(string key) => byKey.TryGetValue(key, out var i) ? i.Existing?.Id : null;

        foreach (var item in plan.Items.Where(i => i.Change == DerivedChange.Add))
        {
            if (!earlier.Remove(item.Key, out var e) || e.Kind != item.Kind || item.IsDiagram && (e as Diagram)?.DiagramType != item.DiagramType) continue;
            item.Existing = e;
            if (item.IsDiagram) { item.Change = DerivedChange.Same; continue; }
        }
        foreach (var item in plan.Items.Where(i => i.Existing != null && i.Change == DerivedChange.Add))
        {
            var e = item.Existing!;
            foreach (var (k, v) in item.Fields)
                if (e.Get(k) != v) item.ChangedFields.Add(k);
            foreach (var (k, refKey) in item.FieldRefs)
                if (Resolve(refKey) is not { } id || e.Get(k) != id) item.ChangedFields.Add(k);
            if (item.ChangedFields.Count == 0) { item.Change = DerivedChange.Same; continue; }
            item.Change = DerivedChange.Change;
            if (DeriveFields.IsEdited(e)) { item.Selected = false; item.Note = "edited since it was derived: not ticked"; }
        }
        if (plan.ProposalsOnly) return;
        foreach (var e in earlier.Values.Where(e => !earlier.Values.Any(a => !ReferenceEquals(a, e) && a.IsAncestorOf(e))))
        {
            var edited = DeriveFields.IsEdited(e);
            var userStuff = HasUserAdditions(m, e);
            var item = new DerivedItem
            {
                Key = e.Get(DeriveFields.Key), Kind = e.Kind, DiagramType = (e as Diagram)?.DiagramType, Existing = e, Change = DerivedChange.Remove,
                Source = e is Relationship rs ? new ItemRef(null, rs.Source) : (ItemRef?)null, Target = e is Relationship rt ? new ItemRef(null, rt.Target) : (ItemRef?)null,
                Selected = !edited && !userStuff,
                Note = edited ? "edited since it was derived: kept unless ticked" : userStuff ? "has things you added: kept unless ticked" : null,
            };
            plan.Items.Add(item);
        }
    }

    /// <summary>Relationships or owned elements that a person (not this derivation) attached to <paramref name="e"/>.</summary>
    private static bool HasUserAdditions(ModelRepository m, Element e)
    {
        return e.Descendants().Any(x => !DeriveFields.IsDerived(x)) || e is not Relationship && m.RelationshipsOf(e).Any(r => !DeriveFields.IsDerived(r));
    }
}
