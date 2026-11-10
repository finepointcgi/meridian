using Meridian.Core.Model;

namespace Meridian.Core.Libraries;

/// <summary>
/// The write rule for library (used project) elements, applied to replication operations: an operation that changes a library
/// element, adds to or removes from it, moves it, or edits one of its diagrams is refused. Adding relationships to library
/// elements, typing features by them and showing them on the project's own diagrams are allowed (those change project elements).
/// <see cref="Permissions.PermissionCheck.Check"/> applies it first, for everyone (owners included).
/// </summary>
public static class LibraryGuard
{
    /// <summary>Null when <paramref name="op"/> leaves library elements alone, else why it is refused.</summary>
    public static string? Check(ModelRepository model, ModelOp op)
    {
        if (!model.HasLibraries) return null;
        return op switch
        {
            SetFieldOp sf => Library(model, sf.ElementId),
            AttachOp a => Library(model, a.OwnerId),
            DetachOp d => Library(model, d.ElementId) ?? Mounts(model, d.ElementId),
            MoveOp mv => Library(model, mv.ElementId) ?? Library(model, mv.NewOwnerId),
            NodeInsertOp x => Library(model, x.DiagramId),
            NodeStyleOp x => Library(model, x.DiagramId),
            NodeRemoveOp x => Library(model, x.DiagramId),
            NodeBoundsOp x => Library(model, x.DiagramId),
            EdgeInsertOp x => Library(model, x.DiagramId),
            EdgeStyleOp x => Library(model, x.DiagramId),
            EdgeRemoveOp x => Library(model, x.DiagramId),
            EdgeWaypointsOp x => Library(model, x.DiagramId),
            _ => null,
        };
    }

    private static string? Library(ModelRepository model, string id) =>
        model.Find(id) is { } e ? model.ReadOnlyReason(e) : null;

    /// <summary>Removing a package that shows a library would take the library with it.</summary>
    private static string? Mounts(ModelRepository model, string id)
    {
        if (model.Find(id) is not { } e) return null;
        foreach (var m in model.Libraries)
            if (ReferenceEquals(m.MountPoint, e) || e.IsAncestorOf(m.MountPoint))
                return $"“{e.DisplayName}” shows the used project “{m.Info.SourceProjectName}”. Remove the usage or change its mount point first.";
        return null;
    }
}
