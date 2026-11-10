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
/// per project, <c>projects/{id}/project.json</c>, <c>model.meridian</c> (latest state, including unsaved live edits)
/// and every committed version under <c>versions/</c>. Restore recreates projects with their history.
/// </summary>
public sealed class BackupService(IMeridianStore db, CollaborationHub hub, IAuditLog audit, AdminSettings settings, ILogger<BackupService> log)
{
    public const string Format = "meridian-backup";
    private const long MaxEntryBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // ------------------------------------------------------------------ format

    public sealed record Manifest(string Format, int FormatVersion, DateTime CreatedAt, string CreatedBy, bool IncludesUsers, List<ManifestProject> Projects);
    public sealed record ManifestProject(Guid Id, string Name, long Revision, DateTime UpdatedAt, int Versions, string? Owner);
    public sealed record BackupUser(Guid Id, string Username, string DisplayName, string Color, bool IsAdmin, bool Disabled, DateTime CreatedAt, IReadOnlyList<string> SsoProviders);
    public sealed record BackupMember(Guid UserId, string Username, string Role);
    public sealed record ProjectDoc(Guid Id, string Name, long Revision, DateTime CreatedAt, DateTime UpdatedAt, List<BackupMember> Members);
    public sealed record VersionDoc(int Number, string Message, Guid? AuthorId, DateTime CreatedAt, string File);

    public sealed record RestoreResult(Guid SourceId, string Name, Guid? NewId, int Versions, string? Error);

    // ------------------------------------------------------------------ backup

    /// <summary>Writes a complete backup zip to <paramref name="output"/> (a seekable stream such as a file).</summary>
    public async Task<Manifest> WriteAsync(Stream output, bool includeUsers, string createdBy, CancellationToken ct = default)
    {
        await hub.FlushAllAsync();
        var projects = await db.ListAllProjectsAsync();
        var manifest = new Manifest(Format, 1, DateTime.UtcNow, createdBy, includeUsers, []);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var p in projects)
        {
            ct.ThrowIfCancellationRequested();
            var row = await db.LoadProjectAsync(p.Id);
            if (row is null) continue; // deleted meanwhile
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
            manifest.Projects.Add(new ManifestProject(p.Id, row.Value.Name, row.Value.Revision, p.UpdatedAt, versions.Count, p.Owner?.Username));
        }
        if (includeUsers)
        {
            var users = await db.ListUsersAsync();
            await WriteJson(zip, "users.json", users.Select(u => new BackupUser(u.User.Id, u.User.Username, u.User.DisplayName, u.User.Color, u.IsAdmin, u.Disabled, u.CreatedAt, u.SsoProviders)).ToList());
        }
        await WriteJson(zip, "manifest.json", manifest);
        return manifest;
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
        {
            var dir = $"projects/{mp.Id:D}/";
            try
            {
                var doc = ReadJson<ProjectDoc>(zip, dir + "project.json") ?? throw new InvalidDataException("project.json is missing");
                var model = ReadText(zip, dir + "model.meridian") ?? throw new InvalidDataException("model.meridian is missing");
                ProjectFile.Deserialize(model); // validate before writing anything
                var versions = new List<VersionSnapshot>();
                foreach (var v in ReadJson<List<VersionDoc>>(zip, dir + "versions.json") ?? [])
                {
                    if (v.File.Contains("..") || !v.File.StartsWith("versions/", StringComparison.Ordinal)) throw new InvalidDataException($"Bad version path {v.File}");
                    var vjson = ReadText(zip, dir + v.File) ?? throw new InvalidDataException($"{v.File} is missing");
                    Guid? author = v.AuthorId is { } a ? await MapUser(a, backupUsers.GetValueOrDefault(a)?.Username) : null;
                    versions.Add(new VersionSnapshot(v.Number, v.Message, vjson, author, v.CreatedAt));
                }
                var members = new List<RestoredMember>();
                foreach (var m in doc.Members)
                    if (await MapUser(m.UserId, m.Username) is { } uid && Enum.TryParse<ProjectRole>(m.Role, true, out var role))
                        members.Add(new RestoredMember(uid, role));
                if (members.All(m => m.Role != ProjectRole.Owner)) members.Add(new RestoredMember(actor.Id, ProjectRole.Owner));

                var exists = await db.LoadProjectAsync(doc.Id) is not null;
                var name = exists ? $"{doc.Name} (restored {DateTime.UtcNow:yyyy-MM-dd})" : doc.Name;
                var newId = await db.RestoreProjectAsync(new RestoredProject(doc.Id, name, model, versions, members));
                await audit.RecordAsync(AuditActions.BackupRestore, actor, newId, name, target: name,
                    details: new { sourceId = doc.Id, versions = versions.Count, members = members.Count, backupCreated = manifest.CreatedAt.ToString("O", CultureInfo.InvariantCulture), copy = exists }, ip: ip);
                results.Add(new RestoreResult(mp.Id, name, newId, versions.Count, null));
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or FormatException or InvalidOperationException or IOException)
            {
                log.LogWarning(ex, "Restore of project {Project} failed", mp.Name);
                results.Add(new RestoreResult(mp.Id, mp.Name, null, 0, ex.Message));
            }
        }
        return results;
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
