using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Services;
using Microsoft.AspNetCore.DataProtection;

namespace Meridian.Web.Integrations;

/// <summary>
/// The OSLC 2.0 provider endpoints under <c>/oslc</c> (team servers only): discovery, resource shapes, RM and AM query
/// capabilities, resources with Compact previews, a creation factory and the delegated selection / creation dialogs.
/// Every RDF resource is served as RDF/XML, Turtle or JSON-LD by <c>Accept</c>.
/// </summary>
public static class OslcEndpoints
{
    /// <summary>Largest creation request body accepted.</summary>
    public const int MaxCreateBytes = 1 << 20;

    public static void Map(WebApplication app)
    {
        var oslc = app.MapGroup("/oslc");

        oslc.MapGet("/rootservices", (HttpContext http) => Rdf(http, Oslc.RootServices(UrlsFor(http), "Meridian")));

        oslc.MapGet("/shapes/{name}", (HttpContext http, string name) =>
            Oslc.Shape(UrlsFor(http), name) is { } shape ? Rdf(http, shape) : Error(http, 404, $"No resource shape “{name}”."));

        oslc.MapGet("/catalog", async (HttpContext http, IMeridianStore db) =>
        {
            var user = await ApiAuth.UserAsync(http, db);
            if (user is null) return Challenge(http);
            var projects = await db.ListProjectsAsync(user.Id);
            return Rdf(http, Oslc.Catalog(UrlsFor(http), "Meridian", projects.Select(p => (p.Id, p.Name))));
        });

        oslc.MapGet("/projects/{projectId:guid}/provider", async (HttpContext http, IMeridianStore db, CollaborationHub hub, Guid projectId) =>
        {
            var user = await ApiAuth.UserAsync(http, db);
            if (user is null) return Challenge(http);
            if (await db.RoleOfAsync(projectId, user.Id) is null || await db.LoadProjectAsync(projectId) is not { } row) return Error(http, 404, "No such project.");
            return Rdf(http, Oslc.Provider(UrlsFor(http), projectId, hub.Find(projectId)?.Name ?? row.Name));
        });

        // ------------------------------------------------------------ requirements (RM)

        oslc.MapGet("/projects/{projectId:guid}/requirements", async (HttpContext http, IMeridianStore db, CollaborationHub hub, Guid projectId) =>
        {
            var p = await Project(http, db, hub, projectId);
            if (p.Error != null) return p.Error;
            var u = UrlsFor(http);
            var q = http.Request.Query;
            List<Oslc.Req> all;
            IReadOnlySet<string>? select;
            try
            {
                all = Oslc.Filter(Oslc.Requirements(p.Model!, u, projectId), q["oslc.where"], q["oslc.searchTerms"], q["oslc.prefix"], q["oslc.orderBy"]).ToList();
                select = Select(q);
            }
            catch (OslcQueryException ex) { return Error(http, 400, ex.Message); }
            var (page, next) = Page(http, all, u.Query(projectId));
            var requestUrl = u.Query(projectId) + http.Request.QueryString;
            var xml = Oslc.QueryRdf(u.Query(projectId), requestUrl, page, all.Count, next, u.Provider(projectId), select);
            return Rdf(http, xml, () => Oslc.QueryJsonLd(u.Query(projectId), requestUrl, page, all.Count, next, u.Provider(projectId), select));
        });

        // Creation factory: POST an oslc_rm:Requirement as RDF/XML or JSON(-LD). Machine clients only (a token or Basic
        // credentials in the Authorization header): a browser cookie alone is refused so other sites cannot forge it.
        oslc.MapPost("/projects/{projectId:guid}/requirements", async (HttpContext http, IMeridianStore db, CollaborationHub hub, Guid projectId) =>
        {
            if (string.IsNullOrEmpty(http.Request.Headers.Authorization)) return Error(http, 401, "Send a personal access token (Authorization: Bearer mrd_…) or Basic credentials to create requirements.", challenge: true);
            var user = await ApiAuth.UserAsync(http, db);
            if (user is null) return Challenge(http);
            if (await db.RoleOfAsync(projectId, user.Id) is not { } role) return Error(http, 404, "No such project.");
            if (role < ProjectRole.Editor) return Error(http, 403, "Creating requirements needs the editor role.");
            var body = await ReadBodyAsync(http);
            if (body is null) return Error(http, 413, "The request body is too large.");
            var u = UrlsFor(http);
            Oslc.NewRequirement request;
            try { request = Oslc.ParseCreation(body, http.Request.ContentType, u.Query(projectId)); }
            catch (RdfFormatException ex) { return Error(http, 400, ex.Message); }
            var created = await CreateAsync(hub, u, projectId, user, request, http.Request.Query["package"]);
            if (created.Error != null) return Error(http, created.Status, created.Error);
            http.Response.Headers.Location = created.Req!.Url;
            http.Response.StatusCode = 201;
            return Rdf(http, Oslc.RequirementRdf(created.Req, u.Provider(projectId)), () => Oslc.RequirementJsonLd(created.Req, u.Provider(projectId)), status: 201);
        }).DisableAntiforgery();

        oslc.MapGet("/projects/{projectId:guid}/requirements/{elementId}", async (HttpContext http, IMeridianStore db, CollaborationHub hub, Guid projectId, string elementId) =>
        {
            var p = await Project(http, db, hub, projectId);
            if (p.Error != null) return p.Error;
            if (p.Model!.Find(elementId) is not { Kind: ElementKind.Requirement } e) return Error(http, 404, "No such requirement.");
            var u = UrlsFor(http);
            var r = Oslc.Describe(p.Model, u, projectId, e);
            var format = RdfNegotiation.Choose(http.Request.Headers.Accept, allowHtml: true, allowCompact: true);
            if (format == RdfFormat.Html) return Results.Redirect(r.WebUrl);
            if (format == RdfFormat.CompactXml)
                return Rdf(http, Oslc.Compact(r, u.Preview(projectId, e.Id), u.Preview(projectId, e.Id, "large"), u.Icon), format: RdfFormat.CompactXml, etag: true);
            IReadOnlySet<string>? select;
            try { select = Select(http.Request.Query); }
            catch (OslcQueryException ex) { return Error(http, 400, ex.Message); }
            return Rdf(http, Oslc.RequirementRdf(r, u.Provider(projectId), select), () => Oslc.RequirementJsonLd(r, u.Provider(projectId), select), etag: true);
        });

        // ------------------------------------------------------------ architecture elements (AM)

        oslc.MapGet("/projects/{projectId:guid}/resources", async (HttpContext http, IMeridianStore db, CollaborationHub hub, Guid projectId) =>
        {
            var p = await Project(http, db, hub, projectId);
            if (p.Error != null) return p.Error;
            var u = UrlsFor(http);
            var q = http.Request.Query;
            List<Oslc.AmRes> all;
            IReadOnlySet<string>? select;
            try
            {
                all = Oslc.FilterAm(Oslc.AmResources(p.Model!, u, projectId), q["oslc.where"], q["oslc.searchTerms"], q["oslc.prefix"], q["oslc.orderBy"]).ToList();
                select = Select(q);
            }
            catch (OslcQueryException ex) { return Error(http, 400, ex.Message); }
            var (page, next) = Page(http, all, u.AmQuery(projectId));
            return Rdf(http, Oslc.AmQueryRdf(u.AmQuery(projectId), u.AmQuery(projectId) + http.Request.QueryString, page, all.Count, next, u.Provider(projectId), select));
        });

        oslc.MapGet("/projects/{projectId:guid}/resources/{elementId}", async (HttpContext http, IMeridianStore db, CollaborationHub hub, Guid projectId, string elementId) =>
        {
            var p = await Project(http, db, hub, projectId);
            if (p.Error != null) return p.Error;
            var u = UrlsFor(http);
            if (p.Model!.Find(elementId) is not { } e) return Error(http, 404, "No such element.");
            if (e.Kind == ElementKind.Requirement) return Results.Redirect(u.Requirement(projectId, e.Id));
            if (!Oslc.IsAmResource(p.Model, e)) return Error(http, 404, "That element is not published as an OSLC resource.");
            var r = Oslc.DescribeAm(p.Model, u, projectId, e);
            var format = RdfNegotiation.Choose(http.Request.Headers.Accept, allowHtml: true, allowCompact: true);
            if (format == RdfFormat.Html) return Results.Redirect(r.WebUrl);
            if (format == RdfFormat.CompactXml)
                return Rdf(http, Oslc.Compact(r.Url, r.Title, r.Type, u.Preview(projectId, e.Id), u.Preview(projectId, e.Id, "large"), u.Icon), format: RdfFormat.CompactXml, etag: true);
            IReadOnlySet<string>? select;
            try { select = Select(http.Request.Query); }
            catch (OslcQueryException ex) { return Error(http, 400, ex.Message); }
            return Rdf(http, Oslc.AmRdf(r, u.Provider(projectId), select), etag: true);
        });

        // ------------------------------------------------------------ delegated UI

        oslc.MapGet("/projects/{projectId:guid}/preview/{elementId}", async (HttpContext http, IMeridianStore db, CollaborationHub hub, Guid projectId, string elementId) =>
        {
            var p = await Project(http, db, hub, projectId);
            if (p.Error != null) return p.Error;
            if (p.Model!.Find(elementId) is not { } e || e is Relationship || e.Kind == ElementKind.Diagram) return Results.NotFound();
            var u = UrlsFor(http);
            var large = string.Equals(http.Request.Query["size"], "large", StringComparison.OrdinalIgnoreCase);
            return Html(http, PreviewOf(p.Model, p.Name!, u, projectId, e, large));
        });

        oslc.MapGet("/projects/{projectId:guid}/selector", async (HttpContext http, IMeridianStore db, CollaborationHub hub, Guid projectId) =>
        {
            var p = await Project(http, db, hub, projectId);
            if (p.Error != null) return p.Error;
            var u = UrlsFor(http);
            var text = http.Request.Query["q"].ToString();
            if (string.Equals(http.Request.Query["type"], "resource", StringComparison.OrdinalIgnoreCase))
            {
                var res = Oslc.FilterAm(Oslc.AmResources(p.Model!, u, projectId), null, text).Take(300).Select(r => new OslcPages.Choice(r.Url, "", r.Title, r.Type)).ToList();
                return Html(http, OslcPages.Selector(p.Name!, text, "model elements", res));
            }
            return Html(http, SelectorPage(p.Name!, text, Oslc.Filter(Oslc.Requirements(p.Model!, u, projectId), null, text).Take(300).ToList()));
        });

        oslc.MapGet("/projects/{projectId:guid}/creator", async (HttpContext http, IMeridianStore db, CollaborationHub hub, IDataProtectionProvider protection, Guid projectId) =>
        {
            var p = await Project(http, db, hub, projectId, ProjectRole.Editor);
            if (p.Error != null) return p.Error;
            return Html(http, OslcPages.Creator(p.Name!, Packages(p.Model!), CreatorToken(protection, p.User!.Id, projectId)));
        });

        oslc.MapPost("/projects/{projectId:guid}/creator", async (HttpContext http, IMeridianStore db, CollaborationHub hub, IDataProtectionProvider protection, Guid projectId) =>
        {
            var p = await Project(http, db, hub, projectId, ProjectRole.Editor);
            if (p.Error != null) return p.Error;
            if (!http.Request.HasFormContentType) return Results.BadRequest();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var (title, id, text, package) = (form["title"].ToString().Trim(), form["identifier"].ToString().Trim(), form["text"].ToString().Trim(), form["package"].ToString());
            var token = CreatorToken(protection, p.User!.Id, projectId);
            var packages = Packages(p.Model!);
            if (!CreatorTokenValid(protection, form["token"].ToString(), p.User.Id, projectId))
                return Html(http, OslcPages.Creator(p.Name!, packages, token, "The form expired — please submit it again.", values: (title, id, text, package)), 400);
            if (title.Length == 0 || title.Length > 500 || id.Length > 100 || text.Length > 20000)
                return Html(http, OslcPages.Creator(p.Name!, packages, token, "Enter a title (at most 500 characters).", values: (title, id, text, package)), 400);
            var created = await CreateAsync(hub, UrlsFor(http), projectId, p.User, new Oslc.NewRequirement(title, id.Length > 0 ? id : null, text.Length > 0 ? text : null), package);
            if (created.Error != null)
                return Html(http, OslcPages.Creator(p.Name!, packages, token, created.Error, values: (title, id, text, package)), created.Status);
            return Html(http, OslcPages.Creator(p.Name!, packages, token, created: (created.Req!.Url, $"{created.Req.Identifier} {created.Req.Title}")));
        }).DisableAntiforgery();   // protected by the per-user, per-project token in the form instead
    }

