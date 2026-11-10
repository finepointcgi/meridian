using Meridian.Core.Analysis;
using Meridian.Core.CodeSync;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Services;

namespace Meridian.Web.CodeSync;

/// <summary>One element waiting for review: its net change on the review branch (against the merge base) and the runs that touched it.</summary>
public sealed record CodeSyncPendingChange(
    string ElementId, CodeSyncChangeKind Kind, string Name, string Path, ElementKind ElementKind,
    IReadOnlyList<FieldChange> Fields, IReadOnlyList<Guid> RunIds);

/// <summary>The open review of one code-sync branch: its unreviewed runs (newest first) and their pending changes.</summary>
public sealed class CodeSyncReview
{
    public required Guid ProjectId { get; init; }
    public required BranchRef Branch { get; init; }
    public required IReadOnlyList<CodeSyncRunRecord> Runs { get; init; }
    public required IReadOnlyList<CodeSyncPendingChange> Changes { get; init; }
    /// <summary>Main's current model (the ghosts of removed elements are drawn from its diagrams).</summary>
    public required ModelRepository Main { get; init; }

    /// <summary>Element id → how it differs, for diagram highlights.</summary>
    public IReadOnlyDictionary<string, CodeSyncChangeKind> Kinds => _kinds ??= Changes.ToDictionary(c => c.ElementId, c => c.Kind, StringComparer.Ordinal);
    private IReadOnlyDictionary<string, CodeSyncChangeKind>? _kinds;
}

/// <summary>An accept that needs conflict decisions: show <see cref="Plan"/> in the merge dialog, then call
/// <see cref="CodeSyncReviewService.CompleteAcceptAsync"/>.</summary>
public sealed class CodeSyncAcceptance
{
    public required MergePlan Plan { get; init; }
    public required IReadOnlyCollection<string> ElementIds { get; init; }
    public required IReadOnlyList<Guid> RunIds { get; init; }
}

/// <summary>Result of a review decision. <see cref="NeedsResolution"/> is set when an accept hit merge conflicts.</summary>
public sealed record CodeSyncDecision(int Count, int? VersionNumber, string Message, CodeSyncAcceptance? NeedsResolution = null)
{
    public bool Done => NeedsResolution is null;
}

/// <summary>
/// Accepting and rejecting code changes from code-sync review branches. Accepting merges the review branch into main
/// with the normal three-way merge (conflicts surface in the merge dialog); accepting only some changes first puts the
/// others back to the merge base, so main keeps its version of them and they stay on the branch for later. Rejecting
/// puts the chosen elements on the branch back to main's version. Only editors and owners decide; viewers can't.
/// </summary>
public sealed class CodeSyncReviewService(IMeridianStore db, CollaborationHub hub, CodeSyncEvents events, ILogger<CodeSyncReviewService> log)
{
    /// <summary>
    /// Raised once per run when its last pending change is decided and at least one of them was accepted into main:
    /// (outcome with status <see cref="CodeSyncStatus.Accepted"/> and a summary, the run's connection id, the run's commit sha).
    /// The git-host status poster subscribes to update the commit status. Handlers run on the reviewer's thread; keep them quick.
    /// </summary>
    public event Action<CodeSyncOutcome, Guid, string>? Accepted;

    /// <summary>As <see cref="Accepted"/>, for runs whose changes were all rejected (the outcome's status is still
    /// <see cref="CodeSyncStatus.Accepted"/> — "reviewed" — and its summary says they were rejected).</summary>
    public event Action<CodeSyncOutcome, Guid, string>? Rejected;

    // ================================================================ reading

    /// <summary>Runs still waiting for review, newest first.</summary>
    public async Task<IReadOnlyList<CodeSyncRunRecord>> OpenRunsAsync(Guid projectId) =>
        (await db.ListCodeSyncRunsAsync(projectId, 500)).Where(r => r.IsOpen && r.BranchId is not null).ToList();

