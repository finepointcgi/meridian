using System.Text;
using System.Text.RegularExpressions;
using Meridian.Core.Model;
using Meridian.Serialization.Mermaid;

namespace Meridian.Serialization.PlantUml;

/// <summary>
/// Imports PlantUML files (.puml, .plantuml, .pu, or the <c>```plantuml</c> blocks of Markdown) as model elements and
/// diagrams, through the Mermaid builder: sequence → interaction, class → block definition diagram, use case → use case
/// diagram, activity → activity, state → state machine. See docs/PLANTUML.md.
/// </summary>
public static partial class PlantUmlImporter
{
    public static readonly IReadOnlyList<string> Extensions = [".puml", ".plantuml", ".pu", ".wsd"];

    public static bool IsPlantUmlFile(string? fileName) =>
        fileName != null && Extensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    [GeneratedRegex(@"^[ \t]*(```|~~~)[ \t]*(plantuml|puml)\b[^\n]*\n(?<body>.*?)^[ \t]*\1[ \t]*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Multiline, 2000)]
    private static partial Regex MarkdownBlock();

    /// <summary>Parses PlantUML text, or only the <c>```plantuml</c> blocks of Markdown.</summary>
    internal static List<MGraph> Parse(string? text, bool markdown, List<string> warnings)
    {
        text ??= "";
        if (!markdown) return PlantUmlParser.Split(text, warnings).Select(PlantUmlParser.Parse).ToList();
        var graphs = new List<MGraph>();
        try
        {
            foreach (Match m in MarkdownBlock().Matches(text.Replace("\r\n", "\n")))
            {
                foreach (var g in PlantUmlParser.Split(m.Groups["body"].Value, warnings).Select(PlantUmlParser.Parse))
                    graphs.Add(g);
                if (graphs.Count >= MermaidLimits.MaxDiagrams) break;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            warnings.Add("PlantUML: the Markdown took too long to search for plantuml blocks; they were skipped.");
        }
        return graphs;
    }

    /// <summary>What each diagram will become, without touching a model.</summary>
    public static MermaidPreview Preview(string? text)
    {
        var warnings = new List<string>();
        var summaries = new List<MermaidDiagramSummary>();
        foreach (var g in Parse(text, false, warnings))
        {
            summaries.Add(MermaidImporter.Summarize(g));
            warnings.AddRange(g.Warnings);
        }
        return new MermaidPreview(summaries, warnings);
    }

    /// <summary>Imports into <paramref name="target"/> as one undo step.</summary>
    public static MermaidImportResult Import(ModelRepository m, Element target, string? text, MermaidImportOptions? options = null)
    {
        options ??= new MermaidImportOptions();
        if (!m.Contains(target) || target.Kind is not (ElementKind.Package or ElementKind.Model)) target = m.Root;
        var warnings = new List<string>();
        var graphs = Parse(text, options.Markdown, warnings);
        var elementsBefore = m.AllElements.Count(e => e is not Relationship && e is not Diagram);
        var relationshipsBefore = m.Relationships.Count();
        var diagrams = new List<Diagram>();
        using (m.History.Transaction("Import PlantUML"))
        {
            var builder = new MermaidBuilder(m, target, options, warnings) { Source = "PlantUML" };
            foreach (var g in graphs)
            {
                if (g.Kind != MermaidDiagramKind.Unsupported && builder.Build(g) is { } d) diagrams.Add(d);
                warnings.AddRange(g.Warnings);
            }
        }
        return new MermaidImportResult(diagrams, m.AllElements.Count(e => e is not Relationship && e is not Diagram) - elementsBefore,
            m.Relationships.Count() - relationshipsBefore, warnings);
    }

    /// <summary>"Open a file as a new project" with a PlantUML file.</summary>
    public static (ModelRepository Model, ImportReport Report) ImportAsNewProject(string? text, string name)
    {
        var model = new ModelRepository(string.IsNullOrWhiteSpace(name) ? "PlantUML import" : name);
        var result = Import(model, model.Root, text);
        model.History.Clear();
        return (model, result.ToReport());
    }

    internal static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart('﻿');
}
