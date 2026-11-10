using System.Security.Claims;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Admin;
using Meridian.Web.Components;
using Meridian.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using MudBlazor.Services;

namespace Meridian.Web;

/// <summary>What kind of host this is: a team server (PostgreSQL, many users) or the desktop app's private local server.</summary>
public sealed record MeridianHostInfo(bool LocalMode, string DataPath, string? LaunchToken)
{
    public static MeridianHostInfo From(IConfiguration c)
    {
        var local = string.Equals(c["Meridian:Mode"], "local", StringComparison.OrdinalIgnoreCase);
        var data = c["Meridian:DataPath"] is { Length: > 0 } p ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "Meridian");
        return new MeridianHostInfo(local, data, c["Meridian:LaunchToken"]);
    }
}

/// <summary>
/// Builds the Meridian web application. Used by the server executable (web / Docker) and, in-process, by the desktop app.
/// </summary>
public static class MeridianServer
{
    /// <summary>
    /// Where the local-mode launch sign-in sends the window afterwards: the requested path, but never another site
    /// ("//host" and "/\host" are protocol-relative URLs to browsers) and never back to the sign-in page.
    /// </summary>
    public static string LocalRedirectPath(PathString path)
    {
        var p = path.HasValue ? path.Value! : "/";
        if (p == "/login" || p.Length < 1 || p[0] != '/' || (p.Length > 1 && p[1] is '/' or '\\') || p.Contains('\\')) return "/";
        return p;
    }

