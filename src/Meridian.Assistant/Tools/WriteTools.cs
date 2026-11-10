using System.Text.Json;
using System.Text.Json.Nodes;
using Meridian.Assistant.Changes;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization;
using static Meridian.Assistant.Tools.ToolSchema;

namespace Meridian.Assistant.Tools;

/// <summary>Shared checks for the write tools: they only ever PROPOSE changes.</summary>
public abstract class WriteTool : AssistantTool
{
    public override bool IsWrite => true;

    internal static readonly string[] ElementKindNames = Metamodel.ElementKinds.Where(k => k != ElementKind.Model).Select(k => k.ToString()).ToArray();
    internal static readonly string[] RelationshipKindNames = Metamodel.RelationshipKinds.Select(k => k.ToString()).ToArray();
    internal static readonly string[] DiagramTypeNames = Enum.GetNames<DiagramType>();

    protected const string ProposalNote =
        "Proposed only: the user reviews every proposed change in a diff card and applies them together. Do not claim it is done.";

    protected static ToolResult Proposed(Proposal p, object? extra = null) =>
        ToolResult.Ok(new { proposed = p.Title, reference = p.Ref, change = p.Number, extra, note = ProposalNote });

    /// <summary>Validates field edits (known enumerations, unique requirement ids) and returns the normalised list.</summary>
    protected static List<KeyValuePair<string, string?>> CheckFields(ToolContext ctx, ElementKind kind, IEnumerable<KeyValuePair<string, string?>> fields, Element? existing)
    {
        var list = new List<KeyValuePair<string, string?>>();
        foreach (var (rawKey, value) in fields)
        {
            var key = rawKey switch
            {
                "verificationMethod" or "verification" => ReqFields.VerificationMethod,
                "id" or "requirementId" => FieldKeys.RequirementId,
                "doc" => FieldKeys.Documentation,
                _ => rawKey,
            };
            if (key.Length == 0) throw new ToolInputException("A field key is empty.");
            if (key.StartsWith("suspect", StringComparison.Ordinal)) throw new ToolInputException($"Field '{key}' is managed by Meridian.");
            if (key == ReqFields.VerificationMethod && value != null && !ReqFields.VerificationMethods.Contains(value))
                throw new ToolInputException($"verifyMethod must be one of: {string.Join(", ", ReqFields.VerificationMethods)}.");
            if (key == ReqFields.Priority && value != null && !ReqFields.Priorities.Contains(value))
                throw new ToolInputException($"priority must be one of: {string.Join(", ", ReqFields.Priorities)}.");
            if (key == ReqFields.Status && value != null && !ReqFields.Statuses.Contains(value))
                throw new ToolInputException($"status must be one of: {string.Join(", ", ReqFields.Statuses)}.");
            if (key == FieldKeys.RequirementId && value != null)
            {
                if (kind != ElementKind.Requirement) throw new ToolInputException("reqId only applies to requirements.");
                var clash = ctx.Model.OfKind(ElementKind.Requirement).FirstOrDefault(r => !ReferenceEquals(r, existing) && r.Get(FieldKeys.RequirementId) == value);
                if (clash != null) throw new ToolInputException($"Requirement id {value} is already used by '{clash.Name}' ({clash.Id}).");
                if (ctx.Changes.Items.Any(p => p.Fields.Any(f => f.Key == FieldKeys.RequirementId && f.Value == value) && p.SubjectRef != existing?.Id))
                    throw new ToolInputException($"Requirement id {value} is already used by another proposed requirement.");
            }
            list.RemoveAll(f => f.Key == key);
            list.Add(new(key, value));
        }
        return list;
    }

    protected static ElementKind ParseKind(string? s, string what)
    {
        if (s == null || !Enum.TryParse<ElementKind>(s, ignoreCase: true, out var k) || !Enum.IsDefined(k)) throw new ToolInputException($"Unknown {what} '{s}'.");
        return k;
    }
}

