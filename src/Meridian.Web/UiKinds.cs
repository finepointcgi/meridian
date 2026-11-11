using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Web;

/// <summary>Glyphs and accent colours per element kind for the tree, palette and inspector.</summary>
public static class UiKinds
{
    public static string Accent(ElementKind k) => k switch
    {
        ElementKind.Requirement or ElementKind.Satisfy or ElementKind.DeriveReqt or ElementKind.Verify or ElementKind.Refine or ElementKind.Trace or ElementKind.Copy => "#2f6fec",
        ElementKind.TestCase => "#2e8b57",
        ElementKind.Block or ElementKind.Class or ElementKind.Component or ElementKind.Property => "#c98a00",
        ElementKind.InterfaceBlock or ElementKind.Interface or ElementKind.Port => "#7b4fd6",
        ElementKind.ValueType or ElementKind.DataType or ElementKind.Enumeration or ElementKind.EnumerationLiteral => "#1d8a8a",
        ElementKind.ConstraintBlock => "#c0392b",
        ElementKind.Actor or ElementKind.UseCase => "#b5651d",
        ElementKind.Activity or ElementKind.Action or ElementKind.StateMachine or ElementKind.State or ElementKind.Interaction or ElementKind.Lifeline or ElementKind.Message => "#2a7fbf",
        ElementKind.Package or ElementKind.Model => "#5b6577",
        ElementKind.Diagram => "#0b8a6a",
        ElementKind.TableView or ElementKind.QueryView => "#2d9cdb",
        ElementKind.ValidationRule => "#d9822b",
        ElementKind.Operation => "#8a5a00",
        ElementKind.Feature or ElementKind.Configuration or ElementKind.FeatureDecomposition or ElementKind.Requires or ElementKind.Excludes => "#8e44ad",
        ElementKind.TradeStudy => "#d35400",
        ElementKind.InstanceSpecification or ElementKind.Slot or ElementKind.Link => "#16a085",
        _ => "#6b7586",
    };

    public static string Glyph(ElementKind k) => k switch
    {
        ElementKind.Model => "M", ElementKind.Package => "P", ElementKind.Block => "B", ElementKind.InterfaceBlock => "IB",
        ElementKind.Class => "C", ElementKind.Interface => "I", ElementKind.ValueType => "VT", ElementKind.DataType => "DT",
        ElementKind.Enumeration => "E", ElementKind.EnumerationLiteral => "l", ElementKind.Component => "Cp", ElementKind.ConstraintBlock => "Cb",
        ElementKind.Property => "p", ElementKind.Port => "pt", ElementKind.Operation => "op", ElementKind.Requirement => "R",
        ElementKind.TestCase => "TC", ElementKind.Actor => "A", ElementKind.UseCase => "UC", ElementKind.Activity => "Ac",
        ElementKind.Action => "a", ElementKind.StateMachine => "SM", ElementKind.State => "S", ElementKind.Diagram => "D",
        ElementKind.Comment => "”", ElementKind.InitialNode or ElementKind.InitialState => "●", ElementKind.ActivityFinalNode or ElementKind.FinalState => "◉",
        ElementKind.DecisionNode => "◇", ElementKind.ForkNode => "▬", ElementKind.Pseudostate => "◇", ElementKind.InteractionOperand => "┄",
        ElementKind.Feature => "F", ElementKind.Configuration => "Cf", ElementKind.TradeStudy => "TS",
        ElementKind.InstanceSpecification => "i", ElementKind.Slot => "=",
        _ when Metamodel.IsRelationship(k) => "→",
        _ => "•",
    };

    public static string Label(ModelRepository m, Element e)
    {
        if (e is Relationship r)
        {
            var kw = Metamodel.Style(r.Kind).Keyword ?? Metamodel.DisplayName(r.Kind);
            var name = string.IsNullOrEmpty(r.Name) ? "" : r.Name + " ";
            return $"{name}«{kw}» {r.Source.DisplayName} → {r.Target.DisplayName}";
        }
        if (e.Kind == ElementKind.Requirement) return $"{e.Get(FieldKeys.RequirementId)} {e.DisplayName}".Trim();
        if (e.Kind == ElementKind.InstanceSpecification) return Meridian.Core.Instances.InstanceModel.Label(m, e);
        if (e.Kind == ElementKind.Slot) return Meridian.Core.Instances.InstanceModel.SlotLine(m, e);
        if (Metamodel.IsTypedFeature(e.Kind))
        {
            var t = m.TypeDisplayName(e);
            return string.IsNullOrEmpty(t) ? e.DisplayName : $"{e.DisplayName} : {t}";
        }
        if (e.Kind == ElementKind.Operation) return e.DisplayName + (e.Get(FieldKeys.Signature) is { Length: > 0 } s ? s : "()");
        if (e.Kind == ElementKind.Comment) { var b = e.Get(FieldKeys.Body); return "“" + (b.Length > 40 ? b[..40] + "…" : b) + "”"; }
        return e.DisplayName;
    }