    public static async Task<WebApplication> CreateAsync(string[] args, Action<WebApplicationBuilder>? customize = null, WebApplicationOptions? options = null)
    {
        var builder = WebApplication.CreateBuilder(options ?? new WebApplicationOptions { Args = args });
        customize?.Invoke(builder);
        Hosting.OperationsHosting.AddMeridianOperations(builder);   // health, JSON/request logging, OpenTelemetry (docs/DEPLOYMENT.md)

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents()
            .AddHubOptions(o => o.MaximumReceiveMessageSize = 64 * 1024 * 1024);
        builder.Services.AddMudServices();
        var host = MeridianHostInfo.From(builder.Configuration);
        builder.Services.AddSingleton(host);
        builder.Services.AddSingleton<IMeridianStore>(host.LocalMode || builder.Configuration["Meridian:Storage"] == "sqlite"
            ? new SqliteStore(Path.Combine(host.DataPath, "meridian.db"))
            : new PostgresStore(builder.Configuration.GetConnectionString("Meridian")
                ?? throw new InvalidOperationException("Missing connection string 'Meridian' (PostgreSQL). Set Meridian:Storage=sqlite for a single-file database.")));
        builder.Services.AddSingleton<CollaborationHub>();
        builder.Services.AddSingleton<ImageCache>();
        builder.Services.AddScoped<Workspace>();
        builder.Services.AddScoped<ReviewService>();
        builder.Services.AddScoped<MobileUiState>();   // phone / tablet shell state (Components/Mobile)
        builder.Services.AddMeridianScripting(builder.Configuration, host);   // script console + plugins (docs/SCRIPTING.md, docs/PLUGINS.md)
        builder.Services.AddMeridianAssistant();   // AI assistant powered by Claude (docs/AI.md)
        Settings.SettingsHosting.AddMeridianSettings(builder.Services, host);   // Settings page: preferences, theme, account
        builder.Services.AddHttpContextAccessor();
        Integrations.IntegrationsSetup.AddMeridianIntegrations(builder.Services);   // issue trackers, webhooks, API tokens, OSLC
        CodeSync.Hosts.CodeSyncHostsSetup.AddMeridianCodeSyncHosts(builder.Services);   // code repositories: webhooks, fetch, commit status

        // Persist cookie-encryption keys (containers): set Meridian:KeysPath to a mounted volume.
        if ((builder.Configuration["Meridian:KeysPath"] is { Length: > 0 } kp ? kp : host.LocalMode ? Path.Combine(host.DataPath, "keys") : null) is { } keysPath)
            Microsoft.AspNetCore.DataProtection.DataProtectionBuilderExtensions.PersistKeysToFileSystem(
                Microsoft.Extensions.DependencyInjection.DataProtectionServiceCollectionExtensions.AddDataProtection(builder.Services), new DirectoryInfo(keysPath));

        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.LoginPath = "/login";
                o.LogoutPath = "/account/logout";
                o.ExpireTimeSpan = TimeSpan.FromDays(14);
                o.SlidingExpiration = true;
                o.Cookie.Name = host.LocalMode ? "meridian.local" : "meridian.auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
            });
        builder.Services.AddAuthorization();
        builder.Services.AddCascadingAuthenticationState();
        builder.AddMeridianAdmin(host); // SSO, lockout + rate limiting, audit log, backups (Admin/AdminHosting.cs)

        var app = builder.Build();
        Hosting.OperationsHosting.UseMeridianRequestLogging(app);

        var meridianDb = app.Services.GetRequiredService<IMeridianStore>();
        await meridianDb.MigrateAsync();
        var localUser = host.LocalMode ? await EnsureLocalUser(meridianDb) : null;

        // Development only: seed test accounts from appsettings.Development.json (DevSeed:Users).
        if (app.Environment.IsDevelopment())
        {
            foreach (var u in app.Configuration.GetSection("DevSeed:Users").GetChildren())
            {
                var username = u["Username"];
                if (string.IsNullOrEmpty(username) || await meridianDb.FindUserAsync(username) != null) continue;
                var created = await meridianDb.CreateUserAsync(username, u["DisplayName"] ?? username, u["Password"]!);
                await meridianDb.CreateProjectAsync("Vehicle System (sample)", ProjectFile.Serialize(SampleModels.Vehicle()), created.Id);
            }
        }

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            if (!host.LocalMode) app.UseHsts();
        }
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseMeridianAdmin();
        app.UseAuthentication();

        // Desktop local mode: the host can import a file it was asked to open (token-guarded; see LocalFileImport).
        app.UseLocalFileImport(host, localUser);

        // Desktop local mode: a single user, signed in automatically — but only by the window that holds this launch's token,
        // so other browsers or local processes reaching the loopback port cannot get in.
        if (localUser != null)
        {
            app.Use(async (http, next) =>
            {
                if (http.User.Identity?.IsAuthenticated == true) { await next(); return; }
                var token = http.Request.Query["launch"].ToString();
                if (token.Length > 0 && host.LaunchToken is { Length: > 0 } expected &&
                    System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(expected)))
                {
                    await SignIn(http, localUser);
                    var keep = QueryString.Create(http.Request.Query.Where(q => q.Key != "launch")); // e.g. ?project=<id> from the desktop host
                    http.Response.Redirect(LocalRedirectPath(http.Request.Path) + keep);
                    return;
                }
                if (http.Request.Path.StartsWithSegments("/_framework") || http.Request.Path.StartsWithSegments("/_content") || Path.HasExtension(http.Request.Path.Value)) { await next(); return; }
                http.Response.StatusCode = 403;
                await http.Response.WriteAsync("This Meridian workspace is private to the desktop app. Open it from the Meridian window.");
            });
        }
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapStaticAssets();
        Hosting.OperationsHosting.MapMeridianHealth(app);
        ImageCache.Map(app);
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        static async Task<UserAccount> EnsureLocalUser(IMeridianStore db)
        {
            if (await db.FindUserAsync("local") is { } existing) return existing;
            var name = Environment.UserName is { Length: > 0 } u ? u : "Me";
            var user = await db.CreateUserAsync("local", char.ToUpperInvariant(name[0]) + name[1..],
                Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)));
            await db.CreateProjectAsync("Vehicle System (sample)", ProjectFile.Serialize(SampleModels.Vehicle()), user.Id);
            return user;
        }

        // ---------------------------------------------------------------- account endpoints (form posts from the static sign-in page)

        static Task SignIn(HttpContext http, UserAccount user) => MeridianAuth.SignInAsync(http, user);

        static string Back(string? mode, string error) => $"/login?mode={mode}&error={Uri.EscapeDataString(error)}";

        var account = app.MapGroup("/account");

        account.MapPost("/login", AdminHosting.PasswordLoginAsync).RequireRateLimiting(AdminHosting.LoginRateLimit);

        account.MapPost("/register", async (HttpContext http, IMeridianStore db) =>
        {
            if (AdminHosting.RegistrationBlocked(http) is { } blocked) return Results.Redirect(Back("register", blocked));
            var form = await http.Request.ReadFormAsync();
            try
            {
                var user = await db.CreateUserAsync(form["username"].ToString(), form["display"].ToString(), form["password"].ToString());
                // Every new account starts with its own copy of the sample model so there is something to explore.
                await db.CreateProjectAsync("Vehicle System (sample)", ProjectFile.Serialize(SampleModels.Vehicle()), user.Id);
                await AdminHosting.OnRegisteredAsync(http, db, user);
                await SignIn(http, user);
                return Results.Redirect("/");
            }
            catch (InvalidOperationException ex)
            {
                // Keep what was typed (never the password) so the user only fixes the field that was wrong.
                return Results.Redirect(Back("register", ex.Message) + "&user=" + Uri.EscapeDataString(form["username"].ToString()) + "&display=" + Uri.EscapeDataString(form["display"].ToString()));
            }
        }).RequireRateLimiting(AdminHosting.LoginRateLimit);

        account.MapMethods("/logout", ["GET", "POST"], async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });

        // ---------------------------------------------------------------- SysML v2 API (read-only subset of the OMG Systems Modeling API)
        // Auth: the browser session cookie, or HTTP Basic with a Meridian username/password.

        static async Task<UserAccount?> ApiUser(HttpContext http, IMeridianStore db)
        {
            if (http.User.Identity?.IsAuthenticated == true && Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
                return new UserAccount(id, http.User.Identity.Name ?? "", http.User.FindFirstValue("display_name") ?? "", http.User.FindFirstValue("color") ?? "");
            if (await Integrations.ApiAuth.TokenUserAsync(http, db) is { } tokenUser) return tokenUser;   // personal access token (Bearer)
            var header = http.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) || AdminHosting.LocalPasswordsDisabled(http)) return null;
            try
            {
                var raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                var colon = raw.IndexOf(':');
                return colon < 0 ? null : await db.VerifyUserAsync(raw[..colon], raw[(colon + 1)..]);
            }
            catch (FormatException) { return null; }
        }

        static IResult Unauthorized() => Results.Text("Authentication required", statusCode: 401);

        async Task<(Meridian.Core.Model.ModelRepository? Model, IResult? Error)> LoadCommit(HttpContext http, IMeridianStore db, CollaborationHub hub, Guid projectId, string commitId)
        {
            var user = await ApiUser(http, db);
            if (user is null) { http.Response.Headers.WWWAuthenticate = "Basic realm=\"Meridian\""; return (null, Unauthorized()); }
            if (await db.RoleOfAsync(projectId, user.Id) is null) return (null, Results.NotFound());
            string? json;
            if (commitId is "head" or "HEAD") json = hub.Find(projectId)?.Snapshot() ?? (await db.LoadProjectAsync(projectId))?.ModelJson;
            else json = long.TryParse(commitId, out var vid) ? await db.LoadVersionAsync(projectId, vid) : null;
            return json is null ? (null, Results.NotFound()) : (ProjectFile.Deserialize(json), null);
        }

        var api = app.MapGroup("/api");

        api.MapGet("/projects", async (HttpContext http, IMeridianStore db) =>
        {
            var user = await ApiUser(http, db);
            if (user is null) { http.Response.Headers.WWWAuthenticate = "Basic realm=\"Meridian\""; return Unauthorized(); }
            var list = await db.ListProjectsAsync(user.Id);
            return Results.Json(list.Select(p => new Dictionary<string, object?>
            {
                ["@id"] = p.Id, ["@type"] = "Project", ["name"] = p.Name, ["created"] = p.UpdatedAt,
                ["defaultBranch"] = new Dictionary<string, object> { ["@id"] = "main" }, ["role"] = p.Role.ToString().ToLowerInvariant(),
            }));
        });

        api.MapGet("/projects/{projectId:guid}/commits", async (HttpContext http, IMeridianStore db, Guid projectId) =>
        {
            var user = await ApiUser(http, db);
            if (user is null) return Unauthorized();
            if (await db.RoleOfAsync(projectId, user.Id) is null) return Results.NotFound();
            var versions = await db.ListVersionsAsync(projectId);
            var commits = versions.Select(v => new Dictionary<string, object?>
            {
                ["@id"] = v.Id.ToString(), ["@type"] = "Commit", ["description"] = $"v{v.Number}: {v.Message}", ["created"] = v.CreatedAt, ["owningProject"] = new { @id = projectId },
            }).Prepend(new Dictionary<string, object?> { ["@id"] = "head", ["@type"] = "Commit", ["description"] = "Live working state" });
            return Results.Json(commits);
        });

        api.MapGet("/projects/{projectId:guid}/commits/{commitId}/elements", async (HttpContext http, IMeridianStore db, CollaborationHub hub, Guid projectId, string commitId) =>
        {
            var (model, error) = await LoadCommit(http, db, hub, projectId, commitId);
            if (error != null) return error;
            var nodes = new System.Text.Json.Nodes.JsonArray(model!.AllElements.Select(e => (System.Text.Json.Nodes.JsonNode)SysMLv2Json.Element(model, e)).ToArray());
            return Results.Content(nodes.ToJsonString(), "application/json");
        });

        api.MapGet("/projects/{projectId:guid}/commits/{commitId}/roots", async (HttpContext http, IMeridianStore db, CollaborationHub hub, Guid projectId, string commitId) =>
        {
            var (model, error) = await LoadCommit(http, db, hub, projectId, commitId);
            if (error != null) return error;
            return Results.Content(new System.Text.Json.Nodes.JsonArray(SysMLv2Json.Element(model!, model!.Root)).ToJsonString(), "application/json");
        });

        api.MapGet("/projects/{projectId:guid}/commits/{commitId}/elements/{elementId}", async (HttpContext http, IMeridianStore db, CollaborationHub hub, Guid projectId, string commitId, string elementId) =>
        {
            var (model, error) = await LoadCommit(http, db, hub, projectId, commitId);
            if (error != null) return error;
            return model!.Find(elementId) is { } e ? Results.Content(SysMLv2Json.Element(model, e).ToJsonString(), "application/json") : Results.NotFound();
        });

        Integrations.IntegrationsSetup.MapMeridianIntegrations(app);
        CodeSync.Hosts.CodeSyncHostsSetup.MapMeridianCodeSyncHosts(app);
        await app.MapMeridianAdminAsync();

        app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<CollaborationHub>().FlushAllAsync().GetAwaiter().GetResult());

        return app;
    }
}
