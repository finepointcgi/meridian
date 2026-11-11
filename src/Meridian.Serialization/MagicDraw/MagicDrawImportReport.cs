namespace Meridian.Serialization;

/// <summary>What a MagicDraw / Cameo import produced: counts per imported kind, constructs it skipped, and warnings.</summary>
public sealed partial class MagicDrawImportReport
{
    private const int MaxWarnings = 1000;
    private int _droppedWarnings;
    private readonly List<string> _warnings = new();

    /// <summary>Imported elements per kind label ("Block", "Part property", "Satisfy", "Diagram (with layout)" …).</summary>
    public SortedDictionary<string, int> Imported { get; } = new(StringComparer.Ordinal);

    /// <summary>Constructs that were read but have no Meridian counterpart, per construct.</summary>
    public SortedDictionary<string, int> Skipped { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Warnings => _warnings;

    public int Elements { get; internal set; }
    public int Relationships { get; internal set; }
    public int Diagrams { get; internal set; }
    public int DiagramsWithLayout { get; internal set; }
    public int DiagramsAutoLaidOut { get; internal set; }

    /// <summary>Set by <see cref="MagicDrawImporter.ImportInto"/>: elements created, updated and left alone in the target model.</summary>
    public int Added { get; internal set; }
    public int Updated { get; internal set; }
    public int Unchanged { get; internal set; }
    /// <summary>Elements in the target that came from an earlier import but are no longer in the file (kept, not deleted).</summary>
    public int NotInFile { get; internal set; }

    /// <summary>Category of <see cref="Approximated"/>: pseudostates whose kind is not a UML kind (imported as choices).</summary>
    public const string UnknownPseudostate = "Unknown pseudostate kinds";

    /// <summary>Category of <see cref="Approximated"/>: fragments whose operand separators could not be read and were spread evenly.</summary>
    public const string OperandGeometryGuessed = "Operand separators guessed";

    private const int MaxApproximationsPerCategory = 50;

    /// <summary>What was imported, but not exactly as in the file, per category (see the constants), with one entry per occurrence.</summary>
    public SortedDictionary<string, List<string>> Approximated { get; } = new(StringComparer.Ordinal);

    internal void Approximate(string category, string detail)
    {
        if (!Approximated.TryGetValue(category, out var list)) Approximated[category] = list = [];
        if (list.Count < MaxApproximationsPerCategory) list.Add(detail);
        else if (list.Count == MaxApproximationsPerCategory) list.Add("…");
    }

    internal void Count(string label, int n = 1) => Imported[label] = Imported.GetValueOrDefault(label) + n;
    internal void Skip(string construct, int n = 1) => Skipped[construct] = Skipped.GetValueOrDefault(construct) + n;

    internal void Warn(string message)
    {
        if (_warnings.Count < MaxWarnings) _warnings.Add(message);
        else _droppedWarnings++;
    }

    internal void Finish()
    {
        if (_droppedWarnings > 0) _warnings.Add($"… and {_droppedWarnings} more warnings");
        _droppedWarnings = 0;
    }

    /// <summary>The generic report shape the other importers use.</summary>
    public ImportReport ToImportReport() => new(Elements, Relationships, Warnings);

    /// <summary>Human-readable report for the Output dock.</summary>
    public IEnumerable<string> Lines(string fileName)
    {
        yield return $"Imported MagicDraw/Cameo project {fileName}: {Elements} elements, {Relationships} relationships, {Diagrams} diagrams " +
                     $"({DiagramsWithLayout} with original layout, {DiagramsAutoLaidOut} auto-laid out), {Warnings.Count} warnings.";
        if (Added + Updated + Unchanged > 0)
            yield return $"  Re-import: {Added} added, {Updated} updated, {Unchanged} unchanged, {NotInFile} kept that are no longer in the file.";
        if (Imported.Count > 0)
        {
            yield return "  Imported:";
            foreach (var (k, v) in Imported) yield return $"    {k}: {v}";
        }
        if (Skipped.Count > 0)
        {
            yield return "  Skipped (no Meridian equivalent):";
            foreach (var (k, v) in Skipped) yield return $"    {k}: {v}";
        }
        if (Approximated.Count > 0)
        {
            yield return "  Approximated (imported, but not exactly as in the file):";
            foreach (var (k, v) in Approximated)
            {
                yield return $"    {k}: {v.Count(x => x != "…")}{(v.Contains("…") ? "+" : "")}";
                foreach (var d in v) yield return "      " + d;
            }
        }
        if (_used.Count > 0)
            yield return "  Uses other Cameo projects: " + string.Join(", ", UsedProjects.Select(u => u.Name)) +
                         " (references to them are kept by name, or by id when they are used from Meridian projects).";
        foreach (var l in UsedProjectLines()) yield return l;
        foreach (var w in Warnings) yield return "  warning: " + w;
    }
}
