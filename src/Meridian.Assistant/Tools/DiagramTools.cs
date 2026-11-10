using System.Text.Json;
using System.Text.Json.Nodes;
using Meridian.Assistant.Changes;
using Meridian.Core.Language;
using Meridian.Core.Model;
using static Meridian.Assistant.Tools.ToolSchema;

namespace Meridian.Assistant.Tools;

/// <summary><c>plan_diagrams</c>: what Meridian's deterministic auto-diagrammer would create (read-only).</summary>
public sealed class PlanDiagramsTool : AssistantTool
{
    public override string Name => "plan_diagrams";
    public override string Description =>
        "See which diagrams Meridian's auto-diagrammer would create for the model or one package: a package overview, a block definition " +
        "diagram per package with blocks, an internal block diagram per block with parts, a requirement diagram per requirements package, a " +
        "diagram per activity or state machine without one, and use case diagrams. Each entry gives the diagram type, a suggested name, the owner, " +
        "the element ids it would show, its shape count, and whether it is new or updates an earlier auto diagram. Elements already on a diagram of " +
        "that kind are skipped unless regenerate is true. Read-only: use it to decide which views tell the story, then propose them with create_diagrams.";

    public override JsonObject Schema { get; } = Object(
        Opt("scope_id", String("Id of a package to plan for; omit for the whole model.")),
        Opt("regenerate", Boolean("Also plan views for elements already shown on diagrams (default false).")));

    public override string Describe(JsonElement input) => "Planning diagrams" + (input.Str("scope_id") is { Length: > 0 } s ? $" for {s}" : "");

    protected override ToolResult Execute(ToolContext ctx, JsonElement input)
    {
        Element? scope = null;
        if (input.Str("scope_id") is { Length: > 0 } id)
        {
            scope = ctx.Resolve(id, "scope_id").Existing ?? throw new ToolInputException("scope_id must be an existing package.");
            if (scope.Kind is not (ElementKind.Package or ElementKind.Model)) throw new ToolInputException("scope_id must be a package or the model root.");
        }
        var plan = AutoDiagrammer.Plan(ctx.Model, new AutoDiagramOptions { Scope = scope, Regenerate = input.Bool("regenerate") ?? false, DerivePackageDependencies = false });
        return ToolResult.Ok(new
        {
            scope = (scope ?? ctx.Model.Root).QualifiedName is { Length: > 0 } q ? q : ctx.Model.Root.Name,
            existing_diagrams = ctx.Model.Diagrams.Count(),
            planned = plan.Diagrams.Count,
            diagrams = plan.Diagrams.Take(60).Select(p => new
            {
                diagram_type = p.Type.ToString(),
                name = p.Name,
                owner_id = p.Owner.Id,
                owner = p.Owner.QualifiedName is { Length: > 0 } oq ? oq : p.Owner.Name,
                status = p.Status == AutoDiagramStatus.New ? "new" : "update",
                shapes = p.NodeCount,
                purpose = p.Purpose,
                element_ids = p.Elements.Take(40).Select(e => e.Id),
                elements = p.Elements.Take(40).Select(e => $"{e.DisplayName} [{e.Kind}]"),
                more_elements = Math.Max(0, p.Elements.Count - 40),
            }),
            note = plan.IsEmpty ? "Every element is already on a diagram of its kind; pass regenerate true to plan fresh views anyway." : null,
        });
    }
}

/// <summary><c>create_diagrams</c>: several laid-out diagrams at once, one reviewable proposal each.</summary>
public sealed class CreateDiagramsTool : WriteTool
{
    private const int MaxDiagrams = 20;
    private const int MaxShapes = 60;

    private static readonly string[] Types = AutoDiagrammer.SupportedTypes.Select(t => t.ToString()).ToArray();

    public override string Name => "create_diagrams";
    public override string Description =>
        "Propose one or more diagrams (each is its own item in the diff card, accepted or rejected separately), laid out by Meridian's " +
        "auto-diagrammer: layered for hierarchies and flows, a grid for loose sets, packages and composite states nested, IBD parts with their ports " +
        "and connectors, no overlaps, orthogonal connectors. For each diagram give the type, the owner (Package/BlockDefinition/Requirement/UseCase/" +
        "Class → a package or the model root; InternalBlock → the block whose parts it shows; Activity → the Activity; StateMachine → the StateMachine), " +
        "a short readable name, a one-line purpose saying what the view is for, and the elements to show. Omit element_ids to show the owner's default " +
        $"content (as plan_diagrams lists it) and use exclude_ids to leave things out. At most {MaxShapes} shapes per diagram; split bigger views. " +
        "Relationships between shown elements are drawn automatically. " + ProposalNote;

    public override JsonObject Schema { get; } = Object(
        Req("diagrams", Array(Object(
            Req("diagram_type", String("Diagram type.", Types)),
            Req("owner_id", String("Owning element id (or new:N).")),
            Req("name", String("Readable diagram name, e.g. \"Drone – internal structure\".")),
            Req("purpose", String("One line: what this view shows and why it matters.")),
            Opt("element_ids", Array(String("Element id or new:N."), "Elements to show; omit for the owner's default content.")),
            Opt("exclude_ids", Array(String("Element id."), "Elements to leave out of the default content."))),
            $"The diagrams to propose (1 to {MaxDiagrams}).")));

