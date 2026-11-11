using Meridian.Core.Model;

namespace Meridian.Core.Language;

public enum ArrowHead { None, Open, HollowTriangle, FilledDiamond, HollowDiamond, Filled, Dot, CrowsFoot, Bar, HollowDot }

public sealed record EdgeStyle(bool Dashed, ArrowHead TargetHead, ArrowHead SourceHead, string? Keyword, bool Dotted = false);

public enum NodeShape
{
    Rectangle, Rounded, Ellipse, StickFigure, Folder, Note, FilledCircle, BullsEye, Diamond, Bar, SmallSquare, Lifeline, Fragment,
    Swimlane, Event, Gateway, DataObject, Pool,
    // Appended: timing, interaction overview
    Timeline, TimeMarker, DurationMarker, InteractionUse, InlineInteraction,
    // Appended: history, entry / exit point and terminate pseudostates (drawn by subtype)
    Pseudostate,
}

/// <summary>A palette entry: an element or relationship kind, optionally with a subtype (BPMN, ArchiMate…).</summary>
/// <param name="Stereotype">Stereotype applied to elements created from this entry (UAF viewpoints).</param>
public sealed record PaletteItem(ElementKind Kind, string? Subtype = null, string? Label = null, string? Stereotype = null)
{
    public string Title => Label ?? (Subtype != null ? Subtypes.Display(Subtype) : Metamodel.DisplayName(Kind));
}

/// <summary>
/// The language definition: which kinds exist, how they nest, which relationships may connect what,
/// what each diagram's palette offers, and how each kind is drawn.
/// </summary>
public static class Metamodel
{
    private static readonly HashSet<ElementKind> Relationships = new()
    {
        ElementKind.Association, ElementKind.Composition, ElementKind.Aggregation, ElementKind.Generalization,
        ElementKind.Realization, ElementKind.Dependency, ElementKind.Satisfy, ElementKind.Verify, ElementKind.Refine,
        ElementKind.Trace, ElementKind.DeriveReqt, ElementKind.Copy, ElementKind.Include, ElementKind.Extend,
        ElementKind.Transition, ElementKind.ControlFlow, ElementKind.ObjectFlow, ElementKind.Allocate, ElementKind.Connector,
        ElementKind.Message, ElementKind.Binding, ElementKind.BpmnFlow, ElementKind.ArchiRelation, ElementKind.ForeignKey, ElementKind.Line,
        ElementKind.FeatureDecomposition, ElementKind.Requires, ElementKind.Excludes, ElementKind.CommunicationLink,
        ElementKind.Link,
    };

    private static readonly HashSet<ElementKind> Classifiers = new()
    {
        ElementKind.Class, ElementKind.Interface, ElementKind.DataType, ElementKind.Enumeration, ElementKind.Component,
        ElementKind.Block, ElementKind.InterfaceBlock, ElementKind.ValueType, ElementKind.ConstraintBlock,
        ElementKind.Actor, ElementKind.UseCase, ElementKind.Requirement, ElementKind.TestCase,
        ElementKind.Activity, ElementKind.StateMachine, ElementKind.Interaction, ElementKind.Table, ElementKind.Stereotype,
    };

    private static readonly HashSet<ElementKind> StructuralClassifiers = new()
    {
        ElementKind.Class, ElementKind.Interface, ElementKind.DataType, ElementKind.Enumeration, ElementKind.Component,
        ElementKind.Block, ElementKind.InterfaceBlock, ElementKind.ValueType, ElementKind.ConstraintBlock, ElementKind.Actor,
    };

    private static readonly HashSet<ElementKind> ActivityNodes = new()
    {
        ElementKind.Action, ElementKind.InitialNode, ElementKind.ActivityFinalNode, ElementKind.DecisionNode, ElementKind.ForkNode,
        ElementKind.InputPin, ElementKind.OutputPin, ElementKind.ObjectNode, ElementKind.InteractionUse, ElementKind.InlineInteraction,
    };

    private static readonly HashSet<ElementKind> Vertices = new()
    {
        ElementKind.State, ElementKind.InitialState, ElementKind.FinalState, ElementKind.Pseudostate,
    };

    private static readonly HashSet<ElementKind> PackageMembers = new()
    {
        ElementKind.BpmnPool, ElementKind.BpmnLane, ElementKind.BpmnNode, ElementKind.ArchiElement, ElementKind.Profile, ElementKind.Shape,
        ElementKind.TableView, ElementKind.QueryView, ElementKind.ValidationRule, ElementKind.RelationMap,
        ElementKind.Feature, ElementKind.Configuration, ElementKind.TradeStudy,
        ElementKind.Sequence, ElementKind.Routine,
        ElementKind.InstanceSpecification,
    };

    /// <summary>Kinds that carry no name of their own in normal use.</summary>
    private static readonly HashSet<ElementKind> Anonymous = new()
    {
        ElementKind.InitialNode, ElementKind.ActivityFinalNode, ElementKind.DecisionNode, ElementKind.ForkNode,
        ElementKind.InitialState, ElementKind.FinalState, ElementKind.Comment, ElementKind.CombinedFragment, ElementKind.Shape,
        ElementKind.DurationConstraint, ElementKind.TimeConstraint, ElementKind.InteractionOperand, ElementKind.Pseudostate,
        ElementKind.Slot,
    };

