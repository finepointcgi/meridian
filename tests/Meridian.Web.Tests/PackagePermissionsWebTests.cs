using Bunit;
using Meridian.Assistant.Changes;
using Meridian.Assistant.Engine;
using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Scripting;
using Meridian.Serialization;
using Meridian.Serialization.Mermaid;
using Meridian.Web.CodeSync;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>
/// Per-package permissions on the server, against a real SQLite store and live sessions: every write path is checked by the
/// session (or, for whole-model writes, before them), and data of hidden packages never reaches the user's model, markup,
/// exports, assistant context or notifications.
/// </summary>
public sealed class PackagePermissionsWebTests : TestContext, IAsyncLifetime
{
    private const string SecretName = "XJ9ReactorCore", SecretDoc = "TOP SECRET fuel ratios", SecretPackage = "Skunkworks";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-perm-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;
    private CollaborationHub _hub = default!;
    private UserAccount _alice = default!, _bob = default!;
    private Guid _project;
    private string _secretPkgId = "", _secretId = "", _assocId = "";

    public PackagePermissionsWebTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        _hub = new CollaborationHub(_db, NullLogger<CollaborationHub>.Instance);
        _alice = await _db.CreateUserAsync("alice", "Alice", "correct horse");
        _bob = await _db.CreateUserAsync("bob", "Bob", "battery staple");

        // The vehicle sample plus a secret package whose block appears on the (visible) structure diagram.
        var m = SampleModels.Vehicle();
        var pkg = m.Create(m.Root, ElementKind.Package, SecretPackage);
        var secret = m.Create(pkg, ElementKind.Block, SecretName);
        m.Set(secret, FieldKeys.Documentation, SecretDoc);
        var vehicle = m.OfKind(ElementKind.Block).First(b => b.Name == "Vehicle");
        var structure = vehicle.Owner!;
        var assoc = m.Connect(ElementKind.Association, vehicle, secret, structure);
        var bdd = m.Diagrams.First(d => d.Name == "Vehicle Structure");
        m.AddNode(bdd, secret, new Bounds(600, 400, 140, 70));
        m.AddEdge(bdd, assoc);
        (_secretPkgId, _secretId, _assocId) = (pkg.Id, secret.Id, assoc.Id);

