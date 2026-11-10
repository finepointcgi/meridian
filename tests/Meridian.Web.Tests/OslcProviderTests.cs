using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Admin;
using Meridian.Web.Integrations;
using Meridian.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meridian.Web.Tests;

/// <summary>The RDF toolkit (RDF/XML parser, Turtle and JSON-LD writers, content negotiation).</summary>
public class RdfTests
{
    [Fact]
    public void RdfXml_Parser_Handles_Typed_Nodes_Nesting_Literals_And_Blank_Nodes()
    {
        const string xml = """
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:dcterms="http://purl.org/dc/terms/"
                     xmlns:oslc="http://open-services.net/ns/core#" xmlns:ex="http://example.test/ns#" xml:base="https://x.test/base/">
              <oslc:ServiceProvider rdf:about="sp1" ex:attr="plain">
                <dcterms:title rdf:parseType="Literal"><b>Bold</b> title</dcterms:title>
                <dcterms:modified rdf:datatype="http://www.w3.org/2001/XMLSchema#dateTime">2026-01-02T03:04:05Z</dcterms:modified>
                <dcterms:description xml:lang="de">Beschreibung</dcterms:description>
                <oslc:service rdf:parseType="Resource"><oslc:domain rdf:resource="http://open-services.net/ns/rm#"/></oslc:service>
                <ex:shared rdf:nodeID="n1"/>
                <ex:nested><ex:Thing rdf:about="#thing"><dcterms:title>Inner</dcterms:title></ex:Thing></ex:nested>
                <ex:list rdf:parseType="Collection"><rdf:Description rdf:about="a"/><rdf:Description rdf:about="b"/></ex:list>
              </oslc:ServiceProvider>
              <rdf:Description rdf:nodeID="n1"><dcterms:title>Shared</dcterms:title></rdf:Description>
              <rdf:Bag rdf:about="bag"><rdf:li>one</rdf:li><rdf:li>two</rdf:li></rdf:Bag>
            </rdf:RDF>
            """;
        var g = RdfXmlParser.Parse(xml);
        var sp = new RdfIri("https://x.test/base/sp1");
        Assert.Contains("http://open-services.net/ns/core#ServiceProvider", g.Iris(sp, RdfGraph.RdfType));
        var title = Assert.IsType<RdfLiteral>(g.Objects(sp, "http://purl.org/dc/terms/title").Single());
        Assert.Equal(RdfLiteral.XmlLiteral, title.Datatype);
        Assert.Contains("<b>Bold</b> title", title.Value);
        Assert.Equal("http://www.w3.org/2001/XMLSchema#dateTime", ((RdfLiteral)g.Objects(sp, "http://purl.org/dc/terms/modified").Single()).Datatype);
        Assert.Equal("de", ((RdfLiteral)g.Objects(sp, "http://purl.org/dc/terms/description").Single()).Lang);
        Assert.Equal("plain", g.Value(sp, "http://example.test/ns#attr"));
        var service = g.Objects(sp, "http://open-services.net/ns/core#service").Single();
        Assert.IsType<RdfBlank>(service);
        Assert.Equal("http://open-services.net/ns/rm#", g.Value(service, "http://open-services.net/ns/core#domain"));
        Assert.Equal("Shared", g.Value(g.Objects(sp, "http://example.test/ns#shared").Single(), "http://purl.org/dc/terms/title"));
        Assert.Equal("Inner", g.Value(new RdfIri("https://x.test/base/#thing"), "http://purl.org/dc/terms/title"));
        var list = g.Objects(sp, "http://example.test/ns#list").Single();
        Assert.Equal("https://x.test/base/a", g.Value(list, RdfGraph.RdfNs + "first"));
        Assert.Equal("two", g.Value(new RdfIri("https://x.test/base/bag"), RdfGraph.RdfNs + "_2"));
    }

    [Fact]
    public void RdfXml_Parser_Refuses_Dtds_And_Garbage()
    {
        Assert.Throws<RdfFormatException>(() => RdfXmlParser.Parse("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><x>&e;</x>"));
        Assert.Throws<RdfFormatException>(() => RdfXmlParser.Parse("<html><body>Login"));
    }

