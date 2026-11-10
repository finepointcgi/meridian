using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Derivation;

/// <summary>The MBSE derivation steps: each reads one source and creates the next view, keeping trace links.</summary>
public enum DerivationKind
{
    UseCasesFromRequirements,
    ActivityFromUseCase,
    SequenceFromActivity,
    StructureFromSequence,
    StateMachineFromSequence,
    StateMachineFromActivity,
    SatisfyAndVerify,
}

public static class DerivationKinds
{
    /// <summary>The value stored in <see cref="DeriveFields.Kind"/>.</summary>
    public static string Id(DerivationKind k) => k switch
    {
        DerivationKind.UseCasesFromRequirements => "usecases-from-requirements",
        DerivationKind.ActivityFromUseCase => "activity-from-usecase",
        DerivationKind.SequenceFromActivity => "sequence-from-activity",
        DerivationKind.StructureFromSequence => "structure-from-sequence",
        DerivationKind.StateMachineFromSequence => "statemachine-from-sequence",
        DerivationKind.StateMachineFromActivity => "statemachine-from-activity",
        _ => "satisfy-verify",
    };

    public static DerivationKind? Parse(string id) =>
        Enum.GetValues<DerivationKind>().Cast<DerivationKind?>().FirstOrDefault(k => Id(k!.Value) == id);

    /// <summary>What the derivation creates, for menus ("Create activity from use case").</summary>
    public static string Label(DerivationKind k) => k switch
    {
        DerivationKind.UseCasesFromRequirements => "Create use cases from requirements",
        DerivationKind.ActivityFromUseCase => "Create activity from use case",
        DerivationKind.SequenceFromActivity => "Create sequence from activity",
        DerivationKind.StructureFromSequence => "Create BDD + IBD from sequence",
        DerivationKind.StateMachineFromSequence => "Create state machine from sequence",
        DerivationKind.StateMachineFromActivity => "Create state machine from activity",
        _ => "Propose satisfy and verify links",
    };

    /// <summary>Short name of what is made ("activity"), for the status bar.</summary>
    public static string Product(DerivationKind k) => k switch
    {
        DerivationKind.UseCasesFromRequirements => "use cases",
        DerivationKind.ActivityFromUseCase => "activity",
        DerivationKind.SequenceFromActivity => "sequence",
        DerivationKind.StructureFromSequence => "BDD + IBD",
        DerivationKind.StateMachineFromSequence or DerivationKind.StateMachineFromActivity => "state machine",
        _ => "satisfy / verify links",
    };

    /// <summary>Derivations where the optional AI fill-in has something to add.</summary>
    public static bool SupportsAi(DerivationKind k) => k is DerivationKind.ActivityFromUseCase or DerivationKind.SequenceFromActivity;
}

/// <summary>What an update does to one derived item.</summary>
public enum DerivedChange { Add, Change, Remove, Same, Reuse }

/// <summary>A reference from one planned item to another: a key of the same plan, or an element that already exists.</summary>
public readonly record struct ItemRef(string? Key, Element? Element)
{
    public static implicit operator ItemRef(Element e) => new(null, e);
    public static ItemRef To(string key) => new(key, null);
    public override string ToString() => Key ?? Element?.Id ?? "";
}

/// <summary>Where a planned element is drawn: on the diagram with key <paramref name="DiagramKey"/>; bounds null = laid out automatically.</summary>
public sealed record PlannedShape(string DiagramKey, Bounds? Bounds = null, string? ParentKey = null);

/// <summary>One element, relationship or diagram the derivation would create, change, remove or reuse.</summary>
public sealed class DerivedItem
{
    public required string Key { get; init; }
    public required ElementKind Kind { get; init; }
    public DiagramType? DiagramType { get; init; }

    /// <summary>Fields to write (the name included). Values compare against the existing element on update.</summary>
    public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);

    /// <summary>Fields whose value is the id of another planned item (a part typed by a block the plan creates): field → item key.</summary>
    public Dictionary<string, string> FieldRefs { get; } = new(StringComparer.Ordinal);

    public ItemRef? Owner { get; init; }
    public ItemRef? Source { get; init; }
    public ItemRef? Target { get; init; }

    /// <summary>The element this item is (on update, or when an existing element is reused).</summary>
    public Element? Existing { get; set; }

    public DerivedChange Change { get; set; } = DerivedChange.Add;
    public bool Selected { get; set; } = true;

    /// <summary>Why it is unticked or what to know ("edited since derived", "written by AI").</summary>
    public string? Note { get; set; }

    /// <summary>Fields that differ on a change.</summary>
    public List<string> ChangedFields { get; } = new();

    public PlannedShape? Shape { get; set; }

    /// <summary>Created only when a created relationship uses it (actors of use cases that were unticked are left out).</summary>
    public bool OnlyIfUsed { get; set; }

    /// <summary>Messages: vertical position on the sequence diagram.</summary>
    public double? MessageY { get; set; }

    /// <summary>Diagrams laid out by <see cref="AutoDiagrammer"/> (use case, BDD, IBD, state machine) rather than by the rule.</summary>
    public bool AutoLayout { get; init; }

    public string Name
    {
        get => Fields.TryGetValue(FieldKeys.Name, out var n) ? n : Existing?.Name ?? "";
        set => Fields[FieldKeys.Name] = value;
    }

    public bool IsRelationship => Metamodel.IsRelationship(Kind);
    public bool IsDiagram => Kind == ElementKind.Diagram;

    /// <summary>Shown in the review list (reused elements are referenced, not changed).</summary>
    public bool Listed => Change is DerivedChange.Add or DerivedChange.Change or DerivedChange.Remove;

    /// <summary>One line for the review list, e.g. "Action “Check brakes”" or "«refine» Drive → REQ-002".</summary>
    public string Label(DerivationPlan plan)
    {
        if (IsDiagram) return $"{Metamodel.DisplayName(DiagramType ?? Model.DiagramType.Class)} “{Name}”";
        if (IsRelationship)
        {
            var kw = Metamodel.Style(Kind).Keyword is { } k ? $"«{k}»" : Metamodel.DisplayName(Kind);
            var label = Kind == ElementKind.Message && Name.Length > 0 ? $"Message {Name}:" : kw;
            return $"{label} {plan.NameOf(Source)} → {plan.NameOf(Target)}".Trim();
        }
        var name = Name.Length > 0 ? $" “{Name}”" : "";
        return Metamodel.DisplayName(Kind) + name;
    }
}