/// <summary><c>create_element</c>.</summary>
public sealed class CreateElementTool : WriteTool
{
    public override string Name => "create_element";
    public override string Description =>
        "Propose a new model element (block, requirement, part property, port, activity, action, use case, package…) owned by an existing element " +
        "or by one proposed earlier in this turn (new:N). Requirements get the next requirement id automatically unless you pass reqId in fields. " +
        "Returns a new:N reference you can use as owner, relationship end or diagram content in later calls. " + ProposalNote;

    public override JsonObject Schema { get; } = Object(
        Req("kind", String("Element kind.", ElementKindNames)),
        Req("owner_id", String("Id of the owning package/element, or new:N.")),
        Req("name", String("Element name.")),
        Opt("fields", FieldList("Optional fields, e.g. text, reqId, rationale, verifyMethod (test/analysis/inspection/demonstration), priority, documentation, type, multiplicity, direction.")));

    public override string Describe(JsonElement input) => $"Proposing {input.Str("kind")} \"{input.Str("name")}\"";

    protected override ToolResult Execute(ToolContext ctx, JsonElement input)
    {
        var kind = ParseKind(input.Str("kind"), "kind");
        if (Metamodel.IsRelationship(kind) || kind is ElementKind.Diagram or ElementKind.Model)
            throw new ToolInputException($"Use {(kind == ElementKind.Diagram ? "create_diagram_with_elements" : "create_relationship")} for a {kind}.");
        var owner = ctx.Resolve(input.Str("owner_id"), "owner_id");
        if (owner.Kind == ElementKind.Diagram || owner.Existing is Relationship)
            throw new ToolInputException("A diagram or relationship cannot own elements.");
        if (!Metamodel.CanOwn(owner.Kind, kind))
            throw new ToolInputException($"A {Metamodel.DisplayName(owner.Kind)} cannot own a {Metamodel.DisplayName(kind)}. Pick another owner (e.g. a package).");
        var name = input.Str("name")!.Trim();
        if (name.Length == 0 && Metamodel.NeedsName(kind)) throw new ToolInputException("name must not be empty.");
        var fields = CheckFields(ctx, kind, input.Fields("fields").Where(f => f.Key != FieldKeys.Name), null);
        if (fields.FirstOrDefault(f => f.Key == FieldKeys.Type).Value is { } typeRef && typeRef.StartsWith('_') && ctx.Model.Find(typeRef) == null)
            throw new ToolInputException($"type '{typeRef}' is not an element id in this model.");
        var p = new Proposal
        {
            Number = ctx.Changes.Items.Count + 1,
            Kind = ProposalKind.CreateElement,
            Ref = ctx.NewRef(),
            ElementKind = kind,
            Name = name,
            OwnerRef = owner.Existing?.Id ?? owner.Pending!.Ref,
            Fields = fields,
            Title = $"New {Metamodel.DisplayName(kind)} \"{name}\" in {owner.Display}",
        };
        ctx.Changes.Add(p);
        return Proposed(p);
    }
}

/// <summary><c>update_fields</c>.</summary>
public sealed class UpdateFieldsTool : WriteTool
{
    public override string Name => "update_fields";
    public override string Description =>
        "Propose new values for fields of an existing element: name, documentation, requirement text, rationale, verifyMethod, priority, status, " +
        "type, multiplicity, defaultValue, stereotypes (comma separated), tag:<name>… An empty value clears the field. " + ProposalNote;

    public override JsonObject Schema { get; } = Object(
        Req("element_id", String("Id of the element to change.")),
        Req("fields", FieldList("Fields to set. Use key \"name\" to rename.")));

    public override string Describe(JsonElement input) => $"Proposing edits to {input.Str("element_id")}";

