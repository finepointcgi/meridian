using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Services;

namespace Meridian.Web.Admin;

/// <summary>
/// Full-server backups as a zip of JSON: <c>manifest.json</c>, optional <c>users.json</c> (never password hashes) and,
/// per project, <c>projects/{id}/project.json</c>, <c>model.meridian</c> (latest state, including unsaved live edits),
/// every committed version under <c>versions/</c> and (format 2) <c>comments.json</c>, <c>reviews.json</c> and
/// <c>baselines.json</c>. Restore recreates projects with their history. A project archive
/// (<see cref="WriteProjectAsync"/>, "Download project with history") is the same format holding one project, so a
/// project moves from the desktop's local workspace to a team server, or between servers, with its history.
/// </summary>
public sealed class BackupService(IMeridianStore db, CollaborationHub hub, IAuditLog audit, AdminSettings settings, ILogger<BackupService> log)
{
    public const string Format = "meridian-backup";
    /// <summary>2 added comments, reviews and baselines; format 1 backups still restore (without them).</summary>
    public const int CurrentFormatVersion = 2;
    private const long MaxEntryBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // ------------------------------------------------------------------ format

    public sealed record Manifest(string Format, int FormatVersion, DateTime CreatedAt, string CreatedBy, bool IncludesUsers, List<ManifestProject> Projects);
    public sealed record ManifestProject(Guid Id, string Name, long Revision, DateTime UpdatedAt, int Versions, string? Owner);
    public sealed record BackupUser(Guid Id, string Username, string DisplayName, string Color, bool IsAdmin, bool Disabled, DateTime CreatedAt, IReadOnlyList<string> SsoProviders);
    public sealed record BackupMember(Guid UserId, string Username, string Role);
    public sealed record ProjectDoc(Guid Id, string Name, long Revision, DateTime CreatedAt, DateTime UpdatedAt, List<BackupMember> Members);
    public sealed record VersionDoc(int Number, string Message, Guid? AuthorId, DateTime CreatedAt, string File);
    public sealed record CommentDoc(long Id, string ElementId, Guid? AuthorId, string? Author, string Body, bool Resolved, DateTime CreatedAt, long? ParentId);
    public sealed record ReviewerDoc(Guid UserId, string Username, ReviewVote Vote, DateTime? VotedAt);
    public sealed record ReviewCommentDoc(long Id, string? ElementId, long? ParentId, Guid? AuthorId, string? Author, string Body, DateTime CreatedAt);
    public sealed record ReviewCheckDoc(string ElementId, Guid UserId);
    public sealed record ReviewDoc(string Title, string Description, Guid? AuthorId, string? Author, ReviewState State, ReviewScopeKind ScopeKind,
        List<string> ElementIds, int? BaseVersion, int? HeadVersion, bool ApplyStatus, DateTime CreatedAt, DateTime UpdatedAt,
        List<ReviewerDoc> Reviewers, List<ReviewCommentDoc> Comments, List<ReviewCheckDoc> Checks);
    public sealed record BaselineDoc(int Version, string Name, string Description, DateTime CreatedAt);

    public sealed record RestoreResult(Guid SourceId, string Name, Guid? NewId, int Versions, string? Error);

    // ------------------------------------------------------------------ backup

