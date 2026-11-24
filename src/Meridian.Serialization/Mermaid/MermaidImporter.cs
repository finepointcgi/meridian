using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization.Mermaid;

/// <summary>How imported structural elements (blocks, interfaces, requirements, actors) relate to the open model.</summary>
public enum MermaidMatchMode
{
    /// <summary>Always create new elements (elements of the same name inside one import are still shared).</summary>
    CreateNew,
    /// <summary>Reuse existing elements of the same name (requirements also by id); only missing members and relationships are added.</summary>
    MatchByName,
}

public sealed record MermaidImportOptions
{
    public MermaidMatchMode Match { get; init; } = MermaidMatchMode.CreateNew;

    /// <summary>The input is a Markdown file: only its ```mermaid blocks are read.</summary>
    public bool Markdown { get; init; }
}

/// <summary>What one Mermaid diagram of the input will become.</summary>
public sealed record MermaidDiagramSummary(
    int Line, MermaidDiagramKind Kind, string Keyword, string Title, string Becomes, int Nodes, int Edges, int Groups, int Notes, int Warnings)
{
    public bool Supported => Kind != MermaidDiagramKind.Unsupported;

    /// <summary>"7 nodes · 8 flows · 2 swimlanes".</summary>
    public string Counts => MermaidImporter.CountsText(this);
}

public sealed record MermaidPreview(IReadOnlyList<MermaidDiagramSummary> Diagrams, IReadOnlyList<string> Warnings)
{
    public int Supported => Diagrams.Count(d => d.Supported);
}

public sealed record MermaidImportResult(IReadOnlyList<Diagram> Diagrams, int Elements, int Relationships, IReadOnlyList<string> Warnings)
{
    public ImportReport ToReport() => new(Elements, Relationships, Warnings);

    public string Summary => Diagrams.Count == 0
        ? "Mermaid: nothing imported" + (Warnings.Count > 0 ? $" ({Warnings.Count} warnings)" : "")
        : $"Mermaid: {Diagrams.Count} diagram{(Diagrams.Count == 1 ? "" : "s")}, {Elements} elements, {Relationships} relationships" +
          (Warnings.Count > 0 ? $", {Warnings.Count} warning{(Warnings.Count == 1 ? "" : "s")}" : "");
}

/// <summary>
/// Imports Mermaid diagrams (a .mmd file, several diagrams in one file, or the ```mermaid blocks of a Markdown file)
/// as SysML model elements and diagrams: flowcharts as activities, class diagrams as BDDs, state diagrams as state
/// machines, sequence diagrams as interactions, requirement diagrams as requirements with their relationships, ER and
/// C4 diagrams as blocks; Ishikawa (fishbone), radar and treemap charts as whiteboard drawings. The whole import is one undo step; nothing here throws on bad input (see the warnings).
/// See docs/MERMAID.md for the mapping.
/// </summary>
public static class MermaidImporter
{
    /// <summary>File extensions read as Mermaid (Markdown files are searched for ```mermaid blocks).</summary>
    public static readonly IReadOnlyList<string> Extensions = [".mmd", ".mermaid", ".md"];

