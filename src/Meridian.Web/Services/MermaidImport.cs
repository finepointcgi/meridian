using Meridian.Core.Model;
using Meridian.Serialization.Mermaid;

namespace Meridian.Web.Services;

/// <summary>
/// Mermaid import into the open project, shared by the Paste Mermaid dialog, "Import into this project…" and dropping a
/// .mmd file on the canvas: one undo step, the report in the Output panel, new diagrams open as tabs.
/// </summary>
public static class MermaidImport
{
    /// <summary>Largest Mermaid file read from an upload or a drop.</summary>
    public const long MaxBytes = 8L * 1024 * 1024;

    /// <summary>The package new elements go to: the selection's package, else the active diagram's, else the model root.</summary>
    public static Element TargetFor(Workspace ws)
    {
        var from = ws.Selected ?? ws.Active?.Diagram.Owner;
        return from?.AncestorsAndSelf().FirstOrDefault(a => a.Kind is ElementKind.Package or ElementKind.Model && ws.Model.Contains(a)) ?? ws.Model.Root;
    }

    /// <summary>Imports <paramref name="text"/> and reports it. Returns null when the project is read-only.</summary>
    public static MermaidImportResult? Apply(Workspace ws, string text, MermaidImportOptions options, string source, Element? target = null)
    {
        if (ws.ReadOnly)
        {
            ws.SetStatus("View-only: you can't import into this project");
            return null;
        }
        var result = MermaidImporter.Import(ws.Model, target ?? TargetFor(ws), text, options);
        Report(ws, result, source);
        if (result.Diagrams.Count > 0) ws.Open(result.Diagrams[0]);
        return result;
    }

    public static void Report(Workspace ws, MermaidImportResult result, string source)
    {
        ws.Log($"Imported Mermaid from {source}: {result.Diagrams.Count} diagrams ({string.Join(", ", result.Diagrams.Select(d => d.Name))}), " +
               $"{result.Elements} elements, {result.Relationships} relationships, {result.Warnings.Count} warnings.");
        foreach (var w in result.Warnings.Take(200)) ws.Log("  warning: " + w);
        if (result.Warnings.Count > 200) ws.Log($"  … and {result.Warnings.Count - 200} more warnings.");
        ws.SetStatus(result.Summary + (result.Diagrams.Count > 0 ? " — ⌘Z undoes it" : ""));
    }
}
