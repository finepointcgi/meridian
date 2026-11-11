using Bunit;
using Meridian.Core.Analysis;
using Meridian.Core.Libraries;
using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Components.Mobile;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Libraries;
using Meridian.Web.Services;
using Meridian.Web.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>
/// Used projects (docs/LIBRARIES.md), the gaps after the first release: suspect marks in lines and projects nobody has open,
/// "library changed upstream" notices, the phone layout, "Open in source project" at the pin, and tabs kept across an update.
/// Real SQLite store, live sessions.
/// </summary>
public sealed class LibraryGapsWebTests : TestContext, IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-libgaps-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;
    private CollaborationHub _hub = default!;
    private LibraryService _service = default!;
    private UserAccount _alice = default!, _bob = default!, _carol = default!;
    private Guid _parts, _vehicle;
    private string _pkgId = "", _wheelId = "", _secretId = "", _libDiagramId = "", _xj9Id = "";

    public LibraryGapsWebTests()
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
        var secret = lib.Create(pkg, ElementKind.Package, "Prototypes");
        _xj9Id = lib.Create(secret, ElementKind.Block, "XJ9 Hubmotor").Id;
        var d = lib.CreateDiagram(pkg, DiagramType.BlockDefinition, "Parts");
        lib.AddNode(d, wheel, new Bounds(10, 10, 120, 60));
        (_pkgId, _wheelId, _secretId, _libDiagramId) = (pkg.Id, wheel.Id, secret.Id, d.Id);
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

    private async Task<Workspace> Open(UserAccount user, Guid? project = null)
    {
        var p = project ?? _vehicle;
        var ws = new Workspace { User = user };
        Assert.True(await ws.JoinAsync(_hub, p, await _db.RoleOfAsync(p, user.Id) ?? ProjectRole.Viewer));
        return ws;
    }

    private Task<Guid> UseParts(string pin = "1") => _service.AddAsync(_vehicle, _parts, _pkgId, LibraryPinKind.Version, pin, null, _alice.Id);

    /// <summary>Commits the next version of the library (through the store): the wheel is bigger, optionally its diagram is gone.</summary>
    private async Task CommitLibrary(string doc = "19 inch", bool dropDiagram = false)
    {
        var lib = ProjectFile.Deserialize((await _db.LoadProjectAsync(_parts))!.Value.ModelJson);
        lib.Set(lib.Find(_wheelId)!, FieldKeys.Documentation, doc);
        if (dropDiagram) lib.Delete(lib.Find(_libDiagramId)!);
        var json = ProjectFile.Serialize(lib);
        await _db.SaveProjectAsync(_parts, json, _alice.Id);
        await _db.CommitVersionAsync(_parts, doc, json, _alice.Id);
    }

    private static Element TypePart(ModelRepository m, string wheelId, string name = "frontLeft")
    {
        var vehicle = m.OfKind(ElementKind.Block).First(b => b.Name == "Vehicle");
        var part = m.Create(vehicle, ElementKind.Property, name);
        m.Set(part, FieldKeys.Type, wheelId);
        return part;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    private static LibraryChangeMark? MarkIn(string json, string elementId) =>
        ProjectFile.Deserialize(json).Find(elementId) is { } e ? LibraryChangeMark.Parse(e.Get(ReqFields.LibraryChange)) : null;

    private void AddUi(Workspace ws)
    {
        Services.AddSingleton(ws);
        Services.AddSingleton<IMeridianStore>(_db);
        Services.AddSingleton(_hub);
        Services.AddSingleton(_service);
        Services.AddSingleton<LibraryState>();
        Services.AddSingleton<ImageCache>();
        Services.AddSingleton<MobileUiState>();
        Services.AddSingleton(new MeridianHostInfo(false, Path.GetTempPath(), null));
        Services.AddSingleton<ReviewService>();
        Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        Services.AddSingleton(new Meridian.Web.Integrations.IntegrationService(_db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(),
            new FakeHttpClientFactory(new FakeHandler((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)))));
        RenderComponent<MudPopoverProvider>();
    }

    // ================================================================= 1. suspect marks where nobody is looking

    [Fact]
    public async Task Accepting_An_Update_Marks_Closed_Branches_And_Using_Projects_As_System_Commits_And_Undo_Clears_Them()
    {
        await UseParts();
        // The stored vehicle has a part typed by the library's Wheel; a branch "dev" is made from it and nobody opens it.
        var alice = await Open(_alice);
        var part = TypePart(alice.Model, _wheelId);
        var session = _hub.Find(_vehicle)!;
        await session.PersistAsync();
        var dev = await VersionControl.CreateBranchAsync(_db, _hub, _vehicle, "dev", BranchRef.Main, null, _alice.Id);

        // "Fleet" uses the vehicle's package, so it loads the parts library through it; its own part is typed by Wheel too.
        var vehicleModel = ProjectFile.Deserialize((await _db.LoadProjectAsync(_vehicle))!.Value.ModelJson);
        var vehiclePkg = vehicleModel.Root.OwnedElements.First(e => e.Kind == ElementKind.Package).Id;
        var fleetModel = new ModelRepository("Fleet");
        fleetModel.Create(fleetModel.Root, ElementKind.Block, "Vehicle");   // a block of its own (new ids), named like the helper expects
        var fleetPart = TypePart(fleetModel, _wheelId, "spare");
        var fleet = await _db.CreateProjectAsync("Fleet", ProjectFile.Serialize(fleetModel), _alice.Id);
        await _service.AddAsync(fleet, _vehicle, vehiclePkg, LibraryPinKind.Branch, "main", null, _alice.Id);
        Assert.Contains((await LibraryLoader.ResolveAsync(_db, _hub, fleet)).Payloads, p => p.Info.SourceProjectId == _parts);

        await CommitLibrary();
        var state = new LibraryState(alice, _service);
        var offer = Assert.Single(await state.CheckAsync());
        var plan = await _service.PrepareUpdateAsync(session, offer.UsageId, offer.Kind, offer.Value, _alice.Id);
        var result = await state.ApplyAsync(plan);

        // The open line (main) is marked live; dev and Fleet's main get the marks as a system commit.
        Assert.Equal(1, result.Suspect);
        Assert.NotNull(SuspectLinks.LibraryChangeOf(alice.Model.Find(part.Id)!));
        var devHead = (await _db.LoadBranchAsync(dev))!.HeadJson;
        Assert.Equal(_wheelId, MarkIn(devHead, part.Id)?.TargetId);
        Assert.DoesNotContain("19 inch", devHead);   // library content is never stored
        var devCommit = (await _db.ListBranchVersionsAsync(_vehicle, dev)).OrderByDescending(v => v.Number).First();
        Assert.StartsWith("Library update: “Parts Library” v1 → v2", devCommit.Message);
        var fleetHead = (await _db.LoadProjectAsync(fleet))!.Value.ModelJson;
        Assert.Equal(_wheelId, MarkIn(fleetHead, fleetPart.Id)?.TargetId);
        Assert.Contains(await _db.ListBranchVersionsAsync(fleet, null), v => v.Message.StartsWith("Library update"));
        Assert.Equal(3, result.Lines.Count);
        Assert.Contains(result.Lines, l => l.ProjectId == _vehicle && l.BranchId == null && l.Version is null);   // live
        Assert.Contains(result.Lines, l => l.ProjectId == _vehicle && l.BranchId == dev && l.Version is not null);
        Assert.Contains(result.Lines, l => l.ProjectId == fleet && l.Version is not null);

        // Opening the branch later shows the mark (nothing to re-check).
        var onDev = new Workspace { User = _alice };
        Assert.True(await onDev.JoinAsync(_hub, _vehicle, ProjectRole.Owner, dev));
        Assert.NotNull(SuspectLinks.LibraryChangeOf(onDev.Model.Find(part.Id)!));
        onDev.LeaveSession();
        await Eventually(() => _hub.Find(_vehicle, dev) is null);

        // ⌘Z moves the pin back and clears the marks everywhere, again as system commits where nobody is looking.
        alice.Model.History.Undo();
        await Eventually(() => alice.LibraryOf(alice.Model.Find(_wheelId))?.PinLabel == "v1");
        await Eventually(() => MarkIn(_db.LoadBranchAsync(dev).Result!.HeadJson, part.Id) is null);
        Assert.Null(MarkIn((await _db.LoadProjectAsync(fleet))!.Value.ModelJson, fleetPart.Id));
        Assert.Null(SuspectLinks.LibraryChangeOf(alice.Model.Find(part.Id)!));
    }

    [Fact]
    public async Task Only_Editors_Can_Accept_And_The_Marks_Never_Touch_Library_Elements()
    {
        await UseParts();
        var alice = await Open(_alice);
        TypePart(alice.Model, _wheelId);
        await _hub.Find(_vehicle)!.PersistAsync();
        await CommitLibrary();
        var usage = (await _db.ListProjectUsagesAsync(_vehicle))[0];
        await _db.SetMemberAsync(_vehicle, _bob.Id, ProjectRole.Viewer);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _service.PrepareUpdateAsync(_hub.Find(_vehicle)!, usage.Id, LibraryPinKind.Version, "2", _bob.Id));
        var plan = await _service.PrepareUpdateAsync(_hub.Find(_vehicle)!, usage.Id, LibraryPinKind.Version, "2", _alice.Id);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _service.ApplyUpdateAsync(plan, _bob.Id, null));
        await _service.ApplyUpdateAsync(plan, _alice.Id, null);
        Assert.Null(SuspectLinks.LibraryChangeOf(_hub.Find(_vehicle)!.ReadMaster(m => m.Find(_wheelId)!)));
        Assert.Equal("Wheel", _hub.Find(_vehicle)!.ReadMaster(m => m.Find(_wheelId)!.Name));
    }

    /// <summary>Everything Carol can get at for the vehicle: her snapshot, her model's exports and validation messages.</summary>
    private string CarolSees(Workspace carol)
    {
        var xmi = Path.Combine(Path.GetTempPath(), $"meridian-libgaps-{Guid.NewGuid():N}.xmi");
        try
        {
            XmiExchange.Export(carol.Model, xmi);
            return string.Join("\n",
                _hub.Find(_vehicle)!.SnapshotFor(carol.ParticipantId),
                ProjectFile.Serialize(carol.Model, includeLibraries: true),
                HtmlReport.Generate(carol.Model),
                HtmlReport.Generate(LibraryExport.Prepare(carol.Model, LibraryExportMode.Inline)),
                HtmlReport.Generate(LibraryExport.Prepare(carol.Model, LibraryExportMode.Reference)),
                SysMLv2Text.Export(carol.Model),
                File.ReadAllText(xmi),
                string.Join("\n", new Core.Validation.ModelValidator().Validate(carol.Model).Select(i => i.Message)));
        }
        finally { File.Delete(xmi); }
    }

    /// <summary>
    /// Alice sees everything; "Prototypes" is hidden from Carol in the library. Alice accepts an update that changed the hidden
    /// "XJ9 Hubmotor", which a part of the vehicle is typed by: Carol's inspector, snapshot, exports and validation say "a restricted
    /// library element", never the name; Alice's say "XJ9 Hubmotor".
    /// </summary>
    [Fact]
    public async Task A_Suspect_Mark_Never_Names_A_Library_Element_Hidden_From_The_Viewer()
    {
        await UseParts();
        var alice = await Open(_alice);
        var carol = await Open(_carol);
        var bob = await Open(_bob);
        var motor = TypePart(alice.Model, _xj9Id, "motor");
        await Eventually(() => carol.Model.Find(motor.Id) is not null && bob.Model.Find(motor.Id) is not null);
        Assert.DoesNotContain("XJ9", CarolSees(carol));

        var lib = ProjectFile.Deserialize((await _db.LoadProjectAsync(_parts))!.Value.ModelJson);
        lib.Set(lib.Find(_xj9Id)!, FieldKeys.Documentation, "48 V");
        var json = ProjectFile.Serialize(lib);
        await _db.SaveProjectAsync(_parts, json, _alice.Id);
        await _db.CommitVersionAsync(_parts, "motor voltage", json, _alice.Id);
        var state = new LibraryState(alice, _service);
        var offer = Assert.Single(await state.CheckAsync());
        await state.ApplyAsync(await _service.PrepareUpdateAsync(_hub.Find(_vehicle)!, offer.UsageId, offer.Kind, offer.Value, _alice.Id));

        Assert.Equal("XJ9 Hubmotor changed in Parts Library (v1 → v2)", SuspectLinks.LibraryChangeOf(alice.Model.Find(motor.Id)!, alice.Model));
        await Eventually(() => carol.Model.Find(motor.Id) is { } m && SuspectLinks.HasLibraryChange(m));
        Assert.Equal($"{LibraryChangeMark.Restricted} changed in Parts Library (v1 → v2)", SuspectLinks.LibraryChangeOf(carol.Model.Find(motor.Id)!, carol.Model));
        await Eventually(() => bob.Model.Find(motor.Id) is { } m && SuspectLinks.HasLibraryChange(m));
        Assert.Equal($"{LibraryChangeMark.Restricted} changed in {LibraryChangeMark.UnknownLibrary} (v1 → v2)", SuspectLinks.LibraryChangeOf(bob.Model.Find(motor.Id)!, bob.Model));

        var seen = CarolSees(carol);
        Assert.DoesNotContain("XJ9", seen);
        Assert.Contains(LibraryChangeMark.Restricted, seen);   // TRC-001 in her validation
        await _hub.Find(_vehicle)!.PersistAsync();
        Assert.DoesNotContain("XJ9", (await _db.LoadProjectAsync(_vehicle))!.Value.ModelJson);   // stored: ids only

        AddUi(carol);
        carol.Select(carol.Model.Find(motor.Id)!);
        var inspector = RenderComponent<Inspector>();
        var shown = inspector.Find("[data-testid=inspector-library-suspect]").TextContent;
        Assert.Contains(LibraryChangeMark.Restricted, shown);
        Assert.DoesNotContain("XJ9", inspector.Markup);
    }

    /// <summary>Marks written by the earlier format named the element; a session rewrites them to ids before anyone gets a snapshot.</summary>
    [Fact]
    public async Task Older_Named_Marks_Are_Neutralised_Before_They_Reach_A_Viewer()
    {
        await UseParts();
        var stored = ProjectFile.Deserialize((await _db.LoadProjectAsync(_vehicle))!.Value.ModelJson);
        var motor = TypePart(stored, _xj9Id, "motor");
        stored.Set(motor, ReqFields.LibraryChange, "XJ9 Hubmotor changed in Parts Library (v1 → v2)");
        await _db.SaveProjectAsync(_vehicle, ProjectFile.Serialize(stored), _alice.Id);
        await _db.CommitVersionAsync(_vehicle, "with an old mark", ProjectFile.Serialize(stored), _alice.Id);

        var carol = await Open(_carol);
        var alice = await Open(_alice);
        Assert.DoesNotContain("XJ9", CarolSees(carol));
        Assert.Equal($"{LibraryChangeMark.Restricted} changed in Parts Library (v1 → v2)", SuspectLinks.LibraryChangeOf(carol.Model.Find(motor.Id)!, carol.Model));
        Assert.Equal("XJ9 Hubmotor changed in Parts Library (v1 → v2)", SuspectLinks.LibraryChangeOf(alice.Model.Find(motor.Id)!, alice.Model));
        await _hub.Find(_vehicle)!.PersistAsync();
        Assert.DoesNotContain("XJ9", (await _db.LoadProjectAsync(_vehicle))!.Value.ModelJson);   // saved again without the name

        // A stored version from before is rewritten when shown from history, too.
        var old = (await _db.ListVersionsAsync(_vehicle)).Single(v => v.Message == "with an old mark");
        Assert.DoesNotContain("XJ9", PackagePermissions.RedactJson((await _db.LoadVersionAsync(_vehicle, old.Id))!, carol.Access));
    }

    [Fact]
    public async Task Committing_The_Source_Notifies_Owners_And_Editors_Who_Can_Read_It_Once_And_Respects_The_Setting()
    {
        var usageId = (await UseParts()).ToString("N");
        var parts = await Open(_alice, _parts);
        parts.Model.Set(parts.Model.Find(_wheelId)!, FieldKeys.Documentation, "20 inch");
        await VersionControl.CommitAsync(_db, parts.Session!, "bigger wheel", _alice.Id);

        async Task<NotificationInfo?> NoticeOf(UserAccount u) =>
            (await _db.ListNotificationsAsync(u.Id)).FirstOrDefault(n => n.Kind == NotificationKinds.LibraryUpdate && !n.Read);
        var forAlice = await NoticeOf(_alice);
        Assert.NotNull(forAlice);
        Assert.Equal((_vehicle, usageId), (forAlice!.ProjectId, forAlice.ElementId));
        Assert.Contains("v2 is available for “Vehicle” (pinned to v1)", forAlice.Text);
        Assert.NotNull(await NoticeOf(_carol));   // a viewer of the library, its package visible to her
        Assert.Null(await NoticeOf(_bob));        // can't read the library: not told about it

        // No second notice while the first is unread; none for a user who turned them off; none when the package didn't change.
        parts.Model.Set(parts.Model.Find(_wheelId)!, FieldKeys.Documentation, "21 inch");
        await VersionControl.CommitAsync(_db, parts.Session!, "even bigger", _alice.Id);
        Assert.Single(await _db.ListNotificationsAsync(_alice.Id), n => n.Kind == NotificationKinds.LibraryUpdate);
        await _db.MarkAllNotificationsReadAsync(_alice.Id);
        await _db.MarkAllNotificationsReadAsync(_carol.Id);
        await _db.SaveUserPreferencesAsync(_carol.Id, new UserPreferences { LibraryUpstreamNotices = false }.ToJson());
        parts.Model.Create(parts.Model.Root, ElementKind.Block, "Not shared");
        await VersionControl.CommitAsync(_db, parts.Session!, "outside the package", _alice.Id);
        Assert.Null(await NoticeOf(_alice));
        parts.Model.Set(parts.Model.Find(_wheelId)!, FieldKeys.Documentation, "22 inch");
        await VersionControl.CommitAsync(_db, parts.Session!, "again", _alice.Id);
        Assert.NotNull(await NoticeOf(_alice));
        Assert.Null(await NoticeOf(_carol));

        // The bell offers "Review update"; the review finds the offer for that usage.
        var vehicle = await Open(_alice);
        AddUi(vehicle);
        var bell = RenderComponent<NotificationBell>();
        bell.Find(".notif-btn").Click();
        bell.WaitForAssertion(() => Assert.Contains("Review update", bell.Find("[data-testid=notif-review-update]").TextContent));
        var offers = await Services.GetRequiredService<LibraryState>().CheckAsync();
        Assert.Contains(offers, o => o.UsageId.ToString("N") == usageId && o.Label == "v5");
    }

    // ================================================================= 2. phone layout

    [Fact]
    public async Task Phone_List_Shows_The_Library_Read_Only_With_Its_Badge_And_The_Update_Review_As_A_Sheet()
    {
        await UseParts();
        var alice = await Open(_alice);
        TypePart(alice.Model, _wheelId);
        AddUi(alice);
        var ui = Services.GetRequiredService<MobileUiState>();
        ui.SetViewport(390);
        Assert.True(ui.IsPhone);

        var list = RenderComponent<MobileModelList>();
        var badge = list.Find("[data-testid=m-lib-badge]");
        Assert.Contains("v1", badge.TextContent);
        Assert.Contains("Parts Library", badge.GetAttribute("title"));
        var row = list.Find($"li.m-row[data-id='{_pkgId}']");
        Assert.Contains("lib", row.ClassList);
        Assert.Contains("read-only", row.TextContent);
        row.QuerySelector(".m-row-main")!.Click();   // drill into the library
        list.WaitForAssertion(() => Assert.Contains("read-only", list.Find("[data-testid=m-lib-hint]").TextContent));
        Assert.Contains("Wheel", list.Markup);

        ui.OpenSheet(alice.Model.Find(_wheelId)!);
        var sheet = RenderComponent<MobileElementSheet>();
        Assert.Contains("From Parts Library · v1", sheet.Find("[data-testid=m-sheet-library]").TextContent);

        // The update badge in the top bar opens the review as a full-screen sheet.
        await CommitLibrary();
        var state = Services.GetRequiredService<LibraryState>();
        await state.CheckAsync();
        var updates = RenderComponent<MobileLibraryUpdates>();
        Assert.Equal("1", updates.Find("[data-testid=m-lib-updates] .n").TextContent);
        var provider = RenderComponent<MudDialogProvider>();
        updates.Find("[data-testid=m-lib-updates]").Click();
        provider.WaitForAssertion(() => provider.Find("[data-testid=lib-review]"));
        Assert.NotEmpty(provider.FindAll(".mud-dialog-fullscreen"));
        Assert.NotEmpty(provider.FindAll(".lib-sheet"));
        provider.Find("button[data-testid=lib-accept]").Click();
        await Eventually(() => alice.LibraryOf(alice.Model.Find(_wheelId))?.PinLabel == "v2");
        updates.WaitForAssertion(() => Assert.Empty(updates.FindAll("[data-testid=m-lib-updates]")));
    }

    // ================================================================= 3. "Open in source project" at the pin

    [Fact]
    public async Task Open_In_Source_Shows_The_Pinned_Version_Read_Only_Until_Go_To_Latest()
    {
        await UseParts();
        await CommitLibrary();   // the source moved on to v2; the vehicle still uses v1
        var alice = await Open(_alice);
        var lib = alice.LibraryOf(alice.Model.Find(_wheelId))!;

        Assert.Null(await alice.OpenAtPinAsync(_db, _hub, lib));
        Assert.Equal(_parts, alice.Session!.ProjectId);
        Assert.Equal("Viewing version 1 (pinned)", alice.Pinned!.Label);
        Assert.Equal("18 inch", alice.Model.Find(_wheelId)!.Documentation);
        Assert.True(alice.ReadOnly);
        Assert.True(alice.Model.History.ReadOnly);
        Assert.False(alice.CanEdit(alice.Model.Find(_wheelId)!));

        // The live head moving on doesn't reach the historical view.
        var other = await Open(_alice, _parts);
        other.Model.Rename(other.Model.Find(_wheelId)!, "Tyre");
        await Task.Delay(100);
        Assert.Equal("Wheel", alice.Model.Find(_wheelId)!.Name);

        AddUi(alice);
        var banner = RenderComponent<PinnedViewBanner>();
        Assert.Contains("Viewing version 1 (pinned)", banner.Find("[data-testid=pinned-view]").TextContent);
        Assert.Contains("Go to latest", banner.Find("[data-testid=pinned-latest]").TextContent);

        // Go to latest: the live main line.
        Assert.True(await alice.JoinAsync(_hub, _parts, ProjectRole.Owner));
        Assert.Null(alice.Pinned);
        Assert.False(alice.ReadOnly);
        Assert.Equal("Tyre", alice.Model.Find(_wheelId)!.Name);
        banner.WaitForAssertion(() => Assert.Empty(banner.FindAll("[data-testid=pinned-view]")));
    }

    [Fact]
    public async Task Open_In_Source_Hides_What_Is_Hidden_And_Follows_Branch_Pins_Live()
    {
        await UseParts();
        // Carol sees the library without "Prototypes"' contents; her pinned view of the source hides them too.
        var carol = await Open(_carol);
        Assert.Null(await carol.OpenAtPinAsync(_db, _hub, carol.LibraryOf(carol.Model.Find(_wheelId))!));
        Assert.NotNull(carol.Pinned);
        Assert.Empty(carol.Model.Find(_secretId)!.OwnedElements);
        Assert.DoesNotContain("XJ9", ProjectFile.Serialize(carol.Model));
        // Bob can't read the source project at all.
        var bob = await Open(_bob);
        var info = new LibraryInfo("u", _parts, "Parts Library", _pkgId, LibraryPinKind.Version, "1", "v1", "");
        Assert.Contains("don't have access", await bob.OpenAtPinAsync(_db, _hub, info));

        // A branch pin opens that branch, live and editable for its editors.
        var dev = await VersionControl.CreateBranchAsync(_db, _hub, _parts, "dev", BranchRef.Main, null, _alice.Id);
        var usage = (await _db.ListProjectUsagesAsync(_vehicle))[0];
        await _service.ChangeAsync(usage.Id, LibraryPinKind.Branch, "dev", null, _alice.Id);
        var alice = await Open(_alice);
        Assert.Null(await alice.OpenAtPinAsync(_db, _hub, alice.LibraryOf(alice.Model.Find(_wheelId))!));
        Assert.Null(alice.Pinned);
        Assert.Equal((dev, "dev"), (alice.Session!.BranchId!.Value, alice.BranchName));
        Assert.False(alice.ReadOnly);
    }

    // ================================================================= 4. tabs across an update

    [Fact]
    public async Task Accepting_An_Update_Keeps_Diagram_Tabs_The_Active_Tab_Selection_And_Zoom()
    {
        await UseParts();
        var alice = await Open(_alice);
        var part = TypePart(alice.Model, _wheelId);
        var own = alice.Model.Diagrams.Where(d => !alice.Model.IsLibraryElement(d)).Take(3).ToList();
        foreach (var d in own) alice.Open(d);
        alice.Open((Diagram)alice.Model.Find(_libDiagramId)!);   // the library's diagram, removed by the update
        var active = alice.Open(own[1]);
        active.SetZoom(1.7);
        var node = own[1].Nodes.First();
        active.Select(node.Element);
        alice.Select(part, syncCanvas: false);
        var tabsBefore = alice.OpenDiagrams.Select(c => c.Diagram.Id).ToList();        var (zoom, panX) = (active.Zoom, active.PanX);

        // A second tab of mine on the same project gets the rebuilt model through the session.
        var mine2 = await Open(_alice);
        foreach (var d in own) mine2.Open((Diagram)mine2.Model.Find(d.Id)!);
        mine2.Open((Diagram)mine2.Model.Find(own[2].Id)!);
        var mine2Tabs = mine2.OpenDiagrams.Select(c => c.Diagram.Id).ToList();

        await CommitLibrary(dropDiagram: true);
        var state = new LibraryState(alice, _service);
        var offer = Assert.Single(await state.CheckAsync());
        await state.ApplyAsync(await _service.PrepareUpdateAsync(_hub.Find(_vehicle)!, offer.UsageId, offer.Kind, offer.Value, _alice.Id));

        Assert.Equal(tabsBefore.Where(id => id != _libDiagramId), alice.OpenDiagrams.Select(c => c.Diagram.Id));
        Assert.Equal(own[1].Id, alice.Active!.Diagram.Id);
        Assert.NotSame(active, alice.Active);   // a new controller on the rebuilt model …
        Assert.Equal((zoom, panX), (alice.Active.Zoom, alice.Active.PanX));   // … where the old one was
        Assert.Equal([node.Element.Id], alice.Active.SelectedElements.Select(e => e.Id));
        Assert.Equal(part.Id, alice.Selected?.Id);
        Assert.Equal("19 inch", alice.Model.Find(_wheelId)!.Documentation);

        await Eventually(() => mine2.Model.Find(_wheelId)?.Documentation == "19 inch");
        Assert.Equal(mine2Tabs, mine2.OpenDiagrams.Select(c => c.Diagram.Id));
        Assert.Equal(own[2].Id, mine2.Active!.Diagram.Id);
    }

    [Fact]
    public async Task When_The_Active_Library_Diagram_Is_Removed_Its_Neighbour_Becomes_Active()
    {
        await UseParts();
        var alice = await Open(_alice);
        var own = alice.Model.Diagrams.First(d => !alice.Model.IsLibraryElement(d));
        foreach (var c in alice.OpenDiagrams.ToList()) alice.Close(c);
        alice.Open(own);
        alice.Open((Diagram)alice.Model.Find(_libDiagramId)!);
        await CommitLibrary(dropDiagram: true);
        var state = new LibraryState(alice, _service);
        var offer = Assert.Single(await state.CheckAsync());
        await state.ApplyAsync(await _service.PrepareUpdateAsync(_hub.Find(_vehicle)!, offer.UsageId, offer.Kind, offer.Value, _alice.Id));
        Assert.Equal([own.Id], alice.OpenDiagrams.Select(c => c.Diagram.Id));
        Assert.Equal(own.Id, alice.Active!.Diagram.Id);
    }
}
