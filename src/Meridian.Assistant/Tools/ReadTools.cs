using System.Text.Json;
using System.Text.Json.Nodes;
using Meridian.Assistant.Changes;
using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Validation;
using Meridian.Serialization;
using static Meridian.Assistant.Tools.ToolSchema;

namespace Meridian.Assistant.Tools;

/// <summary><c>search_elements</c>: runs a Meridian model query (the same syntax as the Query panel).</summary>
public sealed class SearchElementsTool : AssistantTool
{
    public override string Name => "search_elements";
    public override string Description =>
        "Find model elements. Call this whenever you need element ids or want to know what exists before answering or proposing changes. " +
        "`query` uses Meridian's model query syntax: `kind:Block,Requirement stereotype:<name> in:<package qualified name or id> where <condition>`; " +
        "conditions: `field = value` (also !=, <, >, <=, >=, contains, is empty, is not empty), `has:satisfy`, `has:in:verify`, `has:out:allocate`, " +
        "`no:verify`, `not`, `and`, `or`, parentheses. Fields: name, text, reqId, documentation, type, multiplicity, stereotype, tag:<name>, owner. " +
        "Examples: `kind:Requirement where no:satisfy`, `kind:Block where name contains Engine`, `kind:Requirement in:Requirements where text contains shall`. " +
        "An empty query matches every ordinary element. `text` additionally filters by a case-insensitive substring of name, requirement id or text.";

    public override JsonObject Schema { get; } = Object(
        Req("query", String("Model query, e.g. \"kind:Requirement where no:verify\". Use \"\" for everything.")),
        Opt("text", String("Optional free-text filter (name, requirement id or text contains).")),
        Opt("limit", Integer("Maximum results (default 50, at most 200).")));

    public override string Describe(JsonElement input) => $"Searching {(input.Str("query") is { Length: > 0 } q ? q : "all elements")}" +
                                                          (input.Str("text") is { Length: > 0 } t ? $" for \"{t}\"" : "");

    protected override ToolResult Execute(ToolContext ctx, JsonElement input)
    {
        if (!ModelQuery.TryParse(input.Str("query"), out var query, out var error))
            throw new ToolInputException($"The query does not parse: {error}");
        var limit = Math.Clamp(input.Int("limit") ?? 50, 1, 200);
        IEnumerable<Element> hits = query.Run(ctx.Model);
        if (input.Str("text") is { Length: > 0 } text)
        {
            var matches = ctx.Model.Search(text).ToHashSet();
            hits = hits.Where(matches.Contains);
        }
        var list = hits.ToList();
        return ToolResult.Ok(new
        {
            total = list.Count,
            returned = Math.Min(limit, list.Count),
            results = list.Take(limit).Select(ToolContext.Brief),
        });
    }
}

/// <summary><c>get_element</c>: every field, the owner, owned elements, relationships and diagrams of one element.</summary>
public sealed class GetElementTool : AssistantTool
{
    public override string Name => "get_element";
    public override string Description =>
        "Get one element's details: all fields (name, documentation, requirement text, rationale, verification method, type, multiplicity, " +
        "stereotypes, tagged values…), its owner, owned elements, incoming/outgoing relationships and the diagrams showing it. " +
        "Call this before updating, linking or deleting an element you have not inspected yet. Accepts an element id or a new:N reference.";

    public override JsonObject Schema { get; } = Object(Req("id", String("Element id (e.g. _3f2a…) or new:N for an element proposed in this turn.")));

    public override string Describe(JsonElement input) => $"Reading {input.Str("id")}";

    protected override ToolResult Execute(ToolContext ctx, JsonElement input)
    {
        var t = ctx.Resolve(input.Str("id"), "id");
        if (t.Pending is { } p)
            return ToolResult.Ok(new { reference = p.Ref, proposed = true, kind = t.Kind.ToString(), name = p.Name, owner = p.OwnerRef, fields = p.Fields.ToDictionary(f => f.Key, f => f.Value), note = "Proposed in this turn; not applied yet." });
        var e = t.Existing!;
        var m = ctx.Model;
        var result = ToolContext.Brief(e);
        result["fields"] = e.Fields.Where(f => !f.Key.StartsWith("suspect", StringComparison.Ordinal))
                                   .ToDictionary(f => f.Key, f => ToolContext.Truncate(f.Value, 2000));
        if (e.Owner is { } owner) result["owner"] = new { id = owner.Id, kind = owner.Kind.ToString(), name = owner.Name };
        if (Metamodel.IsTypedFeature(e.Kind) && m.ResolveType(e) is { } type) result["typeElement"] = new { id = type.Id, name = type.Name };
        var children = e.OwnedElements.Where(c => c is not Relationship).ToList();
        result["ownedElements"] = children.Take(80).Select(c => new { id = c.Id, kind = c.Kind.ToString(), name = c.Name });
        if (children.Count > 80) result["ownedElementsTruncated"] = children.Count;
        if (e is not Relationship)
        {
            result["outgoing"] = m.Outgoing(e).Take(80).Select(r => new { id = r.Id, kind = r.Kind.ToString(), target = new { id = r.Target.Id, kind = r.Target.Kind.ToString(), name = r.Target.Name } });
            result["incoming"] = m.Incoming(e).Take(80).Select(r => new { id = r.Id, kind = r.Kind.ToString(), source = new { id = r.Source.Id, kind = r.Source.Kind.ToString(), name = r.Source.Name } });
        }
        result["shownOnDiagrams"] = m.Diagrams.Where(d => d.NodeFor(e) != null).Take(20).Select(d => new { id = d.Id, name = d.Name });
        return ToolResult.Ok(result);
    }
}

