using Bunit;
using Meridian.Core.Collaboration;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>Collaborative SysML v2 text through the collaboration hub, with two simulated editors whose messages are queued
/// (as over a connection) and delivered later.</summary>
public class SharedTextTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-st-{Guid.NewGuid():N}.db");
    private SqliteStore _db = null!;
    private CollaborationHub _hub = null!;
    private Guid _project;
    private UserAccount _ann = null!, _ben = null!;

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        _ann = await _db.CreateUserAsync("ann", "Ann Lee", "correct horse");
        _ben = await _db.CreateUserAsync("ben", "Ben Ito", "battery staple");
        _project = await _db.CreateProjectAsync("Vehicle", ProjectFile.Serialize(SampleModels.Vehicle()), _ann.Id);
        await _db.SetMemberAsync(_project, _ben.Id, ProjectRole.Editor);
        _hub = new CollaborationHub(_db, NullLogger<CollaborationHub>.Instance);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    /// <summary>A simulated browser editor: the OT client algorithm plus an inbox of server messages.</summary>
    private sealed class Editor(Workspace ws) : ISharedTextClient
    {
        private readonly Queue<Action> _inbox = new();
        public Workspace Ws { get; } = ws;
        public ProjectSession Session => Ws.Session!;
        public TextClient Client { get; private set; } = null!;
        public string Key { get; private set; } = "";
        public SharedTextStatus? Status { get; private set; }
        public IReadOnlyList<TextCursor> Cursors { get; private set; } = [];
        public string Text => Client.Text;

        public void Open(string? scopeId)
        {
            var snap = Session.OpenText(Ws.ParticipantId, scopeId, true, this);
            Key = snap.Key;
            Client = new TextClient(snap.Text, snap.Revision);
            Status = snap.Status;
        }

        /// <summary>Types: replaces the first occurrence of <paramref name="find"/>; sent now or buffered behind the one in flight.</summary>
        public void Edit(string find, string replace)
        {
            var at = Text.IndexOf(find, StringComparison.Ordinal);
            Assert.True(at >= 0, $"'{find}' not in the text");
            var op = new TextOperation().Retain(at).Delete(find.Length).Insert(replace).Retain(Text.Length - at - find.Length);
            if (Client.ApplyLocal(op) is { } send) Session.SubmitText(Key, Ws.ParticipantId, Client.Revision, send);
        }

        public void Deliver()
        {
            while (_inbox.Count > 0) _inbox.Dequeue()();
        }

        public bool Pending => _inbox.Count > 0;

        void ISharedTextClient.TextOperation(string key, TextOperation op) => _inbox.Enqueue(() => Client.ApplyServer(op));

        void ISharedTextClient.TextAck(string key) => _inbox.Enqueue(() =>
        {
            if (Client.ServerAck() is { } next) Session.SubmitText(Key, Ws.ParticipantId, Client.Revision, next);
        });

        void ISharedTextClient.TextCursors(string key, int revision, IReadOnlyList<TextCursor> cursors) => _inbox.Enqueue(() => Cursors = cursors);
        void ISharedTextClient.TextStatus(string key, SharedTextStatus status) => _inbox.Enqueue(() => Status = status);
        void ISharedTextClient.TextReset(string key, string text, int revision) => _inbox.Enqueue(() => Client = new TextClient(text, revision));
    }

    private static void DeliverAll(params Editor[] editors)
    {
        while (editors.Any(e => e.Pending))
            foreach (var e in editors) e.Deliver();
    }

    private string Canonical(ProjectSession session, string? scopeId)
    {
        var master = ProjectFile.Deserialize(session.Snapshot());
        var scope = scopeId == null ? master.Root : master.Find(scopeId)!;
        return SysMLv2Text.Export(master, new SysMLv2ExportOptions { Scope = scope, EmitIds = true }).Replace("\r\n", "\n");
    }

    [Fact]
    public async Task Undo_After_Apply_And_Reload_Updates_The_Shared_Text()
    {
        var annWs = new Workspace { User = _ann };
        Assert.True(await annWs.JoinAsync(_hub, _project, ProjectRole.Owner));
        var session = annWs.Session!;
        var structure = annWs.Model.Root.OwnedElements.Single(e => e.Name == "Structure");
        var ann = new Editor(annWs);
        ann.Open(structure.Id);

        // apply a new part definition through the text
        ann.Edit("part def BrakeSystem", "part def Turbocharger {\n    }\n    part def BrakeSystem");
        DeliverAll(ann);
        var result = SysMLv2Sync.Apply(annWs.Model, annWs.Model.Find(structure.Id)!, SysMLv2Parser.Parse(ann.Text));
        Assert.True(result.Applied);
        session.TextApplied(ann.Key, ann.Client.Revision, ann.Text, null);
        DeliverAll(ann);
        Assert.Contains("part def Turbocharger", ann.Text);

        // type something broken, then reload: the broken text is discarded
        ann.Edit("part def Turbocharger", "part def Turbocharger {{{");
        DeliverAll(ann);
        session.ResetText(ann.Key);
        DeliverAll(ann);
        Assert.Equal(Canonical(session, structure.Id), ann.Text);
        Assert.False(ann.Status!.Dirty);

        // undo the applied edit: the clean shared text follows the model
        annWs.Model.History.Undo();
        Assert.DoesNotContain(annWs.Model.OfKind(ElementKind.Block), b => b.Name == "Turbocharger");
        session.RefreshSharedTexts();
        DeliverAll(ann);
        Assert.DoesNotContain("Turbocharger", ann.Text);
        Assert.Equal(Canonical(session, structure.Id), ann.Text);
        annWs.LeaveSession();
    }

    [Fact]
    public async Task Two_Editors_Merge_Concurrent_Edits_Apply_Once_And_Follow_The_Model()
    {
        var annWs = new Workspace { User = _ann };
        var benWs = new Workspace { User = _ben };
        Assert.True(await annWs.JoinAsync(_hub, _project, ProjectRole.Owner));
        Assert.True(await benWs.JoinAsync(_hub, _project, ProjectRole.Editor));
        var session = annWs.Session!;
        Assert.Same(session, benWs.Session);
        var structure = annWs.Model.Root.OwnedElements.Single(e => e.Name == "Structure");
        var ann = new Editor(annWs);
        var ben = new Editor(benWs);
        ann.Open(structure.Id);
        ben.Open(structure.Id);
        Assert.Equal(ann.Key, ben.Key);
        Assert.Equal(ann.Text, ben.Text);
        Assert.Contains("part def BrakeSystem", ann.Text);

        // 1) concurrent edits in different places, several in flight, merge into the same text everywhere
        ann.Edit("part def BrakeSystem", "part def Brakes");
        ben.Edit("part def FuelTank", "part def Tank");
        ann.Edit("part def Brakes", "part def BrakeUnit");      // buffered behind Ann's first operation
        ben.Edit("attribute capacity", "attribute volume");
        DeliverAll(ann, ben);
        Assert.Equal(ann.Text, ben.Text);
        Assert.Equal(session.TextState(ann.Key)!.Value.Text, ann.Text);
        Assert.Contains("part def BrakeUnit", ann.Text);
        Assert.Contains("part def Tank", ann.Text);
        Assert.Contains("attribute volume", ann.Text);
        Assert.True(ann.Status!.Dirty);
        Assert.True(ben.Status!.Dirty);

        // 2) remote cursors carry names and colours
        session.UpdateTextCursor(ben.Key, benWs.ParticipantId, ben.Client.Revision, 10, 20);
        DeliverAll(ann, ben);
        var cursor = Assert.Single(ann.Cursors, c => c.ParticipantId == benWs.ParticipantId);
        Assert.Equal("Ben Ito", cursor.Name);
        Assert.Equal(_ben.Color, cursor.Color);
        Assert.Equal((10, 20), (cursor.Anchor, cursor.Head));

        // 3) Ann applies once: Ben's model follows through the replicated model operations, and the shared text becomes the
        //    canonical text for both of them
        var doc = SysMLv2Parser.Parse(ann.Text);
        Assert.False(doc.HasErrors, string.Join("\n", doc.Diagnostics));
        var result = SysMLv2Sync.Apply(annWs.Model, annWs.Model.Find(structure.Id)!, doc);
        Assert.True(result.Applied);
        Assert.Contains(benWs.Model.OfKind(ElementKind.Block), b => b.Name == "BrakeUnit");
        Assert.Contains(benWs.Model.OfKind(ElementKind.Property), p => p.Name == "volume");
        session.TextApplied(ann.Key, ann.Client.Revision, ann.Text, "Ann Lee applied 3 changes.");
        DeliverAll(ann, ben);
        Assert.Equal(Canonical(session, structure.Id), ann.Text);
        Assert.Equal(ann.Text, ben.Text);
        Assert.False(ben.Status!.Dirty);
        Assert.Equal("Ann Lee applied 3 changes.", ben.Status.Notice);

        // 4) a model change made elsewhere (a diagram) updates the clean shared text
        benWs.Model.Rename(benWs.Model.OfKind(ElementKind.Block).Single(b => b.Name == "Engine"), "Motor");
        session.RefreshSharedTexts();
        DeliverAll(ann, ben);
        Assert.Contains("part def Motor", ann.Text);
        Assert.Equal(Canonical(session, structure.Id), ben.Text);

        // 5) with unapplied edits in other lines, the model change is merged in and the edits stay
        ann.Edit("part def Tank", "part def FuelTank");
        DeliverAll(ann, ben);
        benWs.Model.Rename(benWs.Model.OfKind(ElementKind.Block).Single(b => b.Name == "Motor"), "Engine");
        session.RefreshSharedTexts();
        DeliverAll(ann, ben);
        Assert.Contains("part def Engine", ben.Text);
        Assert.Contains("part def FuelTank", ben.Text);
        Assert.Equal(ann.Text, ben.Text);
        Assert.True(ann.Status!.Dirty);
        Assert.False(ann.Status.Stale);

        // 6) when the model changes the very lines someone has unapplied edits in, editors get the "model changed" notice
        ben.Edit("part def Engine", "part def Powerplant");
        DeliverAll(ann, ben);
        annWs.Model.Rename(annWs.Model.OfKind(ElementKind.Block).Single(b => b.Name == "Engine"), "Motor");
        session.RefreshSharedTexts();
        DeliverAll(ann, ben);
        Assert.True(ann.Status!.Stale);
        Assert.True(ben.Status!.Stale);
        Assert.Contains("part def Powerplant", ann.Text);
        Assert.DoesNotContain("part def Motor", ann.Text);

        // 7) reloading discards the unapplied edits for everyone
        session.ResetText(ann.Key);
        DeliverAll(ann, ben);
        Assert.Equal(Canonical(session, structure.Id), ann.Text);
        Assert.Equal(ann.Text, ben.Text);
        Assert.False(ann.Status!.Dirty || ann.Status.Stale);

        session.CloseText(ann.Key, annWs.ParticipantId);
        session.CloseText(ben.Key, benWs.ParticipantId);
        Assert.Null(session.TextState(ann.Key));   // nothing unapplied: the shared text is dropped
        annWs.LeaveSession();
        benWs.LeaveSession();
    }

    [Fact]
    public async Task Panel_Sends_Edits_And_Applies_The_Shared_Text()
    {
        var annWs = new Workspace { User = _ann };
        var benWs = new Workspace { User = _ben };
        Assert.True(await annWs.JoinAsync(_hub, _project, ProjectRole.Owner));
        Assert.True(await benWs.JoinAsync(_hub, _project, ProjectRole.Editor));
        var ben = new Editor(benWs);
        ben.Open(null);

        using var ctx = new Bunit.TestContext();
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(annWs);
        ctx.JSInterop.Mode = Bunit.JSRuntimeMode.Loose;
        ctx.RenderComponent<MudBlazor.MudPopoverProvider>();
        var state = new Components.Workbench.SysmlTextPanel.EditorState();
        var cut = ctx.RenderComponent<Components.Workbench.SysmlTextPanel>(p => p.Add(x => x.State, state));
        Assert.NotNull(state.SharedDirty);   // collaborative mode
        Assert.Equal(ben.Text, state.Text);
        Assert.Contains("In sync", cut.Markup);

        // the browser turns typing into an operation; the other editor receives it
        const string from = "part def BrakeSystem";
        var at = state.Text.IndexOf(from, StringComparison.Ordinal);
        var op = new TextOperation().Retain(at).Delete(from.Length).Insert("part def Brakes").Retain(state.Text.Length - at - from.Length);
        await cut.InvokeAsync(() => cut.Instance.OnTextOp(0, System.Text.Json.JsonSerializer.SerializeToElement(op.ToWire())));
        ben.Deliver();
        Assert.Contains("part def Brakes", ben.Text);
        cut.WaitForAssertion(() => Assert.Contains("Modified", cut.Markup));
        cut.WaitForAssertion(() => Assert.Contains("BI", cut.Markup));   // Ben's presence chip

        // Apply once in Ann's tab: Ben's model follows and his shared text becomes canonical
        await cut.InvokeAsync(() => cut.Instance.OnEdit(ben.Text));
        cut.Find("button.sx-btn.primary").Click();
        Assert.Contains(benWs.Model.OfKind(ElementKind.Block), b => b.Name == "Brakes");
        ben.Deliver();
        Assert.False(ben.Status!.Dirty);
        Assert.StartsWith("Ann Lee applied", ben.Status.Notice);
        Assert.Equal(Canonical(annWs.Session!, null), ben.Text);
        cut.WaitForAssertion(() => Assert.Contains("In sync", cut.Markup));
        annWs.LeaveSession();
        benWs.LeaveSession();
    }

    [Fact]
    public async Task Model_Operations_Schedule_A_Refresh_Of_Shared_Texts()
    {
        var annWs = new Workspace { User = _ann };
        Assert.True(await annWs.JoinAsync(_hub, _project, ProjectRole.Owner));
        var ann = new Editor(annWs);
        ann.Open(null);
        ann.Deliver();
        annWs.Model.Rename(annWs.Model.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle"), "Car");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ann.Pending && DateTime.UtcNow < deadline) await Task.Delay(20);   // the session's debounced refresh
        ann.Deliver();
        Assert.Contains("part def Car", ann.Text);
        annWs.LeaveSession();
    }
}
