using System.Text.Json;
using Meridian.Core.CodeSync;
using Meridian.Core.CodeSync.Conformance;
using Meridian.Data;
using Meridian.Web.Integrations;

namespace Meridian.Web.CodeSync.Conformance;

/// <summary>
/// What the architecture check keeps per code repository connection: the last synced code (packed compact JSON, so
/// "Check architecture" can run again without the git host), the last result and the per-repository setting "Fail pull
/// requests on architecture violations". Stored as an integration row of its own kind, next to the connection, so the
/// connection's settings are untouched.
/// </summary>
public sealed class ArchitectureRecord
{
    public Guid ConnectionId { get; set; }
    public string RepoDisplay { get; set; } = "";
    public string Commit { get; set; } = "";
    public string? CommitUrl { get; set; }
    /// <summary>False when the last sync read only the changed files.</summary>
    public bool IsFullSnapshot { get; set; } = true;
    /// <summary>Post a failing "meridian/architecture" status when there are violations (default: success with a summary).</summary>
    public bool FailPullRequests { get; set; }
    public DateTime? CheckedAt { get; set; }
    public string? Summary { get; set; }
    public int Errors { get; set; }
    public int Warnings { get; set; }
    /// <summary><see cref="CodeModelJson.Pack"/> of the last synced code.</summary>
    public string? PackedCode { get; set; }

    public CodeModel? Code() => CodeModelJson.Unpack(PackedCode);

    public static ArchitectureRecord Parse(string json)
    {
        try { return JsonSerializer.Deserialize<ArchitectureRecord>(json, IntegrationJson.Options) ?? new ArchitectureRecord(); }
        catch (JsonException) { return new ArchitectureRecord(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this, IntegrationJson.Options);
}

/// <summary>Reads and writes <see cref="ArchitectureRecord"/>s (integration rows of kind <see cref="Kind"/>).</summary>
public sealed class ArchitectureStore(IMeridianStore db)
{
    public const string Kind = "code-architecture";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The records of a project's connections, by connection id.</summary>
    public async Task<IReadOnlyDictionary<Guid, ArchitectureRecord>> ListAsync(Guid projectId)
    {
        var rows = (await db.ListIntegrationsAsync(projectId)).Where(c => c.Kind == Kind);
        var map = new Dictionary<Guid, ArchitectureRecord>();
        foreach (var r in rows.OrderBy(r => r.CreatedAt))
        {
            var rec = ArchitectureRecord.Parse(r.ConfigJson);
            if (rec.ConnectionId != Guid.Empty) map[rec.ConnectionId] = rec;
        }
        return map;
    }

    public async Task<ArchitectureRecord?> FindAsync(Guid projectId, Guid connectionId) =>
        (await ListAsync(projectId)).GetValueOrDefault(connectionId);

    /// <summary>Changes a connection's record (created when missing) under a lock, and returns the saved copy.</summary>
    public async Task<ArchitectureRecord> UpdateAsync(Guid projectId, Guid connectionId, Action<ArchitectureRecord> change)
    {
        await _gate.WaitAsync();
        try
        {
            var row = (await db.ListIntegrationsAsync(projectId))
                .FirstOrDefault(c => c.Kind == Kind && ArchitectureRecord.Parse(c.ConfigJson).ConnectionId == connectionId);
            var rec = row is null ? new ArchitectureRecord { ConnectionId = connectionId } : ArchitectureRecord.Parse(row.ConfigJson);
            change(rec);
            rec.ConnectionId = connectionId;
            await db.SaveIntegrationAsync(new IntegrationConnection(row?.Id ?? Guid.NewGuid(), projectId, Kind, $"Architecture {connectionId:N}",
                rec.ToJson(), null, row?.CreatedAt ?? DateTime.UtcNow));
            return rec;
        }
        finally { _gate.Release(); }
    }
}
