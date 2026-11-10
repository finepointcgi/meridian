using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;

namespace Meridian.Web.Services;

/// <summary>One line of development in a project: main (BranchId null) or a named branch.</summary>
public sealed record BranchRef(Guid? BranchId, string Name)
{
    public static readonly BranchRef Main = new(null, Branching.MainBranch);
    public bool IsMain => BranchId is null;
}

/// <summary>A prepared three-way merge of <see cref="Source"/> into <see cref="Target"/>, waiting for conflict decisions.</summary>
public sealed class MergePlan
{
    public required Guid ProjectId { get; init; }
    public required BranchRef Source { get; init; }
    public required BranchRef Target { get; init; }
    public required string BaseJson { get; init; }
    public required string OursJson { get; init; }
    public required string TheirsJson { get; init; }
    public required MergeResult Result { get; init; }

    /// <summary>What merging changes on the target (with the current conflict choices).</summary>
    public IReadOnlyList<ElementDiff> IncomingChanges(ModelRepository merged) => ModelDiff.Compare(ProjectFile.Deserialize(OursJson), merged);
}

/// <summary>
/// Branch, commit, merge and baseline workflows on top of <see cref="IMeridianStore"/> and the live sessions in
/// <see cref="CollaborationHub"/> (a line someone is editing is read from, and written through, its live session).
/// </summary>
public static class VersionControl
{
    /// <summary>The current head of a line: the live session's model if it is open, else what is stored.</summary>
    public static async Task<string> HeadJsonAsync(IMeridianStore db, CollaborationHub hub, Guid projectId, Guid? branchId)
    {
        if (hub.Find(projectId, branchId) is { } live) return live.Snapshot();
        if (branchId is { } b) return (await db.LoadBranchAsync(b))?.HeadJson ?? throw new InvalidOperationException("That branch no longer exists.");
        return (await db.LoadProjectAsync(projectId))?.ModelJson ?? throw new InvalidOperationException("That project no longer exists.");
    }

    public static async Task<IReadOnlyList<BranchRef>> LinesAsync(IMeridianStore db, Guid projectId) =>
        [BranchRef.Main, .. (await db.ListBranchesAsync(projectId)).Select(b => new BranchRef(b.Id, b.Name))];

    /// <summary>Commits the session's current model as a version on the line it edits.</summary>
    public static async Task<(long Id, int Number)> CommitAsync(IMeridianStore db, ProjectSession session, string message, Guid? author, UserAccount? authorAccount = null)
    {
        // The authoritative role check (the buttons are only disabled for viewers; ⌘S and the palette reach this too).
        if (author is { } a && await db.RoleOfAsync(session.ProjectId, a) is not (ProjectRole.Editor or ProjectRole.Owner))
            throw new InvalidOperationException("Only editors and owners can commit versions of this project.");
        await session.PersistAsync();
        var result = await db.CommitBranchVersionAsync(session.ProjectId, session.BranchId, message.Trim(), session.Snapshot(), author);
        session.NotifyVersionCommitted(result.Number, message.Trim(), authorAccount);   // webhooks
        return result;
    }

    /// <summary>Creates a branch from a committed version, or (when <paramref name="fromVersionId"/> is null) from the head of <paramref name="from"/>.</summary>
    public static async Task<Guid> CreateBranchAsync(IMeridianStore db, CollaborationHub hub, Guid projectId, string name, BranchRef from, long? fromVersionId, Guid? creator)
    {
        string json;
        if (fromVersionId is { } v)
            json = await db.LoadVersionAsync(projectId, v) ?? throw new InvalidOperationException("That version no longer exists.");
        else
        {
            if (hub.Find(projectId, from.BranchId) is { } live) await live.PersistAsync();
            json = await HeadJsonAsync(db, hub, projectId, from.BranchId);
        }
        return await db.CreateBranchAsync(projectId, name, json, json, fromVersionId, creator);
    }

    public static async Task DeleteBranchAsync(IMeridianStore db, CollaborationHub hub, Guid projectId, Guid branchId)
    {
        if (hub.Find(projectId, branchId) is { } live && live.Participants.Count > 0)
            throw new InvalidOperationException($"{live.Participants.Count} {(live.Participants.Count == 1 ? "person is" : "people are")} working on that branch.");
        await db.DeleteBranchAsync(branchId);
    }

