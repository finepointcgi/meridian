using System.Net;
using System.Text;

namespace Meridian.Web.Tests;

/// <summary>
/// A scripted HTTP server: routes by method and URL, records every request (with its body), and can be changed between
/// calls to simulate edits on the remote side. No network access.
/// </summary>
public sealed class FakeServer : HttpMessageHandler
{
    public delegate HttpResponseMessage? Route(HttpRequestMessage request, string? body);

    private readonly List<Route> _routes = new();
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

    public FakeServer On(HttpMethod method, Func<Uri, bool> match, Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
    {
        _routes.Insert(0, (r, b) => r.Method == method && match(r.RequestUri!) ? respond(r, b) : null);
        return this;
    }

    public FakeServer Get(string url, Func<HttpResponseMessage> respond) => On(HttpMethod.Get, u => u.AbsoluteUri == url, (_, _) => respond());

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        lock (Requests) Requests.Add((request, body));
        foreach (var route in _routes)
            if (route(request, body) is { } response) return response;
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"No fixture for {request.Method} {request.RequestUri}") };
    }

    public static HttpResponseMessage Rdf(string xml, string? etag = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/rdf+xml") };
        if (etag != null) r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return r;
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/vnd.api+json") };

    public static HttpResponseMessage Redirect(string location, params string[] setCookies)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        foreach (var c in setCookies) r.Headers.TryAddWithoutValidation("Set-Cookie", c);
        return r;
    }
}

/// <summary>
/// Hand-written, recorded-style responses of an IBM DOORS Next 7.x server (jazz.example.test) and a Polarion 2410
/// REST API (polarion.example.test), trimmed to what Meridian reads plus typical noise it must ignore.
/// </summary>
public static class Fixtures
{
    public const string Jazz = "https://jazz.example.test";
    public const string Rm = Jazz + "/rm";
    public const string Catalog = Rm + "/oslc_rm/catalog";
    public const string Provider1 = Rm + "/oslc_rm/_aB3dEfProjArea1/services.xml";
    public const string Provider2 = Rm + "/oslc_rm/_kL9mNoProjArea2/services.xml";
    public const string QueryBase = Rm + "/views?oslc.query=true&projectURL=https%3A%2F%2Fjazz.example.test%2Frm%2Fprocess%2Fproject-areas%2F_aB3dEfProjArea1";
    public const string Module = Rm + "/resources/_MOD1sysreq";
    public const string R1 = Rm + "/resources/_R1aaaLaneKeep";
    public const string R2 = Rm + "/resources/_R2bbbBrakeDist";
    public const string R3 = Rm + "/resources/_R3cccDriverAlert";

    public const string RootServices = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rdf:Description rdf:about="https://jazz.example.test/rm/rootservices"
            xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
            xmlns:dc="http://purl.org/dc/terms/"
            xmlns:jfs="http://jazz.net/xmlns/prod/jazz/jfs/1.0/"
            xmlns:jd="http://jazz.net/xmlns/prod/jazz/discovery/1.0/"
            xmlns:oslc_rm="http://open-services.net/xmlns/rm/1.0/"
            xmlns:oslc_config="http://open-services.net/ns/config#">
          <dc:title xml:lang="en">Requirements Management</dc:title>
          <oslc_rm:rmServiceProviders rdf:resource="https://jazz.example.test/rm/oslc_rm/catalog"/>
          <oslc_config:cmServiceProviders rdf:resource="https://jazz.example.test/rm/oslc_config"/>
          <jfs:oauthRealmName>Jazz</jfs:oauthRealmName>
          <jfs:oauthDomain>https://jazz.example.test/jts</jfs:oauthDomain>
          <jfs:oauthRequestConsumerKeyUrl rdf:resource="https://jazz.example.test/jts/oauth-request-consumer"/>
          <jfs:oauthRequestTokenUrl rdf:resource="https://jazz.example.test/jts/oauth-request-token"/>
          <jfs:oauthUserAuthorizationUrl rdf:resource="https://jazz.example.test/jts/oauth-authorize"/>
          <jfs:oauthAccessTokenUrl rdf:resource="https://jazz.example.test/jts/oauth-access-token"/>
          <jd:registration rdf:resource="https://jazz.example.test/rm/service/com.ibm.team.repository.service.internal.setup.IRegistrationHandlerService"/>
        </rdf:Description>
        """;

    public const string CatalogRdf = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rdf:RDF
            xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
            xmlns:dcterms="http://purl.org/dc/terms/"
            xmlns:oslc="http://open-services.net/ns/core#"
            xmlns:jp06="http://jazz.net/xmlns/prod/jazz/process/0.6/">
          <oslc:ServiceProviderCatalog rdf:about="https://jazz.example.test/rm/oslc_rm/catalog">
            <dcterms:title rdf:parseType="Literal">Project Areas</dcterms:title>
            <oslc:domain rdf:resource="http://open-services.net/ns/rm#"/>
            <oslc:serviceProvider>
              <oslc:ServiceProvider rdf:about="https://jazz.example.test/rm/oslc_rm/_aB3dEfProjArea1/services.xml">
                <dcterms:title rdf:parseType="Literal">Drive Assist (Requirements)</dcterms:title>
                <jp06:projectArea rdf:resource="https://jazz.example.test/rm/process/project-areas/_aB3dEfProjArea1"/>
                <oslc:details rdf:resource="https://jazz.example.test/rm/process/project-areas/_aB3dEfProjArea1"/>
              </oslc:ServiceProvider>
            </oslc:serviceProvider>
            <oslc:serviceProvider>
              <oslc:ServiceProvider rdf:about="https://jazz.example.test/rm/oslc_rm/_kL9mNoProjArea2/services.xml">
                <dcterms:title rdf:parseType="Literal">Braking Systems</dcterms:title>
                <jp06:projectArea rdf:resource="https://jazz.example.test/rm/process/project-areas/_kL9mNoProjArea2"/>
              </oslc:ServiceProvider>
            </oslc:serviceProvider>
          </oslc:ServiceProviderCatalog>
        </rdf:RDF>
        """;

