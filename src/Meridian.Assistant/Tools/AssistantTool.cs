using System.Text.Json;
using System.Text.Json.Nodes;
using Meridian.Assistant.Changes;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Assistant.Tools;

/// <summary>What a tool returns to Claude: text (usually JSON) and whether it is an error result.</summary>
public sealed record ToolResult(string Content, bool IsError)
{
    public static ToolResult Ok(object value) => new(JsonSerializer.Serialize(value, ToolContext.Json), false);
    public static ToolResult Error(string message) => new(message, true);
}

/// <summary>A tool input that is well-formed but makes no sense for this model (unknown id, illegal owner…).</summary>
public sealed class ToolInputException(string message) : Exception(message);

/// <summary>A finding reported by the review quick action (<c>report_findings</c>), shown as a checklist.</summary>
public sealed record ReviewFinding(string Severity, string Category, string Message, string? ElementId, string? ElementName)
{
    public bool Done { get; set; }
}

/// <summary>Everything a tool can see while it runs: the model, the turn's proposed changes and reported findings.</summary>
public sealed class ToolContext(ModelRepository model, ChangeSet changes, Func<int> nextRef)
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public ModelRepository Model { get; } = model;
    public ChangeSet Changes { get; } = changes;
    public List<ReviewFinding> Findings { get; } = new();
    public string NewRef() => "new:" + nextRef();

    /// <summary>An element, either existing (by id) or proposed earlier in this turn (<c>new:N</c>).</summary>
    public sealed record Target(Element? Existing, Proposal? Pending, ElementKind Kind, string Display);

    public Target Resolve(string? reference, string what)
    {
        if (string.IsNullOrWhiteSpace(reference)) throw new ToolInputException($"{what} is required.");
        reference = reference.Trim();
        if (ChangeSet.IsNewRef(reference))
        {
            var p = Changes.Creating(reference) ?? throw new ToolInputException($"{what} '{reference}' is not an element proposed in this turn.");
            if (p.Kind == ProposalKind.CreateDiagram) return new Target(null, p, ElementKind.Diagram, p.Name ?? reference);
            return new Target(null, p, p.ElementKind!.Value, $"{p.Name ?? Metamodel.DisplayName(p.ElementKind!.Value)} ({reference})");
        }
        var e = Model.Find(reference) ?? throw new ToolInputException(
            $"{what} '{reference}' does not exist. Use search_elements to find element ids (they look like _3f2a…).");
        return new Target(e, null, e.Kind, Describe(e));
    }

    public static string Describe(Element e) =>
        e is Relationship r ? $"{r.Kind} {r.Source.DisplayName} → {r.Target.DisplayName}" :
        e.Kind == ElementKind.Requirement && e.Get(FieldKeys.RequirementId) is { Length: > 0 } id ? $"{id} {e.DisplayName}" :
        $"{Metamodel.DisplayName(e.Kind)} {e.DisplayName}";

    /// <summary>A compact element summary for tool results.</summary>
    public static Dictionary<string, object?> Brief(Element e)
    {
        var d = new Dictionary<string, object?> { ["id"] = e.Id, ["kind"] = e.Kind.ToString(), ["name"] = e.Name };
        if (e is Relationship r) { d["source"] = r.Source.Id; d["target"] = r.Target.Id; }
        else d["qualifiedName"] = e.QualifiedName;
        if (e.Kind == ElementKind.Requirement)
        {
            d["reqId"] = e.Get(FieldKeys.RequirementId);
            d["text"] = Truncate(e.Get(FieldKeys.Text), 240);
        }
        return d;
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// A client-side tool Claude can call. Inputs are validated against <see cref="Schema"/> before <see cref="Execute"/>
/// runs; invalid input, unknown ids and illegal edits come back as <c>is_error</c> tool results so Claude can correct them.
/// Write tools (<see cref="IsWrite"/>) never edit the model: they add a <see cref="Proposal"/> to the turn's change set.
/// </summary>
public abstract class AssistantTool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract JsonObject Schema { get; }
    public virtual bool IsWrite => false;

    protected abstract ToolResult Execute(ToolContext ctx, JsonElement input);

    public ToolResult Run(ToolContext ctx, JsonElement input)
    {
        var errors = ToolSchema.Validate(Schema, input);
        if (errors.Count > 0) return ToolResult.Error("Invalid input: " + string.Join("; ", errors.Take(8)));
        try { return Execute(ctx, input); }
        catch (ToolInputException e) { return ToolResult.Error(e.Message); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException)
        {
            return ToolResult.Error($"{Name} failed: {e.Message}");
        }
    }

    /// <summary>A short, human-readable line for the activity log in the chat ("Searching kind:Requirement…").</summary>
    public virtual string Describe(JsonElement input) => Name;
}