    public static readonly IReadOnlyList<string> FragmentOperators = ["alt", "opt", "loop", "par", "break", "critical", "neg", "seq", "strict"];

    public static bool IsRelationship(ElementKind k) => Relationships.Contains(k);
    public static bool IsClassifier(ElementKind k) => Classifiers.Contains(k);
    public static bool IsStructuralClassifier(ElementKind k) => StructuralClassifiers.Contains(k);
    public static bool IsActivityNode(ElementKind k) => ActivityNodes.Contains(k);
    public static bool IsVertex(ElementKind k) => Vertices.Contains(k);
    public static bool NeedsName(ElementKind k) => !IsRelationship(k) && !Anonymous.Contains(k) && k != ElementKind.Model;
    public static bool IsNamespace(ElementKind k) => k is ElementKind.Model or ElementKind.Package or ElementKind.Profile || IsClassifier(k) || k == ElementKind.State;
    public static bool IsTypedFeature(ElementKind k) => k is ElementKind.Property or ElementKind.Port or ElementKind.Lifeline or ElementKind.ObjectNode or ElementKind.InputPin or ElementKind.OutputPin;
    public static bool IsBehavior(ElementKind k) => k is ElementKind.Activity or ElementKind.StateMachine or ElementKind.Interaction;
    public static bool IsPin(ElementKind k) => k is ElementKind.InputPin or ElementKind.OutputPin;

    public static IEnumerable<ElementKind> ElementKinds => Enum.GetValues<ElementKind>().Where(k => !IsRelationship(k) && k != ElementKind.Diagram);
    public static IEnumerable<ElementKind> RelationshipKinds => Enum.GetValues<ElementKind>().Where(IsRelationship);

    /// <summary>May an element of <paramref name="child"/> kind be owned by <paramref name="owner"/>?</summary>
    public static bool CanOwn(ElementKind owner, ElementKind child)
    {
        if (child == ElementKind.Model) return false;
        if (child == ElementKind.Diagram) return IsNamespace(owner);
        if (child == ElementKind.Comment) return true;
        if (IsRelationship(child)) return owner is ElementKind.Model or ElementKind.Package || IsClassifier(owner) || IsVertex(owner);

        return owner switch
        {
            ElementKind.Model or ElementKind.Package =>
                child is ElementKind.Package || IsClassifier(child) && child != ElementKind.Stereotype || PackageMembers.Contains(child),
            ElementKind.Profile => child is ElementKind.Stereotype or ElementKind.Enumeration or ElementKind.Package,
            ElementKind.Stereotype => child == ElementKind.TagDefinition,
            ElementKind.Enumeration => child is ElementKind.EnumerationLiteral or ElementKind.Operation,
            ElementKind.Interface or ElementKind.InterfaceBlock => child is ElementKind.Property or ElementKind.Operation or ElementKind.Port,
            ElementKind.Class or ElementKind.Block or ElementKind.Component or ElementKind.ConstraintBlock or ElementKind.DataType or ElementKind.ValueType =>
                child is ElementKind.Property or ElementKind.Operation or ElementKind.Port || IsStructuralClassifier(child) || IsBehavior(child),
            ElementKind.Actor or ElementKind.UseCase => child is ElementKind.Property or ElementKind.Operation || IsBehavior(child),
            ElementKind.Interaction => child is ElementKind.Lifeline or ElementKind.CombinedFragment or ElementKind.TimingLifeline
                or ElementKind.DurationConstraint or ElementKind.TimeConstraint or ElementKind.CommunicationLifeline,
            ElementKind.CombinedFragment => child is ElementKind.InteractionOperand,
            ElementKind.Requirement => child is ElementKind.Requirement,  // requirement containment
            ElementKind.TestCase => child is ElementKind.Activity,
            ElementKind.Activity => IsActivityNode(child) && !IsPin(child) || child == ElementKind.Partition,
            ElementKind.Action => IsPin(child),
            ElementKind.StateMachine => IsVertex(child),
            ElementKind.State => IsVertex(child),
            ElementKind.Table => child is ElementKind.Column or ElementKind.DbIndex or ElementKind.DbConstraint or ElementKind.Trigger,
            ElementKind.Feature => child == ElementKind.Feature,   // feature tree by containment
            ElementKind.InstanceSpecification => child is ElementKind.Slot or ElementKind.InstanceSpecification,   // slots and nested part instances
            _ => false,
        };
    }

