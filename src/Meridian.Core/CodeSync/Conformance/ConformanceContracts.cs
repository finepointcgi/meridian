using Meridian.Core.Model;

namespace Meridian.Core.CodeSync.Conformance;

// Architecture conformance ("model-to-code checks"): the code read by code sync is checked against the architecture
// drawn in the model. See docs/ARCHITECTURE-CHECKS.md.

/// <summary>The rules <see cref="ArchitectureConformance"/> checks.</summary>
public enum ConformanceRule
{
    /// <summary>(a) A code dependency between two types whose model elements have no relationship path that permits it.</summary>
    Dependency,
    /// <summary>(b) A dependency from one «layer» package to another that the first layer does not allow.</summary>
    Layering,
    /// <summary>(c) A block that realizes an interface block in the model does not implement it (or its operations) in code.</summary>
    InterfaceConformance,
    /// <summary>(d) A block marked «implemented» has no matching type in code (warning).</summary>
    MissingInCode,
    /// <summary>(d) A type in the synced code has no element in the model (warning).</summary>
    MissingInModel,
}

public enum ViolationSeverity { Warning, Error }

/// <summary>Model relationship paths that permit a code dependency (rule a). Configurable per project.</summary>
[Flags]
public enum PermittedPaths
{
    None = 0,
    Association = 1,
    Composition = 2,
    Aggregation = 4,
    /// <summary>A connector between parts typed by the two blocks (not through ports typed by an interface block).</summary>
    Connector = 8,
    /// <summary>A plain dependency (not «use», «allow» or «allowed»).</summary>
    Dependency = 16,
    /// <summary>A dependency stereotyped «use» or «usage».</summary>
    Usage = 32,
    Realization = 64,
    Generalization = 128,
    /// <summary>The target is an interface (block) the source shares: a port typed by it, or a connector through it.</summary>
    SharedInterface = 256,
    All = Association | Composition | Aggregation | Connector | Dependency | Usage | Realization | Generalization | SharedInterface,
}

/// <summary>One finding. <see cref="File"/> / <see cref="Line"/> locate the code (repository-relative path, 1-based
/// line; null / 0 when the finding has no code side); <see cref="ElementId"/> and <see cref="TargetElementId"/> are the
/// model elements involved (null when there is none).</summary>
public sealed record ArchitectureViolation(
    ConformanceRule Rule,
    ViolationSeverity Severity,
    string Message,
    string FixHint,
    string? File,
    int Line,
    string? CodeSymbol,
    string? TargetSymbol,
    string? ElementId,
    string? ElementName,
    string? TargetElementId,
    string? TargetElementName)
{
    /// <summary>"dependency", "layering", "interface", "missing-in-code", "missing-in-model" (used in suppressions).</summary>
    public string RuleId => ArchitectureConformance.RuleId(Rule);

    /// <summary>"src/Thermal/ThermalControl.cs:12", or "" without a code side.</summary>
    public string Location => File is null ? "" : Line > 0 ? $"{File}:{Line}" : File;

    /// <summary>The lines that break the rule (dependency and layering findings, when the reader recorded them): the first
    /// <see cref="ConformanceOptions.MaxLocationsPerViolation"/> uses of the target, sorted by file and line. Empty when
    /// unknown; <see cref="File"/> / <see cref="Line"/> (the type's declaration) still locate the finding.</summary>
    public IReadOnlyList<CodeDependencyUse> Locations { get; init; } = [];

    /// <summary>Uses of the target beyond <see cref="Locations"/> (shown as "+N more").</summary>
    public int MoreLocations { get; init; }

    /// <summary>The first line that breaks the rule ("src/A.cs:42"), else the type's <see cref="Location"/>.</summary>
    public string FirstLocation => Locations.Count > 0 ? Locations[0].Location : Location;
}

