using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Scripting;
using Meridian.Serialization;

namespace Meridian.Assistant.Changes;

public enum ProposalKind { CreateElement, UpdateFields, CreateRelationship, DeleteElement, CreateDiagram, ApplySysml }

public enum DiffKind { Added, Changed, Removed }

public enum ChangeSetState { Pending, Applied, Discarded, Failed }

/// <summary>One field edit in an <see cref="ProposalKind.UpdateFields"/> proposal, with the value it replaces.</summary>
public sealed record FieldChange(string Key, string? Old, string? New);

/// <summary>
/// A single change the assistant proposed through a write tool. Nothing touches the model until the user applies the
/// <see cref="ChangeSet"/>. Elements created by earlier proposals are referenced as <c>new:N</c> (<see cref="Ref"/>).
/// </summary>
public sealed class Proposal
{
    public required int Number { get; init; }
    public required ProposalKind Kind { get; init; }

    /// <summary><c>new:N</c> for proposals that create something, else null.</summary>
    public string? Ref { get; init; }

    public ElementKind? ElementKind { get; init; }
    public DiagramType? DiagramType { get; init; }
    public string? Name { get; init; }

    /// <summary>Owner of a new element or diagram, or the scope package of a SysML text edit.</summary>
    public string? OwnerRef { get; init; }

    /// <summary>The element updated or deleted.</summary>
    public string? SubjectRef { get; init; }

    public string? SourceRef { get; init; }
    public string? TargetRef { get; init; }

    /// <summary>Elements shown on a proposed diagram.</summary>
    public IReadOnlyList<string> ElementRefs { get; init; } = [];

    public IReadOnlyList<KeyValuePair<string, string?>> Fields { get; init; } = [];
    public IReadOnlyList<FieldChange> FieldChanges { get; init; } = [];

    public string? SysmlText { get; init; }

    /// <summary>What a SysML text edit would change (a dry run on a copy of the model).</summary>
    public IReadOnlyList<string> Preview { get; init; } = [];

    /// <summary>Suggested trace links: 0..1 confidence and the assistant's reason.</summary>
    public double? Confidence { get; init; }
    public string? Reason { get; init; }

    /// <summary>Proposed diagrams: the one-line purpose of the view (stored as the diagram's documentation).</summary>
    public string? Purpose { get; init; }

    /// <summary>One-line, human readable description shown in the diff card.</summary>
    public required string Title { get; init; }

    public DiffKind Diff => Kind switch
    {
        ProposalKind.CreateElement or ProposalKind.CreateRelationship or ProposalKind.CreateDiagram => DiffKind.Added,
        ProposalKind.DeleteElement => DiffKind.Removed,
        _ => DiffKind.Changed,
    };

    /// <summary>The user's per-item choice (accept / reject) before applying.</summary>
    public bool Accepted { get; set; } = true;

    /// <summary>Every <c>new:N</c> reference this proposal depends on.</summary>
    public IEnumerable<string> Dependencies =>
        new[] { OwnerRef, SubjectRef, SourceRef, TargetRef }.Concat(ElementRefs)
            .OfType<string>().Where(ChangeSet.IsNewRef);
}

public sealed record ApplyResult(bool Ok, string Message, IReadOnlyDictionary<string, string> CreatedIds);

/// <summary>
/// The write-tool proposals of one assistant turn, shown as a reviewable diff card. <see cref="Apply"/> runs every accepted
/// proposal as ONE undoable step through the model repository (and therefore through the collaboration replication of
/// every local operation); if any proposal fails the whole step is rolled back.
/// </summary>
public sealed class ChangeSet
{
    private readonly List<Proposal> _items = new();

    public ChangeSet(int id) => Id = id;

    public int Id { get; }
    public IReadOnlyList<Proposal> Items => _items;
    public ChangeSetState State { get; private set; } = ChangeSetState.Pending;
    public string? Outcome { get; private set; }

    /// <summary>After <see cref="Apply"/>: <c>new:N</c> → id of the created element.</summary>
    public IReadOnlyDictionary<string, string> CreatedIds { get; private set; } = new Dictionary<string, string>();

    public bool IsEmpty => _items.Count == 0;
    public int Added => _items.Count(p => p.Diff == DiffKind.Added);
    public int Changed => _items.Count(p => p.Diff == DiffKind.Changed);
    public int Removed => _items.Count(p => p.Diff == DiffKind.Removed);

    public static bool IsNewRef(string? r) => r != null && r.StartsWith("new:", StringComparison.Ordinal);

    internal void Add(Proposal p) => _items.Add(p);

    public Proposal? Creating(string? newRef) => newRef == null ? null : _items.FirstOrDefault(p => p.Ref == newRef);

    /// <summary>Accepted, and everything it depends on (new elements it refers to) is accepted too.</summary>
    public bool IsEffective(Proposal p) => IsEffective(p, 0);

    private bool IsEffective(Proposal p, int depth) =>
        p.Accepted && depth < 64 && p.Dependencies.All(d => Creating(d) is { } dep && IsEffective(dep, depth + 1));

    public void Discard()
    {
        if (State != ChangeSetState.Pending) return;
        State = ChangeSetState.Discarded;
        Outcome = "Discarded";
    }

    public string Description => $"Assistant: {_items.Count(p => IsEffective(p))} change{(_items.Count(p => IsEffective(p)) == 1 ? "" : "s")}";