    /// <summary>Checks a relationship's end kinds. Returns null when valid, otherwise a reason.</summary>
    public static string? CheckEnds(ElementKind rel, ElementKind source, ElementKind target)
    {
        bool req(ElementKind k) => k == ElementKind.Requirement;
        return rel switch
        {
            ElementKind.Satisfy => req(target) && !req(source) ? null : "Satisfy must go from a design element to a Requirement",
            ElementKind.Verify => req(target) ? null : "Verify must target a Requirement",
            ElementKind.DeriveReqt or ElementKind.Copy => req(source) && req(target) ? null : $"{rel} must connect two Requirements",
            ElementKind.Include or ElementKind.Extend => source == ElementKind.UseCase && target == ElementKind.UseCase ? null : $"{rel} must connect two Use Cases",
            ElementKind.Generalization => source == target && IsClassifier(source) ? null : "Generalization must connect two classifiers of the same kind",
            ElementKind.Realization => IsClassifier(source) && target is ElementKind.Interface or ElementKind.InterfaceBlock
                || source == ElementKind.Table && IsClassifier(target) ? null : "Realization must target an Interface (or go from a table to the logical entity it realizes)",
            ElementKind.Association or ElementKind.Composition or ElementKind.Aggregation =>
                IsClassifier(source) && IsClassifier(target) ? null : $"{rel} must connect two classifiers",
            ElementKind.Transition => IsVertex(source) && IsVertex(target) && source != ElementKind.FinalState && target != ElementKind.InitialState
                ? null : "Transition must connect states (not out of a final state, not into an initial state)",
            ElementKind.ControlFlow or ElementKind.ObjectFlow =>
                IsActivityNode(source) && IsActivityNode(target) && source != ElementKind.ActivityFinalNode && target != ElementKind.InitialNode
                ? null : $"{rel} must connect activity nodes (not out of a final node, not into an initial node)",
            ElementKind.Connector => source is ElementKind.Property or ElementKind.Port && target is ElementKind.Property or ElementKind.Port
                ? null : "Connector must connect parts or ports",
            ElementKind.Binding => source == ElementKind.Property && target == ElementKind.Property ? null : "Binding connectors connect value properties and constraint parameters",
            ElementKind.Message => source == ElementKind.Lifeline && target == ElementKind.Lifeline ? null : "Message must connect two lifelines",
            ElementKind.BpmnFlow => source is ElementKind.BpmnNode or ElementKind.BpmnPool && target is ElementKind.BpmnNode or ElementKind.BpmnPool ? null : "BPMN flows connect BPMN elements",
            ElementKind.ArchiRelation => source == ElementKind.ArchiElement && target == ElementKind.ArchiElement ? null : "ArchiMate relationships connect ArchiMate elements",
            ElementKind.ForeignKey => source == ElementKind.Table && target == ElementKind.Table ? null : "Foreign keys connect two tables",
            ElementKind.FeatureDecomposition or ElementKind.Requires or ElementKind.Excludes =>
                source == ElementKind.Feature && target == ElementKind.Feature ? null : $"{DisplayName(rel)} must connect two Features",
            ElementKind.CommunicationLink => source is ElementKind.CommunicationLifeline or ElementKind.Lifeline && target is ElementKind.CommunicationLifeline or ElementKind.Lifeline
                ? null : "Communication links connect lifelines",
            ElementKind.Link => source == ElementKind.InstanceSpecification && target == ElementKind.InstanceSpecification ? null : "Links connect two instance specifications",
            _ => null, // Dependency, Refine, Trace, Allocate: any to any
        };
    }

    public static IReadOnlyList<PaletteItem> PaletteItems(DiagramType t) => t switch
    {
        DiagramType.Bpmn =>
        [
            new(ElementKind.BpmnPool, Label: "Pool"), new(ElementKind.BpmnLane, Label: "Lane"),
            new(ElementKind.BpmnNode, "startEvent"), new(ElementKind.BpmnNode, "task"), new(ElementKind.BpmnNode, "userTask"),
            new(ElementKind.BpmnNode, "serviceTask"), new(ElementKind.BpmnNode, "subProcess"), new(ElementKind.BpmnNode, "exclusiveGateway"),
            new(ElementKind.BpmnNode, "parallelGateway"), new(ElementKind.BpmnNode, "intermediateEvent"), new(ElementKind.BpmnNode, "timerEvent"),
            new(ElementKind.BpmnNode, "endEvent"), new(ElementKind.BpmnNode, "dataObject"), new(ElementKind.BpmnNode, "dataStore"),
            new(ElementKind.Comment),
        ],
        DiagramType.ArchiMate => Subtypes.ArchiElements.Select(s => new PaletteItem(ElementKind.ArchiElement, s)).Append(new(ElementKind.Comment)).ToList(),
        DiagramType.Database =>
        [
            new(ElementKind.Table), new(ElementKind.Table, DataModeling.DbFields.View, "View"), new(ElementKind.Table, DataModeling.DbFields.MaterializedView, "Materialized View"),
            new(ElementKind.Class, Label: "Entity (logical)", Stereotype: "entity"), new(ElementKind.Requirement), new(ElementKind.Block), new(ElementKind.Comment),
        ],
        DiagramType.Whiteboard => GeneralItems.Append(new(ElementKind.Comment, Label: "Sticky note")).ToList(),
        DiagramType.StateMachine =>
        [
            new(ElementKind.State), new(ElementKind.InitialState), new(ElementKind.FinalState),
            .. Subtypes.PseudostateKinds.Select(s => new PaletteItem(ElementKind.Pseudostate, s)),
            new(ElementKind.Comment),
        ],
        DiagramType.Activity =>
        [
            new(ElementKind.Action), new(ElementKind.InitialNode), new(ElementKind.ActivityFinalNode), new(ElementKind.DecisionNode), new(ElementKind.ForkNode),
            new(ElementKind.Partition), new(ElementKind.ObjectNode), new(ElementKind.ObjectNode, Subtypes.DataStore, "Data Store"),
            new(ElementKind.ObjectNode, Subtypes.CentralBuffer, "Central Buffer"), new(ElementKind.InputPin), new(ElementKind.OutputPin), new(ElementKind.Comment),
        ],
        _ when UafViewpoints.Of(t) is { } uaf => UafViewpoints.PaletteItems(uaf),
        _ => PaletteNodes(t).Select(k => new PaletteItem(k)).ToList(),
    };

