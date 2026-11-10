using Bunit;
using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>Branch sessions, three-way merges through the hub, baselines and element locks, against a real SQLite store.</summary>
public sealed class VersionControlTests : TestContext, IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-vc-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;
    private CollaborationHub _hub = default!;
    private UserAccount _ann = default!, _ben = default!;
    private Guid _project;

    public VersionControlTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        _hub = new CollaborationHub(_db, NullLogger<CollaborationHub>.Instance);
        _ann = await _db.CreateUserAsync("ann", "Ann", "correct horse");
        _ben = await _db.CreateUserAsync("ben", "Ben", "battery staple");
        _project = await _db.CreateProjectAsync("Vehicle", ProjectFile.Serialize(SampleModels.Vehicle()), _ann.Id);
        await _db.SetMemberAsync(_project, _ben.Id, ProjectRole.Editor);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    private async Task<Workspace> Open(UserAccount user, Guid? branch = null)
    {
        var ws = new Workspace { User = user };
        Assert.True(await ws.JoinAsync(_hub, _project, ProjectRole.Editor, branch));
        return ws;
    }

    private static Element Block(Workspace ws, string name) => ws.Model.OfKind(ElementKind.Block).First(b => b.Name == name);

    [Fact]
    public async Task Branch_Sessions_Are_Separate_And_Merge_Back_Into_Main()
    {
        var main = await Open(_ann);
        var v1 = await VersionControl.CommitAsync(_db, main.Session!, "Start", _ann.Id);
        var branchId = await VersionControl.CreateBranchAsync(_db, _hub, _project, "feature", BranchRef.Main, v1.Id, _ben.Id);

        var feature = await Open(_ben, branchId);
        Assert.Equal("feature", feature.BranchName);
        Assert.NotSame(main.Session, feature.Session);

        // Edits on each line stay on that line.
        feature.Model.Set(Block(feature, "Engine"), FieldKeys.Documentation, "From the branch");
        main.Model.Set(Block(main, "Vehicle"), FieldKeys.Documentation, "From main");
        Assert.Equal("", Block(main, "Engine").Documentation);
        Assert.Equal("", Block(feature, "Vehicle").Documentation);
        var vb = await VersionControl.CommitAsync(_db, feature.Session!, "Branch work", _ben.Id);
        Assert.Contains(await _db.ListBranchVersionsAsync(_project, branchId), v => v.Id == vb.Id);
        Assert.Contains("From the branch", (await _db.LoadBranchAsync(branchId))!.HeadJson);

        // Clean merge feature → main updates the live main session for everyone on it.
        var plan = await VersionControl.PrepareMergeAsync(_db, _hub, _project, new BranchRef(branchId, "feature"), BranchRef.Main);
        Assert.False(plan.Result.HasConflicts);
        var merge = await VersionControl.CompleteMergeAsync(_db, _hub, plan, plan.Result.Resolve(), "Merge feature", _ben.Id);
        Assert.Equal("From the branch", Block(main, "Engine").Documentation);
        Assert.Equal("From main", Block(main, "Vehicle").Documentation);
        Assert.Contains("Merged", main.Status);
        Assert.Contains(await _db.ListBranchVersionsAsync(_project, null), v => v.Id == merge.Id && v.Message == "Merge feature");

        // The merge base moved: merging again brings nothing new and raises no conflicts.
        var again = await VersionControl.PrepareMergeAsync(_db, _hub, _project, new BranchRef(branchId, "feature"), BranchRef.Main);
        Assert.False(again.Result.HasConflicts);
        Assert.Empty(again.IncomingChanges(again.Result.Model));

        // Update the branch from main: brings main's own edit over.
        var update = await VersionControl.PrepareMergeAsync(_db, _hub, _project, BranchRef.Main, new BranchRef(branchId, "feature"));
        await VersionControl.CompleteMergeAsync(_db, _hub, update, update.Result.Resolve(), "Update from main", _ben.Id);
        Assert.Equal("From main", Block(feature, "Vehicle").Documentation);

        main.Dispose();
        feature.Dispose();
    }

    [Fact]
    public async Task Conflicting_Merge_Uses_The_Chosen_Side_And_Detects_A_Moving_Target()
    {
        var main = await Open(_ann);
        var branchId = await VersionControl.CreateBranchAsync(_db, _hub, _project, "rework", BranchRef.Main, null, _ann.Id);
        var rework = await Open(_ben, branchId);
        main.Model.Set(Block(main, "Engine"), FieldKeys.Documentation, "Petrol");
        rework.Model.Set(Block(rework, "Engine"), FieldKeys.Documentation, "Electric");

        var plan = await VersionControl.PrepareMergeAsync(_db, _hub, _project, new BranchRef(branchId, "rework"), BranchRef.Main);
        var c = Assert.Single(plan.Result.Conflicts);
        Assert.Equal("Petrol", c.Ours);
        Assert.Equal("Electric", c.Theirs);
        c.Choice = MergeChoice.Theirs;

        // Someone edits main while the merge dialog is open: completing must refuse and ask for a fresh review.
        main.Model.Rename(Block(main, "Vehicle"), "Car");
        await Assert.ThrowsAsync<MergeOutdatedException>(() => VersionControl.CompleteMergeAsync(_db, _hub, plan, plan.Result.Resolve(), "Merge", _ben.Id));

        plan = await VersionControl.PrepareMergeAsync(_db, _hub, _project, new BranchRef(branchId, "rework"), BranchRef.Main);
        plan.Result.ChooseAll(MergeChoice.Theirs);
        await VersionControl.CompleteMergeAsync(_db, _hub, plan, plan.Result.Resolve(), "Merge rework", _ben.Id);
        Assert.Equal("Electric", Block(main, "Engine").Documentation);
        Assert.NotNull(main.Model.OfKind(ElementKind.Block).FirstOrDefault(b => b.Name == "Car"));

        // A branch someone is on cannot be deleted.
        await Assert.ThrowsAsync<InvalidOperationException>(() => VersionControl.DeleteBranchAsync(_db, _hub, _project, branchId));
        rework.Dispose();
        await Task.Delay(50);
        await VersionControl.DeleteBranchAsync(_db, _hub, _project, branchId);
        Assert.Empty(await _db.ListBranchesAsync(_project));
        main.Dispose();
    }

    [Fact]
    public async Task Element_Locks_Reject_Other_Users_Edits_And_Release_On_Leave()
    {
        var ann = await Open(_ann);
        var ben = await Open(_ben);
        var session = ann.Session!;
        var powertrain = ann.Model.AllElements.First(e => e.Kind == ElementKind.Package && e.OwnedElements.Any(c => c.Kind == ElementKind.Block))!;
        var inside = powertrain.OwnedElements.First(c => c.Kind == ElementKind.Block);

        Assert.Null(session.TryLock(ann.ParticipantId, powertrain.Id, subtree: true));
        Assert.NotNull(session.TryLock(ben.ParticipantId, inside.Id, subtree: false));   // covered by Ann's subtree lock
        Assert.NotNull(ben.LockOf(ben.Model.Find(inside.Id)!));
        Assert.True(ben.IsLockedForMe(ben.Model.Find(inside.Id)!));
        Assert.False(ann.IsLockedForMe(inside));

        // Ben edits a locked element: rejected, his replica is resynchronised and told why.
        ben.Model.Set(ben.Model.Find(inside.Id)!, FieldKeys.Documentation, "Ben was here");
        Assert.Equal("", ann.Model.Find(inside.Id)!.Documentation);
        Assert.Equal("", ben.Model.Find(inside.Id)!.Documentation);
        Assert.Contains("locked by Ann", ben.Status);
        Assert.DoesNotContain("Ben was here", session.Snapshot());

        // Deleting the package (which contains locked elements) is rejected too; unlocked elements stay editable.
        ben.Model.Delete(ben.Model.Find(powertrain.Id)!);
        Assert.NotNull(ann.Model.Find(powertrain.Id));
        Assert.NotNull(ben.Model.Find(powertrain.Id));
        var free = ben.Model.OfKind(ElementKind.Requirement).First();
        ben.Model.Set(free, FieldKeys.Documentation, "allowed");
        Assert.Equal("allowed", ann.Model.Find(free.Id)!.Documentation);

        // Ann (the holder) can still edit.
        ann.Model.Set(inside, FieldKeys.Documentation, "Ann's edit");
        Assert.Equal("Ann's edit", ben.Model.Find(inside.Id)!.Documentation);

        // Unlocking (only by the holder) or leaving releases the lock.
        Assert.False(session.Unlock(powertrain.Id, _ben.Id));
        Assert.Null(session.TryLock(ann.ParticipantId, inside.Id, subtree: false));
        ann.Dispose();
        Assert.Empty(session.Locks);
        ben.Model.Set(ben.Model.Find(inside.Id)!, FieldKeys.Documentation, "now free");
        Assert.Contains("now free", session.Snapshot());
        ben.Dispose();
    }

    [Fact]
    public async Task Merge_Dialog_Lists_Conflicts_And_Returns_The_Chosen_Resolution()
    {
        var b = SampleModels.Vehicle();
        var o = ProjectFile.Deserialize(ProjectFile.Serialize(b));
        var t = ProjectFile.Deserialize(ProjectFile.Serialize(b));
        o.Set(o.OfKind(ElementKind.Block).First(x => x.Name == "Engine"), FieldKeys.Documentation, "Ours text");
        t.Set(t.OfKind(ElementKind.Block).First(x => x.Name == "Engine"), FieldKeys.Documentation, "Theirs text");
        var plan = new MergePlan
        {
            ProjectId = _project, Source = new BranchRef(Guid.NewGuid(), "feature"), Target = BranchRef.Main,
            BaseJson = ProjectFile.Serialize(b), OursJson = ProjectFile.Serialize(o), TheirsJson = ProjectFile.Serialize(t),
            Result = ModelMerge.Merge(b, o, t),
        };
        var provider = RenderComponent<MudBlazor.MudDialogProvider>();
        var dialogs = Services.GetRequiredService<MudBlazor.IDialogService>();
        MudBlazor.IDialogReference? reference = null;
        await provider.InvokeAsync(async () => reference = await dialogs.ShowAsync<MergeDialog>("Merge",
            new MudBlazor.DialogParameters<MergeDialog> { { x => x.Plan, plan } }));
        Assert.Contains("Ours text", provider.Markup);
        Assert.Contains("Theirs text", provider.Markup);
        Assert.Single(provider.FindAll(".vc-crow:not(.vc-head)"));
        provider.FindAll(".vc-val.pick")[1].Click();   // theirs
        Assert.Equal(MergeChoice.Theirs, plan.Result.Conflicts[0].Choice);
        provider.FindAll("button").First(x => x.TextContent.Contains("Merge into main")).Click();
        var result = await reference!.Result;
        var decision = Assert.IsType<MergeDialog.Decision>(result!.Data);
        Assert.Equal("Theirs text", decision.Merged.OfKind(ElementKind.Block).First(x => x.Name == "Engine").Documentation);
    }
}
