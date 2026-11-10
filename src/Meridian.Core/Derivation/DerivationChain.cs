using Meridian.Core.Model;

namespace Meridian.Core.Derivation;

/// <summary>One link of the derivation chain: the element at that stage (null when missing) and the view that shows it.</summary>
/// <param name="CreateNext">For the first missing stage: the derivation that would create it.</param>
public sealed record ChainStep(string Label, Element? Element, Diagram? View, bool Current, DerivationOffer? CreateNext);

/// <summary>
/// The MBSE chain an element sits in: Requirements → Use case → Activity → Sequence → IBD (→ State machine), found through
/// derivation records, «refine» links and behaviour ownership. Shown in the inspector with click-to-open links and
/// "Create next…" for the first missing stage.
/// </summary>
public static class DerivationChain
{
    private static readonly string[] Labels = ["Requirements", "Use case", "Activity", "Sequence", "IBD", "State machine"];

    /// <summary>The stage of <paramref name="e"/> (0 requirements … 5 state machine), or -1 when it is not part of a chain.</summary>
    public static int StageOf(Element e) => e.Kind switch
    {
        ElementKind.Requirement => 0,
        ElementKind.UseCase => 1,
        ElementKind.Activity => 2,
        ElementKind.Interaction => 3,
        ElementKind.Block when e.Get(DeriveFields.Key) == "context" && e.Get(DeriveFields.Kind) == DerivationKinds.Id(DerivationKind.StructureFromSequence) => 4,
        ElementKind.StateMachine when DeriveFields.IsDerived(e) => 5,
        _ => -1,
    };

    /// <summary>A diagram stands for the element it shows (its owner, or for derived diagrams the derivation's main element).</summary>
    public static Element? Subject(ModelRepository m, Element e)
    {
        if (e is not Diagram d) return e;
        if (d.Owner is { } o && StageOf(o) >= 0) return o;
        if (d.DiagramType == DiagramType.UseCase) return d.Nodes.Select(n => n.Element).FirstOrDefault(x => x.Kind == ElementKind.UseCase);
        if (d.DiagramType == DiagramType.BlockDefinition && DiagramDerivation.Origin(m, d) is { Kind: DerivationKind.StructureFromSequence } origin)
            return DiagramDerivation.Existing(m, DerivationKind.StructureFromSequence, origin.Source).FirstOrDefault(x => x.Get(DeriveFields.Key) == "context");
        return null;
    }

    public static IReadOnlyList<ChainStep> For(ModelRepository m, Element? selected)
    {
        if (selected == null || !m.Contains(selected) || Subject(m, selected) is not { } e || StageOf(e) is var stage && stage < 0) return [];
        var at = new Element?[6];
        at[stage] = e;
        // Upstream.
        if (stage == 5 && DiagramDerivation.Origin(m, e) is { } origin)
        {
            if (origin.Source.Kind == ElementKind.Interaction) at[3] = origin.Source;
            else if (origin.Source.Kind == ElementKind.Activity) at[2] = origin.Source;
        }
        if (stage == 4) at[3] = Up(m, e, 4);
        for (var s = 3; s >= 1; s--)
            if (at[s] is { } x && at[s - 1] == null) at[s - 1] = Up(m, x, s);
        // Downstream (not past a state machine).
        if (stage < 5)
            for (var s = stage; s < 4; s++)
            {
                if (at[s] is not { } x) break;
                at[s + 1] ??= Down(m, x, s);
            }
        var stm = at[5] ?? (at[3] is { } ia ? DiagramDerivation.Existing(m, DerivationKind.StateMachineFromSequence, ia).FirstOrDefault(x => x.Kind == ElementKind.StateMachine) : null);
        at[5] = stm;

        var steps = new List<ChainStep>();
        var offered = false;
        for (var s = 0; s < 6; s++)
        {
            if (s == 5 && at[5] == null) break;
            DerivationOffer? next = null;
            if (at[s] == null && !offered && s > 0 && at[s - 1] is { } prev)
            {
                next = s switch
                {
                    1 => DiagramDerivation.Offers(m, prev.Kind == ElementKind.Requirement ? prev.Owner ?? prev : prev).FirstOrDefault(o => o.Kind == DerivationKind.UseCasesFromRequirements),
                    2 => DiagramDerivation.Offers(m, prev).FirstOrDefault(o => o.Kind == DerivationKind.ActivityFromUseCase),
                    3 => DiagramDerivation.Offers(m, prev).FirstOrDefault(o => o.Kind == DerivationKind.SequenceFromActivity),
                    4 => DiagramDerivation.Offers(m, prev).FirstOrDefault(o => o.Kind == DerivationKind.StructureFromSequence),
                    _ => null,
                };
                offered = next != null;
            }
            steps.Add(new ChainStep(Labels[s], at[s], at[s] is { } x ? ViewOf(m, x) : null, s == stage, next));
        }
        return steps;
    }

    private static Element? Up(ModelRepository m, Element x, int stage) => stage switch
    {
        1 => m.Outgoing(x, ElementKind.Refine).Select(r => r.Target).FirstOrDefault(t => t.Kind == ElementKind.Requirement)
             ?? m.Incoming(x, ElementKind.Refine).Select(r => r.Source).FirstOrDefault(t => t.Kind == ElementKind.Requirement),
        2 => DiagramDerivation.Origin(m, x) is { Kind: DerivationKind.ActivityFromUseCase } o ? o.Source : x.Owner?.Kind == ElementKind.UseCase ? x.Owner : null,
        3 => DiagramDerivation.Origin(m, x) is { Kind: DerivationKind.SequenceFromActivity } o ? o.Source : null,
        4 => DiagramDerivation.Origin(m, x) is { Kind: DerivationKind.StructureFromSequence } o ? o.Source : null,
        _ => null,
    };

    private static Element? Down(ModelRepository m, Element x, int stage) => stage switch
    {
        0 => m.Incoming(x, ElementKind.Refine).Select(r => r.Source).FirstOrDefault(s => s.Kind == ElementKind.UseCase),
        1 => DiagramDerivation.Existing(m, DerivationKind.ActivityFromUseCase, x).FirstOrDefault(e => e.Kind == ElementKind.Activity)
             ?? x.OwnedElements.FirstOrDefault(e => e.Kind == ElementKind.Activity),
        2 => DiagramDerivation.Existing(m, DerivationKind.SequenceFromActivity, x).FirstOrDefault(e => e.Kind == ElementKind.Interaction),
        3 => DiagramDerivation.Existing(m, DerivationKind.StructureFromSequence, x).FirstOrDefault(e => e.Get(DeriveFields.Key) == "context"),
        _ => null,
    };

    /// <summary>The diagram to open for a chain element: its own diagram, or one that shows it.</summary>
    public static Diagram? ViewOf(ModelRepository m, Element x) =>
        x.OwnedElements.OfType<Diagram>().FirstOrDefault()
        ?? m.Diagrams.FirstOrDefault(d => d.Get(DeriveFields.Source) is { Length: > 0 } && d.Shows(x))
        ?? m.Diagrams.FirstOrDefault(d => d.Shows(x));
}
