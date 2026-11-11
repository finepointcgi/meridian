namespace Meridian.Core.Model;

/// <summary>
/// Every metaclass the tool understands. UML and SysML v1 concepts share one enum;
/// <see cref="Language.Metamodel"/> holds the rules (ownership, relationship ends, notation).
/// </summary>
public enum ElementKind
{
    // Containers
    Model,
    Package,

    // Structure (UML)
    Class,
    Interface,
    DataType,
    Enumeration,
    EnumerationLiteral,
    Component,
    Property,
    Operation,

    // Structure (SysML)
    Block,
    InterfaceBlock,
    ValueType,
    ConstraintBlock,
    Port,

    // Requirements
    Requirement,
    TestCase,

    // Use cases
    Actor,
    UseCase,

    // Activities
    Activity,
    Action,
    InitialNode,
    ActivityFinalNode,
    DecisionNode,
    ForkNode,
    Partition,
    InputPin,
    OutputPin,
    ObjectNode,

    // Interactions (sequence diagrams)
    Interaction,
    Lifeline,
    CombinedFragment,

    // State machines
    StateMachine,
    State,
    InitialState,
    FinalState,

    // Annotation
    Comment,

    // Profiles
    Profile,
    Stereotype,
    TagDefinition,

    // BPMN (subtype field selects task / event / gateway / data …)
    BpmnPool,
    BpmnLane,
    BpmnNode,

    // ArchiMate (subtype field selects the ArchiMate element type)
    ArchiElement,

    // Data modeling
    Table,
    Column,

    // General drawing (draw.io-style free shapes; subtype selects the outline)
    Shape,

    // Relationships
    Association,
    Composition,
    Aggregation,
    Generalization,
    Realization,
    Dependency,
    Satisfy,
    Verify,
    Refine,
    Trace,
    DeriveReqt,
    Copy,
    Include,
    Extend,
    Transition,
    ControlFlow,
    ObjectFlow,
    Allocate,
    Connector,
    Message,
    Binding,
    BpmnFlow,
    ArchiRelation,
    ForeignKey,
    Line,           // plain connector between any two shapes (subtype: arrow / line / dashed …)

    // Views
    Diagram,

    // Saved model views and custom rules (generic tables, model queries, query-based validation)
    TableView,
    QueryView,
    ValidationRule,
    // Variability / product lines and trade studies (appended: keep existing ordinals stable)
    Feature,
    Configuration,
    TradeStudy,
    FeatureDecomposition,   // parent feature -> child feature (tree edge on feature model diagrams)
    Requires,               // cross-tree constraint: selecting the source requires the target
    Excludes,               // cross-tree constraint: source and target may not both be selected
    // Timing, interaction overview and communication diagrams (appended: keep existing ordinals stable)
    TimingLifeline,         // lifeline with a state timeline over the time axis
    DurationConstraint,     // {min..max} between two time points
    TimeConstraint,         // {t = ...} at one time point
    InteractionUse,         // "ref" frame referring to another interaction (interaction overview)
    InlineInteraction,      // "sd" frame with an inline sequence (interaction overview)
    CommunicationLifeline,  // lifeline drawn as a box (communication diagram)
    CommunicationLink,      // link between communication lifelines carrying numbered messages
    // Operands and pseudostates (appended: keep existing ordinals stable)
    InteractionOperand,     // one operand of a combined fragment: guard; its vertical extent is a style of the fragment's shape
    Pseudostate,            // state machine pseudostate; subtype: choice / junction / fork / join / shallowHistory / deepHistory / entryPoint / exitPoint / terminate
    // Data modelling (appended: keep existing ordinals stable). Databases, schemas and subject areas are packages with a
    // "db.role" field; views are tables with subtype "view" / "materializedView"; enum and domain types are Enumeration / DataType.
    DbIndex,                // index on a table: db.columns, unique, db.method, db.expression (partial index WHERE)
    DbConstraint,           // named unique (subtype "unique") or check (subtype "check") constraint of a table
    Sequence,               // sequence generator in a schema
    Routine,                // stored procedure (subtype "procedure") or function (subtype "function"): signature only
    Trigger,                // trigger on its owning table: db.timing, db.events
    // Saved relation maps (appended: keep existing ordinals stable): root, depth and filters in the "relationMap" field
    RelationMap,
    // Instance specifications (appended: keep existing ordinals stable). See Instances/InstanceModel.cs.
    InstanceSpecification,  // an object: classified by blocks / value types / signals, owns its slots and nested part instances
    Slot,                   // one value of an instance for a structural feature of its classifier
    Link,                   // instance-level association / connector between two instances, typed by an association
    // Deployment (appended: keep existing ordinals stable). Nodes, devices and execution environments nest inside each other;
    // artifacts are deployed to them («deploy») and manifest model elements («manifest»). See Deployment.DeploymentModel.
    Node,
    Device,
    ExecutionEnvironment,
    Artifact,
    DeploymentSpecification,
    Deployment,             // artifact (deployment specification, database / schema) -> node, device or execution environment: «deploy»
    Manifestation,          // artifact -> the block, component, package or code element it manifests: «manifest»
    CommunicationPath,      // node <-> node; field "protocol"
}