    /// <summary>Writes a complete backup zip to <paramref name="output"/> (a seekable stream such as a file).</summary>
    public async Task<Manifest> WriteAsync(Stream output, bool includeUsers, string createdBy, CancellationToken ct = default)
    {
        await hub.FlushAllAsync();
        var projects = await db.ListAllProjectsAsync();
        var manifest = new Manifest(Format, CurrentFormatVersion, DateTime.UtcNow, createdBy, includeUsers, []);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var p in projects)
        {
            ct.ThrowIfCancellationRequested();
            if (await WriteProjectEntriesAsync(zip, p) is { } entry) manifest.Projects.Add(entry);
        }
        if (includeUsers)
        {
            var users = await db.ListUsersAsync();
            await WriteJson(zip, "users.json", users.Select(u => new BackupUser(u.User.Id, u.User.Username, u.User.DisplayName, u.User.Color, u.IsAdmin, u.Disabled, u.CreatedAt, u.SsoProviders)).ToList());
        }
        await WriteJson(zip, "manifest.json", manifest);
        return manifest;
    }

    /// <summary>
    /// "Download project with history": one project with its versions, comments, reviews and baselines, in the backup
    /// format (no accounts). Only the project's owners may call this (the caller checks): owners see every package.
    /// </summary>
    public async Task<Manifest> WriteProjectAsync(Stream output, Guid projectId, string createdBy, CancellationToken ct = default)
    {
        await hub.FlushAllAsync();
        var project = (await db.ListAllProjectsAsync()).FirstOrDefault(p => p.Id == projectId)
            ?? throw new InvalidOperationException("The project no longer exists.");
        var manifest = new Manifest(Format, CurrentFormatVersion, DateTime.UtcNow, createdBy, false, []);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        ct.ThrowIfCancellationRequested();
        if (await WriteProjectEntriesAsync(zip, project) is { } entry) manifest.Projects.Add(entry);
        await WriteJson(zip, "manifest.json", manifest);
        return manifest;
    }

    public static string ProjectFileName(string projectName)
    {
        var safe = string.Concat(projectName.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '_' : c)).Trim();
        return $"{(safe.Length == 0 ? "project" : safe)} (with history).zip";
    }

    private async Task<ManifestProject?> WriteProjectEntriesAsync(ZipArchive zip, AdminProject p)
    {
        var row = await db.LoadProjectAsync(p.Id);
        if (row is null) return null; // deleted meanwhile
        var model = hub.Find(p.Id)?.Snapshot() ?? row.Value.ModelJson;
        var members = await db.MembersAsync(p.Id);
        var versions = await db.ListVersionSnapshotsAsync(p.Id);
        var dir = $"projects/{p.Id:D}/";
        await WriteJson(zip, dir + "project.json", new ProjectDoc(p.Id, row.Value.Name, row.Value.Revision, p.CreatedAt, p.UpdatedAt,
            members.Select(m => new BackupMember(m.User.Id, m.User.Username, m.Role.ToString().ToLowerInvariant())).ToList()));
        await WriteText(zip, dir + "model.meridian", model);
        var docs = new List<VersionDoc>();
        foreach (var v in versions)
        {
            var file = $"versions/{v.Number:D4}.meridian";
            await WriteText(zip, dir + file, v.ModelJson);
            docs.Add(new VersionDoc(v.Number, v.Message, v.AuthorId, v.CreatedAt, file));
        }
        await WriteJson(zip, dir + "versions.json", docs);

        // Discussion and reviews refer to versions by number (row ids differ on the server that restores them).
        var numberOf = (await db.ListVersionsAsync(p.Id)).ToDictionary(v => v.Id, v => v.Number);
        int? Number(long? versionId) => versionId is { } id && numberOf.TryGetValue(id, out var n) ? n : null;
        var parents = await db.ListCommentParentsAsync(p.Id);
        await WriteJson(zip, dir + "comments.json", (await db.ListCommentsAsync(p.Id)).OrderBy(c => c.Id).Select(c =>
            new CommentDoc(c.Id, c.ElementId, c.Author.Id, c.Author.Username, c.Body, c.Resolved, c.CreatedAt, parents.TryGetValue(c.Id, out var parent) ? parent : null)).ToList());
        var reviews = new List<ReviewDoc>();
        foreach (var r in (await db.ListReviewsAsync(p.Id)).OrderBy(r => r.Id))
        {
            var comments = (await db.ListReviewCommentsAsync(r.Id)).OrderBy(c => c.Id)
                .Select(c => new ReviewCommentDoc(c.Id, c.ElementId, c.ParentId, c.Author.Id, c.Author.Username, c.Body, c.CreatedAt)).ToList();
            var checks = (await db.ListReviewChecksAsync(r.Id)).Select(c => new ReviewCheckDoc(c.ElementId, c.UserId)).ToList();
            reviews.Add(new ReviewDoc(r.Title, r.Description, r.Author.Id, r.Author.Username, r.State, r.ScopeKind, r.ElementIds.ToList(),
                Number(r.BaseVersionId), Number(r.HeadVersionId), r.ApplyStatus, r.CreatedAt, r.UpdatedAt,
                r.Reviewers.Select(x => new ReviewerDoc(x.User.Id, x.User.Username, x.Vote, x.VotedAt)).ToList(), comments, checks));
        }
        await WriteJson(zip, dir + "reviews.json", reviews);
        await WriteJson(zip, dir + "baselines.json", (await db.ListBaselinesAsync(p.Id)).OrderBy(b => b.Id)
            .Select(b => new BaselineDoc(b.VersionNumber, b.Name, b.Description, b.CreatedAt)).ToList());
        return new ManifestProject(p.Id, row.Value.Name, row.Value.Revision, p.UpdatedAt, versions.Count, p.Owner?.Username);
    }

    public static string FileName(DateTime utc) => $"meridian-backup-{utc:yyyyMMdd-HHmmss}.zip";

    /// <summary>Scheduled backup: writes into the configured folder and prunes old files beyond the retention count.</summary>
    public async Task<string> WriteToFolderAsync(CancellationToken ct = default)
    {
        var o = settings.Backup;
        if (string.IsNullOrWhiteSpace(o.Folder)) throw new InvalidOperationException("No backup folder is configured (Meridian:Backup:Folder).");
        Directory.CreateDirectory(o.Folder);
        var path = Path.Combine(o.Folder, FileName(DateTime.UtcNow));
        var temp = path + ".partial";
        Manifest manifest;
        await using (var fs = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            manifest = await WriteAsync(fs, o.IncludeUsers, "scheduler", ct);
        File.Move(temp, path, overwrite: true);
        foreach (var old in ListFiles().Skip(Math.Max(1, o.Retain)))
        {
            try { File.Delete(old.FullName); }
            catch (IOException ex) { log.LogWarning(ex, "Could not prune old backup {File}", old.Name); }
        }
        await audit.RecordAsync(AuditActions.BackupScheduled, null, target: Path.GetFileName(path), details: new { projects = manifest.Projects.Count, bytes = new FileInfo(path).Length }, actorName: "system");
        return path;
    }

    /// <summary>Scheduled backup files, newest first.</summary>
    public IReadOnlyList<FileInfo> ListFiles()
    {
        var folder = settings.Backup.Folder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return [];
        return new DirectoryInfo(folder).GetFiles("meridian-backup-*.zip").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList();
    }

    // ------------------------------------------------------------------ restore

    public static Manifest ReadManifest(ZipArchive zip) =>
        ReadJson<Manifest>(zip, "manifest.json") is { Format: Format } m ? m : throw new InvalidDataException("This is not a Meridian backup (manifest.json is missing or of another format).");

    /// <summary>
    /// Restores projects from a backup. Members are matched to accounts by id, then username; with
    /// <paramref name="recreateMissingUsers"/> missing accounts are recreated from <c>users.json</c> without a password
    /// (an administrator resets it, or they sign in with SSO). Projects whose id is taken are restored as a copy.
    /// </summary>
    public async Task<IReadOnlyList<RestoreResult>> RestoreAsync(ZipArchive zip, IReadOnlySet<Guid>? only, bool recreateMissingUsers, UserAccount actor, string? ip = null)
    {
        var manifest = ReadManifest(zip);
        var backupUsers = (ReadJson<List<BackupUser>>(zip, "users.json") ?? []).ToDictionary(u => u.Id);
        var userMap = new Dictionary<Guid, Guid?>();
        var results = new List<RestoreResult>();

        async Task<Guid?> MapUser(Guid id, string? username)
        {
            if (userMap.TryGetValue(id, out var mapped)) return mapped;
            Guid? result = null;
            if (await db.GetUserAsync(id) is not null) result = id;
            else if (username is { Length: > 0 } && await db.FindUserAsync(username) is { } byName) result = byName.Id;
            else if (recreateMissingUsers && backupUsers.TryGetValue(id, out var bu))
            {
                try
                {
                    var created = await db.CreateUserAsync(bu.Username, bu.DisplayName, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)));
                    if (bu.IsAdmin) await db.SetUserAdminAsync(created.Id, true);
                    await audit.RecordAsync(AuditActions.UserCreate, actor, target: created.Username, details: new { reason = "recreated from backup" }, ip: ip);
                    result = created.Id;
                }
                catch (InvalidOperationException ex) { log.LogWarning("Could not recreate user {User}: {Error}", bu.Username, ex.Message); }
            }
            userMap[id] = result;
            return result;
        }

        foreach (var mp in manifest.Projects.Where(p => only is null || only.Contains(p.Id)))
            results.Add(await RestoreProjectAsync(zip, manifest, mp, (id, username) => MapUser(id, username ?? backupUsers.GetValueOrDefault(id)?.Username),
                keepMembers: true, actor, AuditActions.BackupRestore, ip));
        return results;
    }

    /// <summary>
    /// "Open project with history" for any signed-in user: every project in the archive becomes a new project owned by
    /// <paramref name="actor"/> alone, with its versions, comments, reviews and baselines. Nothing is matched to other
    /// accounts on this server: what someone else wrote is attributed to <paramref name="actor"/> (a local workspace has
    /// one author anyway), and the archive's members are not added. Administrators use <see cref="RestoreAsync"/> to keep them.
    /// </summary>
    public async Task<IReadOnlyList<RestoreResult>> ImportProjectsAsync(ZipArchive zip, UserAccount actor, string? ip = null)
    {
        var manifest = ReadManifest(zip);
        var results = new List<RestoreResult>();
        foreach (var mp in manifest.Projects)
            results.Add(await RestoreProjectAsync(zip, manifest, mp, (_, _) => Task.FromResult<Guid?>(actor.Id), keepMembers: false, actor, AuditActions.ProjectImportHistory, ip));
        return results;
    }

    private async Task<RestoreResult> RestoreProjectAsync(ZipArchive zip, Manifest manifest, ManifestProject mp, Func<Guid, string?, Task<Guid?>> mapUser,
        bool keepMembers, UserAccount actor, string auditAction, string? ip)
    {
        var dir = $"projects/{mp.Id:D}/";
        try
        {
            var doc = ReadJson<ProjectDoc>(zip, dir + "project.json") ?? throw new InvalidDataException("project.json is missing");
            var model = ReadText(zip, dir + "model.meridian") ?? throw new InvalidDataException("model.meridian is missing");
            ProjectFile.Deserialize(model); // validate before writing anything
            async Task<Guid?> Author(Guid? id, string? username) => id is { } a ? await mapUser(a, username) : null;

            var versions = new List<VersionSnapshot>();
            foreach (var v in ReadJson<List<VersionDoc>>(zip, dir + "versions.json") ?? [])
            {
                if (v.File.Contains("..") || !v.File.StartsWith("versions/", StringComparison.Ordinal)) throw new InvalidDataException($"Bad version path {v.File}");
                var vjson = ReadText(zip, dir + v.File) ?? throw new InvalidDataException($"{v.File} is missing");
                versions.Add(new VersionSnapshot(v.Number, v.Message, vjson, await Author(v.AuthorId, null), v.CreatedAt));
            }
            var members = new List<RestoredMember>();
            if (keepMembers)
                foreach (var m in doc.Members)
                    if (await mapUser(m.UserId, m.Username) is { } uid && Enum.TryParse<ProjectRole>(m.Role, true, out var role))
                        members.Add(new RestoredMember(uid, role));
            if (members.All(m => m.Role != ProjectRole.Owner)) members.Add(new RestoredMember(actor.Id, ProjectRole.Owner));

            var comments = new List<RestoredComment>();
            foreach (var c in ReadJson<List<CommentDoc>>(zip, dir + "comments.json") ?? [])
                comments.Add(new RestoredComment(c.Id, c.ElementId, await Author(c.AuthorId, c.Author), c.Body, c.Resolved, c.CreatedAt, c.ParentId));
            var reviews = new List<RestoredReview>();
            foreach (var r in ReadJson<List<ReviewDoc>>(zip, dir + "reviews.json") ?? [])
            {
                var reviewers = new List<RestoredReviewer>();
                foreach (var x in r.Reviewers)
                    if (await mapUser(x.UserId, x.Username) is { } uid && reviewers.All(y => y.UserId != uid))
                        reviewers.Add(new RestoredReviewer(uid, x.Vote, x.VotedAt));
                var rc = new List<RestoredReviewComment>();
                foreach (var c in r.Comments)
                    rc.Add(new RestoredReviewComment(c.Id, c.ElementId, c.ParentId, await Author(c.AuthorId, c.Author), c.Body, c.CreatedAt));
                var checks = new List<RestoredReviewCheck>();
                foreach (var c in r.Checks)
                    if (await mapUser(c.UserId, null) is { } uid) checks.Add(new RestoredReviewCheck(c.ElementId, uid));
                reviews.Add(new RestoredReview(r.Title, r.Description, await Author(r.AuthorId, r.Author), r.State, r.ScopeKind, r.ElementIds,
                    r.BaseVersion, r.HeadVersion, r.ApplyStatus, r.CreatedAt, r.UpdatedAt, reviewers, rc, checks));
            }
            var baselines = (ReadJson<List<BaselineDoc>>(zip, dir + "baselines.json") ?? [])
                .Select(b => new RestoredBaseline(b.Version, b.Name, b.Description, b.CreatedAt)).ToList();

            var exists = await db.LoadProjectAsync(doc.Id) is not null;
            var name = exists ? $"{doc.Name} (restored {DateTime.UtcNow:yyyy-MM-dd})" : doc.Name;
            var newId = await db.RestoreProjectAsync(new RestoredProject(doc.Id, name, model, versions, members, comments, reviews, baselines));
            await audit.RecordAsync(auditAction, actor, newId, name, target: name,
                details: new { sourceId = doc.Id, versions = versions.Count, comments = comments.Count, reviews = reviews.Count, members = members.Count,
                    backupCreated = manifest.CreatedAt.ToString("O", CultureInfo.InvariantCulture), copy = exists }, ip: ip);
            return new RestoreResult(mp.Id, name, newId, versions.Count, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or FormatException or InvalidOperationException or IOException)
        {
            log.LogWarning(ex, "Restore of project {Project} failed", mp.Name);
            return new RestoreResult(mp.Id, mp.Name, null, 0, ex.Message);
        }
    }

    // ------------------------------------------------------------------ zip helpers

    private static async Task WriteText(ZipArchive zip, string path, string text)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        await using var s = entry.Open();
        await s.WriteAsync(Encoding.UTF8.GetBytes(text));
    }

    private static Task WriteJson<T>(ZipArchive zip, string path, T value) => WriteText(zip, path, JsonSerializer.Serialize(value, Json));

    private static string? ReadText(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null) return null;
        if (entry.Length > MaxEntryBytes) throw new InvalidDataException($"{path} is too large.");
        using var s = entry.Open();
        using var limited = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            limited.Write(buffer, 0, n);
            if (limited.Length > MaxEntryBytes) throw new InvalidDataException($"{path} is too large.");
        }
        return Encoding.UTF8.GetString(limited.GetBuffer(), 0, (int)limited.Length);
    }

    private static T? ReadJson<T>(ZipArchive zip, string path) => ReadText(zip, path) is { } text ? JsonSerializer.Deserialize<T>(text, Json) : default;
}

