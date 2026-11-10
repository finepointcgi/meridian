using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization.Mermaid;

namespace Meridian.Tests;

/// <summary>Mermaid: operands (alt / par / critical), pseudostates, datastores and multiplicities at both ends, in and out.</summary>
public class MermaidOperandsTests
{
    private static (ModelRepository M, MermaidImportResult R) Import(string text)
    {
        var m = new ModelRepository("Imported");
        var r = MermaidImporter.Import(m, m.Root, text);
        return (m, r);
    }

    private static (ModelRepository M, MermaidImportResult R, string Text) RoundTrip(ModelRepository m, Diagram d)
    {
        var text = MermaidExporter.Export(m, d);
        var (copy, r) = Import(text);
        return (copy, r, text);
    }

    private static string OperandOf(Diagram d, string message)
    {
        var edge = d.Edges.Single(e => e.Relationship.Name == message);
        var y = Notation.MessageY(edge);
        var f = d.Nodes.Where(n => n.Element.Kind == ElementKind.CombinedFragment && y >= n.Bounds.Y && y <= n.Bounds.Bottom).OrderBy(n => n.Bounds.Height).First();
        var band = InteractionOperands.At(f, f.Bounds, y);
        return $"{f.Element.Get(FieldKeys.Operator)}:{band.Operand?.Get(FieldKeys.Guard) ?? f.Element.Get(FieldKeys.Guard)}";
    }

    private const string Sequence = """
        sequenceDiagram
            participant C as Client
            participant S as Server
            participant D as Db
            C->>S: login
            alt valid password
                S->>D: load
                D-->>S: profile
            else locked
                S-->>C: locked
            else
                S-->>C: denied
            end
            par
                S-)D: audit
            and notify
                S-)C: welcome
            end
            critical connect
                S->>D: open
            option timeout
                S->>S: retry
            end
        """;

