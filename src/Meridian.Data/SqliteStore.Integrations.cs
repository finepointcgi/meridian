namespace Meridian.Data;

/// <summary>Integrations (issue trackers, webhooks, delivery log) and personal access tokens — SQLite.</summary>
public sealed partial class SqliteStore
{
    // =============================================================== schema (integrations migration block)

    private Task MigrateIntegrationsAsync() => ExecAsync("""
        create table if not exists project_integrations (
            id text primary key,
            project_id text not null references projects(id) on delete cascade,
            kind text not null,
            name text not null,
            config text not null,
            secret text,
            created_at text not null);

        create table if not exists webhook_deliveries (
            id integer primary key autoincrement,
            project_id text not null references projects(id) on delete cascade,
            hook_id text not null,
            delivery_id text not null,
            event text not null,
            attempt integer not null,
            status_code integer,
            success integer not null,
            error text,
            created_at text not null);

        create table if not exists api_tokens (
            id text primary key,
            user_id text not null references users(id) on delete cascade,
            name text not null,
            token_hash text not null unique,
            prefix text not null,
            created_at text not null,
            last_used_at text,
            revoked integer not null default 0);

        create index if not exists integrations_project on project_integrations(project_id);
        create index if not exists deliveries_project on webhook_deliveries(project_id, id);
        create index if not exists api_tokens_user on api_tokens(user_id);
        """);

    // =============================================================== integrations

    private static IntegrationConnection ReadIntegration(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(Id(r, 0), Id(r, 1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), Time(r, 6));

    public async Task<IReadOnlyList<IntegrationConnection>> ListIntegrationsAsync(Guid projectId)
    {
        await using var cmd = await CmdAsync("select id, project_id, kind, name, config, secret, created_at from project_integrations where project_id = $1 order by created_at", S(projectId));
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<IntegrationConnection>();
        while (await r.ReadAsync()) list.Add(ReadIntegration(r));
        return list;
    }

    public async Task<IntegrationConnection?> FindIntegrationAsync(Guid id)
    {
        await using var cmd = await CmdAsync("select id, project_id, kind, name, config, secret, created_at from project_integrations where id = $1", S(id));
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadIntegration(r) : null;
    }

    public Task SaveIntegrationAsync(IntegrationConnection c) => ExecAsync("""
        insert into project_integrations(id, project_id, kind, name, config, secret, created_at) values ($1,$2,$3,$4,$5,$6,$7)
        on conflict (id) do update set name = excluded.name, config = excluded.config, secret = excluded.secret
        """, S(c.Id), S(c.ProjectId), c.Kind, c.Name, c.ConfigJson, c.ProtectedSecret, Now());

    public Task DeleteIntegrationAsync(Guid id) => ExecAsync("delete from project_integrations where id = $1", S(id));

    public Task AddWebhookDeliveryAsync(WebhookDelivery d) => ExecAsync("""
        insert into webhook_deliveries(project_id, hook_id, delivery_id, event, attempt, status_code, success, error, created_at)
        values ($1,$2,$3,$4,$5,$6,$7,$8,$9)
        """, S(d.ProjectId), S(d.HookId), d.DeliveryId, d.Event, d.Attempt, d.StatusCode, d.Success ? 1 : 0, d.Error, Now());

    public async Task<IReadOnlyList<WebhookDelivery>> ListWebhookDeliveriesAsync(Guid projectId, int limit = 50)
    {
        await using var cmd = await CmdAsync("""
            select id, project_id, hook_id, delivery_id, event, attempt, status_code, success, error, created_at
            from webhook_deliveries where project_id = $1 order by id desc limit $2
            """, S(projectId), limit);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<WebhookDelivery>();
        while (await r.ReadAsync())
            list.Add(new WebhookDelivery(r.GetInt64(0), Id(r, 1), Id(r, 2), r.GetString(3), r.GetString(4), r.GetInt32(5),
                r.IsDBNull(6) ? null : r.GetInt32(6), r.GetInt64(7) != 0, r.IsDBNull(8) ? null : r.GetString(8), Time(r, 9)));
        return list;
    }

    // =============================================================== personal access tokens

    public async Task<ApiTokenInfo> CreateApiTokenAsync(Guid userId, string name, string tokenHash, string prefix)
    {
        var info = new ApiTokenInfo(Guid.NewGuid(), userId, string.IsNullOrWhiteSpace(name) ? "API token" : name.Trim(), prefix, DateTime.UtcNow, null);
        await ExecAsync("insert into api_tokens(id, user_id, name, token_hash, prefix, created_at) values ($1,$2,$3,$4,$5,$6)",
            S(info.Id), S(userId), info.Name, tokenHash, prefix, Now());
        return info;
    }

    public async Task<IReadOnlyList<ApiTokenInfo>> ListApiTokensAsync(Guid userId)
    {
        await using var cmd = await CmdAsync("select id, user_id, name, prefix, created_at, last_used_at from api_tokens where user_id = $1 and revoked = 0 order by created_at desc", S(userId));
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<ApiTokenInfo>();
        while (await r.ReadAsync())
            list.Add(new ApiTokenInfo(Id(r, 0), Id(r, 1), r.GetString(2), r.GetString(3), Time(r, 4), r.IsDBNull(5) ? null : Time(r, 5)));
        return list;
    }

    public Task RevokeApiTokenAsync(Guid userId, Guid tokenId) =>
        ExecAsync("update api_tokens set revoked = 1 where id = $1 and user_id = $2", S(tokenId), S(userId));

    public async Task<UserAccount?> FindUserByApiTokenAsync(string tokenHash)
    {
        UserAccount? user = null;
        string? tokenId = null;
        await using (var cmd = await CmdAsync("""
            select u.id, u.username, u.display_name, u.color, t.id from api_tokens t join users u on u.id = t.user_id
            where t.token_hash = $1 and t.revoked = 0
            """, tokenHash))
        await using (var r = await cmd.ExecuteReaderAsync())
            if (await r.ReadAsync()) { user = new UserAccount(Id(r, 0), r.GetString(1), r.GetString(2), r.GetString(3)); tokenId = r.GetString(4); }
        if (tokenId != null) await ExecAsync("update api_tokens set last_used_at = $2 where id = $1", tokenId, Now());
        return user;
    }
}