/// <summary>Writes a backup on the configured interval (<c>Meridian:Backup</c>) and keeps the last N files.</summary>
public sealed class BackupScheduler(BackupService backups, AdminSettings settings, ILogger<BackupScheduler> log) : BackgroundService
{
    public static DateTime? LastRun { get; private set; }
    public static DateTime? NextRun { get; private set; }
    public static string? LastError { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = settings.Backup;
        if (!o.Enabled) return;
        var interval = TimeSpan.FromHours(Math.Max(o.IntervalHours, 1.0 / 60));
        // First run soon after start-up unless a recent backup already exists.
        var newest = backups.ListFiles().FirstOrDefault()?.LastWriteTimeUtc;
        var wait = newest is { } n && DateTime.UtcNow - n < interval ? interval - (DateTime.UtcNow - n) : TimeSpan.FromMinutes(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            NextRun = DateTime.UtcNow + wait;
            try { await Task.Delay(wait, stoppingToken); }
            catch (TaskCanceledException) { return; }
            try
            {
                var path = await backups.WriteToFolderAsync(stoppingToken);
                LastRun = DateTime.UtcNow;
                LastError = null;
                log.LogInformation("Scheduled backup written to {Path}", path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LastError = ex.Message;
                log.LogError(ex, "Scheduled backup failed");
            }
            wait = interval;
        }
    }
}