/// <summary>What a check found: the violations to report, and those an «allow» dependency or a suppression entry hid.</summary>
public sealed record ConformanceResult(
    IReadOnlyList<ArchitectureViolation> Violations,
    IReadOnlyList<ArchitectureViolation> Suppressed,
    int TypesChecked,
    int TypesMapped)
{
    public static readonly ConformanceResult Empty = new([], [], 0, 0);

    public int Errors => Violations.Count(v => v.Severity == ViolationSeverity.Error);
    public int Warnings => Violations.Count(v => v.Severity == ViolationSeverity.Warning);

    /// <summary>"No architecture violations", "2 violations, 1 warning", "3 warnings".</summary>
    public string Summary
    {
        get
        {
            if (Violations.Count == 0) return "No architecture violations";
            var parts = new List<string>();
            if (Errors > 0) parts.Add($"{Errors} violation{(Errors == 1 ? "" : "s")}");
            if (Warnings > 0) parts.Add($"{Warnings} warning{(Warnings == 1 ? "" : "s")}");
            return string.Join(", ", parts);
        }
    }
}

/// <summary>How to check. <see cref="FromModel"/> reads the project's settings from the model root's fields.</summary>
public sealed record ConformanceOptions
{
    /// <summary>Model-root field: comma separated <see cref="PermittedPaths"/> names (default: all).</summary>
    public const string PermittedPathsField = "architecture.permittedPaths";
    /// <summary>Model-root field: "true" checks every mapped dependency, not only those of blocks the model constrains.</summary>
    public const string StrictField = "architecture.strict";
    /// <summary>Model-root field: suppression entries, one per line (or separated by ';').</summary>
    public const string SuppressionsField = "architecture.suppressions";
    /// <summary>Model-root field: comma separated rule ids to skip ("missing-in-model, missing-in-code").</summary>
    public const string DisabledRulesField = "architecture.disabledRules";

    public PermittedPaths Permitted { get; init; } = PermittedPaths.All;

    /// <summary>Rule (a) normally applies only to dependencies whose source block the model constrains (it has a
    /// hand-made relationship, port or part). Strict checks every dependency between mapped types.</summary>
    public bool Strict { get; init; }

    /// <summary>Project-level suppressions: "rule: source -> target" or "rule: subject"; '*' is a wildcard.</summary>
    public IReadOnlyList<string> Suppressions { get; init; } = [];

    public IReadOnlySet<ConformanceRule> DisabledRules { get; init; } = new HashSet<ConformanceRule>();

    /// <summary>The code connection being checked (matches <c>code.repo</c>); elements synced from other repositories are ignored.</summary>
    public string? RepoId { get; init; }

    /// <summary>False for a partial push: «implemented» blocks are then not reported as missing in code.</summary>
    public bool CodeIsComplete { get; init; } = true;

    /// <summary>Lines listed per violation (<see cref="ArchitectureViolation.Locations"/>); the rest are counted.</summary>
    public int MaxLocationsPerViolation { get; init; } = 5;

    public static ConformanceOptions FromModel(ModelRepository model, string? repoId = null)
    {
        var root = model.Root;
        var permitted = PermittedPaths.All;
        if (root.Get(PermittedPathsField) is { Length: > 0 } list)
        {
            permitted = PermittedPaths.None;
            foreach (var part in Split(list, ','))
                if (Enum.TryParse<PermittedPaths>(part.Replace("-", "", StringComparison.Ordinal), ignoreCase: true, out var p)) permitted |= p;
        }
        var disabled = new HashSet<ConformanceRule>();
        foreach (var id in Split(root.Get(DisabledRulesField), ','))
            if (ArchitectureConformance.ParseRule(id) is { } r) disabled.Add(r);
        return new ConformanceOptions
        {
            Permitted = permitted,
            Strict = root.Get(StrictField).Equals("true", StringComparison.OrdinalIgnoreCase),
            Suppressions = Split(root.Get(SuppressionsField), '\n', ';').ToList(),
            DisabledRules = disabled,
            RepoId = repoId,
        };
    }

    internal static IEnumerable<string> Split(string? text, params char[] separators) =>
        (text ?? "").Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => !s.StartsWith('#'));
}