    public static string Icon(ElementKind k) => k switch
    {
        ElementKind.Model => MudBlazor.Icons.Material.Outlined.Hub,
        ElementKind.Package => MudBlazor.Icons.Material.Outlined.Folder,
        ElementKind.Block => MudBlazor.Icons.Material.Outlined.ViewAgenda,
        ElementKind.InterfaceBlock => MudBlazor.Icons.Material.Outlined.SettingsInputComponent,
        ElementKind.ValueType => MudBlazor.Icons.Material.Outlined.Straighten,
        ElementKind.ConstraintBlock => MudBlazor.Icons.Material.Outlined.Functions,
        ElementKind.Class => MudBlazor.Icons.Material.Outlined.Class,
        ElementKind.Interface => MudBlazor.Icons.Material.Outlined.Cable,
        ElementKind.DataType => MudBlazor.Icons.Material.Outlined.DataObject,
        ElementKind.Enumeration => MudBlazor.Icons.Material.Outlined.FormatListNumbered,
        ElementKind.EnumerationLiteral => MudBlazor.Icons.Material.Outlined.Label,
        ElementKind.Component => MudBlazor.Icons.Material.Outlined.Widgets,
        ElementKind.Property => MudBlazor.Icons.Material.Outlined.Extension,
        ElementKind.Port => MudBlazor.Icons.Material.Outlined.Input,
        ElementKind.Operation => MudBlazor.Icons.Material.Outlined.PlayCircleOutline,
        ElementKind.Requirement => MudBlazor.Icons.Material.Outlined.Assignment,
        ElementKind.TestCase => MudBlazor.Icons.Material.Outlined.Science,
        ElementKind.Actor => MudBlazor.Icons.Material.Outlined.Person,
        ElementKind.UseCase => MudBlazor.Icons.Material.Outlined.Adjust,
        ElementKind.Activity => MudBlazor.Icons.Material.Outlined.AccountTree,
        ElementKind.Action => MudBlazor.Icons.Material.Outlined.SmartButton,
        ElementKind.InitialNode or ElementKind.InitialState => MudBlazor.Icons.Material.Filled.Circle,
        ElementKind.ActivityFinalNode or ElementKind.FinalState => MudBlazor.Icons.Material.Outlined.RadioButtonChecked,
        ElementKind.DecisionNode => MudBlazor.Icons.Material.Outlined.Diamond,
        ElementKind.ForkNode => MudBlazor.Icons.Material.Outlined.HorizontalRule,
        ElementKind.StateMachine => MudBlazor.Icons.Material.Outlined.Sync,
        ElementKind.Interaction => MudBlazor.Icons.Material.Outlined.SwapVert,
        ElementKind.Lifeline => MudBlazor.Icons.Material.Outlined.HorizontalSplit,
        ElementKind.Message => MudBlazor.Icons.Material.Outlined.East,
        ElementKind.CombinedFragment => MudBlazor.Icons.Material.Outlined.CropFree,
        ElementKind.InteractionOperand => MudBlazor.Icons.Material.Outlined.HorizontalSplit,
        ElementKind.Pseudostate => MudBlazor.Icons.Material.Outlined.Diamond,
        ElementKind.State => MudBlazor.Icons.Material.Outlined.RoundedCorner,
        ElementKind.Comment => MudBlazor.Icons.Material.Outlined.StickyNote2,
        ElementKind.Shape => MudBlazor.Icons.Material.Outlined.Category,
        ElementKind.Line => MudBlazor.Icons.Material.Outlined.NorthEast,
        ElementKind.Partition => MudBlazor.Icons.Material.Outlined.ViewColumn,
        ElementKind.InputPin => MudBlazor.Icons.Material.Outlined.Login,
        ElementKind.OutputPin => MudBlazor.Icons.Material.Outlined.Logout,
        ElementKind.ObjectNode => MudBlazor.Icons.Material.Outlined.Inventory2,
        ElementKind.Profile => MudBlazor.Icons.Material.Outlined.Badge,
        ElementKind.Stereotype => MudBlazor.Icons.Material.Outlined.LocalOffer,
        ElementKind.TagDefinition => MudBlazor.Icons.Material.Outlined.Sell,
        ElementKind.BpmnPool => MudBlazor.Icons.Material.Outlined.ViewStream,
        ElementKind.BpmnLane => MudBlazor.Icons.Material.Outlined.TableRows,
        ElementKind.BpmnNode => MudBlazor.Icons.Material.Outlined.CropSquare,
        ElementKind.ArchiElement => MudBlazor.Icons.Material.Outlined.Layers,
        ElementKind.Table => MudBlazor.Icons.Material.Outlined.TableChart,
        ElementKind.Column => MudBlazor.Icons.Material.Outlined.ViewColumn,
        ElementKind.ForeignKey => MudBlazor.Icons.Material.Outlined.Key,
        ElementKind.DbIndex => MudBlazor.Icons.Material.Outlined.Bolt,
        ElementKind.DbConstraint => MudBlazor.Icons.Material.Outlined.Rule,
        ElementKind.Sequence => MudBlazor.Icons.Material.Outlined.FormatListNumbered,
        ElementKind.Routine => MudBlazor.Icons.Material.Outlined.Functions,
        ElementKind.Trigger => MudBlazor.Icons.Material.Outlined.FlashOn,
        ElementKind.Binding => MudBlazor.Icons.Material.Outlined.DragHandle,
        ElementKind.ArchiRelation or ElementKind.BpmnFlow => MudBlazor.Icons.Material.Outlined.ArrowRightAlt,
        ElementKind.Diagram => MudBlazor.Icons.Material.Outlined.Schema,
        ElementKind.TableView => MudBlazor.Icons.Material.Outlined.TableChart,
        ElementKind.QueryView => MudBlazor.Icons.Material.Outlined.ManageSearch,
        ElementKind.ValidationRule => MudBlazor.Icons.Material.Outlined.Rule,
        ElementKind.Satisfy => MudBlazor.Icons.Material.Outlined.TaskAlt,
        ElementKind.Verify => MudBlazor.Icons.Material.Outlined.Verified,
        ElementKind.DeriveReqt => MudBlazor.Icons.Material.Outlined.CallSplit,
        ElementKind.Refine => MudBlazor.Icons.Material.Outlined.Tune,
        ElementKind.Trace => MudBlazor.Icons.Material.Outlined.Timeline,
        ElementKind.Copy => MudBlazor.Icons.Material.Outlined.ContentCopy,
        ElementKind.Allocate => MudBlazor.Icons.Material.Outlined.MoveDown,
        ElementKind.Generalization or ElementKind.Realization => MudBlazor.Icons.Material.Outlined.NorthEast,
        ElementKind.Composition or ElementKind.Aggregation => MudBlazor.Icons.Material.Outlined.Diamond,
        ElementKind.Include or ElementKind.Extend => MudBlazor.Icons.Material.Outlined.Login,
        ElementKind.Feature => MudBlazor.Icons.Material.Outlined.ToggleOn,
        ElementKind.Configuration => MudBlazor.Icons.Material.Outlined.Checklist,
        ElementKind.TradeStudy => MudBlazor.Icons.Material.Outlined.Balance,
        ElementKind.FeatureDecomposition => MudBlazor.Icons.Material.Outlined.AccountTree,
        ElementKind.Requires => MudBlazor.Icons.Material.Outlined.Link,
        ElementKind.Excludes => MudBlazor.Icons.Material.Outlined.LinkOff,
        ElementKind.TimingLifeline => MudBlazor.Icons.Material.Outlined.Timeline,
        ElementKind.DurationConstraint => MudBlazor.Icons.Material.Outlined.Straighten,
        ElementKind.TimeConstraint => MudBlazor.Icons.Material.Outlined.Schedule,
        ElementKind.InteractionUse => MudBlazor.Icons.Material.Outlined.CropFree,
        ElementKind.InlineInteraction => MudBlazor.Icons.Material.Outlined.SwapVert,
        ElementKind.CommunicationLifeline => MudBlazor.Icons.Material.Outlined.CropSquare,
        ElementKind.CommunicationLink => MudBlazor.Icons.Material.Outlined.HorizontalRule,
        ElementKind.InstanceSpecification => MudBlazor.Icons.Material.Outlined.DataObject,
        ElementKind.Slot => MudBlazor.Icons.Material.Outlined.DragHandle,
        ElementKind.Link => MudBlazor.Icons.Material.Outlined.Link,
        _ => MudBlazor.Icons.Material.Outlined.ArrowRightAlt,
    };