    [Fact]
    public void Turtle_Uses_Prefixes_And_Escapes_Literals()
    {
        var g = new RdfGraph();
        var s = new RdfIri("https://m.test/r/1");
        g.Add(s, RdfGraph.RdfType, new RdfIri("http://open-services.net/ns/rm#Requirement"));
        g.Add(s, "http://purl.org/dc/terms/title", new RdfLiteral("Say \"hi\"\nnow \\ ok"));
        g.Add(s, "http://purl.org/dc/terms/created", new RdfLiteral("2026-01-01", "http://www.w3.org/2001/XMLSchema#date"));
        g.Add(s, "http://purl.org/dc/terms/subject", new RdfLiteral("Bremse", null, "de"));
        g.Add(s, "http://example.test/odd path#x", new RdfIri("https://m.test/a b"));
        var ttl = Turtle.Write(g);
        Assert.Contains("@prefix dcterms: <http://purl.org/dc/terms/> .", ttl);
        Assert.Contains("@prefix oslc_rm: <http://open-services.net/ns/rm#> .", ttl);
        Assert.DoesNotContain("@prefix oslc_am:", ttl);   // only prefixes in use
        Assert.Contains("<https://m.test/r/1>\n    a oslc_rm:Requirement ;", ttl);
        Assert.Contains("dcterms:title \"Say \\\"hi\\\"\\nnow \\\\ ok\"", ttl);
        Assert.Contains("\"2026-01-01\"^^xsd:date", ttl);
        Assert.Contains("\"Bremse\"@de", ttl);
        Assert.Contains("<http://example.test/odd\\u0020path#x> <https://m.test/a\\u0020b>", ttl);
        Assert.EndsWith(" .\n", ttl);
    }

    [Fact]
    public void JsonLd_Writes_A_Compacted_Graph()
    {
        var g = new RdfGraph();
        var s = new RdfIri("https://m.test/r/1");
        g.Add(s, RdfGraph.RdfType, new RdfIri("http://open-services.net/ns/rm#Requirement"));
        g.Add(s, "http://purl.org/dc/terms/title", new RdfLiteral("Brake"));
        g.Add(s, "http://open-services.net/ns/rm#satisfiedBy", new RdfIri("https://m.test/a"));
        g.Add(s, "http://open-services.net/ns/rm#satisfiedBy", new RdfIri("https://m.test/b"));
        g.Add(s, "http://open-services.net/ns/core#totalCount", new RdfLiteral("3", "http://www.w3.org/2001/XMLSchema#integer"));
        var json = JsonLd.Write(g);
        Assert.Equal("http://purl.org/dc/terms/", (string)json["@context"]!["dcterms"]!);
        Assert.Null(json["@context"]!["oslc_am"]);
        var node = json["@graph"]!.AsArray().Single()!;
        Assert.Equal("oslc_rm:Requirement", (string)node["@type"]!);
        Assert.Equal("Brake", (string)node["dcterms:title"]!);
        Assert.Equal(2, node["oslc_rm:satisfiedBy"]!.AsArray().Count);
        Assert.Equal("xsd:integer", (string)node["oslc:totalCount"]!["@type"]!);
    }

    [Theory]
    [InlineData(null, RdfFormat.RdfXml)]
    [InlineData("*/*", RdfFormat.RdfXml)]
    [InlineData("text/turtle", RdfFormat.Turtle)]
    [InlineData("application/ld+json", RdfFormat.JsonLd)]
    [InlineData("application/json", RdfFormat.JsonLd)]
    [InlineData("application/rdf+xml;q=0.5, text/turtle;q=0.9", RdfFormat.Turtle)]
    [InlineData("text/turtle;q=0, application/ld+json;q=0.1", RdfFormat.JsonLd)]
    [InlineData("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", RdfFormat.Html)]
    [InlineData("application/x-oslc-compact+xml", RdfFormat.CompactXml)]
    public void Negotiation_Picks_The_Best_Supported_Format(string? accept, RdfFormat expected) =>
        Assert.Equal(expected, RdfNegotiation.Choose(accept, allowHtml: true, allowCompact: true));

    [Fact]
    public void Negotiation_Ignores_Html_And_Compact_Unless_Allowed() =>
        Assert.Equal(RdfFormat.RdfXml, RdfNegotiation.Choose("text/html, application/x-oslc-compact+xml"));
}

/// <summary>oslc.where / oslc.select / oslc.orderBy / oslc.prefix parsing and evaluation.</summary>
public class OslcQueryTests
{
    private static readonly Dictionary<string, string> P = OslcQuery.Prefixes(null);
    private const string Dc = "http://purl.org/dc/terms/";

