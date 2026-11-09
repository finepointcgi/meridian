using System.Text.RegularExpressions;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Validation;

public enum Severity { Info, Warning, Error }

public sealed record ValidationIssue(Severity Severity, string RuleId, string Message, Element Element);

public interface IValidationRule
{
    string Id { get; }
    string Description { get; }
    IEnumerable<ValidationIssue> Check(ModelRepository model);
}

/// <summary>Active validation suite, comparable to Cameo's validation suites / EA's model validation.</summary>
public sealed partial class ModelValidator
{
    public List<IValidationRule> Rules { get; } = new()
    {
        new Rule("MER-001", "Named elements must have a name", m =>
            m.AllElements.Where(e => Metamodel.NeedsName(e.Kind) && string.IsNullOrWhiteSpace(e.Name)
                                     && !(e.Kind == ElementKind.BpmnNode && Notation.ShapeOf(e) is NodeShape.Event or NodeShape.Gateway))
                .Select(e => Issue(Severity.Error, "MER-001", $"{Metamodel.DisplayName(e.Kind)} has no name", e))),

        new Rule("MER-002", "Sibling names must be unique per kind", m =>
            m.AllElements.Where(e => e.OwnedElements.Count > 1).SelectMany(owner =>
                owner.OwnedElements.Where(c => !string.IsNullOrEmpty(c.Name) && !c.IsRelationship)
                    .GroupBy(c => (c.Kind, c.Name)).Where(g => g.Count() > 1)
                    .SelectMany(g => g.Skip(1).Select(c => Issue(Severity.Error, "MER-002", $"Duplicate name '{c.Name}' in {owner.DisplayName}", c))))),

        new Rule("MER-003", "Relationship ends must be valid for the relationship kind", m =>
            m.Relationships.Select(r => (r, problem: Metamodel.CheckEnds(r.Kind, r.Source.Kind, r.Target.Kind)))
                .Where(t => t.problem != null).Select(t => Issue(Severity.Error, "MER-003", t.problem!, t.r))),

        new Rule("MER-004", "Generalization hierarchy must not be cyclic", CheckCycles),

        new Rule("MER-005", "Typed features must reference an existing type", m =>
            m.AllElements.Where(e => Metamodel.IsTypedFeature(e.Kind) && e.TypeRef.StartsWith('_') && m.Find(e.TypeRef) == null)
                .Select(e => Issue(Severity.Error, "MER-005", $"'{e.Name}' references a type that no longer exists", e))),

        new Rule("MER-006", "Multiplicity must be well formed", m =>
            m.AllElements.Where(e => !string.IsNullOrEmpty(e.Multiplicity) && !MultiplicityRegex().IsMatch(e.Multiplicity))
                .Select(e => Issue(Severity.Error, "MER-006", $"Invalid multiplicity '{e.Multiplicity}' (expected e.g. 1, 0..1, 1..*, *)", e))),

        new Rule("REQ-001", "Requirements must have an id and text", m =>
            m.OfKind(ElementKind.Requirement).SelectMany(r => new[]
            {
                string.IsNullOrWhiteSpace(r.Get(FieldKeys.RequirementId)) ? Issue(Severity.Error, "REQ-001", $"Requirement '{r.Name}' has no id", r) : null,
                string.IsNullOrWhiteSpace(r.Get(FieldKeys.Text)) ? Issue(Severity.Warning, "REQ-001", $"Requirement '{r.Name}' has no text", r) : null,
            }).OfType<ValidationIssue>()),

        new Rule("REQ-002", "Requirement ids must be unique", m =>
            m.OfKind(ElementKind.Requirement).Where(r => !string.IsNullOrWhiteSpace(r.Get(FieldKeys.RequirementId)))
                .GroupBy(r => r.Get(FieldKeys.RequirementId)).Where(g => g.Count() > 1)
                .SelectMany(g => g.Select(r => Issue(Severity.Error, "REQ-002", $"Requirement id '{g.Key}' is used {g.Count()} times", r)))),

        new Rule("REQ-003", "Leaf requirements should be satisfied", m =>
            m.OfKind(ElementKind.Requirement)
                .Where(r => !r.OwnedElements.Any(c => c.Kind == ElementKind.Requirement))
                .Where(r => !m.Incoming(r, ElementKind.Satisfy).Any() && !m.Outgoing(r, ElementKind.DeriveReqt).Any() && !m.Incoming(r, ElementKind.DeriveReqt).Any(x => m.Incoming(x.Source, ElementKind.Satisfy).Any()))
                .Select(r => Issue(Severity.Warning, "REQ-003", $"Requirement '{r.Get(FieldKeys.RequirementId)} {r.Name}' is not satisfied by any design element", r))),

        new Rule("REQ-004", "Requirements should be verified", m =>
            m.OfKind(ElementKind.Requirement).Where(r => !r.OwnedElements.Any(c => c.Kind == ElementKind.Requirement) && !m.Incoming(r, ElementKind.Verify).Any())
                .Select(r => Issue(Severity.Info, "REQ-004", $"Requirement '{r.Get(FieldKeys.RequirementId)} {r.Name}' has no verifying test case", r))),

        new Rule("BEH-001", "State machines and activities need exactly one initial node", m =>
            m.AllElements.Where(e => e.Kind is ElementKind.StateMachine or ElementKind.Activity && e.OwnedElements.Count > 0)
                .Select(e => (e, n: e.OwnedElements.Count(c => c.Kind is ElementKind.InitialState or ElementKind.InitialNode)))
                .Where(t => t.n != 1)
                .Select(t => Issue(Severity.Warning, "BEH-001", $"'{t.e.Name}' has {t.n} initial nodes (expected 1)", t.e))),

        new Rule("BEH-002", "States should be reachable", m =>
            m.OfKind(ElementKind.State).Where(s => !m.Incoming(s, ElementKind.Transition).Any())
                .Select(s => Issue(Severity.Warning, "BEH-002", $"State '{s.Name}' has no incoming transition", s))),

        new Analysis.SuspectLinkRule(),
    };

    public IReadOnlyList<ValidationIssue> Validate(ModelRepository model) =>
        Rules.SelectMany(r => r.Check(model))
             .OrderByDescending(i => i.Severity).ThenBy(i => i.RuleId).ThenBy(i => i.Element.QualifiedName)
             .ToList();

    private static ValidationIssue Issue(Severity s, string id, string msg, Element e) => new(s, id, msg, e);

    private static IEnumerable<ValidationIssue> CheckCycles(ModelRepository m)
    {
        var reported = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        foreach (var start in m.AllElements.Where(e => Metamodel.IsClassifier(e.Kind)))
        {
            var seen = new HashSet<Element>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<Element>(m.General(start));
            while (stack.Count > 0)
            {
                var g = stack.Pop();
                if (ReferenceEquals(g, start))
                {
                    if (reported.Add(start)) yield return Issue(Severity.Error, "MER-004", $"'{start.Name}' inherits from itself", start);
                    break;
                }
                if (seen.Add(g)) foreach (var x in m.General(g)) stack.Push(x);
            }
        }
    }

    [GeneratedRegex(@"^\s*(\d+|\*)(\s*\.\.\s*(\d+|\*))?\s*$")]
    private static partial Regex MultiplicityRegex();

    private sealed class Rule(string id, string description, Func<ModelRepository, IEnumerable<ValidationIssue>> check) : IValidationRule
    {
        public string Id { get; } = id;
        public string Description { get; } = description;
        public IEnumerable<ValidationIssue> Check(ModelRepository model) => check(model);
    }
}
