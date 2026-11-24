using System.Text;
using Meridian.Core.CodeSync;
using Meridian.Core.Model;
using Meridian.Serialization.DiagramFiles;
using Meridian.Serialization.Mermaid;

namespace Meridian.Web.CodeSync;

/// <summary>
/// Diagram files in a synced repository ("diagrams as code"): Mermaid, PlantUML and draw.io files become diagrams in a
/// <see cref="DiagramsPackage"/> package of the model package, one package per folder, through the folder import. Only files
/// whose content changed are imported again, so diagrams of untouched files keep their ids and any layout made since; a
/// file deleted from the repository takes its diagrams with it. See docs/CODE-SYNC.md, "Diagram files".
/// </summary>
public sealed partial class CodeSyncRunner
{
    /// <summary>Name of the package that receives a repository's diagram files.</summary>
    public const string DiagramsPackage = "Diagrams";

    /// <summary>A Mermaid, PlantUML or draw.io file code sync picks up (not binary .drawio.png or Visio files, which the
    /// archive reader leaves out anyway).</summary>
    public static bool IsDiagramFile(string path) =>
        DiagramFolderImport.IsDiagramFile(path) && !Meridian.Serialization.DrawIo.DrawIoFile.IsBinary(path);

    /// <summary>The request's diagram files, when the connection syncs them.</summary>
    private static IReadOnlyList<DiagramSourceFile> DiagramFilesOf(CodeSyncRequest request, Hosts.CodeRepoConfig? connection) =>
        connection?.EffectiveDiagramFiles == true
            ? request.Files.Where(f => IsDiagramFile(f.Path)).Select(f => new DiagramSourceFile(f.Path, Encoding.UTF8.GetBytes(f.Content))).ToList()
            : [];

    /// <summary>A diagram made from a diagram file (by code sync or a folder import).</summary>
    public static bool IsFromDiagramFile(Diagram d) => d.Get(DiagramFolderImport.SourceField).Length > 0;

    /// <summary>
    /// Imports <paramref name="files"/> into the repository's Diagrams package. With a full snapshot, diagrams of files
    /// that are gone are removed. Returns what changed, as element ids.
    /// </summary>
    internal static CodeSyncChanges SyncDiagramFiles(ModelRepository model, IReadOnlyList<DiagramSourceFile> files, string? rootPackage, string repoId, bool fullSnapshot)
    {
        var package = FindDiagramsPackage(model, rootPackage, repoId);
        if (package is null && files.Count == 0) return new CodeSyncChanges([], [], [], []);
        var before = model.AllElements.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        package ??= CreateDiagramsPackage(model, rootPackage, repoId);
        var result = DiagramFolderImport.Import(model, package, files, new DiagramFolderOptions
        {
            PackagePerFolder = true,
            // Blocks, actors and requirements named like elements already in the model (the synced code too) are reused.
            Match = MermaidMatchMode.MatchByName,
            ReplacePrevious = true,
            SkipUnchanged = true,
            RemoveMissing = fullSnapshot,
        });
        var after = model.AllElements.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var warnings = result.Warnings.Select(w => "Diagram file " + w).ToList();
        return new CodeSyncChanges(after.Where(id => !before.Contains(id)).ToList(), [], before.Where(id => !after.Contains(id)).ToList(), warnings);
    }

    private static Element? FindDiagramsPackage(ModelRepository model, string? rootPackage, string repoId)
    {
        if (CodeModelMapper.FindRootPackage(model, rootPackage) is not { } root) return null;
        var packages = root.OwnedElements.Where(c => c.Kind == ElementKind.Package && c.Name == DiagramsPackage).ToList();
        return packages.FirstOrDefault(p => p.Get(CodeFieldKeys.Repo) == repoId) ?? packages.FirstOrDefault();
    }

    private static Element CreateDiagramsPackage(ModelRepository model, string? rootPackage, string repoId)
    {
        Element owner = model.Root;
        foreach (var name in CodeModelMapper.RootPackageNames(rootPackage))
            owner = owner.OwnedElements.FirstOrDefault(c => c.Kind == ElementKind.Package && c.Name == name) ?? model.Create(owner, ElementKind.Package, name);
        var package = model.Create(owner, ElementKind.Package, DiagramsPackage);
        // Marks the repository as synced (a repository of diagram files only is then not "first imported" again). No
        // code symbol, so the code mapper never treats the package as code that disappeared.
        model.Set(package, CodeFieldKeys.Repo, repoId);
        return package;
    }

    private static CodeSyncChanges Merge(CodeSyncChanges a, CodeSyncChanges b) =>
        b.IsEmpty && b.Warnings.Count == 0 ? a
            : new CodeSyncChanges([.. a.Added, .. b.Added], [.. a.Changed, .. b.Changed], [.. a.Removed, .. b.Removed], [.. a.Warnings, .. b.Warnings])
            { TrivialDiagramsRemoved = a.TrivialDiagramsRemoved };
}