    [Fact]
    public void Where_Parses_Comparisons_In_Lists_Uris_And_Conjunctions()
    {
        var terms = OslcQuery.ParseWhere("dcterms:identifier!=\"REQ-1\" and dcterms:title in [\"A\", \"B\\\"q\"] and oslc_rm:satisfiedBy=<https://m/x> and dcterms:modified>=\"2026-01-01\"^^xsd:dateTime and rdf:type=oslc_rm:Requirement", P);
        Assert.Equal(5, terms.Count);
        Assert.Equal((Dc + "identifier", OslcQuery.Op.Ne, new OslcQuery.Value("REQ-1", false)), (terms[0].Property, terms[0].Op, Assert.Single(terms[0].Values)));
        Assert.Equal(OslcQuery.Op.In, terms[1].Op);
        Assert.Equal(["A", "B\"q"], terms[1].Values.Select(v => v.Text));
        Assert.Equal(new OslcQuery.Value("https://m/x", true), terms[2].Values[0]);
        Assert.Equal(OslcQuery.Op.Ge, terms[3].Op);
        Assert.Equal("http://open-services.net/ns/rm#Requirement", terms[4].Values[0].Text);
    }

    [Theory]
    [InlineData("dcterms:title=\"a\" or dcterms:title=\"b\"", "only “and”")]
    [InlineData("oslc_rm:satisfiedBy{dcterms:title=\"x\"}", "nested")]
    [InlineData("foo:title=\"x\"", "unknown prefix")]
    [InlineData("dcterms:title=x", "quote strings")]
    [InlineData("dcterms:title=\"x", "unterminated")]
    [InlineData("dcterms:title ~ \"x\"", "comparison")]
    [InlineData("title=\"x\"", "needs a prefix")]
    public void Where_Rejects_Unsupported_Syntax_With_A_Clear_Message(string where, string message) =>
        Assert.Contains(message, Assert.Throws<OslcQueryException>(() => OslcQuery.ParseWhere(where, P)).Message);

    [Fact]
    public void Select_And_OrderBy_And_Prefix()
    {
        Assert.Null(OslcQuery.ParseSelect("*", P));
        Assert.Null(OslcQuery.ParseSelect("", P));
        var sel = OslcQuery.ParseSelect("dcterms:title,oslc_rm:satisfiedBy{dcterms:title,dcterms:identifier},x:y", OslcQuery.Prefixes("x=<https://x.test/>"))!;
        Assert.Equal(new HashSet<string> { Dc + "title", "http://open-services.net/ns/rm#satisfiedBy", "https://x.test/y" }, sel.ToHashSet());
        var order = OslcQuery.ParseOrderBy("-dcterms:identifier,+dcterms:title", P);
        Assert.Equal([new OslcQuery.Order(Dc + "identifier", true), new OslcQuery.Order(Dc + "title", false)], order);
        Assert.Throws<OslcQueryException>(() => OslcQuery.ParseOrderBy("dcterms:title", P));
        Assert.Throws<OslcQueryException>(() => OslcQuery.Prefixes("x=https://no-brackets"));
    }

    [Fact]
    public void Filter_Evaluates_Against_Requirements()
    {
        var m = SampleModels.Vehicle();
        var u = new Oslc.Urls("https://meridian.test");
        var all = Oslc.Requirements(m, u, Guid.Empty);
        var notOne = Oslc.Filter(all, "dcterms:identifier!=\"REQ-001\"", null).ToList();
        Assert.Equal(all.Count - 1, notOne.Count);
        var sorted = Oslc.Filter(all, null, null, orderBy: "-dcterms:identifier").Select(r => r.Identifier).ToList();
        Assert.Equal(sorted.OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase), sorted);
        var accel = all.Single(r => r.Identifier == "REQ-002");
        var bySat = Oslc.Filter(all, $"oslc_rm:satisfiedBy=<{accel.SatisfiedBy[0]}>", null).ToList();
        Assert.Contains(bySat, r => r.Identifier == "REQ-002");
        Assert.Equal(["REQ-002", "REQ-003"], Oslc.Filter(all, "dcterms:identifier>\"REQ-001\" and dcterms:identifier<=\"REQ-003\"", null).Select(r => r.Identifier));
    }
}

