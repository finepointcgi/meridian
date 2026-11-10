using Microsoft.Data.Sqlite;

namespace Meridian.Data;

/// <summary>Reviews, review comments and checklists, threaded replies, @mentions and notifications (SQLite).</summary>
public sealed partial class SqliteStore
{
    private async Task MigrateReviewsAsync()
    {
        await ExecAsync("""
            create table if not exists reviews (
                id integer primary key autoincrement,
                project_id text not null references projects(id) on delete cascade,
                title text not null,
                description text not null default '',
                author text references users(id) on delete set null,
                state integer not null default 0,
                scope_kind integer not null default 0,
                scope_ids text not null default '[]',
                base_version integer references project_versions(id) on delete set null,
                head_version integer references project_versions(id) on delete set null,
                apply_status integer not null default 0,
                created_at text not null,
                updated_at text not null);

            create table if not exists review_reviewers (
                review_id integer not null references reviews(id) on delete cascade,
                user_id text not null references users(id) on delete cascade,
                vote integer not null default 0,
                voted_at text,
                primary key (review_id, user_id));

            create table if not exists review_comments (
                id integer primary key autoincrement,
                review_id integer not null references reviews(id) on delete cascade,
                element_id text,
                parent_id integer references review_comments(id) on delete cascade,
                author text references users(id) on delete set null,
                body text not null,
                created_at text not null);

            create table if not exists review_checks (
                review_id integer not null references reviews(id) on delete cascade,
                element_id text not null,
                user_id text not null references users(id) on delete cascade,
                checked_at text not null,
                primary key (review_id, element_id, user_id));

            create table if not exists comment_replies (
                comment_id integer primary key references comments(id) on delete cascade,
                parent_id integer not null references comments(id) on delete cascade);

            create table if not exists mentions (
                id integer primary key autoincrement,
                project_id text not null references projects(id) on delete cascade,
                source text not null,
                source_id integer not null,
                user_id text not null references users(id) on delete cascade,
                created_at text not null);

            create table if not exists notifications (
                id integer primary key autoincrement,
                user_id text not null references users(id) on delete cascade,
                kind text not null,
                project_id text references projects(id) on delete cascade,
                element_id text,
                review_id integer references reviews(id) on delete set null,
                actor text references users(id) on delete set null,
                body text not null,
                is_read integer not null default 0,
                created_at text not null);

            create index if not exists reviews_project on reviews(project_id);
            create index if not exists review_comments_review on review_comments(review_id);
            create index if not exists mentions_source on mentions(source, source_id);
            create index if not exists notifications_user on notifications(user_id, is_read, created_at);
            """);
    }

    private static UserAccount UserAt(SqliteDataReader r, int i) =>
        new(r.IsDBNull(i) ? Guid.Empty : Id(r, i), r.GetString(i + 1), r.GetString(i + 2), r.GetString(i + 3));

    private const string UserCols = "u.id, coalesce(u.username, ''), coalesce(u.display_name, 'unknown'), coalesce(u.color, '#888')";

    // =============================================================== reviews

