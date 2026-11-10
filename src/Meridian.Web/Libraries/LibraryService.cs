using Meridian.Core.Analysis;
using Meridian.Core.Libraries;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Services;

namespace Meridian.Web.Libraries;

/// <summary>A newer pin for a usage: the next version, a changed branch head or a newer baseline.</summary>
public sealed record LibraryUpdateOffer(Guid UsageId, string LibraryName, string CurrentLabel, LibraryPinKind Kind, string Value, string Label);

/// <summary>The pins a source project offers: committed versions, branches and baselines.</summary>
public sealed record LibraryPinOptions(IReadOnlyList<VersionInfo> Versions, IReadOnlyList<string> Branches, IReadOnlyList<BaselineInfo> Baselines);

/// <summary>
/// A prepared library update, waiting for review: what changes between the pins (<see cref="Diff"/>, with the library content at
/// both pins for highlighting) and which project elements refer to changed or removed library elements (they become suspect).
/// </summary>
public sealed class LibraryUpdatePlan
{
    public required ProjectUsage Usage { get; init; }
    public required string LibraryName { get; init; }
    public required LibraryPinKind ToKind { get; init; }
    public required string ToValue { get; init; }
    public required string FromLabel { get; init; }
    public required string ToLabel { get; init; }
    public required ModelRepository Before { get; init; }
    public required ModelRepository After { get; init; }
    public required IReadOnlyList<ElementDiff> Diff { get; init; }
    public required IReadOnlySet<string> Changed { get; init; }
    public required IReadOnlySet<string> Removed { get; init; }
    /// <summary>Project elements that refer to changed library elements (suspect after the update) …</summary>
    public required IReadOnlyList<LibraryReference> Affected { get; init; }
    /// <summary>… of which these refer to elements the update removes (missing references after it).</summary>
    public required IReadOnlyList<LibraryReference> Broken { get; init; }

    /// <summary>Highlights for <see cref="SvgRenderer.RenderChangeHighlights"/>, by element id.</summary>
    public IReadOnlyDictionary<string, ChangeHighlight> Highlights =>
        Diff.Where(d => d.Kind != DiffKind.Moved).GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First().Kind switch
        {
            DiffKind.Added => ChangeHighlight.Added,
            DiffKind.Removed => ChangeHighlight.Removed,
            _ => ChangeHighlight.Changed,
        }, StringComparer.Ordinal);
}

/// <summary>What an applied update did, to undo it as one step.</summary>
public sealed record LibraryUpdateResult(Guid UsageId, LibraryPinKind FromKind, string FromValue, string? FromMount, int Suspect, IReadOnlyList<LibraryMark> Marks);

/// <summary>
/// Used projects (docs/LIBRARIES.md): adding, re-pinning and removing usages with their permission checks, checking for and
/// applying library updates, and rebuilding live sessions when libraries change.
/// </summary>
public sealed class LibraryService(IMeridianStore db, CollaborationHub hub)
{
    public Task<IReadOnlyList<ProjectUsage>> UsagesAsync(Guid projectId) => db.ListProjectUsagesAsync(projectId);

    /// <summary>Projects the user can read (candidates to use), without <paramref name="except"/>.</summary>
    public async Task<IReadOnlyList<ProjectSummary>> SourceProjectsAsync(Guid userId, Guid except) =>
        (await db.ListProjectsAsync(userId)).Where(p => p.Id != except).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<LibraryPinOptions> PinsAsync(Guid sourceProjectId, Guid userId)
    {
        if (await db.RoleOfAsync(sourceProjectId, userId) is null) throw new PermissionDeniedException("You need at least read access to that project.");
        var branches = new List<string> { Branching.MainBranch };
        branches.AddRange((await db.ListBranchesAsync(sourceProjectId)).Select(b => b.Name));
        return new LibraryPinOptions(await db.ListVersionsAsync(sourceProjectId), branches, await db.ListBaselinesAsync(sourceProjectId));
    }

    /// <summary>The packages of a source project at a pin that the user may use (hidden ones are not listed).</summary>
    public async Task<IReadOnlyList<(string Id, string Path)>> PackagesAsync(Guid sourceProjectId, LibraryPinKind kind, string value, Guid userId)
    {
        if (await db.RoleOfAsync(sourceProjectId, userId) is null) throw new PermissionDeniedException("You need at least read access to that project.");
        if (await LibraryLoader.LoadAtPinAsync(db, hub, sourceProjectId, kind, value) is not { } source) return [];
        var view = await PackagePermissions.ViewForAsync(db, sourceProjectId, userId);
        var model = ProjectFile.Deserialize(source.ModelJson);
        return LibraryContent.Packages(model).Where(p => model.Find(p.Id) is { } e && !view.IsHidden(e) && view.CanSeeContents(e)).ToList();
    }

