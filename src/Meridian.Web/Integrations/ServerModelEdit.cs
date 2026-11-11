using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Services;

namespace Meridian.Web.Integrations;

/// <summary>
/// Edits a project's live model from the server (OSLC creation, scheduled requirements sync) the way a browser tab
/// does: it joins the project's collaboration session as an editing participant and submits each operation, so open
/// tabs see the change at once and the session persists it (immediately on dispose when nobody else has it open).
/// Every <see cref="EditAsync{T}"/> works on a fresh replica of the session's current model.
/// </summary>
public sealed class ServerModelEdit : IAsyncDisposable
{
    private readonly ProjectSession _session;
    private readonly string _participant;
    private readonly Guid _userId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Relay _client;

    private ServerModelEdit(ProjectSession session, string participant, Guid userId, Relay client)
    {
        _session = session;
        _participant = participant;
        _userId = userId;
        _client = client;
    }

    /// <summary>Joins the main line of <paramref name="projectId"/> as <paramref name="user"/>. Null when the project does not exist.</summary>
    public static async Task<ServerModelEdit?> OpenAsync(CollaborationHub hub, Guid projectId, UserAccount user)
    {
        var participant = "server-" + Guid.NewGuid().ToString("N");
        var client = new Relay();
        var session = await hub.JoinAsync(projectId, new Presence(participant, user, null, null), client);
        return session is null ? null : new ServerModelEdit(session, participant, user.Id, client);
    }

    /// <summary>
    /// Runs <paramref name="change"/> on a replica of the current model (as the user may see it: package permissions) and
    /// submits its operations together, all or nothing. Throws <see cref="PermissionDeniedException"/> when the session
    /// refuses them (a read-only or hidden package, a lock, a view-only role).
    /// </summary>
    public async Task<T> EditAsync<T>(Func<ModelRepository, T> change)
    {
        await _gate.WaitAsync();
        try
        {
            var model = ProjectFile.Deserialize(_session.SnapshotFor(_participant));
            var ops = new List<ModelOp>();
            model.OpEmitted += ops.Add;
            var result = change(model);
            if (ops.Count > 0 && _session.SubmitBatch(_participant, _userId, ops) is { Refused: { } why })
                throw new PermissionDeniedException(why);
            return result;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _session.Leave(_participant);
        await _session.PersistAsync();
        _gate.Dispose();
    }

    // Each edit starts from a fresh snapshot, so other participants' operations need no handling here.
    private sealed class Relay : ISessionClient
    {
        public void Replaced(string modelJson, string message) { }
        public void RemoteOp(ModelOp op) { }
        public void PresenceChanged() { }
        public void Reset(string modelJson) { }
        public void CommentsChanged() { }
    }
}