/// <summary>Provider documents: shapes, services, AM resources, Compact, creation.</summary>
public class OslcProviderDocumentTests
{
    private static readonly Oslc.Urls U = new("https://meridian.test");
    private static readonly Guid P = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Provider_Lists_Rm_And_Am_Services_With_Shapes_Factory_And_Dialogs()
    {
        var g = RdfXmlParser.Parse(Oslc.Provider(U, P, "Vehicle"));
        var services = g.Triples.Where(t => t.Predicate.Value == Oslc.Core.NamespaceName + "service").Select(t => t.Object).ToList();
        Assert.Equal(2, services.Count);
        var rm = services.Single(s => g.Iris(s, Oslc.Core.NamespaceName + "domain").Contains(Oslc.Rm.NamespaceName));
        string? Dialog(RdfNode svc, string kind) => g.Objects(svc, Oslc.Core.NamespaceName + kind).Select(d => g.Iris(d, Oslc.Core.NamespaceName + "dialog").First()).FirstOrDefault();
        Assert.Equal(U.Selector(P), Dialog(rm, "selectionDialog"));
        Assert.Equal(U.Creator(P), Dialog(rm, "creationDialog"));
        var factory = g.Objects(rm, Oslc.Core.NamespaceName + "creationFactory").Single();
        Assert.Equal(U.Query(P), g.Iris(factory, Oslc.Core.NamespaceName + "creation").Single());
        Assert.Equal(U.Shape("requirement"), g.Iris(factory, Oslc.Core.NamespaceName + "resourceShape").Single());
        var am = services.Single(s => g.Iris(s, Oslc.Core.NamespaceName + "domain").Contains(Oslc.Am.NamespaceName));
        var amQuery = g.Objects(am, Oslc.Core.NamespaceName + "queryCapability").Single();
        Assert.Equal(U.AmQuery(P), g.Iris(amQuery, Oslc.Core.NamespaceName + "queryBase").Single());
        Assert.Equal(U.AmSelector(P), Dialog(am, "selectionDialog"));
    }

    [Fact]
    public void Shapes_Describe_Requirement_And_Resource_Properties()
    {
        var g = RdfXmlParser.Parse(Oslc.Shape(U, "requirement")!);
        var shape = new RdfIri(U.Shape("requirement"));
        Assert.Equal(Oslc.Rm.NamespaceName + "Requirement", g.Iris(shape, Oslc.Core.NamespaceName + "describes").Single());
        var props = g.Objects(shape, Oslc.Core.NamespaceName + "property").ToList();
        var names = props.Select(p => g.Value(p, Oslc.Core.NamespaceName + "name")).ToList();
        Assert.Contains("identifier", names);
        Assert.Contains("satisfiedBy", names);
        var sat = props.Single(p => g.Value(p, Oslc.Core.NamespaceName + "name") == "satisfiedBy");
        Assert.Equal(Oslc.Core.NamespaceName + "Zero-or-many", g.Iris(sat, Oslc.Core.NamespaceName + "occurs").Single());
        Assert.Equal("true", g.Value(sat, Oslc.Core.NamespaceName + "readOnly"));
        Assert.NotNull(Oslc.Shape(U, "resource"));
        Assert.Null(Oslc.Shape(U, "nope"));
    }

    [Fact]
    public void Am_Resources_Cover_Blocks_And_Link_To_Requirements()
    {
        var m = SampleModels.Vehicle();
        var all = Oslc.AmResources(m, U, P);
        Assert.DoesNotContain(all, r => r.Kind is "Requirement" or "Diagram" or "Satisfy");
        var vehicle = all.Single(r => r.Title == "Vehicle" && r.Kind == "Block");
        Assert.Contains(vehicle.Satisfies, s => s.StartsWith(U.Base + $"/oslc/projects/{P}/requirements/"));
        Assert.Single(Oslc.FilterAm(all, "dcterms:type=\"Block\" and dcterms:title=\"Vehicle\"", null));
        var rdf = RdfXmlParser.Parse(Oslc.AmRdf(vehicle, U.Provider(P)));
        Assert.Contains(Oslc.Am.NamespaceName + "Resource", rdf.Iris(new RdfIri(vehicle.Url), RdfGraph.RdfType));
        var trimmed = Oslc.AmRdf(vehicle, U.Provider(P), new HashSet<string> { Oslc.Dcterms.NamespaceName + "title" }).ToString();
        Assert.Contains("Vehicle", trimmed);
        Assert.DoesNotContain("mrd:satisfies", trimmed);
    }

