namespace Meridian.Serialization;

/// <summary>
/// Fidelity accounting for MagicDraw / Cameo imports: an inventory of what the file holds, compared with what the import kept.
/// Every model element the walk reaches is either <em>imported</em> (it became a Meridian element), <em>folded</em> (its content
/// went into a field of another element, e.g. documentation comments or an interaction operand) or <em>not imported</em>. The
/// last group is the honest measure of loss: the per-construct <see cref="MagicDrawImportReport.Skipped"/> list explains most
/// of it, and anything it does not explain still shows up here by UML type.
/// </summary>
public static partial class MagicDrawImporter
{
    private sealed partial class Builder
    {
        /// <summary>Source element id → UML type, for every element the model walk reached.</summary>
        private readonly Dictionary<string, string> _source = new(StringComparer.Ordinal);

        /// <summary>Source ids whose content was absorbed into another element instead of becoming one.</summary>
        private readonly HashSet<string> _folded = new(StringComparer.Ordinal);

        // Infrastructure, value specifications and bookkeeping elements: not something a modeler counts as "an element".
        private static readonly HashSet<string> DetailTypes = new(StringComparer.Ordinal)
        {
            "Extension", "ProfileApplication", "PackageImport", "ElementImport", "Diagram",
            "LiteralString", "LiteralInteger", "LiteralReal", "LiteralBoolean", "LiteralUnlimitedNatural", "LiteralNull",
            "OpaqueExpression", "Expression", "StringExpression", "InstanceValue", "ElementValue", "TimeExpression", "Duration",
            "MessageOccurrenceSpecification", "OccurrenceSpecification", "ExecutionOccurrenceSpecification", "DestructionOccurrenceSpecification",
            "BehaviorExecutionSpecification", "ActionExecutionSpecification",
            "SignalEvent", "CallEvent", "TimeEvent", "ChangeEvent", "AnyReceiveEvent",
        };

        // Owned features whose content the parent element reads into its own fields (multiplicity, default value, guard,
        // entry/exit/do behavior text, trigger…). Not the reference-or-containment tags such as node/edge, which hold real
        // activity nodes in UML 2.4 exports.
        private static readonly HashSet<string> ParentReadTags = new(StringComparer.Ordinal)
        {
            "lowerValue", "upperValue", "defaultValue", "specification", "guard", "effect", "entry", "exit", "doActivity", "trigger",
            "deferrableTrigger", "body", "nameExpression", "ownedParameter", "ownedTemplateSignature", "templateBinding", "selection",
            "transformation", "joinSpec", "weight", "decisionInput", "precondition", "postcondition", "upperBound", "condition",
            "ownedEnd", "value", "language",
        };

        /// <summary>Records a source element reached by the model walk. Features read by their parent are not counted.</summary>
        private void Inventory(string tag, string? type, string? id)
        {
            if (id == null || type == null || DetailTypes.Contains(type) || ParentReadTags.Contains(tag)) return;
            _source.TryAdd(id, type);
        }

        private void Fold(string? id)
        {
            if (id != null) _folded.Add(id);
        }

        /// <summary>Compares the inventory with the built model and fills the report's fidelity section.</summary>
        private void ComputeFidelity()
        {
            foreach (var (id, type) in _source)
            {
                var t = _report.Tally(type);
                t.InFile++;
                if (_map.ContainsKey(id)) t.Imported++;
                else if (_folded.Contains(id)) t.Folded++;
            }

            _report.DiagramsInFile = _diagrams.Count;

            foreach (var (baseId, apps) in _stereo)
            {
                foreach (var app in apps)
                {
                    if (IsToolProfile(app.Profile) || app.Name is "DiagramInfo" or "auxiliaryResource") continue;
                    _report.StereotypeApplications++;
                    if (_map.ContainsKey(baseId) || _folded.Contains(baseId)) _report.StereotypeApplicationsKept++;
                    else _report.LoseStereotype(app.Name);
                }
            }
        }

        /// <summary>Counts a tag value that could not be copied (a structured value: instance, nested specification…).</summary>
        private void LoseTagValue(string tag) => _report.LoseTagValue(tag);
    }
}