    protected override ToolResult Execute(ToolContext ctx, JsonElement input)
    {
        var t = ctx.Resolve(input.Str("element_id"), "element_id");
        if (t.Existing is not { } e) throw new ToolInputException("Pass fields directly to create_element for elements proposed in this turn.");
        if (e.Kind == ElementKind.Model && input.Fields("fields").Any(f => f.Key != FieldKeys.Name && f.Key != FieldKeys.Documentation))
            throw new ToolInputException("Only the model root's name and documentation can change.");
        var fields = CheckFields(ctx, e.Kind, input.Fields("fields"), e);
        if (fields.Count == 0) throw new ToolInputException("fields must not be empty.");
        if (fields.Any(f => f.Key == FieldKeys.Name && string.IsNullOrWhiteSpace(f.Value) && Metamodel.NeedsName(e.Kind)))
            throw new ToolInputException("The name cannot be cleared.");
        var changes = fields.Select(f => new FieldChange(f.Key, e.Fields.TryGetValue(f.Key, out var old) ? old : null, f.Value))
                            .Where(c => c.Old != c.New).ToList();
        if (changes.Count == 0) throw new ToolInputException("Those fields already have these values; nothing to change.");
        var p = new Proposal
        {
            Number = ctx.Changes.Items.Count + 1,
            Kind = ProposalKind.UpdateFields,
            SubjectRef = e.Id,
            Fields = changes.Select(c => new KeyValuePair<string, string?>(c.Key, c.New)).ToList(),
            FieldChanges = changes,
            Title = $"Edit {ToolContext.Describe(e)}: {string.Join(", ", changes.Select(c => c.Key))}",
        };
        ctx.Changes.Add(p);
        return Proposed(p);
    }
}

/// <summary><c>create_relationship</c>.</summary>
public sealed class CreateRelationshipTool : WriteTool
{
    public override string Name => "create_relationship";
    public override string Description =>
        "Propose a relationship between two elements (existing ids or new:N). Directions: Satisfy = design element → requirement; " +
        "Verify = test case → requirement; DeriveReqt = derived (client) requirement → source requirement; Refine = refining element → requirement; " +
        "Trace = any → any; Allocate = function/activity → structure; Generalization = specific → general; Composition = whole → part type. " +
        "For suggested trace links always give confidence (0–1) and a one-sentence reason; the user accepts or rejects each one. " + ProposalNote;

    public override JsonObject Schema { get; } = Object(
        Req("kind", String("Relationship kind.", RelationshipKindNames)),
        Req("source_id", String("Source element id or new:N.")),
        Req("target_id", String("Target element id or new:N.")),
        Opt("confidence", Number("How sure you are this link is right, from 0 to 1 (for suggested links).")),
        Opt("reason", String("One sentence explaining why the link holds (for suggested links).")));

    public override string Describe(JsonElement input) => $"Proposing {input.Str("kind")} link";

    protected override ToolResult Execute(ToolContext ctx, JsonElement input)
    {
        var kind = ParseKind(input.Str("kind"), "relationship kind");
        if (!Metamodel.IsRelationship(kind)) throw new ToolInputException($"'{kind}' is not a relationship kind.");
        var s = ctx.Resolve(input.Str("source_id"), "source_id");
        var t = ctx.Resolve(input.Str("target_id"), "target_id");
        if (Metamodel.CheckEnds(kind, s.Kind, t.Kind) is { } problem) throw new ToolInputException(problem);
        if (s.Existing != null && t.Existing != null && ctx.Model.Outgoing(s.Existing, kind).Any(r => ReferenceEquals(r.Target, t.Existing)))
            throw new ToolInputException($"A {kind} from {s.Display} to {t.Display} already exists.");
        var sRef = s.Existing?.Id ?? s.Pending!.Ref;
        var tRef = t.Existing?.Id ?? t.Pending!.Ref;
        if (ctx.Changes.Items.Any(p => p.Kind == ProposalKind.CreateRelationship && p.ElementKind == kind && p.SourceRef == sRef && p.TargetRef == tRef))
            throw new ToolInputException("That relationship is already proposed in this turn.");
        var confidence = input.Num("confidence");
        if (confidence is < 0 or > 1) throw new ToolInputException("confidence must be between 0 and 1.");
        var p = new Proposal
        {
            Number = ctx.Changes.Items.Count + 1,
            Kind = ProposalKind.CreateRelationship,
            Ref = ctx.NewRef(),
            ElementKind = kind,
            SourceRef = sRef,
            TargetRef = tRef,
            Confidence = confidence,
            Reason = input.Str("reason"),
            Title = $"{kind}: {s.Display} → {t.Display}",
        };
        ctx.Changes.Add(p);
        return Proposed(p);
    }
}

