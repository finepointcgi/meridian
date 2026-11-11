namespace Meridian.Core.Model;

/// <summary>
/// The exact inverses of the primitive mutations a <see cref="ModelRepository"/> made while this journal was recording
/// (<see cref="ModelRepository.Journal"/>), plus the objects those mutations attached or inserted. Collaboration records one per
/// local user action that the server has not confirmed yet, so the action can be taken back (rebase, rejection) and re-applied
/// on the same objects. Unlike undo history it is structural: running the inverses newest first restores the model exactly.
/// </summary>
internal sealed class RawJournal
{
    internal readonly List<Action> Inverses = new();
    internal readonly Dictionary<string, Element> Elements = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, DiagramNode> Nodes = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, DiagramEdge> Edges = new(StringComparer.Ordinal);

    public bool IsEmpty => Inverses.Count == 0;
}