/// <summary>One UML type's line in the fidelity table.</summary>
public sealed class MagicDrawTypeTally
{
    public int InFile { get; internal set; }
    public int Imported { get; internal set; }
    public int Folded { get; internal set; }
    public int NotImported => InFile - Imported - Folded;
}

public sealed partial class MagicDrawImportReport
{
    /// <summary>Per source UML type: how many the file held and what happened to them.</summary>
    public SortedDictionary<string, MagicDrawTypeTally> SourceTypes { get; } = new(StringComparer.Ordinal);

    /// <summary>Diagrams found in the file (imported or not).</summary>
    public int DiagramsInFile { get; internal set; }

    /// <summary>User stereotype applications in the file (tool-internal profiles excluded).</summary>
    public int StereotypeApplications { get; internal set; }
    public int StereotypeApplicationsKept { get; internal set; }

    /// <summary>Stereotype applications lost because the element they were applied to was not imported, per stereotype.</summary>
    public SortedDictionary<string, int> StereotypesLost { get; } = new(StringComparer.Ordinal);

    public int TagValuesKept { get; internal set; }

    /// <summary>Tag values that could not be copied (structured values), per tag name.</summary>
    public SortedDictionary<string, int> TagValuesLost { get; } = new(StringComparer.Ordinal);

    public int ElementsInFile => SourceTypes.Values.Sum(t => t.InFile);
    public int ElementsKept => SourceTypes.Values.Sum(t => t.Imported + t.Folded);
    public int ElementsNotImported => SourceTypes.Values.Sum(t => t.NotImported);

    /// <summary>Share of the file's model elements that were imported or folded into another element (1 when the file was empty).</summary>
    public double ElementCoverage => ElementsInFile == 0 ? 1 : (double)ElementsKept / ElementsInFile;

    public double DiagramCoverage => DiagramsInFile == 0 ? 1 : (double)Diagrams / DiagramsInFile;

    public double StereotypeCoverage => StereotypeApplications == 0 ? 1 : (double)StereotypeApplicationsKept / StereotypeApplications;

    /// <summary>True when nothing in the file was lost: every element, diagram, stereotype and tag value came across.</summary>
    public bool IsLossless => ElementsNotImported == 0 && Diagrams == DiagramsInFile && StereotypesLost.Count == 0 && TagValuesLost.Count == 0;

    /// <summary>UML types with losses, most lost first.</summary>
    public IEnumerable<(string Type, MagicDrawTypeTally Tally)> Losses =>
        SourceTypes.Where(kv => kv.Value.NotImported > 0).OrderByDescending(kv => kv.Value.NotImported).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                   .Select(kv => (kv.Key, kv.Value));

    internal MagicDrawTypeTally Tally(string type)
    {
        if (!SourceTypes.TryGetValue(type, out var t)) SourceTypes[type] = t = new MagicDrawTypeTally();
        return t;
    }

    internal void LoseStereotype(string name) => StereotypesLost[name] = StereotypesLost.GetValueOrDefault(name) + 1;
    internal void LoseTagValue(string tag) => TagValuesLost[tag] = TagValuesLost.GetValueOrDefault(tag) + 1;

    private static string Pct(double v) => (v * 100).ToString(v >= 0.995 && v < 1 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture) + "%";

    /// <summary>One-line fidelity summary: "Kept 97% of model elements (1203 of 1235), 41 of 45 diagrams, 300 of 310 stereotypes."</summary>
    public string FidelitySummary =>
        IsLossless
            ? $"Nothing lost: all {ElementsInFile} model elements, {DiagramsInFile} diagrams and {StereotypeApplications} stereotype applications came across."
            : $"Kept {Pct(ElementCoverage)} of model elements ({ElementsKept} of {ElementsInFile}), {Diagrams} of {DiagramsInFile} diagrams, " +
              $"{StereotypeApplicationsKept} of {StereotypeApplications} stereotype applications" +
              (TagValuesLost.Count > 0 ? $"; {TagValuesLost.Values.Sum()} tag values could not be copied." : ".");