    public static string Icon(ElementKind k, string? subtype) => (k, subtype) switch
    {
        (ElementKind.Pseudostate, "junction") => MudBlazor.Icons.Material.Filled.Circle,
        (ElementKind.Pseudostate, "fork" or "join") => MudBlazor.Icons.Material.Outlined.HorizontalRule,
        (ElementKind.Pseudostate, "shallowHistory" or "deepHistory") => MudBlazor.Icons.Material.Outlined.History,
        (ElementKind.Pseudostate, "entryPoint") => MudBlazor.Icons.Material.Outlined.RadioButtonUnchecked,
        (ElementKind.Pseudostate, "exitPoint") => MudBlazor.Icons.Material.Outlined.HighlightOff,
        (ElementKind.Pseudostate, "terminate") => MudBlazor.Icons.Material.Outlined.Close,
        (ElementKind.ObjectNode, "datastore") => MudBlazor.Icons.Material.Outlined.Storage,
        (ElementKind.ObjectNode, "centralBuffer") => MudBlazor.Icons.Material.Outlined.Inbox,
        (ElementKind.BpmnNode, "startEvent") => MudBlazor.Icons.Material.Outlined.RadioButtonUnchecked,
        (ElementKind.BpmnNode, "endEvent") => MudBlazor.Icons.Material.Outlined.StopCircle,
        (ElementKind.BpmnNode, "intermediateEvent") => MudBlazor.Icons.Material.Outlined.TripOrigin,
        (ElementKind.BpmnNode, "timerEvent") => MudBlazor.Icons.Material.Outlined.Schedule,
        (ElementKind.BpmnNode, "messageEvent") => MudBlazor.Icons.Material.Outlined.MailOutline,
        (ElementKind.BpmnNode, "task") => MudBlazor.Icons.Material.Outlined.CropSquare,
        (ElementKind.BpmnNode, "userTask") => MudBlazor.Icons.Material.Outlined.PersonOutline,
        (ElementKind.BpmnNode, "serviceTask") => MudBlazor.Icons.Material.Outlined.Settings,
        (ElementKind.BpmnNode, "scriptTask") => MudBlazor.Icons.Material.Outlined.Code,
        (ElementKind.BpmnNode, "subProcess") => MudBlazor.Icons.Material.Outlined.AddBox,
        (ElementKind.BpmnNode, "exclusiveGateway") => MudBlazor.Icons.Material.Outlined.Close,
        (ElementKind.BpmnNode, "parallelGateway") => MudBlazor.Icons.Material.Outlined.Add,
        (ElementKind.BpmnNode, "inclusiveGateway") => MudBlazor.Icons.Material.Outlined.RadioButtonUnchecked,
        (ElementKind.BpmnNode, "dataObject") => MudBlazor.Icons.Material.Outlined.InsertDriveFile,
        (ElementKind.BpmnNode, "dataStore") => MudBlazor.Icons.Material.Outlined.Storage,
        (ElementKind.BpmnFlow, "message") => MudBlazor.Icons.Material.Outlined.ForwardToInbox,
        (ElementKind.BpmnFlow, "association") => MudBlazor.Icons.Material.Outlined.MoreHoriz,
        (ElementKind.BpmnFlow, _) => MudBlazor.Icons.Material.Outlined.ArrowRightAlt,
        (ElementKind.ArchiRelation, "composition" or "aggregation") => MudBlazor.Icons.Material.Outlined.Diamond,
        (ElementKind.ArchiRelation, "realization" or "specialization") => MudBlazor.Icons.Material.Outlined.NorthEast,
        (ElementKind.ArchiRelation, "assignment") => MudBlazor.Icons.Material.Outlined.Adjust,
        (ElementKind.ArchiRelation, "access") => MudBlazor.Icons.Material.Outlined.MoreHoriz,
        (ElementKind.ArchiRelation, "influence") => MudBlazor.Icons.Material.Outlined.TrendingUp,
        (ElementKind.ArchiRelation, "triggering" or "flow") => MudBlazor.Icons.Material.Outlined.DoubleArrow,
        _ => Icon(k),
    };

