using Meridian.Core.Model;
using Meridian.Serialization.DiagramFiles;
using Meridian.Serialization.DrawIo;
using Meridian.Web.Services;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>
/// Diagram files: "Import a folder of diagrams…" (Mermaid and draw.io, one package per folder), draw.io files in "Open a file
/// as a new project" and "Import into this project…", and Export diagram / all diagrams as draw.io. See docs/DRAWIO.md.
/// </summary>
public partial class Workbench
{
    private IEnumerable<CommandPalette.Command> DiagramFilePaletteCommands() =>
    [
        new("Import a folder of diagrams (Mermaid, PlantUML, draw.io)…", "Command", Icons.Material.Outlined.DriveFolderUpload, ImportDiagramFolder),
        new("Export diagram as draw.io (.drawio)", "Command", Icons.Material.Outlined.Draw, ExportDrawIo),
        new("Export all diagrams as draw.io (.drawio)", "Command", Icons.Material.Outlined.Draw, ExportAllDrawIo),
    ];

    /// <summary>Asks the browser for a folder, reads its Mermaid and draw.io files, previews them and imports into the selection's package.</summary>
    private async Task ImportDiagramFolder()
    {
        if (Ws.ReadOnly) { Ws.SetStatus("View-only: you can't import into this project"); return; }
        var target = MermaidImport.TargetFor(Ws);
        var receiver = new DiagramFolderReceiver();
        using var reference = DotNetObjectReference.Create(receiver);
        await JS.InvokeVoidAsync("meridian.pickDiagramFolder", reference);
        if (await receiver.Result is not { } got) return;
        if (got.Files.Count == 0)
        {
            Snackbar.Add($"No Mermaid, PlantUML or draw.io files in {got.Folder}" + (got.Skipped > 0 ? $" ({got.Skipped} other files)" : ""), Severity.Warning);
            return;
        }
        await ImportDiagramFiles(got.Files, target, $"Import folder {got.Folder}", got.Folder, got.Skipped);
    }

    /// <summary>Previews <paramref name="files"/>, then imports them as one undo step.</summary>
    private async Task ImportDiagramFiles(IReadOnlyList<DiagramSourceFile> files, Element target, string title, string source, int skipped = 0)
    {
        // Files imported before go back where they went, so their diagrams are updated rather than copied.
        target = DiagramFolderImport.PreviousTarget(Ws.Model, files) ?? target;
        var options = await DiagramFilesDialog.ShowAsync(Dialogs, files, target, DiagramFilesImport.DefaultOptions(Ws, files, target), title, skipped);
        if (options == null) return;
        var result = DiagramFilesImport.Apply(Ws, files, options, source, target);
        if (result == null) return;
        if (result.Warnings.Count > 0) OpenDock(3);
        Snackbar.Add(result.Summary, result.Diagrams.Count > 0 ? Severity.Success : Severity.Warning);
    }

    private static async Task<DiagramSourceFile> ReadDiagramFile(IBrowserFile file, string? path = null)
    {
        if (file.Size > DiagramFolderImport.MaxFileBytes) throw new InvalidDataException($"{file.Name} is larger than {DiagramFolderImport.MaxFileBytes / (1024 * 1024)} MB.");
        await using var s = file.OpenReadStream(DiagramFolderImport.MaxFileBytes);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return new DiagramSourceFile(path ?? file.Name, ms.ToArray());
    }

    /// <summary>"Open a file as a new project" with a draw.io file.</summary>
    private async Task OpenDrawIoProject(IBrowserFile file)
    {
        var src = await ReadDiagramFile(file);
        var (model, report) = DrawIoImporter.ImportAsNewProject(file.Name, src.Content);
        if (!model.Diagrams.Any())
        {
            Snackbar.Add($"{file.Name} contains no diagram Meridian can import.", Severity.Warning);
            return;
        }
        var name = DrawIoImporter.BaseName(file.Name);
        await CreateProjectFrom(model, name);
        if (model.Diagrams.FirstOrDefault() is { } first && Ws.Model.Find(first.Id) is Diagram opened) Ws.Open(opened);
        Ws.Log($"Imported {file.Name}: {Plural.Of(model.Diagrams.Count(), "diagram")}, {Plural.Of(report.Elements, "element")}, {Plural.Of(report.Relationships, "relationship")}, {Plural.Of(report.Warnings.Count, "warning")}.");
        foreach (var w in report.Warnings.Take(200)) Ws.Log("  warning: " + w);
        if (report.Warnings.Count > 0) OpenDock(3);
        Snackbar.Add($"Imported {file.Name} as a new project", Severity.Success);
    }

    private Task ExportDrawIo()
    {
        if (Ws.Active is not { } c) { Ws.SetStatus("Open a diagram first"); return Task.CompletedTask; }
        return DownloadText(c.Diagram.Name + ".drawio", "application/vnd.jgraph.mxfile", DrawIoExporter.Export(Ws.Model, [c.Diagram]));
    }

    private Task ExportAllDrawIo()
    {
        var diagrams = Ws.Model.Diagrams.OrderBy(d => d.QualifiedName, StringComparer.OrdinalIgnoreCase).ToList();
        if (diagrams.Count == 0) { Ws.SetStatus("This project has no diagrams yet"); return Task.CompletedTask; }
        return DownloadText(BaseName + ".drawio", "application/vnd.jgraph.mxfile", DrawIoExporter.Export(Ws.Model, diagrams));
    }
}
