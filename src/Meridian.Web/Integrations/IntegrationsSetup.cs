using System.Net;
using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Services;

namespace Meridian.Web.Integrations;

/// <summary>Authentication for machine clients (/api and /oslc): the browser cookie, a personal access token, or Basic.</summary>
public static class ApiAuth
{
    /// <summary>
    /// <c>Authorization: Bearer mrd_…</c> personal access token, or <c>Basic</c> with the token as the password
    /// (for tools that only speak Basic). Returns null when no valid token is presented.
    /// </summary>
    public static async Task<UserAccount?> TokenUserAsync(HttpContext http, IMeridianStore db)
    {
        var header = http.Request.Headers.Authorization.ToString();
        string? token = null, basicUser = null;
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = header[7..].Trim();
        else if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var raw = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                var colon = raw.IndexOf(':');
                if (colon >= 0 && raw[(colon + 1)..].StartsWith(ApiTokens.Marker, StringComparison.Ordinal)) { basicUser = raw[..colon]; token = raw[(colon + 1)..]; }
            }
            catch (FormatException) { }
        }
        if (token is null || !token.StartsWith(ApiTokens.Marker, StringComparison.Ordinal)) return null;
        var user = await db.FindUserByApiTokenAsync(ApiTokens.Hash(token));
        if (user != null && basicUser is { Length: > 0 } && !string.Equals(basicUser.Trim(), user.Username, StringComparison.OrdinalIgnoreCase)) return null;
        if (user != null && (await db.GetUserAsync(user.Id))?.Disabled == true) return null;   // disabled accounts lose API access too
        return user;
    }

    /// <summary>Cookie, then token, then Basic username/password.</summary>
    public static async Task<UserAccount?> UserAsync(HttpContext http, IMeridianStore db)
    {
        if (http.User.Identity?.IsAuthenticated == true && Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            return new UserAccount(id, http.User.Identity.Name ?? "", http.User.FindFirstValue("display_name") ?? "", http.User.FindFirstValue("color") ?? "");
        if (await TokenUserAsync(http, db) is { } t) return t;
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var colon = raw.IndexOf(':');
            return colon < 0 ? null : await db.VerifyUserAsync(raw[..colon], raw[(colon + 1)..]);
        }
        catch (FormatException) { return null; }
    }
}

