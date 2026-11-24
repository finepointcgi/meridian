using System.Collections.Concurrent;
using Meridian.Core.CodeSync;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Serialization.DiagramFiles;
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
/// blocks when the project allows it), commits the result on the branch and records a run for review. A repository's
/// first import goes straight into main instead (<see cref="IsFirstImport"/>), unless the connection asks to review it.
/// Runs of one project are serialized; review decisions take the same lock.
/// </summary>
public sealed partial class CodeSyncRunner(
    IMeridianStore db, CollaborationHub hub, IEnumerable<ICodeReader> readers, ICodeModelApplier applier,
    CodeSyncEvents events, ILogger<CodeSyncRunner> log, IConfiguration? config = null) : ICodeSyncRunner, Hosts.IPullRequestPreviews
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
        name = name.Length <= 60 ? name : name[..60].TrimEnd('/', '-');
        // A repository called "pr-5" must not read as a pull request preview.
        return PreviewPattern().IsMatch(name) ? name[..Math.Min(name.Length, 59)] + "_" : name;
    }

    /// <summary>The preview branch of pull / merge request <paramref name="number"/> ("code-sync/acme/sat-fsw/pr-12").</summary>
    public static string PreviewBranchNameFor(string repoDisplay, int number)
    {
        var suffix = $"/pr-{number}";
        var main = BranchNameFor(repoDisplay);
        if (main.Length + suffix.Length > 60) main = main[..(60 - suffix.Length)].TrimEnd('/', '-');
        return main + suffix;
    }

    /// <summary>The pull request a preview branch shows ("code-sync/acme/sat/pr-12" → 12); null for other branches.</summary>
    public static int? PreviewNumber(string? branchName) =>
        branchName is not null && PreviewPattern().Match(branchName) is { Success: true } m && int.TryParse(m.Groups[1].ValueSpan, out var n) ? n : null;

    public static bool IsPreviewBranch(string? branchName) => PreviewNumber(branchName) is not null;

    [System.Text.RegularExpressions.GeneratedRegex(@"^code-sync/.+/pr-([0-9]{1,9})$")]
    private static partial System.Text.RegularExpressions.Regex PreviewPattern();

    public static bool IsReviewBranch(string branchName) => branchName.StartsWith(BranchPrefix, StringComparison.Ordinal);

    public Task<CodeSyncOutcome> RunAsync(CodeSyncRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunCoreAsync(request, BranchNameFor(request.RepoDisplay), preview: false, ct);
    }

    /// <summary>
    /// Syncs the head of pull / merge request <paramref name="pullRequest"/> into its preview branch
    /// (<see cref="PreviewBranchNameFor"/>, created from main). Previews are for looking only: they are never accepted
    /// into main; the sync of the target branch after the merge brings the changes to review.
    /// </summary>
    public Task<CodeSyncOutcome> RunPreviewAsync(CodeSyncRequest request, int pullRequest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pullRequest);
        return RunCoreAsync(request, PreviewBranchNameFor(request.RepoDisplay, pullRequest), preview: true, ct);
    }

    /// <summary>
    /// Ends the preview of a pull request that was merged or closed: its open runs are closed and its branch deleted
    /// (kept, and said so, while someone has it open). Returns a short message.
    /// </summary>
    public async Task<string> ClosePreviewAsync(Guid projectId, string repoDisplay, int pullRequest, bool merged, CancellationToken ct = default)
    {
        var name = PreviewBranchNameFor(repoDisplay, pullRequest);
        var gate = LockFor(projectId);
        await gate.WaitAsync(ct);
        try
        {
            var branch = (await db.ListBranchesAsync(projectId)).FirstOrDefault(b => b.Name == name);
            if (branch is null) return $"No preview of PR #{pullRequest}.";
            var why = merged
                ? $"PR #{pullRequest} was merged; the sync of the target branch brings its changes to review"
                : $"PR #{pullRequest} was closed without merging";
            foreach (var run in (await db.ListCodeSyncRunsAsync(projectId, 500)).Where(r => r.IsOpen && r.BranchId == branch.Id))
                await db.UpdateCodeSyncRunAsync(run.Id, merged ? CodeSyncRunStatus.NoChanges : CodeSyncRunStatus.Rejected,
                    CodeSyncRuns.Clip($"{run.Summary.Split(" — ")[0]} — preview closed: {why}", 1000)!, run.ChangesJson);
            try
            {
                await VersionControl.DeleteBranchAsync(db, hub, projectId, branch.Id);
                return $"Preview {name} deleted: {why}.";
            }
            catch (InvalidOperationException ex)
            {
                log.LogInformation("Kept preview branch {Branch} of project {Project}: {Reason}", name, projectId, ex.Message);
                return $"Preview closed ({why}); {name} is kept because {ex.Message.TrimEnd('.')}.";
            }
        }
        finally
        {
            gate.Release();
            events.Notify(projectId);
        }
    }

    /// <summary>The net model changes of a preview branch's open runs (as the review lists them).</summary>
    public async Task<IReadOnlyList<Hosts.PreviewChange>> PreviewChangesAsync(Guid projectId, string repoDisplay, int pullRequest)
    {
        var name = PreviewBranchNameFor(repoDisplay, pullRequest);
        if ((await db.ListBranchesAsync(projectId)).FirstOrDefault(b => b.Name == name) is not { } branch) return [];
        var runs = (await db.ListCodeSyncRunsAsync(projectId, 500)).Where(r => r.IsOpen && r.BranchId == branch.Id).ToList();
        if (runs.Count == 0 || await db.LoadBranchAsync(branch.Id) is not { } models) return [];
        var baseModel = ProjectFile.Deserialize(models.BaseJson);
        var head = ProjectFile.Deserialize(await VersionControl.HeadJsonAsync(db, hub, projectId, branch.Id));
        return CodeSyncReviewService.Pending(baseModel, head, baseModel, runs).SelectMany(c => PreviewChanges(c, head.Find(c.ElementId) ?? baseModel.Find(c.ElementId))).ToList();
    }

    /// <summary>A pending change as pull request comment rows: a submodule bump (a change of the pins on the model root)
    /// reads "libs/core 1a2b3c4 → 5d6e7f8", kind "Submodule".</summary>
    private static IEnumerable<Hosts.PreviewChange> PreviewChanges(CodeSyncPendingChange c, Element? element)
    {
        var pins = c.Fields.Where(f => CodeSubmodulePins.IsPinsField(f.Key)).ToList();
        foreach (var bump in pins.SelectMany(f => CodeSubmodulePins.Bumps(f.Before, f.After)))
            yield return new Hosts.PreviewChange(bump.OldSha is null ? CodeSyncChangeKind.Added : bump.NewSha is null ? CodeSyncChangeKind.Removed : CodeSyncChangeKind.Changed,
                $"{bump.Path} {(bump.OldSha is { } o ? Short(o) : "∅")} → {(bump.NewSha is { } n ? Short(n) : "∅")}", "", "Submodule");
        if (pins.Count == 0 || pins.Count < c.Fields.Count)
            yield return new Hosts.PreviewChange(c.Kind, c.Name, c.Path, Metamodel.DisplayName(c.ElementKind))
            {
                Diagram = element is Diagram d ? d.DiagramType switch { DiagramType.Activity => "activity", DiagramType.Sequence => "sequence", _ => "structure" } : null,
            };
    }

    /// <summary>The branch a run is working on, and whether the run created it (a failed run deletes a branch it created).</summary>
    private sealed class RunBranch
    {
        public Guid? Id;
        public bool Created;
    }

    private async Task<CodeSyncOutcome> RunCoreAsync(CodeSyncRequest request, string branchName, bool preview, CancellationToken ct)
    {
        var gate = LockFor(request.ProjectId);
        await gate.WaitAsync(ct);
        var branch = new RunBranch();
        var runId = Guid.NewGuid();
        try
        {
            var outcome = await RunLockedAsync(request, branchName, preview, runId, branch, ct);
            events.Completed(request, outcome);
            return outcome;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await RecordFailureAsync(request, runId, await DiscardCreatedAsync(request, branch), "Cancelled before the sync finished.");
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Code sync of {Repo}@{Commit} into project {Project} failed", request.RepoDisplay, request.Commit, request.ProjectId);
            var summary = ex is NotImplementedException or InvalidOperationException ? ex.Message : $"The sync failed: {ex.GetType().Name}: {ex.Message}";
            await RecordFailureAsync(request, runId, await DiscardCreatedAsync(request, branch), summary);
            return new CodeSyncOutcome(CodeSyncStatus.Failed, summary, null, runId);
        }
        finally
        {
            gate.Release();
            events.Notify(request.ProjectId);
        }
    }

    /// <summary>A failed run leaves no branch it created behind. Returns the branch id to record (null once deleted).</summary>
    private async Task<Guid?> DiscardCreatedAsync(CodeSyncRequest request, RunBranch branch)
    {
        if (branch.Id is not { } id || !branch.Created) return branch.Id;
        try
        {
            await VersionControl.DeleteBranchAsync(db, hub, request.ProjectId, id);
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not delete code-sync branch {Branch} after a failed sync of project {Project}", id, request.ProjectId);
            return id;
        }
    }

    private async Task<CodeSyncOutcome> RunLockedAsync(CodeSyncRequest request, string branchName, bool preview, Guid runId, RunBranch runBranch, CancellationToken ct)
    {
        if (await db.LoadProjectAsync(request.ProjectId) is null) throw new InvalidOperationException("That project no longer exists.");

        var connection = await db.FindIntegrationAsync(request.ConnectionId) is { Kind: Hosts.CodeRepoKinds.CodeRepo } conn ? Hosts.CodeRepoConfig.Parse(conn.ConfigJson) : null;
        var diagramFiles = DiagramFilesOf(request, connection);

        // Pick the reader that handles the pushed files. A repository of diagram files only has no code to read.
        var reader = readers.FirstOrDefault(r => request.Files.Any(f => r.Handles(f.Path)));
        if (reader is null && diagramFiles.Count == 0)
        {
            var none = "No supported source files in this push.";
            await RecordAsync(request, runId, null, CodeSyncRunStatus.NoChanges, none, [], 0, 0, 0);
            return new CodeSyncOutcome(CodeSyncStatus.NoChanges, none, null, runId);
        }
        ct.ThrowIfCancellationRequested();
        var code = reader is null ? null : reader.Read(request.Files.Where(f => reader.Handles(f.Path)).ToList(), ct);
        var input = new SyncInput(code, diagramFiles);
        ct.ThrowIfCancellationRequested();
        var runs = await db.ListCodeSyncRunsAsync(request.ProjectId, 500);

        // The first import of a repository goes straight into main (unless the connection asks to review it too).
        string? note = null;
        if (!preview)
        {
            if (hub.Find(request.ProjectId, null) is { } openMain) await openMain.PersistAsync();
            var mainJson = await VersionControl.HeadJsonAsync(db, hub, request.ProjectId, null);
            if (IsFirstImport(request, connection, runs, mainJson))
            {
                var first = await ImportIntoMainAsync(request, input, connection, runs, mainJson, branchName, runId, ct);
                if (first.Outcome is { } done) return done;
                note = first.Note;   // could not go into main: review it on the branch, and say why
            }
        }

        // The review branch: found, or made from main's head. It accumulates runs until they are accepted.
        var branch = (await db.ListBranchesAsync(request.ProjectId)).FirstOrDefault(b => b.Name == branchName);
        var created = branch is null;
        var branchId = branch?.Id ?? await VersionControl.CreateBranchAsync(db, hub, request.ProjectId, branchName, BranchRef.Main, null, null);
        (runBranch.Id, runBranch.Created) = (branchId, created);

        if (hub.Find(request.ProjectId, branchId) is { } open) await open.PersistAsync();
        var headJson = await VersionControl.HeadJsonAsync(db, hub, request.ProjectId, branchId);
        var mapped = Map(request, input, connection, runs, headJson, ct);
        if (mapped.IsEmpty)
        {
            if (created) await db.DeleteBranchAsync(branchId);   // nothing to review: don't leave an empty branch behind
            runBranch.Id = null;
            var nothing = $"No model changes from {Short(request.Commit)}.";
            await RecordAsync(request, runId, created ? null : branchId, CodeSyncRunStatus.NoChanges, nothing, [], 0, 0, 0);
            return new CodeSyncOutcome(CodeSyncStatus.NoChanges, nothing, null, runId);
        }

        var message = CommitMessage(request);
        var json = await WriteAsync(request.ProjectId, branchId, headJson, mapped.Model, mapped.Ops);
        await db.CommitBranchVersionAsync(request.ProjectId, branchId, message, json, null);

        var (list, added, changed, removed, summary) = Describe(mapped, input);
        if (note is not null) summary = $"{summary} — {note}";
        await RecordAsync(request, runId, branchId, CodeSyncRunStatus.AwaitingReview, summary, list, added, changed, removed);
        return new CodeSyncOutcome(CodeSyncStatus.AwaitingReview, summary, ReviewUrl(request.ProjectId, branchId), runId);
    }

    /// <summary>What one mapping did to a copy of a line's head: the model, its operations and what changed.</summary>
    private sealed record Mapped(ModelRepository Before, ModelRepository Model, List<ModelOp> Ops, CodeSyncChanges Changes, DiagramRefreshResult Refresh)
    {
        public bool IsEmpty => Changes.IsEmpty && Refresh.Created.Count == 0;
    }

    /// <summary>What a run reads from the files: the code (null when no reader handles any of them) and the diagram files.</summary>
    private sealed record SyncInput(CodeModel? Code, IReadOnlyList<DiagramSourceFile> DiagramFiles)
    {
        public IReadOnlyList<string> Warnings => Code?.Warnings ?? [];
    }

    /// <summary>Maps the code onto a copy of <paramref name="headJson"/>, refreshes the diagrams that show it and imports
    /// the diagram files.</summary>
    private Mapped Map(CodeSyncRequest request, SyncInput input, Hosts.CodeRepoConfig? connection, IReadOnlyList<CodeSyncRunRecord> runs, string headJson, CancellationToken ct)
    {
        var before = ProjectFile.Deserialize(headJson);
        var model = ProjectFile.Deserialize(headJson);
        // Every change the sync makes, as replicable operations: applied to the live line they keep concurrent edits.
        var ops = new List<ModelOp>();
        model.OpEmitted += ops.Add;

        var root = model.Root;
        var options = new CodeSyncOptions
        {
            // The project's override, else the repository connection's root package (defaults to the repository name).
            RootPackage = root.Get(RootPackageField) is { Length: > 0 } rp ? rp : connection?.EffectiveRootPackage ?? "Code",
            RepoId = request.ConnectionId.ToString(),
            Commit = request.Commit,
            // A partial push (only the changed files) must not read as "everything else was deleted".
            RemoveDeleted = request.IsFullSnapshot,
            Submodules = request.Submodules.Where(s => s.Fetched).ToList(),
            Artifacts = connection?.DeploymentArtifacts ?? CodeArtifactMode.None,
            Behaviour = connection?.EffectiveBehaviour ?? CodeBehaviourMode.OnDemand,
            CollapseRootNamespace = connection?.CollapseRootNamespace ?? true,
            // The diagrams earlier runs of this repository made: only those may be cleaned up as trivial.
            SyncedDiagrams = runs.Where(r => r.ConnectionId == request.ConnectionId)
                .SelectMany(r => r.Changes).Where(c => c.Kind == CodeSyncChangeKind.Added && c.ElementKind == nameof(ElementKind.Diagram))
                .Select(c => c.ElementId).ToHashSet(StringComparer.Ordinal),
        };
        var changes = input.Code is { } code
            ? WithSubmodulePins(model, request, applier.Apply(model, code, options))
            : new CodeSyncChanges([], [], [], []);
        ct.ThrowIfCancellationRequested();

        // Diagrams are refreshed even when the code brought no change: a project synced before the package overview
        // existed gets it on its next sync.
        var refresh = input.Code is null ? DiagramRefreshResult.None : CodeSyncDiagrams.Refresh(model, changes, createDiagrams: AutoDiagrams(root, connection),
            codeRoot: CodeModelMapper.FindRootPackage(model, options.RootPackage));
        if (input.DiagramFiles.Count > 0 || connection?.EffectiveDiagramFiles == true)
            changes = Merge(changes, SyncDiagramFiles(model, input.DiagramFiles, options.RootPackage, options.RepoId, request.IsFullSnapshot));
        model.OpEmitted -= ops.Add;
        return new Mapped(before, model, ops, changes, refresh);
    }

    /// <summary>The change list, counts and summary of a mapping ("3 added; 2 structure, 1 activity diagram (1 warnings)").</summary>
    private static (List<CodeSyncRunChange> List, int Added, int Changed, int Removed, string Summary) Describe(Mapped m, SyncInput code)
    {
        var list = ChangeList(m.Before, m.Model, m.Changes, m.Refresh);
        int added = list.Count(c => c.Kind == CodeSyncChangeKind.Added), changed = list.Count(c => c.Kind == CodeSyncChangeKind.Changed),
            removed = list.Count(c => c.Kind == CodeSyncChangeKind.Removed);
        // Every diagram the run made: structure diagrams from the refresh, behaviour diagrams from the mapping.
        var made = m.Changes.Added.Select(m.Model.Find).OfType<Diagram>().Where(d => !IsFromDiagramFile(d)).Concat(m.Refresh.Created).DistinctBy(d => d.Id).ToList();
        var fromFiles = m.Changes.Added.Select(m.Model.Find).OfType<Diagram>().Count(IsFromDiagramFile);
        var (structure, activity, sequence) = DiagramCounts(made);
        var diagrams = DiagramSummary(structure, activity, sequence, m.Changes.TrivialDiagramsRemoved);
        if (fromFiles > 0) diagrams = (diagrams.Length > 0 ? diagrams + ", " : "") + $"{fromFiles} from diagram file{(fromFiles == 1 ? "" : "s")}";
        var summary = Summary(added, changed, removed) + (diagrams.Length > 0 ? "; " + diagrams : "")
                      + (m.Changes.Warnings.Count + code.Warnings.Count > 0 ? $" ({m.Changes.Warnings.Count + code.Warnings.Count} warnings)" : "");
        return (list, added, changed, removed, summary);
    }

    /// <summary>
    /// A connection's first import: it never had an accepted run, main holds none of its code (no element carries its
    /// <see cref="CodeFieldKeys.Repo"/>), and it does not ask to review the first import too
    /// (<see cref="Hosts.CodeRepoConfig.ReviewFirstImport"/>).
    /// </summary>
    public static bool IsFirstImport(CodeSyncRequest request, Hosts.CodeRepoConfig? connection, IReadOnlyList<CodeSyncRunRecord> runs, string mainJson)
    {
        if (connection?.ReviewFirstImport == true) return false;
        if (runs.Any(r => r.ConnectionId == request.ConnectionId && r.Status == CodeSyncRunStatus.Accepted)) return false;
        var repo = request.ConnectionId.ToString();
        return !ProjectFile.Deserialize(mainJson).AllElements.Any(e => e.Get(CodeFieldKeys.Repo) == repo);
    }

    /// <summary>The commit message of a first import ("Code sync: initial import of acme/sat at 1a2b3c4 (connected by Ann)").</summary>
    public static string InitialImportMessage(CodeSyncRequest r, string? connectedBy) =>
        $"Code sync: initial import of {r.RepoDisplay} at {Short(r.Commit)}{(connectedBy is { Length: > 0 } who ? $" (connected by {who})" : "")}";

    /// <summary>
    /// Applies a first import straight to main, as the system, through the live session when main is open (everyone sees it
    /// at once) and committed as a version of main. The run is recorded as accepted. A review branch left by an earlier
    /// unaccepted attempt is deleted (or, while someone has it open, put back to main). Returns the outcome, or a note
    /// saying why the import must be reviewed instead (the user it runs for may not edit main).
    /// </summary>
    private async Task<(CodeSyncOutcome? Outcome, string? Note)> ImportIntoMainAsync(CodeSyncRequest request, SyncInput code, Hosts.CodeRepoConfig? connection,
        IReadOnlyList<CodeSyncRunRecord> runs, string mainJson, string branchName, Guid runId, CancellationToken ct)
    {
        var user = request.TriggeredBy;
        string? who = null;
        // No one to check permissions for (a connection made before connectors were recorded, synced by a webhook):
        // never write main unchecked; review it instead. "Sync now" always runs as whoever pressed it.
        if (user is null)
            return (null, "waiting for review because no one is recorded as having connected this repository");
        if (user is { } u)
        {
            who = (await db.GetUserAsync(u))?.User.DisplayName ?? "the user who started the sync";
            if (await db.RoleOfAsync(request.ProjectId, u) is not (ProjectRole.Editor or ProjectRole.Owner))
                return (null, $"waiting for review because {who} can't edit main");
        }

        var mapped = Map(request, code, connection, runs, mainJson, ct);
        if (mapped.IsEmpty) return (null, null);   // nothing to import: the usual "no changes" run
        var refused = $"waiting for review because {who} can't change every package of main the import touches";
        if (user is { } actor)
        {
            try { await PackagePermissions.EnsureCanReplaceAsync(db, request.ProjectId, actor, mapped.Before, mapped.Model); }
            catch (PermissionDeniedException) { return (null, refused); }
        }

        var connectedBy = connection?.ConnectedBy is { } cb ? (await db.GetUserAsync(cb))?.User.DisplayName : who;
        var message = InitialImportMessage(request, connectedBy);
        try { await WriteMainAsync(request.ProjectId, mainJson, mapped, message, user); }
        catch (PermissionDeniedException) { return (null, refused); }

        await DiscardLeftoverBranchAsync(request, runs, branchName);
        var (list, added, changed, removed, summary) = Describe(mapped, code);
        var accepted = list.Select(c => c with { State = CodeSyncChangeState.Accepted }).ToList();
        summary = $"Imported into main: {summary}";
        await RecordAsync(request, runId, null, CodeSyncRunStatus.Accepted, summary, accepted, added, changed, removed);
        return (new CodeSyncOutcome(CodeSyncStatus.Accepted, summary, MainUrl(request.ProjectId), runId), null);
    }

    /// <summary>
    /// Writes a mapping onto main and commits it (no author: the system). Main open: the operations go through its live
    /// session (permission-checked for <paramref name="user"/>), which is then committed. Main closed: the stored head is
    /// replaced (or, if it moved since it was read, the operations are replayed onto it) while no session can open it.
    /// Returns the version number.
    /// </summary>
    private async Task<int> WriteMainAsync(Guid projectId, string readJson, Mapped mapped, string message, Guid? user)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (hub.Find(projectId, null) is { } live)
            {
                var view = user is { } u ? await PackagePermissions.ViewForAsync(db, projectId, u) : null;
                live.ApplySystemOps(mapped.Ops, null, view);
                return (await VersionControl.CommitAsync(db, live, message, null)).Number;
            }
            string? written = null;
            var done = await hub.ChangeClosedLineAsync<bool>(projectId, null, message, stored =>
            {
                if (stored == readJson) written = ProjectFile.Serialize(mapped.Model);
                else
                {
                    var current = ProjectFile.Deserialize(stored);
                    foreach (var op in mapped.Ops) current.ApplyRemote(op);
                    written = ProjectFile.Serialize(current);
                }
                return Task.FromResult<(string Json, bool Result)?>((written, true));
            });
            if (done is { } d && written is not null)
            {
                Data.History.ElementHistoryIndexer.For(db).VersionCommitted(projectId, d.VersionId, written);
                await hub.LibraryNoticesAsync(projectId, null, d.Version, written);
                return d.Version;
            }
            // Someone opened main meanwhile: write through the live session.
        }
        throw new InvalidOperationException("Main could not be updated; try the sync again.");
    }

    /// <summary>
    /// After a first import into main: the review branch an earlier, unaccepted attempt left behind goes (its open runs
    /// are closed as superseded). While someone has it open it is kept, put back to main's new head.
    /// </summary>
    private async Task DiscardLeftoverBranchAsync(CodeSyncRequest request, IReadOnlyList<CodeSyncRunRecord> runs, string branchName)
    {
        if ((await db.ListBranchesAsync(request.ProjectId)).FirstOrDefault(b => b.Name == branchName) is not { } branch) return;
        var onBranch = runs.Where(r => r.BranchId == branch.Id).ToList();
        if (onBranch.Any(r => r.ConnectionId != request.ConnectionId)) return;   // another repository's review: not ours to remove
        foreach (var run in onBranch.Where(r => r.IsOpen))
            await db.UpdateCodeSyncRunAsync(run.Id, CodeSyncRunStatus.NoChanges,
                CodeSyncRuns.Clip($"{run.Summary.Split(" — ")[0]} — superseded by the initial import into main", 1000)!, run.ChangesJson);
        try { await VersionControl.DeleteBranchAsync(db, hub, request.ProjectId, branch.Id); }
        catch (InvalidOperationException ex)
        {
            log.LogInformation("Kept code-sync branch {Branch} of project {Project} after the initial import: {Reason}", branchName, request.ProjectId, ex.Message);
            var main = await VersionControl.HeadJsonAsync(db, hub, request.ProjectId, null);
            if (hub.Find(request.ProjectId, branch.Id) is { } live)
            {
                live.Replace(main, Guid.Empty, "The code was imported into main; this review branch now matches main.");
                await live.PersistAsync();
            }
            else await db.SaveBranchAsync(branch.Id, main, null);
            await db.SetBranchBaseAsync(branch.Id, main, null);
        }
    }

    /// <summary>
    /// Records the commits the repository pins its submodules to on the model root
    /// (<see cref="CodeSubmodulePins.FieldKey"/>). A different pin is a change of the root, which "What changed" shows
    /// as a submodule bump next to the element changes the bump caused; accepting it moves main's pins forward.
    /// A partial push without submodule information leaves the pins alone.
    /// </summary>
    internal static CodeSyncChanges WithSubmodulePins(ModelRepository model, CodeSyncRequest request, CodeSyncChanges changes)
    {
        if (!request.IsFullSnapshot && request.Submodules.Count == 0) return changes;
        var key = CodeSubmodulePins.FieldKey(request.ConnectionId.ToString());
        var value = CodeSubmodulePins.Format(request.Submodules);
        if ((value ?? "") == model.Root.Get(key)) return changes;
        model.Set(model.Root, key, value);
        return changes.Changed.Contains(model.Root.Id) ? changes : changes with { Changed = [.. changes.Changed, model.Root.Id] };
    }

    /// <summary>
    /// Whether to diagram new namespaces and blocks: the model root's <see cref="AutoDiagramsField"/> when set, else the
    /// repository connection's setting (on by default).
    /// </summary>
    public static bool AutoDiagrams(Element root, Hosts.CodeRepoConfig? connection) => root.Get(AutoDiagramsField) switch
    {
        "false" => false,
        "true" => true,
        _ => connection?.AutoDiagrams ?? true,
    };

    /// <summary>
    /// Writes the sync onto the branch without losing edits made while it ran. With the branch open, the sync's
    /// operations are applied to the live session (everyone sees them at once, and what they changed meanwhile stays).
    /// Otherwise the result replaces the stored head, unless the head moved since it was read: then the operations are
    /// replayed onto the new head instead. Returns the model JSON to commit.
    /// </summary>
    private async Task<string> WriteAsync(Guid projectId, Guid branchId, string readJson, ModelRepository model, IReadOnlyList<ModelOp> ops)
    {
        if (hub.Find(projectId, branchId) is { } live)
        {
            live.ApplySystemOps(ops, null);
            await live.PersistAsync();
            return live.Snapshot();
        }
        var stored = (await db.LoadBranchAsync(branchId))?.HeadJson ?? throw new InvalidOperationException("The review branch was deleted during the sync.");
        string json;
        if (stored == readJson) json = ProjectFile.Serialize(model);
        else
        {
            var current = ProjectFile.Deserialize(stored);
            foreach (var op in ops) current.ApplyRemote(op);
            json = ProjectFile.Serialize(current);
        }
        await db.SaveBranchAsync(branchId, json, null);
        return json;
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

    /// <summary>Structure (package, block, internal block …), activity and sequence diagrams among <paramref name="diagrams"/>.</summary>
    public static (int Structure, int Activity, int Sequence) DiagramCounts(IEnumerable<Diagram> diagrams)
    {
        int structure = 0, activity = 0, sequence = 0;
        foreach (var d in diagrams)
        {
            if (d.DiagramType == DiagramType.Activity) activity++;
            else if (d.DiagramType == DiagramType.Sequence) sequence++;
            else structure++;
        }
        return (structure, activity, sequence);
    }

    /// <summary>"3 structure, 217 activity, 83 sequence diagrams; 380 trivial diagrams removed" (empty when there is nothing to say).</summary>
    public static string DiagramSummary(int structure, int activity, int sequence, int trivialRemoved)
    {
        var parts = new List<string>();
        if (structure + activity + sequence > 0) parts.Add($"{structure} structure, {activity} activity, {sequence} sequence diagram{(structure + activity + sequence == 1 ? "" : "s")}");
        if (trivialRemoved > 0) parts.Add($"{trivialRemoved} trivial diagram{(trivialRemoved == 1 ? "" : "s")} removed");
        return string.Join("; ", parts);
    }

    /// <summary>The workbench on the review branch with "What changed" open; absolute when Meridian:PublicUrl is set.</summary>
    public string ReviewUrl(Guid projectId, Guid branchId) =>
        Integrations.DeepLink.ForBranch(config?["Meridian:PublicUrl"] ?? "", projectId, branchId, Integrations.DeepLink.CodeChangesPanel);

    /// <summary>The workbench on main (where a first import lands); absolute when Meridian:PublicUrl is set.</summary>
    public string MainUrl(Guid projectId) => $"{(config?["Meridian:PublicUrl"] ?? "").TrimEnd('/')}/?project={projectId}";

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
