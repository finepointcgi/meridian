using System.Text.Json;
using System.Text.Json.Serialization;
using Meridian.Core.Analysis;
using Meridian.Core.Model;

namespace Meridian.Serialization.Reports;

/// <summary>
/// A document template for program deliverables (SRS, ICD, SDD, VCRM …): front matter, a tree of numbered sections,
/// and data-driven blocks. Serialises to JSON (see <see cref="ToJson"/>) so it can live in a file or on a model element.
/// <para>
/// Text anywhere in a template may use placeholders: <c>{name}</c>, <c>{reqId}</c>, <c>{tag:priority}</c> — any
/// <see cref="ModelValues"/> key of the current loop element; <c>{var.key}</c> for an outer loop's named element;
/// <c>{doc.title}</c>, <c>{doc.number}</c>, <c>{doc.revision}</c>, <c>{doc.classification}</c>, <c>{doc.author}</c>,
/// <c>{model.name}</c>, <c>{model.documentation}</c>, <c>{date}</c>, <c>{index}</c>, <c>{count}</c>. Cross-references:
/// <c>{ref:name}</c> (e.g. "Figure 3-2", "Table 4-1", "Section 2.3"), <c>{page:name}</c> and <c>{title:name}</c>, where
/// <c>name</c> is the <c>Ref</c> of a section, figure or table. <c>{{</c> writes a literal brace.
/// </para>
/// </summary>
public sealed class DocTemplate
{
    public string Name { get; set; } = "New template";
    public string Description { get; set; } = "";

    // ------------------------------------------------------------- document properties
    public string Title { get; set; } = "{model.name}";
    public string Subtitle { get; set; } = "";
    public string DocumentNumber { get; set; } = "";
    public string Revision { get; set; } = "A";
    public string Classification { get; set; } = "UNCLASSIFIED";
    public string Author { get; set; } = "";
    public string Organization { get; set; } = "";
    /// <summary>Markdown shown on the title page under the document properties.</summary>
    public string TitleNotes { get; set; } = "";
    public string PageSize { get; set; } = "Letter";   // Letter | A4

    // ------------------------------------------------------------- front matter
    public bool TitlePage { get; set; } = true;
    public bool RevisionHistory { get; set; } = true;
    public List<RevisionEntry> Revisions { get; set; } = new();
    public bool TableOfContents { get; set; } = true;
    public int TocDepth { get; set; } = 3;
    public bool ListOfFigures { get; set; } = true;
    public bool ListOfTables { get; set; } = true;
    /// <summary>"chapter" numbers captions per top-level section ("Figure 3-2"); "sequential" numbers them through the document.</summary>
    public string CaptionNumbering { get; set; } = "chapter";

    public List<DocSection> Sections { get; set; } = new();

    // ------------------------------------------------------------- JSON

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static DocTemplate FromJson(string json) =>
        JsonSerializer.Deserialize<DocTemplate>(json, JsonOptions) ?? throw new JsonException("Empty template");

    public DocTemplate Clone() => FromJson(ToJson());

    /// <summary>Every section in document order (depth first).</summary>
    public IEnumerable<DocSection> AllSections() => Sections.SelectMany(s => s.SelfAndDescendants());
}

public sealed class RevisionEntry
{
    public string Revision { get; set; } = "";
    public string Date { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
}

/// <summary>A (nested) document section. Heading numbers (1, 1.1, 1.1.1) are assigned at generation time.</summary>
public sealed class DocSection
{
    public string Title { get; set; } = "Section";
    /// <summary>Cross-reference name, used by <c>{ref:…}</c>.</summary>
    public string? Ref { get; set; }
    public bool Numbered { get; set; } = true;
    public bool PageBreakBefore { get; set; }
    /// <summary>Lay this section (and its subsections) out on landscape pages, e.g. for wide matrices.</summary>
    public bool Landscape { get; set; }
    public List<DocBlock> Blocks { get; set; } = new();
    public List<DocSection> Sections { get; set; } = new();

