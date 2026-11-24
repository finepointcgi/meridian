using System.Text;
using Meridian.Core.Model;
using Meridian.Serialization.DiagramFiles;
using Meridian.Serialization.Mermaid;
using Meridian.Serialization.PlantUml;

namespace Meridian.Tests;

public class PlantUmlTests
{
    private static (ModelRepository M, MermaidImportResult R) Import(string text)
    {
        var m = new ModelRepository("P");
        return (m, PlantUmlImporter.Import(m, m.Root, text));
    }

    private static Element Named(ModelRepository m, string name, ElementKind kind) => m.AllElements.Single(e => e.Name == name && e.Kind == kind);

    [Fact]
    public void Sequence_Diagram_Becomes_An_Interaction_With_Messages_Fragments_And_Notes()
    {
        var (m, r) = Import("""
            @startuml
            title Login
            ' a comment
            actor User as U
            participant "Web App" as W
            database DB
            U -> W : login(name, pw)
            activate W
            W -> DB : query
            DB --> W : row
            alt valid
              W ->> U : welcome
            else invalid
              W --> U : error
            end
            note over W, DB : checked every time
            W -> W : log
            U -> W : logout
            return done
            @enduml
            """);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal("Login", d.Name);
        Assert.Equal(DiagramType.Sequence, d.DiagramType);
        Assert.Equal(["User", "Web App", "DB"], m.OfKind(ElementKind.Lifeline).Select(l => l.Name));
        var messages = m.OfKind(ElementKind.Message).Cast<Relationship>().ToList();
        Assert.Equal(["login(name, pw)", "query", "row", "welcome", "error", "log", "logout", "done"], messages.Select(x => x.Name));
        Assert.Equal("reply", messages[2].Get(FieldKeys.MessageSort));
        Assert.Equal("asynchCall", messages[3].Get(FieldKeys.MessageSort));
        Assert.Equal("Web App", messages[^1].Source.Name);   // return answers the open call U -> W : logout
        Assert.Equal("User", messages[^1].Target.Name);
        var fragment = Assert.Single(m.OfKind(ElementKind.CombinedFragment));
        Assert.Equal(["valid", "invalid"], fragment.OwnedElements.Where(e => e.Kind == ElementKind.InteractionOperand).Select(o => o.Get(FieldKeys.Guard)));
        Assert.Contains(m.OfKind(ElementKind.Comment), c => c.Get(FieldKeys.Body) == "checked every time");
    }

