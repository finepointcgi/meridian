using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;
using Meridian.Serialization.Mermaid;

namespace Meridian.Tests;

public class MermaidTests
{
    private static (ModelRepository M, MermaidImportResult R) Import(string text, MermaidImportOptions? options = null)
    {
        var m = new ModelRepository("Test");
        var r = MermaidImporter.Import(m, m.Root, text, options);
        return (m, r);
    }

    private static List<Element> Owned(Element owner, ElementKind kind) => owner.Descendants().Where(e => e.Kind == kind).ToList();

    private static Element Named(ModelRepository m, string name, ElementKind? kind = null) =>
        m.AllElements.Single(e => e.Name == name && e is not Diagram && (kind == null || e.Kind == kind));

    // ================================================================== flowchart

    private const string OrderFlow = """
        ---
        title: Order handling
        config:
          theme: forest
        ---
        %%{init: {"flowchart": {"curve": "basis"}}}%%
        flowchart TD
            %% the happy path first
            start((Start)) --> receive[Receive order]
            receive --> check{"Stock<br>available?"}
            check -->|yes| pick([Pick items]) ==> ship[[Ship parcel]]
            check -- no --> back[Back-order item] -.-> notify>Notify customer]
            back --> db[(Orders DB)]
            ship & notify --> done(((End)))
            receive --> café["Café façade ✓"]
        """;

    [Fact]
    public void Flowchart_Becomes_An_Activity_Diagram_With_Decisions_Guards_And_Initial_Final_Nodes()
    {
        var (m, r) = Import(OrderFlow);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.Activity, d.DiagramType);
        Assert.Equal("Order handling", d.Name);
        var act = Assert.IsType<Element>(d.Owner);
        Assert.Equal(ElementKind.Activity, act.Kind);
        Assert.Equal("Order handling", act.Name);