    public static IReadOnlyList<PaletteItem> PaletteLinks(DiagramType t) => t switch
    {
        DiagramType.Bpmn => [new(ElementKind.BpmnFlow, "sequence"), new(ElementKind.BpmnFlow, "message"), new(ElementKind.BpmnFlow, "association")],
        DiagramType.ArchiMate => Subtypes.ArchiRelations.Select(s => new PaletteItem(ElementKind.ArchiRelation, s)).ToList(),
        DiagramType.Whiteboard => GeneralLinks,
        _ when UafViewpoints.Of(t) is { } uaf => UafViewpoints.PaletteLinks(uaf),
        _ => PaletteEdges(t).Select(k => new PaletteItem(k)).ToList(),
    };

    /// <summary>Free shapes offered on every diagram (the "General" section of the shapes panel).</summary>
    public static readonly IReadOnlyList<PaletteItem> GeneralItems = Subtypes.Shapes.Select(s => new PaletteItem(ElementKind.Shape, s)).ToList();

    /// <summary>Plain connectors offered on every diagram.</summary>
    public static readonly IReadOnlyList<PaletteItem> GeneralLinks = Subtypes.Lines.Select(s => new PaletteItem(ElementKind.Line, s)).ToList();

    public static IReadOnlyList<ElementKind> PaletteNodes(DiagramType t) => t switch
    {
        DiagramType.Class => [ElementKind.Package, ElementKind.Class, ElementKind.Interface, ElementKind.DataType, ElementKind.Enumeration, ElementKind.Component, ElementKind.Comment],
        DiagramType.BlockDefinition => [ElementKind.Package, ElementKind.Block, ElementKind.InterfaceBlock, ElementKind.ValueType, ElementKind.ConstraintBlock, ElementKind.Enumeration, ElementKind.Actor, ElementKind.InstanceSpecification, ElementKind.Comment],
        DiagramType.InternalBlock => [ElementKind.Property, ElementKind.Port, ElementKind.Comment],
        DiagramType.Parametric => [ElementKind.Property, ElementKind.Comment],
        DiagramType.Requirement => [ElementKind.Requirement, ElementKind.TestCase, ElementKind.Block, ElementKind.UseCase, ElementKind.Comment],
        DiagramType.UseCase => [ElementKind.Actor, ElementKind.UseCase, ElementKind.Block, ElementKind.Package, ElementKind.Comment],
        DiagramType.Activity => [ElementKind.Action, ElementKind.InitialNode, ElementKind.ActivityFinalNode, ElementKind.DecisionNode, ElementKind.ForkNode,
                                 ElementKind.Partition, ElementKind.ObjectNode, ElementKind.InputPin, ElementKind.OutputPin, ElementKind.Comment],
        DiagramType.StateMachine => [ElementKind.State, ElementKind.InitialState, ElementKind.FinalState, ElementKind.Pseudostate, ElementKind.Comment],
        DiagramType.Package => [ElementKind.Package, ElementKind.Profile, ElementKind.Stereotype, ElementKind.Block, ElementKind.Class, ElementKind.Interface, ElementKind.Comment],
        DiagramType.Sequence => [ElementKind.Lifeline, ElementKind.CombinedFragment, ElementKind.Comment],
        DiagramType.Database => [ElementKind.Table, ElementKind.Class, ElementKind.Requirement, ElementKind.Block, ElementKind.Comment],
        DiagramType.FeatureModel => [ElementKind.Feature, ElementKind.Configuration, ElementKind.Comment],
        DiagramType.Timing => [ElementKind.TimingLifeline, ElementKind.DurationConstraint, ElementKind.TimeConstraint, ElementKind.Comment],
        DiagramType.InteractionOverview => [ElementKind.InteractionUse, ElementKind.InlineInteraction, ElementKind.InitialNode, ElementKind.ActivityFinalNode,
                                            ElementKind.DecisionNode, ElementKind.ForkNode, ElementKind.Comment],
        DiagramType.Communication => [ElementKind.CommunicationLifeline, ElementKind.Comment],
        DiagramType.Object => [ElementKind.InstanceSpecification, ElementKind.Block, ElementKind.Comment],
        _ when UafViewpoints.Of(t) is { } uaf => uaf.Elements.Select(x => x.Kind).Distinct().Append(ElementKind.Comment).ToList(),
        _ => [],
    };