    [Fact]
    public void Requirement_Select_And_Compact_Preview()
    {
        var m = SampleModels.Vehicle();
        var e = m.OfKind(ElementKind.Requirement).Single(r => r.Get(FieldKeys.RequirementId) == "REQ-002");
        var r = Oslc.Describe(m, U, P, e);
        var selected = Oslc.RequirementRdf(r, U.Provider(P), new HashSet<string> { Oslc.Dcterms.NamespaceName + "identifier" }).ToString();
        Assert.Contains("REQ-002", selected);
        Assert.DoesNotContain("satisfiedBy", selected);
        var json = Oslc.RequirementJsonLd(r, U.Provider(P), new HashSet<string> { Oslc.Dcterms.NamespaceName + "title" });
        Assert.Null(json["dcterms:identifier"]);
        Assert.Equal("Acceleration", (string)json["dcterms:title"]!);

        var compact = RdfXmlParser.Parse(Oslc.Compact(r, U.Preview(P, e.Id), U.Preview(P, e.Id, "large"), U.Icon));
        var about = new RdfIri(r.Url);
        Assert.Equal("REQ-002", compact.Value(about, Oslc.Core.NamespaceName + "shortTitle"));
        var small = compact.Objects(about, Oslc.Core.NamespaceName + "smallPreview").Single();
        Assert.Equal(U.Preview(P, e.Id), compact.Iris(small, Oslc.Core.NamespaceName + "document").Single());
        Assert.Equal("400px", compact.Value(small, Oslc.Core.NamespaceName + "hintWidth"));
        Assert.Single(compact.Objects(about, Oslc.Core.NamespaceName + "largePreview"));
    }

    [Fact]
    public void Creation_Bodies_Parse_From_Rdf_Xml_And_Json_Ld()
    {
        var rdf = """
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:oslc_rm="http://open-services.net/ns/rm#">
              <oslc_rm:Requirement rdf:about="">
                <dcterms:title rdf:parseType="Literal"><div xmlns="http://www.w3.org/1999/xhtml">Hill start</div></dcterms:title>
                <dcterms:identifier>REQ-900</dcterms:identifier>
                <dcterms:description>Hold on 20 % grade.</dcterms:description>
              </oslc_rm:Requirement>
            </rdf:RDF>
            """;
        Assert.Equal(new Oslc.NewRequirement("Hill start", "REQ-900", "Hold on 20 % grade."), Oslc.ParseCreation(rdf, "application/rdf+xml", "https://m/q"));
        Assert.Equal(new Oslc.NewRequirement("Cruise", null, "Hold speed"),
            Oslc.ParseCreation("""{"@context":{"dcterms":"http://purl.org/dc/terms/"},"@type":"oslc_rm:Requirement","dcterms:title":"Cruise","dcterms:description":{"@value":"Hold speed"}}""", "application/ld+json", "https://m/q"));
        Assert.Throws<RdfFormatException>(() => Oslc.ParseCreation("{\"dcterms:description\":\"x\"}", "application/json", "https://m/q"));
        Assert.Throws<RdfFormatException>(() => Oslc.ParseCreation("not xml", "application/rdf+xml", "https://m/q"));
    }

    [Fact]
    public void Create_Adds_A_Requirement_In_One_Undo_Step_And_Refuses_Duplicate_Ids()
    {
        var m = SampleModels.Vehicle();
        var e = Oslc.Create(m, new Oslc.NewRequirement("Hill start", "REQ-900", "Hold on 20 % grade."), null);
        Assert.Equal("Requirements", e.Owner!.Name);
        Assert.Equal(("REQ-900", "Hold on 20 % grade."), (e.Get(FieldKeys.RequirementId), e.Get(FieldKeys.Text)));
        Assert.Equal("Create requirement (OSLC)", m.History.UndoDescription);
        var auto = Oslc.Create(m, new Oslc.NewRequirement("Auto id", null, null), m.Root.Id);
        Assert.Same(m.Root, auto.Owner);
        Assert.StartsWith("REQ-", auto.Get(FieldKeys.RequirementId));
        Assert.Throws<InvalidOperationException>(() => Oslc.Create(m, new Oslc.NewRequirement("Dup", "req-900", null), null));
    }