    public override string Describe(JsonElement input) => $"Proposing {input.Arr("diagrams").Count()} diagram(s)";

    protected override ToolResult Execute(ToolContext ctx, JsonElement input)
    {
        var items = input.Arr("diagrams").ToList();
        if (items.Count == 0) throw new ToolInputException("diagrams must contain at least one diagram.");
        if (items.Count > MaxDiagrams) throw new ToolInputException($"At most {MaxDiagrams} diagrams per call.");
        var proposed = new List<object>();
        var problems = new List<string>();
        for (var i = 0; i < items.Count; i++)
        {
            try { proposed.Add(ProposeOne(ctx, items[i])); }
            catch (ToolInputException e) { problems.Add($"diagrams[{i}] (\"{items[i].Str("name")}\"): {e.Message}"); }
        }
        if (proposed.Count == 0) return ToolResult.Error("No diagram was proposed:\n" + string.Join("\n", problems));
        return ToolResult.Ok(new { proposed, problems = problems.Count > 0 ? problems : null, note = ProposalNote });
    }

    private static object ProposeOne(ToolContext ctx, JsonElement item)
    {
        if (!Enum.TryParse<DiagramType>(item.Str("diagram_type"), out var type) || !AutoDiagrammer.SupportedTypes.Contains(type))
            throw new ToolInputException($"diagram_type must be one of {string.Join(", ", Types)}.");
        var owner = ctx.Resolve(item.Str("owner_id"), "owner_id");
        if (!Metamodel.IsValidDiagramContext(type, owner.Kind))
            throw new ToolInputException($"A {Metamodel.DisplayName(type)} cannot be owned by a {Metamodel.DisplayName(owner.Kind)}.");
        var name = item.Str("name")!.Trim();
        if (name.Length == 0) throw new ToolInputException("name must not be empty.");
        var purpose = item.Str("purpose")!.Trim();
        if (purpose.Length == 0) throw new ToolInputException("purpose must not be empty.");
        if (owner.Existing is { } oe && oe.OwnedElements.OfType<Diagram>().Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ToolInputException($"{owner.Display} already has a diagram named \"{name}\"; pick another name.");
        if (ctx.Changes.Items.Any(p => p.Kind == ProposalKind.CreateDiagram && p.OwnerRef == (owner.Existing?.Id ?? owner.Pending!.Ref) && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ToolInputException($"A diagram named \"{name}\" is already proposed for that owner.");

        var refs = new List<string>();
        var existing = new List<Element>();
        var pendingCount = 0;
        if (item.TryGetProperty("element_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            foreach (var raw in ids.EnumerateArray().Select(x => x.GetString()!))
            {
                var t = ctx.Resolve(raw, "element_ids");
                if (t.Kind == ElementKind.Diagram || Metamodel.IsRelationship(t.Kind))
                    throw new ToolInputException($"{t.Display} cannot be shown as a shape; relationships are drawn automatically.");
                if (t.Existing is { } e && owner.Existing is { } o && AutoDiagrammer.CheckShape(type, o, e) is { } why) throw new ToolInputException(why);
                var r = t.Existing?.Id ?? t.Pending!.Ref!;
                if (refs.Contains(r)) continue;
                refs.Add(r);
                if (t.Existing is { } ex) existing.Add(ex); else pendingCount++;
            }
        }
        else
        {
            if (owner.Existing is not { } o) throw new ToolInputException("Give element_ids for a diagram owned by an element proposed in this turn.");
            existing = AutoDiagrammer.DefaultContent(ctx.Model, type, o);
            refs = existing.Select(e => e.Id).ToList();
        }
        var exclude = item.Arr("exclude_ids").Select(x => x.GetString()).ToHashSet();
        if (exclude.Count > 0)
        {
            refs.RemoveAll(exclude.Contains);
            existing.RemoveAll(e => exclude.Contains(e.Id));
        }
        if (refs.Count == 0) throw new ToolInputException("The diagram would be empty: give element_ids (or exclude fewer).");
        var shapes = (owner.Existing is { } ow ? AutoDiagrammer.ShapeCount(ctx.Model, type, ow, existing) : existing.Count) + pendingCount;
        if (shapes > MaxShapes) throw new ToolInputException($"That diagram would have {shapes} shapes (at most {MaxShapes}); split it into several focused views.");

        var p = new Proposal
        {
            Number = ctx.Changes.Items.Count + 1,
            Kind = ProposalKind.CreateDiagram,
            Ref = ctx.NewRef(),
            DiagramType = type,
            Name = name,
            OwnerRef = owner.Existing?.Id ?? owner.Pending!.Ref,
            ElementRefs = refs,
            Purpose = purpose,
            Reason = purpose,
            Title = $"New {Metamodel.DisplayName(type)} \"{name}\" in {owner.Display}: {shapes} shape{(shapes == 1 ? "" : "s")} (auto-laid out)",
        };
        ctx.Changes.Add(p);
        return new { proposed = p.Title, reference = p.Ref, change = p.Number, shapes };
    }
}
