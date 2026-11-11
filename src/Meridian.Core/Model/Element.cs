using Meridian.Core.Language;

namespace Meridian.Core.Model;

/// <summary>
/// A model element. Scalar data lives in a string field bag so that the property inspector,
/// undo/redo, CSV import and every serializer can treat all fields uniformly.
/// Mutate through <see cref="ModelRepository"/> so changes are undoable and observable.
/// </summary>
public class Element
{
    // Copy-on-write: a replica made by ModelRepository.CreateReplica shares this dictionary with the element it was copied
    // from (both are flagged), and whichever side writes first takes its own copy. A shared dictionary is never mutated, so
    // replicas on other threads may read it while the original is being edited.
    private Dictionary<string, string> _fields;
    private bool _fieldsShared;
    // Most elements are leaves (94 % at Cameo scale), so the child list only exists once there is a child.
    private List<Element>? _children;

    /// <summary>The owned elements (read-only view; change them through <see cref="ChildList"/>).</summary>
    internal ChildView Children => new(_children);

    /// <summary>The owned elements, for changing them (created on first use).</summary>
    internal List<Element> ChildList => _children ??= new List<Element>();

    public Element(ElementKind kind, string? id = null)
    {
        Kind = kind;
        Id = string.IsNullOrWhiteSpace(id) ? NewId() : id;
        _fields = new(StringComparer.Ordinal);
    }

    /// <summary>A copy of <paramref name="source"/>'s identity and fields (shared copy-on-write), without owner or children.</summary>
    private protected Element(Element source)
    {
        Kind = source.Kind;
        Id = source.Id;
        source._fieldsShared = true;
        _fields = source._fields;
        _fieldsShared = true;
    }

    /// <summary>A shallow replica of this element (see <see cref="ModelRepository.CreateReplica"/>).</summary>
    internal virtual Element CopyForReplica() => new(this);

    /// <summary>Sets the owner while building a replica (no name-cache invalidation: the copy has no cached names yet).</summary>
    internal void SetOwnerForReplica(Element owner) => _owner = owner;

    public static string NewId() => "_" + Guid.NewGuid().ToString("N");

    public string Id { get; }
    public ElementKind Kind { get; }
    public Element? Owner
    {
        get => _owner;
        internal set
        {
            if (ReferenceEquals(_owner, value)) return;
            _owner = value;
            InvalidateNames();
        }
    }
    private Element? _owner;
    public IReadOnlyList<Element> OwnedElements => (IReadOnlyList<Element>?)_children ?? Array.Empty<Element>();
    public IReadOnlyDictionary<string, string> Fields => _fields;

    public string Name => Get(FieldKeys.Name);
    public string Documentation => Get(FieldKeys.Documentation);
    public string TypeRef => Get(FieldKeys.Type);
    public string Multiplicity => Get(FieldKeys.Multiplicity);
    public bool IsAbstract => Get(FieldKeys.IsAbstract) == "true";

    public IReadOnlyList<string> Stereotypes =>
        Get(FieldKeys.Stereotypes).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public IEnumerable<KeyValuePair<string, string>> TaggedValues =>
        _fields.Where(f => f.Key.StartsWith(FieldKeys.TagPrefix, StringComparison.Ordinal))
               .Select(f => new KeyValuePair<string, string>(f.Key[FieldKeys.TagPrefix.Length..], f.Value));

    public string Get(string key) => _fields.TryGetValue(key, out var v) ? v : string.Empty;

    /// <summary>Raw write without undo; used by commands and loaders.</summary>
    internal void SetRaw(string key, string? value)
    {
        if (_fieldsShared)
        {
            if (string.IsNullOrEmpty(value) ? !_fields.ContainsKey(key) : _fields.TryGetValue(key, out var same) && same == value) return;
            _fields = new Dictionary<string, string>(_fields, StringComparer.Ordinal);
            _fieldsShared = false;
        }
        if (string.IsNullOrEmpty(value)) _fields.Remove(key);
        else _fields[key] = value;
        if (key == FieldKeys.Name) InvalidateNames();
        else if (key == FieldKeys.RequirementId) Interlocked.Increment(ref _requirementIdGeneration);
    }

    private static int _requirementIdGeneration;
    /// <summary>Bumped whenever any element's requirement id changes (lets repositories cache the highest id number).</summary>
    internal static int RequirementIdGeneration => Volatile.Read(ref _requirementIdGeneration);

    // Qualified names are cached per element. Any rename or move anywhere bumps a global generation, which
    // invalidates every cached name at once (renames and moves are rare next to reads; a stale name is never served).
    private static int _nameGeneration;
    private sealed record NameCache(int Generation, string Value);
    private NameCache? _qualifiedName;

    private static void InvalidateNames() => Interlocked.Increment(ref _nameGeneration);

    public bool IsRelationship => Metamodel.IsRelationship(Kind);

    public IEnumerable<Element> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    public IEnumerable<Element> AncestorsAndSelf()
    {
        for (var e = this; e != null; e = e.Owner) yield return e;
    }

    public bool IsAncestorOf(Element other) => other.AncestorsAndSelf().Skip(1).Contains(this);

    /// <summary>Package-qualified name, e.g. "Vehicle::Powertrain::Engine".</summary>
    public string QualifiedName
    {
        get
        {
            var generation = Volatile.Read(ref _nameGeneration);
            if (_qualifiedName is { } c && c.Generation == generation) return c.Value;
            // Built from the owner's (cached) name, so a whole subtree costs O(n) rather than O(n · depth).
            var own = string.IsNullOrEmpty(Name) ? "<unnamed>" : Name;
            string value;
            if (Owner == null) value = Kind == ElementKind.Model ? "" : own;
            else
            {
                var prefix = Owner.QualifiedName;
                value = prefix.Length == 0 ? own : prefix + "::" + own;
            }
            _qualifiedName = new NameCache(generation, value);
            return value;
        }
    }

    public string DisplayName => string.IsNullOrEmpty(Name) ? $"<{Metamodel.DisplayName(Kind)}>" : Name;

    public override string ToString() => $"{Kind} '{Name}' ({Id})";
}

public sealed class Relationship : Element
{
    public Relationship(ElementKind kind, Element source, Element target, string? id = null) : base(kind, id)
    {
        if (!Metamodel.IsRelationship(kind)) throw new ArgumentException($"{kind} is not a relationship kind", nameof(kind));
        Source = source;
        Target = target;
    }

    private Relationship(Relationship source) : base(source)
    {
        Source = source.Source;   // re-pointed at the replica's elements by ModelRepository.CreateReplica
        Target = source.Target;
    }

    internal override Element CopyForReplica() => new Relationship(this);

    public Element Source { get; internal set; }
    public Element Target { get; internal set; }

    public bool Touches(Element e) => ReferenceEquals(Source, e) || ReferenceEquals(Target, e);
}

/// <summary>A read-only, allocation-free view of an element's children (see <see cref="Element.ChildList"/> to change them).</summary>
internal readonly struct ChildView(List<Element>? list) : IReadOnlyList<Element>
{
    private static readonly List<Element> None = new(0);

    public int Count => list?.Count ?? 0;
    public Element this[int index] => (list ?? None)[index];
    public int IndexOf(Element e) => list?.IndexOf(e) ?? -1;
    public bool Contains(Element e) => list?.Contains(e) ?? false;
    public List<Element>.Enumerator GetEnumerator() => (list ?? None).GetEnumerator();
    IEnumerator<Element> IEnumerable<Element>.GetEnumerator() => GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
