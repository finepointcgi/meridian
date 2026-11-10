using Meridian.Core.Permissions;

namespace Meridian.Data;

/// <summary>
/// One row of <c>package_permissions</c>: <see cref="Level"/> for a user or a team on a package (by element id) of a project and
/// everything below it. <see cref="PrincipalName"/> is the user's display name or the team's name (empty when it no longer exists).
/// </summary>
public sealed record PackagePermission(Guid ProjectId, string ElementId, PrincipalKind PrincipalKind, Guid PrincipalId, PackageAccess Level,
                                       string PrincipalName, DateTime UpdatedAt)
{
    public PermissionRule Rule => new(ElementId, PrincipalKind, PrincipalId, Level);
}

/// <summary>A team with a role on a project (a principal package permissions can name).</summary>
public sealed record ProjectTeam(Guid Id, string Name, ProjectRole Role);

/// <summary>Encoding of package permission columns, shared by every store.</summary>
public static class PackagePermissionCodes
{
    public const int MaxElementIdLength = 200;

    public static string Kind(PrincipalKind k) => k == PrincipalKind.Team ? "team" : "user";
    public static PrincipalKind Kind(string s) => s == "team" ? PrincipalKind.Team : PrincipalKind.User;

    public static string Level(PackageAccess a) => a switch
    {
        PackageAccess.None => "none",
        PackageAccess.Read => "read",
        PackageAccess.Edit => "edit",
        PackageAccess.Manage => "manage",
        _ => throw new ArgumentOutOfRangeException(nameof(a)),
    };

    /// <summary>Unknown values read as "none" (fail closed).</summary>
    public static PackageAccess Level(string s) => s switch
    {
        "read" => PackageAccess.Read,
        "edit" => PackageAccess.Edit,
        "manage" => PackageAccess.Manage,
        _ => PackageAccess.None,
    };

    public static string CheckElementId(string elementId)
    {
        if (string.IsNullOrWhiteSpace(elementId) || elementId.Length > MaxElementIdLength)
            throw new InvalidOperationException("A package permission needs the package's element id.");
        return elementId;
    }
}
