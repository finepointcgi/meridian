using Meridian.Core.Model;

namespace Meridian.Core.Collaboration;

/// <summary>
/// Structural checks the server makes on one user action's operations before sequencing them (docs/COLLABORATION.md).
/// A batch that fails is refused as a whole and the author's replica takes it back (<see cref="ReplicaSync.Reject"/>).
/// </summary>
public static class BatchCheck
{
    /// <summary>
    /// Null when the batch's moves are fine, else why not: a move that would put an element inside itself, which happens when
    /// two people move two packages into each other at the same moment (the later one in the server's order is refused).
    /// Earlier operations of the batch count (an element created, deleted or moved by it), without changing the model.
    /// </summary>
    public static string? MoveCycle(ModelRepository model, IReadOnlyList<ModelOp> ops)
    {
        if (!ops.Any(o => o is MoveOp)) return null;
        // Owner changes made by earlier operations of the batch: id → owner id ("" = detached).
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        string? OwnerOf(string id) => owner.TryGetValue(id, out var o) ? o.Length == 0 ? null : o : model.Find(id)?.Owner?.Id;
        bool Exists(string id) => owner.TryGetValue(id, out var o) ? o.Length > 0 : model.Find(id) is { } e && (e.Owner != null || ReferenceEquals(e, model.Root));

        foreach (var op in ops)
            switch (op)
            {
                case AttachOp a when Exists(a.OwnerId) && !Exists(a.Snap.Id):
                    void Place(ElementSnap s, string parent)
                    {
                        owner[s.Id] = parent;
                        foreach (var c in s.Children) Place(c, s.Id);
                    }
                    Place(a.Snap, a.OwnerId);
                    break;
                case DetachOp d:
                    owner[d.ElementId] = "";
                    break;
                case MoveOp mv when Exists(mv.ElementId) && Exists(mv.NewOwnerId):
                {
                    var steps = 0;
                    for (var x = mv.NewOwnerId; x != null; x = OwnerOf(x))
                    {
                        if (x == mv.ElementId)
                        {
                            string Name(string id) => model.Find(id)?.DisplayName ?? "that element";
                            return $"“{Name(mv.ElementId)}” can't be moved into “{Name(mv.NewOwnerId)}”: someone just moved “{Name(mv.NewOwnerId)}” inside it. Your move was undone.";
                        }
                        if (++steps > 100_000) break;   // a corrupt chain; MoveRaw refuses real cycles anyway
                    }
                    owner[mv.ElementId] = mv.NewOwnerId;
                    break;
                }
            }
        return null;
    }
}
