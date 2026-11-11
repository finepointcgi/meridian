using System.Text.Json;
using System.Text.Json.Serialization;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization.Spreadsheets;

/// <summary>What a spreadsheet column means.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SheetRole
{
    Ignore,
    /// <summary>The matching key (see <see cref="SheetMapping.KeyMatch"/>).</summary>
    Key,
    Name,
    Documentation,
    /// <summary>The element kind of the row ("by column" mappings).</summary>
    Kind,
    /// <summary>Owner / package path: "a::b::c" or "a/b/c"; missing packages are created.</summary>
    Owner,
    /// <summary>Hierarchy: the parent row's key (or name / qualified name).</summary>
    Parent,
    /// <summary>Hierarchy: outline level 1, 2, 3 … (bills of materials).</summary>
    Level,
    /// <summary>Type, resolved by name or qualified name (primitives allowed); missing types can be created.</summary>
    Type,
    Multiplicity,
    /// <summary>A value with an optional unit ("1450 kg"): the row's own default value, or the value property <see cref="SheetColumn.Name"/>.</summary>
    Value,
    /// <summary>A unit symbol for the row's value (or for value property <see cref="SheetColumn.Name"/>).</summary>
    Unit,
    /// <summary>Stereotypes, separated by commas or semicolons.</summary>
    Stereotypes,
    /// <summary>Tagged value <see cref="SheetColumn.Name"/> ("tag:" field).</summary>
    Tag,
    /// <summary>Custom requirement attribute <see cref="SheetColumn.Name"/> ("attr:" field).</summary>
    Attribute,
    /// <summary>Any raw element field <see cref="SheetColumn.Name"/> (text, priority, direction, sqlType …).</summary>
    Field,
    /// <summary>Relationships of kind <see cref="SheetColumn.Relationship"/> to the listed targets ("REQ-001; REQ-002").</summary>
    Relationship,
    /// <summary>"Part of": the listed blocks get a part property typed by the row's element.</summary>
    PartOf,
}

/// <summary>How keys identify existing elements.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SheetKeyMatch
{
    /// <summary>Meridian's own element id (round trips of exported sheets).</summary>
    ElementId,
    /// <summary>An external id stored in <see cref="SheetMapping.KeyField"/> (requirement id, part number …); re-imports update rather than duplicate.</summary>
    ExternalId,
    /// <summary>Qualified name from the key column, or the owner path plus the name when no key column is mapped.</summary>
    QualifiedName,
    /// <summary>Name, unique among the elements of the target kind in scope.</summary>
    Name,
}

/// <summary>How relationship / parent / part-of targets in a cell are found.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SheetTargetMatch
{
    /// <summary>Element id, requirement id, the mapping's key field, qualified name, then a unique name.</summary>
    Auto,
    ElementId,
    RequirementId,
    /// <summary>The mapping's key (same rule as <see cref="SheetMapping.KeyMatch"/>).</summary>
    Key,
    Name,
    QualifiedName,
}

/// <summary>What the row hierarchy (indentation, level or parent column) means.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SheetHierarchy
{
    /// <summary>The parent row owns the child (requirements, packages, nested blocks, states).</summary>
    Containment,
    /// <summary>The child is a part of the parent: a part property typed by the child goes into the parent (bills of materials).</summary>
    PartOf,
}

/// <summary>One column of a mapping. Columns are matched to sheet headers by name, ignoring case and surrounding spaces.</summary>
public sealed class SheetColumn
{
    public SheetColumn() { }
    public SheetColumn(string column, SheetRole role, string? name = null) { Column = column; Role = role; Name = name; }