    private async Task EnsureEditorAsync(Guid projectId, Guid userId)
    {
        if (await db.RoleOfAsync(projectId, userId) is not (ProjectRole.Editor or ProjectRole.Owner))
            throw new PermissionDeniedException("Only editors and owners can change the projects this project uses.");
    }

    /// <summary>Refuses a usage that would make libraries use each other in a circle.</summary>
    private async Task EnsureNoCycleAsync(Guid projectId, Guid sourceProjectId)
    {
        var seen = new HashSet<Guid>();
        var queue = new Queue<Guid>([sourceProjectId]);
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            if (p == projectId) throw new InvalidOperationException("That project already uses this one (directly or through other libraries): using it would form a cycle.");
            if (!seen.Add(p) || seen.Count > 200) continue;
            foreach (var u in await db.ListProjectUsagesAsync(p)) queue.Enqueue(u.UsedProjectId);
        }
    }

    /// <summary>Adds a usage after checking the user can edit this project and read the source project and package at the pin.</summary>
    public async Task<Guid> AddAsync(Guid projectId, Guid sourceProjectId, string packageId, LibraryPinKind kind, string value, string? mountId, Guid userId,
                                     string? initiator = null)
    {
        await EnsureEditorAsync(projectId, userId);
        var source = await LibraryLoader.LoadAtPinAsync(db, hub, sourceProjectId, kind, value)
                     ?? throw new InvalidOperationException("That version, branch or baseline doesn't exist.");
        if (await LibraryLoader.CanReadAsync(db, sourceProjectId, packageId, userId, source.ModelJson) is { } why) throw new PermissionDeniedException(why);
        await EnsureNoCycleAsync(projectId, sourceProjectId);
        var id = await db.AddProjectUsageAsync(projectId, sourceProjectId, packageId, kind, value, mountId, userId);
        await ReloadAsync(projectId, $"A used project was added ({source.Label}).", initiator);
        return id;
    }

    /// <summary>Moves a usage's pin and/or mount point (no review: see <see cref="PrepareUpdateAsync"/> for that).</summary>
    public async Task ChangeAsync(Guid usageId, LibraryPinKind kind, string value, string? mountId, Guid userId, string? initiator = null)
    {
        var usage = await db.GetProjectUsageAsync(usageId) ?? throw new InvalidOperationException("That usage no longer exists.");
        await EnsureEditorAsync(usage.ProjectId, userId);
        var source = await LibraryLoader.LoadAtPinAsync(db, hub, usage.UsedProjectId, kind, value)
                     ?? throw new InvalidOperationException("That version, branch or baseline doesn't exist.");
        if (await LibraryLoader.CanReadAsync(db, usage.UsedProjectId, usage.RootPackageId, userId, source.ModelJson) is { } why) throw new PermissionDeniedException(why);
        await db.UpdateProjectUsageAsync(usageId, kind, value, mountId);
        await ReloadAsync(usage.ProjectId, $"“{usage.UsedProjectName}” now uses {source.Label}.", initiator);
    }

    public async Task RemoveAsync(Guid usageId, Guid userId, string? initiator = null)
    {
        var usage = await db.GetProjectUsageAsync(usageId) ?? throw new InvalidOperationException("That usage no longer exists.");
        await EnsureEditorAsync(usage.ProjectId, userId);
        await db.RemoveProjectUsageAsync(usageId);
        await ReloadAsync(usage.ProjectId, $"“{usage.UsedProjectName}” is no longer used; references to it are kept as missing references.", initiator);
    }

    /// <summary>
    /// Rebuilds every live session of the project with its current libraries. Returns the model document for
    /// <paramref name="initiator"/> (that tab installs it itself), if it is connected to one of them.
    /// </summary>
    public async Task<string?> ReloadAsync(Guid projectId, string message, string? initiator = null, Func<ModelRepository, IReadOnlyList<LibraryMark>>? afterMount = null,
                                           List<IReadOnlyList<LibraryMark>>? marks = null)
    {
        string? mine = null;
        foreach (var session in hub.SessionsOf(projectId).ToList())
        {
            var resolution = await LibraryLoader.ResolveAsync(db, hub, projectId);
            var per = new Dictionary<string, IReadOnlyList<LibraryPayload>>(StringComparer.Ordinal);
            var byUser = new Dictionary<Guid, IReadOnlyList<LibraryPayload>>();
            foreach (var (participant, user) in session.ParticipantUsers())
            {
                if (!byUser.TryGetValue(user, out var list)) byUser[user] = list = await LibraryLoader.ForUserAsync(db, hub, resolution, user);
                per[participant] = list;
            }
            var (json, result) = session.ReloadLibraries(resolution, per, message, initiator, m => afterMount?.Invoke(m) ?? []);
            marks?.Add(result);
            mine ??= json;
        }
        return mine;
    }

    // ---------------------------------------------------------------- updates

    /// <summary>
    /// Newer pins for the project's usages: the latest committed version for a version pin, a changed head for a branch pin
    /// (compared with what the session loaded), the newest baseline for a baseline pin.
    /// </summary>
    public async Task<IReadOnlyList<LibraryUpdateOffer>> CheckUpdatesAsync(Guid projectId, Guid userId, IReadOnlyList<LibraryInfo> loaded)
    {
        var offers = new List<LibraryUpdateOffer>();
        foreach (var u in await db.ListProjectUsagesAsync(projectId))
        {
            if (string.IsNullOrEmpty(u.UsedProjectName) || await db.RoleOfAsync(u.UsedProjectId, userId) is null) continue;
            var current = loaded.FirstOrDefault(l => l.UsageId == u.Id.ToString("N"));
            var label = current?.PinLabel ?? LibraryPins.Label(u.PinKind, u.PinValue);
            switch (u.PinKind)
            {
                case LibraryPinKind.Version:
                    var latest = (await db.ListBranchVersionsAsync(u.UsedProjectId, null)).Select(v => v.Number).DefaultIfEmpty(0).Max();
                    if (int.TryParse(u.PinValue, out var pinned) && latest > pinned)
                        offers.Add(new(u.Id, u.UsedProjectName, label, LibraryPinKind.Version, latest.ToString(), $"v{latest}"));
                    break;
                case LibraryPinKind.Baseline:
                    var baselines = await db.ListBaselinesAsync(u.UsedProjectId);
                    var mine = baselines.FirstOrDefault(b => b.Id.ToString() == u.PinValue);
                    var newest = baselines.OrderByDescending(b => b.VersionNumber).ThenByDescending(b => b.Id).FirstOrDefault();
                    if (newest is not null && (mine is null || newest.VersionNumber > mine.VersionNumber))
                        offers.Add(new(u.Id, u.UsedProjectName, label, LibraryPinKind.Baseline, newest.Id.ToString(), LibraryPins.Label(LibraryPinKind.Baseline, newest.Id.ToString(), newest.Name)));
                    break;
                default:
                    if (current?.Resolved is not { } was) break;
                    var fetched = await LibraryLoader.FetchAsync(db, hub, u.Request, u.UsedProjectName);
                    if (fetched?.Info.Resolved is { } now && now != was)
                        offers.Add(new(u.Id, u.UsedProjectName, label, LibraryPinKind.Branch, u.PinValue, $"{u.PinValue} (newer head)"));
                    break;
            }
        }
        return offers;
    }

    /// <summary>Compares a usage's current content with the content at a new pin, and lists the project elements affected.</summary>
    public async Task<LibraryUpdatePlan> PrepareUpdateAsync(ProjectSession session, Guid usageId, LibraryPinKind kind, string value, Guid userId)
    {
        var usage = await db.GetProjectUsageAsync(usageId) ?? throw new InvalidOperationException("That usage no longer exists.");
        if (usage.ProjectId != session.ProjectId) throw new InvalidOperationException("That usage belongs to another project.");
        await EnsureEditorAsync(usage.ProjectId, userId);
        var key = usage.Id.ToString("N");
        var resolution = new LibraryResolution(session.Libraries.Payloads.Where(p => p.Info.UsageId == key).ToList(), []);
        var current = (await LibraryLoader.ForUserAsync(db, hub, resolution, userId)).FirstOrDefault();
        var request = usage.Request with { PinKind = kind, PinValue = value };
        var next = await LibraryLoader.FetchAsync(db, hub, request, usage.UsedProjectName)
                   ?? throw new InvalidOperationException("The library's package doesn't exist at that pin (or the pin doesn't).");
        var nextMine = (await LibraryLoader.ForUserAsync(db, hub, new LibraryResolution([new LibraryPayload(next.Info, next.RootJson)], []), userId)).FirstOrDefault()
                       ?? throw new PermissionDeniedException("You can't read that library at the new pin.");

        var before = current is null ? new ModelRepository(usage.UsedProjectName) : ProjectFile.LibraryContent(current);
        var after = ProjectFile.LibraryContent(nextMine);
        var diff = ModelDiff.Compare(before, after).Where(d => d.Id != before.Root.Id && d.Id != after.Root.Id).ToList();
        var (changed, removed) = LibraryUpdates.ChangedIds(before, after, diff);
        var (affected, broken) = session.ReadMaster(m =>
        {
            var mount = m.Libraries.FirstOrDefault(l => l.Info.UsageId == key);
            var refs = mount is null ? [] : LibraryUpdates.References(m, mount).Where(r => r.Referrer is not Diagram || removed.Contains(r.Target.Id)).ToList();
            return ((IReadOnlyList<LibraryReference>)refs.Where(r => changed.Contains(r.Target.Id)).ToList(),
                    (IReadOnlyList<LibraryReference>)refs.Where(r => removed.Contains(r.Target.Id)).ToList());
        });
        return new LibraryUpdatePlan
        {
            Usage = usage, LibraryName = usage.UsedProjectName, ToKind = kind, ToValue = value,
            FromLabel = current?.Info.PinLabel ?? LibraryPins.Label(usage.PinKind, usage.PinValue), ToLabel = next.Info.PinLabel,
            Before = before, After = after, Diff = diff, Changed = changed, Removed = removed, Affected = affected, Broken = broken,
        };
    }

    /// <summary>
    /// Accepts a reviewed update: moves the pin, rebuilds the live sessions with the new content and marks the project's references
    /// to changed library elements as suspect. Returns the new document for <paramref name="initiator"/> and what to undo.
    /// </summary>
    public async Task<(string? Json, LibraryUpdateResult Result)> ApplyUpdateAsync(LibraryUpdatePlan plan, Guid userId, string? initiator)
    {
        var usage = await db.GetProjectUsageAsync(plan.Usage.Id) ?? throw new InvalidOperationException("That usage no longer exists.");
        await EnsureEditorAsync(usage.ProjectId, userId);
        if (usage.PinKind != plan.Usage.PinKind || usage.PinValue != plan.Usage.PinValue)
            throw new InvalidOperationException("Someone changed this usage while you were reviewing the update. Review it again.");
        await db.UpdateProjectUsageAsync(usage.Id, plan.ToKind, plan.ToValue, usage.MountPointId);
        var names = plan.Before.AllElements.Concat(plan.After.AllElements).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.Last().DisplayName, StringComparer.Ordinal);
        var allMarks = new List<IReadOnlyList<LibraryMark>>();
        var json = await ReloadAsync(usage.ProjectId, $"“{plan.LibraryName}” was updated to {plan.ToLabel}.", initiator,
            m => LibraryUpdates.MarkSuspects(m, plan.LibraryName, plan.FromLabel, plan.ToLabel, plan.Changed, plan.Removed, names), allMarks);
        var marks = allMarks.FirstOrDefault() ?? [];
        return (json, new LibraryUpdateResult(usage.Id, usage.PinKind, usage.PinValue, usage.MountPointId, marks.Count, marks));
    }

    /// <summary>Undoes an applied update: the pin moves back and the suspect marks it set are cleared.</summary>
    public async Task<string?> UndoUpdateAsync(LibraryUpdateResult result, Guid userId, string? initiator)
    {
        var usage = await db.GetProjectUsageAsync(result.UsageId) ?? throw new InvalidOperationException("That usage no longer exists.");
        await EnsureEditorAsync(usage.ProjectId, userId);
        await db.UpdateProjectUsageAsync(usage.Id, result.FromKind, result.FromValue, result.FromMount);
        return await ReloadAsync(usage.ProjectId, $"The update of “{usage.UsedProjectName}” was undone.", initiator, m =>
        {
            LibraryUpdates.UnmarkSuspects(m, result.Marks);
            return [];
        });
    }
}