    // ================================================================ helpers

    internal static Oslc.Urls UrlsFor(HttpContext http)
    {
        var configured = http.RequestServices.GetRequiredService<IConfiguration>()["Meridian:PublicUrl"];
        return new Oslc.Urls(configured is { Length: > 0 } ? configured.TrimEnd('/') : $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}".TrimEnd('/'));
    }

    /// <summary>oslc.select, or oslc.properties on single resources.</summary>
    private static IReadOnlySet<string>? Select(IQueryCollection q)
    {
        var prefixes = OslcQuery.Prefixes(q["oslc.prefix"]);
        return q.ContainsKey("oslc.select") ? OslcQuery.ParseSelect(q["oslc.select"], prefixes)
            : q.ContainsKey("oslc.properties") ? OslcQuery.ParseSelect(q["oslc.properties"], prefixes, "oslc.properties")
            : null;
    }

    private static (List<T> Page, string? Next) Page<T>(HttpContext http, List<T> all, string queryBase)
    {
        var q = http.Request.Query;
        var paging = q["oslc.paging"] == "true" || q.ContainsKey("oslc.pageSize");
        if (!paging) return (all, null);
        var size = int.TryParse(q["oslc.pageSize"], out var ps) && ps > 0 ? Math.Min(ps, 1000) : 100;
        var pageNo = int.TryParse(q["page"], out var pn) && pn > 0 ? pn : 1;
        var page = all.Skip((pageNo - 1) * size).Take(size).ToList();
        string? next = null;
        if (pageNo * size < all.Count)
        {
            var parts = q.Where(kv => kv.Key != "page").Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value.ToString())}").Append($"page={pageNo + 1}");
            next = $"{queryBase}?{string.Join("&", parts)}";
        }
        return (page, next);
    }

    /// <summary>Serializes an RDF/XML document in the negotiated format (JSON-LD may come from a hand-built document).</summary>
    private static IResult Rdf(HttpContext http, XDocument doc, Func<JsonObject>? jsonLd = null, RdfFormat? format = null, bool etag = false, int status = 200)
    {
        http.Response.Headers["OSLC-Core-Version"] = "2.0";
        http.Response.Headers.Vary = "Accept";
        var f = format ?? RdfNegotiation.Choose(http.Request.Headers.Accept);
        string body, type;
        switch (f)
        {
            case RdfFormat.Turtle:
                body = Turtle.Write(RdfXmlParser.Parse(doc));
                type = Turtle.MediaType;
                break;
            case RdfFormat.JsonLd:
                body = (jsonLd?.Invoke() ?? JsonLd.Write(RdfXmlParser.Parse(doc))).ToJsonString();
                type = JsonLd.MediaType;
                break;
            default:
                var sb = new StringBuilder();
                using (var w = new Utf8StringWriter(sb)) doc.Save(w);
                body = sb.ToString();
                type = f == RdfFormat.CompactXml ? Oslc.CompactXml : Oslc.RdfXml;
                break;
        }
        if (etag)
        {
            var tag = "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)), 0, 12).ToLowerInvariant() + "\"";
            http.Response.Headers.ETag = tag;
            if (http.Request.Headers.IfNoneMatch.ToString().Split(',').Any(t => t.Trim() == tag)) return Results.StatusCode(304);
        }
        return Results.Content(body, type + "; charset=utf-8", Encoding.UTF8, status);
    }

    private static IResult Error(HttpContext http, int status, string message, bool challenge = false)
    {
        if (challenge || status == 401) http.Response.Headers.WWWAuthenticate = "Basic realm=\"Meridian\"";
        return Rdf(http, Oslc.Error(status, message), status: status);
    }

    private static IResult Html(HttpContext http, string html, int status = 200)
    {
        http.Response.Headers["Content-Security-Policy"] = OslcPages.DialogCsp;
        http.Response.Headers.CacheControl = "no-store";
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);
    }

    private sealed class Utf8StringWriter(StringBuilder sb) : StringWriter(sb)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }

    private static IResult Challenge(HttpContext http)
    {
        http.Response.Headers.WWWAuthenticate = "Basic realm=\"Meridian\"";
        return Results.Text("Authentication required: sign in, or send a personal access token as 'Authorization: Bearer mrd_…'.", statusCode: 401);
    }

    private sealed record Loaded(UserAccount? User, string? Name, ModelRepository? Model, IResult? Error);

    /// <summary>Loads the live model of a project the caller may read (or edit, with <paramref name="minimum"/>).</summary>
    private static async Task<Loaded> Project(HttpContext http, IMeridianStore db, CollaborationHub hub, Guid projectId, ProjectRole minimum = ProjectRole.Viewer)
    {
        var user = await ApiAuth.UserAsync(http, db);
        if (user is null) return new(null, null, null, Challenge(http));
        var role = await db.RoleOfAsync(projectId, user.Id);
        if (role is null) return new(user, null, null, Results.NotFound());
        if (role < minimum) return new(user, null, null, Results.Text("This needs the editor role in the project.", statusCode: 403));
        var row = await db.LoadProjectAsync(projectId);
        if (row is null) return new(user, null, null, Results.NotFound());
        var json = hub.Find(projectId)?.Snapshot() ?? row.Value.ModelJson;
        var model = ProjectFile.Deserialize(json);
        // Package permissions: packages hidden from the caller are left out (docs/PERMISSIONS.md).
        Meridian.Core.Permissions.ModelRedactor.Redact(model, await PackagePermissions.ViewForAsync(db, projectId, user.Id));
        return new(user, hub.Find(projectId)?.Name ?? row.Value.Name, model, null);
    }

    private static async Task<string?> ReadBodyAsync(HttpContext http)
    {
        if (http.Request.ContentLength > MaxCreateBytes) return null;
        var bytes = await CodeSync.Hosts.CodeSyncHostsSetup.ReadBodyAsync(http.Request.Body, MaxCreateBytes, http.RequestAborted);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    private sealed record Created(Oslc.Req? Req, string? Error, int Status);

    private static async Task<Created> CreateAsync(CollaborationHub hub, Oslc.Urls u, Guid projectId, UserAccount user, Oslc.NewRequirement request, string? packageId)
    {
        await using var edit = await ServerModelEdit.OpenAsync(hub, projectId, user);
        if (edit is null) return new(null, "No such project.", 404);
        try
        {
            return await edit.EditAsync(model => new Created(Oslc.Describe(model, u, projectId, Oslc.Create(model, request, packageId)), null, 201));
        }
        catch (InvalidOperationException ex) { return new(null, ex.Message, 409); }
    }

    private static IReadOnlyList<(string Id, string Name)> Packages(ModelRepository model) =>
        model.OfKind(ElementKind.Package).Select(p => (p.Id, p.QualifiedName)).OrderBy(p => p.QualifiedName, StringComparer.OrdinalIgnoreCase)
            .Prepend((model.Root.Id, model.Root.DisplayName + " (top level)"))
            .OrderByDescending(p => string.Equals(model.Find(p.Item1)?.Name, "Requirements", StringComparison.OrdinalIgnoreCase)).ToList();

    private const string CreatorPurpose = "Meridian.Oslc.CreationDialog.v1";

    internal static string CreatorToken(IDataProtectionProvider protection, Guid userId, Guid projectId) =>
        protection.CreateProtector(CreatorPurpose).ToTimeLimitedDataProtector().Protect($"{userId:N}|{projectId:N}", TimeSpan.FromHours(2));

    internal static bool CreatorTokenValid(IDataProtectionProvider protection, string token, Guid userId, Guid projectId)
    {
        if (string.IsNullOrEmpty(token)) return false;
        try { return protection.CreateProtector(CreatorPurpose).ToTimeLimitedDataProtector().Unprotect(token) == $"{userId:N}|{projectId:N}"; }
        catch (CryptographicException) { return false; }
    }

    private static string PreviewOf(ModelRepository model, string projectName, Oslc.Urls u, Guid projectId, Element e, bool large)
    {
        static string Label(Element x) => x.Kind == ElementKind.Requirement && x.Get(FieldKeys.RequirementId) is { Length: > 0 } id ? $"{id} {x.DisplayName}" : x.DisplayName;
        var kind = Meridian.Core.Language.Metamodel.DisplayName(e.Kind);
        if (e.Kind == ElementKind.Requirement)
        {
            var text = string.Join("\n\n", new[] { e.Get(FieldKeys.Text), e.Documentation }.Where(s => s.Length > 0));
            return OslcPages.Preview(projectName, kind, e.Get(FieldKeys.RequirementId), e.DisplayName, text, u.Element(projectId, e.Id), large,
            [
                ("Satisfied by", model.Incoming(e, ElementKind.Satisfy).Select(r => Label(r.Source)).ToList()),
                ("Verified by", model.Incoming(e, ElementKind.Verify).Select(r => Label(r.Source)).ToList()),
                ("Traced to", model.RelationshipsOf(e).Where(r => r.Kind == ElementKind.Trace).Select(r => Label(ReferenceEquals(r.Source, e) ? r.Target : r.Source)).ToList()),
            ]);
        }
        return OslcPages.Preview(projectName, kind, "", e.DisplayName, e.Documentation, u.Element(projectId, e.Id), large,
        [
            ("Satisfies", model.Outgoing(e, ElementKind.Satisfy).Select(r => Label(r.Target)).ToList()),
            ("Verifies", model.Outgoing(e, ElementKind.Verify).Select(r => Label(r.Target)).ToList()),
        ]);
    }

    /// <summary>The requirement selection dialog.</summary>
    internal static string SelectorPage(string projectName, string query, IReadOnlyList<Oslc.Req> reqs) =>
        OslcPages.Selector(projectName, query, "requirements", reqs.Select(r => new OslcPages.Choice(r.Url, r.Identifier, r.Title)).ToList());
}