    [Fact]
    public void Alt_Par_And_Critical_Become_Fragments_With_One_Operand_Per_Branch()
    {
        var (m, r) = Import(Sequence);
        var d = Assert.Single(r.Diagrams);
        var frags = m.OfKind(ElementKind.CombinedFragment).ToDictionary(f => f.Get(FieldKeys.Operator));
        Assert.Equal(["valid password", "locked", "else"], InteractionOperands.Guards(frags["alt"]));
        Assert.Equal(["", "notify"], InteractionOperands.Guards(frags["par"]));
        Assert.Equal(["connect", "timeout"], InteractionOperands.Guards(frags["critical"]));
        Assert.Equal("alt:valid password", OperandOf(d, "load"));
        Assert.Equal("alt:valid password", OperandOf(d, "profile"));
        Assert.Equal("alt:locked", OperandOf(d, "locked"));
        Assert.Equal("alt:else", OperandOf(d, "denied"));
        Assert.Equal("par:", OperandOf(d, "audit"));
        Assert.Equal("par:notify", OperandOf(d, "welcome"));
        Assert.Equal("critical:timeout", OperandOf(d, "retry"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("operand"));
    }

    [Fact]
    public void Operands_Round_Trip_Through_Export_With_Their_Separators_In_Place()
    {
        var (m, r) = Import(Sequence);
        var (copy, r2, text) = RoundTrip(m, r.Diagrams[0]);
        Assert.Contains("    else locked\n", text);
        Assert.Contains("    else\n", text);
        Assert.Contains("    and notify\n", text);
        Assert.Contains("    option timeout\n", text);
        var d = r2.Diagrams.Single();
        foreach (var msg in new[] { "load", "profile", "locked", "denied", "audit", "welcome", "open", "retry" })
            Assert.Equal(OperandOf(r.Diagrams[0], msg), OperandOf(d, msg));
        Assert.Equal(m.OfKind(ElementKind.InteractionOperand).Select(o => o.Get(FieldKeys.Guard)), copy.OfKind(ElementKind.InteractionOperand).Select(o => o.Get(FieldKeys.Guard)));
    }

    [Fact]
    public void A_Separator_Moved_On_The_Diagram_Moves_Messages_Between_Operands_In_The_Export()
    {
        var (m, r) = Import("sequenceDiagram\n A->>B: one\n alt yes\n A->>B: two\n A->>B: three\n else no\n A->>B: four\n end");
        var d = r.Diagrams[0];
        var f = d.Nodes.Single(n => n.Element.Kind == ElementKind.CombinedFragment);
        var three = Notation.MessageY(d.Edges.Single(e => e.Relationship.Name == "three"));
        m.MoveSeparator(d, f, 1, three - 10);   // "three" now falls in the "no" operand
        var (_, r2, text) = RoundTrip(m, d);
        Assert.Equal("alt:no", OperandOf(r2.Diagrams[0], "three"));
        Assert.Equal("alt:yes", OperandOf(r2.Diagrams[0], "two"));
        Assert.True(text.IndexOf("else no", StringComparison.Ordinal) < text.IndexOf("three", StringComparison.Ordinal));
    }

    [Fact]
    public void Legacy_Merged_Guards_Are_Still_Exported_As_Separators()
    {
        var m = new ModelRepository("Old");
        var ia = m.Create(m.Root, ElementKind.Interaction, "S");
        var d = m.CreateDiagram(ia, DiagramType.Sequence, "S");
        var a = m.Create(ia, ElementKind.Lifeline, "A");
        m.AddNode(d, a, new Bounds(40, 20, 150, 300));
        var f = m.Create(ia, ElementKind.CombinedFragment);
        m.Set(f, FieldKeys.Guard, "x | y");
        m.AddNode(d, f, new Bounds(20, 100, 200, 100));
        var text = MermaidExporter.Export(m, d);
        Assert.Contains("alt x\n", text);
        Assert.Contains("else y\n", text);
    }

    [Fact]
    public void Choice_Fork_Join_And_History_Become_Pseudostates_And_Round_Trip()
    {
        var (m, r) = Import("""
            stateDiagram-v2
                state check <<choice>>
                state split <<fork>>
                state merge <<join>>
                [*] --> Idle
                Idle --> check : go
                check --> A : [ok]
                check --> B : [else]
                A --> split
                split --> C
                split --> D
                C --> merge
                D --> merge
                merge --> [*]
                state Busy {
                    state H <<history>>
                    state HH <<deephistory>>
                    [*] --> Work
                }
                B --> H
            """);
        var kinds = m.OfKind(ElementKind.Pseudostate).ToDictionary(p => p.Name, p => Pseudostates.KindOf(p));
        Assert.Equal(Pseudostates.Choice, kinds["check"]);
        Assert.Equal(Pseudostates.Fork, kinds["split"]);
        Assert.Equal(Pseudostates.Join, kinds["merge"]);
        Assert.Equal(Pseudostates.ShallowHistory, kinds["H"]);
        Assert.Equal(Pseudostates.DeepHistory, kinds["HH"]);
        Assert.Equal("Busy", m.OfKind(ElementKind.Pseudostate).Single(p => p.Name == "H").Owner!.Name);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("pseudostate"));
        Assert.Equal("ok", m.Relationships.Single(t => t.Kind == ElementKind.Transition && t.Target.Name == "A").Get(FieldKeys.Guard));

        var (copy, _, text) = RoundTrip(m, r.Diagrams[0]);
        Assert.Contains("state check <<choice>>", text);
        Assert.Contains("state HH <<deephistory>>", text);
        Assert.Equal(kinds.OrderBy(k => k.Key), copy.OfKind(ElementKind.Pseudostate).ToDictionary(p => p.Name, p => Pseudostates.KindOf(p)).OrderBy(k => k.Key));
        string Key(Relationship t) => $"{t.Source.Kind}:{t.Source.Name}->{t.Target.Kind}:{t.Target.Name}[{t.Get(FieldKeys.Guard)}]{t.Name}";
        Assert.Equal(m.Relationships.Select(Key).Order(), copy.Relationships.Select(Key).Order());
    }

