using Meridian.Core.Libraries;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Services;
using Meridian.Web.Settings;

namespace Meridian.Web.Libraries;

/// <summary>
/// "Library changed upstream" (docs/LIBRARIES.md): when a project commits a version of its main line, the projects that use one
/// of its packages pinned to an older version, and whose package actually changed, are behind. Their owners and editors are
/// notified (notification kind <see cref="NotificationKinds.LibraryUpdate"/>, "Review update" opens the update review).
/// <list type="bullet">
/// <item>Only people who could accept the update hear about it: an owner or editor of the using project who can read the source
/// project and see the used package there (package permissions). The text names projects and versions, never elements.</item>
/// <item>Optional per user: Settings ▸ Editor ▸ Library changed upstream (on by default).</item>
/// <item>One unread notice per usage: a newer commit doesn't stack a second one while the first is unread.</item>
/// </list>
/// Branch pins follow their head live (nothing to fall behind); baseline pins move when a baseline is made, not on commits.
/// </summary>
public static class LibraryNotices
{
    /// <summary>Notifies the users of projects that use <paramref name="projectId"/> after version <paramref name="number"/> was committed. Returns how many notices were sent.</summary>
    public static async Task<int> VersionCommittedAsync(IMeridianStore db, CollaborationHub hub, Guid projectId, Guid? branchId, int number, string modelJson)
    {
        if (branchId is not null) return 0;   // version pins refer to the main line's versions
        var users = await db.ListProjectUsersAsync(projectId);
        if (users.Count == 0) return 0;
        var committed = ProjectFile.Deserialize(modelJson);
        // The version before this one: a commit that leaves the used package as it was is no news.
        var previous = (await db.ListBranchVersionsAsync(projectId, null)).Where(v => v.Number < number).OrderByDescending(v => v.Number).FirstOrDefault();
        var previousModel = previous is null ? null : await db.LoadVersionAsync(projectId, previous.Id) is { } pj ? ProjectFile.Deserialize(pj) : null;
        var sent = 0;
        foreach (var u in users.Where(u => u.PinKind == LibraryPinKind.Version))
        {
            if (!int.TryParse(u.PinValue, out var pinned) || number <= pinned) continue;
            if (LibraryContent.Extract(committed, u.RootPackageId) is not { } now) continue;   // the package is gone: the review will say so
            if (previousModel is not null && previous!.Number >= pinned && LibraryContent.Extract(previousModel, u.RootPackageId) == now) continue;   // unchanged by this commit
            var then = await LibraryLoader.LoadAtPinAsync(db, hub, projectId, u.PinKind, u.PinValue);
            if (then is not null && LibraryContent.Extract(ProjectFile.Deserialize(then.ModelJson), u.RootPackageId) == now) continue;   // the same as at the pin
            var usingName = (await db.LoadProjectAsync(u.ProjectId))?.Name ?? "a project";
            var package = committed.Find(u.RootPackageId);
            foreach (var member in await db.MembersAsync(u.ProjectId))
            {
                var user = member.User.Id;
                if (await db.RoleOfAsync(u.ProjectId, user) is not (ProjectRole.Owner or ProjectRole.Editor)) continue;
                if (await db.RoleOfAsync(projectId, user) is null) continue;   // can't read the source project
                var view = await PackagePermissions.ViewForAsync(db, projectId, user);
                if (package is not null && view.HidesAnything && (view.IsHidden(package) || !view.CanSeeContents(package))) continue;
                if (!UserPreferences.FromJson(await db.GetUserPreferencesAsync(user)).LibraryUpstreamNotices) continue;
                var key = u.Id.ToString("N");
                if ((await db.ListNotificationsAsync(user, 50)).Any(n => !n.Read && n.Kind == NotificationKinds.LibraryUpdate && n.ElementId == key)) continue;
                await db.AddNotificationAsync(new NotificationDraft(user, NotificationKinds.LibraryUpdate,
                    $"“{u.UsedProjectName}” changed upstream: v{number} is available for “{usingName}” (pinned to v{pinned}).", u.ProjectId, key));
                hub.NotifyUser(user);
                sent++;
            }
        }
        return sent;
    }
}
