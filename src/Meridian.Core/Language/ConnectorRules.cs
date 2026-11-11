using Meridian.Core.Model;

namespace Meridian.Core.Language;

/// <summary>
/// A relationship kind proposed for a line drawn from one element to another. <see cref="Reverse"/> means the relationship
/// points the other way from the gesture (drawing from a requirement to a block makes «satisfy» block → requirement).
/// </summary>
public sealed record ConnectorChoice(ElementKind Kind, bool Reverse = false, string? Subtype = null)
{
    /// <summary>«keyword» or the kind's display name, for chips and menus.</summary>
    public string Label => Subtype != null ? Subtypes.Display(Subtype) : Metamodel.Style(Kind).Keyword is { } kw ? $"«{kw}»" : Metamodel.DisplayName(Kind);
}

/// <summary>
/// Which connector a line between two elements should be, from the diagram type and the two element kinds: actor–use case
/// association, use case include, design element «satisfy» requirement, requirement «deriveReqt», test case «verify», block
/// composition on a BDD, port connectors on an IBD, state transitions, action control flows, dependencies on packages,
/// behaviour «allocate» structure… The first choice is the default; the others are offered to switch to in one click.
/// Every choice is valid for <see cref="Metamodel.CheckEnds"/>.
/// </summary>
public static class ConnectorRules
{
    /// <summary>The connector a line from <paramref name="source"/> to <paramref name="target"/> becomes, or null when none fits.</summary>
    public static ConnectorChoice? Default(DiagramType diagram, ElementKind source, ElementKind target) =>
        Candidates(diagram, source, target).FirstOrDefault();

