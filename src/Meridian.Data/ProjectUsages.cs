using Meridian.Core.Libraries;

namespace Meridian.Data;

/// <summary>
/// One row of <c>project_usages</c> (docs/LIBRARIES.md): <see cref="ProjectId"/> uses package <see cref="RootPackageId"/> of
/// <see cref="UsedProjectId"/>, pinned to a version, a branch head or a baseline, and shows it below <see cref="MountPointId"/>
/// (null: the model root). <see cref="UsedProjectName"/> is empty when the used project no longer exists.
/// </summary>
public sealed record ProjectUsage(Guid Id, Guid ProjectId, Guid UsedProjectId, string UsedProjectName, string RootPackageId,
                                  LibraryPinKind PinKind, string PinValue, string? MountPointId, Guid? AddedBy, string AddedByName, DateTime AddedAt)
{
    public LibraryRequest Request => new(Id.ToString("N"), UsedProjectId, RootPackageId, PinKind, PinValue, MountPointId ?? "");
}

/// <summary>Validation shared by every store.</summary>
public static class ProjectUsageRules
{
    public const int MaxIdLength = 200;

    public static void Check(Guid projectId, Guid usedProjectId, string rootPackageId, string pinValue, string? mountPointId)
    {
        if (projectId == usedProjectId) throw new InvalidOperationException("A project can't use itself.");
        if (string.IsNullOrWhiteSpace(rootPackageId) || rootPackageId.Length > MaxIdLength)
            throw new InvalidOperationException("Pick the package to use.");
        CheckPin(pinValue);
        if (mountPointId is { Length: > MaxIdLength }) throw new InvalidOperationException("That mount point is not a package of this project.");
    }

    public static void CheckPin(string pinValue)
    {
        if (string.IsNullOrWhiteSpace(pinValue) || pinValue.Length > MaxIdLength) throw new InvalidOperationException("Pick a version, branch or baseline.");
    }
}