/// <summary><c>delete_element</c>.</summary>
public sealed class DeleteElementTool : WriteTool
{
    public override string Name => "delete_element";
    public override string Description =>
        "Propose deleting an existing element together with everything it owns, every relationship touching them and their diagram shapes. " +
        "Only when the user asks for removal or a review clearly calls for it. " + ProposalNote;

    public override JsonObject Schema { get; } = Object(
        Req("element_id", String("Id of the element to delete.")),
        Opt("reason", String("Why it should go.")));

    public override string Describe(JsonElement input) => $"Proposing deletion of {input.Str("element_id")}";

    protected override ToolResult Execute(ToolContext ctx, JsonElement input)
    {
        var t = ctx.Resolve(input.Str("element_id"), "element_id");
        if (t.Existing is not { } e) throw new ToolInputException("Elements proposed in this turn cannot be deleted; simply leave them out.");
        if (e.Kind == ElementKind.Model) throw new ToolInputException("The model root cannot be deleted.");
        if (ctx.Changes.Items.Any(p => p.Kind == ProposalKind.DeleteElement && p.SubjectRef == e.Id)) throw new ToolInputException("Already proposed for deletion.");
        var subtree = e.Descendants().Prepend(e).ToHashSet();
        var relationships = ctx.Model.Relationships.Count(r => !subtree.Contains(r) && (subtree.Contains(r.Source) || subtree.Contains(r.Target)));
        var owned = subtree.Count - 1;
        var p = new Proposal
        {
            Number = ctx.Changes.Items.Count + 1,
            Kind = ProposalKind.DeleteElement,
            SubjectRef = e.Id,
            Reason = input.Str("reason"),
            Title = $"Delete {ToolContext.Describe(e)}" + (owned + relationships > 0 ? $" (and {owned} owned element{(owned == 1 ? "" : "s")}, {relationships} relationship{(relationships == 1 ? "" : "s")})" : ""),
        };
        ctx.Changes.Add(p);
        return Proposed(p);
    }
}

/// <summary><c>create_diagram_with_elements</c>.</summary>
public sealed class CreateDiagramTool : WriteTool
{
    public override string Name => "create_diagram_with_elements";
    public override string Description =>
        "Propose a new diagram showing the given elements (existing ids or new:N), automatically laid out; relationships between shown elements " +
        "are drawn too. Owners: BlockDefinition/Requirement/Package/UseCase/Class diagrams → a package; InternalBlock/Parametric → a block; " +
        "Activity → an Activity element; StateMachine → a StateMachine; Sequence → an Interaction. Create the elements first. " + ProposalNote;

    public override JsonObject Schema { get; } = Object(
        Req("diagram_type", String("Diagram type.", DiagramTypeNames)),
        Req("owner_id", String("Owning element id or new:N.")),
        Req("name", String("Diagram name.")),
        Req("element_ids", Array(String("Element id or new:N."), "Elements to show (not relationships).")));

    public override string Describe(JsonElement input) => $"Proposing {input.Str("diagram_type")} diagram \"{input.Str("name")}\"";

