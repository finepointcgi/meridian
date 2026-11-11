using Bunit;
using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Data.History;
using Meridian.Serialization;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>The element history panel (inspector section and "History…" dialog) against a real SQLite store and live session.</summary>
public sealed class ElementHistoryPanelTests : TestContext, IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-hist-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;
    private CollaborationHub _hub = default!;
    private UserAccount _ann = default!, _bob = default!;
    private Guid _project;

    public ElementHistoryPanelTests()
    {
        Services.AddMudServices();
        Services.AddTransient(_ => _ws!);   // the tab being rendered (each test opens one or two)
        Services.AddTransient<IMeridianStore>(_ => _db);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private Workspace? _ws;

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        _hub = new CollaborationHub(_db, NullLogger<CollaborationHub>.Instance);
        _ann = await _db.CreateUserAsync("ann", "Ann", "correct horse");
        _bob = await _db.CreateUserAsync("bob", "Bob", "battery staple");
        _project = await _db.CreateProjectAsync("Vehicle", ProjectFile.Serialize(SampleModels.Vehicle()), _ann.Id);
        await _db.SetMemberAsync(_project, _bob.Id, ProjectRole.Editor);
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
        return _ws = ws;
    }

    /// <summary>Runs a UI action again when the panel re-rendered between finding the element and acting on it (async loads).</summary>
    private static void Retry(Action act)
    {
        for (var i = 0; ; i++)
            try { act(); return; }
            catch (Exception ex) when (i < 40 && (ex is Bunit.Rendering.UnknownEventHandlerIdException || ex.InnerException is ArgumentException))
            {
                Thread.Sleep(25);
            }
    }

    private static Element Engine(Workspace ws) => ws.Model.OfKind(ElementKind.Block).First(b => b.Name is "Engine" or "Motor");

    [Fact]
    public async Task Timeline_Shows_Each_Commit_With_A_Diff_And_Restores_A_Field()
    {
        var ws = await Open(_ann);
        var engine = Engine(ws);
        await VersionControl.CommitAsync(_db, ws.Session!, "Start", _ann.Id, _ann);
        ws.Model.Rename(engine, "Motor");
        ws.Model.Set(engine, FieldKeys.Documentation, "Moves the car forward");
        await VersionControl.CommitAsync(_db, ws.Session!, "Rename to Motor", _ann.Id, _ann);
        await ElementHistoryIndexer.For(_db).WhenIdleAsync(_project);

        var cut = RenderComponent<ElementHistoryPanel>(p => p.Add(x => x.Element, engine));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=hist-entry]").Count), TimeSpan.FromSeconds(10));
        var entries = cut.FindAll("[data-testid=hist-entry]");
        Assert.Contains("v2", entries[0].TextContent);
        Assert.Contains("Rename to Motor", entries[0].TextContent);
        Assert.Contains("Ann", entries[0].TextContent);
        Assert.Contains("Documentation, Name", entries[0].TextContent);
        Assert.Contains("First version", entries[1].TextContent);
        Assert.Empty(cut.FindAll("[data-testid=hist-uncommitted]"));

        // The diff of v2: before/after per field, text as an inline word diff.
        Retry(() => cut.FindAll("[data-testid=hist-entry]")[0].Click());
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=hist-field]").Count), TimeSpan.FromSeconds(10));
        var name = cut.Find("[data-testid=hist-field][data-key=name]");
        Assert.Contains("Engine", name.TextContent);
        Assert.Contains("Motor", name.TextContent);
        Assert.NotNull(name.QuerySelector(".w-removed"));
        Assert.NotNull(name.QuerySelector(".w-added"));

        // Compare v1 with the current model and restore the old name: one undoable edit through the session.
        Retry(() => cut.FindAll("[data-testid=hist-entry]")[1].Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid=hist-compare-current]"));
        Retry(() => cut.Find("[data-testid=hist-compare-current]").Click());
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=hist-field][data-key=name] [data-testid=hist-restore]")), TimeSpan.FromSeconds(10));
        Retry(() => cut.Find("[data-testid=hist-field][data-key=name] [data-testid=hist-restore]").Click());
        Assert.Equal("Engine", engine.Name);
        Assert.Equal("Engine", ProjectFile.Deserialize(ws.Session!.Snapshot()).Find(engine.Id)!.Name);   // the session got the edit

        // Now the element differs from the last commit: an "Uncommitted" entry with who did it.
        cut.WaitForAssertion(() => Assert.Contains("Uncommitted (current session)", cut.Find("[data-testid=hist-uncommitted]").TextContent), TimeSpan.FromSeconds(10));
        Assert.Contains("Ann", cut.Find("[data-testid=hist-uncommitted]").TextContent);
        ws.Model.History.Undo();
        Assert.Equal("Motor", engine.Name);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=hist-uncommitted]")), TimeSpan.FromSeconds(10));
        ws.Dispose();
    }

    [Fact]
    public async Task Relationships_Toggle_And_Viewers_Never_See_Hidden_History()
    {
        var ws = await Open(_ann);
        var engine = Engine(ws);
        await VersionControl.CommitAsync(_db, ws.Session!, "Start", _ann.Id, _ann);
        var secret = ws.Model.Create(ws.Model.Root, ElementKind.Package, "Secret");
        var hidden = ws.Model.Create(secret, ElementKind.Requirement, "Classified");
        var open = ws.Model.Create(ws.Model.Root, ElementKind.Requirement, "Public need");
        ws.Model.Connect(ElementKind.Satisfy, engine, hidden);
        ws.Model.Connect(ElementKind.Satisfy, engine, open);
        await VersionControl.CommitAsync(_db, ws.Session!, "Trace", _ann.Id, _ann);
        await _db.SetPackagePermissionAsync(_project, secret.Id, PrincipalKind.User, _bob.Id, PackageAccess.None, _ann.Id);
        await _hub.ReloadPermissionsAsync(_project);
        await ElementHistoryIndexer.For(_db).WhenIdleAsync(_project);

        var cut = RenderComponent<ElementHistoryPanel>(p => p.Add(x => x.Element, engine));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=hist-entry]")), TimeSpan.FromSeconds(10));
        Retry(() => cut.Find("[data-testid=hist-relationships]").Change(true));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid=hist-entry]").Count), TimeSpan.FromSeconds(10));
        Assert.Contains("Classified", cut.Markup);   // the owner sees everything
        ws.Dispose();

        // Bob has "Secret" hidden: the relationship to the hidden requirement is not in his history.
        var bob = await Open(_bob);
        var bobs = RenderComponent<ElementHistoryPanel>(p => p.Add(x => x.Element, Engine(bob)));
        Retry(() => bobs.Find("[data-testid=hist-relationships]").Change(true));
        bobs.WaitForAssertion(() => Assert.Equal(2, bobs.FindAll("[data-testid=hist-entry]").Count), TimeSpan.FromSeconds(10));
        Assert.Contains("Public need", bobs.Markup);
        Assert.DoesNotContain("Classified", bobs.Markup);
        bob.Dispose();
    }

    [Fact]
    public async Task Merges_Show_Their_Source_Branch_And_Show_Model_Opens_A_Read_Only_Version()
    {
        var ws = await Open(_ann);
        var v1 = await VersionControl.CommitAsync(_db, ws.Session!, "Start", _ann.Id, _ann);
        var branch = await VersionControl.CreateBranchAsync(_db, _hub, _project, "lighter", BranchRef.Main, v1.Id, _bob.Id);
        var feature = new Workspace { User = _bob };
        Assert.True(await feature.JoinAsync(_hub, _project, ProjectRole.Editor, branch));
        feature.Model.Set(Engine(feature), FieldKeys.Documentation, "Aluminium block");
        await VersionControl.CommitAsync(_db, feature.Session!, "Lighter engine", _bob.Id, _bob);
        var plan = await VersionControl.PrepareMergeAsync(_db, _hub, _project, new BranchRef(branch, "lighter"), BranchRef.Main);
        await VersionControl.CompleteMergeAsync(_db, _hub, plan, plan.Result.Resolve(), "Bring the lighter engine in", _bob.Id);
        await ElementHistoryIndexer.For(_db).WhenIdleAsync(_project);

        var cut = RenderComponent<ElementHistoryPanel>(p => p.Add(x => x.Element, Engine(ws)));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=hist-entry]").Count), TimeSpan.FromSeconds(10));
        var merge = cut.FindAll("[data-testid=hist-entry]")[0];
        Assert.Contains("Bring the lighter engine in", merge.TextContent);
        Assert.Contains("Merged from “lighter”", merge.TextContent);
        Assert.Contains("Bob", merge.TextContent);
        Assert.Contains("Documentation", merge.TextContent);

        // "All branches" adds the branch's own commit, labelled with its branch.
        Retry(() => cut.Find("[data-testid=hist-all-branches]").Change(true));
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("[data-testid=hist-entry]"), e => e.TextContent.Contains("Lighter engine") && e.TextContent.Contains("lighter")),
                             TimeSpan.FromSeconds(10));

        // Show model at v1: the read-only historical view of this line, which "Go to latest" returns to.
        Assert.Null(await ws.OpenVersionAsync(_db, _hub, v1.Id, 1));
        Assert.True(ws.ReadOnly);
        Assert.Equal("Version 1", ws.Pinned!.Label);
        Assert.Equal(BranchRef.Main, ws.HistoricalLine);
        Assert.Equal("", Engine(ws).Documentation);
        feature.Dispose();
        ws.Dispose();
    }
}