    public const string ServicesRdf = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rdf:RDF
            xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
            xmlns:dcterms="http://purl.org/dc/terms/"
            xmlns:oslc="http://open-services.net/ns/core#">
          <oslc:ServiceProvider rdf:about="https://jazz.example.test/rm/oslc_rm/_aB3dEfProjArea1/services.xml">
            <dcterms:title rdf:parseType="Literal">Drive Assist (Requirements)</dcterms:title>
            <oslc:service>
              <oslc:Service>
                <oslc:domain rdf:resource="http://open-services.net/ns/rm#"/>
                <oslc:creationFactory>
                  <oslc:CreationFactory>
                    <dcterms:title rdf:parseType="Literal">Default Requirement Factory</dcterms:title>
                    <oslc:creation rdf:resource="https://jazz.example.test/rm/requirementFactory?projectURL=https%3A%2F%2Fjazz.example.test%2Frm%2Fprocess%2Fproject-areas%2F_aB3dEfProjArea1"/>
                    <oslc:resourceType rdf:resource="http://open-services.net/ns/rm#Requirement"/>
                  </oslc:CreationFactory>
                </oslc:creationFactory>
                <oslc:queryCapability>
                  <oslc:QueryCapability>
                    <dcterms:title rdf:parseType="Literal">Query Capability</dcterms:title>
                    <oslc:resourceType rdf:resource="http://open-services.net/ns/rm#Requirement"/>
                    <oslc:resourceType rdf:resource="http://open-services.net/ns/rm#RequirementCollection"/>
                    <oslc:queryBase rdf:resource="https://jazz.example.test/rm/views?oslc.query=true&amp;projectURL=https%3A%2F%2Fjazz.example.test%2Frm%2Fprocess%2Fproject-areas%2F_aB3dEfProjArea1"/>
                    <oslc:resourceShape rdf:resource="https://jazz.example.test/rm/types/_shapeReq"/>
                  </oslc:QueryCapability>
                </oslc:queryCapability>
                <oslc:selectionDialog>
                  <oslc:Dialog>
                    <dcterms:title rdf:parseType="Literal">Select Requirement</dcterms:title>
                    <oslc:dialog rdf:resource="https://jazz.example.test/rm/pickers/com.ibm.rdm.web.RRCPicker?projectURL=x"/>
                    <oslc:hintWidth>800px</oslc:hintWidth>
                    <oslc:hintHeight>550px</oslc:hintHeight>
                  </oslc:Dialog>
                </oslc:selectionDialog>
              </oslc:Service>
            </oslc:service>
          </oslc:ServiceProvider>
        </rdf:RDF>
        """;

    private const string NsDecl = """
        xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
        xmlns:rdfs="http://www.w3.org/2000/01/rdf-schema#"
        xmlns:dcterms="http://purl.org/dc/terms/"
        xmlns:oslc="http://open-services.net/ns/core#"
        xmlns:oslc_rm="http://open-services.net/ns/rm#"
        xmlns:jazz_rm="http://jazz.net/ns/rm#"
        xmlns:nav="http://jazz.net/ns/rm/navigation#"
        """;

    public static string Requirement(string url, string id, string title, string textXhtml, string modified, params (string Property, string Target)[] links) => $"""
        <oslc_rm:Requirement rdf:about="{url}">
          <dcterms:identifier>{id}</dcterms:identifier>
          <dcterms:title rdf:parseType="Literal">{title}</dcterms:title>
          <dcterms:description rdf:parseType="Literal"></dcterms:description>
          <dcterms:modified rdf:datatype="http://www.w3.org/2001/XMLSchema#dateTime">{modified}</dcterms:modified>
          <dcterms:creator rdf:resource="https://jazz.example.test/jts/users/ann"/>
          <jazz_rm:primaryText rdf:parseType="Literal"><div xmlns="http://www.w3.org/1999/xhtml" id="_text{id}"><p dir="ltr">{textXhtml}</p></div></jazz_rm:primaryText>
          <nav:parent rdf:resource="https://jazz.example.test/rm/folders/_folder1"/>
          <oslc:serviceProvider rdf:resource="https://jazz.example.test/rm/oslc_rm/_aB3dEfProjArea1/services.xml"/>
          {string.Concat(links.Select(l => $"<{l.Property} rdf:resource=\"{l.Target}\"/>"))}
        </oslc_rm:Requirement>
        """;

    public static string Document(params string[] resources) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <rdf:RDF {NsDecl}>
        {string.Concat(resources)}
        </rdf:RDF>
        """;

