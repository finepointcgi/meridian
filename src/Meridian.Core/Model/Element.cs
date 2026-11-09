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
    public Element? Owner { get; internal set; }
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
    }

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
    public string QualifiedName =>
        string.Join("::", AncestorsAndSelf().Reverse().Where(e => e.Kind != ElementKind.Model || e.Owner != null)
                                         .Select(e => string.IsNullOrEmpty(e.Name) ? "<unnamed>" : e.Name));

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
