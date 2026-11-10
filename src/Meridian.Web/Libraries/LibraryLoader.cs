using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Meridian.Core.Libraries;
using Meridian.Core.Permissions;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Services;

namespace Meridian.Web.Libraries;

/// <summary>A source project's model at a pin: its JSON and what the pin resolved to.</summary>
public sealed record PinnedSource(string ModelJson, string Resolved, string Label);

/// <summary>
/// Loads used projects (docs/LIBRARIES.md) from the store: resolves pins (a version number, a branch head — live when someone is
/// editing it — or a baseline), extracts the shared package, follows the libraries' own usages transitively
/// (<see cref="LibraryResolver"/>), and narrows the result to what one user may read.
/// </summary>
public static class LibraryLoader
{
    // Content at an immutable pin (a version or a baseline) never changes: cached across sessions (bounded).
    private static readonly ConcurrentDictionary<(Guid Project, string Resolved, string Package), string> Immutable = new();
    private const int ImmutableCacheSize = 64;

    /// <summary>Loads a source project's model at a pin, or null when the project, version, branch or baseline is gone.</summary>
    public static async Task<PinnedSource?> LoadAtPinAsync(IMeridianStore db, CollaborationHub? hub, Guid projectId, LibraryPinKind kind, string value)
    {
        switch (kind)
        {
            case LibraryPinKind.Version:
                if (!int.TryParse(value, out var number)) return null;
                var version = (await db.ListVersionsAsync(projectId)).FirstOrDefault(v => v.Number == number);
                if (version is null || await db.LoadVersionAsync(projectId, version.Id) is not { } vjson) return null;
                return new PinnedSource(vjson, $"v{number}", $"v{number}");
            case LibraryPinKind.Baseline:
                if (!long.TryParse(value, out var baselineId)) return null;
                var baseline = (await db.ListBaselinesAsync(projectId)).FirstOrDefault(b => b.Id == baselineId);
                if (baseline is null || await db.LoadVersionAsync(projectId, baseline.VersionId) is not { } bjson) return null;
                return new PinnedSource(bjson, $"v{baseline.VersionNumber}", LibraryPins.Label(kind, value, baseline.Name));
            default:
                Guid? branchId = null;
                if (!string.Equals(value, Branching.MainBranch, StringComparison.OrdinalIgnoreCase))
                {
                    if ((await db.ListBranchesAsync(projectId)).FirstOrDefault(b => b.Name == value) is not { } branch) return null;
                    branchId = branch.Id;
                }
                string json;
                if (hub?.Find(projectId, branchId) is { } live) json = live.Snapshot();   // what people are editing right now
                else if (branchId is { } b) { if (await db.LoadBranchAsync(b) is not { } models) return null; json = models.HeadJson; }
                else { if (await db.LoadProjectAsync(projectId) is not { } row) return null; json = row.ModelJson; }
                return new PinnedSource(json, value, LibraryPins.Label(kind, value));
        }
    }

    /// <summary>Short content hash, so a branch-head pin can tell whether the shared package changed.</summary>
    public static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)), 0, 6).ToLowerInvariant();

    /// <summary>One library: the package at its pin (as the system sees it), and the usages of its own project.</summary>
    /// <param name="learnName">Told the names of the projects the library uses (for loading them next).</param>
    public static async Task<LibraryFetch?> FetchAsync(IMeridianStore db, CollaborationHub? hub, LibraryRequest req, string projectName,
                                                       Action<Guid, string>? learnName = null)
    {
        var immutable = req.PinKind != LibraryPinKind.Branch;
        var source = await LoadAtPinAsync(db, hub, req.SourceProjectId, req.PinKind, req.PinValue);
        if (source is null) return null;
        var key = (req.SourceProjectId, source.Resolved, req.RootPackageId);
        if (!immutable || !Immutable.TryGetValue(key, out var rootJson))
        {
            rootJson = LibraryContent.Extract(ProjectFile.Deserialize(source.ModelJson), req.RootPackageId);
            if (rootJson is null) return null;
            if (immutable)
            {
                if (Immutable.Count >= ImmutableCacheSize) Immutable.Clear();
                Immutable[key] = rootJson;
            }
        }
        var resolved = req.PinKind == LibraryPinKind.Branch ? $"{source.Resolved}#{Hash(rootJson)}" : source.Resolved;
        var info = new LibraryInfo(req.UsageId, req.SourceProjectId, projectName, req.RootPackageId, req.PinKind, req.PinValue, source.Label,
                                   req.MountId, req.ViaUsageId, resolved);
        var usages = await db.ListProjectUsagesAsync(req.SourceProjectId);
        foreach (var u in usages) learnName?.Invoke(u.UsedProjectId, u.UsedProjectName);
        var uses = usages.Select(u => u.Request).ToList();
        return new LibraryFetch(info, rootJson, uses);
    }

    /// <summary>Every library of a project (transitively), as the system sees them.</summary>
    public static async Task<LibraryResolution> ResolveAsync(IMeridianStore db, CollaborationHub? hub, Guid projectId)
    {
        var usages = await db.ListProjectUsagesAsync(projectId);
        if (usages.Count == 0) return LibraryResolution.Empty;
        var names = new Dictionary<Guid, string>();
        foreach (var u in usages) names[u.UsedProjectId] = u.UsedProjectName;
        return await LibraryResolver.ResolveAsync(projectId, usages.Select(u => u.Request), async req =>
        {
            if (!names.TryGetValue(req.SourceProjectId, out var name) || string.IsNullOrEmpty(name)) return null;   // the used project was deleted
            return await FetchAsync(db, hub, req, name, (id, n) => names.TryAdd(id, n));
        });
    }

    /// <summary>
    /// The libraries <paramref name="userId"/> may read: they need a role on the source project, and packages hidden from them
    /// there stay hidden (<see cref="LibraryContent.Extract"/> with their view of the source project).
    /// </summary>
    public static async Task<IReadOnlyList<LibraryPayload>> ForUserAsync(IMeridianStore db, CollaborationHub? hub, LibraryResolution all, Guid userId)
    {
        if (all.Payloads.Count == 0) return [];
        var list = new List<LibraryPayload>();
        foreach (var p in all.Payloads)
        {
            var source = p.Info.SourceProjectId;
            if (await db.RoleOfAsync(source, userId) is null) continue;
            var view = await PackagePermissions.ViewForAsync(db, source, userId);
            if (!view.HidesAnything) { list.Add(p); continue; }
            if (await LoadAtPinAsync(db, hub, source, p.Info.PinKind, p.Info.PinValue) is not { } pinned) continue;
            if (LibraryContent.Extract(ProjectFile.Deserialize(pinned.ModelJson), p.Info.RootPackageId, view) is { } mine) list.Add(p with { RootJson = mine });
        }
        return list;
    }

    /// <summary>True when <paramref name="userId"/> may use package <paramref name="packageId"/> of <paramref name="projectId"/> (read on both).</summary>
    public static async Task<string?> CanReadAsync(IMeridianStore db, Guid projectId, string packageId, Guid userId, string sourceJson)
    {
        if (await db.RoleOfAsync(projectId, userId) is null) return "You need at least read access to the source project.";
        var view = await PackagePermissions.ViewForAsync(db, projectId, userId);
        var model = ProjectFile.Deserialize(sourceJson);
        if (model.Find(packageId) is not { Kind: Core.Model.ElementKind.Package } pkg) return "That package doesn't exist in the source project at this pin.";
        if (view.IsHidden(pkg) || !view.CanSeeContents(pkg)) return "That package is hidden from you in the source project.";
        return null;
    }
}