    /// <summary>
    /// Three-way merge of <paramref name="source"/> into <paramref name="target"/>. The common ancestor is the merge base the
    /// branch records: the source branch's base, or the target branch's base when main is merged into a branch.
    /// </summary>
    /// <param name="actor">Who is merging: a user with hidden packages can't merge (the conflicts and changes would show them).</param>
    public static async Task<MergePlan> PrepareMergeAsync(IMeridianStore db, CollaborationHub hub, Guid projectId, BranchRef source, BranchRef target, Guid? actor = null)
    {
        if (source.BranchId == target.BranchId) throw new InvalidOperationException("Pick a different branch to merge.");
        await PackagePermissions.EnsureSeesEverythingAsync(db, projectId, actor, "Merging");
        var baseOwner = source.BranchId ?? target.BranchId!.Value;
        var baseJson = (await db.LoadBranchAsync(baseOwner))?.BaseJson ?? throw new InvalidOperationException("That branch no longer exists.");
        foreach (var line in new[] { source, target })
            if (hub.Find(projectId, line.BranchId) is { } live) await live.PersistAsync();
        var ours = await HeadJsonAsync(db, hub, projectId, target.BranchId);
        var theirs = await HeadJsonAsync(db, hub, projectId, source.BranchId);
        var result = ModelMerge.Merge(ProjectFile.Deserialize(baseJson), ProjectFile.Deserialize(ours), ProjectFile.Deserialize(theirs));
        return new MergePlan { ProjectId = projectId, Source = source, Target = target, BaseJson = baseJson, OursJson = ours, TheirsJson = theirs, Result = result };
    }

    /// <summary>
    /// Writes a resolved merge: commits it as a new version on the target, makes it the target's head (live for everyone
    /// editing it) and moves the merge base forward so the next merge only sees newer changes. Package permissions: the author
    /// must see the whole project, and every element the merge changes on the target must be editable by them
    /// (<see cref="PermissionDeniedException"/>); a null author is the system.
    /// </summary>
    public static async Task<(long Id, int Number)> CompleteMergeAsync(IMeridianStore db, CollaborationHub hub, MergePlan plan, ModelRepository merged, string message, Guid? author)
    {
        var current = await HeadJsonAsync(db, hub, plan.ProjectId, plan.Target.BranchId);
        var currentModel = ProjectFile.Deserialize(current);
        if (current != plan.OursJson && ModelDiff.Compare(ProjectFile.Deserialize(plan.OursJson), currentModel).Count > 0)
            throw new MergeOutdatedException($"“{plan.Target.Name}” changed while you were merging. Review the merge again.");
        await PackagePermissions.EnsureSeesEverythingAsync(db, plan.ProjectId, author, "Merging");
        await PackagePermissions.EnsureCanReplaceAsync(db, plan.ProjectId, author, currentModel, merged);

        var json = ProjectFile.Serialize(merged);
        var version = await db.CommitBranchVersionAsync(plan.ProjectId, plan.Target.BranchId, message.Trim(), json, author);
        if (hub.Find(plan.ProjectId, plan.Target.BranchId) is { } live)
        {
            live.Replace(json, author ?? Guid.Empty, $"Merged “{plan.Source.Name}” into “{plan.Target.Name}” (v{version.Number})");
            await live.PersistAsync();
        }
        else if (plan.Target.BranchId is { } tb) await db.SaveBranchAsync(tb, json, author);
        else await db.SaveProjectAsync(plan.ProjectId, json, author);

        // The incoming side's merged state is now shared by both lines.
        if (plan.Source.BranchId is { } sb) await db.SetBranchBaseAsync(sb, plan.TheirsJson, version.Id);
        else if (plan.Target.BranchId is { } tb2) await db.SetBranchBaseAsync(tb2, plan.TheirsJson, version.Id);
        return version;
    }
}

public sealed class MergeOutdatedException(string message) : InvalidOperationException(message);