    /// <summary>The open reviews of a project, one per review branch that has unreviewed runs.</summary>
    public async Task<IReadOnlyList<CodeSyncReview>> LoadAsync(Guid projectId)
    {
        var open = await OpenRunsAsync(projectId);
        if (open.Count == 0) return [];
        var branches = (await db.ListBranchesAsync(projectId)).ToDictionary(b => b.Id);
        var main = ProjectFile.Deserialize(await VersionControl.HeadJsonAsync(db, hub, projectId, null));
        var list = new List<CodeSyncReview>();
        foreach (var g in open.GroupBy(r => r.BranchId!.Value))
        {
            if (!branches.TryGetValue(g.Key, out var b)) continue;
            var (baseModel, head) = await ModelsAsync(projectId, g.Key);
            list.Add(new CodeSyncReview
            {
                ProjectId = projectId, Branch = new BranchRef(b.Id, b.Name), Runs = g.ToList(),
                Changes = Pending(baseModel, head, main, g.ToList()), Main = main,
            });
        }
        return list;
    }

    private async Task<(ModelRepository Base, ModelRepository Head)> ModelsAsync(Guid projectId, Guid branchId)
    {
        var b = await db.LoadBranchAsync(branchId) ?? throw new InvalidOperationException("That review branch no longer exists.");
        return (ProjectFile.Deserialize(b.BaseJson), ProjectFile.Deserialize(await VersionControl.HeadJsonAsync(db, hub, projectId, branchId)));
    }

