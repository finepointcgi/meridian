using System.Globalization;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

/// <summary>Field keys used by saved views (<see cref="ElementKind.TableView"/>, <see cref="ElementKind.QueryView"/>) and custom rules.</summary>
public static class ViewFields
{
    public const string Query = "query";            // query text (see ModelQuery)
    public const string Columns = "tableColumns";   // table views: JSON column / sort definition
    public const string Severity = "severity";      // validation rules: error / warning / info
    public const string Message = "message";        // validation rules: message template with {placeholders}
}

/// <summary>
/// Resolves "value keys" against elements. One key vocabulary is shared by query conditions, table columns and
/// rule message placeholders:
/// <list type="bullet">
/// <item><c>name</c>, <c>kind</c>, <c>owner</c>, <c>qualifiedName</c>, <c>stereotype</c>, <c>type</c>, <c>documentation</c>, <c>id</c></item>
/// <item><c>field:key</c> — a raw element field (e.g. <c>field:reqId</c>)</item>
/// <item><c>tag:name</c> — a tagged value / stereotype property</item>
/// <item><c>prop:name</c> — the default value of an owned value property (e.g. a block's <c>mass</c>)</item>
/// <item><c>rel:out:Satisfy</c>, <c>rel:in:Verify</c>, <c>rel:any:Trace</c> — names of related elements; friendly aliases such as
/// <c>satisfies</c>, <c>satisfiedBy</c>, <c>allocatedTo</c> map onto these</item>
/// <item>a bare word resolves to a special key, then a raw field, a tagged value, and finally an owned value property</item>
/// </list>
/// </summary>
public static class ModelValues
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["satisfies"] = "rel:out:Satisfy", ["satisfiedBy"] = "rel:in:Satisfy",
        ["verifies"] = "rel:out:Verify", ["verifiedBy"] = "rel:in:Verify",
        ["allocatedTo"] = "rel:out:Allocate", ["allocatedFrom"] = "rel:in:Allocate",
        ["refines"] = "rel:out:Refine", ["refinedBy"] = "rel:in:Refine",
        ["derivedFrom"] = "rel:out:DeriveReqt", ["derived"] = "rel:in:DeriveReqt",
        ["tracesTo"] = "rel:out:Trace", ["tracedFrom"] = "rel:in:Trace",
        ["dependsOn"] = "rel:out:Dependency", ["dependents"] = "rel:in:Dependency",
        ["supertypes"] = "rel:out:Generalization", ["subtypes"] = "rel:in:Generalization",
        ["doc"] = "documentation", ["path"] = "qualifiedName", ["stereotypes"] = "stereotype",
        ["reqId"] = "field:reqId", ["text"] = "field:text",
    };

    private static readonly HashSet<string> Specials = new(StringComparer.OrdinalIgnoreCase)
        { "name", "kind", "owner", "qualifiedName", "stereotype", "type", "documentation", "id" };

    /// <summary>Normalizes aliases ("satisfies" → "rel:out:Satisfy", "doc" → "documentation").</summary>
    public static string Canonical(string key)
    {
        key = key.Trim();
        if (Aliases.TryGetValue(key, out var a)) return a;
        foreach (var s in Specials) if (string.Equals(s, key, StringComparison.OrdinalIgnoreCase)) return s;
        if (TryRel(key, out var kind, out var dir)) return $"rel:{dir.ToString().ToLowerInvariant()}:{kind}";
        return key;
    }

    /// <summary>All values of a key on an element (several for stereotypes and relationship-derived keys).</summary>
    public static IReadOnlyList<string> GetAll(ModelRepository m, Element e, string key)
    {
        key = Canonical(key);
        if (TryRel(key, out var rk, out var dir))
            return Related(m, e, rk, dir).Select(ElementLabel).ToList();
        if (key == "stereotype") return e.Stereotypes;
        if (key == "kind") return [e.Kind.ToString(), Metamodel.DisplayName(e.Kind)];
        var v = Get(m, e, key);
        return v.Length == 0 ? [] : [v];
    }

    /// <summary>The display value of a key on an element ("" when absent).</summary>
    public static string Get(ModelRepository m, Element e, string key)
    {
        key = Canonical(key);
        switch (key)
        {
            case "name": return e.Name;
            case "kind": return Metamodel.DisplayName(e.Kind);
            case "owner": return e.Owner?.DisplayName ?? "";
            case "qualifiedName": return e.QualifiedName;
            case "stereotype": return string.Join(", ", e.Stereotypes);
            case "type": return Metamodel.IsTypedFeature(e.Kind) ? m.TypeDisplayName(e) : e.TypeRef;
            case "documentation": return e.Documentation;
            case "id": return e.Id;
        }
        if (TryRel(key, out var rk, out var dir)) return string.Join(", ", Related(m, e, rk, dir).Select(ElementLabel));
        if (key.StartsWith("field:", StringComparison.OrdinalIgnoreCase)) return e.Get(key[6..]);
        if (key.StartsWith(FieldKeys.TagPrefix, StringComparison.OrdinalIgnoreCase)) return TagValue(e, key[4..]);
        if (key.StartsWith("prop:", StringComparison.OrdinalIgnoreCase)) return ValueProperty(e, key[5..])?.Get(FieldKeys.DefaultValue) ?? "";
        if (e.Fields.TryGetValue(key, out var raw)) return raw;
        var tag = TagValue(e, key);
        if (tag.Length > 0) return tag;
        return ValueProperty(e, key)?.Get(FieldKeys.DefaultValue) ?? "";
    }

    /// <summary>Can a cell for this key be edited in place?</summary>
    public static bool CanEdit(Element e, string key)
    {
        key = Canonical(key);
        if (key is "kind" or "owner" or "qualifiedName" or "id" or "type") return false;
        if (TryRel(key, out _, out _)) return false;
        if (key.StartsWith("prop:", StringComparison.OrdinalIgnoreCase)) return ValueProperty(e, key[5..]) != null;
        if (key is "name" or "stereotype" or "documentation") return true;
        if (key.StartsWith("field:", StringComparison.OrdinalIgnoreCase) || key.StartsWith(FieldKeys.TagPrefix, StringComparison.OrdinalIgnoreCase)) return true;
        // Bare key: editable when it resolves to a field, tag or value property (or is a field this kind supports).
        return e.Fields.ContainsKey(key) || e.Fields.ContainsKey(FieldKeys.TagPrefix + key) || ValueProperty(e, key) != null
               || Metamodel.EditableFields(e.Kind).Contains(key);
    }

    /// <summary>Writes a value through the repository, so the edit is undoable and replicates.</summary>
    public static void Set(ModelRepository m, Element e, string key, string? value)
    {
        key = Canonical(key);
        value = value?.Trim();
        if (!CanEdit(e, key)) throw new InvalidOperationException($"'{Label(key)}' is not editable");
        string target;
        Element owner = e;
        if (key == "name") target = FieldKeys.Name;
        else if (key == "documentation") target = FieldKeys.Documentation;
        else if (key == "stereotype")
        {
            target = FieldKeys.Stereotypes;
            value = string.Join(",", (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        else if (key.StartsWith("field:", StringComparison.OrdinalIgnoreCase)) target = key[6..];
        else if (key.StartsWith(FieldKeys.TagPrefix, StringComparison.OrdinalIgnoreCase)) target = TagKey(e, key[4..]);
        else if (key.StartsWith("prop:", StringComparison.OrdinalIgnoreCase)) { owner = ValueProperty(e, key[5..])!; target = FieldKeys.DefaultValue; }
        else if (e.Fields.ContainsKey(key) || Metamodel.EditableFields(e.Kind).Contains(key)) target = key;
        else if (e.Fields.ContainsKey(FieldKeys.TagPrefix + key)) target = FieldKeys.TagPrefix + key;
        else { owner = ValueProperty(e, key)!; target = FieldKeys.DefaultValue; }
        if (owner.Get(target) == (value ?? "")) return;
        m.Set(owner, target, string.IsNullOrEmpty(value) ? null : value);
    }

    /// <summary>A human column header for a key.</summary>
    public static string Label(string key)
    {
        var c = Canonical(key);
        foreach (var (alias, target) in Aliases)
            if (target == c && alias.Length > 4 && char.IsLower(alias[0]) && alias is not ("reqId" or "text" or "path" or "doc" or "stereotypes"))
                return Humanize(alias);
        if (TryRel(c, out var k, out var dir))
            return dir switch { RelDirection.Out => $"{Metamodel.DisplayName(k)} →", RelDirection.In => $"← {Metamodel.DisplayName(k)}", _ => Metamodel.DisplayName(k) };
        return c switch
        {
            "qualifiedName" => "Qualified name",
            "stereotype" => "Stereotypes",
            "field:reqId" => "Id",
            "field:text" => "Text",
            _ when c.StartsWith("field:", StringComparison.OrdinalIgnoreCase) => Humanize(c[6..]),
            _ when c.StartsWith(FieldKeys.TagPrefix, StringComparison.OrdinalIgnoreCase) => c[4..],
            _ when c.StartsWith("prop:", StringComparison.OrdinalIgnoreCase) => c[5..],
            _ => Humanize(c),
        };
    }

    /// <summary>The relationship-derived keys offered as table columns.</summary>
    public static readonly IReadOnlyList<string> RelationshipColumns =
        ["satisfies", "satisfiedBy", "verifies", "verifiedBy", "allocatedTo", "allocatedFrom", "refines", "refinedBy", "derivedFrom", "derived", "tracesTo", "dependsOn", "supertypes"];

    public static bool TryRel(string key, out ElementKind kind, out RelDirection dir)
    {
        kind = default;
        dir = RelDirection.Any;
        if (!key.StartsWith("rel:", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = key.Split(':');
        if (parts.Length == 2) return ModelQuery.TryParseKind(parts[1], out kind) && Metamodel.IsRelationship(kind);
        if (parts.Length != 3 || !TryDirection(parts[1], out dir)) return false;
        return ModelQuery.TryParseKind(parts[2], out kind) && Metamodel.IsRelationship(kind);
    }

    internal static bool TryDirection(string s, out RelDirection dir)
    {
        dir = s.ToLowerInvariant() switch
        {
            "out" or "outgoing" or "to" => RelDirection.Out,
            "in" or "incoming" or "from" => RelDirection.In,
            "any" or "both" => RelDirection.Any,
            _ => (RelDirection)(-1),
        };
        return (int)dir >= 0;
    }

    public static IEnumerable<Element> Related(ModelRepository m, Element e, ElementKind kind, RelDirection dir) =>
        m.RelationshipsOf(e).Where(r => r.Kind == kind && (dir == RelDirection.Any || (dir == RelDirection.Out ? ReferenceEquals(r.Source, e) : ReferenceEquals(r.Target, e))))
            .Select(r => ReferenceEquals(r.Source, e) ? r.Target : r.Source);

    public static bool TryNumber(string s, out double d) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d);

    private static string ElementLabel(Element e) => e.Kind == ElementKind.Requirement && e.Get(FieldKeys.RequirementId) is { Length: > 0 } id ? $"{id} {e.Name}" : e.DisplayName;

    private static string TagValue(Element e, string name)
    {
        var v = e.Get(FieldKeys.TagPrefix + name);
        if (v.Length > 0) return v;
        foreach (var t in e.TaggedValues)
            if (string.Equals(t.Key, name, StringComparison.OrdinalIgnoreCase)) return t.Value;
        return "";
    }

    private static string TagKey(Element e, string name) =>
        e.TaggedValues.Where(t => string.Equals(t.Key, name, StringComparison.OrdinalIgnoreCase)).Select(t => FieldKeys.TagPrefix + t.Key).FirstOrDefault()
        ?? FieldKeys.TagPrefix + name;

    private static Element? ValueProperty(Element e, string name) =>
        e.OwnedElements.FirstOrDefault(c => c.Kind == ElementKind.Property && string.Equals(c.Name, name, StringComparison.Ordinal))
        ?? e.OwnedElements.FirstOrDefault(c => c.Kind == ElementKind.Property && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string Humanize(string s)
    {
        if (s.Length == 0) return s;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpperInvariant(s[i]) : char.ToLowerInvariant(s[i]));
        }
        return sb.ToString();
    }
}