    [Fact]
    public void Dialog_And_Preview_Pages_Escape_Model_Text_And_Carry_The_Protocol()
    {
        var creator = OslcPages.Creator("P <&> Q", [("pkg1", "Requirements <x>")], "tok\"en", error: "<bad>",
            created: ("https://m/r/1", "REQ-1 </script><script>alert(1)</script>"));
        Assert.DoesNotContain("<script>alert(1)", creator);
        Assert.Contains("P &lt;&amp;&gt; Q", creator);
        Assert.Contains("value=\"tok&quot;en\"", creator);
        Assert.Contains("oslc-response:", creator);
        Assert.Contains("respond([", creator);   // a created resource is returned at once
        var preview = OslcPages.Preview("Proj", "Requirement", "REQ-1", "<i>x</i>", new string('a', 400), "https://m/?project=1", false, [("Satisfied by", ["<Block>"])]);
        Assert.Contains("&lt;i&gt;x&lt;/i&gt;", preview);
        Assert.Contains("Satisfied by: 1", preview);
        Assert.Contains("…", preview);
        var amSelector = OslcPages.Selector("P", "", "model elements", [new OslcPages.Choice("https://m/a", "", "<Engine>", "Block")]);
        Assert.Contains("&lt;Engine&gt;", amSelector);
        Assert.Contains("URLSearchParams", amSelector);   // searching keeps ?type=resource
    }

    [Fact]
    public void Creator_Token_Is_Bound_To_User_And_Project()
    {
        var protection = new EphemeralDataProtectionProvider();
        var (user, project) = (Guid.NewGuid(), Guid.NewGuid());
        var token = OslcEndpoints.CreatorToken(protection, user, project);
        Assert.True(OslcEndpoints.CreatorTokenValid(protection, token, user, project));
        Assert.False(OslcEndpoints.CreatorTokenValid(protection, token, Guid.NewGuid(), project));
        Assert.False(OslcEndpoints.CreatorTokenValid(protection, token, user, Guid.NewGuid()));
        Assert.False(OslcEndpoints.CreatorTokenValid(protection, "garbage", user, project));
        Assert.False(OslcEndpoints.CreatorTokenValid(protection, "", user, project));
    }
}

