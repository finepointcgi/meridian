using System.Collections.Concurrent;
using Meridian.Core.CodeSync;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Services;

namespace Meridian.Web.CodeSync;

/// <summary>Tells open workbenches that a project's code-sync runs changed (a new run, or a review decision).</summary>
public sealed class CodeSyncEvents
{
    /// <summary>Raised with the project id. Handlers must only queue work (they run on the sync's thread).</summary>
    public event Action<Guid>? RunsChanged;

    public void Notify(Guid projectId)
    {
        try { RunsChanged?.Invoke(projectId); }
        catch (Exception) { /* a closed circuit must not fail a sync */ }
    }

    /// <summary>Raised when a sync run finished (not when it failed), with the request and its outcome. Handlers must
    /// only queue work: the architecture check (CodeSync/Conformance) subscribes here.</summary>
    public event Action<CodeSyncRequest, CodeSyncOutcome>? RunCompleted;

    public void Completed(CodeSyncRequest request, CodeSyncOutcome outcome)
    {
        try { RunCompleted?.Invoke(request, outcome); }
        catch (Exception) { /* a follow-up check must not fail a sync */ }
    }
}

/// <summary>
/// Applies a push to a project's code-sync review branch (<c>code-sync/&lt;repo&gt;</c>): reads the files, maps them
/// onto the branch's model, adds new elements to the diagrams that show their context (and diagrams new namespaces /
/// blocks when the project allows it), commits the result on the branch and records a run for review.
/// Runs of one project are serialized; review decisions take the same lock.
/// </summary>
public sealed class CodeSyncRunner(
    IMeridianStore db, CollaborationHub hub, IEnumerable<ICodeReader> readers, ICodeModelApplier applier,
    CodeSyncEvents events, ILogger<CodeSyncRunner> log, IConfiguration? config = null) : ICodeSyncRunner
{
    /// <summary>Prefix of review branch names.</summary>
    public const string BranchPrefix = "code-sync/";

    /// <summary>Model-root field: "false" stops code sync creating diagrams for new namespaces and blocks (default on).</summary>
    public const string AutoDiagramsField = "codeSync.autoDiagrams";

    /// <summary>Model-root field: qualified name of the package code is mapped into (default "Code").</summary>
    public const string RootPackageField = "codeSync.rootPackage";

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    /// <summary>The per-project lock shared by sync runs and review decisions.</summary>
    internal static SemaphoreSlim LockFor(Guid projectId) => Locks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));

    /// <summary>The review branch name for a repository ("code-sync/acme/sat-fsw"), within the branch-name rules.</summary>
    public static string BranchNameFor(string repoDisplay)
    {
        var clean = new string((repoDisplay ?? "").Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' ? c : '-').ToArray()).Trim('/', '-');
        if (clean.Length == 0) clean = "repository";
        var name = BranchPrefix + clean;
        return name.Length <= 60 ? name : name[..60].TrimEnd('/', '-');
    }

    public static bool IsReviewBranch(string branchName) => branchName.StartsWith(BranchPrefix, StringComparison.Ordinal);

    public async Task<CodeSyncOutcome> RunAsync(CodeSyncRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var gate = LockFor(request.ProjectId);
        await gate.WaitAsync(ct);
        Guid? branchId = null;
        var runId = Guid.NewGuid();
        try
        {
            var outcome = await RunLockedAsync(request, runId, id => branchId = id, ct);
            events.Completed(request, outcome);
            return outcome;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await RecordFailureAsync(request, runId, branchId, "Cancelled before the sync finished.");
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Code sync of {Repo}@{Commit} into project {Project} failed", request.RepoDisplay, request.Commit, request.ProjectId);
            var summary = ex is NotImplementedException or InvalidOperationException ? ex.Message : $"The sync failed: {ex.GetType().Name}: {ex.Message}";
            await RecordFailureAsync(request, runId, branchId, summary);
            return new CodeSyncOutcome(CodeSyncStatus.Failed, summary, null, runId);
        }
        finally
        {
            gate.Release();
            events.Notify(request.ProjectId);
        }
    }

    private async Task<CodeSyncOutcome> RunLockedAsync(CodeSyncRequest request, Guid runId, Action<Guid> branchCreated, CancellationToken ct)
    {
        if (await db.LoadProjectAsync(request.ProjectId) is null) throw new InvalidOperationException("That project no longer exists.");

        // Pick the reader that handles the pushed files.
        var reader = readers.FirstOrDefault(r => request.Files.Any(f => r.Handles(f.Path)));
        if (reader is null)
        {
            var none = "No supported source files in this push.";
            await RecordAsync(request, runId, null, CodeSyncRunStatus.NoChanges, none, [], 0, 0, 0);
            return new CodeSyncOutcome(CodeSyncStatus.NoChanges, none, null, runId);
        }
        var files = request.Files.Where(f => reader.Handles(f.Path)).ToList();
        ct.ThrowIfCancellationRequested();
        var code = reader.Read(files, ct);
        ct.ThrowIfCancellationRequested();

        // The review branch: found, or made from main's head. It accumulates runs until they are accepted.
        var name = BranchNameFor(request.RepoDisplay);
        var branch = (await db.ListBranchesAsync(request.ProjectId)).FirstOrDefault(b => b.Name == name);
        var created = branch is null;
        var branchId = branch?.Id ?? await VersionControl.CreateBranchAsync(db, hub, request.ProjectId, name, BranchRef.Main, null, null);
        branchCreated(branchId);

        if (hub.Find(request.ProjectId, branchId) is { } open) await open.PersistAsync();
        var headJson = await VersionControl.HeadJsonAsync(db, hub, request.ProjectId, branchId);
        var before = ProjectFile.Deserialize(headJson);
        var model = ProjectFile.Deserialize(headJson);

        var root = model.Root;
        var options = new CodeSyncOptions
        {
            // The project's override, else the repository connection's root package (defaults to the repository name).
            RootPackage = root.Get(RootPackageField) is { Length: > 0 } rp ? rp
                : await db.FindIntegrationAsync(request.ConnectionId) is { Kind: Hosts.CodeRepoKinds.CodeRepo } conn ? Hosts.CodeRepoConfig.Parse(conn.ConfigJson).EffectiveRootPackage
                : "Code",
            RepoId = request.ConnectionId.ToString(),
            Commit = request.Commit,
            // A partial push (only the changed files) must not read as "everything else was deleted".
            RemoveDeleted = request.IsFullSnapshot,
        };
        var changes = applier.Apply(model, code, options);
        ct.ThrowIfCancellationRequested();

        if (changes.IsEmpty)
        {
            if (created) await db.DeleteBranchAsync(branchId);   // nothing to review: don't leave an empty branch behind
            var nothing = $"No model changes from {Short(request.Commit)}.";
            await RecordAsync(request, runId, created ? null : branchId, CodeSyncRunStatus.NoChanges, nothing, [], 0, 0, 0);
            return new CodeSyncOutcome(CodeSyncStatus.NoChanges, nothing, null, runId);
        }

        var refresh = CodeSyncDiagrams.Refresh(model, changes, createDiagrams: root.Get(AutoDiagramsField) != "false");
        var list = ChangeList(before, model, changes, refresh);
        var json = ProjectFile.Serialize(model);
        var message = CommitMessage(request);

        // Live users of the branch see the result at once; otherwise it goes straight to the store.
        if (hub.Find(request.ProjectId, branchId) is { } live)
        {
            live.Replace(json, null, $"{message} — the code changes are on this branch for review");
            await live.PersistAsync();
        }
        else await db.SaveBranchAsync(branchId, json, null);
        await db.CommitBranchVersionAsync(request.ProjectId, branchId, message, json, null);

        int added = list.Count(c => c.Kind == CodeSyncChangeKind.Added), changed = list.Count(c => c.Kind == CodeSyncChangeKind.Changed),
            removed = list.Count(c => c.Kind == CodeSyncChangeKind.Removed);
        var summary = Summary(added, changed, removed) + (refresh.Created.Count > 0 ? $"; {refresh.Created.Count} new diagram{(refresh.Created.Count == 1 ? "" : "s")}" : "")
                      + (changes.Warnings.Count + code.Warnings.Count > 0 ? $" ({changes.Warnings.Count + code.Warnings.Count} warnings)" : "");
        await RecordAsync(request, runId, branchId, CodeSyncRunStatus.AwaitingReview, summary, list, added, changed, removed);
        return new CodeSyncOutcome(CodeSyncStatus.AwaitingReview, summary, ReviewUrl(request.ProjectId, branchId), runId);
    }

    /// <summary>"3 added, 1 changed, 2 removed".</summary>
    public static string Summary(int added, int changed, int removed)
    {
        var parts = new List<string>();
        if (added > 0) parts.Add($"{added} added");
        if (changed > 0) parts.Add($"{changed} changed");
        if (removed > 0) parts.Add($"{removed} removed");
        return parts.Count == 0 ? "No changes" : string.Join(", ", parts);
    }

    /// <summary>The workbench on the review branch with "What changed" open; absolute when Meridian:PublicUrl is set.</summary>
    public string ReviewUrl(Guid projectId, Guid branchId) =>
        Integrations.DeepLink.ForBranch(config?["Meridian:PublicUrl"] ?? "", projectId, branchId, Integrations.DeepLink.CodeChangesPanel);

    public static string CommitMessage(CodeSyncRequest r)
    {
        var first = (r.Message ?? "").Split('\n', 2)[0].Trim();
        if (first.Length > 120) first = first[..120] + "…";
        return $"Code sync: {Short(r.Commit)}{(first.Length > 0 ? " " + first : "")}";
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private static List<CodeSyncRunChange> ChangeList(ModelRepository before, ModelRepository after, CodeSyncChanges changes, DiagramRefreshResult refresh)
    {
        var list = new List<CodeSyncRunChange>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(Element? e, string id, CodeSyncChangeKind kind)
        {
            if (!seen.Add(id)) return;
            list.Add(e is null
                ? new CodeSyncRunChange(id, kind, id, "", "Element")
                : new CodeSyncRunChange(id, kind, Label(e), e.Owner?.QualifiedName ?? "", e.Kind.ToString()));
        }
        foreach (var id in changes.Removed) Add(before.Find(id), id, CodeSyncChangeKind.Removed);
        foreach (var id in changes.Added) Add(after.Find(id), id, CodeSyncChangeKind.Added);
        foreach (var d in refresh.Created) Add(d, d.Id, CodeSyncChangeKind.Added);
        foreach (var id in changes.Changed) Add(after.Find(id), id, CodeSyncChangeKind.Changed);
        return list;
    }

    /// <summary>A readable name for a change row (relationships read "«kind» Source → Target").</summary>
    public static string Label(Element e) => e switch
    {
        Relationship r => $"«{Metamodel.Style(r.Kind).Keyword ?? Metamodel.DisplayName(r.Kind)}» {r.Source.DisplayName} → {r.Target.DisplayName}",
        _ => e.DisplayName,
    };

    private Task RecordAsync(CodeSyncRequest r, Guid runId, Guid? branchId, CodeSyncRunStatus status, string summary,
                             IReadOnlyList<CodeSyncRunChange> list, int added, int changed, int removed) =>
        db.AddCodeSyncRunAsync(new CodeSyncRunRecord(runId, r.ProjectId, r.ConnectionId, branchId,
            CodeSyncRuns.Clip(r.RepoDisplay, 200)!, CodeSyncRuns.Clip(r.Commit, 80)!, CodeSyncRuns.Clip(r.CommitUrl, 500),
            CodeSyncRuns.Clip(r.Author, 200), CodeSyncRuns.Clip(r.Message, 4000), status, CodeSyncRuns.Clip(summary, 1000)!,
            added, changed, removed, CodeSyncRuns.SerializeChanges(list), DateTime.UtcNow, DateTime.UtcNow));

    private async Task RecordFailureAsync(CodeSyncRequest r, Guid runId, Guid? branchId, string summary)
    {
        try { await RecordAsync(r, runId, branchId, CodeSyncRunStatus.Failed, summary, [], 0, 0, 0); }
        catch (Exception ex) { log.LogWarning(ex, "Could not record the failed code sync of project {Project}", r.ProjectId); }
    }
}