    /// <summary>Valid connectors between the two kinds, most fitting first, without duplicates.</summary>
    public static IReadOnlyList<ConnectorChoice> Candidates(DiagramType diagram, ElementKind source, ElementKind target)
    {
        var list = new List<ConnectorChoice>();
        void Add(ElementKind kind, bool reverse = false, string? subtype = null)
        {
            var (s, t) = reverse ? (target, source) : (source, target);
            if (Metamodel.CheckEnds(kind, s, t) != null) return;
            if (list.Any(c => c.Kind == kind && c.Reverse == reverse && c.Subtype == subtype)) return;
            list.Add(new ConnectorChoice(kind, reverse, subtype));
        }
        static bool Req(ElementKind k) => k == ElementKind.Requirement;
        static bool Behavior(ElementKind k) => k is ElementKind.Action or ElementKind.Activity || Metamodel.IsActivityNode(k) && k is not (ElementKind.InputPin or ElementKind.OutputPin or ElementKind.ObjectNode);
        static bool Structure(ElementKind k) => k is ElementKind.Block or ElementKind.Component or ElementKind.Class or ElementKind.Property or ElementKind.Actor;
        static bool PartOrPort(ElementKind k) => k is ElementKind.Property or ElementKind.Port;
        static bool ObjectEnd(ElementKind k) => k is ElementKind.ObjectNode or ElementKind.InputPin or ElementKind.OutputPin;

        // Notations with their own connector family come first: their palette's first valid link (with its subtype).
        if (diagram is DiagramType.Bpmn or DiagramType.ArchiMate || UafViewpoints.IsUaf(diagram) ||
            source is ElementKind.BpmnNode or ElementKind.BpmnPool or ElementKind.ArchiElement)
            foreach (var l in Metamodel.PaletteLinks(diagram).Where(l => l.Kind != ElementKind.Allocate))
                Add(l.Kind, false, l.Subtype);

        // Deployment: node ↔ node communication path, artifact → node «deploy», artifact → block / component / package «manifest»
        // (a database or schema package → node is a «deploy» first on deployment diagrams).
        static bool Node(ElementKind k) => Deployment.DeploymentModel.IsNode(k);
        static bool Artifact(ElementKind k) => Deployment.DeploymentModel.IsArtifact(k);
        if (Node(source) && Node(target)) Add(ElementKind.CommunicationPath);
        if ((Artifact(source) || source == ElementKind.Package && diagram == DiagramType.Deployment) && Node(target)) Add(ElementKind.Deployment);
        if (Node(source) && (Artifact(target) || target == ElementKind.Package && diagram == DiagramType.Deployment)) Add(ElementKind.Deployment, reverse: true);
        if (Artifact(source) && !Node(target)) Add(ElementKind.Manifestation);
        if (Artifact(target) && !Node(source)) Add(ElementKind.Manifestation, reverse: true);

        // Packages: dependency.
        if (source == ElementKind.Package || target == ElementKind.Package) Add(ElementKind.Dependency);
        if (source == ElementKind.Package && Node(target)) Add(ElementKind.Deployment);
        if (Node(source) && target == ElementKind.Package) Add(ElementKind.Deployment, reverse: true);

        // Requirements.
        if (Req(source) && Req(target)) { Add(ElementKind.DeriveReqt); Add(ElementKind.Refine); Add(ElementKind.Copy); Add(ElementKind.Trace); }
        if (source == ElementKind.TestCase && Req(target)) Add(ElementKind.Verify);
        if (Req(source) && target == ElementKind.TestCase) Add(ElementKind.Verify, reverse: true);
        if (Req(target) && !Req(source)) { Add(ElementKind.Satisfy); Add(ElementKind.Verify); Add(ElementKind.Refine); Add(ElementKind.Trace); }
        if (Req(source) && !Req(target)) { Add(ElementKind.Satisfy, true); Add(ElementKind.Verify, true); Add(ElementKind.Refine, true); Add(ElementKind.Trace); }

        // Use cases.
        if (source is ElementKind.Actor && target is ElementKind.UseCase || source is ElementKind.UseCase && target is ElementKind.Actor) Add(ElementKind.Association);
        if (source == ElementKind.UseCase && target == ElementKind.UseCase) { Add(ElementKind.Include); Add(ElementKind.Extend); Add(ElementKind.Generalization); }

        // Interactions.
        if (source == ElementKind.Lifeline && target == ElementKind.Lifeline)
            Add(diagram == DiagramType.Communication ? ElementKind.CommunicationLink : ElementKind.Message);
        if (source is ElementKind.CommunicationLifeline or ElementKind.Lifeline && target is ElementKind.CommunicationLifeline or ElementKind.Lifeline) Add(ElementKind.CommunicationLink);

        // Internal structure: connectors between parts and ports (binding connectors on parametric diagrams).
        if (PartOrPort(source) && PartOrPort(target))
        {
            if (diagram == DiagramType.Parametric) Add(ElementKind.Binding);
            Add(ElementKind.Connector);
            Add(ElementKind.Binding);
        }

        // Behaviour.
        if (Metamodel.IsVertex(source) && Metamodel.IsVertex(target)) Add(ElementKind.Transition);
        if (Metamodel.IsActivityNode(source) && Metamodel.IsActivityNode(target))
        {
            if (ObjectEnd(source) || ObjectEnd(target)) Add(ElementKind.ObjectFlow);
            Add(ElementKind.ControlFlow);
            Add(ElementKind.ObjectFlow);
        }
        // Behaviour allocated to structure (swimlane-style allocation), whichever way the line was drawn.
        if (Behavior(source) && Structure(target)) Add(ElementKind.Allocate);
        if (Structure(source) && Behavior(target) && !PartOrPort(source)) Add(ElementKind.Allocate, reverse: true);

        // Classifiers.
        if (target is ElementKind.Interface && Metamodel.IsClassifier(source)) Add(ElementKind.Realization);
        // Data models: a foreign key between tables; a table realizes the logical entity it implements; data items are allocated to tables.
        if (source == ElementKind.Table && target == ElementKind.Table) Add(ElementKind.ForeignKey);
        if (source == ElementKind.Table && target is ElementKind.Class or ElementKind.Block) { Add(ElementKind.Realization); Add(ElementKind.Trace); }
        if (source is ElementKind.Class or ElementKind.Block && target == ElementKind.Table) { Add(ElementKind.Realization, reverse: true); Add(ElementKind.Allocate); Add(ElementKind.Trace); }
        if (Metamodel.IsStructuralClassifier(source) && Metamodel.IsStructuralClassifier(target))
        {
            if (diagram == DiagramType.BlockDefinition && source is not ElementKind.Actor && target is not ElementKind.Actor) Add(ElementKind.Composition);
            Add(ElementKind.Association);
            Add(ElementKind.Composition);
            Add(ElementKind.Aggregation);
            Add(ElementKind.Generalization);
            Add(ElementKind.Realization);
        }

        // Other notations with one connector kind.
        Add(ElementKind.ForeignKey);
        Add(ElementKind.FeatureDecomposition);
        Add(ElementKind.Requires);
        Add(ElementKind.Excludes);
        Add(ElementKind.BpmnFlow, false, source is ElementKind.BpmnNode or ElementKind.BpmnPool ? "sequence" : null);
        Add(ElementKind.ArchiRelation);

        // Whatever else this diagram's palette offers, then the any-to-any links.
        foreach (var l in Metamodel.PaletteLinks(diagram).Where(l => l.Kind != ElementKind.Allocate)) Add(l.Kind, false, l.Subtype);
        if (!IsSpecialized(source) && !IsSpecialized(target))
        {
            Add(ElementKind.Dependency);
            Add(ElementKind.Trace);
            Add(ElementKind.Refine);
            Add(ElementKind.Allocate);
        }
        return list;
    }

    /// <summary>Elements whose notation has its own connectors only (no generic dependency/trace lines).</summary>
    private static bool IsSpecialized(ElementKind k) => k is ElementKind.BpmnNode or ElementKind.BpmnPool or ElementKind.BpmnLane or ElementKind.ArchiElement
        or ElementKind.Feature or ElementKind.Lifeline or ElementKind.CommunicationLifeline or ElementKind.TimingLifeline
        or ElementKind.InitialNode or ElementKind.ActivityFinalNode or ElementKind.DecisionNode or ElementKind.ForkNode or ElementKind.InitialState or ElementKind.FinalState;
}
