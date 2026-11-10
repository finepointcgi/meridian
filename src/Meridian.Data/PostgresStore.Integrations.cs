using Npgsql;

namespace Meridian.Data;

/// <summary>Integrations (issue trackers, webhooks, delivery log) and personal access tokens — PostgreSQL.</summary>
public sealed partial class PostgresStore
{
    // =============================================================== schema (integrations migration block)

    private static async Task MigrateIntegrationsAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand("""
            create table if not exists project_integrations (
                id uuid primary key,
                project_id uuid not null references projects(id) on delete cascade,
                kind text not null,
                name text not null,
                config jsonb not null,
                secret text,
                created_at timestamptz not null default now());

            create table if not exists webhook_deliveries (
                id bigserial primary key,
                project_id uuid not null references projects(id) on delete cascade,
                hook_id uuid not null,
                delivery_id text not null,
                event text not null,
                attempt int not null,
                status_code int,
                success boolean not null,
                error text,
                created_at timestamptz not null default now());

            create table if not exists api_tokens (
                id uuid primary key,
                user_id uuid not null references users(id) on delete cascade,
                name text not null,
                token_hash text not null unique,
                prefix text not null,
                created_at timestamptz not null default now(),
                last_used_at timestamptz,
                revoked boolean not null default false);

            create index if not exists integrations_project on project_integrations(project_id);
            create index if not exists deliveries_project on webhook_deliveries(project_id, id);
            create index if not exists api_tokens_user on api_tokens(user_id);
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private NpgsqlCommand IxCmd(string sql, params object?[] args)
    {
        var cmd = _ds.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.Add(new() { Value = a ?? DBNull.Value });
        return cmd;
    }

    private async Task IxExecAsync(string sql, params object?[] args)
    {
        await using var cmd = IxCmd(sql, args);
        await cmd.ExecuteNonQueryAsync();
    }

    // =============================================================== integrations

    private static IntegrationConnection ReadIntegration(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetDateTime(6));

    public async Task<IReadOnlyList<IntegrationConnection>> ListIntegrationsAsync(Guid projectId)
    {
        await using var cmd = IxCmd("select id, project_id, kind, name, config::text, secret, created_at from project_integrations where project_id = $1 order by created_at", projectId);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<IntegrationConnection>();
        while (await r.ReadAsync()) list.Add(ReadIntegration(r));
        return list;
    }

    public async Task<IntegrationConnection?> FindIntegrationAsync(Guid id)
    {
        await using var cmd = IxCmd("select id, project_id, kind, name, config::text, secret, created_at from project_integrations where id = $1", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadIntegration(r) : null;
    }

    public Task SaveIntegrationAsync(IntegrationConnection c) => IxExecAsync("""
        insert into project_integrations(id, project_id, kind, name, config, secret) values ($1,$2,$3,$4,$5::jsonb,$6)
        on conflict (id) do update set name = excluded.name, config = excluded.config, secret = excluded.secret
        """, c.Id, c.ProjectId, c.Kind, c.Name, c.ConfigJson, c.ProtectedSecret);

    public Task DeleteIntegrationAsync(Guid id) => IxExecAsync("delete from project_integrations where id = $1", id);

    public Task AddWebhookDeliveryAsync(WebhookDelivery d) => IxExecAsync("""
        insert into webhook_deliveries(project_id, hook_id, delivery_id, event, attempt, status_code, success, error)
        values ($1,$2,$3,$4,$5,$6,$7,$8)
        """, d.ProjectId, d.HookId, d.DeliveryId, d.Event, d.Attempt, d.StatusCode, d.Success, d.Error);

    public async Task<IReadOnlyList<WebhookDelivery>> ListWebhookDeliveriesAsync(Guid projectId, int limit = 50)
    {
        await using var cmd = IxCmd("""
            select id, project_id, hook_id, delivery_id, event, attempt, status_code, success, error, created_at
            from webhook_deliveries where project_id = $1 order by id desc limit $2
            """, projectId, limit);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<WebhookDelivery>();
        while (await r.ReadAsync())
            list.Add(new WebhookDelivery(r.GetInt64(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetInt32(5),
                r.IsDBNull(6) ? null : r.GetInt32(6), r.GetBoolean(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetDateTime(9)));
        return list;
    }

    // =============================================================== personal access tokens

    public async Task<ApiTokenInfo> CreateApiTokenAsync(Guid userId, string name, string tokenHash, string prefix)
    {
        var info = new ApiTokenInfo(Guid.NewGuid(), userId, string.IsNullOrWhiteSpace(name) ? "API token" : name.Trim(), prefix, DateTime.UtcNow, null);
        await IxExecAsync("insert into api_tokens(id, user_id, name, token_hash, prefix) values ($1,$2,$3,$4,$5)", info.Id, userId, info.Name, tokenHash, prefix);
        return info;
    }

    public async Task<IReadOnlyList<ApiTokenInfo>> ListApiTokensAsync(Guid userId)
    {
        await using var cmd = IxCmd("select id, user_id, name, prefix, created_at, last_used_at from api_tokens where user_id = $1 and not revoked order by created_at desc", userId);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<ApiTokenInfo>();
        while (await r.ReadAsync())
            list.Add(new ApiTokenInfo(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetDateTime(4), r.IsDBNull(5) ? null : r.GetDateTime(5)));
        return list;
    }

    public Task RevokeApiTokenAsync(Guid userId, Guid tokenId) =>
        IxExecAsync("update api_tokens set revoked = true where id = $1 and user_id = $2", tokenId, userId);

    public async Task<UserAccount?> FindUserByApiTokenAsync(string tokenHash)
    {
        await using var cmd = IxCmd("""
            with t as (update api_tokens set last_used_at = now() where token_hash = $1 and not revoked returning user_id)
            select u.id, u.username, u.display_name, u.color from t join users u on u.id = t.user_id
            """, tokenHash);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? new UserAccount(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3)) : null;
    }
}
