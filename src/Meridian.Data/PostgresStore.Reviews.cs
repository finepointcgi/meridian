using Npgsql;
using NpgsqlTypes;

namespace Meridian.Data;

/// <summary>Reviews, review comments and checklists, threaded replies, @mentions and notifications (PostgreSQL).</summary>
public sealed partial class PostgresStore
{
    private async Task MigrateReviewsAsync()
    {
        await using var cmd = _ds.CreateCommand("""
            create table if not exists reviews (
                id bigserial primary key,
                project_id uuid not null references projects(id) on delete cascade,
                title text not null,
                description text not null default '',
                author uuid references users(id) on delete set null,
                state smallint not null default 0,
                scope_kind smallint not null default 0,
                scope_ids jsonb not null default '[]',
                base_version bigint references project_versions(id) on delete set null,
                head_version bigint references project_versions(id) on delete set null,
                apply_status boolean not null default false,
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now());

            create table if not exists review_reviewers (
                review_id bigint not null references reviews(id) on delete cascade,
                user_id uuid not null references users(id) on delete cascade,
                vote smallint not null default 0,
                voted_at timestamptz,
                primary key (review_id, user_id));

            create table if not exists review_comments (
                id bigserial primary key,
                review_id bigint not null references reviews(id) on delete cascade,
                element_id text,
                parent_id bigint references review_comments(id) on delete cascade,
                author uuid references users(id) on delete set null,
                body text not null,
                created_at timestamptz not null default now());

            create table if not exists review_checks (
                review_id bigint not null references reviews(id) on delete cascade,
                element_id text not null,
                user_id uuid not null references users(id) on delete cascade,
                checked_at timestamptz not null default now(),
                primary key (review_id, element_id, user_id));

            create table if not exists comment_replies (
                comment_id bigint primary key references comments(id) on delete cascade,
                parent_id bigint not null references comments(id) on delete cascade);

            create table if not exists mentions (
                id bigserial primary key,
                project_id uuid not null references projects(id) on delete cascade,
                source text not null,
                source_id bigint not null,
                user_id uuid not null references users(id) on delete cascade,
                created_at timestamptz not null default now());

            create table if not exists notifications (
                id bigserial primary key,
                user_id uuid not null references users(id) on delete cascade,
                kind text not null,
                project_id uuid references projects(id) on delete cascade,
                element_id text,
                review_id bigint references reviews(id) on delete set null,
                actor uuid references users(id) on delete set null,
                body text not null,
                is_read boolean not null default false,
                created_at timestamptz not null default now());

            create index if not exists reviews_project on reviews(project_id);
            create index if not exists review_comments_review on review_comments(review_id);
            create index if not exists mentions_source on mentions(source, source_id);
            create index if not exists notifications_user on notifications(user_id, is_read, created_at);
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };

    private NpgsqlCommand Cmd(string sql, params NpgsqlParameter[] ps)
    {
        var cmd = _ds.CreateCommand(sql);
        foreach (var p in ps) cmd.Parameters.Add(p);
        return cmd;
    }

    private static UserAccount UserAt(NpgsqlDataReader r, int i) =>
        new(r.IsDBNull(i) ? Guid.Empty : r.GetGuid(i), r.GetString(i + 1), r.GetString(i + 2), r.GetString(i + 3));

    private const string UserCols = "u.id, coalesce(u.username, ''), coalesce(u.display_name, 'unknown'), coalesce(u.color, '#888')";

    // =============================================================== reviews

    public async Task<long> CreateReviewAsync(Guid projectId, ReviewDraft draft, Guid author)
    {
        ReviewRules.Check(draft);
        await using var conn = await _ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        long id;
        await using (var cmd = new NpgsqlCommand("""
            insert into reviews(project_id, title, description, author, scope_kind, scope_ids, base_version, head_version, apply_status)
            values ($1,$2,$3,$4,$5,$6,$7,$8,$9) returning id
            """, conn, tx))
        {
            cmd.Parameters.Add(P(projectId, NpgsqlDbType.Uuid));
            cmd.Parameters.Add(P(draft.Title.Trim(), NpgsqlDbType.Text));
            cmd.Parameters.Add(P(draft.Description.Trim(), NpgsqlDbType.Text));
            cmd.Parameters.Add(P(author, NpgsqlDbType.Uuid));
            cmd.Parameters.Add(P((short)draft.ScopeKind, NpgsqlDbType.Smallint));
            cmd.Parameters.Add(P(ReviewRules.EncodeIds(draft.ElementIds), NpgsqlDbType.Jsonb));
            cmd.Parameters.Add(P(draft.BaseVersionId, NpgsqlDbType.Bigint));
            cmd.Parameters.Add(P(draft.HeadVersionId, NpgsqlDbType.Bigint));
            cmd.Parameters.Add(P(draft.ApplyStatus, NpgsqlDbType.Boolean));
            id = (long)(await cmd.ExecuteScalarAsync())!;
        }
        foreach (var reviewer in draft.Reviewers.Distinct())
        {
            await using var cmd = new NpgsqlCommand("insert into review_reviewers(review_id, user_id) values ($1,$2)", conn, tx);
            cmd.Parameters.Add(P(id, NpgsqlDbType.Bigint));
            cmd.Parameters.Add(P(reviewer, NpgsqlDbType.Uuid));
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return id;
    }

    public Task<IReadOnlyList<ReviewInfo>> ListReviewsAsync(Guid projectId) => QueryReviewsAsync("r.project_id = $1", P(projectId, NpgsqlDbType.Uuid));

    public async Task<ReviewInfo?> GetReviewAsync(long reviewId) => (await QueryReviewsAsync("r.id = $1", P(reviewId, NpgsqlDbType.Bigint))).FirstOrDefault();

    private async Task<IReadOnlyList<ReviewInfo>> QueryReviewsAsync(string where, NpgsqlParameter key)
    {
        var reviewers = new Dictionary<long, List<ReviewerInfo>>();
        await using (var cmd = Cmd($"""
            select rr.review_id, {UserCols}, rr.vote, rr.voted_at
            from review_reviewers rr join reviews r on r.id = rr.review_id join users u on u.id = rr.user_id
            where {where} order by u.display_name
            """, key.Clone()))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                if (!reviewers.TryGetValue(r.GetInt64(0), out var list)) reviewers[r.GetInt64(0)] = list = new();
                list.Add(new ReviewerInfo(UserAt(r, 1), (ReviewVote)r.GetInt16(5), r.IsDBNull(6) ? null : r.GetDateTime(6)));
            }

        var result = new List<ReviewInfo>();
        await using (var cmd = Cmd($"""
            select r.id, r.project_id, r.title, r.description, {UserCols}, r.state, r.scope_kind, r.scope_ids::text, r.base_version, r.head_version,
                   r.apply_status, r.created_at, r.updated_at
            from reviews r left join users u on u.id = r.author where {where} order by r.updated_at desc
            """, key.Clone()))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                result.Add(new ReviewInfo(r.GetInt64(0), r.GetGuid(1), r.GetString(2), r.GetString(3), UserAt(r, 4), (ReviewState)r.GetInt16(8),
                    (ReviewScopeKind)r.GetInt16(9), ReviewRules.DecodeIds(r.GetString(10)), r.IsDBNull(11) ? null : r.GetInt64(11),
                    r.IsDBNull(12) ? null : r.GetInt64(12), r.GetBoolean(13), r.GetDateTime(14), r.GetDateTime(15),
                    reviewers.GetValueOrDefault(r.GetInt64(0)) ?? []));
        return result;
    }

    public async Task SetReviewStateAsync(long reviewId, ReviewState state)
    {
        await using var cmd = Cmd("update reviews set state = $2, updated_at = now() where id = $1", P(reviewId, NpgsqlDbType.Bigint), P((short)state, NpgsqlDbType.Smallint));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetReviewVoteAsync(long reviewId, Guid userId, ReviewVote vote)
    {
        await using (var cmd = Cmd("""
            insert into review_reviewers(review_id, user_id, vote, voted_at) values ($1,$2,$3, case when $3 = 0 then null else now() end)
            on conflict (review_id, user_id) do update set vote = excluded.vote, voted_at = excluded.voted_at
            """, P(reviewId, NpgsqlDbType.Bigint), P(userId, NpgsqlDbType.Uuid), P((short)vote, NpgsqlDbType.Smallint)))
            await cmd.ExecuteNonQueryAsync();
        await using (var cmd = Cmd("update reviews set updated_at = now() where id = $1", P(reviewId, NpgsqlDbType.Bigint)))
            await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteReviewAsync(long reviewId)
    {
        await using var cmd = Cmd("delete from reviews where id = $1", P(reviewId, NpgsqlDbType.Bigint));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<ReviewCommentInfo>> ListReviewCommentsAsync(long reviewId)
    {
        await using var cmd = Cmd($"""
            select c.id, c.review_id, c.element_id, c.parent_id, {UserCols}, c.body, c.created_at
            from review_comments c left join users u on u.id = c.author where c.review_id = $1 order by c.created_at, c.id
            """, P(reviewId, NpgsqlDbType.Bigint));
        var list = new List<ReviewCommentInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new ReviewCommentInfo(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3),
                UserAt(r, 4), r.GetString(8), r.GetDateTime(9)));
        return list;
    }

    public async Task<long> AddReviewCommentAsync(long reviewId, string? elementId, long? parentId, Guid author, string body)
    {
        long id;
        await using (var cmd = Cmd("insert into review_comments(review_id, element_id, parent_id, author, body) values ($1,$2,$3,$4,$5) returning id",
            P(reviewId, NpgsqlDbType.Bigint), P(elementId, NpgsqlDbType.Text), P(parentId, NpgsqlDbType.Bigint), P(author, NpgsqlDbType.Uuid), P(body, NpgsqlDbType.Text)))
            id = (long)(await cmd.ExecuteScalarAsync())!;
        await using (var cmd = Cmd("update reviews set updated_at = now() where id = $1", P(reviewId, NpgsqlDbType.Bigint)))
            await cmd.ExecuteNonQueryAsync();
        return id;
    }

    public async Task DeleteReviewCommentAsync(long commentId)
    {
        await using var cmd = Cmd("delete from review_comments where id = $1", P(commentId, NpgsqlDbType.Bigint));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<ReviewCheck>> ListReviewChecksAsync(long reviewId)
    {
        await using var cmd = Cmd("select element_id, user_id from review_checks where review_id = $1", P(reviewId, NpgsqlDbType.Bigint));
        var list = new List<ReviewCheck>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new ReviewCheck(r.GetString(0), r.GetGuid(1)));
        return list;
    }

    public async Task SetReviewCheckAsync(long reviewId, string elementId, Guid userId, bool done)
    {
        await using var cmd = Cmd(done
                ? "insert into review_checks(review_id, element_id, user_id) values ($1,$2,$3) on conflict do nothing"
                : "delete from review_checks where review_id = $1 and element_id = $2 and user_id = $3",
            P(reviewId, NpgsqlDbType.Bigint), P(elementId, NpgsqlDbType.Text), P(userId, NpgsqlDbType.Uuid));
        await cmd.ExecuteNonQueryAsync();
    }

    // =============================================================== replies & mentions

    public async Task SetCommentParentAsync(long commentId, long parentId)
    {
        await using var cmd = Cmd("insert into comment_replies(comment_id, parent_id) values ($1,$2) on conflict (comment_id) do update set parent_id = excluded.parent_id",
            P(commentId, NpgsqlDbType.Bigint), P(parentId, NpgsqlDbType.Bigint));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyDictionary<long, long>> ListCommentParentsAsync(Guid projectId)
    {
        await using var cmd = Cmd("select cr.comment_id, cr.parent_id from comment_replies cr join comments c on c.id = cr.comment_id where c.project_id = $1",
            P(projectId, NpgsqlDbType.Uuid));
        var map = new Dictionary<long, long>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) map[r.GetInt64(0)] = r.GetInt64(1);
        return map;
    }

    public async Task AddMentionsAsync(Guid projectId, string source, long sourceId, IReadOnlyCollection<Guid> userIds)
    {
        foreach (var u in userIds.Distinct())
        {
            await using var cmd = Cmd("insert into mentions(project_id, source, source_id, user_id) values ($1,$2,$3,$4)",
                P(projectId, NpgsqlDbType.Uuid), P(source, NpgsqlDbType.Text), P(sourceId, NpgsqlDbType.Bigint), P(u, NpgsqlDbType.Uuid));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task<IReadOnlyList<Guid>> ListMentionsAsync(string source, long sourceId)
    {
        await using var cmd = Cmd("select distinct user_id from mentions where source = $1 and source_id = $2", P(source, NpgsqlDbType.Text), P(sourceId, NpgsqlDbType.Bigint));
        var list = new List<Guid>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetGuid(0));
        return list;
    }

    // =============================================================== notifications

    public async Task<long> AddNotificationAsync(NotificationDraft n)
    {
        await using var cmd = Cmd("""
            insert into notifications(user_id, kind, project_id, element_id, review_id, actor, body) values ($1,$2,$3,$4,$5,$6,$7) returning id
            """, P(n.UserId, NpgsqlDbType.Uuid), P(n.Kind, NpgsqlDbType.Text), P(n.ProjectId, NpgsqlDbType.Uuid), P(n.ElementId, NpgsqlDbType.Text),
            P(n.ReviewId, NpgsqlDbType.Bigint), P(n.ActorId, NpgsqlDbType.Uuid), P(n.Text, NpgsqlDbType.Text));
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<IReadOnlyList<NotificationInfo>> ListNotificationsAsync(Guid userId, int limit = 50)
    {
        await using var cmd = Cmd($"""
            select n.id, n.user_id, n.kind, n.project_id, p.name, n.element_id, n.review_id, n.body, n.is_read, n.created_at, {UserCols}
            from notifications n left join projects p on p.id = n.project_id left join users u on u.id = n.actor
            where n.user_id = $1 order by n.created_at desc, n.id desc limit $2
            """, P(userId, NpgsqlDbType.Uuid), P(limit, NpgsqlDbType.Integer));
        var list = new List<NotificationInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new NotificationInfo(r.GetInt64(0), r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6), r.GetString(7), r.GetBoolean(8), r.GetDateTime(9),
                r.IsDBNull(10) ? null : UserAt(r, 10)));
        return list;
    }

    public async Task<int> UnreadNotificationCountAsync(Guid userId)
    {
        await using var cmd = Cmd("select count(*) from notifications where user_id = $1 and not is_read", P(userId, NpgsqlDbType.Uuid));
        return (int)(long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    public async Task MarkNotificationReadAsync(long notificationId, Guid userId)
    {
        await using var cmd = Cmd("update notifications set is_read = true where id = $1 and user_id = $2", P(notificationId, NpgsqlDbType.Bigint), P(userId, NpgsqlDbType.Uuid));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task MarkAllNotificationsReadAsync(Guid userId)
    {
        await using var cmd = Cmd("update notifications set is_read = true where user_id = $1 and not is_read", P(userId, NpgsqlDbType.Uuid));
        await cmd.ExecuteNonQueryAsync();
    }
}
