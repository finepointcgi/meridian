using System.Security.Cryptography;

namespace Meridian.Data;

public sealed record UserAccount(Guid Id, string Username, string DisplayName, string Color);

public enum ProjectRole { Viewer = 0, Editor = 1, Owner = 2 }

public sealed record ProjectSummary(Guid Id, string Name, ProjectRole Role, DateTime UpdatedAt, long Revision, string? UpdatedBy);

public sealed record ProjectMember(UserAccount User, ProjectRole Role);

public sealed record VersionInfo(long Id, int Number, string Message, string Author, DateTime CreatedAt);

public sealed record CommentInfo(long Id, string ElementId, UserAccount Author, string Body, bool Resolved, DateTime CreatedAt);

/// <summary>
/// System of record for accounts, projects, version history and comments. PostgreSQL backs team servers;
/// SQLite backs the desktop app's local (single-user, offline) mode. Both store models as JSON documents.
/// </summary>
public interface IMeridianStore
{
    Task MigrateAsync();

    Task<UserAccount> CreateUserAsync(string username, string displayName, string password);
    Task<UserAccount?> VerifyUserAsync(string username, string password);
    Task<UserAccount?> FindUserAsync(string username);

    Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(Guid userId);
    Task<Guid> CreateProjectAsync(string name, string modelJson, Guid ownerId);
    Task<(string Name, string ModelJson, long Revision)?> LoadProjectAsync(Guid projectId);
    Task<long> SaveProjectAsync(Guid projectId, string modelJson, Guid? userId);
    Task RenameProjectAsync(Guid projectId, string name);
    Task DeleteProjectAsync(Guid projectId);
    Task<ProjectRole?> RoleOfAsync(Guid projectId, Guid userId);
    Task<IReadOnlyList<ProjectMember>> MembersAsync(Guid projectId);
    Task SetMemberAsync(Guid projectId, Guid userId, ProjectRole role);
    Task RemoveMemberAsync(Guid projectId, Guid userId);

    Task<int> CommitVersionAsync(Guid projectId, string message, string modelJson, Guid? author);
    Task<IReadOnlyList<VersionInfo>> ListVersionsAsync(Guid projectId);
    Task<string?> LoadVersionAsync(Guid projectId, long versionId);

    Task<IReadOnlyList<CommentInfo>> ListCommentsAsync(Guid projectId);
    Task<long> AddCommentAsync(Guid projectId, string elementId, Guid author, string body);
    Task SetCommentResolvedAsync(long commentId, bool resolved);
    Task DeleteCommentAsync(long commentId);

    // ---------------------------------------------------------------- reviews, threaded replies, @mentions & notifications (Reviews.cs)

    Task<long> CreateReviewAsync(Guid projectId, ReviewDraft draft, Guid author);
    Task<IReadOnlyList<ReviewInfo>> ListReviewsAsync(Guid projectId);
    Task<ReviewInfo?> GetReviewAsync(long reviewId);
    Task SetReviewStateAsync(long reviewId, ReviewState state);
    Task SetReviewVoteAsync(long reviewId, Guid userId, ReviewVote vote);
    Task DeleteReviewAsync(long reviewId);
    Task<IReadOnlyList<ReviewCommentInfo>> ListReviewCommentsAsync(long reviewId);
    Task<long> AddReviewCommentAsync(long reviewId, string? elementId, long? parentId, Guid author, string body);
    Task DeleteReviewCommentAsync(long commentId);
    Task<IReadOnlyList<ReviewCheck>> ListReviewChecksAsync(long reviewId);
    Task SetReviewCheckAsync(long reviewId, string elementId, Guid userId, bool done);

    /// <summary>Element comment threading: <paramref name="commentId"/> is a reply to <paramref name="parentId"/>.</summary>
    Task SetCommentParentAsync(long commentId, long parentId);
    Task<IReadOnlyDictionary<long, long>> ListCommentParentsAsync(Guid projectId);
    /// <summary>Records who a comment mentions; <paramref name="source"/> is "comment" or "review_comment".</summary>
    Task AddMentionsAsync(Guid projectId, string source, long sourceId, IReadOnlyCollection<Guid> userIds);
    Task<IReadOnlyList<Guid>> ListMentionsAsync(string source, long sourceId);

    Task<long> AddNotificationAsync(NotificationDraft notification);
    Task<IReadOnlyList<NotificationInfo>> ListNotificationsAsync(Guid userId, int limit = 50);
    Task<int> UnreadNotificationCountAsync(Guid userId);
    Task MarkNotificationReadAsync(long notificationId, Guid userId);
    Task MarkAllNotificationsReadAsync(Guid userId);
    // ---------------------------------------------------------------- integrations (issue trackers, webhooks) and API tokens

    Task<IReadOnlyList<IntegrationConnection>> ListIntegrationsAsync(Guid projectId);
    Task<IntegrationConnection?> FindIntegrationAsync(Guid id);
    /// <summary>Inserts or updates by id. The secret must already be encrypted by the caller.</summary>
    Task SaveIntegrationAsync(IntegrationConnection connection);
    Task DeleteIntegrationAsync(Guid id);
    Task AddWebhookDeliveryAsync(WebhookDelivery delivery);
    Task<IReadOnlyList<WebhookDelivery>> ListWebhookDeliveriesAsync(Guid projectId, int limit = 50);
    Task<ApiTokenInfo> CreateApiTokenAsync(Guid userId, string name, string tokenHash, string prefix);
    Task<IReadOnlyList<ApiTokenInfo>> ListApiTokensAsync(Guid userId);
    Task RevokeApiTokenAsync(Guid userId, Guid tokenId);
    /// <summary>The owner of an unrevoked token with this hash (and records its use), or null.</summary>
    Task<UserAccount?> FindUserByApiTokenAsync(string tokenHash);
}

/// <summary>Account rules and password hashing shared by every store.</summary>
public static class Accounts
{
    public static readonly string[] Palette = ["#4f8cff", "#2dd4bf", "#f0b429", "#ff6b6b", "#a283ff", "#3fbf7f", "#ff8fab", "#f97316", "#38bdf8"];

    public static string NormalizeUsername(string username)
    {
        username = username.Trim().ToLowerInvariant();
        if (username.Length is < 2 or > 40 || !username.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new InvalidOperationException("Username must be 2–40 letters, digits, '.', '_' or '-'.");
        return username;
    }

    public static void CheckPassword(string password)
    {
        if (password.Length < 8) throw new InvalidOperationException("Password must be at least 8 characters.");
    }

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256$210000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