public enum DiagramType
{
    Class,
    BlockDefinition,
    InternalBlock,
    Requirement,
    UseCase,
    Activity,
    StateMachine,
    Package,
    Sequence,
    Parametric,
    Bpmn,
    ArchiMate,
    Database,
    Whiteboard,
    FeatureModel,
    // Appended: keep existing ordinals stable
    Timing,
    InteractionOverview,
    Communication,
    UafStrategic,
    UafOperational,
    UafServices,
    UafPersonnel,
    UafResources,
    UafSecurity,
    UafProjects,
    UafStandards,
    UafActualResources,
    // Appended: keep existing ordinals stable
    Object,                 // object diagram: instance specifications, their slots and links
    Deployment,
}

public static class FieldKeys
{
    public const string Name = "name";
    public const string Documentation = "documentation";
    public const string Visibility = "visibility";
    public const string IsAbstract = "isAbstract";
    public const string Type = "type";               // id of the typing classifier, or a primitive name
    public const string Multiplicity = "multiplicity";
    public const string DefaultValue = "defaultValue";
    public const string Direction = "direction";     // ports: in / out / inout
    public const string Signature = "signature";     // operations: "(a : Integer, b : String) : Boolean"
    public const string RequirementId = "reqId";
    public const string Text = "text";
    public const string Guard = "guard";
    public const string SourceMultiplicity = "sourceMultiplicity"; // associations: multiplicity at the source end (Multiplicity is the target end)
    public const string Effect = "effect";
    public const string MessageSort = "messageSort"; // synchCall / asynchCall / reply
    public const string Operator = "operator";       // combined fragments: alt / opt / loop / par / break
    public const string Subtype = "subtype";         // BPMN / ArchiMate element and relationship subtypes
    public const string PortKind = "portKind";       // proxy / full
    public const string ItemFlow = "itemFlow";       // connectors: id or name of the conveyed item
    public const string FlowDirection = "flowDirection"; // connectors: forward / reverse
    public const string SourcePart = "sourcePart";   // connector end: part owning the source port
    public const string TargetPart = "targetPart";
    public const string Behavior = "behavior";       // call behavior actions: called activity id
    public const string Represents = "represents";   // partitions: block / actor id
    public const string Entry = "entry";
    public const string Exit = "exit";
    public const string DoActivity = "doActivity";
    public const string Metaclass = "metaclass";     // stereotypes: comma separated element kinds
    public const string TagType = "tagType";         // tag definitions: String / Integer / Real / Boolean / Enum
    public const string TagValues = "tagValues";     // tag definitions: enumeration literals, comma separated
    public const string Legend = "legend";           // diagrams: "true" shows an auto-generated legend
    public const string Color = "color";             // stereotypes: header colour for elements carrying them
    public const string SqlType = "sqlType";
    public const string PrimaryKey = "pk";
    public const string Nullable = "nullable";
    public const string Unique = "unique";
    public const string FkColumns = "fkColumns";
    public const string RefColumns = "refColumns";
    public const string Body = "body";               // constraints, opaque actions, comments
    public const string Stereotypes = "stereotypes"; // comma separated
    public const string TagPrefix = "tag:";          // tagged values: "tag:priority" = "high"
}