    [Fact]
    public void Junctions_Export_As_Choices_And_Other_Pseudostates_As_Commented_States()
    {
        var m = new ModelRepository("J");
        var sm = m.Create(m.Root, ElementKind.StateMachine, "SM");
        var j = m.Create(sm, ElementKind.Pseudostate, "j");
        m.Set(j, FieldKeys.Subtype, Pseudostates.Junction);
        var t = m.Create(sm, ElementKind.Pseudostate, "stop");
        m.Set(t, FieldKeys.Subtype, Pseudostates.Terminate);
        m.Connect(ElementKind.Transition, j, t);
        var d = m.CreateDiagram(sm, DiagramType.StateMachine, "SM");
        m.AddNode(d, j, new Bounds(0, 0, 16, 16));
        m.AddNode(d, t, new Bounds(100, 0, 22, 22));
        var text = MermaidExporter.Export(m, d);
        Assert.Contains("state j <<choice>>", text);
        Assert.Contains("%% stop is a terminate", text);
        Assert.Contains("j --> stop", text);
    }

    [Fact]
    public void Cylinders_Are_Datastores_Horizontal_Cylinders_Central_Buffers_And_Documents_Object_Nodes()
    {
        var (m, r) = Import("flowchart LR\n a[Take order] --> db[(Orders)]\n a --> q@{ shape: h-cyl, label: \"Queue\" }\n a --> o@{ shape: doc, label: \"Order\" }\n q --> b[Ship]");
        var nodes = m.OfKind(ElementKind.ObjectNode).ToDictionary(o => o.Name, o => o.Get(FieldKeys.Subtype));
        Assert.Equal(Subtypes.DataStore, nodes["Orders"]);
        Assert.Equal(Subtypes.CentralBuffer, nodes["Queue"]);
        Assert.Equal("", nodes["Order"]);
        Assert.Equal(4, m.Relationships.Count(x => x.Kind == ElementKind.ObjectFlow));

        var (copy, _, text) = RoundTrip(m, r.Diagrams[0]);
        Assert.Contains("[(\"Orders\")]", text);
        Assert.Contains("shape: h-cyl", text);
        Assert.Contains("shape: doc", text);
        Assert.Equal(nodes.OrderBy(k => k.Key), copy.OfKind(ElementKind.ObjectNode).ToDictionary(o => o.Name, o => o.Get(FieldKeys.Subtype)).OrderBy(k => k.Key));
    }

    [Fact]
    public void Class_Multiplicities_Are_Kept_At_Both_Ends_And_Round_Trip()
    {
        var (m, r) = Import("classDiagram\n Fleet \"1\" --> \"1..*\" Car : has\n Car \"0..1\" *-- \"4\" Wheel\n Garage \"*\" o-- \"*\" Car");
        Relationship Rel(ModelRepository x, ElementKind k) => x.Relationships.Single(e => e.Kind == k);
        Assert.Equal(("1", "1..*"), (Rel(m, ElementKind.Association).Get(FieldKeys.SourceMultiplicity), Rel(m, ElementKind.Association).Multiplicity));
        Assert.Equal(("0..1", "4"), (Rel(m, ElementKind.Composition).Get(FieldKeys.SourceMultiplicity), Rel(m, ElementKind.Composition).Multiplicity));
        Assert.Equal(("*", "*"), (Rel(m, ElementKind.Aggregation).Get(FieldKeys.SourceMultiplicity), Rel(m, ElementKind.Aggregation).Multiplicity));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("source end"));

        var (copy, _, text) = RoundTrip(m, r.Diagrams[0]);
        Assert.Contains("Fleet \"1\" --> \"1..*\" Car", text);
        foreach (var k in new[] { ElementKind.Association, ElementKind.Composition, ElementKind.Aggregation })
            Assert.Equal((Rel(m, k).Get(FieldKeys.SourceMultiplicity), Rel(m, k).Multiplicity), (Rel(copy, k).Get(FieldKeys.SourceMultiplicity), Rel(copy, k).Multiplicity));
    }

    [Fact]
    public void Er_Cardinalities_Keep_Both_Ends()
    {
        var (m, _) = Import("erDiagram\n CUSTOMER ||--o{ ORDER : places");
        var r = m.Relationships.Single(x => x.Kind == ElementKind.Association);
        Assert.Equal("1", r.Get(FieldKeys.SourceMultiplicity));
        Assert.Equal("*", r.Multiplicity);
    }
}
