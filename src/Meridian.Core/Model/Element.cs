using Meridian.Core.Language;

namespace Meridian.Core.Model;

/// <summary>
/// A model element. Scalar data lives in a string field bag so that the property inspector,
/// undo/redo, CSV import and every serializer can treat all fields uniformly.
/// Mutate through <see cref="ModelRepository"/> so changes are undoable and observable.
/// </summary>
public class Element
{
    private readonly Dictionary<string, string> _fields = new(StringComparer.Ordinal);
    internal readonly List<Element> Children = new();

    public Element(ElementKind kind, string? id = null)
    {
        Kind = kind;
        Id = string.IsNullOrWhiteSpace(id) ? NewId() : id;
    }

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
    public IReadOnlyList<Element> OwnedElements => Children;
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

    public Element Source { get; internal set; }
    public Element Target { get; internal set; }

    public bool Touches(Element e) => ReferenceEquals(Source, e) || ReferenceEquals(Target, e);
}