    /// <summary>Pending entries of the runs, as net changes of the branch against its merge base.</summary>
    private static List<CodeSyncPendingChange> Pending(ModelRepository baseModel, ModelRepository head, ModelRepository main, IReadOnlyList<CodeSyncRunRecord> runs)
    {
        var entries = runs.SelectMany(r => r.Changes.Where(c => c.State == CodeSyncChangeState.Pending).Select(c => (Run: r.Id, Change: c)))
                          .GroupBy(x => x.Change.ElementId, StringComparer.Ordinal).ToList();
        if (entries.Count == 0) return [];
        var ids = entries.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var diff = ModelDiff.Compare(baseModel, head).Where(d => ids.Contains(d.Id)).ToDictionary(d => d.Id, StringComparer.Ordinal);
        var list = new List<CodeSyncPendingChange>();
        foreach (var g in entries)
        {
            if (!diff.TryGetValue(g.Key, out var d)) continue;   // no net difference left (already in main, or undone)
            var kind = d.Kind switch { DiffKind.Added => CodeSyncChangeKind.Added, DiffKind.Removed => CodeSyncChangeKind.Removed, _ => CodeSyncChangeKind.Changed };
            var e = head.Find(d.Id) ?? baseModel.Find(d.Id) ?? main.Find(d.Id);
            list.Add(new CodeSyncPendingChange(d.Id, kind, e is null ? d.Name : CodeSyncRunner.Label(e), d.Path, d.ElementKind, d.Fields,
                g.Select(x => x.Run).Distinct().ToList()));
        }
        return list.OrderBy(c => c.Kind).ThenBy(c => c.Path, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
    }

    // ================================================================ deciding

    private async Task RequireEditorAsync(Guid projectId, UserAccount user)
    {
        if (await db.RoleOfAsync(projectId, user.Id) is not (ProjectRole.Editor or ProjectRole.Owner))
            throw new InvalidOperationException("Only editors and owners can accept or reject code changes.");
    }

    /// <summary>
    /// Merges code changes into main: those of <paramref name="runIds"/> (null = every open run), limited to
    /// <paramref name="elementIds"/> (null = all of them). Changes not chosen stay on the review branch. When the merge
    /// conflicts with edits on main, nothing is written and the result carries the plan to resolve.
    /// </summary>
    public async Task<CodeSyncDecision> AcceptAsync(Guid projectId, IReadOnlyCollection<Guid>? runIds, IReadOnlyCollection<string>? elementIds, UserAccount user)
    {
        await RequireEditorAsync(projectId, user);
        var gate = CodeSyncRunner.LockFor(projectId);
        await gate.WaitAsync();
        try
        {
            var total = 0;
            int? version = null;
            foreach (var review in await LoadAsync(projectId))
            {
                var scope = Scope(review, runIds, elementIds);
                if (scope.Count == 0) { await MarkAsync(review.Runs, Stale(review, runIds, elementIds), CodeSyncChangeState.Accepted); continue; }
                var acceptance = await PrepareAsync(review, scope, runIds, elementIds);
                if (acceptance.Plan.Result.HasConflicts)
                    return new CodeSyncDecision(total, version, $"Accepting conflicts with changes on main — choose what to keep.", acceptance);
                var v = await CompleteLockedAsync(acceptance, acceptance.Plan.Result.Resolve(), user);
                (total, version) = (total + scope.Count, v);
            }
            return new CodeSyncDecision(total, version, total == 0 ? "There were no code changes to accept." : $"Accepted {total} code change{(total == 1 ? "" : "s")} into main.");
        }
        finally
        {
            gate.Release();
            events.Notify(projectId);
        }
    }

    /// <summary>Writes an accept whose conflicts were resolved in the merge dialog.</summary>
    public async Task<CodeSyncDecision> CompleteAcceptAsync(CodeSyncAcceptance acceptance, ModelRepository merged, UserAccount user)
    {
        await RequireEditorAsync(acceptance.Plan.ProjectId, user);
        var gate = CodeSyncRunner.LockFor(acceptance.Plan.ProjectId);
        await gate.WaitAsync();
        try
        {
            var v = await CompleteLockedAsync(acceptance, merged, user);
            return new CodeSyncDecision(acceptance.ElementIds.Count, v, $"Accepted {acceptance.ElementIds.Count} code change{(acceptance.ElementIds.Count == 1 ? "" : "s")} into main.");
        }
        finally
        {
            gate.Release();
            events.Notify(acceptance.Plan.ProjectId);
        }
    }

    /// <summary>Puts the chosen code changes on the review branch back to main's version, and marks them rejected.</summary>
    public async Task<CodeSyncDecision> RejectAsync(Guid projectId, IReadOnlyCollection<Guid>? runIds, IReadOnlyCollection<string>? elementIds, UserAccount user)
    {
        await RequireEditorAsync(projectId, user);
        var gate = CodeSyncRunner.LockFor(projectId);
        await gate.WaitAsync();
        try
        {
            var total = 0;
            int? version = null;
            foreach (var review in await LoadAsync(projectId))
            {
                var scope = Scope(review, runIds, elementIds);
                if (scope.Count == 0) { await MarkAsync(review.Runs, Stale(review, runIds, elementIds), CodeSyncChangeState.Rejected); continue; }
                var branchId = review.Branch.BranchId!.Value;
                if (hub.Find(projectId, branchId) is { } open) await open.PersistAsync();
                var head = ProjectFile.Deserialize(await VersionControl.HeadJsonAsync(db, hub, projectId, branchId));
                CodeSyncRevert.Revert(head, review.Main, scope);
                var json = ProjectFile.Serialize(head);
                var message = $"Code sync: rejected {scope.Count} change{(scope.Count == 1 ? "" : "s")}";
                if (hub.Find(projectId, branchId) is { } live)
                {
                    live.Replace(json, user.Id, $"{user.DisplayName} rejected {scope.Count} code change{(scope.Count == 1 ? "" : "s")}");
                    await live.PersistAsync();
                }
                else await db.SaveBranchAsync(branchId, json, user.Id);
                var v = await db.CommitBranchVersionAsync(projectId, branchId, message, json, user.Id);
                await MarkAsync(review.Runs, [.. scope, .. Stale(review, runIds, elementIds)], CodeSyncChangeState.Rejected);
                (total, version) = (total + scope.Count, v.Number);
            }
            return new CodeSyncDecision(total, version, total == 0 ? "There were no code changes to reject." : $"Rejected {total} code change{(total == 1 ? "" : "s")}.");
        }
        finally
        {
            gate.Release();
            events.Notify(projectId);
        }
    }

    /// <summary>The pending element ids a decision covers.</summary>
    private static HashSet<string> Scope(CodeSyncReview review, IReadOnlyCollection<Guid>? runIds, IReadOnlyCollection<string>? elementIds)
    {
        var inRuns = review.Changes.Where(c => runIds is null || c.RunIds.Any(runIds.Contains)).Select(c => c.ElementId);
        return (elementIds is null ? inRuns : inRuns.Where(elementIds.Contains)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Pending entries of the runs in scope that no longer differ from the merge base (the same change reached main another
    /// way, or a later run undid it): a decision on the whole run closes them too.
    /// </summary>
    private static HashSet<string> Stale(CodeSyncReview review, IReadOnlyCollection<Guid>? runIds, IReadOnlyCollection<string>? elementIds)
    {
        if (elementIds is not null) return [];
        var live = review.Changes.Select(c => c.ElementId).ToHashSet(StringComparer.Ordinal);
        return review.Runs.Where(r => runIds is null || runIds.Contains(r.Id)).SelectMany(r => r.Changes)
            .Where(c => c.State == CodeSyncChangeState.Pending && !live.Contains(c.ElementId)).Select(c => c.ElementId).ToHashSet(StringComparer.Ordinal);
    }

    private async Task<CodeSyncAcceptance> PrepareAsync(CodeSyncReview review, HashSet<string> chosen, IReadOnlyCollection<Guid>? runIds, IReadOnlyCollection<string>? elementIds)
    {
        var plan = await VersionControl.PrepareMergeAsync(db, hub, review.ProjectId, review.Branch, BranchRef.Main);
        var others = review.Changes.Select(c => c.ElementId).Where(id => !chosen.Contains(id)).ToList();
        if (others.Count > 0)
        {
            // Only some changes: the others go back to the merge base in the incoming side, so main keeps its version of
            // them; recording that side as the new merge base leaves them pending on the branch.
            var baseModel = ProjectFile.Deserialize(plan.BaseJson);
            var theirs = ProjectFile.Deserialize(plan.TheirsJson);
            CodeSyncRevert.Revert(theirs, baseModel, others);
            plan = new MergePlan
            {
                ProjectId = plan.ProjectId, Source = plan.Source, Target = plan.Target, BaseJson = plan.BaseJson, OursJson = plan.OursJson,
                TheirsJson = ProjectFile.Serialize(theirs),
                Result = ModelMerge.Merge(baseModel, ProjectFile.Deserialize(plan.OursJson), theirs),
            };
        }
        return new CodeSyncAcceptance
        {
            Plan = plan, ElementIds = chosen.Concat(Stale(review, runIds, elementIds)).ToHashSet(StringComparer.Ordinal),
            RunIds = review.Runs.Where(r => runIds is null || runIds.Contains(r.Id)).Select(r => r.Id).ToList(),
        };
    }

    private async Task<int> CompleteLockedAsync(CodeSyncAcceptance a, ModelRepository merged, UserAccount user)
    {
        var count = a.ElementIds.Count;
        var message = $"Accept {count} code change{(count == 1 ? "" : "s")} from {a.Plan.Source.Name}";
        var v = await VersionControl.CompleteMergeAsync(db, hub, a.Plan, merged, message, user.Id);
        var runs = (await OpenRunsAsync(a.Plan.ProjectId)).Where(r => r.BranchId == a.Plan.Source.BranchId).ToList();
        await MarkAsync(runs, a.ElementIds, CodeSyncChangeState.Accepted);
        return v.Number;
    }

    /// <summary>Marks the runs' pending entries for <paramref name="ids"/>, closing runs that have nothing left to decide.</summary>
    private async Task MarkAsync(IReadOnlyList<CodeSyncRunRecord> runs, IReadOnlyCollection<string> ids, CodeSyncChangeState state)
    {
        foreach (var run in runs)
        {
            var changes = run.Changes;
            if (!changes.Any(c => c.State == CodeSyncChangeState.Pending && ids.Contains(c.ElementId))) continue;
            var updated = changes.Select(c => c.State == CodeSyncChangeState.Pending && ids.Contains(c.ElementId) ? c with { State = state } : c).ToList();
            var open = updated.Any(c => c.State == CodeSyncChangeState.Pending);
            var accepted = updated.Count(c => c.State == CodeSyncChangeState.Accepted);
            var rejected = updated.Count(c => c.State == CodeSyncChangeState.Rejected);
            var status = open ? CodeSyncRunStatus.AwaitingReview : accepted > 0 ? CodeSyncRunStatus.Accepted : CodeSyncRunStatus.Rejected;
            var summary = open
                ? $"{run.Summary.Split(" — ")[0]} — {accepted} accepted, {rejected} rejected, {updated.Count - accepted - rejected} to review"
                : $"{run.Summary.Split(" — ")[0]} — {accepted} accepted, {rejected} rejected";
            await db.UpdateCodeSyncRunAsync(run.Id, status, summary, CodeSyncRuns.SerializeChanges(updated));
            if (open) continue;
            var outcome = new CodeSyncOutcome(CodeSyncStatus.Accepted, accepted > 0 ? $"Accepted into the model: {summary}" : $"Rejected: {summary}", null, run.Id);
            try { (accepted > 0 ? Accepted : Rejected)?.Invoke(outcome, run.ConnectionId, run.Commit); }
            catch (Exception ex) { log.LogWarning(ex, "A code-sync review listener failed for run {Run}", run.Id); }
        }
    }
}
