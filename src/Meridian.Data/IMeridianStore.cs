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
    // ------------------------------------------------------------ enterprise administration (see AdminModels.cs)
    // Server roles, account state and lockout, SSO links, organizations/teams, all-projects admin, restore, audit log.

    Task<AdminUser?> GetUserAsync(Guid userId);
    Task<IReadOnlyList<AdminUser>> ListUsersAsync();
    Task SetUserAdminAsync(Guid userId, bool admin);
    Task SetUserDisabledAsync(Guid userId, bool disabled);
    Task SetPasswordAsync(Guid userId, string password);
    Task UnlockUserAsync(Guid userId);
    /// <summary>When no administrator exists yet, promotes the oldest account (the first one registered). Returns true if it promoted someone.</summary>
    Task<bool> EnsureAdminAsync();
    /// <summary>Password sign-in with account state: disabled accounts are refused, and <paramref name="maxFailures"/> wrong passwords in a row lock the account for <paramref name="lockout"/>.</summary>
    Task<LoginResult> AuthenticateAsync(string username, string password, int maxFailures, TimeSpan lockout);

    Task<UserAccount?> FindExternalLoginAsync(string issuer, string subject);
    /// <summary>Creates an account that has no usable password (single sign-on only) and links it to the external identity.</summary>
    Task<UserAccount> CreateExternalUserAsync(string username, string displayName, ExternalLogin login);
    Task LinkExternalLoginAsync(Guid userId, ExternalLogin login);

    Task<Guid> CreateOrganizationAsync(string name);
    Task DeleteOrganizationAsync(Guid organizationId);
    Task<IReadOnlyList<OrganizationInfo>> ListOrganizationsAsync();
    Task<IReadOnlyList<UserAccount>> OrganizationMembersAsync(Guid organizationId);
    Task AddOrganizationMemberAsync(Guid organizationId, Guid userId);
    Task RemoveOrganizationMemberAsync(Guid organizationId, Guid userId);
    Task<Guid> CreateTeamAsync(Guid organizationId, string name);
    Task DeleteTeamAsync(Guid teamId);
    Task<IReadOnlyList<TeamInfo>> ListTeamsAsync(Guid organizationId);
    Task<IReadOnlyList<UserAccount>> TeamMembersAsync(Guid teamId);
    /// <summary>Adds a user to a team (and to the team's organization).</summary>
    Task AddTeamMemberAsync(Guid teamId, Guid userId);
    Task RemoveTeamMemberAsync(Guid teamId, Guid userId);
    Task<IReadOnlyList<TeamGrant>> TeamGrantsAsync(Guid teamId);
    Task SetTeamGrantAsync(Guid teamId, Guid projectId, ProjectRole role);
    Task RemoveTeamGrantAsync(Guid teamId, Guid projectId);

    Task<IReadOnlyList<AdminProject>> ListAllProjectsAsync();
    /// <summary>Makes <paramref name="newOwnerId"/> an owner; previous owners become editors.</summary>
    Task TransferProjectAsync(Guid projectId, Guid newOwnerId);
    /// <summary>Recreates a project from a backup with its version history. Keeps the original id unless it is taken.</summary>
    Task<Guid> RestoreProjectAsync(RestoredProject project);
    /// <summary>Every version of a project with its full model, oldest first (for backups).</summary>
    Task<IReadOnlyList<VersionSnapshot>> ListVersionSnapshotsAsync(Guid projectId);

    Task AppendAuditAsync(AuditEntry entry);
    Task<AuditPage> QueryAuditAsync(AuditQuery query);
    // ---------------------------------------------------------------- branches & baselines (see Branching.cs)
    // The project row itself is the "main" branch; named branches each keep their own head model.

    Task<Guid> CreateBranchAsync(Guid projectId, string name, string headModelJson, string baseModelJson, long? baseVersionId, Guid? creator);
    Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(Guid projectId);
    Task<BranchInfo?> GetBranchAsync(Guid branchId);
    Task<BranchModels?> LoadBranchAsync(Guid branchId);
    Task<long> SaveBranchAsync(Guid branchId, string modelJson, Guid? userId);
    /// <summary>Moves the branch's merge base (after a merge, the merged-in state becomes the new common ancestor).</summary>
    Task SetBranchBaseAsync(Guid branchId, string baseModelJson, long? baseVersionId);
    Task DeleteBranchAsync(Guid branchId);
    /// <summary>Commits a version on a branch (null = main). Version numbers are shared by the whole project.</summary>
    Task<(long Id, int Number)> CommitBranchVersionAsync(Guid projectId, Guid? branchId, string message, string modelJson, Guid? author);
    /// <summary>Versions committed on one branch (null = main).</summary>
    Task<IReadOnlyList<VersionInfo>> ListBranchVersionsAsync(Guid projectId, Guid? branchId);

    Task<long> CreateBaselineAsync(Guid projectId, long versionId, string name, string description, Guid? creator);
    Task<IReadOnlyList<BaselineInfo>> ListBaselinesAsync(Guid projectId);
    Task DeleteBaselineAsync(long baselineId);

    // ---------------------------------------------------------------- per-user preferences & account (see Preferences.cs)
    // One JSON document per user (theme, diagram defaults, editor options…) that follows the user across devices.

    /// <summary>The user's preferences document (a JSON object), or null when none was saved yet.</summary>
    Task<string?> GetUserPreferencesAsync(Guid userId);
    /// <summary>Replaces the user's preferences document (a JSON object of at most <see cref="Preferences.MaxJsonLength"/> characters).</summary>
    Task SaveUserPreferencesAsync(Guid userId, string json);
    /// <summary>Changes the display name and colour shown to collaborators. Returns the updated account.</summary>
    Task<UserAccount> UpdateUserProfileAsync(Guid userId, string displayName, string color);
    /// <summary>"Sign out everywhere": sessions issued before now stop working.</summary>
    Task RevokeUserSessionsAsync(Guid userId);
    /// <summary>When the user last signed out everywhere (sessions issued earlier are invalid), or null.</summary>
    Task<DateTime?> UserSessionsRevokedAtAsync(Guid userId);

    // ---------------------------------------------------------------- code sync: review-branch runs (see CodeSyncRuns.cs)
    // One row per push applied to a project's code-sync review branch, with the list of element changes as JSON.

    Task AddCodeSyncRunAsync(CodeSyncRunRecord run);
    Task<CodeSyncRunRecord?> GetCodeSyncRunAsync(Guid runId);
    /// <summary>A project's runs, newest first.</summary>
    Task<IReadOnlyList<CodeSyncRunRecord>> ListCodeSyncRunsAsync(Guid projectId, int limit = 50);
    /// <summary>Records a review decision: the run's status, summary and its change list (a JSON array).</summary>
    Task UpdateCodeSyncRunAsync(Guid runId, CodeSyncRunStatus status, string summary, string changesJson);

    // ---------------------------------------------------------------- per-package permissions (see PackagePermissions.cs)
    // Explicit rules (none / read / edit / manage) for a user or a team on a package and everything below it; see docs/PERMISSIONS.md.

    /// <summary>Every rule of a project, with the user's display name or the team's name.</summary>
    Task<IReadOnlyList<PackagePermission>> ListPackagePermissionsAsync(Guid projectId);
    /// <summary>Creates or replaces the rule for one principal on one package.</summary>
    Task SetPackagePermissionAsync(Guid projectId, string elementId, Core.Permissions.PrincipalKind kind, Guid principalId,
                                   Core.Permissions.PackageAccess level, Guid? updatedBy);
    Task RemovePackagePermissionAsync(Guid projectId, string elementId, Core.Permissions.PrincipalKind kind, Guid principalId);
    /// <summary>The teams a user belongs to (team rules apply to their members).</summary>
    Task<IReadOnlyList<Guid>> UserTeamIdsAsync(Guid userId);
    /// <summary>Teams with a role on a project.</summary>
    Task<IReadOnlyList<ProjectTeam>> ProjectTeamsAsync(Guid projectId);

    // ---------------------------------------------------------------- used projects / shared model libraries (see ProjectUsages.cs)
    // A project uses a package of another project, pinned to a version, a branch head or a baseline; see docs/LIBRARIES.md.

    /// <summary>The packages a project uses, oldest first (the load order: the first usage of a package wins).</summary>
    Task<IReadOnlyList<ProjectUsage>> ListProjectUsagesAsync(Guid projectId);
    /// <summary>The usages of a project by other projects.</summary>
    Task<IReadOnlyList<ProjectUsage>> ListProjectUsersAsync(Guid usedProjectId);
    Task<ProjectUsage?> GetProjectUsageAsync(Guid usageId);
    /// <summary>Adds a usage; a project can use each package of another project once (<see cref="InvalidOperationException"/> otherwise).</summary>
    Task<Guid> AddProjectUsageAsync(Guid projectId, Guid usedProjectId, string rootPackageId, Core.Libraries.LibraryPinKind pinKind, string pinValue,
                                    string? mountPointId, Guid? addedBy);
    /// <summary>Moves a usage's pin and/or mount point.</summary>
    Task UpdateProjectUsageAsync(Guid usageId, Core.Libraries.LibraryPinKind pinKind, string pinValue, string? mountPointId);
    Task RemoveProjectUsageAsync(Guid usageId);
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
