using Meridian.Core.Libraries;
using Meridian.Core.Model;
using Meridian.Core.Permissions;

namespace Meridian.Serialization;

/// <summary>Builds library payloads (docs/LIBRARIES.md) from a source project's model.</summary>
public static class LibraryContent
{
    /// <summary>
    /// The JSON of package <paramref name="rootPackageId"/> of <paramref name="source"/> as <paramref name="view"/> may see it, or
    /// null when there is no such package or it (or its contents) is hidden from the user. Packages hidden inside it are left
    /// out exactly as in the source project (<see cref="ModelRedactor"/>).
    /// </summary>
    public static string? Extract(ModelRepository source, string rootPackageId, AccessView? view = null)
    {
        if (source.Find(rootPackageId) is not { Kind: ElementKind.Package } package) return null;
        if (view is null || !view.HidesAnything) return ProjectFile.SerializeSubtree(package);
        if (view.IsHidden(package) || !view.CanSeeContents(package)) return null;
        var copy = ProjectFile.Deserialize(ProjectFile.Serialize(source));
        ModelRedactor.Redact(copy, view);
        return copy.Find(rootPackageId) is { } redacted ? ProjectFile.SerializeSubtree(redacted) : null;
    }

    /// <summary>The packages of a model that can be shared as libraries (every package, with its qualified name).</summary>
    public static IReadOnlyList<(string Id, string Path)> Packages(ModelRepository model) =>
        model.OfKind(ElementKind.Package).Where(p => !model.IsLibraryElement(p))
             .Select(p => (p.Id, p.QualifiedName)).OrderBy(p => p.QualifiedName, StringComparer.OrdinalIgnoreCase).ToList();
}
