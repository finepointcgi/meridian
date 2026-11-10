using Meridian.Core.Model;

namespace Meridian.Web.Services;

public sealed partial class ProjectSession
{
    /// <summary>
    /// Applies operations made outside any tab (a code sync computed them on a copy of this session's model) and relays
    /// them to everyone, in order. Unlike <see cref="Replace"/> it keeps what people changed meanwhile: each operation is
    /// addressed by element id, so edits to other elements (or other fields) are untouched. Element locks do not apply.
    /// </summary>
    public void ApplySystemOps(IReadOnlyList<ModelOp> ops, Guid? userId)
    {
        if (ops.Count == 0) return;
        lock (_gate)
        {
            foreach (var op in ops) _master.ApplyRemote(op);
            ScheduleTextRefresh();
            Sequence += ops.Count;
            MarkDirty();
            _lastEditor = userId;
            foreach (var p in _participants.Values)
                foreach (var op in ops) p.Client.RemoteOp(op);
        }
    }
}
