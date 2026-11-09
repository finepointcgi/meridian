using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;
using Microsoft.Data.Sqlite;

namespace Meridian.Tests;

public class InteropTests
{
    [Fact]
    public void Enterprise_Architect_Qea_Imports_Elements_Connectors_And_Layout()
    {
        var dir = Directory.CreateTempSubdirectory("meridian-ea-test-");
        var path = Path.Combine(dir.FullName, "model.qea");
        try
        {
            using (var c = new SqliteConnection($"Data Source={path}"))
            {
                c.Open();
                void Exec(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
                Exec("create table t_package (Package_ID int, Name text, Parent_ID int, Notes text)");
                Exec("create table t_object (Object_ID int, Object_Type text, Name text, Alias text, Note text, Package_ID int, Stereotype text, ParentID int, Classifier int, Abstract text, NType int)");
                Exec("create table t_objectproperties (Object_ID int, Property text, Value text, Notes text)");
                Exec("create table t_attribute (ID int, Object_ID int, Name text, Type text, Classifier text, LowerBound text, UpperBound text, \"Default\" text, Notes text, Scope text, Pos int)");
                Exec("create table t_operation (OperationID int, Object_ID int, Name text, Type text, Notes text, Pos int)");
                Exec("create table t_operationparams (OperationID int, Name text, Type text, Kind text, Pos int)");
                Exec("create table t_connector (Connector_ID int, Connector_Type text, SubType text, Stereotype text, Name text, Start_Object_ID int, End_Object_ID int, DestCard text, PDATA2 text)");
                Exec("create table t_diagram (Diagram_ID int, Package_ID int, ParentID int, Diagram_Type text, Name text, StyleEx text)");
                Exec("create table t_diagramobjects (Diagram_ID int, Object_ID int, RectTop int, RectLeft int, RectRight int, RectBottom int, Sequence int)");
                Exec("create table t_diagramlinks (DiagramID int, ConnectorID int, Hidden text)");
                Exec("insert into t_package values (1,'Model',0,''),(2,'Drone',1,'UAV design')");
                Exec("""
                    insert into t_object values
                    (10,'Class','Drone','','A quadcopter',2,'block',0,0,'0',0),
                    (11,'Class','Rotor','','',2,'block',0,0,'0',0),
                    (12,'Requirement','Endurance','R-1','Fly 30 minutes',2,'',0,0,'0',0),
                    (13,'Port','power','','',2,'',10,0,'0',0),
                    (14,'Activity','Fly','','',2,'',0,0,'0',0),
                    (15,'Action','Take off','','',2,'',14,0,'0',0),
                    (16,'StateNode','','','',2,'',14,0,'0',100)
                    """);
                Exec("insert into t_objectproperties values (12,'priority','high','')");
                Exec("insert into t_attribute values (1,10,'mass','double','','1','1','1.2','',  'Public',0),(2,10,'rotors','','11','4','4','','','Public',1)");
                Exec("insert into t_operation values (1,10,'land','bool','',0)");
                Exec("insert into t_operationparams values (1,'speed','double','in',0)");
                Exec("""
                    insert into t_connector values
                    (100,'Aggregation','Strong','','',11,10,'4',''),
                    (101,'Abstraction','','satisfy','',10,12,'',''),
                    (102,'ControlFlow','','','',16,15,'','guard=ready;')
                    """);
                Exec("insert into t_diagram values (1,2,0,'Logical','Drone structure','MDGDgm=SysML1.4::BlockDefinition;')");
                Exec("insert into t_diagramobjects values (1,10,-50,100,300,-150,1),(1,11,-250,100,260,-330,2)");
                Exec("insert into t_diagramlinks values (1,100,'0')");
            }

            var (m, report) = EaImporter.Import(path);
            var drone = m.OfKind(ElementKind.Block).Single(b => b.Name == "Drone");
            var rotor = m.OfKind(ElementKind.Block).Single(b => b.Name == "Rotor");
            Assert.Equal("A quadcopter", drone.Documentation);
            Assert.Equal("Drone", drone.Owner!.Name);
            var rotors = drone.OwnedElements.Single(e => e.Name == "rotors");
            Assert.Same(rotor, m.ResolveType(rotors));
            Assert.Equal("4", rotors.Multiplicity);
            Assert.Equal("Real", drone.OwnedElements.Single(e => e.Name == "mass").TypeRef);
            Assert.Equal("(speed : Real) : Boolean", drone.OwnedElements.Single(e => e.Name == "land").Get(FieldKeys.Signature));
            Assert.Single(drone.OwnedElements, e => e.Kind == ElementKind.Port);
            var req = m.OfKind(ElementKind.Requirement).Single();
            Assert.Equal("R-1", req.Get(FieldKeys.RequirementId));
            Assert.Equal("Fly 30 minutes", req.Get(FieldKeys.Text));
            Assert.Equal("high", req.Get(FieldKeys.TagPrefix + "priority"));
            Assert.Single(m.Incoming(req, ElementKind.Satisfy));
            // EA composition (part → whole) becomes whole → part.
            var comp = m.Relationships.Single(r => r.Kind == ElementKind.Composition);
            Assert.Same(drone, comp.Source);
            Assert.Equal("ready", m.Relationships.Single(r => r.Kind == ElementKind.ControlFlow).Get(FieldKeys.Guard));
            var bdd = m.Diagrams.Single();
            Assert.Equal(DiagramType.BlockDefinition, bdd.DiagramType);
            Assert.Equal(new Bounds(100, 50, 200, 100), bdd.NodeFor(drone)!.Bounds);
            Assert.Single(bdd.Edges);
            _ = report;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            dir.Delete(true);
        }
    }

    [Fact]
    public void ReqIf_Round_Trips_Requirements_Hierarchy_And_Links()
    {
        var src = SampleModels.Vehicle();
        var doc = ReqIfExchange.Export(src);
        var m = new ModelRepository("Target");
        var pkg = m.Create(m.Root, ElementKind.Package, "Imported");
        var (created, updated, relations) = ReqIfExchange.Import(m, pkg, doc);
        Assert.Equal(src.OfKind(ElementKind.Requirement).Count(), created);
        Assert.Equal(0, updated);
        Assert.Equal(1, relations);   // the deriveReqt link
        var accel = m.OfKind(ElementKind.Requirement).Single(r => r.Get(FieldKeys.RequirementId) == "REQ-002");
        Assert.Equal("Performance", accel.Owner!.Name);
        Assert.StartsWith("The vehicle shall accelerate", accel.Get(FieldKeys.Text));
        Assert.Single(m.Incoming(accel, ElementKind.DeriveReqt));
        // Re-import updates instead of duplicating.
        (created, updated, _) = ReqIfExchange.Import(m, pkg, doc);
        Assert.Equal((0, src.OfKind(ElementKind.Requirement).Count()), (created, updated));
    }

    [Fact]
    public void Xmi_Preserves_Diagram_Layout_Nesting_And_Style()
    {
        var m = SampleModels.Vehicle();
        var ibd = m.Diagrams.Single(d => d.DiagramType == DiagramType.InternalBlock);
        var part = ibd.Nodes.First();
        m.SetNodeStyle(ibd, part, "fill", "#ff6b6b");
        var port = m.AddNode(ibd, m.OfKind(ElementKind.Port).First(p => p.Name == "fuelIn"), new Bounds(10, 10, 16, 16), parent: part);
        var (back, report) = XmiExchange.Import(XmiExchange.ToXml(m));
        Assert.Empty(report.Warnings);
        Assert.Equal(m.Diagrams.Count(), back.Diagrams.Count());
        foreach (var d in m.Diagrams)
        {
            var bd = (Diagram)back.Find(d.Id)!;
            Assert.Equal(d.DiagramType, bd.DiagramType);
            Assert.Equal(d.Nodes.Count, bd.Nodes.Count);
            Assert.Equal(d.Edges.Count, bd.Edges.Count);
            foreach (var n in d.Nodes) Assert.Equal(n.Bounds, bd.FindNode(n.Id)!.Bounds);
        }
        var bIbd = (Diagram)back.Find(ibd.Id)!;
        Assert.Equal("#ff6b6b", bIbd.FindNode(part.Id)!.Style["fill"]);
        Assert.Equal(part.Id, bIbd.FindNode(port.Id)!.ParentId);
    }
}