    public static IReadOnlyList<ElementKind> PaletteEdges(DiagramType t) => t switch
    {
        DiagramType.Class => [ElementKind.Association, ElementKind.Composition, ElementKind.Aggregation, ElementKind.Generalization, ElementKind.Realization, ElementKind.Dependency],
        DiagramType.BlockDefinition => [ElementKind.Association, ElementKind.Composition, ElementKind.Aggregation, ElementKind.Generalization, ElementKind.Realization, ElementKind.Dependency, ElementKind.Allocate, ElementKind.Link],
        DiagramType.InternalBlock => [ElementKind.Connector, ElementKind.ObjectFlow, ElementKind.Allocate],
        DiagramType.Parametric => [ElementKind.Binding],
        DiagramType.Requirement => [ElementKind.DeriveReqt, ElementKind.Satisfy, ElementKind.Verify, ElementKind.Refine, ElementKind.Trace, ElementKind.Copy],
        DiagramType.UseCase => [ElementKind.Association, ElementKind.Include, ElementKind.Extend, ElementKind.Generalization],
        DiagramType.Activity => [ElementKind.ControlFlow, ElementKind.ObjectFlow, ElementKind.Allocate],
        DiagramType.StateMachine => [ElementKind.Transition],
        DiagramType.Package => [ElementKind.Dependency, ElementKind.Generalization, ElementKind.Realization, ElementKind.Trace],
        DiagramType.Sequence => [ElementKind.Message],
        DiagramType.Database => [ElementKind.ForeignKey, ElementKind.Realization, ElementKind.Trace, ElementKind.Satisfy, ElementKind.Allocate],
        DiagramType.FeatureModel => [ElementKind.FeatureDecomposition, ElementKind.Requires, ElementKind.Excludes],
        DiagramType.InteractionOverview => [ElementKind.ControlFlow],
        DiagramType.Communication => [ElementKind.CommunicationLink],
        DiagramType.Object => [ElementKind.Link, ElementKind.Dependency],
        _ when UafViewpoints.Of(t) is { } uaf => uaf.Links.Select(x => x.Kind).Distinct().ToList(),
        _ => [],
    };

    /// <summary>Which element kinds may own a diagram of this type ("context").</summary>
    public static bool IsValidDiagramContext(DiagramType t, ElementKind owner) => t switch
    {
        DiagramType.InternalBlock or DiagramType.Parametric => owner is ElementKind.Block or ElementKind.Class or ElementKind.Component or ElementKind.ConstraintBlock,
        DiagramType.Activity => owner == ElementKind.Activity,
        DiagramType.StateMachine => owner == ElementKind.StateMachine,
        DiagramType.Sequence or DiagramType.Timing or DiagramType.Communication => owner == ElementKind.Interaction,
        DiagramType.InteractionOverview => owner == ElementKind.Activity,
        DiagramType.Bpmn or DiagramType.ArchiMate or DiagramType.Database or DiagramType.Whiteboard or DiagramType.FeatureModel => owner is ElementKind.Model or ElementKind.Package,
        _ => IsNamespace(owner),
    };

    public static string DiagramAbbreviation(DiagramType t) => t switch
    {
        DiagramType.Class => "class",
        DiagramType.BlockDefinition => "bdd",
        DiagramType.InternalBlock => "ibd",
        DiagramType.Parametric => "par",
        DiagramType.Requirement => "req",
        DiagramType.UseCase => "uc",
        DiagramType.Activity => "act",
        DiagramType.StateMachine => "stm",
        DiagramType.Package => "pkg",
        DiagramType.Sequence => "sd",
        DiagramType.Bpmn => "bpmn",
        DiagramType.ArchiMate => "archi",
        DiagramType.Database => "erd",
        DiagramType.Whiteboard => "board",
        DiagramType.FeatureModel => "fm",
        DiagramType.Timing => "timing",
        DiagramType.InteractionOverview => "io",
        DiagramType.Communication => "comm",
        DiagramType.Object => "obj",
        _ when UafViewpoints.Of(t) is { } uaf => "uaf " + uaf.Code,
        _ => "diagram",
    };