    /// <summary>A query result page: rdfs:member list, oslc:ResponseInfo with an optional next page, then the members.</summary>
    public static string QueryPage(string pageUrl, string? nextPage, int total, params (string Url, string Body)[] members) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <rdf:RDF {NsDecl}>
          <rdf:Description rdf:about="{System.Security.SecurityElement.Escape(QueryBase)}">
            {string.Concat(members.Select(m => $"<rdfs:member rdf:resource=\"{m.Url}\"/>"))}
          </rdf:Description>
          <oslc:ResponseInfo rdf:about="{System.Security.SecurityElement.Escape(pageUrl)}">
            <dcterms:title>Query Results</dcterms:title>
            <oslc:totalCount>{total}</oslc:totalCount>
            {(nextPage != null ? $"<oslc:nextPage rdf:resource=\"{System.Security.SecurityElement.Escape(nextPage)}\"/>" : "")}
          </oslc:ResponseInfo>
          {string.Concat(members.Select(m => m.Body))}
        </rdf:RDF>
        """;

    public static string R1Body(string title = "Lane keeping above 60 km/h", string text = "The system shall keep the vehicle centred in its lane at speeds above 60&#160;km/h.", string modified = "2026-10-02T09:14:31.112Z") =>
        Requirement(R1, "1201", title, text, modified);

    public static string R2Body(string modified = "2026-10-03T11:02:00.000Z") =>
        Requirement(R2, "1202", "Braking distance", "From 100 km/h the vehicle shall stop within <b>40 m</b> on dry asphalt.", modified);

    public static string R3Body(string modified = "2026-10-04T15:45:10.000Z") =>
        Requirement(R3, "1203", "Driver alert", "The driver shall be alerted &lt;1 s after a lane departure.", modified);

    public const string ModuleList = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <rdf:RDF
            xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
            xmlns:rdfs="http://www.w3.org/2000/01/rdf-schema#"
            xmlns:dcterms="http://purl.org/dc/terms/"
            xmlns:oslc="http://open-services.net/ns/core#"
            xmlns:jazz_rm="http://jazz.net/ns/rm#">
          <rdf:Description rdf:about="https://jazz.example.test/rm/views?oslc.query=true&amp;projectURL=https%3A%2F%2Fjazz.example.test%2Frm%2Fprocess%2Fproject-areas%2F_aB3dEfProjArea1">
            <rdfs:member rdf:resource="https://jazz.example.test/rm/resources/_MOD1sysreq"/>
            <rdfs:member rdf:resource="https://jazz.example.test/rm/resources/_MOD2stakeholder"/>
          </rdf:Description>
          <jazz_rm:Module rdf:about="https://jazz.example.test/rm/resources/_MOD1sysreq">
            <dcterms:title rdf:parseType="Literal">System Requirements</dcterms:title>
          </jazz_rm:Module>
          <jazz_rm:Module rdf:about="https://jazz.example.test/rm/resources/_MOD2stakeholder">
            <dcterms:title rdf:parseType="Literal">Stakeholder Needs</dcterms:title>
          </jazz_rm:Module>
        </rdf:RDF>
        """;

