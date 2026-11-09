using Meridian.Core.Model;

namespace Meridian.Core.Language;

public enum ArchiLayer { Strategy, Business, Application, Technology, Physical, Motivation, Implementation, Other }

/// <summary>Subtype catalogue for BPMN and ArchiMate elements and relationships (stored in the <c>subtype</c> field).</summary>
public static class Subtypes
{
    public static readonly IReadOnlyList<string> BpmnNodes =
    [
        "startEvent", "intermediateEvent", "timerEvent", "messageEvent", "endEvent",
        "task", "userTask", "serviceTask", "scriptTask", "subProcess",
        "exclusiveGateway", "parallelGateway", "inclusiveGateway", "eventGateway",
        "dataObject", "dataStore",
    ];

    public static readonly IReadOnlyList<string> BpmnFlows = ["sequence", "message", "association"];

    public static readonly IReadOnlyList<string> ArchiElements =
    [
        // Strategy
        "capability", "valueStream", "resource", "courseOfAction",
        // Business
        "businessActor", "businessRole", "businessProcess", "businessFunction", "businessEvent", "businessService", "businessObject",
        // Application
        "applicationComponent", "applicationInterface", "applicationFunction", "applicationService", "dataObject",
        // Technology
        "node", "device", "systemSoftware", "technologyService", "communicationNetwork", "artifact",
        // Motivation
        "stakeholder", "driver", "goal", "outcome", "principle", "requirement", "constraint",
        // Implementation & migration
        "workPackage", "deliverable", "plateau",
    ];

    /// <summary>Free shapes (general drawing), in palette order.</summary>
    public static readonly IReadOnlyList<string> Shapes =
    [
        "rectangle", "rounded", "ellipse", "diamond", "text", "cylinder", "hexagon", "parallelogram", "triangle", "document", "cloud", "callout",
    ];

    /// <summary>Plain connector styles.</summary>
    public static readonly IReadOnlyList<string> Lines = ["arrow", "line", "dashedArrow", "dashed", "bidirectional", "dotted"];

    public static readonly IReadOnlyList<string> ArchiRelations =
    [
        "composition", "aggregation", "assignment", "realization", "serving", "access", "influence", "triggering", "flow", "specialization", "association",
    ];

    public static IReadOnlyList<string> For(ElementKind k) => k switch
    {
        ElementKind.BpmnNode => BpmnNodes,
        ElementKind.BpmnFlow => BpmnFlows,
        ElementKind.ArchiElement => ArchiElements,
        ElementKind.ArchiRelation => ArchiRelations,
        ElementKind.Shape => Shapes,
        ElementKind.Line => Lines,
        _ => [],
    };