    public static string DisplayName(ElementKind k) => k switch
    {
        ElementKind.InterfaceBlock => "Interface Block",
        ElementKind.CombinedFragment => "Fragment",
        ElementKind.ValueType => "Value Type",
        ElementKind.ConstraintBlock => "Constraint Block",
        ElementKind.TestCase => "Test Case",
        ElementKind.UseCase => "Use Case",
        ElementKind.InitialNode => "Initial Node",
        ElementKind.ActivityFinalNode => "Final Node",
        ElementKind.DecisionNode => "Decision",
        ElementKind.ForkNode => "Fork/Join",
        ElementKind.StateMachine => "State Machine",
        ElementKind.InitialState => "Initial",
        ElementKind.FinalState => "Final State",
        ElementKind.EnumerationLiteral => "Literal",
        ElementKind.DataType => "Data Type",
        ElementKind.DeriveReqt => "Derive",
        ElementKind.ControlFlow => "Control Flow",
        ElementKind.ObjectFlow => "Object Flow",
        ElementKind.InputPin => "Input Pin",
        ElementKind.OutputPin => "Output Pin",
        ElementKind.ObjectNode => "Object Node",
        ElementKind.Partition => "Swimlane",
        ElementKind.TagDefinition => "Tag Definition",
        ElementKind.BpmnPool => "Pool",
        ElementKind.BpmnLane => "Lane",
        ElementKind.BpmnNode => "BPMN Element",
        ElementKind.BpmnFlow => "BPMN Flow",
        ElementKind.ArchiElement => "ArchiMate Element",
        ElementKind.ArchiRelation => "ArchiMate Relationship",
        ElementKind.ForeignKey => "Foreign Key",
        ElementKind.Binding => "Binding Connector",
        ElementKind.Line => "Line",
        ElementKind.TableView => "Table View",
        ElementKind.QueryView => "Query",
        ElementKind.ValidationRule => "Validation Rule",
        ElementKind.RelationMap => "Relation Map",
        ElementKind.TradeStudy => "Trade Study",
        ElementKind.FeatureDecomposition => "Feature Decomposition",
        ElementKind.TimingLifeline => "Timing Lifeline",
        ElementKind.DurationConstraint => "Duration Constraint",
        ElementKind.TimeConstraint => "Time Constraint",
        ElementKind.InteractionUse => "Interaction Use",
        ElementKind.InlineInteraction => "Inline Interaction",
        ElementKind.CommunicationLifeline => "Lifeline",
        ElementKind.CommunicationLink => "Link",
        ElementKind.InteractionOperand => "Operand",
        ElementKind.DbIndex => "Index",
        ElementKind.DbConstraint => "Constraint",
        ElementKind.InstanceSpecification => "Instance",
        _ => k.ToString(),
    };

    public static string DisplayName(DiagramType t) => t switch
    {
        DiagramType.BlockDefinition => "Block Definition Diagram",
        DiagramType.InternalBlock => "Internal Block Diagram",
        DiagramType.Parametric => "Parametric Diagram",
        DiagramType.UseCase => "Use Case Diagram",
        DiagramType.StateMachine => "State Machine Diagram",
        DiagramType.Sequence => "Sequence Diagram",
        DiagramType.Bpmn => "BPMN Process Diagram",
        DiagramType.ArchiMate => "ArchiMate View",
        DiagramType.Database => "Database Diagram (ER)",
        DiagramType.Whiteboard => "Whiteboard",
        DiagramType.FeatureModel => "Feature Model Diagram",
        DiagramType.Timing => "Timing Diagram",
        DiagramType.InteractionOverview => "Interaction Overview Diagram",
        DiagramType.Communication => "Communication Diagram",
        DiagramType.Object => "Object Diagram",
        _ when UafViewpoints.Of(t) is { } uaf => uaf.Name,
        _ => $"{t} Diagram",
    };

    /// <summary>The «keyword» shown above a node's name, if any.</summary>
    public static string? Keyword(ElementKind k) => k switch
    {
        ElementKind.Block => "block",
        ElementKind.InterfaceBlock => "interfaceBlock",
        ElementKind.ValueType => "valueType",
        ElementKind.ConstraintBlock => "constraint",
        ElementKind.Requirement => "requirement",
        ElementKind.TestCase => "testCase",
        ElementKind.Interface => "interface",
        ElementKind.Enumeration => "enumeration",
        ElementKind.DataType => "dataType",
        ElementKind.Component => "component",
        ElementKind.Activity => "activity",
        ElementKind.StateMachine => "stateMachine",
        ElementKind.Interaction => "interaction",
        ElementKind.Profile => "profile",
        ElementKind.Stereotype => "stereotype",
        ElementKind.Table => "table",
        ElementKind.Feature => "feature",
        ElementKind.Configuration => "configuration",
        ElementKind.TradeStudy => "tradeStudy",
        ElementKind.Sequence => "sequence",
        ElementKind.Routine => "routine",
        _ => null,
    };

