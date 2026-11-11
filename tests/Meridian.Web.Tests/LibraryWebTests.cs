using Bunit;
using Meridian.Core.Analysis;
using Meridian.Core.Libraries;
using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Libraries;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>
/// Used projects / shared model libraries (docs/LIBRARIES.md) against a real SQLite store and live sessions: loading, read-only
/// enforcement by the session, per-user library permissions, the update review and its undo, and the dialogs and tree badge.
/// </summary>
public sealed class LibraryWebTests : TestContext, IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-lib-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;
    private CollaborationHub _hub = default!;
    private LibraryService _service = default!;
    private UserAccount _alice = default!, _bob = default!, _carol = default!;
    private Guid _parts, _vehicle;
    private string _pkgId = "", _wheelId = "", _loadId = "", _secretId = "";

    public LibraryWebTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        _hub = new CollaborationHub(_db, NullLogger<CollaborationHub>.Instance);
        _service = new LibraryService(_db, _hub);
        _alice = await _db.CreateUserAsync("alice", "Alice", "correct horse");
        _bob = await _db.CreateUserAsync("bob", "Bob", "battery staple");
        _carol = await _db.CreateUserAsync("carol", "Carol", "tr0ub4dor&3");

        var lib = new ModelRepository("Parts Library");
        var pkg = lib.Create(lib.Root, ElementKind.Package, "Standard parts");
        var wheel = lib.Create(pkg, ElementKind.Block, "Wheel");
        lib.Set(wheel, FieldKeys.Documentation, "18 inch");
        var load = lib.Create(pkg, ElementKind.Requirement, "Max load");
        lib.Set(load, FieldKeys.Text, "A wheel shall carry 500 kg.");
        var secret = lib.Create(pkg, ElementKind.Package, "Prototypes");
        lib.Create(secret, ElementKind.Block, "XJ9 Hubmotor");
        var d = lib.CreateDiagram(pkg, DiagramType.BlockDefinition, "Parts");
        lib.AddNode(d, wheel, new Bounds(10, 10, 120, 60));
        (_pkgId, _wheelId, _loadId, _secretId) = (pkg.Id, wheel.Id, load.Id, secret.Id);
        var json = ProjectFile.Serialize(lib);
        _parts = await _db.CreateProjectAsync("Parts Library", json, _alice.Id);
        await _db.CommitVersionAsync(_parts, "v1", json, _alice.Id);
        await _db.SetMemberAsync(_parts, _carol.Id, ProjectRole.Viewer);
        await _db.SetPackagePermissionAsync(_parts, secret.Id, PrincipalKind.User, _carol.Id, PackageAccess.None, _alice.Id);

        _vehicle = await _db.CreateProjectAsync("Vehicle", ProjectFile.Serialize(SampleModels.Vehicle()), _alice.Id);
        await _db.SetMemberAsync(_vehicle, _bob.Id, ProjectRole.Editor);
        await _db.SetMemberAsync(_vehicle, _carol.Id, ProjectRole.Editor);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    private async Task<Workspace> Open(UserAccount user)
    {
        var ws = new Workspace { User = user };
        Assert.True(await ws.JoinAsync(_hub, _vehicle, await _db.RoleOfAsync(_vehicle, user.Id) ?? ProjectRole.Viewer));
        return ws;
    }

    private Task<Guid> UseParts(string pin = "1") => _service.AddAsync(_vehicle, _parts, _pkgId, LibraryPinKind.Version, pin, null, _alice.Id);

    /// <summary>Commits v2 of the library: the wheel is bigger now.</summary>
    private async Task CommitV2()
    {
        var lib = ProjectFile.Deserialize((await _db.LoadProjectAsync(_parts))!.Value.ModelJson);
        lib.Set(lib.Find(_wheelId)!, FieldKeys.Documentation, "19 inch");
        var json = ProjectFile.Serialize(lib);
        await _db.SaveProjectAsync(_parts, json, _alice.Id);
        await _db.CommitVersionAsync(_parts, "v2", json, _alice.Id);
    }

    private static Element TypePart(Workspace ws, string wheelId)
    {
        var vehicle = ws.Model.OfKind(ElementKind.Block).First(b => b.Name == "Vehicle");
        var part = ws.Model.Create(vehicle, ElementKind.Property, "frontLeft");
        ws.Model.Set(part, FieldKeys.Type, wheelId);
        return part;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    // ================================================================= loading, storage and read-only enforcement

    [Fact]
    public async Task Used_Package_Loads_Read_Only_In_The_Session_And_Never_Reaches_The_Stored_Project()
    {
        var alice = await Open(_alice);
        await UseParts();
        Assert.Single(await _db.ListProjectUsagesAsync(_vehicle));
        await Eventually(() => alice.Model.HasLibraries);   // the open tab got the rebuilt model

        var wheel = alice.Model.Find(_wheelId)!;
        Assert.True(alice.Model.IsLibraryElement(wheel));
        Assert.Equal("Parts Library", alice.LibraryOf(wheel)!.SourceProjectName);
        Assert.False(alice.CanEdit(wheel));
        var part = TypePart(alice, _wheelId);
        Assert.Equal("Wheel", alice.Model.TypeDisplayName(part));

        // Editing the library element is refused locally, and by the session for an operation sent anyway.
        Assert.Throws<ReadOnlyElementException>(() => alice.Model.Rename(wheel, "Tyre"));
        var session = _hub.Find(_vehicle)!;
        session.Submit(alice.ParticipantId, _alice.Id, new SetFieldOp(_wheelId, FieldKeys.Name, "Tyre"));
        await Eventually(() => alice.Status.Contains("read-only here"));
        Assert.Equal("Wheel", session.ReadMaster(m => m.Find(_wheelId)!.Name));

        // What is stored has the part and its type reference, not the library.
        await session.PersistAsync();
        var stored = (await _db.LoadProjectAsync(_vehicle))!.Value.ModelJson;
        Assert.Contains(_wheelId, stored);
        Assert.Contains("frontLeft", stored);
        Assert.DoesNotContain("Standard parts", stored);
        Assert.DoesNotContain("18 inch", stored);
        var commit = await VersionControl.CommitAsync(_db, session, "with a library part", _alice.Id);
        Assert.DoesNotContain("18 inch", await _db.LoadVersionAsync(_vehicle, commit.Id) ?? "");

        // A new tab gets the library again from the usage.
        var again = await Open(_alice);
        Assert.Equal("Wheel", again.Model.TypeDisplayName(again.Model.Find(part.Id)!));
    }

    [Fact]
    public async Task Library_Content_Follows_The_Users_Permissions_In_The_Source_Project()
    {
        await UseParts();
        var alice = await Open(_alice);
        var bob = await Open(_bob);       // no role on the library project
        var carol = await Open(_carol);   // viewer of the library, "Prototypes" hidden from her

        var part = TypePart(alice, _wheelId);
        Assert.NotNull(alice.Model.Find(_secretId));
        Assert.False(bob.Model.HasLibraries);
        Assert.Null(bob.Model.Find(_wheelId));
        Assert.DoesNotContain("XJ9", ProjectFile.Serialize(bob.Model, includeLibraries: true));
        await Eventually(() => bob.Model.Find(part.Id) is not null);
        Assert.Equal(_wheelId, bob.Model.Find(part.Id)!.TypeRef);   // the reference is kept, unresolved for him
        Assert.True(carol.Model.HasLibraries);
        Assert.NotNull(carol.Model.Find(_wheelId));
        Assert.Empty(carol.Model.Find(_secretId)!.OwnedElements);   // the hidden package stays listed, its contents don't
        Assert.DoesNotContain("XJ9", ProjectFile.Serialize(carol.Model, includeLibraries: true));

        // Using a package needs read access to it: Bob can't add the library himself.
        await _service.RemoveAsync((await _db.ListProjectUsagesAsync(_vehicle))[0].Id, _alice.Id);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _service.AddAsync(_vehicle, _parts, _pkgId, LibraryPinKind.Version, "1", null, _bob.Id));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _service.AddAsync(_vehicle, _parts, _secretId, LibraryPinKind.Version, "1", null, _carol.Id));
        // A project can't use a project that uses it.
        await _service.AddAsync(_vehicle, _parts, _pkgId, LibraryPinKind.Version, "1", null, _alice.Id);
        var vehicleRoot = ProjectFile.Deserialize((await _db.LoadProjectAsync(_vehicle))!.Value.ModelJson).OfKind(ElementKind.Package).First().Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.AddAsync(_parts, _vehicle, vehicleRoot, LibraryPinKind.Branch, "main", null, _alice.Id));
    }

    // ================================================================= updates

    [Fact]
    public async Task Update_Is_Detected_Reviewed_Applied_With_Suspects_And_Undone_As_One_Step()
    {
        await UseParts();
        var alice = await Open(_alice);
        var part = TypePart(alice, _wheelId);
        var state = new LibraryState(alice, _service);
        Assert.Empty(await state.CheckAsync());

        await CommitV2();
        var offer = Assert.Single(await state.CheckAsync());
        Assert.Equal(("v1", "v2", LibraryPinKind.Version, "2"), (offer.CurrentLabel, offer.Label, offer.Kind, offer.Value));

        var plan = await _service.PrepareUpdateAsync(_hub.Find(_vehicle)!, offer.UsageId, offer.Kind, offer.Value, _alice.Id);
        Assert.Contains(plan.Diff, d => d.Id == _wheelId && d.Kind == DiffKind.Changed);
        Assert.Contains(plan.Affected, a => a.Referrer.Id == part.Id);
        Assert.Equal(ChangeHighlight.Changed, plan.Highlights[_wheelId]);

        var result = await state.ApplyAsync(plan);
        Assert.Equal(1, result.Suspect);
        Assert.Equal("19 inch", alice.Model.Find(_wheelId)!.Documentation);
        Assert.Equal("v2", alice.LibraryOf(alice.Model.Find(_wheelId))!.PinLabel);
        Assert.Contains("Wheel changed in Parts Library (v1 → v2)", SuspectLinks.LibraryChangeOf(alice.Model.Find(part.Id)!, alice.Model));
        Assert.Equal("2", (await _db.ListProjectUsagesAsync(_vehicle))[0].PinValue);
        Assert.Empty(state.Updates);
        Assert.StartsWith("Update “Parts Library” to v2", alice.Model.History.UndoDescription);

        alice.Model.History.Undo();
        await Eventually(() => alice.LibraryOf(alice.Model.Find(_wheelId))?.PinLabel == "v1");
        Assert.Equal("1", (await _db.ListProjectUsagesAsync(_vehicle))[0].PinValue);
        Assert.Equal("18 inch", alice.Model.Find(_wheelId)!.Documentation);
        Assert.Null(SuspectLinks.LibraryChangeOf(alice.Model.Find(part.Id)!));
    }

    [Fact]
    public async Task Removed_Library_Element_Becomes_A_Missing_Reference_Not_A_Crash()
    {
        await UseParts();
        var alice = await Open(_alice);
        var vehicle = alice.Model.OfKind(ElementKind.Block).First(b => b.Name == "Vehicle");
        var satisfy = alice.Model.Connect(ElementKind.Satisfy, vehicle, alice.Model.Find(_loadId)!);

        var lib = ProjectFile.Deserialize((await _db.LoadProjectAsync(_parts))!.Value.ModelJson);
        lib.Delete(lib.Find(_loadId)!);
        var json = ProjectFile.Serialize(lib);
        await _db.CommitVersionAsync(_parts, "v2 without the load requirement", json, _alice.Id);
        var usage = (await _db.ListProjectUsagesAsync(_vehicle))[0];
        var plan = await _service.PrepareUpdateAsync(_hub.Find(_vehicle)!, usage.Id, LibraryPinKind.Version, "2", _alice.Id);
        Assert.Contains(plan.Broken, b => b.Referrer.Id == satisfy.Id);
        await new LibraryState(alice, _service).ApplyAsync(plan);

        var r = (Relationship)alice.Model.Find(satisfy.Id)!;
        Assert.True(MissingReference.Is(r.Target));
        Assert.Contains(new Core.Validation.ModelValidator().Validate(alice.Model), i => i.RuleId == "LIB-001");
        await _hub.Find(_vehicle)!.PersistAsync();
        Assert.Contains(_loadId, (await _db.LoadProjectAsync(_vehicle))!.Value.ModelJson);   // kept, not dropped
    }

    // ================================================================= UI

    private void AddUi(Workspace ws)
    {
        Services.AddSingleton(ws);
        Services.AddSingleton<IMeridianStore>(_db);
        Services.AddSingleton(_hub);
        Services.AddSingleton(_service);
        Services.AddSingleton<LibraryState>();
        Services.AddSingleton<ImageCache>();
        RenderComponent<MudPopoverProvider>();
    }

    [Fact]
    public async Task Tree_Shows_The_Library_Badge_And_Pin_And_Diagrams_Mark_Library_Shapes()
    {
        await UseParts();
        var alice = await Open(_alice);
        AddUi(alice);
        var tree = RenderComponent<ModelTree>();
        var badge = tree.Find("[data-testid=lib-badge]");
        Assert.Contains("v1", badge.TextContent);
        Assert.Contains("Parts Library", badge.GetAttribute("title"));
        Assert.Contains("mtree-row", tree.Markup);
        Assert.Contains(" lib\"", tree.Markup.Replace("  ", " "));

        var bdd = alice.Model.Diagrams.First(d => !alice.Model.IsLibraryElement(d) && d.DiagramType == DiagramType.BlockDefinition);
        alice.Model.AddNode(bdd, alice.Model.Find(_wheelId)!, new Bounds(700, 400, 120, 60), showRelationships: false);   // dropping a library block
        Assert.Contains("lib-marker", SvgRenderer.Render(alice.Model, bdd));
        Assert.DoesNotContain("lib-marker", SvgRenderer.Render(alice.Model, alice.Model.Diagrams.First(alice.Model.IsLibraryElement)));
        Assert.True(alice.IsReadOnly(alice.Model.Diagrams.First(alice.Model.IsLibraryElement)));
    }

    [Fact]
    public async Task Used_Projects_Dialog_Adds_Lists_References_And_Removes_A_Usage()
    {
        var alice = await Open(_alice);
        AddUi(alice);
        var provider = RenderComponent<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        await provider.InvokeAsync(() => dialogs.ShowAsync<UsedProjectsDialog>("Used projects"));
        provider.WaitForAssertion(() => provider.Find("[data-testid=lib-empty]"));

        var dialog = provider.FindComponent<UsedProjectsDialog>();
        await provider.InvokeAsync(() => provider.FindComponents<MudSelect<Guid?>>()[0].Instance.ValueChanged.InvokeAsync(_parts));
        provider.WaitForAssertion(() => Assert.False(provider.Find("button[data-testid=lib-save]").HasAttribute("disabled")));
        provider.Find("button[data-testid=lib-save]").Click();

        provider.WaitForAssertion(() => Assert.Single(provider.FindAll("[data-testid=lib-usage]")));
        var usage = Assert.Single(await _db.ListProjectUsagesAsync(_vehicle));
        Assert.Equal((_parts, _pkgId, LibraryPinKind.Version, "1"), (usage.UsedProjectId, usage.RootPackageId, usage.PinKind, usage.PinValue));
        await Eventually(() => alice.Model.HasLibraries);

        TypePart(alice, _wheelId);
        dialog.Render();
        provider.WaitForAssertion(() => Assert.Contains("1 reference", provider.Find("[data-testid=lib-usage]").TextContent));
        provider.Find("[data-testid=lib-refs-toggle]").Click();
        Assert.Contains("frontLeft", provider.Find("[data-testid=lib-refs]").TextContent);

        // Removing warns about the reference first.
        provider.Find("[data-testid=lib-remove]").Click();
        provider.WaitForAssertion(() => Assert.Contains("refers to “Parts Library”", provider.Markup));
        provider.FindAll("button").First(b => b.TextContent.Trim() == "Remove").Click();
        provider.WaitForAssertion(() => Assert.Empty(provider.FindAll("[data-testid=lib-usage]")));
        Assert.Empty(await _db.ListProjectUsagesAsync(_vehicle));
    }

    [Fact]
    public async Task Update_Review_Shows_The_Changes_And_Affected_Elements_And_Accepts()
    {
        await UseParts();
        var alice = await Open(_alice);
        var part = TypePart(alice, _wheelId);
        AddUi(alice);
        await CommitV2();
        var state = Services.GetRequiredService<LibraryState>();
        var offer = Assert.Single(await state.CheckAsync());

        var badge = RenderComponent<LibraryUpdatesBadge>();
        Assert.Contains("Library update (1)", badge.Find("[data-testid=lib-updates-badge]").TextContent);

        var provider = RenderComponent<MudDialogProvider>();
        await provider.InvokeAsync(() => LibraryUpdateDialog.ShowAsync(Services.GetRequiredService<IDialogService>(), offer));
        provider.WaitForAssertion(() => provider.Find("[data-testid=lib-review]"));
        Assert.Contains("1 changed", provider.Find("[data-testid=lib-review]").TextContent);
        Assert.Contains("frontLeft", provider.Find("[data-testid=lib-affected]").TextContent);
        Assert.Contains("chg-changed", provider.Find("[data-testid=lib-review-diagram]").InnerHtml);
        Assert.Contains("19 inch", provider.Markup);

        provider.Find("button[data-testid=lib-accept]").Click();
        await Eventually(() => alice.LibraryOf(alice.Model.Find(_wheelId))?.PinLabel == "v2");
        Assert.NotNull(SuspectLinks.LibraryChangeOf(alice.Model.Find(part.Id)!));
        badge.WaitForAssertion(() => Assert.Empty(badge.FindAll("[data-testid=lib-updates-badge]")));
    }
}
