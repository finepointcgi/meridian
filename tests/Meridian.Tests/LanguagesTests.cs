using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;

namespace Meridian.Tests;

public class LanguagesTests
{
    private static (ModelRepository M, Element Pkg) Empty()
    {
        var m = new ModelRepository("Enterprise");
        return (m, m.Create(m.Root, ElementKind.Package, "Processes"));
    }

    [Fact]
    public void Bpmn_Round_Trips_Process_With_Layout()
    {
        var (m, pkg) = Empty();
        var d = m.CreateDiagram(pkg, DiagramType.Bpmn, "Order handling");
        Element Node(string sub, string name, double x)
        {
            var e = m.Create(pkg, ElementKind.BpmnNode, name);
            m.Set(e, FieldKeys.Subtype, sub);
            var (w, h) = Notation.DefaultSizeOf(e);
            m.AddNode(d, e, new Bounds(x, 100, w, h));
            return e;
        }
        var start = Node("startEvent", "", 40);
        var task = Node("userTask", "Check order", 140);
        var gw = Node("exclusiveGateway", "", 320);
        var end = Node("endEvent", "", 420);
        foreach (var (s, t) in new[] { (start, task), (task, gw), (gw, end) })
        {
            var f = m.Connect(ElementKind.BpmnFlow, s, t, pkg);
            m.Set(f, FieldKeys.Subtype, "sequence");
            m.AddEdge(d, f);
        }
        var xml = BpmnExchange.Export(m, d);
        Assert.Contains("userTask", xml.ToString());
        var (m2, pkg2) = Empty();
        var back = BpmnExchange.Import(m2, pkg2, xml);
        Assert.Equal(4, back.Nodes.Count);
        Assert.Equal(3, back.Edges.Count);
        var t2 = back.Nodes.Single(n => n.Element.Name == "Check order");
        Assert.Equal("userTask", t2.Element.Get(FieldKeys.Subtype));
        Assert.Equal(140, t2.Bounds.X);
    }

    [Fact]
    public void ArchiMate_Round_Trips_Elements_Relations_And_Views()
    {
        var (m, pkg) = Empty();
        var v = m.CreateDiagram(pkg, DiagramType.ArchiMate, "Application cooperation");
        var actor = m.Create(pkg, ElementKind.ArchiElement, "Customer"); m.Set(actor, FieldKeys.Subtype, "businessActor");
        var app = m.Create(pkg, ElementKind.ArchiElement, "CRM"); m.Set(app, FieldKeys.Subtype, "applicationComponent");
        var svc = m.Create(pkg, ElementKind.ArchiElement, "Customer service"); m.Set(svc, FieldKeys.Subtype, "applicationService");
        m.AddNode(v, actor, new Bounds(0, 0, 150, 70)); m.AddNode(v, app, new Bounds(300, 0, 150, 70)); m.AddNode(v, svc, new Bounds(300, 150, 150, 70));
        var r1 = m.Connect(ElementKind.ArchiRelation, app, svc, pkg); m.Set(r1, FieldKeys.Subtype, "realization"); m.AddEdge(v, r1);
        var r2 = m.Connect(ElementKind.ArchiRelation, svc, actor, pkg); m.Set(r2, FieldKeys.Subtype, "serving"); m.AddEdge(v, r2);

        var xml = ArchiMateExchange.Export(m);
        var (m2, pkg2) = Empty();
        var (els, rels, views) = ArchiMateExchange.Import(m2, pkg2, xml);
        Assert.Equal((3, 2, 1), (els, rels, views));
        Assert.Equal("applicationComponent", m2.OfKind(ElementKind.ArchiElement).Single(e => e.Name == "CRM").Get(FieldKeys.Subtype));
        Assert.Equal(2, m2.Diagrams.Single().Edges.Count);
        Assert.Contains(m2.Relationships, r => r.Get(FieldKeys.Subtype) == "serving");
    }