    protected override ToolResult Execute(ToolContext ctx, JsonElement input)
    {
        if (!Enum.TryParse<DiagramType>(input.Str("diagram_type"), true, out var type)) throw new ToolInputException("Unknown diagram_type.");
        var owner = ctx.Resolve(input.Str("owner_id"), "owner_id");
        if (!Metamodel.IsValidDiagramContext(type, owner.Kind))
            throw new ToolInputException($"A {Metamodel.DisplayName(type)} cannot be owned by a {Metamodel.DisplayName(owner.Kind)}.");
        var refs = new List<string>();
        foreach (var id in input.Arr("element_ids").Select(x => x.GetString()!))
        {
            var t = ctx.Resolve(id, "element_ids");
            if (t.Kind == ElementKind.Diagram || Metamodel.IsRelationship(t.Kind)) throw new ToolInputException($"{t.Display} cannot be shown as a shape; relationships are drawn automatically.");
            var r = t.Existing?.Id ?? t.Pending!.Ref!;
            if (!refs.Contains(r)) refs.Add(r);
        }
        if (refs.Count == 0) throw new ToolInputException("element_ids must name at least one element.");
        var name = input.Str("name")!.Trim();
        var p = new Proposal
        {
            Number = ctx.Changes.Items.Count + 1,
            Kind = ProposalKind.CreateDiagram,
            Ref = ctx.NewRef(),
            DiagramType = type,
            Name = name.Length > 0 ? name : null,
            OwnerRef = owner.Existing?.Id ?? owner.Pending!.Ref,
            ElementRefs = refs,
            Title = $"New {Metamodel.DisplayName(type)} \"{name}\" with {refs.Count} element{(refs.Count == 1 ? "" : "s")} (auto-laid out)",
        };
        ctx.Changes.Add(p);
        return Proposed(p);
    }
}

/// <summary><c>apply_sysml_text</c>: an edited package text, synchronised through <see cref="SysMLv2Sync"/>.</summary>
public sealed class ApplySysmlTextTool : WriteTool
{
    public override string Name => "apply_sysml_text";
    public override string Description =>
        "Propose replacing a package's content with edited SysML v2 text (get it first with get_sysml_text and keep the /* @id */ comments). " +
        "Only the differences are applied: creates, renames, field changes, moves, deletions and links. Anything missing from the text " +
        "that the notation can express is deleted, so always send the whole package. Syntax errors are returned so you can fix them. " + ProposalNote;

    public override JsonObject Schema { get; } = Object(
        Req("package_id", String("Id of the package (or model root) the text describes.")),
        Req("text", String("The complete SysML v2 text of that package.")));

    public override string Describe(JsonElement input) => $"Proposing a SysML v2 text edit of {input.Str("package_id")}";

    protected override ToolResult Execute(ToolContext ctx, JsonElement input)
    {
        var scope = ctx.Resolve(input.Str("package_id"), "package_id").Existing ?? throw new ToolInputException("package_id must be an existing package.");
        if (scope.Kind is not (ElementKind.Package or ElementKind.Model)) throw new ToolInputException("package_id must be a package or the model root.");
        var text = input.Str("text")!;
        var doc = SysMLv2Parser.Parse(text);
        if (doc.HasErrors)
            return ToolResult.Error("The SysML v2 text has errors:\n" + string.Join("\n", doc.Errors.Take(10).Select(d => $"line {d.Span.Line}:{d.Span.Column}: {d.Message}")));

        // Dry run on a copy of the model: the user sees exactly what would change.
        var copy = ProjectFile.Deserialize(ProjectFile.Serialize(ctx.Model));
        var copyScope = copy.Find(scope.Id)!;
        var result = SysMLv2Sync.Apply(copy, copyScope, doc, new SysmlSyncOptions());
        if (result.Changes.Count == 0) throw new ToolInputException("The text matches the model already; nothing would change.");
        var p = new Proposal
        {
            Number = ctx.Changes.Items.Count + 1,
            Kind = ProposalKind.ApplySysml,
            OwnerRef = scope.Id,
            SysmlText = text,
            Preview = result.Changes.Select(c => $"{c.Kind}: {c.Description}").ToList(),
            Title = $"SysML v2 text edit of {ToolContext.Describe(scope)}: {result.Changes.Count} change{(result.Changes.Count == 1 ? "" : "s")}",
        };
        ctx.Changes.Add(p);
        return Proposed(p, new { changes = p.Preview.Take(50) });
    }
}
