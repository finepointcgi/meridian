using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meridian.Assistant.Tools;

/// <summary>
/// Builds the JSON Schemas of the assistant's tools (the strict-tool subset: object/string/integer/number/boolean/array,
/// enum, required, <c>additionalProperties: false</c>) and validates tool inputs against them before anything runs.
/// </summary>
public static class ToolSchema
{
    public sealed record Prop(string Name, JsonObject Schema, bool Required);

    public static Prop Req(string name, JsonObject schema) => new(name, schema, true);
    public static Prop Opt(string name, JsonObject schema) => new(name, schema, false);

    public static JsonObject Object(params Prop[] props)
    {
        var properties = new JsonObject();
        foreach (var p in props) properties[p.Name] = p.Schema;
        var o = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        var required = props.Where(p => p.Required).Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray();
        if (required.Length > 0) o["required"] = new JsonArray(required);
        return o;
    }

    public static JsonObject String(string description, IEnumerable<string>? values = null)
    {
        var o = new JsonObject { ["type"] = "string", ["description"] = description };
        if (values != null) o["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
        return o;
    }

    public static JsonObject Integer(string description) => new() { ["type"] = "integer", ["description"] = description };
    public static JsonObject Number(string description) => new() { ["type"] = "number", ["description"] = description };
    public static JsonObject Boolean(string description) => new() { ["type"] = "boolean", ["description"] = description };

    public static JsonObject Array(JsonObject items, string description) =>
        new() { ["type"] = "array", ["description"] = description, ["items"] = items };

    /// <summary>A list of <c>{key, value}</c> pairs (strict schemas cannot express free-form maps).</summary>
    public static JsonObject FieldList(string description) =>
        Array(Object(Req("key", String("Field name, e.g. text, reqId, rationale, verifyMethod, documentation, type, multiplicity, tag:<name>")),
                     Req("value", String("New value; an empty string clears the field")))
            , description);

    // ------------------------------------------------------------------ validation

    /// <summary>Validates <paramref name="value"/> against <paramref name="schema"/>; returns every problem found (empty = valid).</summary>
    public static IReadOnlyList<string> Validate(JsonObject schema, JsonElement value)
    {
        var errors = new List<string>();
        Check(schema, value, "input", errors);
        return errors;
    }

    private static void Check(JsonObject schema, JsonElement v, string path, List<string> errors)
    {
        var type = schema["type"]?.GetValue<string>();
        switch (type)
        {
            case "object":
                if (v.ValueKind != JsonValueKind.Object) { errors.Add($"{path} must be an object"); return; }
                var props = schema["properties"] as JsonObject ?? new JsonObject();
                if (schema["required"] is JsonArray req)
                    foreach (var r in req.Select(x => x!.GetValue<string>()))
                        if (!v.TryGetProperty(r, out var rv) || rv.ValueKind == JsonValueKind.Null) errors.Add($"{path}.{r} is required");
                foreach (var p in v.EnumerateObject())
                {
                    if (props[p.Name] is JsonObject ps) Check(ps, p.Value, $"{path}.{p.Name}", errors);
                    else if (schema["additionalProperties"] is JsonValue ap && ap.GetValue<bool>() == false) errors.Add($"{path}.{p.Name} is not a known property");
                }
                break;
            case "array":
                if (v.ValueKind != JsonValueKind.Array) { errors.Add($"{path} must be an array"); return; }
                if (schema["items"] is JsonObject items)
                {
                    var i = 0;
                    foreach (var item in v.EnumerateArray()) Check(items, item, $"{path}[{i++}]", errors);
                }
                break;
            case "string":
                if (v.ValueKind != JsonValueKind.String) { errors.Add($"{path} must be a string"); return; }
                if (schema["enum"] is JsonArray values)
                {
                    var s = v.GetString();
                    if (!values.Any(x => x!.GetValue<string>() == s))
                        errors.Add($"{path} must be one of: {string.Join(", ", values.Select(x => x!.GetValue<string>()))}");
                }
                break;
            case "integer":
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out _)) errors.Add($"{path} must be an integer");
                break;
            case "number":
                if (v.ValueKind != JsonValueKind.Number) errors.Add($"{path} must be a number");
                break;
            case "boolean":
                if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) errors.Add($"{path} must be true or false");
                break;
        }
    }

    // ------------------------------------------------------------------ reading validated input

    public static string? Str(this JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int? Int(this JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    public static double? Num(this JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static bool? Bool(this JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static IEnumerable<JsonElement> Arr(this JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    public static List<KeyValuePair<string, string?>> Fields(this JsonElement o, string name) =>
        o.Arr(name).Select(f => new KeyValuePair<string, string?>(f.Str("key")!.Trim(), f.Str("value") is { Length: > 0 } s ? s : null)).ToList();
}
