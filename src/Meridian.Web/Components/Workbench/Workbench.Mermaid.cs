using System.Text;
using Meridian.Serialization.Mermaid;
using Meridian.Web.Services;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>
/// Mermaid: Paste Mermaid… (Project menu ▸ Import, ⌘K), .mmd / .mermaid / .md files in "Open a file as a new project" and
/// "Import into this project…", and Export diagram as Mermaid / Copy diagram as Mermaid. See docs/MERMAID.md.
/// </summary>
public partial class Workbench
{
    private IEnumerable<CommandPalette.Command> MermaidPaletteCommands() =>
    [
        new("Paste Mermaid… (import diagrams from Mermaid text)", "Command", Icons.Material.Outlined.Schema, () => PasteMermaid()),
        new("Export diagram as Mermaid (.mmd)", "Command", Icons.Material.Outlined.FileDownload, ExportMermaid),
        new("Copy diagram as Mermaid", "Command", Icons.Material.Outlined.ContentCopy, CopyMermaid),
    ];

    /// <summary>Shows the Paste Mermaid dialog (optionally with a file's text) and imports what it returns into the selection's package.</summary>
    public async Task PasteMermaid(string? text = null, string? fileName = null)
    {
        if (Ws.ReadOnly) { Ws.SetStatus("View-only: you can't import into this project"); return; }
        if (await MermaidDialog.ShowAsync(Dialogs, text, fileName) is not { } answer) return;
        var result = MermaidImport.Apply(Ws, answer.Text, answer.Options, fileName ?? "pasted text");
        if (result == null) return;
        if (result.Warnings.Count > 0) OpenDock(3);
        Snackbar.Add(result.Summary, result.Diagrams.Count > 0 ? Severity.Success : Severity.Warning);
    }

    private static async Task<string> ReadMermaid(IBrowserFile file)
    {
        if (file.Size > MermaidImport.MaxBytes) throw new InvalidDataException($"{file.Name} is larger than {MermaidImport.MaxBytes / (1024 * 1024)} MB.");
        await using var s = file.OpenReadStream(MermaidImport.MaxBytes);
        using var reader = new StreamReader(s, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    /// <summary>"Open a file as a new project" with a .mmd, .mermaid or Markdown file.</summary>
    private async Task OpenMermaidProject(IBrowserFile file)
    {
        var text = await ReadMermaid(file);
        var name = Path.GetFileNameWithoutExtension(file.Name);
        var (model, report) = Meridian.Serialization.PlantUml.PlantUmlImporter.IsPlantUmlFile(file.Name)
            ? Meridian.Serialization.PlantUml.PlantUmlImporter.ImportAsNewProject(text, name)
            : MermaidImporter.ImportAsNewProject(text, name, MermaidImporter.IsMarkdownFile(file.Name));
        if (!model.Diagrams.Any())
        {
            foreach (var w in report.Warnings.Take(50)) Ws.Log("  warning: " + w);
            Snackbar.Add($"{file.Name} contains no diagram Meridian can import (see Output).", Severity.Warning);
            OpenDock(3);
            return;
        }
        await CreateProjectFrom(model, name);
        if (model.Diagrams.FirstOrDefault() is { } first && Ws.Model.Find(first.Id) is Core.Model.Diagram opened) Ws.Open(opened);
        Ws.Log($"Imported {file.Name}: {Plural.Of(report.Elements, "element")}, {Plural.Of(report.Relationships, "relationship")}, {Plural.Of(report.Warnings.Count, "warning")}.");
        foreach (var w in report.Warnings.Take(200)) Ws.Log("  warning: " + w);
        if (report.Warnings.Count > 0) OpenDock(3);
        Snackbar.Add($"Imported {file.Name} as a new project", Severity.Success);
    }

    private Task ExportMermaid()
    {
        if (MermaidText() is not { } text) return Task.CompletedTask;
        return DownloadText(Ws.Active!.Diagram.Name + ".mmd", "text/plain", text);
    }

    private async Task CopyMermaid()
    {
        if (MermaidText() is not { } text) return;
        var problem = await JS.InvokeAsync<string?>("meridian.copyText", text);
        if (problem != null) Snackbar.Add(problem, Severity.Warning);
        else Ws.SetStatus($"Copied {Ws.Active!.Diagram.Name} to the clipboard as Mermaid");
    }

    private string? MermaidText()
    {
        if (Ws.Active is not { } c) { Ws.SetStatus("Open a diagram first"); return null; }
        if (!MermaidExporter.CanExport(c.Diagram))
        {
            Ws.SetStatus("Mermaid export supports activity, state machine, sequence, block definition, class and requirement diagrams");
            return null;
        }
        return MermaidExporter.Export(Ws.Model, c.Diagram);
    }

    private bool CanExportMermaid => MermaidExporter.CanExport(Ws.Active?.Diagram);
}