/// <summary>
/// A derivation's proposal: the elements, relationships and diagrams to create (or, on update, the differences against
/// what the previous run made), with warnings. Nothing changes until <see cref="DiagramDerivation.Apply"/>.
/// </summary>
public sealed class DerivationPlan
{
    public required DerivationKind Kind { get; init; }
    public required Element Source { get; init; }

    /// <summary>The block a state machine is derived for.</summary>
    public Element? Target { get; init; }

    public required string Title { get; init; }
    public List<DerivedItem> Items { get; } = new();
    public List<string> Warnings { get; } = new();

    /// <summary>Hash of the source content this plan was computed from.</summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>True when an earlier run exists: the items are the differences to it.</summary>
    public bool IsUpdate { get; set; }

    /// <summary>Proposals only (satisfy / verify): nothing earlier is ever offered for removal.</summary>
    public bool ProposalsOnly { get; init; }

    /// <summary>The options the plan was made with (AI fill-in results), so it can be recomputed, e.g. on a copy for a preview.</summary>
    public DerivationOptions Options { get; init; } = new();

    /// <summary>Which AI fill-in produced part of this plan, or null.</summary>
    public string? AiNote { get; set; }

    public DerivedItem? this[string key] => Items.FirstOrDefault(i => i.Key == key);

    public IEnumerable<DerivedItem> Listed => Items.Where(i => i.Listed);
    public int Count(DerivedChange c) => Items.Count(i => i.Change == c);
    public bool HasChanges => Items.Any(i => i.Listed);

    public string NameOf(ItemRef? r)
    {
        if (r is not { } x) return "";
        if (x.Element is { } e) return e.Kind == ElementKind.Requirement && e.Get(FieldKeys.RequirementId) is { Length: > 0 } id ? id : e.DisplayName;
        return this[x.Key!] is { } i ? i.Name.Length > 0 ? i.Name : Metamodel.DisplayName(i.Kind) : x.Key!;
    }

    /// <summary>"3 to add, 1 to change, 2 to remove", or "Up to date".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Count(DerivedChange.Add) is var a and > 0) parts.Add($"{a} to add");
            if (Count(DerivedChange.Change) is var c and > 0) parts.Add($"{c} to change");
            if (Count(DerivedChange.Remove) is var r and > 0) parts.Add($"{r} to remove");
            return parts.Count == 0 ? "Up to date: nothing to change" : string.Join(", ", parts);
        }
    }
}

/// <summary>Options for planning: the AI fill-in's results (validated) replace what the rules would infer.</summary>
public sealed class DerivationOptions
{
    /// <summary>Use case → activity: steps to use instead of parsing the use case's description (same mini-language).</summary>
    public IReadOnlyList<string>? Steps { get; init; }

    /// <summary>Activity → sequence: message names by message key ("msg:1" → "requestStart").</summary>
    public IReadOnlyDictionary<string, string>? MessageNames { get; init; }

    /// <summary>Shown in the preview when AI wrote part of the plan.</summary>
    public string? AiNote { get; init; }
}

public sealed record DerivationResult(DerivationPlan Plan, IReadOnlyList<Diagram> Diagrams, int Added, int Changed, int Removed, IReadOnlyList<string> CreatedIds,
                                      IReadOnlyList<string> ChangedIds, IReadOnlyList<string> RemovedIds)
{
    public Diagram? Primary => Diagrams.LastOrDefault();

    public string Summary
    {
        get
        {
            var what = Plan.IsUpdate ? $"Updated {DerivationKinds.Product(Plan.Kind)} from {Plan.Source.DisplayName}" :
                Diagrams.Count > 0 ? $"Created {string.Join(" and ", Diagrams.Select(d => $"“{d.Name}”"))}" : $"Added {DerivationKinds.Product(Plan.Kind)}";
            var counts = new List<string>();
            if (Added > 0) counts.Add($"{Added} added");
            if (Changed > 0) counts.Add($"{Changed} changed");
            if (Removed > 0) counts.Add($"{Removed} removed");
            return $"{what}{(counts.Count > 0 ? " (" + string.Join(", ", counts) + ")" : "")} (⌘Z to undo)";
        }
    }
}

/// <summary>A derivation that can run on an element: what it is, what it reads and (state machines) which block it is for.</summary>
public sealed record DerivationOffer(DerivationKind Kind, Element Source, Element? Target, string Label, bool Exists);
