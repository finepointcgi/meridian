using System.Globalization;

namespace Meridian.Data;

/// <summary>Per-user preferences, profile edits and session revocation (SQLite).</summary>
public sealed partial class SqliteStore
{
    private Task MigratePreferencesAsync() => ExecAsync("""
        create table if not exists user_preferences (
            user_id text primary key references users(id) on delete cascade,
            prefs text not null default '{}',
            sessions_revoked_at text,
            updated_at text not null);
        """);

    public async Task<string?> GetUserPreferencesAsync(Guid userId) =>
        await ScalarAsync("select prefs from user_preferences where user_id = $1", S(userId)) as string;

    public Task SaveUserPreferencesAsync(Guid userId, string json) => ExecAsync("""
        insert into user_preferences(user_id, prefs, updated_at) values ($1, $2, $3)
        on conflict(user_id) do update set prefs = excluded.prefs, updated_at = excluded.updated_at
        """, S(userId), Preferences.CheckJson(json), Now());

    public async Task<UserAccount> UpdateUserProfileAsync(Guid userId, string displayName, string color)
    {
        var name = Preferences.NormalizeDisplayName(displayName);
        var c = Preferences.NormalizeColor(color);
        await using var cmd = await CmdAsync("update users set display_name = $2, color = $3 where id = $1 returning username", S(userId), name, c);
        var username = await cmd.ExecuteScalarAsync() as string ?? throw new InvalidOperationException("That account no longer exists.");
        return new UserAccount(userId, username, name, c);
    }

    public Task RevokeUserSessionsAsync(Guid userId) => ExecAsync("""
        insert into user_preferences(user_id, sessions_revoked_at, updated_at) values ($1, $2, $2)
        on conflict(user_id) do update set sessions_revoked_at = excluded.sessions_revoked_at
        """, S(userId), Now());

    public async Task<DateTime?> UserSessionsRevokedAtAsync(Guid userId) =>
        await ScalarAsync("select sessions_revoked_at from user_preferences where user_id = $1", S(userId)) is string s
            ? DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            : null;
}