    public static bool IsMermaidFile(string? fileName) =>
        fileName != null && Extensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    public static bool IsMarkdownFile(string? fileName) =>
        fileName != null && Path.GetExtension(fileName).Equals(".md", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses without touching any model: what each diagram will become, with counts and warnings.</summary>
    public static MermaidPreview Preview(string? text, bool markdown = false)
    {
        var warnings = new List<string>();
        var summaries = new List<MermaidDiagramSummary>();
        foreach (var g in Parse(text, markdown, warnings))
        {
            summaries.Add(Summarize(g));
            warnings.AddRange(g.Warnings);
        }
        return new MermaidPreview(summaries, warnings);
    }

    /// <summary>Mermaid diagrams, or PlantUML ones when the text starts a <c>@startuml</c> block; Markdown is searched for
    /// both <c>```mermaid</c> and <c>```plantuml</c> blocks.</summary>
    internal static List<MGraph> Parse(string? text, bool markdown, List<string> warnings)
    {
        if (!markdown && PlantUml.PlantUmlParser.LooksLikePlantUml(text)) return PlantUml.PlantUmlImporter.Parse(text, false, warnings);
        var graphs = MermaidSource.Split(text, markdown, warnings).Select(MermaidParser.Parse).ToList();
        if (markdown) graphs.AddRange(PlantUml.PlantUmlImporter.Parse(text, true, warnings));
        return graphs;
    }

    /// <summary>Imports into <paramref name="target"/> (a package or the model root) as one undo step.</summary>
    public static MermaidImportResult Import(ModelRepository m, Element target, string? text, MermaidImportOptions? options = null)
    {
        options ??= new MermaidImportOptions();
        if (!m.Contains(target) || target.Kind is not (ElementKind.Package or ElementKind.Model)) target = m.Root;
        var warnings = new List<string>();
        var graphs = Parse(text, options.Markdown, warnings);
        var elementsBefore = m.AllElements.Count(e => e is not Relationship && e is not Diagram);
        var relationshipsBefore = m.Relationships.Count();
        var diagrams = new List<Diagram>();
        if (graphs.Any(g => g.Kind != MermaidDiagramKind.Unsupported))
        {
            using (m.History.Transaction("Import Mermaid"))
            {
                var builder = new MermaidBuilder(m, target, options, warnings) { Source = PlantUml.PlantUmlParser.LooksLikePlantUml(text) ? "PlantUML" : "Mermaid" };
                foreach (var g in graphs)
                {
                    if (g.Kind != MermaidDiagramKind.Unsupported && builder.Build(g) is { } d) diagrams.Add(d);
                    warnings.AddRange(g.Warnings);   // after building: the builder adds its own
                }
            }
        }
        else foreach (var g in graphs) warnings.AddRange(g.Warnings);
        var elements = m.AllElements.Count(e => e is not Relationship && e is not Diagram) - elementsBefore;
        var relationships = m.Relationships.Count() - relationshipsBefore;
        return new MermaidImportResult(diagrams, elements, relationships, warnings);
    }

    /// <summary>A new model named <paramref name="name"/> holding the imported diagrams ("Open a file as a new project").</summary>
    public static (ModelRepository Model, ImportReport Report) ImportAsNewProject(string? text, string name, bool markdown = false)
    {
        var model = new ModelRepository(string.IsNullOrWhiteSpace(name) ? "Mermaid import" : name);
        var result = Import(model, model.Root, text, new MermaidImportOptions { Markdown = markdown });
        model.History.Clear();
        return (model, result.ToReport());
    }

    // ================================================================== preview

    internal static string Becomes(MermaidDiagramKind kind) => kind switch
    {
        MermaidDiagramKind.Flowchart => "Activity diagram",
        MermaidDiagramKind.Class => "Block definition diagram",
        MermaidDiagramKind.State => "State machine diagram",
        MermaidDiagramKind.Sequence => "Sequence diagram",
        MermaidDiagramKind.Requirement => "Requirement diagram",
        MermaidDiagramKind.EntityRelationship => "Block definition diagram",
        MermaidDiagramKind.C4 => "Block definition diagram",
        MermaidDiagramKind.Ishikawa => "Whiteboard drawing (fishbone)",
        MermaidDiagramKind.Radar => "Whiteboard drawing (radar chart)",
        MermaidDiagramKind.Treemap => "Whiteboard drawing (treemap)",
        MermaidDiagramKind.UseCase => "Use case diagram",
        _ => "Skipped (not supported)",
    };

    internal static string TypeName(MermaidDiagramKind kind) => kind switch
    {
        MermaidDiagramKind.Flowchart => "Flowchart",
        MermaidDiagramKind.Class => "Class diagram",
        MermaidDiagramKind.State => "State diagram",
        MermaidDiagramKind.Sequence => "Sequence diagram",
        MermaidDiagramKind.Requirement => "Requirement diagram",
        MermaidDiagramKind.EntityRelationship => "ER diagram",
        MermaidDiagramKind.C4 => "C4 diagram",
        MermaidDiagramKind.Ishikawa => "Ishikawa diagram",
        MermaidDiagramKind.Radar => "Radar chart",
        MermaidDiagramKind.Treemap => "Treemap",
        MermaidDiagramKind.UseCase => "Use case diagram",
        _ => "Diagram",
    };

    internal static MermaidDiagramSummary Summarize(MGraph g)
    {
        int nodes, edges;
        if (g.Chart is { } chart)
        {
            // Ishikawa: causes / categories; radar: axes / curves; treemap: values / sections.
            var all = chart.Root.Descendants().ToList();
            var (n, e, gr) = g.Kind switch
            {
                MermaidDiagramKind.Ishikawa => (all.Count - chart.Root.Children.Count, 0, chart.Root.Children.Count),
                MermaidDiagramKind.Radar => (chart.Radar.Axes.Count, chart.Radar.Curves.Count, 0),
                _ => (all.Count(x => x.Children.Count == 0), 0, all.Count(x => x.Children.Count > 0)),
            };
            return new MermaidDiagramSummary(g.Line, g.Kind, g.Keyword, g.Title ?? "", Becomes(g.Kind), n, e, gr, 0, g.Warnings.Count);
        }
        if (g.Kind == MermaidDiagramKind.Sequence)
        {
            nodes = g.Nodes.Count;
            edges = g.Sequence.Count(x => x is MSeqMessage);
        }
        else if (g.Kind == MermaidDiagramKind.Flowchart)
        {
            nodes = g.Nodes.Count(n => n.Shape != "subgraph");
            edges = g.Edges.Count;
        }
        else
        {
            nodes = g.Nodes.Count;
            edges = g.Edges.Count;
        }
        var groups = g.Kind switch
        {
            MermaidDiagramKind.Sequence => g.Sequence.Count(x => x is MSeqFragmentStart),
            MermaidDiagramKind.State => g.Nodes.Count(n => n.Shape == "composite"),
            MermaidDiagramKind.C4 => 0,
            _ => g.Groups.Count,
        };
        var notes = g.Kind == MermaidDiagramKind.Sequence ? g.Sequence.Count(x => x is MSeqNote) : g.Notes.Count;
        return new MermaidDiagramSummary(g.Line, g.Kind, g.Keyword, g.Title ?? "", Becomes(g.Kind), nodes, edges, groups, notes, g.Warnings.Count);
    }

    internal static string CountsText(MermaidDiagramSummary s)
    {
        if (!s.Supported) return "skipped";
        static string P(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
        switch (s.Kind)
        {
            case MermaidDiagramKind.Ishikawa: return $"{P(s.Groups, "category", "categories")} · {P(s.Nodes, "cause", "causes")}";
            case MermaidDiagramKind.Radar: return $"{P(s.Nodes, "axis", "axes")} · {P(s.Edges, "curve", "curves")}";
            case MermaidDiagramKind.Treemap: return $"{P(s.Nodes, "value", "values")} · {P(s.Groups, "section", "sections")}";
        }
        var (node, nodes, edge, edges, group, groups) = s.Kind switch
        {
            MermaidDiagramKind.Flowchart => ("node", "nodes", "flow", "flows", "swimlane", "swimlanes"),
            MermaidDiagramKind.Class => ("block", "blocks", "relationship", "relationships", "package", "packages"),
            MermaidDiagramKind.State => ("state", "states", "transition", "transitions", "composite state", "composite states"),
            MermaidDiagramKind.Sequence => ("lifeline", "lifelines", "message", "messages", "fragment", "fragments"),
            MermaidDiagramKind.Requirement => ("element", "elements", "relationship", "relationships", "group", "groups"),
            MermaidDiagramKind.EntityRelationship => ("block", "blocks", "association", "associations", "group", "groups"),
            MermaidDiagramKind.UseCase => ("actor or use case", "actors and use cases", "relationship", "relationships", "boundary", "boundaries"),
            _ => ("element", "elements", "dependency", "dependencies", "group", "groups"),
        };
        var parts = new List<string> { P(s.Nodes, node, nodes), P(s.Edges, edge, edges) };
        if (s.Groups > 0) parts.Add(P(s.Groups, group, groups));
        if (s.Notes > 0) parts.Add(P(s.Notes, "note", "notes"));
        return string.Join(" · ", parts);
    }
}
