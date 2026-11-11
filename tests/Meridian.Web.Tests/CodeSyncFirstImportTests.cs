using Bunit;
using Meridian.Core.CodeSync;
using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.CodeSync;
using Meridian.Web.CodeSync.Hosts;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Meridian.Web.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>
/// The first import of a repository goes straight into main (docs/CODE-SYNC.md, "The first import"): later syncs, the
/// "Review the first import too" setting, a main that already holds the repository's code, two repositories, users who
/// can't edit main, leftovers of an earlier unaccepted attempt and pull request previews. Then the notices on main when
/// code waits for review: the banner (open, accept, dismiss, viewers, phones) and the empty project's hint.
/// </summary>
public sealed class CodeSyncFirstImportTests : TestContext, IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-first-import-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;
    private CollaborationHub _hub = default!;
    private CodeSyncEvents _events = default!;
    private CodeSyncReviewService _review = default!;
    private UserAccount _ann = default!, _ben = default!, _vic = default!;
    private Guid _project;
    private Guid _connection;
    private int _commits;

    public CodeSyncFirstImportTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        _hub = new CollaborationHub(_db, NullLogger<CollaborationHub>.Instance);
        _events = new CodeSyncEvents();
        _review = new CodeSyncReviewService(_db, _hub, _events, NullLogger<CodeSyncReviewService>.Instance);
        _ann = await _db.CreateUserAsync("ann", "Ann", "correct horse");
        _ben = await _db.CreateUserAsync("ben", "Ben", "battery staple");
        _vic = await _db.CreateUserAsync("vic", "Vic", "viewer only!");
        _project = await _db.CreateProjectAsync("Vehicle", ProjectFile.Serialize(SampleModels.Vehicle()), _ann.Id);
        await _db.SetMemberAsync(_project, _ben.Id, ProjectRole.Editor);
        await _db.SetMemberAsync(_project, _vic.Id, ProjectRole.Viewer);
        _connection = await ConnectAsync("acme/sat");
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    // ================================================================ fakes and helpers

    private sealed class FakeReader : ICodeReader
    {
        public string Language => "C#";
        public bool Handles(string path) => path.EndsWith(".cs", StringComparison.Ordinal);
        public CodeModel Read(IReadOnlyList<CodeFile> files, CancellationToken ct = default) => new("C#", [], []);
    }

    /// <summary>Adds a block named after the next queued name to the first package called "Structure" (else the root),
    /// stamped with the connection like the real mapper does. A run that maps twice (a first import that falls back to
    /// review) gets the same name again.</summary>
    private sealed class BlockApplier : ICodeModelApplier
    {
        private string _last = "";
        public Queue<string> Names { get; } = new();

        public CodeSyncChanges Apply(ModelRepository model, CodeModel code, CodeSyncOptions options)
        {
            var owner = model.OfKind(ElementKind.Package).FirstOrDefault(p => p.Name == "Structure") ?? model.Root;
            if (Names.Count > 0) _last = Names.Dequeue();
            var b = model.Create(owner, ElementKind.Block, _last);
            model.Set(b, CodeFieldKeys.Symbol, "Sat." + b.Name);
            model.Set(b, CodeFieldKeys.Repo, options.RepoId);
            return new CodeSyncChanges([b.Id], [], [], []);
        }
    }

    private readonly BlockApplier _applier = new();

    private CodeSyncRunner Runner() => new(_db, _hub, [new FakeReader()], _applier, _events, NullLogger<CodeSyncRunner>.Instance);

    private async Task<Guid> ConnectAsync(string repo, bool reviewFirst = false, Guid? connectedBy = null)
    {
        var id = Guid.NewGuid();
        var parts = repo.Split('/');
        var config = new CodeRepoConfig { Owner = parts[0], Repo = parts[1], ReviewFirstImport = reviewFirst, ConnectedBy = connectedBy ?? _ann.Id };
        await _db.SaveIntegrationAsync(new IntegrationConnection(id, _project, CodeRepoKinds.CodeRepo, repo, config.ToJson(), null, DateTime.UtcNow));
        return id;
    }

    private async Task SetReviewFirstAsync(Guid connection, bool on)
    {
        var row = (await _db.FindIntegrationAsync(connection))!;
        var config = CodeRepoConfig.Parse(row.ConfigJson);
        config.ReviewFirstImport = on;
        await _db.SaveIntegrationAsync(row with { ConfigJson = config.ToJson() });
    }

    /// <summary>Runs a sync as <paramref name="triggeredBy"/> (default: Ann, the owner who connected the repository, as the
    /// host passes it); <paramref name="anonymous"/> runs it with no one recorded.</summary>
    private Task<CodeSyncOutcome> SyncAsync(string block, Guid? connection = null, string repo = "acme/sat", Guid? triggeredBy = null, bool anonymous = false)
    {
        _applier.Names.Enqueue(block);
        return Runner().RunAsync(Request(connection ?? _connection, repo, $"Add {block}") with { TriggeredBy = anonymous ? null : triggeredBy ?? _ann.Id });
    }

    [Fact]
    public async Task A_First_Import_With_No_One_Recorded_Waits_For_Review()
    {
        // A connection made before connectors were recorded, synced by a webhook: main is never written unchecked.
        var outcome = await SyncAsync("Antenna", anonymous: true);
        Assert.Equal(CodeSyncStatus.AwaitingReview, outcome.Status);
        Assert.Contains("no one is recorded", outcome.Summary);
    }

    private CodeSyncRequest Request(Guid connection, string repo, string message) => new(
        _project, connection, repo, "main", $"{++_commits:D2}c0ffee{Guid.NewGuid():N}"[..40], $"https://git.example/{repo}/commit/abc",
        "Dana Dev", message, [new CodeFile("src/Battery.cs", "class Battery {}")], IsFullSnapshot: true);

    private async Task<ModelRepository> MainAsync() => ProjectFile.Deserialize(await VersionControl.HeadJsonAsync(_db, _hub, _project, null));

    private static Element? Named(ModelRepository m, string name) => m.AllElements.FirstOrDefault(e => e.Name == name && e is not Diagram);

    private async Task<Workspace> Open(UserAccount user, ProjectRole role, Guid? branch = null)
    {
        var ws = new Workspace { User = user };
        Assert.True(await ws.JoinAsync(_hub, _project, role, branch));
        return ws;
    }

    // ================================================================ the first import

    [Fact]
    public async Task The_First_Sync_Goes_Straight_Into_Main_Live_And_Is_Recorded_As_Accepted()
    {
        var live = await Open(_ben, ProjectRole.Editor);   // someone is working on main
        CodeSyncOutcome? completed = null;
        _events.RunCompleted += (_, o) => completed = o;

        var outcome = await SyncAsync("Battery");

        Assert.Equal(CodeSyncStatus.Accepted, outcome.Status);
        Assert.Equal("Imported into main: 1 added", outcome.Summary);
        Assert.Equal($"/?project={_project}", outcome.ReviewUrl);
        Assert.Same(outcome, completed);   // the architecture check runs on it like on any sync
        Assert.Empty(await _db.ListBranchesAsync(_project));   // no review branch
        Assert.NotNull(Named(live.Model, "Battery"));   // the open tab on main sees it at once
        Assert.NotNull(Named(await MainAsync(), "Battery"));

        var version = (await _db.ListBranchVersionsAsync(_project, null)).OrderByDescending(v => v.Number).First();
        var run = Assert.Single(await _db.ListCodeSyncRunsAsync(_project));
        Assert.Equal($"Code sync: initial import of acme/sat at {run.ShortCommit} (connected by Ann)", version.Message);
        Assert.Equal((CodeSyncRunStatus.Accepted, (Guid?)null, outcome.RunId), (run.Status, run.BranchId, (Guid?)run.Id));
        Assert.Equal(CodeSyncChangeState.Accepted, Assert.Single(run.Changes).State);
        Assert.Empty(await _review.LoadAsync(_project));   // nothing to review
        live.Dispose();
    }

    [Fact]
    public async Task The_First_Import_Into_A_Closed_Main_Is_Stored_And_Committed()
    {
        var before = (await _db.ListBranchVersionsAsync(_project, null)).Count;
        var outcome = await SyncAsync("Battery");
        Assert.Equal(CodeSyncStatus.Accepted, outcome.Status);
        Assert.NotNull(Named(ProjectFile.Deserialize((await _db.LoadProjectAsync(_project))!.Value.ModelJson), "Battery"));
        Assert.Equal(before + 1, (await _db.ListBranchVersionsAsync(_project, null)).Count);
    }

    [Fact]
    public async Task The_Second_Sync_Waits_For_Review_On_The_Branch()
    {
        await SyncAsync("Battery");
        var second = await SyncAsync("Radio");

        Assert.Equal(CodeSyncStatus.AwaitingReview, second.Status);
        var branch = Assert.Single(await _db.ListBranchesAsync(_project));
        Assert.Equal("code-sync/acme/sat", branch.Name);
        Assert.Null(Named(await MainAsync(), "Radio"));
        Assert.Equal(["Radio"], (await _review.LoadAsync(_project)).Single().Changes.Select(c => c.Name));
    }

    [Fact]
    public async Task Review_The_First_Import_Too_Keeps_The_First_Sync_On_The_Branch()
    {
        await SetReviewFirstAsync(_connection, true);
        var outcome = await SyncAsync("Battery");
        Assert.Equal(CodeSyncStatus.AwaitingReview, outcome.Status);
        Assert.Null(Named(await MainAsync(), "Battery"));
        Assert.Single(await _db.ListBranchesAsync(_project));
        Assert.False(new CodeRepoConfig().ReviewFirstImport);   // off by default
    }

    [Fact]
    public async Task Main_Already_Holding_This_Repositorys_Code_Means_Review()
    {
        var model = SampleModels.Vehicle();
        var old = model.Create(model.Root, ElementKind.Block, "Imported earlier");
        model.Set(old, CodeFieldKeys.Repo, _connection.ToString());
        await _db.SaveProjectAsync(_project, ProjectFile.Serialize(model), null);

        var outcome = await SyncAsync("Battery");
        Assert.Equal(CodeSyncStatus.AwaitingReview, outcome.Status);
        Assert.Null(Named(await MainAsync(), "Battery"));

        // Another repository's code in main does not count.
        var other = await ConnectAsync("acme/ground");
        Assert.Equal(CodeSyncStatus.Accepted, (await SyncAsync("Antenna", other, "acme/ground")).Status);
    }

    [Fact]
    public async Task Each_Of_Two_Repositories_Imports_Into_Main_Once()
    {
        var ground = await ConnectAsync("acme/ground");
        Assert.Equal(CodeSyncStatus.Accepted, (await SyncAsync("Battery")).Status);
        Assert.Equal(CodeSyncStatus.Accepted, (await SyncAsync("Antenna", ground, "acme/ground")).Status);
        var main = await MainAsync();
        Assert.NotNull(Named(main, "Battery"));
        Assert.NotNull(Named(main, "Antenna"));
        Assert.Empty(await _db.ListBranchesAsync(_project));

        Assert.Equal(CodeSyncStatus.AwaitingReview, (await SyncAsync("Dish", ground, "acme/ground")).Status);
        Assert.Equal("code-sync/acme/ground", Assert.Single(await _db.ListBranchesAsync(_project)).Name);
    }

    [Fact]
    public async Task Someone_Who_Cannot_Edit_Main_Gets_The_Review_Branch_With_A_Note()
    {
        var outcome = await SyncAsync("Battery", triggeredBy: _vic.Id);
        Assert.Equal(CodeSyncStatus.AwaitingReview, outcome.Status);
        Assert.Contains("waiting for review because Vic can't edit main", outcome.Summary);
        Assert.Null(Named(await MainAsync(), "Battery"));
        Assert.Contains("Vic can't edit main", Assert.Single(await _db.ListCodeSyncRunsAsync(_project)).Summary);
    }

    [Fact]
    public async Task Package_Permissions_On_Main_Are_Respected()
    {
        var structure = (await MainAsync()).OfKind(ElementKind.Package).First(p => p.Name == "Structure");
        await _db.SetPackagePermissionAsync(_project, structure.Id, PrincipalKind.User, _ben.Id, PackageAccess.Read, _ann.Id);

        var outcome = await SyncAsync("Battery", triggeredBy: _ben.Id);
        Assert.True(outcome.Status == CodeSyncStatus.AwaitingReview, outcome.Summary);
        Assert.Contains("Ben can't change every package of main", outcome.Summary);
        Assert.Null(Named(await MainAsync(), "Battery"));

        // Ann may: her sync of another repository goes in.
        var other = await ConnectAsync("acme/ground");
        Assert.Equal(CodeSyncStatus.Accepted, (await SyncAsync("Antenna", other, "acme/ground", _ann.Id)).Status);
    }

    [Fact]
    public async Task A_Leftover_Branch_Of_An_Unaccepted_First_Attempt_Is_Removed()
    {
        await SetReviewFirstAsync(_connection, true);
        await SyncAsync("Battery");
        var leftover = Assert.Single(await _db.ListBranchesAsync(_project));
        await SetReviewFirstAsync(_connection, false);

        var outcome = await SyncAsync("Radio");
        Assert.Equal(CodeSyncStatus.Accepted, outcome.Status);
        Assert.DoesNotContain(await _db.ListBranchesAsync(_project), b => b.Id == leftover.Id);
        var runs = await _db.ListCodeSyncRunsAsync(_project);
        Assert.Contains("superseded by the initial import into main", runs.Single(r => r.Id != outcome.RunId).Summary);
        Assert.Empty(await _review.LoadAsync(_project));
    }

    [Fact]
    public async Task A_Leftover_Branch_Someone_Has_Open_Is_Kept_But_Matches_Main()
    {
        await SetReviewFirstAsync(_connection, true);
        await SyncAsync("Battery");
        var leftover = Assert.Single(await _db.ListBranchesAsync(_project));
        var onBranch = await Open(_ben, ProjectRole.Editor, leftover.Id);
        await SetReviewFirstAsync(_connection, false);

        Assert.Equal(CodeSyncStatus.Accepted, (await SyncAsync("Radio")).Status);
        Assert.Single(await _db.ListBranchesAsync(_project));
        Assert.Null(Named(onBranch.Model, "Battery"));   // put back to main's head
        Assert.NotNull(Named(onBranch.Model, "Radio"));
        onBranch.Dispose();
    }

    [Fact]
    public async Task Pull_Request_Previews_Never_Import_Into_Main()
    {
        _applier.Names.Enqueue("Thruster");
        var outcome = await Runner().RunPreviewAsync(Request(_connection, "acme/sat", "Add thruster"), 3);
        Assert.Equal(CodeSyncStatus.AwaitingReview, outcome.Status);
        Assert.Null(Named(await MainAsync(), "Thruster"));
        Assert.Equal("code-sync/acme/sat/pr-3", Assert.Single(await _db.ListBranchesAsync(_project)).Name);
        Assert.Equal(CodeSyncStatus.Accepted, (await SyncAsync("Battery")).Status);   // the followed branch's first sync still does
    }

    [Fact]
    public void The_Initial_Import_Message_Names_The_Repository_Commit_And_Connector()
    {
        var r = Request(Guid.NewGuid(), "acme/sat", "x") with { Commit = "abcdef0123456789" };
        Assert.Equal("Code sync: initial import of acme/sat at abcdef0", CodeSyncRunner.InitialImportMessage(r, null));
        Assert.Equal("Code sync: initial import of acme/sat at abcdef0 (connected by Ann)", CodeSyncRunner.InitialImportMessage(r, "Ann"));
    }

    // ================================================================ the notice on main

    /// <summary>A pending review (the connection reviews its first import too), and the UI services for <paramref name="user"/>.</summary>
    private async Task<(Workspace Ws, CodeSyncState State, ThemeService Theme)> WaitingAsync(UserAccount user, ProjectRole role, Guid? branch = null, bool sync = true)
    {
        await SetReviewFirstAsync(_connection, true);
        if (sync) await SyncAsync("Battery");
        var ws = await Open(user, role, branch);
        Services.AddSingleton(ws);
        Services.AddSingleton<IMeridianStore>(_db);
        Services.AddSingleton(_hub);
        Services.AddSingleton(_events);
        Services.AddSingleton(_review);
        Services.AddSingleton<ImageCache>();
        Services.AddScoped<CodeSyncState>();
        Services.AddScoped<ThemeService>();
        var theme = Services.GetRequiredService<ThemeService>();
        await theme.LoadForAsync(user.Id);
        var state = Services.GetRequiredService<CodeSyncState>();
        await state.ReloadAsync();
        RenderComponent<MudBlazor.MudPopoverProvider>();
        return (ws, state, theme);
    }

    [Fact]
    public async Task The_Banner_Says_What_Waits_And_Accept_All_Brings_It_Into_Main()
    {
        var (ws, _, _) = await WaitingAsync(_ann, ProjectRole.Owner);
        var asked = 0;
        ws.Confirm = (_, _) => { asked++; return Task.FromResult(asked > 1); };   // "no" the first time, then "yes"
        var commit = (await _db.ListCodeSyncRunsAsync(_project)).Single().ShortCommit;

        var cut = RenderComponent<CodeReviewBanner>();
        Assert.Equal($"1 code change from acme/sat is waiting for review (commit {commit})", cut.Find(".code-review-banner .t").TextContent);
        Assert.Contains("Open the code branch", cut.Find("[data-testid=code-review-open]").TextContent);

        cut.Find("[data-testid=code-review-accept]").Click();
        Assert.Equal(1, asked);
        Assert.Null(Named(await MainAsync(), "Battery"));   // declined: nothing happened

        cut.Find("[data-testid=code-review-accept]").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=code-review-banner]")), TimeSpan.FromSeconds(5));
        Assert.NotNull(Named(await MainAsync(), "Battery"));
        Assert.Equal(CodeSyncRunStatus.Accepted, (await _db.ListCodeSyncRunsAsync(_project)).Single().Status);
        ws.Dispose();
    }

    [Fact]
    public async Task Dismiss_Hides_The_Banner_For_This_User_Until_A_New_Run_Arrives()
    {
        var (ws, _, _) = await WaitingAsync(_ann, ProjectRole.Owner);
        var cut = RenderComponent<CodeReviewBanner>();
        cut.Find("[data-testid=code-review-dismiss]").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=code-review-banner]")));
        var stored = UserPreferences.FromJson(await _db.GetUserPreferencesAsync(_ann.Id));
        Assert.True(stored.CodeReviewsDismissed.ContainsKey(_project.ToString()));   // kept per user, in preferences

        await SyncAsync("Radio");   // a new push brings it back
        cut.WaitForAssertion(() => Assert.Contains("2 code changes from acme/sat are waiting", cut.Find(".code-review-banner .t").TextContent), TimeSpan.FromSeconds(5));
        ws.Dispose();
    }

    [Fact]
    public async Task Viewers_See_The_Banner_Without_Accept_And_The_Code_Branch_Shows_None()
    {
        var (ws, _, _) = await WaitingAsync(_vic, ProjectRole.Viewer);
        var cut = RenderComponent<CodeReviewBanner>();
        Assert.Single(cut.FindAll("[data-testid=code-review-open]"));
        Assert.Empty(cut.FindAll("[data-testid=code-review-accept]"));

        cut.Find("[data-testid=code-review-open]").Click();   // no workbench here: opens "What changed"
        Assert.Equal(ViewTabKind.CodeChanges, ws.ActiveView?.Kind);
        ws.Dispose();
    }

    [Fact]
    public async Task The_Banner_Is_Not_Shown_On_The_Code_Sync_Branch()
    {
        await SetReviewFirstAsync(_connection, true);
        await SyncAsync("Battery");
        var branch = Assert.Single(await _db.ListBranchesAsync(_project));
        var (ws, _, _) = await WaitingAsync(_ann, ProjectRole.Owner, branch.Id, sync: false);
        Assert.Empty(RenderComponent<CodeReviewBanner>().FindAll("[data-testid=code-review-banner]"));
        ws.Dispose();
    }

    [Fact]
    public async Task The_Phone_Banner_Is_Compact_And_Has_No_Accept()
    {
        var (ws, _, _) = await WaitingAsync(_ann, ProjectRole.Owner);
        var cut = RenderComponent<CodeReviewBanner>(p => p.Add(x => x.Compact, true));
        Assert.Contains("compact", cut.Find("[data-testid=code-review-banner]").ClassList);
        Assert.Equal("Open", cut.Find("[data-testid=code-review-open]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=code-review-accept]"));
        ws.Dispose();
    }

    [Fact]
    public async Task An_Empty_Main_Says_The_Code_Is_On_The_Review_Branch()
    {
        _project = await _db.CreateProjectAsync("Empty", ProjectFile.Serialize(new ModelRepository("Empty")), _ann.Id);
        await _db.SetMemberAsync(_project, _vic.Id, ProjectRole.Viewer);
        _connection = await ConnectAsync("acme/sat", reviewFirst: true);
        var (ws, _, _) = await WaitingAsync(_ann, ProjectRole.Owner);
        string? action = null;

        var cut = RenderComponent<EmptyProjectHint>(p => p.Add(x => x.OnAction, a => action = a));
        Assert.Contains("Your code is on the review branch", cut.Markup);
        Assert.Contains("code-sync/acme/sat", cut.Find("[data-testid=empty-code-waiting]").TextContent);
        cut.Find($"[data-action={EmptyProjectHint.OpenCodeBranch}]").Click();
        Assert.Equal(EmptyProjectHint.OpenCodeBranch, action);
        cut.Find($"[data-action={EmptyProjectHint.AcceptCode}]").Click();
        Assert.Equal(EmptyProjectHint.AcceptCode, action);
        ws.Dispose();
    }

    [Fact]
    public void Dismissals_Are_Kept_For_At_Most_200_Projects()
    {
        var p = new UserPreferences();
        for (var i = 0; i < 250; i++) p.CodeReviewsDismissed[Guid.NewGuid().ToString()] = DateTime.UtcNow.AddMinutes(i);
        p.CodeReviewsDismissed["not a project"] = DateTime.UtcNow;
        var back = UserPreferences.FromJson(p.ToJson());
        Assert.Equal(UserPreferences.MaxCodeReviewsDismissed, back.CodeReviewsDismissed.Count);
        Assert.DoesNotContain("not a project", back.CodeReviewsDismissed.Keys);
    }
}