    public static string Display(string subtype)
    {
        if (string.IsNullOrEmpty(subtype)) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var c in subtype)
        {
            if (char.IsUpper(c) && sb.Length > 0) sb.Append(' ');
            sb.Append(sb.Length == 0 ? char.ToUpperInvariant(c) : c);
        }
        return sb.ToString();
    }

    public static ArchiLayer Layer(string subtype) => subtype switch
    {
        "capability" or "valueStream" or "resource" or "courseOfAction" => ArchiLayer.Strategy,
        "businessActor" or "businessRole" or "businessProcess" or "businessFunction" or "businessEvent" or "businessService" or "businessObject" => ArchiLayer.Business,
        "applicationComponent" or "applicationInterface" or "applicationFunction" or "applicationService" or "dataObject" => ArchiLayer.Application,
        "node" or "device" or "systemSoftware" or "technologyService" or "communicationNetwork" or "artifact" => ArchiLayer.Technology,
        "stakeholder" or "driver" or "goal" or "outcome" or "principle" or "requirement" or "constraint" => ArchiLayer.Motivation,
        "workPackage" or "deliverable" or "plateau" => ArchiLayer.Implementation,
        _ => ArchiLayer.Other,
    };

    /// <summary>ArchiMate's conventional layer colours (light, dark).</summary>
    public static (string Light, string Dark) LayerColor(ArchiLayer l) => l switch
    {
        ArchiLayer.Strategy => ("#f5deaa", "#4a3d22"),
        ArchiLayer.Business => ("#ffffb5", "#4a4520"),
        ArchiLayer.Application => ("#b5ffff", "#1f4a4a"),
        ArchiLayer.Technology => ("#c9e7b7", "#2e4426"),
        ArchiLayer.Motivation => ("#ccccff", "#33335a"),
        ArchiLayer.Implementation => ("#ffe0e0", "#4a2b2b"),
        _ => ("#eeeeee", "#2a3242"),
    };

    /// <summary>Small glyph drawn in the top-right corner of ArchiMate boxes (stands in for the notation icon).</summary>
    public static string ArchiGlyph(string subtype) => subtype switch
    {
        "businessActor" or "stakeholder" => "웃",
        "businessRole" => "◯",
        "businessProcess" or "applicationFunction" or "businessFunction" => "➜",
        "businessEvent" => "⌁",
        "businessService" or "applicationService" or "technologyService" => "▭",
        "businessObject" or "dataObject" or "artifact" => "▤",
        "applicationComponent" => "▣",
        "applicationInterface" => "○—",
        "node" => "▢",
        "device" => "⌨",
        "systemSoftware" => "◎",
        "communicationNetwork" => "⇄",
        "goal" or "outcome" => "◎",
        "driver" => "✱",
        "principle" => "!",
        "requirement" or "constraint" => "▱",
        "capability" => "▥",
        "valueStream" => "➤",
        "resource" => "▦",
        "workPackage" => "↻",
        "deliverable" => "▧",
        "plateau" => "≡",
        _ => "",
    };

    public static NodeShape BpmnShape(string subtype) => subtype switch
    {
        "startEvent" or "intermediateEvent" or "timerEvent" or "messageEvent" or "endEvent" => NodeShape.Event,
        "exclusiveGateway" or "parallelGateway" or "inclusiveGateway" or "eventGateway" => NodeShape.Gateway,
        "dataObject" or "dataStore" => NodeShape.DataObject,
        _ => NodeShape.Rounded,
    };

    public static (double W, double H) BpmnSize(string subtype) => BpmnShape(subtype) switch
    {
        NodeShape.Event => (36, 36),
        NodeShape.Gateway => (46, 46),
        NodeShape.DataObject => (40, 50),
        _ when subtype == "subProcess" => (180, 100),
        _ => (130, 70),
    };

    public static EdgeStyle BpmnFlowStyle(string subtype) => subtype switch
    {
        "message" => new(true, ArrowHead.Open, ArrowHead.Dot, null),
        "association" => new(true, ArrowHead.None, ArrowHead.None, null, Dotted: true),
        _ => new(false, ArrowHead.Filled, ArrowHead.None, null),
    };

    public static EdgeStyle ArchiStyle(string subtype) => subtype switch
    {
        "composition" => new(false, ArrowHead.None, ArrowHead.FilledDiamond, null),
        "aggregation" => new(false, ArrowHead.None, ArrowHead.HollowDiamond, null),
        "assignment" => new(false, ArrowHead.Filled, ArrowHead.Dot, null),
        "realization" => new(false, ArrowHead.HollowTriangle, ArrowHead.None, null, Dotted: true),
        "serving" => new(false, ArrowHead.Open, ArrowHead.None, null),
        "access" => new(false, ArrowHead.Open, ArrowHead.None, null, Dotted: true),
        "influence" => new(true, ArrowHead.Open, ArrowHead.None, null),
        "triggering" => new(false, ArrowHead.Filled, ArrowHead.None, null),
        "flow" => new(true, ArrowHead.Filled, ArrowHead.None, null),
        "specialization" => new(false, ArrowHead.HollowTriangle, ArrowHead.None, null),
        _ => new(false, ArrowHead.None, ArrowHead.None, null),
    };

    /// <summary>The outline used for connector clipping and hit testing of a free shape.</summary>
    public static NodeShape ShapeOutline(string subtype) => subtype switch
    {
        "ellipse" or "cloud" => NodeShape.Ellipse,
        "diamond" => NodeShape.Diamond,
        "rounded" => NodeShape.Rounded,
        _ => NodeShape.Rectangle,
    };

    public static (double W, double H) ShapeSize(string subtype) => subtype switch
    {
        "ellipse" => (130, 80),
        "diamond" => (110, 90),
        "text" => (120, 36),
        "cylinder" => (90, 110),
        "hexagon" or "parallelogram" => (140, 70),
        "triangle" => (100, 90),
        "cloud" => (150, 95),
        "callout" => (150, 90),
        _ => (130, 70),
    };

    public static EdgeStyle LineStyle(string subtype) => subtype switch
    {
        "line" => new(false, ArrowHead.None, ArrowHead.None, null),
        "dashed" => new(true, ArrowHead.None, ArrowHead.None, null),
        "dashedArrow" => new(true, ArrowHead.Filled, ArrowHead.None, null),
        "bidirectional" => new(false, ArrowHead.Filled, ArrowHead.Filled, null),
        "dotted" => new(false, ArrowHead.Open, ArrowHead.None, null, Dotted: true),
        _ => new(false, ArrowHead.Filled, ArrowHead.None, null),
    };
}
