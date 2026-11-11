using Meridian.Core.Model;

namespace Meridian.Web.Services;

/// <summary>Who edited which element since the last commit of this line (element history's "Uncommitted" entry).</summary>
public sealed partial class ProjectSession
{
    /// <summary>A collaborator's latest edit of an element that is not committed yet.</summary>
    public sealed record UncommittedEdit(Guid UserId, string UserName, DateTime At);

    private readonly Dictionary<string, UncommittedEdit> _uncommitted = new(StringComparer.Ordinal);

    /// <summary>Records the elements an applied operation touched (under the session lock).</summary>
    private void TrackUncommitted(string participantId, Guid userId, ModelOp op)
    {
        var name = _participants.TryGetValue(participantId, out var p) ? p.Presence.User.DisplayName : "";
        var edit = new UncommittedEdit(userId, name, DateTime.UtcNow);
        switch (op)
        {
            case SetFieldOp s: _uncommitted[s.ElementId] = edit; break;
            case MoveOp m: _uncommitted[m.ElementId] = edit; break;
            case DetachOp d: _uncommitted[d.ElementId] = edit; break;
            case AttachOp a:
                foreach (var x in a.Snap.SelfAndDescendants()) _uncommitted[x.Id] = edit;
                break;
            case NodeInsertOp n:
                _uncommitted[n.DiagramId] = edit;
                _uncommitted[n.ElementId] = edit;   // shown on a diagram
                break;
            default:
                if (DiagramOf(op) is { } diagram) _uncommitted[diagram] = edit;
                break;
        }
    }

    private static string? DiagramOf(ModelOp op) => op switch
    {
        NodeStyleOp x => x.DiagramId,
        NodeRemoveOp x => x.DiagramId,
        NodeBoundsOp x => x.DiagramId,
        EdgeInsertOp x => x.DiagramId,
        EdgeStyleOp x => x.DiagramId,
        EdgeRemoveOp x => x.DiagramId,
        EdgeWaypointsOp x => x.DiagramId,
        _ => null,
    };

    /// <summary>The latest uncommitted edit of an element made in this session, or null (none, or not known: see <see cref="ForgetUncommitted"/>).</summary>
    public UncommittedEdit? LastUncommittedEdit(string elementId)
    {
        lock (_gate) return _uncommitted.GetValueOrDefault(elementId);
    }

    /// <summary>A version of this line was committed: nothing is uncommitted any more.</summary>
    public void ClearUncommitted()
    {
        lock (_gate) _uncommitted.Clear();
    }

    /// <summary>The model was replaced (restore, merge): edits are no longer attributable (history compares with the last commit).</summary>
    private void ForgetUncommitted() => _uncommitted.Clear();
}
