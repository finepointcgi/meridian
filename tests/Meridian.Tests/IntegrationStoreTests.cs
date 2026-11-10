using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>Integration settings, webhook delivery log and personal access tokens in the SQLite store.</summary>
public sealed class IntegrationStoreTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-ix-{Guid.NewGuid():N}.db");
    private SqliteStore _store = default!;

    public async Task InitializeAsync()
    {
        _store = new SqliteStore(_path);
        await _store.MigrateAsync();
        await _store.MigrateAsync(); // idempotent
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Integrations_Are_Saved_Updated_Listed_And_Deleted_Per_Project()
    {
        var ann = await _store.CreateUserAsync("ann", "Ann", "correct horse");
        var p1 = await _store.CreateProjectAsync("One", ProjectFile.Serialize(SampleModels.Vehicle()), ann.Id);
        var p2 = await _store.CreateProjectAsync("Two", ProjectFile.Serialize(SampleModels.Vehicle()), ann.Id);

        var jira = new IntegrationConnection(Guid.NewGuid(), p1, IntegrationKinds.IssueTracker, "Jira", """{"type":"jira-cloud"}""", "CfDJ8-encrypted", DateTime.UtcNow);
        await _store.SaveIntegrationAsync(jira);
        await _store.SaveIntegrationAsync(new IntegrationConnection(Guid.NewGuid(), p2, IntegrationKinds.Webhook, "hook", """{"url":"https://x"}""", null, DateTime.UtcNow));

        var list = await _store.ListIntegrationsAsync(p1);
        Assert.Single(list);
        Assert.Equal("CfDJ8-encrypted", list[0].ProtectedSecret);

        await _store.SaveIntegrationAsync(jira with { Name = "Jira Cloud", ProtectedSecret = "other" });
        var found = await _store.FindIntegrationAsync(jira.Id);
        Assert.Equal("Jira Cloud", found!.Name);
        Assert.Equal("other", found.ProtectedSecret);
        Assert.Single(await _store.ListIntegrationsAsync(p1));

        await _store.DeleteIntegrationAsync(jira.Id);
        Assert.Empty(await _store.ListIntegrationsAsync(p1));
        Assert.Single(await _store.ListIntegrationsAsync(p2));
    }

    [Fact]
    public async Task Webhook_Deliveries_Are_Logged_Newest_First()
    {
        var ann = await _store.CreateUserAsync("ann", "Ann", "correct horse");
        var pid = await _store.CreateProjectAsync("One", ProjectFile.Serialize(SampleModels.Vehicle()), ann.Id);
        var hook = Guid.NewGuid();
        await _store.AddWebhookDeliveryAsync(new WebhookDelivery(0, pid, hook, "d1", "ping", 1, null, false, "Timed out", DateTime.UtcNow));
        await _store.AddWebhookDeliveryAsync(new WebhookDelivery(0, pid, hook, "d1", "ping", 2, 200, true, null, DateTime.UtcNow));
        var log = await _store.ListWebhookDeliveriesAsync(pid);
        Assert.Equal(2, log.Count);
        Assert.True(log[0].Success);
        Assert.Equal(200, log[0].StatusCode);
        Assert.Equal(2, log[0].Attempt);
        Assert.Null(log[1].StatusCode);
        Assert.Equal("Timed out", log[1].Error);
        Assert.Single(await _store.ListWebhookDeliveriesAsync(pid, 1));
    }

    [Fact]
    public async Task Api_Tokens_Are_Stored_Hashed_Resolve_To_Their_Owner_And_Can_Be_Revoked()
    {
        var ann = await _store.CreateUserAsync("ann", "Ann", "correct horse");
        var ben = await _store.CreateUserAsync("ben", "Ben", "battery staple");
        var token = ApiTokens.Generate();
        Assert.StartsWith("mrd_", token);
        Assert.True(token.Length > 40);

        var info = await _store.CreateApiTokenAsync(ann.Id, "CI", ApiTokens.Hash(token), ApiTokens.PrefixOf(token));
        Assert.Equal(ann.Id, (await _store.FindUserByApiTokenAsync(ApiTokens.Hash(token)))!.Id);
        Assert.Null(await _store.FindUserByApiTokenAsync(ApiTokens.Hash(token + "x")));

        var listed = Assert.Single(await _store.ListApiTokensAsync(ann.Id));
        Assert.Equal("CI", listed.Name);
        Assert.NotNull(listed.LastUsedAt);
        Assert.Equal(token[..12], listed.Prefix);   // only a short prefix is kept for display
        Assert.Empty(await _store.ListApiTokensAsync(ben.Id));

        await _store.RevokeApiTokenAsync(ben.Id, info.Id);   // not ben's token: no effect
        Assert.NotNull(await _store.FindUserByApiTokenAsync(ApiTokens.Hash(token)));
        await _store.RevokeApiTokenAsync(ann.Id, info.Id);
        Assert.Null(await _store.FindUserByApiTokenAsync(ApiTokens.Hash(token)));
        Assert.Empty(await _store.ListApiTokensAsync(ann.Id));
    }
}