/// <summary>The /oslc endpoints, run in-process against a SQLite store (no server, no network).</summary>
public sealed class OslcEndpointTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-oslc-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;
    private CollaborationHub _hub = default!;
    private WebApplication _app = default!;
    private RequestDelegate _pipeline = default!;
    private Guid _project;
    private UserAccount _owner = default!, _viewer = default!;
    private string _ownerToken = "", _viewerToken = "";

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        _hub = new CollaborationHub(_db, NullLogger<CollaborationHub>.Instance);
        _owner = await _db.CreateUserAsync("olga", "Olga", "correct horse");
        _viewer = await _db.CreateUserAsync("victor", "Victor", "battery staple");
        _project = await _db.CreateProjectAsync("Vehicle", ProjectFile.Serialize(SampleModels.Vehicle()), _owner.Id);
        await _db.SetMemberAsync(_project, _viewer.Id, ProjectRole.Viewer);
        _ownerToken = await Token(_owner);
        _viewerToken = await Token(_viewer);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration["Meridian:PublicUrl"] = "https://meridian.test";
        builder.Services.AddSingleton<IMeridianStore>(_db);
        builder.Services.AddSingleton(_hub);
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddSingleton(new AdminSettings(new SsoOptions(), new SecurityOptions(), new BackupOptions()));
        builder.Services.AddRouting();
        _app = builder.Build();
        _app.UseRouting();
        OslcEndpoints.Map(_app);
        _app.UseEndpoints(_ => { });
        _pipeline = ((IApplicationBuilder)_app).Build();
    }

    private async Task<string> Token(UserAccount user)
    {
        var token = ApiTokens.Generate();
        await _db.CreateApiTokenAsync(user.Id, "test", ApiTokens.Hash(token), ApiTokens.PrefixOf(token));
        return token;
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    private sealed record Reply(int Status, string Body, IHeaderDictionary Headers)
    {
        public XDocument Xml => XDocument.Parse(Body);
    }

    private async Task<Reply> Send(string method, string pathAndQuery, string? token = null, string? accept = null, string? body = null, string? contentType = null, Action<HttpContext>? setup = null)
    {
        var http = new DefaultHttpContext { RequestServices = _app.Services };
        http.Request.Method = method;
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("meridian.test");
        var q = pathAndQuery.IndexOf('?');
        http.Request.Path = q < 0 ? pathAndQuery : pathAndQuery[..q];
        http.Request.QueryString = q < 0 ? QueryString.Empty : new QueryString(pathAndQuery[q..]);
        if (token != null) http.Request.Headers.Authorization = "Bearer " + token;
        if (accept != null) http.Request.Headers.Accept = accept;
        if (body != null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            http.Request.Body = new MemoryStream(bytes);
            http.Request.ContentLength = bytes.Length;
            http.Request.ContentType = contentType;
        }
        setup?.Invoke(http);
        var response = new MemoryStream();
        http.Response.Body = response;
        await _pipeline(http);
        return new Reply(http.Response.StatusCode, Encoding.UTF8.GetString(response.ToArray()), http.Response.Headers);
    }

    private string Base => $"/oslc/projects/{_project}";

    [Fact]
    public async Task Root_Services_Are_Public_And_Negotiate_Turtle()
    {
        var r = await Send("GET", "/oslc/rootservices", accept: "text/turtle");
        Assert.Equal(200, r.Status);
        Assert.StartsWith("text/turtle", r.Headers.ContentType.ToString());
        Assert.Contains("oslc_rm1:rmServiceProviders <https://meridian.test/oslc/catalog>", r.Body);
        Assert.Equal("2.0", r.Headers["OSLC-Core-Version"].ToString());
        var shape = await Send("GET", "/oslc/shapes/requirement", accept: "application/ld+json");
        Assert.Equal("oslc:ResourceShape", (string)JsonNode.Parse(shape.Body)!["@graph"]!.AsArray().First(n => (string?)n!["@id"] == "https://meridian.test/oslc/shapes/requirement")!["@type"]!);
    }

    [Fact]
    public async Task Catalog_And_Queries_Require_Authentication()
    {
        var r = await Send("GET", "/oslc/catalog");
        Assert.Equal(401, r.Status);
        Assert.Contains("Basic", r.Headers.WWWAuthenticate.ToString());
        Assert.Equal(401, (await Send("GET", Base + "/requirements")).Status);
        var ok = await Send("GET", "/oslc/catalog", _viewerToken);
        Assert.Contains($"https://meridian.test/oslc/projects/{_project}/provider", ok.Body);
        Assert.Equal(404, (await Send("GET", $"/oslc/projects/{Guid.NewGuid()}/requirements", _viewerToken)).Status);
    }

    [Fact]
    public async Task Query_Supports_Where_Select_Paging_And_Json_Ld()
    {
        var r = await Send("GET", Base + "/requirements?oslc.where=" + Uri.EscapeDataString("dcterms:identifier in [\"REQ-001\",\"REQ-002\",\"REQ-003\"]")
                                   + "&oslc.select=dcterms:identifier&oslc.pageSize=2&oslc.orderBy=%2Bdcterms:identifier", _viewerToken, "application/ld+json");
        Assert.Equal(200, r.Status);
        var json = JsonNode.Parse(r.Body)!;
        var members = json["rdfs:member"]!.AsArray();
        Assert.Equal(["REQ-001", "REQ-002"], members.Select(m => (string)m!["dcterms:identifier"]!));
        Assert.Null(members[0]!["dcterms:title"]);   // not selected
        Assert.Equal(3, (int)json["oslc:responseInfo"]!["oslc:totalCount"]!);
        Assert.Contains("page=2", (string)json["oslc:responseInfo"]!["oslc:nextPage"]!["@id"]!);

        var ttl = await Send("GET", Base + "/requirements?oslc.where=" + Uri.EscapeDataString("dcterms:identifier=\"REQ-004\""), _viewerToken, "text/turtle");
        Assert.Contains("a oslc_rm:Requirement", ttl.Body);
        Assert.Contains("dcterms:identifier \"REQ-004\"", ttl.Body);
    }

    [Fact]
    public async Task Bad_Query_Gives_An_Oslc_Error()
    {
        var r = await Send("GET", Base + "/requirements?oslc.where=" + Uri.EscapeDataString("dcterms:title=\"a\" or dcterms:title=\"b\""), _viewerToken);
        Assert.Equal(400, r.Status);
        var g = RdfXmlParser.Parse(r.Body);
        var error = g.OfType(Oslc.Core.NamespaceName + "Error").Single();
        Assert.Equal("400", g.Value(error, Oslc.Core.NamespaceName + "statusCode"));
        Assert.Contains("and", g.Value(error, Oslc.Core.NamespaceName + "message"));
    }

    [Fact]
    public async Task Requirement_Resource_Compact_Etag_And_Browser_Redirect()
    {
        var model = ProjectFile.Deserialize((await _db.LoadProjectAsync(_project))!.Value.ModelJson);
        var req = model.OfKind(ElementKind.Requirement).Single(e => e.Get(FieldKeys.RequirementId) == "REQ-002");
        var url = $"{Base}/requirements/{req.Id}";

        var rdf = await Send("GET", url, _viewerToken, "application/rdf+xml");
        Assert.Equal(200, rdf.Status);
        var etag = rdf.Headers.ETag.ToString();
        Assert.NotEmpty(etag);
        Assert.Equal(304, (await Send("GET", url, _viewerToken, "application/rdf+xml", setup: h => h.Request.Headers.IfNoneMatch = etag)).Status);

        var compact = await Send("GET", url, _viewerToken, Oslc.CompactXml);
        Assert.StartsWith(Oslc.CompactXml, compact.Headers.ContentType.ToString());
        Assert.Contains($"/oslc/projects/{_project}/preview/{req.Id}?size=small", compact.Body);

        var browser = await Send("GET", url, _viewerToken, "text/html,application/xhtml+xml,*/*;q=0.8");
        Assert.Equal(302, browser.Status);
        Assert.Equal($"https://meridian.test/?project={_project}&element={req.Id}", browser.Headers.Location.ToString());

        var preview = await Send("GET", $"{Base}/preview/{req.Id}?size=large", _viewerToken);
        Assert.Contains("Acceleration", preview.Body);
        Assert.Contains("Satisfied by", preview.Body);
        Assert.Contains("default-src 'none'", preview.Headers.ContentSecurityPolicy.ToString());

        var am = await Send("GET", $"{Base}/resources?oslc.where=" + Uri.EscapeDataString("dcterms:title=\"Vehicle\" and dcterms:type=\"Block\""), _viewerToken, "application/ld+json");
        Assert.Contains("oslc_am:Resource", am.Body);
    }

    [Fact]
    public async Task Creation_Factory_Needs_An_Editor_Token_And_Creates_In_The_Live_Model()
    {
        const string body = """{"dcterms:title":"Hill start","dcterms:identifier":"REQ-900","dcterms:description":"Hold on 20 % grade."}""";
        Assert.Equal(401, (await Send("POST", Base + "/requirements", body: body, contentType: "application/json")).Status);   // cookie-only / anonymous refused
        Assert.Equal(403, (await Send("POST", Base + "/requirements", _viewerToken, body: body, contentType: "application/json")).Status);

        var r = await Send("POST", Base + "/requirements", _ownerToken, "application/rdf+xml", body, "application/json");
        Assert.Equal(201, r.Status);
        var location = r.Headers.Location.ToString();
        Assert.StartsWith($"https://meridian.test/oslc/projects/{_project}/requirements/", location);
        Assert.Contains("Hill start", r.Body);
        var saved = ProjectFile.Deserialize((await _db.LoadProjectAsync(_project))!.Value.ModelJson);
        Assert.Contains(saved.OfKind(ElementKind.Requirement), e => e.Get(FieldKeys.RequirementId) == "REQ-900" && e.Name == "Hill start");

        Assert.Equal(409, (await Send("POST", Base + "/requirements", _ownerToken, body: body, contentType: "application/json")).Status);
    }

    [Fact]
    public async Task Creation_Dialog_Checks_Its_Token_And_Answers_With_The_New_Resource()
    {
        Assert.Equal(403, (await Send("GET", Base + "/creator", _viewerToken)).Status);
        var page = await Send("GET", Base + "/creator", _ownerToken);
        Assert.Equal(200, page.Status);
        var token = System.Text.RegularExpressions.Regex.Match(page.Body, "name=\"token\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);

        var forged = await Send("POST", Base + "/creator", _ownerToken, body: "title=Forged&token=nope", contentType: "application/x-www-form-urlencoded");
        Assert.Equal(400, forged.Status);
        Assert.Contains("expired", forged.Body);

        var created = await Send("POST", Base + "/creator", _ownerToken, body: $"title=Lane+change&identifier=REQ-950&text=Signal+first&token={Uri.EscapeDataString(token)}", contentType: "application/x-www-form-urlencoded");
        Assert.Equal(200, created.Status);
        Assert.Contains("REQ-950 Lane change", created.Body);
        Assert.Contains("respond([", created.Body);
        var saved = ProjectFile.Deserialize((await _db.LoadProjectAsync(_project))!.Value.ModelJson);
        Assert.Contains(saved.OfKind(ElementKind.Requirement), e => e.Get(FieldKeys.RequirementId) == "REQ-950");
    }

    [Fact]
    public async Task Selection_Dialog_Lists_Model_Elements_For_Am()
    {
        var r = await Send("GET", Base + "/selector?type=resource&q=Engine", _viewerToken);
        Assert.Equal(200, r.Status);
        Assert.Contains("Engine", r.Body);
        Assert.Contains($"/oslc/projects/{_project}/resources/", r.Body);
        Assert.DoesNotContain("REQ-001", r.Body);
    }
}