    [Fact]
    public void Ddl_Reverse_Engineers_And_Regenerates()
    {
        const string ddl = """
            -- schema
            CREATE TABLE customer (
                id serial PRIMARY KEY,
                email varchar(200) NOT NULL UNIQUE,
                credit numeric(10,2) DEFAULT 0
            );
            CREATE TABLE "order" (
                id bigint NOT NULL,
                customer_id integer NOT NULL REFERENCES customer(id),
                placed_at timestamp,
                CONSTRAINT order_pk PRIMARY KEY (id)
            );
            """;
        var (m, pkg) = Empty();
        var (tables, fks) = SqlDdl.Import(m, pkg, ddl);
        Assert.Equal((2, 1), (tables, fks));
        var customer = m.OfKind(ElementKind.Table).Single(t => t.Name == "customer");
        var email = customer.OwnedElements.Single(c => c.Name == "email");
        Assert.Equal("varchar(200)", email.Get(FieldKeys.SqlType));
        Assert.Equal("true", email.Get(FieldKeys.Unique));
        Assert.Equal("numeric(10,2)", customer.OwnedElements.Single(c => c.Name == "credit").Get(FieldKeys.SqlType));
        Assert.Equal("true", m.OfKind(ElementKind.Table).Single(t => t.Name == "order").OwnedElements.Single(c => c.Name == "id").Get(FieldKeys.PrimaryKey));
        var fk = m.Relationships.Single(r => r.Kind == ElementKind.ForeignKey);
        Assert.Equal("customer_id", fk.Get(FieldKeys.FkColumns));

        var pg = SqlDdl.Generate(m, SqlDialect.PostgreSql);
        Assert.Contains("FOREIGN KEY (\"customer_id\") REFERENCES \"customer\" (\"id\")", pg);
        var (m2, pkg2) = Empty();
        Assert.Equal((2, 1), SqlDdl.Import(m2, pkg2, pg));
    }

    [Fact]
    public void CSharp_Reverse_Engineering_Builds_Class_Model()
    {
        const string code = """
            namespace Shop.Domain;
            /// <summary>A purchasable item.</summary>
            public abstract class Product { public string Name { get; set; } = ""; public decimal Price { get; set; } }
            public interface IShippable { double Weight { get; } }
            public class Book : Product, IShippable { public double Weight => 0.5; public List<Author> Authors { get; } = new(); public bool IsInStock(int quantity) => true; }
            public record Author(string Name, int? BirthYear);
            public enum Format { Hardcover, Paperback, Ebook }
            """;
        var (m, pkg) = Empty();
        var result = CSharpReverse.Import(m, pkg, [("Shop.cs", code)]);
        Assert.Equal(5, result.Types);
        var book = m.AllElements.Single(e => e.Name == "Book");
        Assert.Equal("Domain", book.Owner!.Name);
        Assert.Equal("Shop", book.Owner!.Owner!.Name);
        Assert.Single(m.Outgoing(book, ElementKind.Generalization));
        Assert.Single(m.Outgoing(book, ElementKind.Realization));
        var authors = book.OwnedElements.Single(e => e.Name == "Authors");
        Assert.Equal("*", authors.Multiplicity);
        Assert.Equal("Author", m.TypeDisplayName(authors));
        Assert.Equal("(quantity : Integer) : Boolean", book.OwnedElements.Single(e => e.Name == "IsInStock").Get(FieldKeys.Signature));
        Assert.Equal("true", m.AllElements.Single(e => e.Name == "Product").Get(FieldKeys.IsAbstract));
        Assert.Equal("A purchasable item.", m.AllElements.Single(e => e.Name == "Product").Documentation);
        Assert.Equal("0..1", m.AllElements.Single(e => e.Name == "Author").OwnedElements.Single(p => p.Name == "BirthYear").Multiplicity);
        Assert.Equal(3, m.AllElements.Single(e => e.Name == "Format").OwnedElements.Count);
        Assert.NotNull(result.Diagram);
    }

    [Fact]
    public void Uaf_Profile_Defines_Applicable_Stereotypes_With_Typed_Tags()
    {
        var m = SampleModels.Vehicle();
        var uaf = Profiles.AddUaf(m, m.Root);
        var capability = uaf.OwnedElements.Single(s => s.Name == "Capability");
        Assert.Equal("Block", capability.Get(FieldKeys.Metaclass));
        Assert.Equal("Enum", capability.OwnedElements.Single(t => t.Name == "maturity").Get(FieldKeys.TagType));
        var vehicle = m.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle");
        m.Set(vehicle, FieldKeys.Stereotypes, "Capability");
        Assert.Equal("#f5deaa", SvgRenderer.StereotypeColor(m, vehicle));
        Assert.Contains("«Capability»", Notation.Content(m, vehicle, DiagramType.BlockDefinition).Keyword);
    }
}
