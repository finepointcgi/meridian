using System.Text;
using Meridian.Core.Model;
using Meridian.Serialization.DiagramFiles;
using Meridian.Serialization.Mermaid;
using Microsoft.JSInterop;

namespace Meridian.Web.Services;

/// <summary>A diagram file read by the browser: text, or base64 for a .drawio.png.</summary>
public sealed record BrowserDiagramFile(string Path, string? Text, string? Base64)
{
    public DiagramSourceFile ToSource() =>
        new(Path, Base64 is { Length: > 0 } b ? Convert.FromBase64String(b) : Encoding.UTF8.GetBytes(Text ?? ""));
}

/// <summary>
/// Collects the files of a folder the browser read (meridian.pickDiagramFolder): batches arrive through <see cref="Add"/>,
/// then <see cref="Done"/> or <see cref="Cancel"/>.
/// </summary>
public sealed class DiagramFolderReceiver
{
    private readonly List<DiagramSourceFile> _files = new();
    private long _bytes;
    private readonly TaskCompletionSource<Received?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public sealed record Received(string Folder, IReadOnlyList<DiagramSourceFile> Files, int Skipped);

    public Task<Received?> Result => _done.Task;

    [JSInvokable]
    public void Add(BrowserDiagramFile[] batch)
    {
        foreach (var f in batch)
        {
            if (_files.Count >= DiagramFolderImport.MaxFiles) return;
            try
            {
                var source = f.ToSource();
                _bytes += source.Content.LongLength;
                if (_bytes > DiagramFolderImport.MaxTotalBytes) return;
                _files.Add(source);
            }
            catch (FormatException) { }   // malformed base64 from the browser: ignore that file
        }
    }

    [JSInvokable]
    public void Done(string folder, int skipped) => _done.TrySetResult(new Received(folder, _files, skipped));

    [JSInvokable]
    public void Cancel() => _done.TrySetResult(null);
}

/// <summary>
/// Diagram files (a folder of Mermaid and draw.io files, or a few dropped / uploaded ones) into the open project: one undo
/// step, the report in the Output panel, the first new diagram opens.
/// </summary>
public static class DiagramFilesImport
{
    /// <summary>Default options: match by name and replace earlier diagrams when some of the files were imported here before.</summary>
    public static DiagramFolderOptions DefaultOptions(Workspace ws, IReadOnlyList<DiagramSourceFile> files, Element target)
    {
        var before = DiagramFolderImport.Stamped(ws.Model, target);
        var again = files.Any(f => before.Contains(f.Path.Replace('\\', '/').Trim('/')));
        return new DiagramFolderOptions { Match = again ? MermaidMatchMode.MatchByName : MermaidMatchMode.CreateNew };
    }

    /// <summary>Imports and reports. Returns null when the project (or the target package) can't be changed.</summary>
    public static DiagramFolderResult? Apply(Workspace ws, IReadOnlyList<DiagramSourceFile> files, DiagramFolderOptions options, string source, Element? target = null)
    {
        if (ws.ReadOnly)
        {
            ws.SetStatus("View-only: you can't import into this project");
            return null;
        }
        target ??= MermaidImport.TargetFor(ws);
        if (!ws.CanEdit(target))
        {
            ws.SetStatus(ws.Access.ContentsDeniedMessage(target).Replace("your change was", "nothing was"));
            return null;
        }
        var result = DiagramFolderImport.Import(ws.Model, target, files, options);
        ws.Log($"Imported diagrams from {source}: {result.Summary}.");
        foreach (var d in result.Diagrams.Take(200)) ws.Log($"  {Metamodel(d)} “{d.Name}” ({d.Get(DiagramFolderImport.SourceField)})");
        foreach (var w in result.Warnings.Take(200)) ws.Log("  warning: " + w);
        if (result.Warnings.Count > 200) ws.Log($"  … and {result.Warnings.Count - 200} more warnings.");
        ws.SetStatus(result.Summary + (result.Diagrams.Count > 0 ? " — ⌘Z undoes it" : ""));
        if (result.Diagrams.Count > 0) ws.Open(result.Diagrams[0]);
        return result;
    }

    private static string Metamodel(Diagram d) => Meridian.Core.Language.Metamodel.DisplayName(d.DiagramType);
}
