using Meridian.Core.CodeSync;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Serialization.CodeSync;
using Meridian.Web.CodeSync;
using Meridian.Web.CodeSync.Hosts;
using Meridian.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meridian.Web.Tests;

/// <summary>
/// The repository setting "Describe the code with AI" (docs/CODE-SYNC.md, "AI descriptions"): off by default and then
/// nothing is asked; on, the runner asks the configured provider (a fake here, never a real one) after a sync that changed
/// the model and the names land with the sync; with no provider set up the sync still works and says why it was skipped.
/// </summary>
public sealed class CodeSyncAiDescriptionsTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-ai-descriptions-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;
    private CollaborationHub _hub = default!;
    private UserAccount _ann = default!;
    private Guid _project;
    private int _commits;

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        _hub = new CollaborationHub(_db, NullLogger<CollaborationHub>.Instance);
        _ann = await _db.CreateUserAsync("ann", "Ann", "correct horse");
        _project = await _db.CreateProjectAsync("Shop", ProjectFile.Serialize(SampleModels.Vehicle()), _ann.Id);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    private sealed class FakeDescriber : ICodeDescriber
    {
        public int Calls { get; private set; }
        public string Name => "Fake model";

        public Task<CodeDescribeResult> DescribeAsync(IReadOnlyList<CodeDescribeItem> items, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new CodeDescribeResult(items.Where(i => i.Kind == "block").Select(i => new CodeDescription(i.Key, "Payment desk", "Takes payment for an order.")).ToList()));
        }
    }

    private sealed class Source(ICodeDescriber? describer) : ICodeDescriberSource
    {
        public ICodeDescriber? Create() => describer;
    }

    private async Task<Guid> ConnectAsync(bool ai)
    {
        var id = Guid.NewGuid();
        var config = new CodeRepoConfig { Owner = "acme", Repo = "shop", ConnectedBy = _ann.Id, AiDescriptions = ai };
        await _db.SaveIntegrationAsync(new IntegrationConnection(id, _project, CodeRepoKinds.CodeRepo, "acme/shop", config.ToJson(), null, DateTime.UtcNow));
        return id;
    }

    private Task<CodeSyncOutcome> SyncAsync(Guid connection, ICodeDescriberSource source)
    {
        var runner = new CodeSyncRunner(_db, _hub, [new CSharpCodeReader()], new CodeModelApplier(), new CodeSyncEvents(), NullLogger<CodeSyncRunner>.Instance, describers: source);
        var request = new CodeSyncRequest(_project, connection, "acme/shop", "main", $"{++_commits:D2}c0ffee{Guid.NewGuid():N}"[..40], "https://git.example/acme/shop/commit/abc",
            "Dana Dev", "Add checkout", [new CodeFile("src/Checkout.cs", "namespace Shop { public class Checkout { public void Pay() { } } }")], IsFullSnapshot: true) { TriggeredBy = _ann.Id };
        return runner.RunAsync(request);
    }

    private async Task<ModelRepository> MainAsync() => ProjectFile.Deserialize(await VersionControl.HeadJsonAsync(_db, _hub, _project, null));

    [Fact]
    public async Task Off_by_default_nothing_is_asked_and_names_come_from_the_code()
    {
        Assert.False(new CodeRepoConfig().AiDescriptions);
        var ai = new FakeDescriber();
        var outcome = await SyncAsync(await ConnectAsync(ai: false), new Source(ai));
        Assert.Equal(CodeSyncStatus.Accepted, outcome.Status);
        Assert.Equal(0, ai.Calls);
        Assert.DoesNotContain("AI", outcome.Summary);
        Assert.Contains((await MainAsync()).AllElements, e => e.Name == "Checkout" && e.Get(CodeFieldKeys.Symbol) == "Shop.Checkout");
    }

    [Fact]
    public async Task On_the_provider_names_what_the_sync_made_and_the_summary_says_so()
    {
        var ai = new FakeDescriber();
        var outcome = await SyncAsync(await ConnectAsync(ai: true), new Source(ai));
        Assert.Equal(1, ai.Calls);
        Assert.Contains("AI description", outcome.Summary);
        Assert.Contains("Fake model", outcome.Summary);
        var block = (await MainAsync()).AllElements.Single(e => e.Get(CodeFieldKeys.Symbol) == "Shop.Checkout");
        Assert.Equal(("Payment desk", "Takes payment for an order."), (block.Name, block.Documentation));
        Assert.Equal("Payment desk", block.Get(CodeModelMapper.LabelKey));
    }

    [Fact]
    public async Task On_without_a_provider_the_sync_still_works_and_says_why_nothing_was_described()
    {
        var outcome = await SyncAsync(await ConnectAsync(ai: true), new Source(null));
        Assert.Equal(CodeSyncStatus.Accepted, outcome.Status);
        Assert.Contains("AI descriptions skipped: no AI provider is set up", outcome.Summary);
        Assert.Contains((await MainAsync()).AllElements, e => e.Name == "Checkout");
    }
}