    public static string ArchiColor(string subtype) => Subtypes.Layer(subtype) switch
    {
        ArchiLayer.Business => "#e5c54a",
        ArchiLayer.Application => "#4ec9d6",
        ArchiLayer.Technology => "#7ec46a",
        ArchiLayer.Motivation => "#9a8cff",
        ArchiLayer.Strategy => "#e0a85a",
        ArchiLayer.Implementation => "#f08f8f",
        _ => "#9aa6bd",
    };

    public static string DiagramIcon(DiagramType t) => t switch
    {
        DiagramType.BlockDefinition => MudBlazor.Icons.Material.Outlined.ViewQuilt,
        DiagramType.InternalBlock => MudBlazor.Icons.Material.Outlined.Hub,
        DiagramType.Requirement => MudBlazor.Icons.Material.Outlined.Assignment,
        DiagramType.UseCase => MudBlazor.Icons.Material.Outlined.Person,
        DiagramType.Activity => MudBlazor.Icons.Material.Outlined.AccountTree,
        DiagramType.StateMachine => MudBlazor.Icons.Material.Outlined.Sync,
        DiagramType.Package => MudBlazor.Icons.Material.Outlined.Folder,
        DiagramType.Sequence => MudBlazor.Icons.Material.Outlined.SwapVert,
        DiagramType.Parametric => MudBlazor.Icons.Material.Outlined.Functions,
        DiagramType.Bpmn => MudBlazor.Icons.Material.Outlined.AccountTree,
        DiagramType.ArchiMate => MudBlazor.Icons.Material.Outlined.Layers,
        DiagramType.Database => MudBlazor.Icons.Material.Outlined.Storage,
        DiagramType.Whiteboard => MudBlazor.Icons.Material.Outlined.Draw,
        DiagramType.FeatureModel => MudBlazor.Icons.Material.Outlined.ToggleOn,
        DiagramType.Timing => MudBlazor.Icons.Material.Outlined.Timeline,
        DiagramType.InteractionOverview => MudBlazor.Icons.Material.Outlined.Schema,
        DiagramType.Communication => MudBlazor.Icons.Material.Outlined.Hub,
        DiagramType.Object => MudBlazor.Icons.Material.Outlined.DataObject,
        _ when UafViewpoints.IsUaf(t) => MudBlazor.Icons.Material.Outlined.Widgets,
        _ => MudBlazor.Icons.Material.Outlined.Class,
    };