        _project = await _db.CreateProjectAsync("Plant", ProjectFile.Serialize(m), _alice.Id);
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
        return ws;
    }

    private async Task Rule(string elementId, UserAccount who, PackageAccess level)
    {
        await _db.SetPackagePermissionAsync(_project, elementId, PrincipalKind.User, who.Id, level, _alice.Id);
        await _hub.ReloadPermissionsAsync(_project);
    }

    private string PkgId(string name) => ProjectFile.Deserialize(_hub.Find(_project)!.Snapshot()).OfKind(ElementKind.Package).First(p => p.Name == name).Id;
    private static Element Named(Workspace ws, string name) => ws.Model.AllElements.First(e => e.Name == name);
    private ModelRepository Master() => ProjectFile.Deserialize(_hub.Find(_project)!.Snapshot());

    private static void AssertNoSecrets(string text, string where)
    {
        foreach (var s in new[] { SecretName, SecretDoc })
            Assert.False(text.Contains(s, StringComparison.Ordinal), $"{where} contains “{s}”");
    }

    // ================================================================= server-side enforcement, per write path

    [Fact]
    public async Task Manual_Edits_To_A_Read_Only_Package_Are_Refused_By_The_Session_With_A_Clear_Message()
    {
        var alice = await Open(_alice);
        var bob = await Open(_bob);
        await Rule(PkgId("Structure"), _bob, PackageAccess.Read);

        bob.Model.Rename(Named(bob, "Engine"), "Hacked engine");
        Assert.Contains("“Structure” is read-only for you", bob.Status);
        Assert.NotNull(Named(bob, "Engine"));                       // Bob's model was put back
        Assert.Contains(Master().AllElements, e => e.Name == "Engine");
        Assert.DoesNotContain(alice.Model.AllElements, e => e.Name == "Hacked engine");

        // Bob still edits what he may (after resynchronising).
        bob.Model.Rename(Named(bob, "Refuel"), "Refuel quickly");
        Assert.Contains(Master().AllElements, e => e.Name == "Refuel quickly");
        Assert.Contains(alice.Model.AllElements, e => e.Name == "Refuel quickly");

        // Alice (owner) is not restricted, and the badge/read-only helpers agree with the session.
        alice.Model.Rename(Named(alice, "Engine"), "Engine v2");
        Assert.Contains(Master().AllElements, e => e.Name == "Engine v2");
        Assert.False(bob.CanEdit(Named(bob, "Engine v2")));
        Assert.True(bob.IsReadOnly((Diagram)Named(bob, "Vehicle Structure")));
        Assert.False(bob.IsReadOnly((Diagram)Named(bob, "Driver Use Cases")));
        Assert.Equal(("read", "Read-only for you (package permissions)"), bob.PermissionBadge(Named(bob, "Structure")));
        Assert.Equal("managed", alice.PermissionBadge(Named(alice, "Structure"))?.Css);
    }

    [Fact]
    public async Task Moves_Relationships_And_Diagrams_Of_Protected_Packages_Are_Refused()
    {
        var bob = await Open(_bob);
        await Rule(PkgId("Structure"), _bob, PackageAccess.Read);
        var before = ProjectFile.Serialize(Master());

        bob.Model.Move(Named(bob, "Refuel"), Named(bob, "Structure"));   // into a protected package
        Assert.Contains("read-only", bob.Status);
        bob.Model.Move(Named(bob, "Engine"), Named(bob, "Behavior"));   // out of it
        Assert.Contains("read-only", bob.Status);
        bob.Model.Connect(ElementKind.Dependency, Named(bob, "Refuel"), Named(bob, "Engine"), Named(bob, "Behavior"));   // target protected
        Assert.Contains("read-only", bob.Status);
        var bdd = (Diagram)Named(bob, "Vehicle Structure");                // a diagram owned by the protected package
        bob.Model.SetBounds(bdd, new Dictionary<DiagramNode, Bounds> { [bdd.Nodes[0]] = new(1, 1, 100, 60) });
        Assert.Contains("read-only", bob.Status);
        bob.Model.Delete(Named(bob, "Structure"));
        Assert.Equal(before, ProjectFile.Serialize(Master()));             // nothing of it reached the authoritative model
    }

    [Fact]
    public async Task Scripts_The_Assistant_And_Mermaid_Import_Go_Through_The_Same_Check()
    {
        var bob = await Open(_bob);
        await Rule(PkgId("Structure"), _bob, PackageAccess.Read);
        var before = ProjectFile.Serialize(Master());

        // Scripts edit the workspace model through the scripting API: the session refuses the operations.
        var api = new ModelApi(bob.Model);
        api.Set(api.FindByName("Engine")!, "documentation", "from a script");
        Assert.Contains("read-only", bob.Status);

        // The assistant says so before applying anything.
        var set = new ChangeSet(1);
        set.Add(new Proposal { Number = 1, Kind = ProposalKind.UpdateFields, SubjectRef = Named(bob, "Engine").Id, Title = "Document the engine",
                               Fields = [new("documentation", "from the assistant")] });
        var assistant = new AssistantService(bob, new AssistantSettings(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            new MeridianHostInfo(false, Path.GetTempPath(), null), new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()) { ReadEnvironment = _ => null },
            new AssistantConversations(), new AssistantClientFactory());
        var result = assistant.Apply(set);
        Assert.False(result.Ok);
        Assert.Contains("“Structure” is read-only for you", result.Message);

        // Mermaid import into the protected package: refused up front, and by the session when it is attempted anyway.
        bob.Select(Named(bob, "Engine"));
        Assert.Null(MermaidImport.Apply(bob, "flowchart TD\n  a[One] --> b[Two]\n", new MermaidImportOptions(), "test"));
        Assert.Contains("read-only", bob.Status);
        MermaidImporter.Import(bob.Model, Named(bob, "Structure"), "flowchart TD\n  a[One] --> b[Two]\n", new MermaidImportOptions());
        Assert.Equal(before, ProjectFile.Serialize(Master()));
    }

    [Fact]
    public async Task Merges_Version_Restores_And_Code_Sync_Decisions_Are_Checked()
    {
        var alice = await Open(_alice);
        var v1 = await VersionControl.CommitAsync(_db, alice.Session!, "Start", _alice.Id);
        var branchId = await VersionControl.CreateBranchAsync(_db, _hub, _project, "feature", BranchRef.Main, v1.Id, _alice.Id);
        var feature = await Open(_alice, branchId);
        feature.Model.Set(Named(feature, "Engine"), FieldKeys.Documentation, "Changed on the branch");
        await feature.Session!.PersistAsync();
        alice.Model.Rename(Named(alice, "Engine"), "Engine (main)");
        await Rule(PkgId("Structure"), _bob, PackageAccess.Read);

        // Bob can't merge changes to a package he may only read; Alice can.
        var plan = await VersionControl.PrepareMergeAsync(_db, _hub, _project, new BranchRef(branchId, "feature"), BranchRef.Main, _bob.Id);
        var denied = await Assert.ThrowsAsync<PermissionDeniedException>(() => VersionControl.CompleteMergeAsync(_db, _hub, plan, plan.Result.Resolve(), "Merge", _bob.Id));
        Assert.Contains("Structure", denied.Message);
        Assert.DoesNotContain("Changed on the branch", _hub.Find(_project)!.Snapshot());
        await VersionControl.CompleteMergeAsync(_db, _hub, plan, plan.Result.Resolve(), "Merge", _alice.Id);
        Assert.Contains("Changed on the branch", _hub.Find(_project)!.Snapshot());

        // Restoring a version that differs in the protected package.
        var bob = await Open(_bob);
        var v1Json = (await _db.LoadVersionAsync(_project, v1.Id))!;
        Assert.Throws<PermissionDeniedException>(() => bob.Session!.Reset(v1Json, _bob.Id));
        Assert.Contains("Changed on the branch", _hub.Find(_project)!.Snapshot());

        // With a hidden package Bob can't merge or review code changes at all (their diffs would show it).
        await Rule(_secretPkgId, _bob, PackageAccess.None);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => VersionControl.PrepareMergeAsync(_db, _hub, _project, new BranchRef(branchId, "feature"), BranchRef.Main, _bob.Id));
        var review = new CodeSyncReviewService(_db, _hub, new CodeSyncEvents(), NullLogger<CodeSyncReviewService>.Instance);
        var cs = await Assert.ThrowsAsync<PermissionDeniedException>(() => review.AcceptAsync(_project, null, null, _bob));
        Assert.Contains("Reviewing code changes", cs.Message);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => review.RejectAsync(_project, null, null, _bob));
    }

    // ================================================================= redaction

    [Fact]
    public async Task A_Hidden_Package_Never_Reaches_Bobs_Model_Markup_Exports_Or_Assistant()
    {
        await Rule(_secretPkgId, _bob, PackageAccess.None);
        var bob = await Open(_bob);

        // The model itself: the package is listed and empty; the association and every id of the hidden block are gone.
        var json = ProjectFile.Serialize(bob.Model);
        AssertNoSecrets(json, "Bob's model");
        Assert.DoesNotContain(_secretId, json);
        Assert.DoesNotContain(_assocId, json);
        Assert.Empty(Named(bob, SecretPackage).OwnedElements);
        Assert.Empty(bob.Model.Search("XJ9"));
        Assert.Equal(("none", "Hidden from you: you can see this package, not its contents"), bob.PermissionBadge(Named(bob, SecretPackage)));

        // Rendered markup: the tree, and the diagram (a nameless placeholder where the hidden block was).
        Services.AddSingleton(bob);
        Services.AddSingleton<ImageCache>();
        RenderComponent<MudPopoverProvider>();
        var tree = RenderComponent<ModelTree>();
        Assert.Contains(SecretPackage, tree.Markup);
        Assert.Contains("perm-badge none", tree.Markup);
        AssertNoSecrets(tree.Markup, "the model tree");
        var c = bob.Open((Diagram)Named(bob, "Vehicle Structure"));
        var canvas = RenderComponent<DiagramView>(p => p.Add(x => x.C, c));
        Assert.Contains("redacted-shape", canvas.Markup);
        AssertNoSecrets(canvas.Markup, "the diagram");

        // Exports and reports are made from Bob's model.
        AssertNoSecrets(XmiExchange.ToXml(bob.Model).ToString(), "XMI");
        AssertNoSecrets(SysMLv2Text.Export(bob.Model), "SysML v2 text");
        AssertNoSecrets(HtmlReport.Generate(bob.Model), "the HTML report");
        AssertNoSecrets(SvgRenderer.Render(bob.Model, c.Diagram), "the SVG export");
        AssertNoSecrets(SysMLv2Json.Element(bob.Model, bob.Model.Root).ToJsonString(), "SysML v2 JSON");

        // The assistant's context is built from it too.
        AssertNoSecrets(Prompts.Context(bob.Model, "Plant", null, c.Diagram, false), "the assistant context");

        // Shared SysML v2 text is exported from the whole model: not offered to Bob.
        Assert.Throws<PermissionDeniedException>(() => bob.Session!.OpenText(bob.ParticipantId, null, false, new NullTextClient()));

        // Stored versions are filtered before they are compared or downloaded.
        var alice = await Open(_alice);
        var v = await VersionControl.CommitAsync(_db, alice.Session!, "v1", _alice.Id);
        AssertNoSecrets(PackagePermissions.RedactJson((await _db.LoadVersionAsync(_project, v.Id))!, bob.Access), "a stored version");
        AssertNoSecrets(ProjectFile.Serialize(bob.RedactForMe(ProjectFile.Deserialize((await _db.LoadVersionAsync(_project, v.Id))!))), "a compared version");
    }

    [Fact]
    public async Task Live_Edits_In_A_Hidden_Package_Are_Not_Relayed_And_Moves_Refresh_The_Filtered_Model()
    {
        await Rule(_secretPkgId, _bob, PackageAccess.None);
        var alice = await Open(_alice);
        var bob = await Open(_bob);
        var replicas = new List<string>();

        alice.Model.Set(Named(alice, SecretName), FieldKeys.Documentation, SecretDoc + " v2");
        alice.Model.Rename(Named(alice, SecretName), SecretName + "Mk2");
        alice.Model.Create(Named(alice, SecretPackage), ElementKind.Block, SecretName + "Sibling");
        replicas.Add(ProjectFile.Serialize(bob.Model));

        // A visible block moved into the hidden package disappears for Bob; moved back out, it reappears.
        alice.Model.Move(Named(alice, "Engine"), Named(alice, SecretPackage));
        _hub.Find(_project)!.FlushResyncs();
        Assert.DoesNotContain(bob.Model.AllElements, e => e.Name == "Engine");
        replicas.Add(ProjectFile.Serialize(bob.Model));
        alice.Model.Move(Named(alice, "Engine"), Named(alice, "Structure"));
        _hub.Find(_project)!.FlushResyncs();
        Assert.Contains(bob.Model.AllElements, e => e.Name == "Engine");
        replicas.Add(ProjectFile.Serialize(bob.Model));

        foreach (var r in replicas) AssertNoSecrets(r, "Bob's replica");
        Assert.Equal(Master().Count - Master().AllElements.Count(e => e.Owner?.Name == SecretPackage) - 1, bob.Model.Count);   // minus the association
    }

    [Fact]
    public async Task Code_Sync_System_Operations_Are_Relayed_Filtered_And_Checked_For_A_User()
    {
        await Rule(_secretPkgId, _bob, PackageAccess.None);
        var alice = await Open(_alice);
        var bob = await Open(_bob);
        var session = alice.Session!;

        // What a code sync computes on a copy of the model: a change inside the hidden package and one outside.
        var copy = Master();
        var ops = new List<ModelOp>();
        copy.OpEmitted += ops.Add;
        copy.Set(copy.Find(_secretId)!, FieldKeys.Documentation, SecretDoc + " (from code)");
        copy.Create(copy.AllElements.First(e => e.Name == SecretPackage), ElementKind.Block, SecretName + "FromCode");
        copy.Set(copy.AllElements.First(e => e.Name == "Engine"), FieldKeys.Documentation, "from code");

        session.ApplySystemOps(ops, null);   // the system: no permission check, filtered relay
        Assert.Contains(alice.Model.AllElements, e => e.Name == SecretName + "FromCode");
        Assert.Equal("from code", Named(bob, "Engine").Documentation);
        AssertNoSecrets(ProjectFile.Serialize(bob.Model), "Bob's model after a code sync");

        // On behalf of a user, all or nothing.
        var more = new List<ModelOp>();
        copy.OpEmitted -= ops.Add;
        copy.OpEmitted += more.Add;
        copy.Set(copy.AllElements.First(e => e.Name == "Engine"), FieldKeys.Documentation, "by Bob");
        copy.Set(copy.Find(_secretId)!, FieldKeys.Documentation, "by Bob too");
        Assert.Throws<PermissionDeniedException>(() => session.ApplySystemOps(more, _bob.Id, bob.Access));
        Assert.Equal("from code", Named(alice, "Engine").Documentation);
        session.ApplySystemOps(more, _alice.Id, alice.Access);
        Assert.Equal("by Bob", Named(alice, "Engine").Documentation);
    }

    [Fact]
    public async Task Changing_Rules_Takes_Effect_At_Once_For_Connected_Users()
    {
        var bob = await Open(_bob);
        Assert.Contains(bob.Model.AllElements, e => e.Name == SecretName);
        await Rule(_secretPkgId, _bob, PackageAccess.None);
        AssertNoSecrets(ProjectFile.Serialize(bob.Model), "Bob's model after the rule");
        Assert.Contains("Package permissions changed", bob.Status);

        await _db.RemovePackagePermissionAsync(_project, _secretPkgId, PrincipalKind.User, _bob.Id);
        await _hub.ReloadPermissionsAsync(_project);
        Assert.Contains(bob.Model.AllElements, e => e.Name == SecretName);
    }

    [Fact]
    public async Task Team_Rules_Apply_To_Their_Members()
    {
        var org = await _db.CreateOrganizationAsync("Acme");
        var team = await _db.CreateTeamAsync(org, "Contractors");
        await _db.AddTeamMemberAsync(team, _bob.Id);
        await _db.SetPackagePermissionAsync(_project, _secretPkgId, PrincipalKind.Team, team, PackageAccess.None, _alice.Id);
        var bob = await Open(_bob);
        AssertNoSecrets(ProjectFile.Serialize(bob.Model), "Bob's model (team rule)");
        Assert.True((await PackagePermissions.ViewForAsync(_db, _project, _bob.Id)).HidesAnything);
        Assert.False((await PackagePermissions.ViewForAsync(_db, _project, _alice.Id)).HidesAnything);
    }

    [Fact]
    public async Task Comments_On_Hidden_Elements_Do_Not_Notify_Users_Who_Cannot_See_Them()
    {
        await Rule(_secretPkgId, _bob, PackageAccess.None);
        var alice = await Open(_alice);
        var reviews = new ReviewService(_db, _hub, alice);
        await reviews.CommentOnElementAsync(_secretId, $"@bob check the {SecretDoc}");
        Assert.Empty(await _db.ListNotificationsAsync(_bob.Id));
        await reviews.CommentOnElementAsync(Named(alice, "Engine").Id, "@bob and this one");
        Assert.Single(await _db.ListNotificationsAsync(_bob.Id));
    }

    // ================================================================= the dialog

    [Fact]
    public async Task Permissions_Dialog_Lets_A_Manager_Grant_And_Lists_The_Rules()
    {
        var alice = await Open(_alice);
        var bob = await Open(_bob);
        Services.AddSingleton(alice);
        Services.AddSingleton<IMeridianStore>(_db);
        Services.AddSingleton(_hub);
        Services.AddSingleton<Meridian.Web.Admin.IAuditLog>(new Meridian.Web.Admin.AuditLog(_db, NullLogger<Meridian.Web.Admin.AuditLog>.Instance));
        RenderComponent<MudPopoverProvider>();
        var provider = RenderComponent<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        var pkg = Named(alice, SecretPackage);
        await provider.InvokeAsync(() => dialogs.ShowAsync<PackagePermissionsDialog>("Permissions",
            new DialogParameters<PackagePermissionsDialog> { { x => x.Package, pkg } }));

        provider.WaitForAssertion(() => Assert.Contains("No rules on this package", provider.Markup));
        Assert.Contains("manage", provider.Find("[data-testid=perm-mine]").TextContent);
        var editor = provider.FindComponent<PackagePermissionsEditor>();
        await provider.InvokeAsync(() => editor.FindComponents<MudSelect<string>>()[0].Instance.ValueChanged.InvokeAsync($"user:{_bob.Id}"));
        await provider.InvokeAsync(() => editor.FindComponents<MudSelect<PackageAccess>>()[0].Instance.ValueChanged.InvokeAsync(PackageAccess.None));
        provider.Find("button[data-testid=perm-grant]").Click();

        provider.WaitForAssertion(() => Assert.Single(provider.FindAll("[data-testid=perm-rule]")));
        Assert.Contains("Bob", provider.Find("[data-testid=perm-rule]").TextContent);
        var rule = Assert.Single(await _db.ListPackagePermissionsAsync(_project));
        Assert.Equal((pkg.Id, _bob.Id, PackageAccess.None), (rule.ElementId, rule.PrincipalId, rule.Level));
        AssertNoSecrets(ProjectFile.Serialize(bob.Model), "Bob's model after the grant");   // live for Bob at once
        Assert.Contains((await _db.QueryAuditAsync(new AuditQuery())).Items, e => e.Action == "project.permission-set");

        // Removing the rule from the list.
        provider.Find("button[data-testid=perm-remove]").Click();
        provider.WaitForAssertion(() => Assert.Empty(provider.FindAll("[data-testid=perm-rule]")));
        Assert.Contains(bob.Model.AllElements, e => e.Name == SecretName);
    }

    [Fact]
    public async Task Permissions_Editor_Is_Read_Only_For_Non_Managers_And_Refuses_Them_Server_Side()
    {
        var bob = await Open(_bob);
        await Rule(PkgId("Structure"), _bob, PackageAccess.Read);
        Services.AddSingleton(bob);
        Services.AddSingleton<IMeridianStore>(_db);
        Services.AddSingleton(_hub);
        Services.AddSingleton<Meridian.Web.Admin.IAuditLog>(new Meridian.Web.Admin.AuditLog(_db, NullLogger<Meridian.Web.Admin.AuditLog>.Instance));
        RenderComponent<MudPopoverProvider>();
        var cut = RenderComponent<PackagePermissionsEditor>(p => p.Add(x => x.Package, Named(bob, "Structure")));
        Assert.Empty(cut.FindAll("[data-testid=perm-grant]"));
        Assert.Empty(cut.FindAll("[data-testid=perm-remove]"));
        Assert.Contains("read", cut.Find("[data-testid=perm-mine]").TextContent);

        // With "manage" on a package Bob can grant there (and only there).
        await Rule(PkgId("Behavior"), _bob, PackageAccess.Manage);
        var behavior = RenderComponent<PackagePermissionsEditor>(p => p.Add(x => x.Package, Named(bob, "Behavior")));
        Assert.NotEmpty(behavior.FindAll("[data-testid=perm-grant]"));

        // The project-wide list (Share dialog) shows every rule on packages Bob can see.
        var all = RenderComponent<PackagePermissionsEditor>();
        Assert.Equal(2, all.FindAll("[data-testid=perm-rule]").Count);
    }

    private sealed class NullTextClient : ISharedTextClient
    {
        public void TextOperation(string key, Meridian.Core.Collaboration.TextOperation op) { }
        public void TextAck(string key) { }
        public void TextCursors(string key, int revision, IReadOnlyList<TextCursor> cursors) { }
        public void TextStatus(string key, SharedTextStatus status) { }
        public void TextReset(string key, string text, int revision) { }
    }
}
