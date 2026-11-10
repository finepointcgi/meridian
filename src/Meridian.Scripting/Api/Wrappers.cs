using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Scripting;

/// <summary>Thin, stable wrapper over an internal <see cref="Element"/>. All access is marshalled through the owning <see cref="ModelApi"/>.</summary>
internal class ElementWrapper(ModelApi api, Element element) : IElement
{
    internal ModelApi Api { get; } = api;
    internal Element Element { get; } = element;

    public string Id => Element.Id;
    public string Kind => Element.Kind.ToString();
    public bool IsRelationship => Element is Relationship;
    public bool IsDiagram => Element is Diagram;

    public string Name
    {
        get => Api.Run(() => Element.Name);
        set => Set(FieldKeys.Name, value);
    }

    public string Documentation
    {
        get => Api.Run(() => Element.Documentation);
        set => Set(FieldKeys.Documentation, value);
    }

    public string QualifiedName => Api.Run(() => Element.QualifiedName);
    public IElement? Owner => Api.Run(() => Api.WrapOrNull(Element.Owner));
    public IReadOnlyList<IElement> Children => Api.Run(() => Element.OwnedElements.Select(Api.Wrap).ToList());
    public IReadOnlyList<IElement> Descendants => Api.Run(() => Element.Descendants().Select(Api.Wrap).ToList());
    public IReadOnlyDictionary<string, string> Fields => Api.Run(() => new Dictionary<string, string>(Element.Fields));

    public string Get(string field) => Api.Run(() => Element.Get(field));
    public void Set(string field, string? value) => Api.Edit(() => Api.Repository.Set(Element, field, value));

    public IReadOnlyList<string> Stereotypes => Api.Run(() => Element.Stereotypes.ToList());
    public bool HasStereotype(string name) => Stereotypes.Contains(name, StringComparer.OrdinalIgnoreCase);

    public void AddStereotype(string name)
    {
        var list = Stereotypes.ToList();
        if (list.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        list.Add(name);
        Set(FieldKeys.Stereotypes, string.Join(",", list));
    }

    public void RemoveStereotype(string name)
    {
        var list = Stereotypes.Where(s => !string.Equals(s, name, StringComparison.OrdinalIgnoreCase)).ToList();
        Set(FieldKeys.Stereotypes, list.Count == 0 ? null : string.Join(",", list));
    }

    public IReadOnlyDictionary<string, string> TaggedValues => Api.Run(() => Element.TaggedValues.ToDictionary(kv => kv.Key, kv => kv.Value));
    public string GetTag(string tag) => Get(FieldKeys.TagPrefix + tag);
    public void SetTag(string tag, string? value) => Set(FieldKeys.TagPrefix + tag, value);

    public IReadOnlyList<IRelationship> Relationships =>
        Api.Run(() => Api.Repository.RelationshipsOf(Element).Select(r => (IRelationship)Api.Wrap(r)).ToList());

    public IReadOnlyList<IRelationship> Outgoing(string? kind = null)
    {
        ElementKind? k = kind is null ? null : ModelApi.ParseKind(kind);
        return Api.Run(() => Api.Repository.Outgoing(Element, k).Select(r => (IRelationship)Api.Wrap(r)).ToList());
    }

    public IReadOnlyList<IRelationship> Incoming(string? kind = null)
    {
        ElementKind? k = kind is null ? null : ModelApi.ParseKind(kind);
        return Api.Run(() => Api.Repository.Incoming(Element, k).Select(r => (IRelationship)Api.Wrap(r)).ToList());
    }

    public void Delete() => Api.Delete(this);

    public override bool Equals(object? obj) => obj is ElementWrapper w && ReferenceEquals(w.Element, Element);
    public override int GetHashCode() => Element.Id.GetHashCode(StringComparison.Ordinal);
    public override string ToString() => $"{Kind} '{Element.Name}'";
}

internal sealed class RelationshipWrapper(ModelApi api, Relationship relationship) : ElementWrapper(api, relationship), IRelationship
{
    public IElement Source => Api.Run(() => Api.Wrap(relationship.Source));
    public IElement Target => Api.Run(() => Api.Wrap(relationship.Target));
    public override string ToString() => $"{Kind} {relationship.Source.Name} -> {relationship.Target.Name}";
}

internal sealed class DiagramWrapper(ModelApi api, Diagram diagram) : ElementWrapper(api, diagram), IDiagram
{
    public string DiagramType => diagram.DiagramType.ToString();
    public IReadOnlyList<IElement> Elements => Api.Run(() => diagram.Nodes.Select(n => Api.Wrap(n.Element)).ToList());
    public bool Shows(IElement element) => Api.Run(() => diagram.Shows(ModelApi.Unwrap(element)));

    public void Show(IElement element, double x = 40, double y = 40)
    {
        var e = ModelApi.Unwrap(element);
        Api.Edit(() =>
        {
            if (diagram.Shows(e)) return;
            var (w, h) = Metamodel.DefaultSize(e.Kind);
            Api.Repository.AddNode(diagram, e, new Bounds(x, y, w, h));
        });
    }
}
