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

/// <summary>
/// The suspect marks a library update set on one line (main or a branch) of a project: the project that accepted it, or one that
/// uses that project and so moved to the new pin too. <paramref name="Version"/> is the system commit that wrote them into a line
/// nobody had open (null when they went to a live session).
/// </summary>
public sealed record LineMarks(Guid ProjectId, Guid? BranchId, string Line, IReadOnlyList<LibraryMark> Marks, int? Version = null);

/// <summary>
/// What an applied update did, to undo it as one step. <see cref="Marks"/> are those of the line the update was accepted on;
/// <see cref="Lines"/> lists every line that got marks (open or not, of this project and of the projects that use it).
/// </summary>
public sealed record LibraryUpdateResult(Guid UsageId, LibraryPinKind FromKind, string FromValue, string? FromMount, int Suspect, IReadOnlyList<LibraryMark> Marks)
{
    public IReadOnlyList<LineMarks> Lines { get; init; } = [];
}

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
    public async Task<string?> ReloadAsync(Guid projectId, string message, string? initiator = null) =>
        (await ReloadLiveAsync(projectId, message, initiator, null)).InitiatorJson;

    /// <summary>What rebuilding a project's live sessions did: the initiator's document, and the marks set per live line.</summary>
    private sealed record Reloaded(string? InitiatorJson, LineMarks? InitiatorLine, IReadOnlyList<LineMarks> Lines);

    /// <summary>
    /// Rebuilds every live session of the project with its current libraries; <paramref name="afterMount"/> may change each rebuilt
    /// model (suspect marks, or clearing them on undo) and returns what it set. Each session is persisted at once, so a session
    /// that is closing doesn't lose the marks.
    /// </summary>
    private async Task<Reloaded> ReloadLiveAsync(Guid projectId, string message, string? initiator,
                                                 Func<ProjectSession, ModelRepository, IReadOnlyList<LibraryMark>>? afterMount)
    {
        string? mine = null;
        LineMarks? initiatorLine = null;
        var lines = new List<LineMarks>();
        foreach (var session in hub.SessionsOf(projectId).ToList())
        {
            var resolution = await LibraryLoader.ResolveAsync(db, hub, projectId);
            var per = new Dictionary<string, IReadOnlyList<LibraryPayload>>(StringComparer.Ordinal);
            var byUser = new Dictionary<Guid, IReadOnlyList<LibraryPayload>>();
            var participants = session.ParticipantUsers();
            foreach (var (participant, user) in participants)
            {
                if (!byUser.TryGetValue(user, out var list)) byUser[user] = list = await LibraryLoader.ForUserAsync(db, hub, resolution, user);
                per[participant] = list;
            }
            var (json, result) = session.ReloadLibraries(resolution, per, message, initiator, m => afterMount?.Invoke(session, m) ?? []);
            var line = new LineMarks(projectId, session.BranchId, session.BranchName, result);
            if (result.Count > 0) lines.Add(line);
            if (initiator != null && participants.Any(p => p.ParticipantId == initiator)) initiatorLine = line;
            mine ??= json;
            if (afterMount != null) await session.PersistAsync();
        }
        return new Reloaded(mine, initiatorLine, lines);
    }

    /// <summary>
    /// The one place suspect marks are written into lines nobody has open (docs/LIBRARIES.md): for each line (main and every
    /// branch) of the project without a live session, the stored head is loaded with the project's current libraries,
    /// <paramref name="change"/> runs on it — the same function the live sessions run — and, when it set anything, the head is
    /// saved and committed as a system version with <paramref name="commitMessage"/>. A line that is open is left to
    /// <see cref="ReloadLiveAsync"/>.
    /// </summary>
    private async Task<IReadOnlyList<LineMarks>> ChangeClosedLinesAsync(Guid projectId, string commitMessage,
                                                                         Func<Guid?, ModelRepository, IReadOnlyList<LibraryMark>> change)
    {
        var lines = new List<LineMarks>();
        foreach (var line in await VersionControl.LinesAsync(db, projectId))
        {
            var done = await hub.ChangeClosedLineAsync<IReadOnlyList<LibraryMark>>(projectId, line.BranchId, commitMessage, async json =>
            {
                var resolution = await LibraryLoader.ResolveAsync(db, hub, projectId);
                var model = resolution.Payloads.Count == 0 ? ProjectFile.Deserialize(json) : ProjectFile.Deserialize(json, resolution.Payloads);
                LibraryChangeMark.UpgradeLegacy(model);   // older marks named library elements: ids only
                IReadOnlyList<LibraryMark> marks = change(line.BranchId, model).Where(m => m.Before != m.After).ToList();   // already marked: nothing to commit
                return marks.Count == 0 ? null : (ProjectFile.Serialize(model), marks);   // library content is never written
            });
            if (done is { } d) lines.Add(new LineMarks(projectId, line.BranchId, line.Name, d.Result, d.Version));
        }
        return lines;
    }

    /// <summary>
    /// Projects that use <paramref name="projectId"/> directly or through other libraries: they load its libraries transitively,
    /// so they move to a new pin of one of its usages too.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> DependentsAsync(Guid projectId)
    {
        var found = new List<Guid>();
        var seen = new HashSet<Guid> { projectId };
        var queue = new Queue<Guid>([projectId]);
        while (queue.Count > 0 && seen.Count < 200)
            foreach (var u in await db.ListProjectUsersAsync(queue.Dequeue()))
                if (seen.Add(u.ProjectId))
                {
                    found.Add(u.ProjectId);
                    queue.Enqueue(u.ProjectId);
                }
        return found;
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
        // The one choke point for the marks, open or not: references to what the reviewer saw change, on the project's own
        // elements only (library content is read-only). A mark stores ids and the kind of change, never a name: each viewer's
        // own model names the element when the mark is shown (LibraryChangeMark), so a hidden element is never named.
        var fromPin = LibraryChangeMark.Pin(usage.PinKind, usage.PinValue);
        var toPin = LibraryChangeMark.Pin(plan.ToKind, plan.ToValue);
        IReadOnlyList<LibraryMark> Mark(ModelRepository m) => LibraryUpdates.MarkSuspects(m, usage.UsedProjectId, fromPin, toPin, plan.Changed, plan.Removed);

        // Suspect marks belong to the project that accepts the update — every line of it, since the pin is the project's — and to
        // the projects that use it (they load its libraries transitively, so they move to the new pin as well).
        var lines = new List<LineMarks>();
        string? json = null;
        LineMarks? accepted = null;
        foreach (var project in new[] { usage.ProjectId }.Concat(await DependentsAsync(usage.ProjectId)))
        {
            var own = project == usage.ProjectId;
            var live = await ReloadLiveAsync(project, own ? $"“{plan.LibraryName}” was updated to {plan.ToLabel}." : $"“{plan.LibraryName}” (used through another project) was updated to {plan.ToLabel}.",
                own ? initiator : null, (_, m) => Mark(m));
            if (own) (json, accepted) = (live.InitiatorJson, live.InitiatorLine);
            lines.AddRange(live.Lines);
            lines.AddRange(await ChangeClosedLinesAsync(project, $"Library update: “{plan.LibraryName}” {plan.FromLabel} → {plan.ToLabel} (suspect references marked)", (_, m) => Mark(m)));
        }
        var marks = accepted?.Marks ?? [];
        return (json, new LibraryUpdateResult(usage.Id, usage.PinKind, usage.PinValue, usage.MountPointId, marks.Count, marks) { Lines = lines });
    }

    /// <summary>
    /// Undoes an applied update: the pin moves back and the suspect marks it set are cleared — in the open sessions and, as a
    /// system commit, in the lines nobody has open (of this project and of the projects that use it).
    /// </summary>
    public async Task<string?> UndoUpdateAsync(LibraryUpdateResult result, Guid userId, string? initiator)
    {
        var usage = await db.GetProjectUsageAsync(result.UsageId) ?? throw new InvalidOperationException("That usage no longer exists.");
        await EnsureEditorAsync(usage.ProjectId, userId);
        await db.UpdateProjectUsageAsync(usage.Id, result.FromKind, result.FromValue, result.FromMount);
        IReadOnlyList<LibraryMark> MarksOf(Guid project, Guid? branch) =>
            result.Lines.FirstOrDefault(l => l.ProjectId == project && l.BranchId == branch)?.Marks
            ?? (project == usage.ProjectId && result.Lines.Count == 0 ? result.Marks : []);
        IReadOnlyList<LibraryMark> Unmark(Guid project, Guid? branch, ModelRepository m)
        {
            LibraryUpdates.UnmarkSuspects(m, MarksOf(project, branch));
            return [];
        }

        string? json = null;
        var projects = new[] { usage.ProjectId }.Concat(result.Lines.Select(l => l.ProjectId)).Distinct().ToList();
        foreach (var project in projects)
        {
            var live = await ReloadLiveAsync(project, $"The update of “{usage.UsedProjectName}” was undone.", project == usage.ProjectId ? initiator : null,
                (s, m) => Unmark(project, s.BranchId, m));
            if (project == usage.ProjectId) json = live.InitiatorJson;
            foreach (var line in result.Lines.Where(l => l.ProjectId == project))
                await hub.ChangeClosedLineAsync<IReadOnlyList<LibraryMark>>(project, line.BranchId, $"Undo library update of “{usage.UsedProjectName}” (suspect marks cleared)", async head =>
                {
                    var resolution = await LibraryLoader.ResolveAsync(db, hub, project);
                    var model = resolution.Payloads.Count == 0 ? ProjectFile.Deserialize(head) : ProjectFile.Deserialize(head, resolution.Payloads);
                    LibraryChangeMark.UpgradeLegacy(model);
                    var before = ProjectFile.Serialize(model);
                    LibraryUpdates.UnmarkSuspects(model, line.Marks);
                    var after = ProjectFile.Serialize(model);
                    return after == before ? null : (after, line.Marks);
                });
        }
        return json;
    }
}