    public const string ModuleRdf = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rdf:RDF
            xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
            xmlns:dcterms="http://purl.org/dc/terms/"
            xmlns:oslc_rm="http://open-services.net/ns/rm#"
            xmlns:jazz_rm="http://jazz.net/ns/rm#">
          <jazz_rm:Module rdf:about="https://jazz.example.test/rm/resources/_MOD1sysreq">
            <rdf:type rdf:resource="http://open-services.net/ns/rm#RequirementCollection"/>
            <dcterms:title rdf:parseType="Literal">System Requirements</dcterms:title>
            <dcterms:identifier>1100</dcterms:identifier>
            <oslc_rm:uses rdf:resource="https://jazz.example.test/rm/resources/_R1aaaLaneKeep"/>
            <oslc_rm:uses rdf:resource="https://jazz.example.test/rm/resources/_R3cccDriverAlert"/>
          </jazz_rm:Module>
        </rdf:RDF>
        """;

    // ---------------------------------------------------------------- Polarion REST (JSON:API)

    public const string Polarion = "https://polarion.example.test";
    public const string PolarionApi = Polarion + "/polarion/rest/v1";

    public const string PolarionProjects = """
        {
          "meta": { "totalCount": 2 },
          "data": [
            { "type": "projects", "id": "drivepilot", "attributes": { "name": "DrivePilot" },
              "links": { "self": "https://polarion.example.test/polarion/rest/v1/projects/drivepilot" } },
            { "type": "projects", "id": "chassis", "attributes": { "name": "Chassis Platform" },
              "links": { "self": "https://polarion.example.test/polarion/rest/v1/projects/chassis" } }
          ],
          "links": {
            "self": "https://polarion.example.test/polarion/rest/v1/projects?fields%5Bprojects%5D=name&page%5Bsize%5D=100&page%5Bnumber%5D=1",
            "first": "https://polarion.example.test/polarion/rest/v1/projects?fields%5Bprojects%5D=name&page%5Bsize%5D=100&page%5Bnumber%5D=1",
            "last": "https://polarion.example.test/polarion/rest/v1/projects?fields%5Bprojects%5D=name&page%5Bsize%5D=100&page%5Bnumber%5D=1"
          }
        }
        """;

    public static string WorkItem(string id, string title, string html, string updated, string document = "drivepilot/Specification/System Requirements") => $$"""
        {
          "type": "workitems",
          "id": "drivepilot/{{id}}",
          "revision": "{{updated[..10]}}",
          "attributes": {
            "title": {{System.Text.Json.JsonSerializer.Serialize(title)}},
            "description": { "type": "text/html", "value": {{System.Text.Json.JsonSerializer.Serialize(html)}} },
            "type": "requirement",
            "status": "approved",
            "updated": "{{updated}}"
          },
          "relationships": {
            "module": { "data": { "type": "documents", "id": {{System.Text.Json.JsonSerializer.Serialize(document)}} } }
          },
          "links": { "self": "https://polarion.example.test/polarion/rest/v1/projects/drivepilot/workitems/{{id}}" }
        }
        """;

    public static string WorkItemPage(int page, bool hasNext, int total, params string[] items) => $$"""
        {
          "meta": { "totalCount": {{total}} },
          "data": [ {{string.Join(",\n", items)}} ],
          "included": [
            { "type": "documents", "id": "drivepilot/Specification/System Requirements",
              "attributes": { "title": "System Requirements", "moduleName": "System Requirements", "moduleFolder": "Specification" } },
            { "type": "documents", "id": "drivepilot/Specification/Safety Goals",
              "attributes": { "title": "Safety Goals", "moduleName": "Safety Goals", "moduleFolder": "Specification" } }
          ],
          "links": {
            "self": "https://polarion.example.test/polarion/rest/v1/projects/drivepilot/workitems?page%5Bsize%5D=2&page%5Bnumber%5D={{page}}"{{(hasNext ? $",\n    \"next\": \"https://polarion.example.test/polarion/rest/v1/projects/drivepilot/workitems?query=type%3Arequirement&page%5Bsize%5D=2&page%5Bnumber%5D={page + 1}\"" : "")}}
          }
        }
        """;

    public const string PolarionUnauthorized = """
        { "errors": [ { "status": "401", "title": "Unauthorized", "detail": "Invalid or expired token" } ] }
        """;
}