    public static NodeShape Shape(ElementKind k) => k switch
    {
        ElementKind.Actor => NodeShape.StickFigure,
        ElementKind.UseCase => NodeShape.Ellipse,
        ElementKind.Package or ElementKind.Model or ElementKind.Profile => NodeShape.Folder,
        ElementKind.Comment => NodeShape.Note,
        ElementKind.Action or ElementKind.State => NodeShape.Rounded,
        ElementKind.InitialNode or ElementKind.InitialState => NodeShape.FilledCircle,
        ElementKind.ActivityFinalNode or ElementKind.FinalState => NodeShape.BullsEye,
        ElementKind.DecisionNode => NodeShape.Diamond,
        ElementKind.ForkNode => NodeShape.Bar,
        ElementKind.Port or ElementKind.InputPin or ElementKind.OutputPin => NodeShape.SmallSquare,
        ElementKind.Lifeline => NodeShape.Lifeline,
        ElementKind.CombinedFragment => NodeShape.Fragment,
        ElementKind.Partition => NodeShape.Swimlane,
        ElementKind.BpmnPool or ElementKind.BpmnLane => NodeShape.Pool,
        ElementKind.TimingLifeline => NodeShape.Timeline,
        ElementKind.TimeConstraint => NodeShape.TimeMarker,
        ElementKind.DurationConstraint => NodeShape.DurationMarker,
        ElementKind.InteractionUse => NodeShape.InteractionUse,
        ElementKind.InlineInteraction => NodeShape.InlineInteraction,
        ElementKind.Pseudostate => NodeShape.Diamond,   // a choice; Notation.ShapeOf picks the shape of the other kinds
        _ => NodeShape.Rectangle,
    };

    public static (double W, double H) DefaultSize(ElementKind k) => Shape(k) switch
    {
        NodeShape.StickFigure => (60, 90),
        NodeShape.Ellipse => (150, 70),
        NodeShape.FilledCircle or NodeShape.BullsEye => (28, 28),
        NodeShape.Diamond => (40, 40),
        NodeShape.Bar => (100, 8),
        NodeShape.SmallSquare => (16, 16),
        NodeShape.Rounded => (130, 56),
        NodeShape.Note => (160, 70),
        NodeShape.Folder => (160, 100),
        NodeShape.Lifeline => (150, 380),
        NodeShape.Fragment => (380, 120),
        NodeShape.Swimlane => (240, 460),
        NodeShape.Pool when k == ElementKind.BpmnLane => (640, 140),
        NodeShape.Pool => (700, 260),
        NodeShape.Timeline => (640, 130),
        NodeShape.TimeMarker => (90, 150),
        NodeShape.DurationMarker => (180, 40),
        NodeShape.InteractionUse => (200, 70),
        NodeShape.InlineInteraction => (280, 170),
        _ when k == ElementKind.CommunicationLifeline => (150, 46),
        _ when k == ElementKind.Requirement => (220, 120),
        _ when k is ElementKind.Property or ElementKind.ObjectNode => (160, 60),
        _ when k == ElementKind.ArchiElement => (150, 70),
        _ when k == ElementKind.Table => (200, 120),
        _ when k is ElementKind.Configuration or ElementKind.TradeStudy => (200, 110),
        _ when k == ElementKind.Feature => (140, 60),
        _ when k == ElementKind.InstanceSpecification => (200, 100),
        _ => (180, 110),
    };

    public static EdgeStyle Style(ElementKind rel) => rel switch
    {
        ElementKind.Association => new(false, ArrowHead.None, ArrowHead.None, null),
        ElementKind.Composition => new(false, ArrowHead.None, ArrowHead.FilledDiamond, null),
        ElementKind.Aggregation => new(false, ArrowHead.None, ArrowHead.HollowDiamond, null),
        ElementKind.Generalization => new(false, ArrowHead.HollowTriangle, ArrowHead.None, null),
        ElementKind.Realization => new(true, ArrowHead.HollowTriangle, ArrowHead.None, null),
        ElementKind.Dependency => new(true, ArrowHead.Open, ArrowHead.None, null),
        ElementKind.Satisfy => new(true, ArrowHead.Open, ArrowHead.None, "satisfy"),
        ElementKind.Verify => new(true, ArrowHead.Open, ArrowHead.None, "verify"),
        ElementKind.Refine => new(true, ArrowHead.Open, ArrowHead.None, "refine"),
        ElementKind.Trace => new(true, ArrowHead.Open, ArrowHead.None, "trace"),
        ElementKind.DeriveReqt => new(true, ArrowHead.Open, ArrowHead.None, "deriveReqt"),
        ElementKind.Copy => new(true, ArrowHead.Open, ArrowHead.None, "copy"),
        ElementKind.Include => new(true, ArrowHead.Open, ArrowHead.None, "include"),
        ElementKind.Extend => new(true, ArrowHead.Open, ArrowHead.None, "extend"),
        ElementKind.Allocate => new(true, ArrowHead.Open, ArrowHead.None, "allocate"),
        ElementKind.Transition or ElementKind.ControlFlow => new(false, ArrowHead.Open, ArrowHead.None, null),
        ElementKind.ObjectFlow => new(true, ArrowHead.Open, ArrowHead.None, null),
        ElementKind.Connector => new(false, ArrowHead.None, ArrowHead.None, null),
        ElementKind.Binding => new(false, ArrowHead.None, ArrowHead.None, "equal"),
        ElementKind.Message => new(false, ArrowHead.Filled, ArrowHead.None, null),
        ElementKind.ForeignKey => new(false, ArrowHead.Bar, ArrowHead.CrowsFoot, null),
        ElementKind.FeatureDecomposition => new(false, ArrowHead.Dot, ArrowHead.None, null),
        ElementKind.Requires => new(true, ArrowHead.Open, ArrowHead.None, "requires"),
        ElementKind.Excludes => new(true, ArrowHead.Open, ArrowHead.Open, "excludes"),
        ElementKind.CommunicationLink => new(false, ArrowHead.None, ArrowHead.None, null),
        ElementKind.Link => new(false, ArrowHead.None, ArrowHead.None, null),
        _ => new(false, ArrowHead.None, ArrowHead.None, null),
    };