    /// <summary>Applies every effective proposal as one undo step. Nothing is applied when the model is view-only.</summary>
    public ApplyResult Apply(ModelRepository model)
    {
        if (State != ChangeSetState.Pending) return new ApplyResult(false, "This change set was already " + State.ToString().ToLowerInvariant() + ".", CreatedIds);
        if (model.History.ReadOnly) return new ApplyResult(false, "This project is view-only for you: the assistant's changes cannot be applied.", CreatedIds);
        var items = _items.Where(p => IsEffective(p)).ToList();
        var skipped = _items.Count(p => p.Accepted) - items.Count;
        if (items.Count == 0) return new ApplyResult(false, "Nothing to apply: every change was rejected.", CreatedIds);

        var api = new ModelApi(model);
        var created = new Dictionary<string, Element>(StringComparer.Ordinal);
        var description = Description;
        Exception? failure = null;
        Proposal? failedAt = null;
        using (model.History.Transaction(description))
        {
            foreach (var p in items)
            {
                try
                {
                    ApplyOne(model, api, p, created);
                }
                catch (Exception e) when (e is InvalidOperationException or ArgumentException or KeyNotFoundException)
                {
                    failure = e;
                    failedAt = p;
                    break;
                }
            }
        }

        if (failure != null)
        {
            // Roll the whole step back so the model never keeps half a change set.
            // (the composite step carries this exact description instance, so this never undoes someone else's step).
            if (ReferenceEquals(model.History.UndoDescription, description)) model.History.Undo();
            State = ChangeSetState.Failed;
            Outcome = $"Not applied: change {failedAt!.Number} ({failedAt.Title}) failed: {failure.Message}";
            return new ApplyResult(false, Outcome, CreatedIds);
        }

        State = ChangeSetState.Applied;
        CreatedIds = created.ToDictionary(kv => kv.Key, kv => kv.Value.Id, StringComparer.Ordinal);
        Outcome = $"Applied {items.Count} change{(items.Count == 1 ? "" : "s")} as one undo step" + (skipped > 0 ? $" ({skipped} skipped: they depend on rejected changes)" : "");
        return new ApplyResult(true, Outcome, CreatedIds);
    }

    private static Element Resolve(ModelRepository m, string? r, Dictionary<string, Element> created) =>
        (IsNewRef(r) ? created.GetValueOrDefault(r!) : m.Find(r))
        ?? throw new KeyNotFoundException($"Element '{r}' no longer exists.");

    private static void ApplyOne(ModelRepository m, ModelApi api, Proposal p, Dictionary<string, Element> created)
    {
        switch (p.Kind)
        {
            case ProposalKind.CreateElement:
            {
                var owner = Resolve(m, p.OwnerRef, created);
                var e = m.Create(owner, p.ElementKind!.Value, string.IsNullOrWhiteSpace(p.Name) ? null : p.Name);
                foreach (var (k, v) in p.Fields) m.Set(e, k, v);
                created[p.Ref!] = e;
                break;
            }
            case ProposalKind.UpdateFields:
            {
                var e = Resolve(m, p.SubjectRef, created);
                var w = api.Get(e.Id) ?? throw new KeyNotFoundException($"Element '{p.SubjectRef}' no longer exists.");
                foreach (var (k, v) in p.Fields) api.Set(w, k, v);
                break;
            }
            case ProposalKind.CreateRelationship:
            {
                var r = m.Connect(p.ElementKind!.Value, Resolve(m, p.SourceRef, created), Resolve(m, p.TargetRef, created));
                foreach (var (k, v) in p.Fields) m.Set(r, k, v);
                created[p.Ref!] = r;
                break;
            }
            case ProposalKind.DeleteElement:
            {
                var e = Resolve(m, p.SubjectRef, created);
                if (api.Get(e.Id) is { } w) api.Delete(w);
                break;
            }
            case ProposalKind.CreateDiagram:
            {
                var owner = Resolve(m, p.OwnerRef, created);
                if (AutoDiagrammer.SupportedTypes.Contains(p.DiagramType!.Value))
                {
                    // The auto-diagrammer's layout: layered / grid, nesting, ports on the facing side, no overlaps.
                    var elements = p.ElementRefs.Select(r => Resolve(m, r, created)).ToList();
                    created[p.Ref!] = AutoDiagrammer.Build(m, new PlannedDiagram
                    {
                        Type = p.DiagramType.Value, Owner = owner, Name = p.Name ?? "", Elements = elements, Purpose = p.Purpose ?? "",
                    });
                    break;
                }
                var d = m.CreateDiagram(owner, p.DiagramType!.Value, string.IsNullOrWhiteSpace(p.Name) ? null : p.Name);
                var x = 40.0;
                foreach (var r in p.ElementRefs)
                {
                    var e = Resolve(m, r, created);
                    if (e is Relationship or Diagram || d.NodeFor(e) != null) continue;
                    var (w, h) = Notation.DefaultSizeOf(e);
                    m.AddNode(d, e, new Bounds(x, 40, w, h));
                    x += w + 40;
                }
                // AddNode already drew the paths of relationships between shown elements.
                if (d.Nodes.Any()) m.SetBounds(d, AutoLayout.Layered(d), "Arrange");
                created[p.Ref!] = d;
                break;
            }
            case ProposalKind.ApplySysml:
            {
                var scope = Resolve(m, p.OwnerRef, created);
                var doc = SysMLv2Parser.Parse(p.SysmlText ?? "");
                if (doc.HasErrors) throw new InvalidOperationException("The SysML v2 text no longer parses.");
                var result = SysMLv2Sync.Apply(m, scope, doc, new SysmlSyncOptions { Description = "Assistant: SysML v2 text" });
                if (!result.Applied) throw new InvalidOperationException("The SysML v2 text could not be applied.");
                break;
            }
        }
    }
}