    public IEnumerable<DocSection> SelfAndDescendants() => Sections.SelectMany(s => s.SelfAndDescendants()).Prepend(this);
}

// ======================================================================= blocks

/// <summary>A content block inside a section.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(DocumentationBlock), "documentation")]
[JsonDerivedType(typeof(ForEachBlock), "forEach")]
[JsonDerivedType(typeof(IfBlock), "if")]
[JsonDerivedType(typeof(FieldsBlock), "fields")]
[JsonDerivedType(typeof(TableBlock), "table")]
[JsonDerivedType(typeof(RequirementTableBlock), "requirements")]
[JsonDerivedType(typeof(TraceTableBlock), "traceTable")]
[JsonDerivedType(typeof(TraceMatrixBlock), "traceMatrix")]
[JsonDerivedType(typeof(CoverageBlock), "coverage")]
[JsonDerivedType(typeof(DiagramBlock), "diagram")]
[JsonDerivedType(typeof(ElementListBlock), "list")]
[JsonDerivedType(typeof(StatisticsBlock), "statistics")]
[JsonDerivedType(typeof(ValidationBlock), "validation")]
[JsonDerivedType(typeof(PageBreakBlock), "pageBreak")]
[JsonDerivedType(typeof(InstancesBlock), "instances")]
public abstract class DocBlock
{
    [JsonIgnore] public abstract string TypeLabel { get; }
    /// <summary>One-line description for the template editor tree.</summary>
    [JsonIgnore] public virtual string Summary => TypeLabel;
    /// <summary>Nested blocks (loops and conditions), or null for leaf blocks.</summary>
    [JsonIgnore] public virtual List<DocBlock>? Children => null;