    /// <summary>Editable fields shown in the property inspector, per kind.</summary>
    public static IReadOnlyList<string> EditableFields(ElementKind k)
    {
        var f = new List<string>();
        if (k is not (ElementKind.Comment or ElementKind.CombinedFragment or ElementKind.InteractionOperand or ElementKind.Slot)) f.Add(FieldKeys.Name);
        if (k is ElementKind.BpmnNode or ElementKind.ArchiElement or ElementKind.BpmnFlow or ElementKind.ArchiRelation or ElementKind.Shape or ElementKind.Line
            or ElementKind.Pseudostate or ElementKind.ObjectNode or ElementKind.Table or ElementKind.DbConstraint or ElementKind.Routine) f.Add(FieldKeys.Subtype);
        if (k == ElementKind.InteractionOperand) f.Add(FieldKeys.Guard);
        if (IsClassifier(k) && k is not (ElementKind.Requirement or ElementKind.Table or ElementKind.Stereotype)) f.Add(FieldKeys.IsAbstract);
        if (IsClassifier(k) && k != ElementKind.Table || k is ElementKind.Property or ElementKind.Operation or ElementKind.Port) f.Add(FieldKeys.Visibility);
        if (IsTypedFeature(k)) { f.Add(FieldKeys.Type); if (k != ElementKind.Lifeline) f.Add(FieldKeys.Multiplicity); }
        if (k == ElementKind.Message) { f.Add(FieldKeys.MessageSort); f.Add(FieldKeys.Signature); }
        if (k == ElementKind.CombinedFragment) { f.Add(FieldKeys.Operator); f.Add(FieldKeys.Guard); }
        if (k == ElementKind.Property) f.Add(FieldKeys.DefaultValue);
        if (k == ElementKind.Port) { f.Add(FieldKeys.Direction); f.Add(FieldKeys.PortKind); }
        if (k == ElementKind.Connector) { f.Add(FieldKeys.ItemFlow); f.Add(FieldKeys.FlowDirection); }
        if (k == ElementKind.Operation) f.Add(FieldKeys.Signature);
        if (k == ElementKind.Requirement) { f.Add(FieldKeys.RequirementId); f.Add(FieldKeys.Text); }
        if (k == ElementKind.Transition) { f.Add(FieldKeys.Guard); f.Add(FieldKeys.Effect); }
        if (k is ElementKind.ControlFlow or ElementKind.ObjectFlow) f.Add(FieldKeys.Guard);
        if (k == ElementKind.Action) f.Add(FieldKeys.Behavior);
        if (k == ElementKind.Partition) f.Add(FieldKeys.Represents);
        if (k == ElementKind.State) { f.Add(FieldKeys.Entry); f.Add(FieldKeys.DoActivity); f.Add(FieldKeys.Exit); }
        if (k is ElementKind.ConstraintBlock or ElementKind.Action or ElementKind.Comment) f.Add(FieldKeys.Body);
        if (k is ElementKind.Association or ElementKind.Composition or ElementKind.Aggregation) { f.Add(FieldKeys.Multiplicity); f.Add(FieldKeys.SourceMultiplicity); }
        if (k == ElementKind.Stereotype) { f.Add(FieldKeys.Metaclass); f.Add(FieldKeys.Color); }
        if (k == ElementKind.TagDefinition) { f.Add(FieldKeys.TagType); f.Add(FieldKeys.TagValues); f.Add(FieldKeys.DefaultValue); }
        if (k == ElementKind.Column) { f.Add(FieldKeys.SqlType); f.Add(FieldKeys.PrimaryKey); f.Add(FieldKeys.Nullable); f.Add(FieldKeys.Unique); f.Add(FieldKeys.DefaultValue); }
        if (k == ElementKind.ForeignKey) { f.Add(FieldKeys.FkColumns); f.Add(FieldKeys.RefColumns); }
        f.AddRange(DataModeling.DbFields.EditableFields(k));
        if (k is ElementKind.TableView or ElementKind.QueryView or ElementKind.ValidationRule) f.Add(Analysis.ViewFields.Query);
        if (k == ElementKind.ValidationRule) { f.Add(Analysis.ViewFields.Severity); f.Add(Analysis.ViewFields.Message); }
        f.AddRange(ExtFieldKeys.EditableFields(k));
        f.AddRange(InteractionNotation.EditableFields(k));
        f.Add(FieldKeys.Stereotypes);
        f.Add(FieldKeys.Documentation);
        return f;
    }

    public static readonly IReadOnlyList<string> PrimitiveTypes = ["Boolean", "Integer", "Real", "String", "UnlimitedNatural"];
}