    /// <summary>Relationship kinds offered when dropping one element onto another.</summary>
    public static readonly ElementKind[] LinkKinds =
    [
        ElementKind.Satisfy, ElementKind.Verify, ElementKind.Refine, ElementKind.DeriveReqt, ElementKind.Trace, ElementKind.Allocate,
        ElementKind.Composition, ElementKind.Generalization, ElementKind.Association, ElementKind.Dependency,
    ];

    public static string KindName(Element e) => e is Diagram d ? Metamodel.DisplayName(d.DiagramType) : Meridian.Core.DataModeling.DbModel.KindName(e) ?? Metamodel.DisplayName(e.Kind);

    /// <summary>The icon of a particular element: databases, schemas, subject areas and views have their own.</summary>
    public static string IconOf(Element e) => e switch
    {
        Diagram d => DiagramIcon(d.DiagramType),
        _ when Meridian.Core.DataModeling.DbModel.IsDatabase(e) => MudBlazor.Icons.Material.Outlined.Storage,
        _ when Meridian.Core.DataModeling.DbModel.IsSchema(e) => MudBlazor.Icons.Material.Outlined.SnippetFolder,
        _ when Meridian.Core.DataModeling.DbModel.IsGroup(e) => MudBlazor.Icons.Material.Outlined.Workspaces,
        _ when Meridian.Core.DataModeling.DbModel.IsView(e) => MudBlazor.Icons.Material.Outlined.Preview,
        _ => Icon(e.Kind, e.Get(FieldKeys.Subtype)),
    };
}
