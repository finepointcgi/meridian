using System.Text.Json.Nodes;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>Element JSON in the shape of the OMG Systems Modeling API (SysML v2 REST/HTTP PSM), read-only subset.</summary>
public static class SysMLv2Json
{
    public static string TypeOf(Element e) => e switch
    {
        Diagram => "Element",
        Relationship r => r.Kind switch
        {
            ElementKind.Satisfy => "SatisfyRequirementUsage",
            ElementKind.Generalization => "Subclassification",
            ElementKind.Composition or ElementKind.Aggregation or ElementKind.Association => "Association",
            ElementKind.Connector => "ConnectionUsage",
            ElementKind.Binding => "BindingConnectorAsUsage",
            ElementKind.Allocate => "AllocationUsage",
            ElementKind.Transition => "TransitionUsage",
            ElementKind.ControlFlow or ElementKind.ObjectFlow => "SuccessionAsUsage",
            ElementKind.Message => "FlowConnectionUsage",
            _ => "Dependency",
        },
        _ => e.Kind switch
        {
            ElementKind.Model or ElementKind.Package or ElementKind.Profile => "Package",
            ElementKind.Block or ElementKind.Class or ElementKind.Component or ElementKind.Actor => "PartDefinition",
            ElementKind.InterfaceBlock or ElementKind.Interface => "PortDefinition",
            ElementKind.ValueType or ElementKind.DataType => "AttributeDefinition",
            ElementKind.Enumeration => "EnumerationDefinition",
            ElementKind.EnumerationLiteral => "EnumerationUsage",
            ElementKind.ConstraintBlock => "ConstraintDefinition",
            ElementKind.Requirement => "RequirementDefinition",
            ElementKind.TestCase => "VerificationCaseDefinition",
            ElementKind.UseCase => "UseCaseDefinition",
            ElementKind.Activity => "ActionDefinition",
            ElementKind.Action => "ActionUsage",
            ElementKind.StateMachine => "StateDefinition",
            ElementKind.State => "StateUsage",
            ElementKind.Interaction => "OccurrenceDefinition",
            ElementKind.Port => "PortUsage",
            ElementKind.Property => "AttributeUsage",
            ElementKind.Comment => "Comment",
            _ => "Element",
        },
    };

    private static JsonObject Ref(Element e) => new() { ["@id"] = e.Id };

    public static JsonObject Element(ModelRepository m, Element e)
    {
        var type = TypeOf(e);
        if (e.Kind == ElementKind.Property && m.ResolveType(e)?.Kind is ElementKind.Block or ElementKind.Class or ElementKind.Component) type = "PartUsage";
        var o = new JsonObject
        {
            ["@id"] = e.Id,
            ["@type"] = type,
            ["elementId"] = e.Id,
            ["declaredName"] = string.IsNullOrEmpty(e.Name) ? null : e.Name,
            ["name"] = string.IsNullOrEmpty(e.Name) ? null : e.Name,
            ["qualifiedName"] = e.QualifiedName,
            ["owner"] = e.Owner is null ? null : Ref(e.Owner),
            ["ownedElement"] = new JsonArray(e.OwnedElements.Select(c => (JsonNode)Ref(c)).ToArray()),
            ["documentation"] = string.IsNullOrEmpty(e.Documentation) ? new JsonArray() : new JsonArray(new JsonObject { ["@type"] = "Documentation", ["body"] = e.Documentation }),
            ["isAbstract"] = e.IsAbstract,
            ["meridianKind"] = e.Kind.ToString(),
        };
        if (e.Kind == ElementKind.Requirement)
        {
            o["reqId"] = e.Get(FieldKeys.RequirementId);
            o["text"] = new JsonArray(e.Get(FieldKeys.Text));
        }
        if (Metamodel.IsTypedFeature(e.Kind) && m.ResolveType(e) is { } t) o["type"] = new JsonArray(Ref(t));
        if (e is Relationship r)
        {
            o["source"] = new JsonArray(Ref(r.Source));
            o["target"] = new JsonArray(Ref(r.Target));
        }
        return o;
    }
}