        Assert.Single(Owned(act, ElementKind.InitialNode));
        Assert.Single(Owned(act, ElementKind.ActivityFinalNode));
        var decision = Assert.Single(Owned(act, ElementKind.DecisionNode));
        Assert.Equal("Stock available?", decision.Name);   // <br> folded into one line
        Assert.Single(Owned(act, ElementKind.ObjectNode), o => o.Name == "Orders DB");
        Assert.Contains(Owned(act, ElementKind.Action), a => a.Name == "Café façade ✓");
        Assert.Equal(["Back-order item", "Café façade ✓", "Notify customer", "Pick items", "Receive order", "Ship parcel"],
            Owned(act, ElementKind.Action).Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal));

        var flows = m.Relationships.Where(x => x.Kind is ElementKind.ControlFlow or ElementKind.ObjectFlow).ToList();
        Assert.Equal(10, flows.Count);
        Assert.Contains(flows, f => f.Source == decision && f.Get(FieldKeys.Guard) == "yes");
        Assert.Contains(flows, f => f.Source == decision && f.Get(FieldKeys.Guard) == "no");
        Assert.Single(flows, f => f.Kind == ElementKind.ObjectFlow);
        Assert.Equal(10, d.Nodes.Count);
        Assert.Equal(10, d.Edges.Count);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Flowchart_Subgraphs_Become_Swimlanes_Holding_Their_Nodes()
    {
        var (m, r) = Import("""
            graph LR
              subgraph customer [Customer]
                a[Place order] --> b[Pay]
              end
              subgraph shop["Web shop"]
                c[Confirm] --> d[Ship]
              end
              b --> c
              d --> e((End))
            """);
        var d = Assert.Single(r.Diagrams);
        var lanes = d.Nodes.Where(n => n.Element.Kind == ElementKind.Partition).ToList();
        Assert.Equal(["Customer", "Web shop"], lanes.Select(l => l.Element.Name));
        Assert.Equal(["Place order", "Pay"], d.Nodes.Where(n => n.ParentId == lanes[0].Id).Select(n => n.Element.Name));
        Assert.Equal(["Confirm", "Ship"], d.Nodes.Where(n => n.ParentId == lanes[1].Id).Select(n => n.Element.Name));
        foreach (var lane in lanes)
            foreach (var n in d.Nodes.Where(n => n.ParentId == lane.Id)) Assert.True(lane.Bounds.Contains(n.Bounds.Center));
        Assert.Equal(4, d.Edges.Count);
        Assert.Single(m.OfKind(ElementKind.ActivityFinalNode));
        Assert.Contains(r.Warnings, w => w.Contains("top to bottom"));   // LR with lanes
    }

    [Fact]
    public void Flowchart_Statements_On_One_Line_And_Links_To_Subgraphs_Are_Read()
    {
        var (m, r) = Import("graph TD; A-->B; B---C; C~~~D\nsubgraph G\n  X --> Y\nend\nA --> G");
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(6, d.Nodes.Count(n => n.Element.Kind != ElementKind.Partition));
        Assert.Contains(m.Relationships, x => x.Source.Name == "A" && x.Target.Name == "X");   // A --> G goes to G's first node
        Assert.Contains(r.Warnings, w => w.Contains("invisible"));
    }

    // ================================================================== class diagram

    private const string Fleet = """
        classDiagram
            direction LR
            accTitle: Fleet structure
            namespace Vehicles {
                class Vehicle {
                    <<abstract>>
                    +String name
                    -mass : Real
                    +int wheels = 4
                    +start(speed: Real) bool
                    +stop()$
                }
                class Truck
            }
            class Engine {
                +power : Real
            }
            class Drivable {
                <<interface>>
                +drive() void
            }
            class Fuel {
                <<enumeration>>
                Petrol
                Diesel
                Electric
            }
            class Box~T~
            Vehicle <|-- Truck
            Vehicle ..|> Drivable
            Vehicle "1" *-- "1..2" Engine : engine
            Vehicle o-- Wheel
            Truck "1" --> "*" Box : cargo
            Engine ..> Fuel : burns
            note for Engine "Internal combustion<br>or electric"
            classDef hot fill:#f00
        """;

    [Fact]
    public void Class_Diagram_Becomes_A_Bdd_With_Blocks_Members_And_Relationships()
    {
        var (m, r) = Import(Fleet);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.BlockDefinition, d.DiagramType);
        Assert.Equal("Fleet structure", d.Name);
        var pkg = Named(m, "Vehicles", ElementKind.Package);
        var vehicle = Named(m, "Vehicle", ElementKind.Block);
        Assert.Same(pkg, vehicle.Owner);
        Assert.Same(pkg, Named(m, "Truck").Owner);
        Assert.True(vehicle.IsAbstract);
        Assert.Equal(ElementKind.InterfaceBlock, Named(m, "Drivable").Kind);
        var fuel = Named(m, "Fuel");
        Assert.Equal(ElementKind.Enumeration, fuel.Kind);
        Assert.Equal(["Petrol", "Diesel", "Electric"], fuel.OwnedElements.Select(x => x.Name));
        Assert.Equal("Box<T>", Named(m, "Box<T>").Name);

        var props = vehicle.OwnedElements.Where(x => x.Kind == ElementKind.Property).ToDictionary(x => x.Name);
        Assert.Equal("String", props["name"].TypeRef);
        Assert.Equal("Real", props["mass"].TypeRef);
        Assert.Equal("private", props["mass"].Get(FieldKeys.Visibility));
        Assert.Equal("Integer", props["wheels"].TypeRef);
        Assert.Equal("4", props["wheels"].Get(FieldKeys.DefaultValue));
        var engine = Named(m, "Engine");
        Assert.Equal(engine.Id, props["engine"].TypeRef);          // part property from the composition
        Assert.Equal("1..2", props["engine"].Multiplicity);
        var start = vehicle.OwnedElements.Single(x => x.Name == "start");
        Assert.Equal("(speed: Real) : bool", start.Get(FieldKeys.Signature));

        Assert.Single(m.Relationships, x => x.Kind == ElementKind.Generalization && x.Source.Name == "Truck" && x.Target == vehicle);
        Assert.Single(m.Relationships, x => x.Kind == ElementKind.Realization && x.Source == vehicle);
        Assert.Single(m.Relationships, x => x.Kind == ElementKind.Composition && x.Source == vehicle && x.Target == engine && x.Multiplicity == "1..2");
        Assert.Single(m.Relationships, x => x.Kind == ElementKind.Aggregation && x.Target.Name == "Wheel");
        Assert.Single(m.Relationships, x => x.Kind == ElementKind.Association && x.Name == "cargo" && x.Multiplicity == "*");
        Assert.Single(m.Relationships, x => x.Kind == ElementKind.Dependency && x.Name == "burns");
        Assert.Single(m.OfKind(ElementKind.Comment), c => c.Get(FieldKeys.Body) == "Internal combustion\nor electric");
        Assert.Contains(r.Warnings, w => w.Contains("styling"));
        Assert.Equal(7, d.Nodes.Count(n => n.Element.Kind != ElementKind.Comment));
        Assert.Equal(6, d.Edges.Count);
    }

    // ================================================================== state diagram

    [Fact]
    public void State_Diagram_Becomes_A_State_Machine_With_Composite_States_And_Labelled_Transitions()
    {
        var (m, r) = Import("""
            stateDiagram-v2
                [*] --> Off
                Off --> On : powerOn [battery > 10] / beep()
                state "Running normally" as On {
                    [*] --> Idle
                    Idle --> Busy : job
                    Busy --> Idle
                    --
                    Logging
                }
                On --> Off : powerOff
                state check <<choice>>
                Off --> check
                check --> [*]
                On : entry / lamp = 1
                On : exit / lamp = 0
                note right of Off : Default state
                note left of On
                    Two regions
                    in Mermaid
                end note
            """);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.StateMachine, d.DiagramType);
        Assert.Equal("State diagram 1", d.Name);
        var sm = d.Owner!;
        Assert.Equal(ElementKind.StateMachine, sm.Kind);
        var on = Named(m, "Running normally", ElementKind.State);
        Assert.Equal("lamp = 1", on.Get(FieldKeys.Entry));
        Assert.Equal("lamp = 0", on.Get(FieldKeys.Exit));
        Assert.Equal(["Idle", "Busy", "Logging"], on.OwnedElements.Where(x => x.Kind == ElementKind.State).Select(x => x.Name));
        Assert.Single(on.OwnedElements, x => x.Kind == ElementKind.InitialState);
        Assert.Equal(2, m.OfKind(ElementKind.InitialState).Count());
        Assert.Single(m.OfKind(ElementKind.FinalState));
        var t = m.Relationships.Single(x => x.Kind == ElementKind.Transition && x.Name == "powerOn");
        Assert.Equal("battery > 10", t.Get(FieldKeys.Guard));
        Assert.Equal("beep()", t.Get(FieldKeys.Effect));
        Assert.Equal(8, m.Relationships.Count(x => x.Kind == ElementKind.Transition));
        Assert.Equal(2, m.OfKind(ElementKind.Comment).Count());
        Assert.Contains(r.Warnings, w => w.Contains("choice"));
        Assert.Contains(r.Warnings, w => w.Contains("regions"));
        var onNode = d.NodeFor(on)!;
        Assert.All(d.Nodes.Where(n => n.Element.Owner == on), n => Assert.Equal(onNode.Id, n.ParentId));
    }

    // ================================================================== sequence diagram

    [Fact]
    public void Sequence_Diagram_Becomes_An_Interaction_With_Lifelines_Messages_Fragments_And_Notes()
    {
        var (m, r) = Import("""
            sequenceDiagram
                title: Checkout
                autonumber
                actor U as Shopper
                participant W as Web shop
                participant P as Payments
                U->>+W: checkout()
                W->>P: charge(card)
                alt card accepted
                    P-->>W: ok
                else declined
                    P-->>W: error
                end
                loop every 5 s
                    W-)P: poll
                end
                Note over W,P: PCI zone
                W-->>-U: receipt
                U->>U: think
            """);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.Sequence, d.DiagramType);
        Assert.Equal("Checkout", d.Name);
        var lifelines = d.Nodes.Where(n => n.Element.Kind == ElementKind.Lifeline).OrderBy(n => n.Bounds.X).Select(n => n.Element.Name).ToList();
        Assert.Equal(["Shopper", "Web shop", "Payments"], lifelines);
        var messages = d.Edges.Where(e => e.Relationship.Kind == ElementKind.Message).OrderBy(e => Notation.MessageY(e)).Select(e => e.Relationship).ToList();
        Assert.Equal(["checkout()", "charge(card)", "ok", "error", "poll", "receipt", "think"], messages.Select(x => x.Name));
        Assert.Equal(["synchCall", "synchCall", "reply", "reply", "asynchCall", "reply", "synchCall"], messages.Select(x => x.Get(FieldKeys.MessageSort)));
        var fragments = d.Nodes.Where(n => n.Element.Kind == ElementKind.CombinedFragment).ToList();
        Assert.Equal(2, fragments.Count);
        var alt = fragments.Single(f => f.Element.Get(FieldKeys.Operator) == "alt");
        Assert.Equal("card accepted | declined", alt.Element.Get(FieldKeys.Guard));
        var okEdge = d.Edges.Single(e => e.Relationship.Name == "ok");
        Assert.InRange(Notation.MessageY(okEdge), alt.Bounds.Y, alt.Bounds.Bottom);
        Assert.Single(d.Nodes, n => n.Element.Kind == ElementKind.Comment && n.Element.Get(FieldKeys.Body) == "PCI zone");
        Assert.Contains(r.Warnings, w => w.Contains("activation"));
        Assert.Contains(r.Warnings, w => w.Contains("operands"));
    }

    // ================================================================== requirement diagram

    [Fact]
    public void Requirement_Diagram_Maps_Fields_And_Every_Relationship()
    {
        var (m, r) = Import("""
            requirementDiagram
                requirement brakes {
                    id: SYS-1
                    text: "The vehicle shall stop from 100 km/h within 40 m."
                    risk: High
                    verifymethod: Test
                }
                performanceRequirement "abs response" {
                    id: SYS-1.1
                    text: ABS reacts within 20 ms
                    risk: medium
                    verifymethod: analysis
                }
                functionalRequirement warn {
                    id: SYS-2
                    text: warn the driver
                }
                requirement warnCopy {
                    id: SYS-3
                    text: copy
                }
                element brakeSystem {
                    type: block
                    docref: specs/brakes.pdf
                }
                element brakeTest {
                    type: "test case"
                }
                brakeSystem - satisfies -> brakes
                brakeTest - verifies -> brakes
                brakes - contains -> "abs response"
                "abs response" - derives -> warn
                warn - refines -> brakes
                warnCopy - copies -> warn
                brakeSystem - traces -> warn
                brakes <- satisfies - brakeSystem
            """);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.Requirement, d.DiagramType);
        var brakes = Named(m, "brakes", ElementKind.Requirement);
        Assert.Equal("SYS-1", brakes.Get(FieldKeys.RequirementId));
        Assert.Equal("The vehicle shall stop from 100 km/h within 40 m.", brakes.Get(FieldKeys.Text));
        Assert.Equal("high", brakes.Get(ReqFields.Risk));
        Assert.Equal("test", brakes.Get(ReqFields.VerificationMethod));
        var abs = Named(m, "abs response");
        Assert.Same(brakes, abs.Owner);   // contains
        Assert.Equal("performanceRequirement", abs.Get(ReqFields.CustomPrefix + "Type"));
        Assert.Equal(ElementKind.Block, Named(m, "brakeSystem").Kind);
        Assert.Contains("docref: specs/brakes.pdf", Named(m, "brakeSystem").Documentation);
        Assert.Equal(ElementKind.TestCase, Named(m, "brakeTest").Kind);
        var kinds = m.Relationships.Select(x => x.Kind).OrderBy(k => k).ToList();
        Assert.Equal([ElementKind.Satisfy, ElementKind.Verify, ElementKind.Refine, ElementKind.Trace, ElementKind.DeriveReqt, ElementKind.Copy], kinds);   // the duplicate satisfy is the same link
        Assert.Equal(6, d.Nodes.Count);
    }

    // ================================================================== ER and C4

    [Fact]
    public void Er_Diagram_Becomes_Blocks_With_Attributes_And_Associations()
    {
        var (m, r) = Import("""
            erDiagram
                CUSTOMER ||--o{ ORDER : places
                ORDER ||--|{ LINE-ITEM : contains
                CUSTOMER }|..|{ DELIVERY-ADDRESS : uses
                CUSTOMER {
                    string name PK "full name"
                    int age
                }
                p[Person] {
                    string id
                }
            """);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.BlockDefinition, d.DiagramType);
        var customer = Named(m, "CUSTOMER", ElementKind.Block);
        var name = customer.OwnedElements.Single(x => x.Name == "name");
        Assert.Equal("String", name.TypeRef);
        Assert.Equal("PK — full name", name.Documentation);
        Assert.Equal("Integer", customer.OwnedElements.Single(x => x.Name == "age").TypeRef);
        Assert.Equal("Person", Named(m, "Person").Name);
        var places = m.Relationships.Single(x => x.Name == "places");
        Assert.Equal(ElementKind.Association, places.Kind);
        Assert.Equal("*", places.Multiplicity);
        Assert.Equal("1..*", m.Relationships.Single(x => x.Name == "contains").Multiplicity);
        Assert.Equal(3, d.Edges.Count);
    }

    [Fact]
    public void C4_Context_Becomes_Actors_Blocks_And_Dependencies()
    {
        var (m, r) = Import("""
            C4Context
                title System context
                Person(customer, "Customer", "A bank customer")
                Enterprise_Boundary(b0, "Bank") {
                    System(banking, "Internet Banking", "Lets customers view accounts")
                    SystemDb(mainframe, "Mainframe")
                }
                System_Ext(mail, "E-mail system")
                Rel(customer, banking, "Uses")
                Rel(banking, mainframe, "Reads", "XML/HTTPS")
                Rel_Back(customer, mail, "Sends e-mails to")
                UpdateRelStyle(customer, banking, $textColor="red")
            """);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal("System context", d.Name);
        Assert.Equal(ElementKind.Actor, Named(m, "Customer").Kind);
        Assert.Equal("Lets customers view accounts", Named(m, "Internet Banking").Documentation);
        Assert.Equal(3, m.Relationships.Count(x => x.Kind == ElementKind.Dependency));
        Assert.Single(m.Relationships, x => x.Name == "Sends e-mails to" && x.Source.Name == "E-mail system");
        Assert.Contains(r.Warnings, w => w.Contains("boundaries"));
    }

    [Fact]
    public void Unsupported_Diagram_Types_Are_Skipped_With_A_Warning()
    {
        var (m, r) = Import("""
            pie title Pets
                "Dogs" : 386
                "Cats" : 85
            gantt
                title A plan
                section A
                Task :a1, 2024-01-01, 30d
            mindmap
              root((mindmap))
            flowchart LR
              A --> B
            """);
        Assert.Single(r.Diagrams);
        Assert.Equal(3, r.Warnings.Count(w => w.Contains("no SysML equivalent")));
        var p = MermaidImporter.Preview("pie\n \"a\" : 1\n");
        Assert.Equal(0, p.Supported);
        Assert.Equal("Skipped (not supported)", p.Diagrams[0].Becomes);
        Assert.DoesNotContain(m.Root.OwnedElements, e => e.Kind == ElementKind.Package);
    }

    // ================================================================== documents

    [Fact]
    public void Markdown_File_With_Several_Mermaid_Blocks_Imports_Each_As_A_Diagram()
    {
        var md = """
            # Design notes

            Some prose with `inline code` and a fake fence:

            ```text
            ```mermaid
            graph TD
              Z --> Y
            ```

            ```mermaid
            ---
            title: Login flow
            ---
            flowchart LR
              a[Enter password] --> b{OK?}
            ```

            ~~~mermaid
            sequenceDiagram
              Alice->>Bob: Hi
            ~~~

            ```Mermaid
            classDiagram
              class Account
            ```
            """;
        var preview = MermaidImporter.Preview(md, markdown: true);
        Assert.Equal(3, preview.Diagrams.Count);
        Assert.Equal(["Login flow", "", ""], preview.Diagrams.Select(x => x.Title));
        Assert.Equal("2 nodes · 1 flow", preview.Diagrams[0].Counts);
        var (m, r) = Import(md, new MermaidImportOptions { Markdown = true });
        Assert.Equal(["Login flow", "Sequence diagram 1", "Class diagram 1"], r.Diagrams.Select(d => d.Name));
        Assert.DoesNotContain(m.AllElements, e => e.Name is "Z" or "Y");
    }

    [Fact]
    public void Several_Diagrams_In_One_File_And_Default_Names_Are_Unique()
    {
        var text = "flowchart TD\n A --> B\n\n---\ntitle: Second\n---\ngraph LR\n C --> D\nflowchart TD\n E --> F\n";
        var m = new ModelRepository();
        var first = MermaidImporter.Import(m, m.Root, text);
        Assert.Equal(["Flowchart 1", "Second", "Flowchart 2"], first.Diagrams.Select(d => d.Name));
        var again = MermaidImporter.Import(m, m.Root, "flowchart TD\n X --> Y\n");
        Assert.Equal("Flowchart 3", Assert.Single(again.Diagrams).Name);
    }

    [Fact]
    public void Import_Is_One_Undo_Step()
    {
        var m = SampleModels.Vehicle();
        var before = m.AllElements.Count();
        var r = MermaidImporter.Import(m, m.Root, Fleet + "\n" + OrderFlow);
        Assert.Equal(2, r.Diagrams.Count);
        Assert.True(r.Elements > 10);
        Assert.Equal("Import Mermaid", m.History.UndoDescription);
        m.History.Undo();
        Assert.Equal(before, m.AllElements.Count());
    }

    [Fact]
    public void Match_By_Name_Reuses_Existing_Blocks_And_Requirements()
    {
        var m = SampleModels.Vehicle();
        var engine = m.OfKind(ElementKind.Block).Single(b => b.Name == "Engine");
        var vehicle = m.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle");
        var compositions = m.Relationships.Count(x => x.Kind == ElementKind.Composition);
        var text = "classDiagram\n Vehicle *-- Engine\n class Engine {\n +rpm : Real\n }\n";
        var r = MermaidImporter.Import(m, m.Root, text, new MermaidImportOptions { Match = MermaidMatchMode.MatchByName });
        Assert.Single(m.OfKind(ElementKind.Block), b => b.Name == "Engine");
        Assert.Contains(engine.OwnedElements, p => p.Name == "rpm");
        Assert.Equal(compositions, m.Relationships.Count(x => x.Kind == ElementKind.Composition));   // the existing composition is reused
        Assert.True(r.Diagrams[0].Shows(vehicle));

        var created = MermaidImporter.Import(m, m.Root, text);   // create new
        Assert.Equal(2, m.OfKind(ElementKind.Block).Count(b => b.Name == "Engine"));
        Assert.Equal(2, created.Diagrams[0].Nodes.Count);
    }

    [Fact]
    public void Import_As_New_Project_Builds_A_Model()
    {
        var (model, report) = MermaidImporter.ImportAsNewProject(OrderFlow, "Orders");
        Assert.Equal("Orders", model.Root.Name);
        Assert.Single(model.Diagrams);
        Assert.Equal(11, report.Elements);   // the activity and its 10 nodes
        Assert.False(model.History.CanUndo);
        Assert.NotNull(ProjectFile.Deserialize(ProjectFile.Serialize(model)));
    }

    // ================================================================== robustness

    public static TheoryData<string> Malformed => new()
    {
        "",
        "   \n\n",
        "hello world",
        "flowchart",
        "flowchart TD\n A -->",
        "flowchart TD\n --> B",
        "flowchart TD\n A[unclosed --> B\n C{{{ --> ]]]",
        "flowchart TD\n subgraph\n subgraph\n end end end end",
        "flowchart TD\n A -- text without end B",
        "flowchart TD\n A@{ shape: ",
        "classDiagram\n class {\n A <|-- \n \"1\" *-- \n class B {\n",
        "classDiagram\n A : +foo(\n B : <<\n <<interface>>",
        "stateDiagram-v2\n state \n [*] --> \n --> [*]\n }}}\n state X {\n",
        "stateDiagram\n note right of\n note left of A\n never closed",
        "sequenceDiagram\n A->>\n ->>B: x\n end\n else\n alt\n loop\n Note over : x\n participant",
        "requirementDiagram\n requirement {\n id\n }\n a - bogus -> b\n element x",
        "erDiagram\n A ||--o{ \n { \n B {\n",
        "C4Context\n Person(\n Rel(a)\n System_Boundary(x) {\n",
        "---\ntitle: never closed\nflowchart TD\n A --> B",
        "```mermaid\nflowchart TD\n A --> B",
        "%%{init: {\n broken\nflowchart TD\n A --> B",
        "\u0000\u0001\u0002flowchart TD\n \u0000 --> \u0001",
        "flowchart TD\n A[\"#9999999999; #-1; #quot\"] --> B[\"\ud800\"]",
        "graph TD\n" + string.Concat(Enumerable.Repeat("A-->", 2000)) + "B",
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Malformed_Input_Never_Throws_And_Reports_Problems(string text)
    {
        var preview = MermaidImporter.Preview(text);
        Assert.NotNull(preview);
        var m = new ModelRepository();
        var r = MermaidImporter.Import(m, m.Root, text);
        Assert.NotNull(r);
        if (r.Diagrams.Count == 0) Assert.NotEmpty(r.Warnings);
        Assert.NotNull(ProjectFile.Serialize(m));
    }

    [Fact]
    public void Random_Input_Never_Throws()
    {
        var rnd = new Random(42);
        const string alphabet = "ABC ab-->|[]{}()<>=.~&;:\"'%#*o x\n+-$@!?/\\";
        var headers = new[] { "flowchart TD\n", "classDiagram\n", "stateDiagram-v2\n", "sequenceDiagram\n", "requirementDiagram\n", "erDiagram\n", "C4Context\n", "" };
        for (var i = 0; i < 300; i++)
        {
            var len = rnd.Next(0, 400);
            var chars = new char[len];
            for (var j = 0; j < len; j++) chars[j] = alphabet[rnd.Next(alphabet.Length)];
            var text = headers[i % headers.Length] + new string(chars);
            var m = new ModelRepository();
            MermaidImporter.Import(m, m.Root, text);
        }
    }

    [Fact]
    public void Limits_Cut_Off_Huge_Input_Deep_Nesting_And_Too_Many_Nodes()
    {
        var big = "flowchart TD\n" + new string('x', MermaidLimits.MaxInputChars + 10);
        Assert.Contains(MermaidImporter.Preview(big).Warnings, w => w.Contains("larger than"));
        Assert.Contains(MermaidImporter.Preview(big).Warnings, w => w.Contains("longer than"));

        var nodes = "flowchart TD\n" + string.Join("\n", Enumerable.Range(0, MermaidLimits.MaxNodes + 50).Select(i => $"n{i}"));
        var p = MermaidImporter.Preview(nodes);
        Assert.Equal(MermaidLimits.MaxNodes, p.Diagrams[0].Nodes);
        Assert.Contains(p.Warnings, w => w.Contains($"more than {MermaidLimits.MaxNodes} nodes"));

        var deep = "flowchart TD\n" + string.Concat(Enumerable.Range(0, MermaidLimits.MaxDepth + 10).Select(i => $"subgraph s{i}\n")) + "a --> b\n"
                   + string.Concat(Enumerable.Repeat("end\n", MermaidLimits.MaxDepth + 10));
        var (_, r) = Import(deep);
        Assert.Contains(r.Warnings, w => w.Contains("nested deeper"));
        Assert.Single(r.Diagrams);

        var manyDiagrams = string.Concat(Enumerable.Range(0, MermaidLimits.MaxDiagrams + 5).Select(i => $"flowchart TD\n a{i}\n"));
        var pd = MermaidImporter.Preview(manyDiagrams);
        Assert.Equal(MermaidLimits.MaxDiagrams, pd.Diagrams.Count);
        Assert.Contains(pd.Warnings, w => w.Contains("Only the first"));

        var deepState = "stateDiagram-v2\n" + string.Concat(Enumerable.Range(0, MermaidLimits.MaxDepth + 5).Select(i => $"state S{i} {{\n")) + "[*] --> X\n"
                        + string.Concat(Enumerable.Repeat("}\n", MermaidLimits.MaxDepth + 5));
        var (_, rs) = Import(deepState);
        Assert.Contains(rs.Warnings, w => w.Contains("nested deeper"));
    }

    // ================================================================== layout direction

    [Theory]
    [InlineData("TD")]
    [InlineData("LR")]
    [InlineData("BT")]
    [InlineData("RL")]
    public void Layout_Follows_The_Mermaid_Direction(string direction)
    {
        var (_, r) = Import($"flowchart {direction}\n A[First] --> B[Second] --> C[Third]");
        var d = r.Diagrams[0];
        var c = new[] { "First", "Second", "Third" }.Select(n => d.Nodes.Single(x => x.Element.Name == n).Bounds.Center).ToList();
        switch (direction)
        {
            case "TD": Assert.True(c[0].Y < c[1].Y && c[1].Y < c[2].Y); Assert.Equal(c[0].X, c[2].X, 1); break;
            case "BT": Assert.True(c[0].Y > c[1].Y && c[1].Y > c[2].Y); break;
            case "LR": Assert.True(c[0].X < c[1].X && c[1].X < c[2].X); Assert.Equal(c[0].Y, c[2].Y, 1); break;
            case "RL": Assert.True(c[0].X > c[1].X && c[1].X > c[2].X); break;
        }
        foreach (var a in d.Nodes) foreach (var b in d.Nodes.Where(b => b != a)) Assert.False(a.Bounds.Intersects(b.Bounds), "shapes overlap");
        Assert.Equal("orthogonal", d.Get(EdgeRouter.DiagramKey));
    }

    [Fact]
    public void Class_Diagram_Direction_Statement_Lays_Out_Left_To_Right()
    {
        var (_, r) = Import("classDiagram\n direction LR\n A <|-- B\n B <|-- C");
        var d = r.Diagrams[0];
        double X(string n) => d.Nodes.Single(x => x.Element.Name == n).Bounds.X;
        Assert.True(X("A") < X("B") && X("B") < X("C"));
    }

    // ================================================================== export and round trips

    private static void AssertSameContent(ModelRepository a, ModelRepository b, params ElementKind[] ignore)
    {
        string Key(Element e) => $"{e.Kind}:{e.Name}";
        var ea = a.AllElements.Where(e => e is not Relationship && e is not Diagram && e.Kind != ElementKind.Model && !ignore.Contains(e.Kind)).Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var eb = b.AllElements.Where(e => e is not Relationship && e is not Diagram && e.Kind != ElementKind.Model && !ignore.Contains(e.Kind)).Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(ea, eb);
        string RKey(Relationship r) => $"{r.Kind}:{r.Source.Name}->{r.Target.Name}:{r.Name}:{r.Get(FieldKeys.Guard)}";
        Assert.Equal(a.Relationships.Select(RKey).OrderBy(x => x, StringComparer.Ordinal), b.Relationships.Select(RKey).OrderBy(x => x, StringComparer.Ordinal));
    }

    private static ModelRepository RoundTrip(ModelRepository m, Diagram d, out string text)
    {
        Assert.True(MermaidExporter.CanExport(d));
        text = MermaidExporter.Export(m, d);
        var copy = new ModelRepository("Copy");
        var r = MermaidImporter.Import(copy, copy.Root, text);
        Assert.Single(r.Diagrams);
        Assert.Equal(d.Name, r.Diagrams[0].Name);
        return copy;
    }

    [Fact]
    public void Flowchart_Round_Trip_Keeps_Elements_And_Flows()
    {
        var (m, r) = Import(OrderFlow);
        var copy = RoundTrip(m, r.Diagrams[0], out var text);
        Assert.StartsWith("---\ntitle: \"Order handling\"", text);
        AssertSameContent(m, copy);
    }

    [Fact]
    public void Swimlane_Round_Trip_Keeps_Partitions()
    {
        var (m, r) = Import("flowchart TD\nsubgraph L1 [Lane one]\n a[A] --> b{B}\nend\nsubgraph L2 [Lane two]\n c[C]\nend\nb -->|go| c\nb -->|stop| e(((E)))");
        var copy = RoundTrip(m, r.Diagrams[0], out _);
        AssertSameContent(m, copy);
        Assert.Equal(2, copy.Diagrams.Single().Nodes.Count(n => n.Element.Kind == ElementKind.Partition));
    }

    [Fact]
    public void Class_Diagram_Round_Trip_Keeps_Blocks_Members_And_Relationships()
    {
        var (m, r) = Import(Fleet);
        var copy = RoundTrip(m, r.Diagrams[0], out var text);
        Assert.Contains("<<interface>>", text);
        AssertSameContent(m, copy, ElementKind.Package, ElementKind.Comment);
        var v = copy.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle");
        Assert.True(v.IsAbstract);
        Assert.Equal("(speed: Real) : bool", v.OwnedElements.Single(o => o.Name == "start").Get(FieldKeys.Signature));
    }

    [Fact]
    public void State_Round_Trip_Keeps_States_Nesting_And_Transitions()
    {
        var (m, r) = Import("stateDiagram-v2\n [*] --> Off\n Off --> On : on [ok] / go(); log(1)\n state On {\n [*] --> Idle\n Idle --> Busy\n Busy --> [*]\n }\n On --> Off : off\n Off : entry / reset; count = 0");
        var copy = RoundTrip(m, r.Diagrams[0], out _);
        AssertSameContent(m, copy);
        var on = copy.OfKind(ElementKind.State).Single(s => s.Name == "On");
        Assert.Equal(4, on.OwnedElements.Count(e => Metamodel.IsVertex(e.Kind)));
        Assert.Equal("reset; count = 0", copy.OfKind(ElementKind.State).Single(s => s.Name == "Off").Get(FieldKeys.Entry));
        Assert.Equal("go(); log(1)", copy.Relationships.Single(t => t.Name == "on").Get(FieldKeys.Effect));
    }

    [Fact]
    public void Sequence_Round_Trip_Keeps_Lifelines_Messages_Order_And_Fragments()
    {
        var (m, r) = Import("sequenceDiagram\n participant A as Client app\n participant B\n A->>B: request\n loop retry\n B-->>A: wait\n end\n B-)A: done\n Note over A: hello");
        var copy = RoundTrip(m, r.Diagrams[0], out _);
        AssertSameContent(m, copy);
        var d = copy.Diagrams.Single();
        Assert.Equal(["request", "wait", "done"], d.Edges.OrderBy(e => Notation.MessageY(e)).Select(e => e.Relationship.Name));
        Assert.Equal("loop", d.Nodes.Single(n => n.Element.Kind == ElementKind.CombinedFragment).Element.Get(FieldKeys.Operator));
    }

    [Fact]
    public void Requirement_Round_Trip_Keeps_Requirements_Fields_And_Links()
    {
        var (m, r) = Import("requirementDiagram\nrequirement \"top level\" {\n id: R1\n text: \"Do: it; well\"\n risk: low\n verifymethod: inspection\n}\ninterfaceRequirement child {\n id: R1.1\n text: sub\n}\nelement thing {\n type: block\n}\nelement check {\n type: test\n}\n\"top level\" - contains -> child\nthing - satisfies -> child\ncheck - verifies -> \"top level\"\nchild - traces -> \"top level\"");
        var copy = RoundTrip(m, r.Diagrams[0], out _);
        AssertSameContent(m, copy);
        var top = copy.OfKind(ElementKind.Requirement).Single(x => x.Name == "top level");
        Assert.Equal("Do: it; well", top.Get(FieldKeys.Text));
        Assert.Equal("low", top.Get(ReqFields.Risk));
        Assert.Equal("inspection", top.Get(ReqFields.VerificationMethod));
        var child = copy.OfKind(ElementKind.Requirement).Single(x => x.Name == "child");
        Assert.Same(top, child.Owner);
        Assert.Equal("interfaceRequirement", child.Get(ReqFields.CustomPrefix + "Type"));
    }

    [Fact]
    public void Sample_Model_Diagrams_Export_And_Reimport_With_The_Same_Elements_And_Relationships()
    {
        var m = SampleModels.Vehicle();
        var exported = m.Diagrams.Where(MermaidExporter.CanExport).ToList();
        Assert.Contains(exported, d => d.DiagramType == DiagramType.Activity);
        Assert.Contains(exported, d => d.DiagramType == DiagramType.StateMachine);
        Assert.Contains(exported, d => d.DiagramType == DiagramType.Sequence);
        Assert.Contains(exported, d => d.DiagramType == DiagramType.BlockDefinition);
        Assert.Contains(exported, d => d.DiagramType == DiagramType.Requirement);
        foreach (var d in exported)
        {
            var text = MermaidExporter.Export(m, d);
            var copy = new ModelRepository();
            var r = MermaidImporter.Import(copy, copy.Root, text);
            var nd = Assert.Single(r.Diagrams);
            // Shapes and the relationships drawn between them survive (comments and pins are not exported).
            static IEnumerable<string> Shapes(Diagram x) => x.Nodes.Where(n => n.Element.Kind is not (ElementKind.Comment or ElementKind.InputPin or ElementKind.OutputPin))
                .Select(n => $"{n.Element.Kind}:{n.Element.Name}").OrderBy(s => s, StringComparer.Ordinal);
            static IEnumerable<string> Lines(Diagram x) => x.Edges.Select(e => $"{e.Relationship.Kind}:{e.Source.Element.Name}->{e.Target.Element.Name}:{e.Relationship.Name}")
                .OrderBy(s => s, StringComparer.Ordinal);
            // Only relationships Mermaid has a notation for on that kind of diagram.
            var expectedLines = Lines(d).Where(l => d.DiagramType switch
            {
                DiagramType.BlockDefinition => !l.StartsWith("Satisfy") && !l.StartsWith("Allocate"),
                DiagramType.Requirement => !l.StartsWith("Composition") && !l.StartsWith("Association") && !l.StartsWith("Generalization"),
                _ => true,
            });
            Assert.True(Shapes(d).SequenceEqual(Shapes(nd)), $"{d.Name} shapes differ:\n{text}");
            Assert.True(expectedLines.SequenceEqual(Lines(nd)), $"{d.Name} relationships differ:\n{string.Join("\n", expectedLines)}\n---\n{string.Join("\n", Lines(nd))}\n{text}");
        }
    }

    [Fact]
    public void Other_Diagram_Types_Cannot_Be_Exported()
    {
        var m = SampleModels.Vehicle();
        var ibd = m.Diagrams.First(d => d.DiagramType == DiagramType.InternalBlock);
        Assert.False(MermaidExporter.CanExport(ibd));
        Assert.Throws<InvalidOperationException>(() => MermaidExporter.Export(m, ibd));
    }
}
