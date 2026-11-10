using Npgsql;
using NpgsqlTypes;

namespace Meridian.Data;

/// <summary>Per-user preferences, profile edits and session revocation (PostgreSQL).</summary>
public sealed partial class PostgresStore
{
    private static async Task MigratePreferencesAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand("""
            create table if not exists user_preferences (
                user_id uuid primary key references users(id) on delete cascade,
                prefs jsonb not null default '{}'::jsonb,
                sessions_revoked_at timestamptz,
                updated_at timestamptz not null default now());
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<string?> GetUserPreferencesAsync(Guid userId)
    {
        await using var cmd = Command("select prefs::text from user_preferences where user_id = $1", userId);
        return await cmd.ExecuteScalarAsync() as string;
    }

    public async Task SaveUserPreferencesAsync(Guid userId, string json)
    {
        await using var cmd = Command("""
            insert into user_preferences(user_id, prefs, updated_at) values ($1, $2, now())
            on conflict (user_id) do update set prefs = excluded.prefs, updated_at = now()
            """, userId, Typed(Preferences.CheckJson(json), NpgsqlDbType.Jsonb));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<UserAccount> UpdateUserProfileAsync(Guid userId, string displayName, string color)
    {
        var name = Preferences.NormalizeDisplayName(displayName);
        var c = Preferences.NormalizeColor(color);
        await using var cmd = Command("update users set display_name = $2, color = $3 where id = $1 returning username", userId, name, c);
        var username = await cmd.ExecuteScalarAsync() as string ?? throw new InvalidOperationException("That account no longer exists.");
        return new UserAccount(userId, username, name, c);
    }

    public async Task RevokeUserSessionsAsync(Guid userId)
    {
        await using var cmd = Command("""
            insert into user_preferences(user_id, sessions_revoked_at) values ($1, now())
            on conflict (user_id) do update set sessions_revoked_at = now()
            """, userId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<DateTime?> UserSessionsRevokedAtAsync(Guid userId)
    {
        await using var cmd = Command("select sessions_revoked_at from user_preferences where user_id = $1", userId);
        return await cmd.ExecuteScalarAsync() is DateTime t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;
    }
}