    private IEnumerable<string> FidelityLines()
    {
        yield return "  Fidelity: " + FidelitySummary;
        var losses = Losses.ToList();
        if (losses.Count > 0)
        {
            yield return "  Not imported, by UML type (in file / imported / folded into another element / not imported):";
            foreach (var (type, t) in losses) yield return $"    uml:{type}: {t.InFile} / {t.Imported} / {t.Folded} / {t.NotImported}";
        }
        if (StereotypesLost.Count > 0)
        {
            yield return "  Stereotype applications lost with their element:";
            foreach (var (k, v) in StereotypesLost.OrderByDescending(kv => kv.Value)) yield return $"    «{k}»: {v}";
        }
        if (TagValuesLost.Count > 0)
        {
            yield return "  Tag values with structured values (not copied):";
            foreach (var (k, v) in TagValuesLost.OrderByDescending(kv => kv.Value)) yield return $"    {k}: {v}";
        }
    }

    /// <summary>A Markdown fidelity report for one file (used by the corpus check and the report download).</summary>
    public string ToMarkdown(string fileName)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## {fileName}").AppendLine();
        sb.AppendLine(FidelitySummary).AppendLine();
        sb.AppendLine($"| Measure | Kept | In file | Coverage |").AppendLine("| --- | ---: | ---: | ---: |");
        sb.AppendLine($"| Model elements | {ElementsKept} | {ElementsInFile} | {Pct(ElementCoverage)} |");
        sb.AppendLine($"| Diagrams | {Diagrams} | {DiagramsInFile} | {Pct(DiagramCoverage)} |");
        sb.AppendLine($"| Stereotype applications | {StereotypeApplicationsKept} | {StereotypeApplications} | {Pct(StereotypeCoverage)} |");
        sb.AppendLine($"| Tag values | {TagValuesKept} | {TagValuesKept + TagValuesLost.Values.Sum()} | {Pct(TagValuesKept + TagValuesLost.Values.Sum() == 0 ? 1 : (double)TagValuesKept / (TagValuesKept + TagValuesLost.Values.Sum()))} |");
        sb.AppendLine($"| Diagrams with original layout | {DiagramsWithLayout} | {Diagrams} | |").AppendLine();

        var losses = Losses.ToList();
        if (losses.Count > 0)
        {
            sb.AppendLine("### Not imported, by UML type").AppendLine();
            sb.AppendLine("| UML type | In file | Imported | Folded | Not imported |").AppendLine("| --- | ---: | ---: | ---: | ---: |");
            foreach (var (type, t) in losses) sb.AppendLine($"| uml:{type} | {t.InFile} | {t.Imported} | {t.Folded} | {t.NotImported} |");
            sb.AppendLine();
        }
        if (Skipped.Count > 0)
        {
            sb.AppendLine("### Skipped constructs (with reason)").AppendLine();
            foreach (var (k, v) in Skipped.OrderByDescending(kv => kv.Value)) sb.AppendLine($"- {k}: {v}");
            sb.AppendLine();
        }
        if (StereotypesLost.Count > 0)
        {
            sb.AppendLine("### Stereotype applications lost with their element").AppendLine();
            foreach (var (k, v) in StereotypesLost.OrderByDescending(kv => kv.Value)) sb.AppendLine($"- «{k}»: {v}");
            sb.AppendLine();
        }
        if (TagValuesLost.Count > 0)
        {
            sb.AppendLine("### Tag values not copied (structured values)").AppendLine();
            foreach (var (k, v) in TagValuesLost.OrderByDescending(kv => kv.Value)) sb.AppendLine($"- {k}: {v}");
            sb.AppendLine();
        }
        if (Warnings.Count > 0)
        {
            sb.AppendLine($"### Warnings ({Warnings.Count})").AppendLine();
            foreach (var w in Warnings.Take(50)) sb.AppendLine($"- {w}");
            if (Warnings.Count > 50) sb.AppendLine($"- … {Warnings.Count - 50} more");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