    public async Task<long> CreateReviewAsync(Guid projectId, ReviewDraft draft, Guid author)
    {
        ReviewRules.Check(draft);
        await using var c = await OpenAsync();
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync();
        var now = Now();
        long id;
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                insert into reviews(project_id, title, description, author, scope_kind, scope_ids, base_version, head_version, apply_status, created_at, updated_at)
                values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$10) returning id
                """;
            cmd.Parameters.AddWithValue("$1", S(projectId));
            cmd.Parameters.AddWithValue("$2", draft.Title.Trim());
            cmd.Parameters.AddWithValue("$3", draft.Description.Trim());
            cmd.Parameters.AddWithValue("$4", S(author));
            cmd.Parameters.AddWithValue("$5", (int)draft.ScopeKind);
            cmd.Parameters.AddWithValue("$6", ReviewRules.EncodeIds(draft.ElementIds));
            cmd.Parameters.AddWithValue("$7", (object?)draft.BaseVersionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$8", (object?)draft.HeadVersionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$9", draft.ApplyStatus ? 1 : 0);
            cmd.Parameters.AddWithValue("$10", now);
            id = (long)(await cmd.ExecuteScalarAsync())!;
        }
        foreach (var reviewer in draft.Reviewers.Distinct())
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "insert into review_reviewers(review_id, user_id) values ($1,$2)";
            cmd.Parameters.AddWithValue("$1", id);
            cmd.Parameters.AddWithValue("$2", S(reviewer));
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return id;
    }

    public async Task<IReadOnlyList<ReviewInfo>> ListReviewsAsync(Guid projectId) => await QueryReviewsAsync("r.project_id = $1", S(projectId));

    public async Task<ReviewInfo?> GetReviewAsync(long reviewId) => (await QueryReviewsAsync("r.id = $1", reviewId)).FirstOrDefault();

    private async Task<IReadOnlyList<ReviewInfo>> QueryReviewsAsync(string where, object key)
    {
        var reviewers = new Dictionary<long, List<ReviewerInfo>>();
        await using (var cmd = await CmdAsync($"""
            select rr.review_id, {UserCols}, rr.vote, rr.voted_at
            from review_reviewers rr join reviews r on r.id = rr.review_id join users u on u.id = rr.user_id
            where {where} order by u.display_name
            """, key))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                if (!reviewers.TryGetValue(r.GetInt64(0), out var list)) reviewers[r.GetInt64(0)] = list = new();
                list.Add(new ReviewerInfo(UserAt(r, 1), (ReviewVote)r.GetInt32(5), r.IsDBNull(6) ? null : Time(r, 6)));
            }

        var result = new List<ReviewInfo>();
        await using (var cmd = await CmdAsync($"""
            select r.id, r.project_id, r.title, r.description, {UserCols}, r.state, r.scope_kind, r.scope_ids, r.base_version, r.head_version,
                   r.apply_status, r.created_at, r.updated_at
            from reviews r left join users u on u.id = r.author where {where} order by r.updated_at desc
            """, key))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                result.Add(new ReviewInfo(r.GetInt64(0), Id(r, 1), r.GetString(2), r.GetString(3), UserAt(r, 4), (ReviewState)r.GetInt32(8),
                    (ReviewScopeKind)r.GetInt32(9), ReviewRules.DecodeIds(r.GetString(10)), r.IsDBNull(11) ? null : r.GetInt64(11),
                    r.IsDBNull(12) ? null : r.GetInt64(12), r.GetInt64(13) != 0, Time(r, 14), Time(r, 15),
                    reviewers.GetValueOrDefault(r.GetInt64(0)) ?? []));
        return result;
    }

    public Task SetReviewStateAsync(long reviewId, ReviewState state) =>
        ExecAsync("update reviews set state = $2, updated_at = $3 where id = $1", reviewId, (int)state, Now());

    public async Task SetReviewVoteAsync(long reviewId, Guid userId, ReviewVote vote)
    {
        var now = Now();
        await ExecAsync("""
            insert into review_reviewers(review_id, user_id, vote, voted_at) values ($1,$2,$3,$4)
            on conflict (review_id, user_id) do update set vote = excluded.vote, voted_at = excluded.voted_at
            """, reviewId, S(userId), (int)vote, vote == ReviewVote.Pending ? null : now);
        await ExecAsync("update reviews set updated_at = $2 where id = $1", reviewId, now);
    }

    public Task DeleteReviewAsync(long reviewId) => ExecAsync("delete from reviews where id = $1", reviewId);

    public async Task<IReadOnlyList<ReviewCommentInfo>> ListReviewCommentsAsync(long reviewId)
    {
        await using var cmd = await CmdAsync($"""
            select c.id, c.review_id, c.element_id, c.parent_id, {UserCols}, c.body, c.created_at
            from review_comments c left join users u on u.id = c.author where c.review_id = $1 order by c.created_at, c.id
            """, reviewId);
        var list = new List<ReviewCommentInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new ReviewCommentInfo(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3),
                UserAt(r, 4), r.GetString(8), Time(r, 9)));
        return list;
    }

    public async Task<long> AddReviewCommentAsync(long reviewId, string? elementId, long? parentId, Guid author, string body)
    {
        var now = Now();
        var id = (long)(await ScalarAsync("insert into review_comments(review_id, element_id, parent_id, author, body, created_at) values ($1,$2,$3,$4,$5,$6) returning id",
            reviewId, elementId, parentId, S(author), body, now) ?? 0L);
        await ExecAsync("update reviews set updated_at = $2 where id = $1", reviewId, now);
        return id;
    }

    public Task DeleteReviewCommentAsync(long commentId) => ExecAsync("delete from review_comments where id = $1", commentId);

    public async Task<IReadOnlyList<ReviewCheck>> ListReviewChecksAsync(long reviewId)
    {
        await using var cmd = await CmdAsync("select element_id, user_id from review_checks where review_id = $1", reviewId);
        var list = new List<ReviewCheck>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new ReviewCheck(r.GetString(0), Id(r, 1)));
        return list;
    }

    public Task SetReviewCheckAsync(long reviewId, string elementId, Guid userId, bool done) => done
        ? ExecAsync("insert into review_checks(review_id, element_id, user_id, checked_at) values ($1,$2,$3,$4) on conflict do nothing", reviewId, elementId, S(userId), Now())
        : ExecAsync("delete from review_checks where review_id = $1 and element_id = $2 and user_id = $3", reviewId, elementId, S(userId));

    // =============================================================== replies & mentions

    public Task SetCommentParentAsync(long commentId, long parentId) =>
        ExecAsync("insert into comment_replies(comment_id, parent_id) values ($1,$2) on conflict (comment_id) do update set parent_id = excluded.parent_id", commentId, parentId);

    public async Task<IReadOnlyDictionary<long, long>> ListCommentParentsAsync(Guid projectId)
    {
        await using var cmd = await CmdAsync("select cr.comment_id, cr.parent_id from comment_replies cr join comments c on c.id = cr.comment_id where c.project_id = $1", S(projectId));
        var map = new Dictionary<long, long>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) map[r.GetInt64(0)] = r.GetInt64(1);
        return map;
    }

    public async Task AddMentionsAsync(Guid projectId, string source, long sourceId, IReadOnlyCollection<Guid> userIds)
    {
        foreach (var u in userIds.Distinct())
            await ExecAsync("insert into mentions(project_id, source, source_id, user_id, created_at) values ($1,$2,$3,$4,$5)", S(projectId), source, sourceId, S(u), Now());
    }

    public async Task<IReadOnlyList<Guid>> ListMentionsAsync(string source, long sourceId)
    {
        await using var cmd = await CmdAsync("select distinct user_id from mentions where source = $1 and source_id = $2", source, sourceId);
        var list = new List<Guid>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(Id(r, 0));
        return list;
    }

    // =============================================================== notifications

    public async Task<long> AddNotificationAsync(NotificationDraft n) =>
        (long)(await ScalarAsync("""
            insert into notifications(user_id, kind, project_id, element_id, review_id, actor, body, created_at)
            values ($1,$2,$3,$4,$5,$6,$7,$8) returning id
            """, S(n.UserId), n.Kind, n.ProjectId is { } p ? S(p) : null, n.ElementId, n.ReviewId, n.ActorId is { } a ? S(a) : null, n.Text, Now()) ?? 0L);

    public async Task<IReadOnlyList<NotificationInfo>> ListNotificationsAsync(Guid userId, int limit = 50)
    {
        await using var cmd = await CmdAsync($"""
            select n.id, n.user_id, n.kind, n.project_id, p.name, n.element_id, n.review_id, n.body, n.is_read, n.created_at, {UserCols}
            from notifications n left join projects p on p.id = n.project_id left join users u on u.id = n.actor
            where n.user_id = $1 order by n.created_at desc, n.id desc limit $2
            """, S(userId), limit);
        var list = new List<NotificationInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new NotificationInfo(r.GetInt64(0), Id(r, 1), r.GetString(2), r.IsDBNull(3) ? null : Id(r, 3), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6), r.GetString(7), r.GetInt64(8) != 0, Time(r, 9),
                r.IsDBNull(10) ? null : UserAt(r, 10)));
        return list;
    }

    public async Task<int> UnreadNotificationCountAsync(Guid userId) =>
        (int)(long)(await ScalarAsync("select count(*) from notifications where user_id = $1 and is_read = 0", S(userId)) ?? 0L);

    public Task MarkNotificationReadAsync(long notificationId, Guid userId) =>
        ExecAsync("update notifications set is_read = 1 where id = $1 and user_id = $2", notificationId, S(userId));

    public Task MarkAllNotificationsReadAsync(Guid userId) =>
        ExecAsync("update notifications set is_read = 1 where user_id = $1 and is_read = 0", S(userId));
}