/// <summary>Registers issue trackers, webhooks, personal access tokens and the OSLC RM provider.</summary>
public static class IntegrationsSetup
{
    public static IServiceCollection AddMeridianIntegrations(this IServiceCollection services)
    {
        services.AddDataProtection();
        services.AddHttpClient(IntegrationService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(20))
            .RedactLoggedHeaders(_ => true);   // never log Authorization or signature headers
        services.AddSingleton(new WebhookOptions());
        services.AddSingleton<ProjectEvents>();
        services.AddSingleton<IntegrationService>();
        services.AddSingleton<WebhookDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<WebhookDispatcher>());
        return services;
    }

    public static WebApplication MapMeridianIntegrations(this WebApplication app)
    {
        app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<ProjectEvents>().FlushAll());
        // OSLC is for team servers; the desktop app's private local server is not reachable by other tools.
        if (!app.Services.GetRequiredService<MeridianHostInfo>().LocalMode) MapOslc(app);
        return app;
    }

    // ================================================================ OSLC RM 2.0 provider (read-only)

    private static Oslc.Urls UrlsFor(HttpContext http)
    {
        var configured = http.RequestServices.GetRequiredService<IConfiguration>()["Meridian:PublicUrl"];
        return new Oslc.Urls(configured is { Length: > 0 } ? configured.TrimEnd('/') : $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}".TrimEnd('/'));
    }

    private static bool WantsJson(HttpRequest r)
    {
        var accept = r.Headers.Accept.ToString();
        return accept.Contains(Oslc.JsonLd, StringComparison.OrdinalIgnoreCase) || accept.Contains("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static IResult Xml(HttpContext http, XDocument doc, string type = Oslc.RdfXml)
    {
        http.Response.Headers["OSLC-Core-Version"] = "2.0";
        var sb = new StringBuilder();
        using (var w = new Utf8StringWriter(sb)) doc.Save(w);
        return Results.Content(sb.ToString(), type + "; charset=utf-8");
    }

    private static IResult Json(HttpContext http, System.Text.Json.Nodes.JsonObject o)
    {
        http.Response.Headers["OSLC-Core-Version"] = "2.0";
        return Results.Content(o.ToJsonString(), Oslc.JsonLd + "; charset=utf-8");
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

    /// <summary>Loads the live model of a project the caller may read.</summary>
    private static async Task<(UserAccount? User, string? Name, ModelRepository? Model, IResult? Error)> Project(HttpContext http, IMeridianStore db, CollaborationHub hub, Guid projectId)
    {
        var user = await ApiAuth.UserAsync(http, db);
        if (user is null) return (null, null, null, Challenge(http));
        if (await db.RoleOfAsync(projectId, user.Id) is null) return (user, null, null, Results.NotFound());
        var row = await db.LoadProjectAsync(projectId);
        if (row is null) return (user, null, null, Results.NotFound());
        var json = hub.Find(projectId)?.Snapshot() ?? row.Value.ModelJson;
        return (user, hub.Find(projectId)?.Name ?? row.Value.Name, ProjectFile.Deserialize(json), null);
    }

    private static void MapOslc(WebApplication app)
    {
        var oslc = app.MapGroup("/oslc");

        oslc.MapGet("/rootservices", (HttpContext http) => Xml(http, Oslc.RootServices(UrlsFor(http), "Meridian"), "application/rdf+xml"));

        oslc.MapGet("/catalog", async (HttpContext http, IMeridianStore db) =>
        {
            var user = await ApiAuth.UserAsync(http, db);
            if (user is null) return Challenge(http);
            var projects = await db.ListProjectsAsync(user.Id);
            return Xml(http, Oslc.Catalog(UrlsFor(http), "Meridian", projects.Select(p => (p.Id, p.Name))));
        });

        oslc.MapGet("/projects/{projectId:guid}/provider", async (HttpContext http, IMeridianStore db, CollaborationHub hub, Guid projectId) =>
        {
            var user = await ApiAuth.UserAsync(http, db);
            if (user is null) return Challenge(http);
            if (await db.RoleOfAsync(projectId, user.Id) is null || await db.LoadProjectAsync(projectId) is not { } row) return Results.NotFound();
            return Xml(http, Oslc.Provider(UrlsFor(http), projectId, hub.Find(projectId)?.Name ?? row.Name));
        });

        oslc.MapGet("/projects/{projectId:guid}/requirements", async (HttpContext http, IMeridianStore db, CollaborationHub hub, Guid projectId) =>
        {
            var (_, _, model, error) = await Project(http, db, hub, projectId);
            if (error != null) return error;
            var u = UrlsFor(http);
            var q = http.Request.Query;
            var all = Oslc.Filter(Oslc.Requirements(model!, u, projectId), q["oslc.where"], q["oslc.searchTerms"]).ToList();
            var paging = q["oslc.paging"] == "true" || q.ContainsKey("oslc.pageSize");
            var size = int.TryParse(q["oslc.pageSize"], out var ps) && ps > 0 ? Math.Min(ps, 1000) : 100;
            var pageNo = int.TryParse(q["page"], out var pn) && pn > 0 ? pn : 1;
            var page = paging ? all.Skip((pageNo - 1) * size).Take(size).ToList() : all;
            string? next = null;
            if (paging && pageNo * size < all.Count)
            {
                var parts = q.Where(kv => kv.Key != "page").Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value.ToString())}").Append($"page={pageNo + 1}");
                next = $"{u.Query(projectId)}?{string.Join("&", parts)}";
            }
            var requestUrl = u.Query(projectId) + http.Request.QueryString;
            return WantsJson(http.Request)
                ? Json(http, Oslc.QueryJsonLd(u.Query(projectId), requestUrl, page, all.Count, next, u.Provider(projectId)))
                : Xml(http, Oslc.QueryRdf(u.Query(projectId), requestUrl, page, all.Count, next, u.Provider(projectId)));
        });

        oslc.MapGet("/projects/{projectId:guid}/requirements/{elementId}", async (HttpContext http, IMeridianStore db, CollaborationHub hub, Guid projectId, string elementId) =>
        {
            var (_, _, model, error) = await Project(http, db, hub, projectId);
            if (error != null) return error;
            if (model!.Find(elementId) is not { Kind: ElementKind.Requirement } e) return Results.NotFound();
            var u = UrlsFor(http);
            var r = Oslc.Describe(model, u, projectId, e);
            var accept = http.Request.Headers.Accept.ToString();
            if (accept.Contains(Oslc.CompactXml, StringComparison.OrdinalIgnoreCase)) return Xml(http, Oslc.Compact(r), Oslc.CompactXml);
            if (WantsJson(http.Request)) return Json(http, Oslc.RequirementJsonLd(r, u.Provider(projectId)));
            if (accept.Contains("text/html", StringComparison.OrdinalIgnoreCase) && !accept.Contains(Oslc.RdfXml, StringComparison.OrdinalIgnoreCase))
                return Results.Redirect(r.WebUrl);
            return Xml(http, Oslc.RequirementRdf(r, u.Provider(projectId)));
        });

        oslc.MapGet("/projects/{projectId:guid}/selector", async (HttpContext http, IMeridianStore db, CollaborationHub hub, Guid projectId) =>
        {
            var (_, name, model, error) = await Project(http, db, hub, projectId);
            if (error != null) return error;
            var u = UrlsFor(http);
            var text = http.Request.Query["q"].ToString();
            var reqs = Oslc.Filter(Oslc.Requirements(model!, u, projectId), null, text).Take(300).ToList();
            http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; form-action 'self'";
            return Results.Content(SelectorPage(name!, text, reqs), "text/html; charset=utf-8");
        });
    }

    private static string H(string s) => WebUtility.HtmlEncode(s);

    /// <summary>OSLC delegated selection dialog: supports the postMessage and windowName protocols.</summary>
    internal static string SelectorPage(string projectName, string query, IReadOnlyList<Oslc.Req> reqs)
    {
        var rows = new StringBuilder();
        foreach (var r in reqs)
            rows.Append($"""<label class="row"><input type="checkbox" value="{H(r.Url)}" data-label="{H(r.Identifier + " " + r.Title)}"><b>{H(r.Identifier)}</b> {H(r.Title)}</label>""");
        if (reqs.Count == 0) rows.Append("<p class=\"empty\">No matching requirements.</p>");
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Select requirements — Meridian</title>
            <style>
              body{font:13px system-ui,-apple-system,Segoe UI,sans-serif;margin:0;color:#1d2330;background:#fff}
              header{padding:10px 12px;border-bottom:1px solid #e3e6eb;font-weight:600}
              form.search{display:flex;gap:6px;padding:8px 12px}
              form.search input{flex:1;padding:6px 8px;border:1px solid #c9ced8;border-radius:6px}
              .list{padding:0 12px;max-height:calc(100vh - 140px);overflow:auto}
              .row{display:flex;gap:8px;align-items:baseline;padding:5px 2px;border-bottom:1px solid #f0f2f5;cursor:pointer}
              .row b{white-space:nowrap}
              footer{display:flex;justify-content:flex-end;gap:8px;padding:10px 12px;border-top:1px solid #e3e6eb}
              button{padding:6px 14px;border-radius:6px;border:1px solid #c9ced8;background:#f6f7f9;cursor:pointer}
              button.primary{background:#4f8cff;border-color:#4f8cff;color:#fff}
              .empty{color:#7a8396}
            </style></head>
            <body>
            <header>Meridian — {{H(projectName)}}</header>
            <form class="search" method="get"><input name="q" value="{{H(query)}}" placeholder="Search requirements…" autofocus><button type="submit">Search</button></form>
            <div class="list">{{rows}}</div>
            <footer><button type="button" id="cancel">Cancel</button><button type="button" class="primary" id="ok">Select</button></footer>
            <script>
            (function () {
              var hash = window.location.hash || "";
              // Keep the protocol fragment across searches.
              document.querySelector("form.search").addEventListener("submit", function (ev) {
                ev.preventDefault();
                var q = encodeURIComponent(this.q.value);
                window.location.replace(window.location.pathname + "?q=" + q + hash);
              });
              if (hash.indexOf("oslc-core-windowName-1.0") >= 0 && !sessionStorage.getItem("oslcReturn")) {
                try { sessionStorage.setItem("oslcReturn", window.name); } catch (e) {}
              }
              function respond(results) {
                var json = JSON.stringify({ "oslc:results": results });
                if (hash.indexOf("oslc-core-windowName-1.0") >= 0) {
                  var ret = sessionStorage.getItem("oslcReturn") || "";
                  window.name = json;
                  if (ret) window.location.href = ret;
                } else {
                  var target = window.parent !== window ? window.parent : window.opener;
                  if (target) target.postMessage("oslc-response:" + json, "*");
                }
              }
              document.getElementById("ok").addEventListener("click", function () {
                var picked = Array.prototype.slice.call(document.querySelectorAll(".row input:checked"))
                  .map(function (c) { return { "oslc:label": c.getAttribute("data-label"), "rdf:resource": c.value }; });
                respond(picked);
              });
              document.getElementById("cancel").addEventListener("click", function () { respond([]); });
            })();
            </script>
            </body></html>
            """;
    }
}