    [JsonPropertyName("column")] public string Column { get; set; } = "";
    [JsonPropertyName("role")] public SheetRole Role { get; set; }
    /// <summary>Tag / attribute / field / value-property name.</summary>
    [JsonPropertyName("name")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; set; }
    /// <summary>Relationship kind (an <see cref="ElementKind"/> name: Satisfy, Trace, Allocate, Verify, Refine, DeriveReqt, Dependency …).</summary>
    [JsonPropertyName("relationship")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Relationship { get; set; }
    /// <summary>Relationships: true when the row's element is the target ("Satisfied by"), false when it is the source ("Satisfies").</summary>
    [JsonPropertyName("incoming")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Incoming { get; set; }
    [JsonPropertyName("match")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public SheetTargetMatch Match { get; set; }

    public SheetColumn Clone() => new() { Column = Column, Role = Role, Name = Name, Relationship = Relationship, Incoming = Incoming, Match = Match };

    [JsonIgnore] public ElementKind? RelationshipKind =>
        Enum.TryParse<ElementKind>(Relationship, true, out var k) && Metamodel.IsRelationship(k) ? k : null;

    /// <summary>A short description of the role for lists and exported error reports.</summary>
    [JsonIgnore] public string Describe => Role switch
    {
        SheetRole.Tag => $"Tagged value {Name}",
        SheetRole.Attribute => $"Attribute {Name}",
        SheetRole.Field => $"Field {Name}",
        SheetRole.Value when !string.IsNullOrEmpty(Name) => $"Value of {Name}",
        SheetRole.Unit when !string.IsNullOrEmpty(Name) => $"Unit of {Name}",
        SheetRole.Relationship => $"{(Incoming ? "← " : "")}{(RelationshipKind is { } k ? Metamodel.DisplayName(k) : Relationship)}{(Incoming ? "" : " →")}",
        SheetRole.PartOf => "Part of",
        _ => SheetMapping.RoleLabel(Role),
    };
}

/// <summary>
/// A column mapping for spreadsheet import and export (Cameo's Excel/CSV Import mapping, EA's CSV import/export
/// specification). Saved as JSON per project (<see cref="SheetPresets"/>) and exportable.
/// </summary>
public sealed class SheetMapping
{
    [JsonPropertyName("name")] public string Name { get; set; } = "Mapping";
    /// <summary>Target kind id (see <see cref="SheetKinds"/>), or "" when a <see cref="SheetRole.Kind"/> column holds the kind.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "Block";
    /// <summary>Stereotype applied to every created element (and required on matched ones).</summary>
    [JsonPropertyName("stereotype")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Stereotype { get; set; }
    [JsonPropertyName("keyMatch")] public SheetKeyMatch KeyMatch { get; set; } = SheetKeyMatch.ExternalId;
    /// <summary>Field that stores external keys (<see cref="SheetKeyMatch.ExternalId"/>): "extId", "reqId", "tag:Part Number" …</summary>
    [JsonPropertyName("keyField")] public string KeyField { get; set; } = DefaultKeyField;
    /// <summary>Hierarchy from the indentation of the name column.</summary>
    [JsonPropertyName("indent")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool IndentHierarchy { get; set; }
    [JsonPropertyName("hierarchy")] public SheetHierarchy Hierarchy { get; set; } = SheetHierarchy.Containment;
    [JsonPropertyName("createMissingTypes")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool CreateMissingTypes { get; set; }
    /// <summary>Header row (zero-based); null = detect.</summary>
    [JsonPropertyName("headerRow")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? HeaderRow { get; set; }
    /// <summary>"By column" mappings: the kinds exported by default (target kind ids); empty = the common kinds.</summary>
    [JsonPropertyName("kinds")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Kinds { get; set; }
    [JsonPropertyName("columns")] public List<SheetColumn> Columns { get; set; } = [];

    public const string DefaultKeyField = "extId";

    [JsonIgnore] public bool KindByColumn => string.IsNullOrEmpty(Kind) || Columns.Any(c => c.Role == SheetRole.Kind);

    public SheetColumn? Column(SheetRole role) => Columns.FirstOrDefault(c => c.Role == role);

    public SheetMapping Clone() => new()
    {
        Name = Name, Kind = Kind, Stereotype = Stereotype, KeyMatch = KeyMatch, KeyField = KeyField, IndentHierarchy = IndentHierarchy,
        Hierarchy = Hierarchy, CreateMissingTypes = CreateMissingTypes, HeaderRow = HeaderRow, Columns = Columns.Select(c => c.Clone()).ToList(),
        Kinds = Kinds?.ToList(),
    };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static SheetMapping FromJson(string json)
    {
        try
        {
            var m = JsonSerializer.Deserialize<SheetMapping>(json) ?? throw new FormatException("Empty mapping");
            m.Columns = m.Columns.Where(c => c != null).ToList();
            return m;
        }
        catch (JsonException ex) { throw new FormatException("Not a mapping preset: " + ex.Message); }
    }

    public static string RoleLabel(SheetRole r) => r switch
    {
        SheetRole.Ignore => "(ignore)",
        SheetRole.Key => "Key / id",
        SheetRole.Owner => "Owner / package path",
        SheetRole.Parent => "Parent (hierarchy)",
        SheetRole.Level => "Level (hierarchy)",
        SheetRole.Value => "Value (with unit)",
        SheetRole.Tag => "Tagged value",
        SheetRole.Attribute => "Custom attribute",
        SheetRole.Field => "Other field",
        SheetRole.PartOf => "Part of",
        _ => r.ToString(),
    };

    /// <summary>
    /// Maps sheet headers by name: the preset's own columns first, then common header names ("Description" → documentation,
    /// "Qty" → multiplicity, "Tag: Material" → tagged value, "Attr: Safety" → attribute …). Returns one column per header.
    /// </summary>
    public SheetMapping AutoMap(IReadOnlyList<string> headers)
    {
        var result = Clone();
        result.Columns = [];
        foreach (var raw in headers)
        {
            var h = raw.Trim();
            if (h.Length == 0) continue;
            var preset = Columns.FirstOrDefault(c => string.Equals(c.Column.Trim(), h, StringComparison.OrdinalIgnoreCase));
            result.Columns.Add(preset?.Clone() ?? Guess(h));
        }
        return result;
    }

    /// <summary>A role for an unmapped header from its name.</summary>
    public static SheetColumn Guess(string header)
    {
        var h = header.Trim();
        var l = h.ToLowerInvariant();
        static string After(string s, int n) => s[n..].Trim();
        if (l.StartsWith("tag:")) return new(h, SheetRole.Tag, After(h, 4));
        if (l.StartsWith("attr:")) return new(h, SheetRole.Attribute, After(h, 5));
        if (l.StartsWith("field:")) return new(h, SheetRole.Field, After(h, 6));
        return l switch
        {
            "name" or "title" or "element" or "element name" => new(h, SheetRole.Name),
            "documentation" or "description" or "doc" or "notes" or "comment" => new(h, SheetRole.Documentation),
            "id" or "key" or "external id" or "ext id" or "part number" or "p/n" or "pn" or "req id" or "requirement id" => new(h, SheetRole.Key),
            "kind" or "element kind" or "metaclass" or "element type" => new(h, SheetRole.Kind),
            "owner" or "package" or "path" or "package path" or "owner path" or "block" or "parent block" => new(h, SheetRole.Owner),
            "parent" or "parent id" or "parent key" => new(h, SheetRole.Parent),
            "level" or "bom level" or "indent" => new(h, SheetRole.Level),
            "type" or "data type" => new(h, SheetRole.Type),
            "multiplicity" or "qty" or "quantity" or "count" => new(h, SheetRole.Multiplicity),
            "value" or "default value" or "default" => new(h, SheetRole.Value),
            "unit" or "units" => new(h, SheetRole.Unit),
            "stereotype" or "stereotypes" => new(h, SheetRole.Stereotypes),
            "text" or "requirement text" => new(h, SheetRole.Field, FieldKeys.Text),
            "direction" => new(h, SheetRole.Field, FieldKeys.Direction),
            "sql type" or "sqltype" => new(h, SheetRole.Field, FieldKeys.SqlType),
            "priority" => new(h, SheetRole.Field, ReqFields.Priority),
            "status" => new(h, SheetRole.Field, ReqFields.Status),
            "rationale" => new(h, SheetRole.Field, ReqFields.Rationale),
            "risk" => new(h, SheetRole.Field, ReqFields.Risk),
            "verification method" => new(h, SheetRole.Field, ReqFields.VerificationMethod),
            "satisfies" => new(h, SheetRole.Relationship) { Relationship = nameof(ElementKind.Satisfy) },
            "satisfied by" => new(h, SheetRole.Relationship) { Relationship = nameof(ElementKind.Satisfy), Incoming = true },
            "verifies" => new(h, SheetRole.Relationship) { Relationship = nameof(ElementKind.Verify) },
            "verified by" => new(h, SheetRole.Relationship) { Relationship = nameof(ElementKind.Verify), Incoming = true },
            "traces to" or "trace" or "traces" => new(h, SheetRole.Relationship) { Relationship = nameof(ElementKind.Trace) },
            "allocated to" => new(h, SheetRole.Relationship) { Relationship = nameof(ElementKind.Allocate) },
            "allocated from" => new(h, SheetRole.Relationship) { Relationship = nameof(ElementKind.Allocate), Incoming = true },
            "refines" => new(h, SheetRole.Relationship) { Relationship = nameof(ElementKind.Refine) },
            "derived from" => new(h, SheetRole.Relationship) { Relationship = nameof(ElementKind.DeriveReqt) },
            "depends on" => new(h, SheetRole.Relationship) { Relationship = nameof(ElementKind.Dependency) },
            "part of" or "used in" or "assembly" => new(h, SheetRole.PartOf),
            _ => new(h, SheetRole.Ignore),
        };
    }
}

/// <summary>A target kind of the mapping dialog: an element kind, optionally narrowed (part vs value property) or stereotyped (signal).</summary>
public sealed record SheetKind(string Id, string Label, ElementKind Kind, string? Stereotype = null)
{
    /// <summary>Does an existing element belong to this target kind?</summary>
    public bool Matches(ModelRepository m, Element e)
    {
        if (e.Kind != Kind) return false;
        if (Stereotype != null && !e.Stereotypes.Contains(Stereotype, StringComparer.OrdinalIgnoreCase)) return false;
        return Id switch
        {
            SheetKinds.PartProperty => SheetKinds.IsPart(m, e),
            SheetKinds.ValueProperty => !SheetKinds.IsPart(m, e),
            _ => true,
        };
    }
}

/// <summary>The target kinds offered for import, and parsing of "kind" cells.</summary>
public static class SheetKinds
{
    public const string PartProperty = "PartProperty";
    public const string ValueProperty = "ValueProperty";
    public const string Signal = "Signal";

    /// <summary>The common SysML kinds first (the list the dialog shows on top), then every other element kind.</summary>
    public static readonly IReadOnlyList<SheetKind> Common =
    [
        new("Block", "Block", ElementKind.Block),
        new(PartProperty, "Part property", ElementKind.Property),
        new(ValueProperty, "Value property", ElementKind.Property),
        new("Port", "Port", ElementKind.Port),
        new("InterfaceBlock", "Interface block", ElementKind.InterfaceBlock),
        new(Signal, "Signal", ElementKind.Class, "signal"),
        new("ValueType", "Value type", ElementKind.ValueType),
        new("Actor", "Actor", ElementKind.Actor),
        new("UseCase", "Use case", ElementKind.UseCase),
        new("Activity", "Activity", ElementKind.Activity),
        new("Action", "Action", ElementKind.Action),
        new("StateMachine", "State machine", ElementKind.StateMachine),
        new("State", "State", ElementKind.State),
        new("Requirement", "Requirement", ElementKind.Requirement),
        new("TestCase", "Test case", ElementKind.TestCase),
        new("ConstraintBlock", "Constraint block", ElementKind.ConstraintBlock),
        new("Table", "Table", ElementKind.Table),
        new("Column", "Column", ElementKind.Column),
        new("Package", "Package", ElementKind.Package),
    ];

    private static readonly ElementKind[] NotImportable =
    [
        ElementKind.Model, ElementKind.Diagram, ElementKind.TableView, ElementKind.QueryView, ElementKind.ValidationRule,
        ElementKind.InteractionOperand,
    ];

    /// <summary>Every target kind: <see cref="Common"/> and then the remaining element kinds by display name.</summary>
    public static readonly IReadOnlyList<SheetKind> All = Common.Concat(
        Metamodel.ElementKinds.Where(k => !NotImportable.Contains(k) && Common.All(c => c.Id != k.ToString()) && k != ElementKind.Property)
                 .Select(k => new SheetKind(k.ToString(), Metamodel.DisplayName(k), k))
                 .OrderBy(k => k.Label, StringComparer.OrdinalIgnoreCase))
        .Append(new SheetKind("Property", "Property (any)", ElementKind.Property)).ToList();

    public static SheetKind? ById(string? id) =>
        string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses a kind cell: an id ("PartProperty"), a label ("Part property"), a short form ("part", "value") or a metaclass name.</summary>
    public static SheetKind? Parse(string? text)
    {
        var t = (text ?? "").Trim().Trim('«', '»').Trim();
        if (t.Length == 0) return null;
        var squashed = new string(t.Where(char.IsLetterOrDigit).ToArray());
        foreach (var k in All)
            if (string.Equals(k.Id, squashed, StringComparison.OrdinalIgnoreCase) || string.Equals(k.Label, t, StringComparison.OrdinalIgnoreCase)
                || string.Equals(new string(k.Label.Where(char.IsLetterOrDigit).ToArray()), squashed, StringComparison.OrdinalIgnoreCase))
                return k;
        return squashed.ToLowerInvariant() switch
        {
            "part" or "parts" => ById(PartProperty),
            "value" or "values" => ById(ValueProperty),
            "interface" when ById("InterfaceBlock") is { } ib => ib,
            "flowproperty" => ById("Property"),
            "constraint" => ById("ConstraintBlock"),
            "test" => ById("TestCase"),
            "req" => ById("Requirement"),
            "opaqueaction" or "callbehavioraction" => ById("Action"),
            _ => null,
        };
    }

    /// <summary>A part property is typed by a block-like classifier; everything else is a value (or reference-free) property.</summary>
    public static bool IsPart(ModelRepository m, Element property) =>
        m.ResolveType(property) is { Kind: ElementKind.Block or ElementKind.Class or ElementKind.Component or ElementKind.ConstraintBlock };

    /// <summary>The target kind an existing element is exported as.</summary>
    public static SheetKind Of(ModelRepository m, Element e)
    {
        if (e.Kind == ElementKind.Property) return ById(IsPart(m, e) ? PartProperty : ValueProperty)!;
        if (e.Kind == ElementKind.Class && e.Stereotypes.Contains("signal", StringComparer.OrdinalIgnoreCase)) return ById(Signal)!;
        return ById(e.Kind.ToString()) ?? new SheetKind(e.Kind.ToString(), Metamodel.DisplayName(e.Kind), e.Kind);
    }
}

/// <summary>Built-in mapping presets and per-project presets (stored as JSON on the model root, so they save, undo and replicate).</summary>
public static class SheetPresets
{
    /// <summary>Model-root field holding the project's saved presets (a JSON array of mappings).</summary>
    public const string RootField = "sheetPresets";

    public static SheetMapping Requirements() => new()
    {
        Name = "Requirements", Kind = "Requirement", KeyMatch = SheetKeyMatch.ExternalId, KeyField = FieldKeys.RequirementId,
        Hierarchy = SheetHierarchy.Containment,
        Columns =
        [
            new("Id", SheetRole.Key), new("Name", SheetRole.Name), new("Text", SheetRole.Field, FieldKeys.Text),
            new("Priority", SheetRole.Field, ReqFields.Priority), new("Status", SheetRole.Field, ReqFields.Status),
            new("Owner", SheetRole.Field, ReqFields.Owner), new("Verification Method", SheetRole.Field, ReqFields.VerificationMethod),
            new("Risk", SheetRole.Field, ReqFields.Risk), new("Rationale", SheetRole.Field, ReqFields.Rationale),
            new("Documentation", SheetRole.Documentation), new("Parent Id", SheetRole.Parent),
            new("Satisfied By", SheetRole.Relationship) { Relationship = nameof(ElementKind.Satisfy), Incoming = true },
            new("Verified By", SheetRole.Relationship) { Relationship = nameof(ElementKind.Verify), Incoming = true },
        ],
    };

    public static SheetMapping PartsList() => new()
    {
        Name = "Parts list (BOM)", Kind = "Block", KeyMatch = SheetKeyMatch.ExternalId, KeyField = FieldKeys.TagPrefix + "Part Number",
        Hierarchy = SheetHierarchy.PartOf,
        Columns =
        [
            new("Level", SheetRole.Level), new("Part Number", SheetRole.Key), new("Name", SheetRole.Name),
            new("Description", SheetRole.Documentation), new("Qty", SheetRole.Multiplicity),
            new("Mass", SheetRole.Value, "mass"), new("Material", SheetRole.Tag, "Material"), new("Supplier", SheetRole.Tag, "Supplier"),
        ],
    };

    public static SheetMapping InterfacesSignals() => new()
    {
        Name = "Interfaces / signals", Kind = "", KeyMatch = SheetKeyMatch.QualifiedName,
        Kinds = ["InterfaceBlock", SheetKinds.Signal, nameof(ElementKind.Interface), "Port"],
        Columns =
        [
            new("Qualified Name", SheetRole.Key), new("Kind", SheetRole.Kind), new("Type", SheetRole.Type),
            new("Direction", SheetRole.Field, FieldKeys.Direction), new("Multiplicity", SheetRole.Multiplicity),
            new("Stereotypes", SheetRole.Stereotypes), new("Documentation", SheetRole.Documentation),
        ],
    };

    public static SheetMapping ValueProperties() => new()
    {
        Name = "Value properties", Kind = SheetKinds.ValueProperty, KeyMatch = SheetKeyMatch.QualifiedName,
        Columns =
        [
            new("Block", SheetRole.Owner), new("Name", SheetRole.Name), new("Type", SheetRole.Type),
            new("Value", SheetRole.Value), new("Unit", SheetRole.Unit), new("Multiplicity", SheetRole.Multiplicity),
            new("Documentation", SheetRole.Documentation),
        ],
    };

    public static IReadOnlyList<SheetMapping> BuiltIn() => [Requirements(), PartsList(), InterfacesSignals(), ValueProperties()];

    /// <summary>The project's saved presets.</summary>
    public static List<SheetMapping> Project(ModelRepository m)
    {
        var json = m.Root.Get(RootField);
        if (json.Length == 0) return [];
        try { return JsonSerializer.Deserialize<List<SheetMapping>>(json)?.Where(p => p != null).ToList() ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>Saves (or replaces, by name) a preset in the project (one undo step).</summary>
    public static void Save(ModelRepository m, SheetMapping preset)
    {
        var list = Project(m);
        list.RemoveAll(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        list.Add(preset.Clone());
        m.Set(m.Root, RootField, JsonSerializer.Serialize(list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)));
    }

    public static void Delete(ModelRepository m, string name)
    {
        var list = Project(m);
        if (list.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) == 0) return;
        m.Set(m.Root, RootField, list.Count == 0 ? null : JsonSerializer.Serialize(list));
    }
}