    public static readonly IReadOnlyList<(string Type, string Label, Func<DocBlock> Create)> Catalog =
    [
        ("text", "Text (markdown)", () => new TextBlock()),
        ("documentation", "Element documentation", () => new DocumentationBlock()),
        ("forEach", "For each element", () => new ForEachBlock()),
        ("if", "Condition", () => new IfBlock()),
        ("fields", "Element fields / tagged values", () => new FieldsBlock()),
        ("table", "Generic table", () => new TableBlock()),
        ("requirements", "Requirement list", () => new RequirementTableBlock()),
        ("traceTable", "Traceability table", () => new TraceTableBlock()),
        ("traceMatrix", "Traceability matrix", () => new TraceMatrixBlock()),
        ("coverage", "Coverage summary", () => new CoverageBlock()),
        ("diagram", "Diagram(s)", () => new DiagramBlock()),
        ("list", "Element list", () => new ElementListBlock()),
        ("statistics", "Model statistics", () => new StatisticsBlock()),
        ("validation", "Validation issues", () => new ValidationBlock()),
        ("pageBreak", "Page break", () => new PageBreakBlock()),
        ("instances", "Instances (instance table)", () => new InstancesBlock()),
    ];
}

/// <summary>Markdown text: paragraphs, <c>**bold**</c>, <c>*italic*</c>, <c>`code`</c>, bullet / numbered lists, placeholders and cross-references.</summary>
public sealed class TextBlock : DocBlock
{
    public string Markdown { get; set; } = "";
    public override string TypeLabel => "Text";
    public override string Summary => Markdown.Length == 0 ? "Text" : "Text: " + Shorten(Markdown);
    internal static string Shorten(string s) { s = s.ReplaceLineEndings(" "); return s.Length > 48 ? s[..48] + "…" : s; }
}

/// <summary>A field of the current element (by default its documentation) rendered as markdown.</summary>
public sealed class DocumentationBlock : DocBlock
{
    public string Key { get; set; } = "documentation";
    public string EmptyText { get; set; } = "";
    public override string TypeLabel => "Documentation";
    public override string Summary => $"Documentation: {Key}";
}

/// <summary>Repeats its blocks for each element matching a <see cref="ModelQuery"/> (or related to the current element).</summary>
public sealed class ForEachBlock : DocBlock
{
    /// <summary>ModelQuery text, e.g. <c>kind:Requirement in:{id} where priority = high</c>. Placeholders are substituted first.</summary>
    public string Query { get; set; } = "kind:Requirement";
    /// <summary>Optional relationship key (<c>satisfiedBy</c>, <c>rel:in:Verify</c>, <c>children</c>): iterate elements related to the current element instead; <see cref="Query"/> then filters.</summary>
    public string? Related { get; set; }
    /// <summary>Name for the loop element so nested blocks can use <c>{var.key}</c>.</summary>
    public string? Variable { get; set; }
    public string? SortBy { get; set; }
    public bool Descending { get; set; }
    /// <summary>When set, each element gets its own numbered sub-heading with this text (e.g. <c>{reqId} {name}</c>).</summary>
    public string? Heading { get; set; }
    /// <summary>Cross-reference name for each element's heading (e.g. <c>req-{reqId}</c>).</summary>
    public string? HeadingRef { get; set; }
    public bool PageBreakEach { get; set; }
    public int Limit { get; set; }
    public string EmptyText { get; set; } = "";
    public List<DocBlock> Blocks { get; set; } = new();
    public override string TypeLabel => "For each";
    public override string Summary => $"For each {(string.IsNullOrWhiteSpace(Related) ? "" : Related + " ")}{Query}";
    public override List<DocBlock>? Children => Blocks;
}

/// <summary>Conditional content: a condition on the current element and/or "some element matches a query".</summary>
public sealed class IfBlock : DocBlock
{
    /// <summary>Condition expression on the current element, e.g. <c>priority = high and has:in:verify</c>.</summary>
    public string? Condition { get; set; }
    /// <summary>A query that must return at least one element.</summary>
    public string? Exists { get; set; }
    public List<DocBlock> Blocks { get; set; } = new();
    public List<DocBlock> Else { get; set; } = new();
    public override string TypeLabel => "If";
    public override string Summary => $"If {Condition}{(string.IsNullOrWhiteSpace(Exists) ? "" : " exists " + Exists)}";
    public override List<DocBlock>? Children => Blocks;
}

/// <summary>Key/value table of the current element's fields, requirement attributes and tagged values.</summary>
public sealed class FieldsBlock : DocBlock
{
    /// <summary>Value keys; empty = kind, qualified name, stereotypes plus requirement attributes.</summary>
    public List<string> Keys { get; set; } = new();
    public bool IncludeTags { get; set; } = true;
    public bool IncludeEmpty { get; set; }
    /// <summary>"table" (two-column table) or "inline" (one "Key: value" line each).</summary>
    public string Layout { get; set; } = "table";
    public override string TypeLabel => "Fields";
    public override string Summary => Keys.Count == 0 ? "Fields (standard)" : "Fields: " + string.Join(", ", Keys);
}

/// <summary>Base for blocks that produce a captioned table or figure.</summary>
public abstract class CaptionedBlock : DocBlock
{
    public string? Caption { get; set; }
    /// <summary>Cross-reference name (placeholders allowed, e.g. <c>fig-{name}</c>).</summary>
    public string? Ref { get; set; }
}

/// <summary>A generic table: a saved <see cref="ElementKind.TableView"/> by name, or an inline query plus columns.</summary>
public sealed class TableBlock : CaptionedBlock
{
    public string? TableView { get; set; }
    public string Query { get; set; } = "kind:Block";
    public List<TableViewColumn> Columns { get; set; } = new();
    public string? SortBy { get; set; }
    public bool Descending { get; set; }
    public string EmptyText { get; set; } = "None.";
    public override string TypeLabel => "Table";
    public override string Summary => "Table: " + (TableView ?? Query);
}

/// <summary>Requirements with their attributes, as a table or as detailed paragraphs.</summary>
public sealed class RequirementTableBlock : CaptionedBlock
{
    public string Query { get; set; } = "kind:Requirement";
    public List<string> Attributes { get; set; } = ["reqId", "name", "text", "priority", "status", "verifyMethod"];
    /// <summary>"table" or "detailed".</summary>
    public string Style { get; set; } = "table";
    public override string TypeLabel => "Requirements";
    public override string Summary => $"Requirements ({Style}): {Query}";
}

/// <summary>The requirements traceability table (one row per requirement with its links).</summary>
public sealed class TraceTableBlock : CaptionedBlock
{
    public string Query { get; set; } = "kind:Requirement";
    public List<TraceColumn> Columns { get; set; } = [TraceColumn.SatisfiedBy, TraceColumn.VerifiedBy];
    public bool ShowCoverage { get; set; } = true;
    public bool GapsOnly { get; set; }
    public override string TypeLabel => "Traceability table";
    public override string Summary => "Traceability: " + string.Join(", ", Columns);
}

/// <summary>A dependency matrix: rows and columns from queries, marked where a relationship of the given kinds exists.</summary>
public sealed class TraceMatrixBlock : CaptionedBlock
{
    public string RowQuery { get; set; } = "kind:Requirement";
    public string ColumnQuery { get; set; } = "kind:Block";
    public List<ElementKind> RelationshipKinds { get; set; } = [ElementKind.Satisfy, ElementKind.Verify, ElementKind.Allocate, ElementKind.Refine, ElementKind.Trace];
    /// <summary>Columns per table before the matrix is split into continuation tables.</summary>
    public int MaxColumns { get; set; } = 20;
    /// <summary>Leave out columns without any link, and rows without a link in a table's columns. A requirement × design
    /// matrix of a big model is otherwise mostly empty cells: 16,000 requirements × 6,000 blocks is ~5 million rows.</summary>
    public bool HideEmpty { get; set; }
    public override string TypeLabel => "Matrix";
    public override string Summary => $"Matrix: {RowQuery} × {ColumnQuery}";
}

public sealed class CoverageBlock : CaptionedBlock
{
    public bool ByPackage { get; set; } = true;
    public bool ListGaps { get; set; } = true;
    public override string TypeLabel => "Coverage";
}

/// <summary>One diagram (by name / id, or the current loop element) or every diagram matching a query and type filter.</summary>
public sealed class DiagramBlock : CaptionedBlock
{
    public string? Diagram { get; set; }
    /// <summary>Used when <see cref="Diagram"/> is empty and the current element is not a diagram: e.g. <c>kind:Diagram in:{id}</c>.</summary>
    public string? Query { get; set; }
    public List<DiagramType> DiagramTypes { get; set; } = new();
    public bool IncludeDocumentation { get; set; } = true;
    /// <summary>Maximum figure height as a fraction of the printable page height.</summary>
    public double MaxHeight { get; set; } = 0.75;
    public override string TypeLabel => "Diagram";
    public override string Summary => "Diagram: " + (Diagram ?? Query ?? "current element") + (DiagramTypes.Count > 0 ? " [" + string.Join(", ", DiagramTypes) + "]" : "");
}

/// <summary>A bullet list of matching elements.</summary>
public sealed class ElementListBlock : DocBlock
{
    public string Query { get; set; } = "kind:Package";
    public string? Related { get; set; }
    public string Format { get; set; } = "**{name}** — {documentation}";
    public bool Numbered { get; set; }
    public string EmptyText { get; set; } = "None.";
    public override string TypeLabel => "List";
    public override string Summary => "List: " + (Related ?? Query);
}

public sealed class StatisticsBlock : CaptionedBlock
{
    public override string TypeLabel => "Statistics";
}

public sealed class ValidationBlock : CaptionedBlock
{
    public bool IncludeInfo { get; set; }
    public int Limit { get; set; } = 500;
    public override string TypeLabel => "Validation";
}

/// <summary>
/// The instances of a block (Cameo instance table): one row per instance, a column per feature with its slot value; or,
/// with <see cref="Layout"/> "slots", a feature / value table per instance.
/// </summary>
public sealed class InstancesBlock : CaptionedBlock
{
    /// <summary>The classifier: name, qualified name or id (placeholders allowed). Empty = the current loop element, else every instance.</summary>
    public string? Classifier { get; set; }
    /// <summary>"table" (one row per instance) or "slots" (a table per instance).</summary>
    public string Layout { get; set; } = "table";
    /// <summary>Include instances nested in other instances (part instances).</summary>
    public bool IncludeNested { get; set; } = true;
    public string EmptyText { get; set; } = "No instances.";
    public override string TypeLabel => "Instances";
    public override string Summary => "Instances: " + (string.IsNullOrWhiteSpace(Classifier) ? "current element" : Classifier) + (Layout == "slots" ? " (slots)" : "");
}

public sealed class PageBreakBlock : DocBlock
{
    public override string TypeLabel => "Page break";
}
