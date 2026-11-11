using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Data;
using Meridian.Serialization;

namespace Meridian.Web.Services;

/// <summary>A change refused by package permissions (the message says which package and why; it never names hidden elements).</summary>
public sealed class PermissionDeniedException(string message) : InvalidOperationException(message);

/// <summary>
/// Package permissions on the server: resolving a user's <see cref="AccessView"/> from the store (project role, team
/// memberships, the project's rules), filtering models for users with hidden packages, and checking whole-model writes.
/// Operations from live sessions are checked by <see cref="ProjectSession"/> itself; see docs/PERMISSIONS.md.
/// </summary>
public static class PackagePermissions
{
    /// <summary>The view for a project role: owners manage everything, editors edit unless a rule says otherwise, viewers only read.</summary>
    public static AccessView ViewFor(IEnumerable<PermissionRule> rules, Guid userId, IReadOnlyCollection<Guid> teamIds, ProjectRole? role) => role switch
    {
        ProjectRole.Owner => AccessView.Full,
        ProjectRole.Editor => AccessView.For(rules, userId, teamIds, PackageAccess.Edit, PackageAccess.Manage),
        _ => AccessView.For(rules, userId, teamIds, PackageAccess.Read, PackageAccess.Read),
    };

    /// <summary>A user's view of a project, read from the store.</summary>
    public static async Task<AccessView> ViewForAsync(IMeridianStore db, Guid projectId, Guid userId)
    {
        var role = await db.RoleOfAsync(projectId, userId);
        if (role == ProjectRole.Owner) return AccessView.Full;
        var rules = await db.ListPackagePermissionsAsync(projectId);
        if (rules.Count == 0) return ViewFor([], userId, [], role);
        return ViewFor(rules.Select(r => r.Rule), userId, await db.UserTeamIdsAsync(userId), role);
    }

    /// <summary>The model as <paramref name="view"/> may see it: <paramref name="model"/> itself when nothing is hidden, else a redacted copy.</summary>
    /// <remarks>Library-update suspect marks of the earlier, named format are rewritten to ids only in any case (<see cref="Core.Libraries.LibraryChangeMark"/>).</remarks>
    public static ModelRepository Redact(ModelRepository model, AccessView view)
    {
        if (!view.HidesAnything && !model.AllElements.Any(e => e.Fields.Count > 0 && Core.Libraries.LibraryChangeMark.IsLegacy(e.Get(ReqFields.LibraryChange)))) return model;
        var copy = ProjectFile.Copy(model);
        ModelRedactor.Redact(copy, view);
        return copy;
    }

    /// <summary>A project document as <paramref name="view"/> may see it (older, named library-update marks rewritten to ids only).</summary>
    public static string RedactJson(string modelJson, AccessView view)
    {
        if (!view.HidesAnything && !modelJson.Contains($"\"{ReqFields.LibraryChange}\"", StringComparison.Ordinal)) return modelJson;
        var copy = ProjectFile.Deserialize(modelJson);
        ModelRedactor.Redact(copy, view);   // also rewrites named marks of the earlier format
        return ProjectFile.Serialize(copy);
    }

    /// <summary>
    /// Throws <see cref="PermissionDeniedException"/> unless <paramref name="userId"/> may turn <paramref name="before"/> into
    /// <paramref name="after"/> (merges, version restore, code-sync decisions). A null user is the system (code sync runs).
    /// </summary>
    public static async Task EnsureCanReplaceAsync(IMeridianStore db, Guid projectId, Guid? userId, ModelRepository before, ModelRepository after)
    {
        if (userId is not { } u) return;
        var view = await ViewForAsync(db, projectId, u);
        if (PermissionCheck.CheckReplace(before, after, view) is { } why) throw new PermissionDeniedException(why);
    }

    /// <summary>
    /// Throws unless the user sees the whole project. Workflows that show or rebuild whole models from stored versions (merging,
    /// code-sync review) are not available to users with hidden packages: their conflict lists and diffs would show hidden content.
    /// </summary>
    public static async Task EnsureSeesEverythingAsync(IMeridianStore db, Guid projectId, Guid? userId, string what)
    {
        if (userId is not { } u) return;
        if ((await ViewForAsync(db, projectId, u)).HidesAnything)
            throw new PermissionDeniedException($"{what} needs access to the whole project, and some packages are hidden from you. Ask a project owner.");
    }
}
