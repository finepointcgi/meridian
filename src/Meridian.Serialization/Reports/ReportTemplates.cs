using Meridian.Core.Analysis;
using Meridian.Core.Model;

namespace Meridian.Serialization.Reports;

/// <summary>
/// The built-in deliverable templates, and template storage: as model elements (a <see cref="ElementKind.Comment"/>
/// in the "Report Templates" package carrying the template JSON, so templates version, diff and replicate with the
/// model) or as JSON files.
/// </summary>
public static class ReportTemplates
{
    public const string FieldKey = "reportTemplate";
    public const string PackageName = "Report Templates";

    // ================================================================== storage

    /// <summary>Templates saved in the model, by element.</summary>
    public static IReadOnlyList<(Element Element, DocTemplate Template)> InModel(ModelRepository m)
    {
        var list = new List<(Element, DocTemplate)>();
        foreach (var e in m.OfKind(ElementKind.Comment).Where(e => e.Get(FieldKey).Length > 0).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            if (TryLoad(e, out var t)) list.Add((e, t));
        return list;
    }

    public static bool TryLoad(Element e, out DocTemplate template)
    {
        template = null!;
        try { template = DocTemplate.FromJson(e.Get(FieldKey)); return true; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    /// <summary>Saves (creates or updates) a template on a model element in one undoable step.</summary>
    public static Element Save(ModelRepository m, DocTemplate t, Element? existing = null)
    {
        var json = t.ToJson();
        using (m.History.Transaction($"Save report template {t.Name}"))
        {
            var e = existing != null && m.Contains(existing) ? existing : m.Create(Home(m), ElementKind.Comment, t.Name);
            if (e.Name != t.Name) m.Rename(e, t.Name);
            if (e.Get(FieldKey) != json) m.Set(e, FieldKey, json);
            if (e.Get(FieldKeys.Body) != t.Description) m.Set(e, FieldKeys.Body, t.Description);
            return e;
        }
    }

    private static Element Home(ModelRepository m) =>
        m.Root.OwnedElements.FirstOrDefault(e => e.Kind == ElementKind.Package && e.Name == PackageName)
        ?? m.Create(m.Root, ElementKind.Package, PackageName);

    public static DocTemplate LoadFile(string path) => DocTemplate.FromJson(File.ReadAllText(path));
    public static void SaveFile(DocTemplate t, string path) => File.WriteAllText(path, t.ToJson());

    // ================================================================== built-ins

    /// <summary>Fresh copies of the built-in templates (safe to edit).</summary>
    public static IReadOnlyList<DocTemplate> BuiltIn => [Srs(), Icd(), Sdd(), Vcrm(), ModelOverview(), DataDictionary()];

    private static DocSection S(string title, params DocBlock[] blocks) => new() { Title = title, Blocks = blocks.ToList() };
    private static DocSection S(string title, DocBlock[] blocks, params DocSection[] subs) => new() { Title = title, Blocks = blocks.ToList(), Sections = subs.ToList() };
    private static TextBlock T(string md) => new() { Markdown = md };

    private static DocSection Scope(string kind, string purpose) => S("Scope", [],
        S("Identification", T($"This {kind} applies to **{{model.name}}**. It is generated from the system model; the model is the authoritative source.")),
        S("System overview", new DocumentationBlock { EmptyText = "_No system description is recorded on the model root._" }),
        S("Document overview", T(purpose)));

    public static DocTemplate Srs() => new()
    {
        Name = "System Requirements Specification",
        Description = "SRS (MIL-STD-498 / IEEE 29148 outline): requirements with attributes, qualification provisions and traceability.",
        Title = "{model.name}", Subtitle = "System Requirements Specification", DocumentNumber = "SRS-001",
        Sections =
        [
            Scope("SRS", "Section {ref:requirements} states the requirements; {ref:qualification} the qualification provisions and {ref:traceability} the traceability, " +
                         "summarised in {ref:tbl-req-summary}."),
            S("Applicable documents", T("- Model: {model.name}\n- Interface Control Document (ICD)\n- System Design Description (SDD)")),
            new DocSection
            {
                Title = "Requirements", Ref = "requirements", PageBreakBefore = true,
                Blocks = [T("The requirements are summarised in {ref:tbl-req-summary} and detailed in {ref:req-details}. The verification method of each is given in {ref:qualification}."),
                          new RequirementTableBlock { Caption = "Requirements summary", Ref = "tbl-req-summary", Attributes = ["reqId", "name", "priority", "status", "verifyMethod"] }],
                Sections =
                [
                    new DocSection { Title = "Requirement details", Ref = "req-details", Blocks = [new ForEachBlock
                    {
                        Query = "kind:Requirement", SortBy = "reqId", Heading = "{reqId} {name}", HeadingRef = "req-{reqId}",
                        Blocks =
                        [
                            T("{text}"),
                            new FieldsBlock { Keys = ["priority", "status", "verifyMethod", "risk", "rationale", "field:owner", "satisfiedBy", "verifiedBy", "derivedFrom"], IncludeTags = true },
                        ],
                    }] },
                ],
            },
            new DocSection
            {
                Title = "Qualification provisions", Ref = "qualification",
                Blocks = [T("{ref:tbl-qualification} lists the verification method of each requirement."),
                          new TableBlock { Caption = "Qualification methods", Ref = "tbl-qualification", Query = "kind:Requirement",
                                           Columns = [new("reqId"), new("name"), new("verifyMethod", "Method"), new("verifiedBy")], SortBy = "reqId" }],
            },
            new DocSection
            {
                Title = "Requirements traceability", Ref = "traceability", PageBreakBefore = true, Landscape = true,
                Blocks = [new CoverageBlock { Ref = "tbl-coverage" },
                          new TraceTableBlock { Columns = [TraceColumn.DerivedFrom, TraceColumn.SatisfiedBy, TraceColumn.VerifiedBy, TraceColumn.AllocatedTo] }],
            },
            new DocSection
            {
                Title = "Requirement diagrams", Numbered = true,
                Blocks = [new DiagramBlock { DiagramTypes = [DiagramType.Requirement], Caption = "{name}" }],
            },
        ],
    };

    public static DocTemplate Icd() => new()
    {
        Name = "Interface Control Document",
        Description = "ICD: interface definitions (interface blocks, ports, flows), interface connections and diagrams.",
        Title = "{model.name}", Subtitle = "Interface Control Document", DocumentNumber = "ICD-001",
        Sections =
        [
            Scope("ICD", "Section {ref:interfaces} defines each interface; {ref:tbl-ports} lists every port of the system."),
            new DocSection
            {
                Title = "Interface definitions", Ref = "interfaces", PageBreakBefore = true,
                Blocks =
                [
                    new ForEachBlock
                    {
                        Query = "kind:InterfaceBlock,Interface", Heading = "{name}", HeadingRef = "if-{name}",
                        EmptyText = "_The model defines no interface blocks._",
                        Blocks =
                        [
                            new DocumentationBlock { EmptyText = "_No description._" },
                            new FieldsBlock(),
                            new IfBlock
                            {
                                Exists = "kind:Property,Port,Operation in:{id}",
                                Blocks = [new TableBlock { Caption = "{name} features", Query = "kind:Property,Port,Operation in:{id}",
                                                           Columns = [new("name"), new("kind"), new("type"), new("field:multiplicity", "Mult."), new("field:direction", "Direction"), new("documentation")] }],
                            },
                            new ListBlockAlias { Related = "rel:in:Realization" }.ToList("Realized by:"),
                        ],
                    },
                ],
            },
            new DocSection
            {
                Title = "Ports", Blocks =
                [
                    new TableBlock { Caption = "System ports", Ref = "tbl-ports", Query = "kind:Port",
                                     Columns = [new("owner"), new("name"), new("type"), new("field:direction", "Direction"), new("field:portKind", "Port kind")], SortBy = "owner" },
                ],
            },
            new DocSection
            {
                Title = "Interface connections", Landscape = true, Blocks =
                [
                    new TableBlock { Caption = "Connectors", Query = "kind:Connector", Columns = [new("source"), new("target"), new("field:itemFlow", "Item flow"), new("owner")] },
                ],
            },
            S("Interface diagrams", new DiagramBlock { DiagramTypes = [DiagramType.InternalBlock], Caption = "{name}" }),
        ],
    };

    /// <summary>
    /// SDD: package overview and block hierarchy, a section per design block with its internal structure (IBD) and behavior,
    /// interfaces from ports and connectors, behavior views and requirement allocation. Every diagram is a numbered,
    /// captioned figure; a diagram's documentation (the auto-diagrammer's one-line purpose) is printed above it.
    /// </summary>
    public static DocTemplate Sdd() => new()
    {
        Name = "System Design Description",
        Description = "SDD: package overview and architecture, a section per design block with its internal structure and behavior, interfaces (ports and connectors), behavior views and requirement allocation.",
        Title = "{model.name}", Subtitle = "System Design Description", DocumentNumber = "SDD-001",
        Sections =
        [
            Scope("SDD", "Section {ref:architecture} gives the architecture; {ref:design} describes each design element with its internal structure and behavior; " +
                         "{ref:interfaces} the interfaces; {ref:behavior} the system behavior and {ref:allocation} the allocation of requirements to the design."),
            new DocSection
            {
                Title = "System architecture", Ref = "architecture", PageBreakBefore = true,
                Blocks = [T("This section shows how the model is organised into packages and how the system decomposes into blocks.")],
                Sections =
                [
                    new DocSection
                    {
                        Title = "Package overview",
                        Blocks = [new DiagramBlock { DiagramTypes = [DiagramType.Package], Caption = "{name}", Ref = "fig-{name}" }],
                    },
                    new DocSection
                    {
                        Title = "Structure",
                        Blocks = [new DiagramBlock { DiagramTypes = [DiagramType.BlockDefinition, DiagramType.Class], Caption = "{name}", Ref = "fig-{name}" }],
                    },
                ],
            },
            new DocSection
            {
                Title = "Design elements", Ref = "design",
                Blocks =
                [
                    new ForEachBlock
                    {
                        Query = "kind:Block,Component,Class", Heading = "{name}", Variable = "block",
                        Blocks =
                        [
                            new DocumentationBlock { EmptyText = "_No description._" },
                            new FieldsBlock { Keys = ["stereotype", "owner", "satisfies", "allocatedFrom"] },
                            new IfBlock
                            {
                                Exists = "kind:Property,Port,Operation in:{id}",
                                Blocks = [new TableBlock { Caption = "{name} features", Query = "kind:Property,Port,Operation in:{id}",
                                                           Columns = [new("name"), new("kind"), new("type"), new("field:multiplicity", "Mult."), new("field:defaultValue", "Default")] }],
                            },
                            new DiagramBlock { Query = "kind:Diagram in:{id}", DiagramTypes = [DiagramType.InternalBlock, DiagramType.Parametric], Caption = "{block.name} – internal structure: {name}" },
                            new DiagramBlock { Query = "kind:Diagram in:{id}", DiagramTypes = [DiagramType.Activity, DiagramType.StateMachine, DiagramType.Sequence], Caption = "{block.name} – behavior: {name}" },
                        ],
                    },
                ],
            },
            new DocSection
            {
                Title = "Interfaces", Ref = "interfaces",
                Blocks =
                [
                    T("Interfaces are defined by interface blocks and realised by the ports of the design blocks; connectors join ports inside each internal block diagram."),
                    new ElementListBlock { Query = "kind:InterfaceBlock,Interface", Format = "**{name}** — {documentation}", EmptyText = "_The model defines no interface blocks._" },
                    new TableBlock { Caption = "Ports", Ref = "tbl-sdd-ports", Query = "kind:Port", SortBy = "owner",
                                     Columns = [new("owner"), new("name"), new("type"), new("field:direction", "Direction"), new("field:portKind", "Port kind")] },
                    new TableBlock { Caption = "Connectors", Query = "kind:Connector", Columns = [new("source"), new("target"), new("field:itemFlow", "Item flow"), new("owner")] },
                ],
            },
            new DocSection
            {
                Title = "Behavior", Ref = "behavior",
                Blocks = [new DiagramBlock { DiagramTypes = [DiagramType.UseCase, DiagramType.Activity, DiagramType.StateMachine, DiagramType.Sequence], Caption = "{name}" }],
            },
            new DocSection
            {
                Title = "Requirements allocation", Ref = "allocation", Landscape = true,
                Blocks = [new DiagramBlock { DiagramTypes = [DiagramType.Requirement], Caption = "{name}" },
                          new TraceTableBlock { Caption = "Requirement allocation to design", Columns = [TraceColumn.SatisfiedBy, TraceColumn.AllocatedTo], ShowCoverage = false },
                          new TraceMatrixBlock { Caption = "Requirement × design matrix", RowQuery = "kind:Requirement", ColumnQuery = "kind:Block", RelationshipKinds = [ElementKind.Satisfy, ElementKind.Allocate, ElementKind.Refine], HideEmpty = true }],
            },
        ],
    };

    public static DocTemplate Vcrm() => new()
    {
        Name = "Verification Cross-Reference Matrix / Test Report",
        Description = "VCRM and test report: verification summary, cross-reference matrix, test case results and open items.",
        Title = "{model.name}", Subtitle = "Verification Cross-Reference Matrix and Test Report", DocumentNumber = "VCRM-001",
        Sections =
        [
            Scope("report", "{ref:tbl-coverage} summarises verification status; the full matrix is in {ref:vcrm}."),
            S("Verification summary", new CoverageBlock { Ref = "tbl-coverage" }),
            new DocSection
            {
                Title = "Verification cross-reference matrix", Ref = "vcrm", Landscape = true, PageBreakBefore = true,
                Blocks =
                [
                    new TableBlock { Caption = "VCRM", Query = "kind:Requirement", SortBy = "reqId",
                                     Columns = [new("reqId"), new("name"), new("verifyMethod", "Method"), new("verifiedBy", "Verified by"), new("status"),
                                                new("verificationResult", "Analysis result"), new("verificationCriteria", "Criteria"), new("field:verify.value", "Computed"), new("field:verify.margin", "Margin")] },
                    new TraceMatrixBlock { Caption = "Requirement × test case", RowQuery = "kind:Requirement", ColumnQuery = "kind:TestCase", RelationshipKinds = [ElementKind.Verify], HideEmpty = true },
                ],
            },
            new DocSection
            {
                Title = "Test cases",
                Blocks =
                [
                    new ForEachBlock
                    {
                        Query = "kind:TestCase", Heading = "{name}", EmptyText = "_No test cases are modelled._",
                        Blocks =
                        [
                            new DocumentationBlock(),
                            new FieldsBlock { Keys = ["verdict", "verifies", "owner"] },
                            new IfBlock { Condition = "verdict = fail", Blocks = [T("> **Failed.** Corrective action is required before the verified requirements can be closed.")] },
                        ],
                    },
                ],
            },
            S("Open items", new RequirementTableBlock { Caption = "Requirements without verification", Query = "kind:Requirement where no:in:Verify", Attributes = ["reqId", "name", "verifyMethod", "status"] }),
        ],
    };

    /// <summary>The data dictionary: every table and view with its columns, types, keys, comments and subject areas, plus the database diagrams.</summary>
    public static DocTemplate DataDictionary() => new()
    {
        Name = "Data Dictionary",
        Description = "Data models: tables and views with columns, types, keys, defaults, comments and subject areas, and the database diagrams.",
        Title = "{model.name}", Subtitle = "Data Dictionary", DocumentNumber = "DD-001", RevisionHistory = false, ListOfFigures = true,
        Sections =
        [
            S("Introduction", T("This data dictionary describes the tables and views of **{model.name}** as of {date}. {ref:tbl-dd-tables} lists them; " +
                                "each is detailed with its columns below. Key markers: PK primary key, FK foreign key, UQ unique.")),
            S("Tables and views", new TableBlock
            {
                Caption = "Tables and views", Ref = "tbl-dd-tables", Query = "kind:Table", SortBy = "name", EmptyText = "The model has no tables.",
                Columns = [new("name", "Table"), new("owner", "Schema"), new(Core.Analysis.ModelValues.SubjectAreasKey, "Subject areas"), new("documentation", "Comment")],
            }),
            S("Subject areas", new ForEachBlock
            {
                Query = "kind:Package where db.role = subjectArea", SortBy = "name", Heading = "{name}", EmptyText = "_No subject areas are defined._",
                Blocks = [new DocumentationBlock(), new TableBlock { Query = "kind:Table where subjectAreas contains \"{name}\"", SortBy = "name", Columns = [new("name", "Table"), new("owner", "Schema"), new("documentation", "Comment")] }],
            }),
            new DocSection
            {
                Title = "Table details", PageBreakBefore = true,
                Blocks =
                [
                    new ForEachBlock
                    {
                        Query = "kind:Table", SortBy = "name", Heading = "{name}",
                        Blocks =
                        [
                            new DocumentationBlock { EmptyText = "_No comment._" },
                            new FieldsBlock { Keys = ["qualifiedName", Core.Analysis.ModelValues.SubjectAreasKey, "field:db.definition"], IncludeTags = false, Layout = "inline" },
                            new TableBlock
                            {
                                Caption = "Columns of {name}", Query = "kind:Column in:{id}", EmptyText = "No columns.",
                                Columns = [new("name", "Column"), new("field:sqlType", "Type"), new(Core.Analysis.ModelValues.KeysKey, "Keys"), new("field:nullable", "Nullable"),
                                           new("field:defaultValue", "Default"), new("documentation", "Comment")],
                            },
                        ],
                    },
                ],
            },
            S("Diagrams", new DiagramBlock { Query = "kind:Diagram", DiagramTypes = [DiagramType.Database], Caption = "{name}" }),
        ],
    };

    public static DocTemplate ModelOverview() => new()
    {
        Name = "Model Overview",
        Description = "A general model report: statistics, packages, every diagram, element specifications and validation results.",
        Title = "{model.name}", Subtitle = "Model Overview", RevisionHistory = false, ListOfTables = false,
        Sections =
        [
            S("Introduction", new DocumentationBlock(), T("This report summarises the model as of {date}. {ref:tbl-stats} lists its contents.")),
            S("Model contents", new StatisticsBlock { Ref = "tbl-stats" }, new ElementListBlock { Query = "kind:Package", Format = "**{qualifiedName}** — {documentation}" }),
            S("Diagrams", new ForEachBlock { Query = "kind:Diagram", Heading = "{name}", Blocks = [new DiagramBlock { Caption = "{name} ({diagramType})" }] }),
            S("Element specifications", new ForEachBlock
            {
                Query = "kind:Block,Class,InterfaceBlock,Component,Actor,UseCase,Activity,StateMachine", Heading = "{name}",
                Blocks = [new DocumentationBlock(), new FieldsBlock { Keys = ["kind", "qualifiedName", "stereotype"] }],
            }),
            S("Requirements", new RequirementTableBlock { Style = "detailed", Attributes = ["reqId", "name", "text", "priority", "status", "satisfiedBy", "verifiedBy"] }),
            S("Validation", new ValidationBlock()),
        ],
    };

    /// <summary>Helper to write "label + bullet list of related elements" inline in the template definitions above.</summary>
    private sealed class ListBlockAlias
    {
        public string Related { get; init; } = "";
        public DocBlock ToList(string label) => new IfBlock
        {
            Condition = "has:" + Related["rel:".Length..],
            Blocks = [T("**" + label + "**"), new ElementListBlock { Query = "", Related = Related, Format = "{name}" }],
        };
    }
}