    [Fact]
    public void Class_Diagram_Becomes_Blocks_With_Members_Packages_And_Relationships()
    {
        var (m, r) = Import("""
            @startuml
            package Vehicle {
              abstract class Engine {
                +power : Real
                -{static} count : int
                +start() : Boolean
              }
              class Car extends Vehicle.Base {
                wheels : Wheel[4]
              }
              class Wheel
            }
            interface Drivable
            enum Gear {
              PARK,
              DRIVE
            }
            Car *-- "1" Engine : engine
            Car o-- "4" Wheel
            Car ..|> Drivable
            Car --> Gear : uses
            Car : +honk()
            note right of Car : the main thing
            @enduml
            """);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.BlockDefinition, d.DiagramType);
        var car = Named(m, "Car", ElementKind.Block);
        Assert.Equal("Vehicle", car.Owner!.Name);
        var engine = Named(m, "Engine", ElementKind.Block);
        Assert.True(engine.IsAbstract);
        Assert.Contains(engine.OwnedElements, e => e.Kind == ElementKind.Operation && e.Name == "start");
        Assert.Contains(engine.OwnedElements, e => e.Kind == ElementKind.Property && e.Name == "power");
        Assert.Contains(car.OwnedElements, e => e.Kind == ElementKind.Operation && e.Name == "honk");
        Assert.Contains(car.OwnedElements, e => e.Kind == ElementKind.Property && e.Name == "engine" && e.TypeRef == engine.Id);
        Assert.Equal(["PARK", "DRIVE"], Named(m, "Gear", ElementKind.Enumeration).OwnedElements.Select(e => e.Name));
        Assert.Contains(m.Relationships, x => x.Kind == ElementKind.Realization && x.Source == car && x.Target.Name == "Drivable");
        Assert.Contains(m.Relationships, x => x.Kind == ElementKind.Generalization && x.Source == car);
        Assert.Contains(m.Relationships, x => x.Kind == ElementKind.Aggregation && x.Source == car && x.Get(FieldKeys.Multiplicity) == "4");
        Assert.Contains(m.Relationships, x => x.Kind == ElementKind.Association && x.Name == "uses");
        Assert.Contains(m.OfKind(ElementKind.Comment), c => c.Get(FieldKeys.Body) == "the main thing");
    }

    [Fact]
    public void Use_Case_Diagram_Becomes_Actors_Use_Cases_And_A_Boundary()
    {
        var (m, r) = Import("""
            @startuml
            left to right direction
            actor Customer
            actor :Bank Clerk: as Clerk
            rectangle ATM {
              usecase "Withdraw cash" as UC1
              (Check balance)
              (Authenticate)
            }
            Customer --> UC1
            Customer --> (Check balance)
            UC1 .> (Authenticate) : <<include>>
            Clerk -- (Check balance)
            Admin --|> Clerk
            @enduml
            """);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.UseCase, d.DiagramType);
        Assert.Equal(["Admin", "Bank Clerk", "Customer"], m.OfKind(ElementKind.Actor).Select(a => a.Name).Order());
        Assert.Equal(["Authenticate", "Check balance", "Withdraw cash"], m.OfKind(ElementKind.UseCase).Select(a => a.Name).Order());
        Assert.Contains(m.Relationships, x => x.Kind == ElementKind.Include && x.Source.Name == "Withdraw cash" && x.Target.Name == "Authenticate");
        Assert.Contains(m.Relationships, x => x.Kind == ElementKind.Generalization && x.Source.Name == "Admin");
        Assert.Equal(3, m.Relationships.Count(x => x.Kind == ElementKind.Association));
        Assert.Contains(d.Nodes, n => n.Element.Kind == ElementKind.Shape && n.Element.Name == "ATM");
    }

    [Fact]
    public void Activity_Diagram_Becomes_An_Activity_With_Decisions_Loops_Forks_And_Lanes()
    {
        var (m, r) = Import("""
            @startuml
            |Customer|
            start
            :Choose items;
            :Pay
            by card;
            |Shop|
            if (Paid?) then (yes)
              :Pack;
            else (no)
              :Cancel;
              stop
            endif
            while (More parcels?) is (yes)
              :Label parcel;
            endwhile (no)
            fork
              :Send mail;
            fork again
              :Ship;
            end fork
            stop
            @enduml
            """);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.Activity, d.DiagramType);
        var act = Assert.Single(m.OfKind(ElementKind.Activity));
        var names = act.OwnedElements.Where(e => e.Kind == ElementKind.Action).Select(e => e.Name).ToList();
        Assert.Equal(["Choose items", "Pay by card", "Pack", "Cancel", "Label parcel", "Send mail", "Ship"], names);
        Assert.Equal(2, act.OwnedElements.Count(e => e.Kind == ElementKind.DecisionNode));
        Assert.Equal(2, act.OwnedElements.Count(e => e.Kind == ElementKind.ForkNode));
        Assert.Single(act.OwnedElements, e => e.Kind == ElementKind.InitialNode);
        Assert.Equal(2, act.OwnedElements.Count(e => e.Kind == ElementKind.ActivityFinalNode));
        var flows = m.Relationships.Where(x => x.Kind == ElementKind.ControlFlow).ToList();
        Assert.Contains(flows, f => f.Source.Name == "Paid?" && f.Target.Name == "Pack" && f.Get(FieldKeys.Guard) == "yes");
        Assert.Contains(flows, f => f.Source.Name == "Label parcel" && f.Target.Name == "More parcels?");   // the loop back
        Assert.Contains(flows, f => f.Source.Name == "More parcels?" && f.Get(FieldKeys.Guard) == "no" && f.Target.Kind == ElementKind.ForkNode);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void State_Diagram_Becomes_A_State_Machine_With_Composite_States()
    {
        var (m, r) = Import("""
            @startuml
            [*] --> Idle
            state "Running fast" as Running {
              [*] --> Warmup
              Warmup --> Cruise : warm [temp > 60] / log
            }
            Idle --> Running : start
            Running --> Idle : stop
            Running : entry / spin up
            state Check <<choice>>
            Idle --> Check
            Check --> [*]
            @enduml
            """);
        var d = Assert.Single(r.Diagrams);
        Assert.Equal(DiagramType.StateMachine, d.DiagramType);
        var running = Named(m, "Running fast", ElementKind.State);
        Assert.Equal("spin up", running.Get(FieldKeys.Entry));
        Assert.Contains(running.OwnedElements, e => e.Name == "Warmup");
        Assert.Contains(running.OwnedElements, e => e.Kind == ElementKind.InitialState);
        var warm = m.Relationships.Single(x => x.Kind == ElementKind.Transition && x.Source.Name == "Warmup");
        Assert.Equal("warm", warm.Name);
        Assert.Equal("temp > 60", warm.Get(FieldKeys.Guard));
        Assert.Equal("log", warm.Get(FieldKeys.Effect));
        Assert.Single(m.OfKind(ElementKind.Pseudostate));
        Assert.Single(m.OfKind(ElementKind.FinalState));
    }

    [Fact]
    public void Several_Blocks_Unsupported_Kinds_And_Markdown()
    {
        var preview = PlantUmlImporter.Preview("""
            @startuml
            A -> B : hi
            @enduml
            @startmindmap
            * root
            @endmindmap
            @startuml
            (*) --> "Old"
            @enduml
            """);
        Assert.Equal(3, preview.Diagrams.Count);
        Assert.Equal(1, preview.Supported);
        Assert.Equal("Sequence diagram", preview.Diagrams[0].Becomes);
        Assert.Contains(preview.Warnings, w => w.Contains("@startmindmap"));
        Assert.Contains(preview.Warnings, w => w.Contains("old syntax"));

        // Markdown: both mermaid and plantuml blocks; the Mermaid paste dialog reads PlantUML too.
        var md = MermaidImporter.Preview("# Doc\n```mermaid\nflowchart LR\n A --> B\n```\n\n```plantuml\n@startuml\nA -> B : x\n@enduml\n```\n", markdown: true);
        Assert.Equal(2, md.Supported);
        Assert.Equal(1, MermaidImporter.Preview("@startuml\nclass A\nclass B\nA <|-- B\n@enduml").Supported);
    }

    [Fact]
    public void Folders_Pick_Up_PlantUml_Files()
    {
        var m = new ModelRepository("Docs");
        var files = new[] { new DiagramSourceFile("docs/login.puml", Encoding.UTF8.GetBytes("@startuml\nAlice -> Bob : hi\n@enduml")) };
        Assert.Equal(DiagramFileFormat.PlantUml, DiagramFolderImport.FormatOf("docs/login.puml"));
        Assert.Equal("Sequence diagram", Assert.Single(Assert.Single(DiagramFolderImport.Preview(files)).Diagrams).Becomes);
        var result = DiagramFolderImport.Import(m, m.Root, files);
        Assert.Equal("login", Assert.Single(result.Diagrams).Name);
        Assert.Equal("docs/login.puml", result.Diagrams[0].Get(DiagramFolderImport.SourceField));
    }

    [Fact]
    public void Bad_Input_Never_Throws()
    {
        foreach (var text in new[] { "", "@startuml", "@startuml\n}}}\n@enduml", "@startuml\nif (x) then\n:a;\n@enduml", "@startuml\nclass {\n@enduml", "\0\0", new string('(', 5000) })
        {
            var (_, r) = Import(text);
            Assert.NotNull(r);
        }
    }
}
