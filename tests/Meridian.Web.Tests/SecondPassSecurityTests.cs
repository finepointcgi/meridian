using Meridian.Assistant.Engine;
using Meridian.Core.Collaboration;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meridian.Web.Tests;

/// <summary>Regression tests for the second security pass (docs/SECURITY-REVIEW.md, "Second pass"), web side.</summary>
public class SecondPassSecurityTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-sr2-{Guid.NewGuid():N}.db");
    private SqliteStore _db = null!;
    private CollaborationHub _hub = null!;
    private Guid _project;
    private UserAccount _ann = null!, _vic = null!;

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        _ann = await _db.CreateUserAsync("ann", "Ann Lee", "correct horse");
        _vic = await _db.CreateUserAsync("vic", "Vic Viewer", "battery staple");
        _project = await _db.CreateProjectAsync("Vehicle", ProjectFile.Serialize(SampleModels.Vehicle()), _ann.Id);
        await _db.SetMemberAsync(_project, _vic.Id, ProjectRole.Viewer);
        _hub = new CollaborationHub(_db, NullLogger<CollaborationHub>.Instance);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    private sealed class Client : ISharedTextClient
    {
        public int Resets;
        public void TextOperation(string key, TextOperation op) { }
        public void TextAck(string key) { }
        public void TextCursors(string key, int revision, IReadOnlyList<TextCursor> cursors) { }
        public void TextStatus(string key, SharedTextStatus status) { }
        public void TextReset(string key, string text, int revision) => Resets++;
    }

    // ------------------------------------------------------------------ SR-28: viewers and the shared SysML text

    [Fact]
    public async Task SR28_Viewers_Cannot_Change_Or_Reset_The_Shared_Text()
    {
        var annWs = new Workspace { User = _ann };
        Assert.True(await annWs.JoinAsync(_hub, _project, ProjectRole.Owner));
        var vicWs = new Workspace { User = _vic };
        Assert.True(await vicWs.JoinAsync(_hub, _project, ProjectRole.Viewer));
        var session = annWs.Session!;
        Assert.Same(session, vicWs.Session);

        var annClient = new Client();
        var vicClient = new Client();
        var snap = session.OpenText(annWs.ParticipantId, null, false, annClient);
        session.OpenText(vicWs.ParticipantId, null, false, vicClient);

        // The viewer's operation is refused by the session (its panel already refuses it) and the viewer is resynchronised.
        session.SubmitText(snap.Key, vicWs.ParticipantId, snap.Revision, new TextOperation().Insert("// viewer was here\n").Retain(snap.Text.Length));
        Assert.Equal(snap.Text, session.TextState(snap.Key)!.Value.Text);
        Assert.Equal(1, vicClient.Resets);

        // An editor's unapplied edit can't be discarded by a viewer.
        session.SubmitText(snap.Key, annWs.ParticipantId, snap.Revision, new TextOperation().Insert("// draft\n").Retain(snap.Text.Length));
        Assert.True(session.TextState(snap.Key)!.Value.Status.Dirty);
        session.ResetText(snap.Key, vicWs.ParticipantId, "Vic reloaded");
        Assert.StartsWith("// draft", session.TextState(snap.Key)!.Value.Text);
        session.ResetText(snap.Key, annWs.ParticipantId, "Ann reloaded");
        Assert.Equal(snap.Text, session.TextState(snap.Key)!.Value.Text);

        // Malformed operations are refused without disturbing the text.
        session.SubmitText(snap.Key, annWs.ParticipantId, -5, new TextOperation().Retain(snap.Text.Length).Insert("x"));
        session.SubmitText(snap.Key, annWs.ParticipantId, int.MaxValue, new TextOperation().Retain(snap.Text.Length).Insert("x"));
        session.SubmitText(snap.Key, annWs.ParticipantId, session.TextState(snap.Key)!.Value.Revision, new TextOperation().Retain(snap.Text.Length + 100));
        Assert.Equal(snap.Text, session.TextState(snap.Key)!.Value.Text);
        Assert.Equal(3, annClient.Resets);
    }

    // ------------------------------------------------------------------ SR-29: FMU co-simulation on servers

    private static FmiCoSimSettings Fmi(bool local, params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value)).Build();
        return FmiCoSimSettings.From(config, new MeridianHostInfo(local, Path.Combine(Path.GetTempPath(), "meridian-sr29"), null));
    }

    [Fact]
    public void SR29_Running_Fmus_On_A_Server_Needs_An_Administrator()
    {
        var owner = new Workspace();   // owns the (local) project: every user owns the projects they create
        Assert.Equal(ProjectRole.Owner, owner.Role);

        var server = Fmi(local: false, ("Meridian:Fmi:Enabled", "true"));
        Assert.Contains("administrators", server.WhyNotAllowed(owner));
        owner.IsServerAdmin = true;
        Assert.Null(server.WhyNotAllowed(owner));
        owner.IsServerAdmin = false;

        // Opt-in restores the owner rule.
        var owners = Fmi(local: false, ("Meridian:Fmi:Enabled", "true"), ("Meridian:Fmi:AllowProjectOwners", "true"));
        Assert.Null(owners.WhyNotAllowed(owner));

        // Off by default on servers, on in the desktop app.
        Assert.Contains("turned off", Fmi(local: false).WhyNotAllowed(owner));
        Assert.Null(Fmi(local: true).WhyNotAllowed(owner));
    }

    // ------------------------------------------------------------------ SR-31: assistant limits

    [Fact]
    public async Task SR31_Assistant_Refuses_Oversized_Prompts_Without_Calling_The_Api()
    {
        var data = Path.Combine(Path.GetTempPath(), "meridian-sr31-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Meridian:AI:ApiKey"] = "sk-ant-test-0000000000000000-not-real" }).Build();
        var settings = new AssistantSettings(config, new MeridianHostInfo(false, data, null), new EphemeralDataProtectionProvider()) { ReadEnvironment = _ => null };
        var ws = new Workspace();
        ws.UseLocalModel(SampleModels.Vehicle());
        var calls = new CountingHandler();
        var assistant = new AssistantService(ws, settings, new AssistantConversations(), new AssistantClientFactory(new HttpClient(calls), maxRetries: 0));
        assistant.Acknowledge();

        await assistant.AskAsync(new string('x', AssistantService.MaxPromptChars + 1));
        Assert.Equal(0, calls.Count);
        Assert.Contains(assistant.Conversation.Items.OfType<NoticeItem>(), n => n.Text.Contains("too long"));

        // On a server the key can't be removed (or saved) from the browser.
        Assert.Throws<InvalidOperationException>(settings.RemoveKey);
        Assert.Throws<InvalidOperationException>(() => settings.SaveKey("sk-ant-test-0000000000000000-not-real"));
        Assert.True(settings.HasKey);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Count);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        }
    }
}
