using Meridian.Core.Model;
using Meridian.Serialization;
using Microsoft.AspNetCore.Components.Forms;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>MagicDraw / Cameo (.mdzip, .mdxml) import: as a new project, or re-imported into the open one by element id.</summary>
public partial class Workbench
{
    private static bool IsMagicDrawFile(string name) => MagicDrawImporter.IsSupported(name);

    /// <summary>Uploads are read asynchronously into a private temp file (zips need random access).</summary>
    private static async Task<(MagicDrawImportReport Report, ModelRepository? Model)> ReadMagicDraw(IBrowserFile file, ModelRepository? into)
    {
        var dir = Directory.CreateTempSubdirectory("meridian-md-");
        try
        {
            var tmp = Path.Combine(dir.FullName, "project" + Path.GetExtension(file.Name).ToLowerInvariant());
            await using (var src = file.OpenReadStream(MaxUpload))
            await using (var dst = File.Create(tmp)) await src.CopyToAsync(dst);
            if (into != null) return (MagicDrawImporter.ImportInto(into, tmp), null);
            var (model, report) = MagicDrawImporter.Import(tmp);
            return (report, model);
        }
        finally
        {
            try { dir.Delete(true); } catch (IOException) { }
        }
    }

    private void LogMagicDrawReport(MagicDrawImportReport report, string fileName)
    {
        foreach (var line in report.Lines(fileName)) Ws.Log(line);
        OpenDock(3);
    }

    private async Task OpenMagicDrawProject(IBrowserFile file)
    {
        var (report, model) = await ReadMagicDraw(file, null);
        var usages = await OfferCameoUsagesAsync(model!, report);   // used projects already in Meridian (Workbench.Libraries.cs)
        var created = await CreateProjectFrom(model!, Path.GetFileNameWithoutExtension(file.Name));
        if (created is { } projectId && usages.Count > 0) await CreateCameoUsagesAsync(projectId, usages);
        LogMagicDrawReport(report, file.Name);
        Snackbar.Add($"Imported {file.Name}: {report.Elements} elements, {report.Diagrams} diagrams ({report.Warnings.Count} warnings, see Output)", Severity.Success);
    }

    private async Task ReimportMagicDraw(IBrowserFile file)
    {
        var (report, _) = await ReadMagicDraw(file, Ws.Model);
        LogMagicDrawReport(report, file.Name);
        Snackbar.Add($"Re-imported {file.Name}: {report.Added} added, {report.Updated} updated", Severity.Success);
    }
}
