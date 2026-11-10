using Meridian.Core.Model;
using Meridian.Core.Permissions;

namespace Meridian.Web.Services;

public sealed partial class ProjectSession
{
    /// <summary>Sender id of operations made outside any tab (no participant has it, so everyone receives them).</summary>
    private const string SystemSender = "\0system";

    /// <summary>
    /// Applies operations made outside any tab (a code sync computed them on a copy of this session's model) and relays
    /// them to everyone, in order. Unlike <see cref="Replace"/> it keeps what people changed meanwhile: each operation is
    /// addressed by element id, so edits to other elements (or other fields) are untouched. Element locks do not apply.
    /// <para>
    /// Package permissions: participants with hidden packages receive only what they may see (as for any edit). When
    /// <paramref name="actor"/> is given (a user's action rather than the system's), every operation must pass
    /// <see cref="PermissionCheck.Check"/> for it first — all or nothing — or <see cref="PermissionDeniedException"/> is thrown
    /// and nothing is applied.
    /// </para>
    /// </summary>
    public void ApplySystemOps(IReadOnlyList<ModelOp> ops, Guid? userId, AccessView? actor = null)
    {
        if (ops.Count == 0) return;
        lock (_gate)
        {
            if (actor is not null)
                foreach (var op in ops)
                    if (PermissionCheck.Check(_master, actor, op) is { } why) throw new PermissionDeniedException(why);
            foreach (var op in ops) ApplyLocked(SystemSender, userId ?? Guid.Empty, op, events: false);   // filtered relay (ProjectSession.Permissions.cs)
            _lastEditor = userId;
        }
    }
}
