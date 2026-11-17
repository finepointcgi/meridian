using System.IO.Compression;
using Meridian.Data;
using Meridian.Web.Admin;
using Meridian.Web.Services;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>
/// Projects with their history: Export ▸ Download project with history (owners) writes the project, every version,
/// comments, reviews and baselines as a backup-format .zip; "Open a file as a new project" with such a .zip recreates
/// them as a new project owned by the person opening it. This is how a project moves from the desktop's local
/// workspace to a team server without leaving its history behind (docs-site/docs/version-control.md).
/// </summary>
public partial class Workbench
{
    /// <summary>Owners see every package (package permissions never hide anything from them), so only they may take it all.</summary>
    private bool CanDownloadWithHistory => Ws.Session != null && Ws.Role == ProjectRole.Owner;

    private async Task DownloadProjectWithHistory()
    {
        if (Ws.Session is not { } session || Ws.User is null) return;
        if (!CanDownloadWithHistory) { Snackbar.Add("Only a project owner can download the project with its history.", Severity.Warning); return; }
        var backups = Services.GetRequiredService<BackupService>();
        using var ms = new MemoryStream();
        var manifest = await backups.WriteProjectAsync(ms, session.ProjectId, Ws.User.Username);
        var versions = manifest.Projects.FirstOrDefault()?.Versions ?? 0;
        await Download(BackupService.ProjectFileName(session.Name), "application/zip", ms.ToArray());
        Ws.Log($"Downloaded {session.Name} with {Plural.Of(versions, "version")}, comments, reviews and baselines");
    }

    /// <summary>"Open a file as a new project" with a .zip: a project archive (or backup) opens with its history.</summary>
    private async Task OpenProjectArchive(IBrowserFile file)
    {
        if (Ws.User is null) return;
        var temp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
        await using (temp)
        {
            await using (var src = file.OpenReadStream(MaxUpload)) await src.CopyToAsync(temp);
            temp.Position = 0;
            IReadOnlyList<BackupService.RestoreResult> results;
            try
            {
                using var zip = AdminHosting.OpenBackup(temp);
                results = await Services.GetRequiredService<BackupService>().ImportProjectsAsync(zip, Ws.User);
            }
            catch (InvalidDataException ex)
            {
                ErrorWithGuide($"Could not open {file.Name}: {ex.Message} A .zip opens as a project when it was made with Export ▸ Download project with history (or is a server backup).", Meridian.Web.Hosting.UserGuide.Pages.VersionControl);
                return;
            }
            foreach (var r in results)
                Ws.Log(r.Error is null ? $"Opened {r.Name} with {Plural.Of(r.Versions, "version")}" : $"Could not open {r.Name}: {r.Error}");
            var opened = results.Where(r => r.NewId is not null).ToList();
            if (opened.Count == 0)
            {
                ErrorWithGuide($"Could not open {file.Name}: {results.FirstOrDefault()?.Error ?? "it holds no project."}", Meridian.Web.Hosting.UserGuide.Pages.VersionControl);
                return;
            }
            await OpenProject(opened[^1].NewId!.Value);
            Snackbar.Add(opened.Count == 1
                ? $"Opened {opened[0].Name} with {Plural.Of(opened[0].Versions, "version")} of history"
                : $"Opened {opened.Count} projects with their history", opened.Count == results.Count ? Severity.Success : Severity.Warning);
        }
    }
}