/// <summary><c>list_diagrams</c>.</summary>
public sealed class ListDiagramsTool : AssistantTool
{
    public override string Name => "list_diagrams";
    public override string Description => "List the model's diagrams (id, name, type, owner, number of shapes), optionally only those inside one package or element.";

    public override JsonObject Schema { get; } = Object(Opt("owner_id", String("Only diagrams owned (directly or indirectly) by this element.")));

    public override string Describe(JsonElement input) => "Listing diagrams";

    protected override ToolResult Execute(ToolContext ctx, JsonElement input)
    {
        IEnumerable<Diagram> diagrams = ctx.Model.Diagrams;
        if (input.Str("owner_id") is { Length: > 0 } ownerId)
        {
            var owner = ctx.Resolve(ownerId, "owner_id").Existing ?? throw new ToolInputException("owner_id must be an existing element.");
            diagrams = diagrams.Where(d => owner.IsAncestorOf(d));
        }
        return ToolResult.Ok(diagrams.OrderBy(d => d.QualifiedName, StringComparer.OrdinalIgnoreCase).Take(200).Select(d => new
        {
            id = d.Id, name = d.Name, type = d.DiagramType.ToString(), owner = d.Owner?.QualifiedName, shapes = d.Nodes.Count(),
        }));
    }
}

/// <summary><c>get_traceability</c>: what satisfies, verifies, refines and derives a requirement.</summary>
public sealed class GetTraceabilityTool : AssistantTool
{
    public override string Name => "get_traceability";
    public override string Description =>
        "Traceability of one requirement: elements that satisfy, verify, refine or trace to it, requirements derived from it or it derives from, " +
        "sub-requirements, suspect links (an end changed since the link was made) and whether it is covered. Call it before suggesting trace links.";

    public override JsonObject Schema { get; } = Object(Req("requirement_id", String("Id of a Requirement element.")));

    public override string Describe(JsonElement input) => $"Tracing {input.Str("requirement_id")}";

    protected override ToolResult Execute(ToolContext ctx, JsonElement input)
    {
        var r = ctx.Resolve(input.Str("requirement_id"), "requirement_id").Existing
                ?? throw new ToolInputException("requirement_id must be an existing requirement.");
        if (r.Kind != ElementKind.Requirement) throw new ToolInputException($"'{r.DisplayName}' is a {Metamodel.DisplayName(r.Kind)}, not a requirement.");
        var m = ctx.Model;
        object Ends(IEnumerable<Relationship> rels, bool incoming) =>
            rels.Select(x => new { relationshipId = x.Id, element = ToolContext.Brief(incoming ? x.Source : x.Target), suspect = SuspectLinks.IsSuspect(x) }).ToList();
        var subs = r.OwnedElements.Where(c => c.Kind == ElementKind.Requirement).ToList();
        return ToolResult.Ok(new
        {
            requirement = ToolContext.Brief(r),
            satisfiedBy = Ends(m.Incoming(r, ElementKind.Satisfy), true),
            verifiedBy = Ends(m.Incoming(r, ElementKind.Verify), true),
            refinedBy = Ends(m.Incoming(r, ElementKind.Refine), true),
            tracedFrom = Ends(m.Incoming(r, ElementKind.Trace), true),
            derivedRequirements = Ends(m.Incoming(r, ElementKind.DeriveReqt), true),
            derivesFrom = Ends(m.Outgoing(r, ElementKind.DeriveReqt), false),
            subRequirements = subs.Select(ToolContext.Brief),
            satisfied = subs.Count > 0 || m.Incoming(r, ElementKind.Satisfy).Any(),
            verified = subs.Count > 0 || m.Incoming(r, ElementKind.Verify).Any(),
            verificationMethod = r.Get(ReqFields.VerificationMethod),
        });
    }
}

/// <summary><c>validate_model</c>: Meridian's validation suite.</summary>
public sealed class ValidateModelTool : AssistantTool
{
    public override string Name => "validate_model";
    public override string Description =>
        "Run Meridian's validation rules (naming, duplicate names, relationship ends, requirement ids/text, satisfaction and verification, " +
        "multiplicities, custom rules) and return the issues, optionally only for elements inside one package.";

