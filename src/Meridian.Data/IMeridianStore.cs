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