    public override JsonObject Schema { get; } = Object(
        Opt("scope_id", String("Only report issues on elements inside this package or element.")),
        Opt("limit", Integer("Maximum issues to return (default 100, at most 300).")));

    public override string Describe(JsonElement input) => "Validating the model";

    protected override ToolResult Execute(ToolContext ctx, JsonElement input)
    {
        IEnumerable<ValidationIssue> issues = new ModelValidator().Validate(ctx.Model);
        if (input.Str("scope_id") is { Length: > 0 } scopeId)
        {
            var scope = ctx.Resolve(scopeId, "scope_id").Existing ?? throw new ToolInputException("scope_id must be an existing element.");
            issues = issues.Where(i => scope.IsAncestorOf(i.Element) || ReferenceEquals(scope, i.Element));
        }
        var list = issues.ToList();
        var limit = Math.Clamp(input.Int("limit") ?? 100, 1, 300);
        return ToolResult.Ok(new
        {
            errors = list.Count(i => i.Severity == Severity.Error),
            warnings = list.Count(i => i.Severity == Severity.Warning),
            infos = list.Count(i => i.Severity == Severity.Info),
            issues = list.OrderByDescending(i => i.Severity).Take(limit).Select(i => new
            {
                severity = i.Severity.ToString().ToLowerInvariant(), rule = i.RuleId, message = i.Message,
                element = new { id = i.Element.Id, kind = i.Element.Kind.ToString(), name = i.Element.Name },
            }),
        });
    }
}

/// <summary><c>get_sysml_text</c>: the SysML v2 textual notation of one package (with ids, so edits keep identity).</summary>
public sealed class GetSysmlTextTool : AssistantTool
{
    internal const int MaxChars = 60_000;

    public override string Name => "get_sysml_text";
    public override string Description =>
        "Export a package (or the whole model when package_id is the model root) as SysML v2 textual notation. " +
        "Declarations carry /* @id … */ comments: keep them when you edit the text for apply_sysml_text so elements keep their identity.";

    public override JsonObject Schema { get; } = Object(Req("package_id", String("Id of a Package (or the model root).")));

    public override string Describe(JsonElement input) => $"Reading SysML v2 text of {input.Str("package_id")}";

    protected override ToolResult Execute(ToolContext ctx, JsonElement input)
    {
        var scope = ctx.Resolve(input.Str("package_id"), "package_id").Existing ?? throw new ToolInputException("package_id must be an existing package.");
        if (scope.Kind is not (ElementKind.Package or ElementKind.Model))
            throw new ToolInputException($"'{scope.DisplayName}' is a {Metamodel.DisplayName(scope.Kind)}; pass a package id.");
        var text = SysMLv2Text.Export(ctx.Model, new SysMLv2ExportOptions { Scope = scope, EmitIds = true });
        return new ToolResult(text.Length > MaxChars ? text[..MaxChars] + "\n// … truncated: pick a smaller package" : text, false);
    }
}

/// <summary><c>report_findings</c>: the review quick action's structured checklist (read-only; nothing in the model changes).</summary>
public sealed class ReportFindingsTool : AssistantTool
{
    public static readonly string[] Categories =
        ["unsatisfied_requirement", "unverified_requirement", "orphan_block", "vague_wording", "missing_units", "missing_rationale", "inconsistency", "other"];

    public override string Name => "report_findings";
    public override string Description =>
        "Report model-review findings as a checklist the user can work through (each item links to its element). " +
        "Call it once at the end of a model review with every finding; it does not change the model.";

    public override JsonObject Schema { get; } = Object(
        Req("findings", Array(Object(
            Req("severity", String("How serious the gap is.", ["error", "warning", "info"])),
            Req("category", String("Kind of gap.", Categories)),
            Req("message", String("One sentence: what is wrong and how to fix it.")),
            Opt("element_id", String("Id of the element concerned, if any."))), "The findings, most important first.")));

    public override string Describe(JsonElement input) => $"Reporting {input.Arr("findings").Count()} findings";

    protected override ToolResult Execute(ToolContext ctx, JsonElement input)
    {
        var unknown = 0;
        foreach (var f in input.Arr("findings"))
        {
            var id = f.Str("element_id");
            var e = string.IsNullOrWhiteSpace(id) ? null : ctx.Model.Find(id.Trim());
            if (!string.IsNullOrWhiteSpace(id) && e == null) unknown++;
            ctx.Findings.Add(new ReviewFinding(f.Str("severity")!, f.Str("category")!, f.Str("message")!, e?.Id, e?.DisplayName));
        }
        return ToolResult.Ok(new
        {
            recorded = ctx.Findings.Count,
            note = unknown > 0 ? $"{unknown} element id(s) were not found; those findings are listed without a link." : "Shown to the user as a checklist.",
        });
    }
}
