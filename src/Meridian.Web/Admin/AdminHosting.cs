using System.IO.Compression;
using System.Threading.RateLimiting;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace Meridian.Web.Admin;

/// <summary>
/// Wires enterprise administration into the host: OpenID Connect providers, sign-in rate limiting and lockout,
/// live account-state checks, the audit log, backups and the <c>/admin</c> download endpoints.
/// Called from <see cref="MeridianServer"/> with one line per stage.
/// </summary>
public static class AdminHosting
{
    public const string LoginRateLimit = "meridian-login";

    // ================================================================== services

    public static void AddMeridianAdmin(this WebApplicationBuilder builder, MeridianHostInfo host)
    {
        var settings = AdminSettings.From(builder.Configuration);
        if (host.LocalMode) settings = settings with { Sso = new SsoOptions(), Backup = new BackupOptions() }; // desktop: single user, no SSO or schedule
        LoginPolicy.MaxFailures = Math.Max(0, settings.Security.MaxFailedLogins);
        LoginPolicy.Lockout = TimeSpan.FromMinutes(Math.Max(1, settings.Security.LockoutMinutes));

        var s = builder.Services;
        s.AddSingleton(settings);
        s.AddSingleton<IAuditLog, AuditLog>();
        s.AddSingleton<AdminAccess>();
        s.AddSingleton<SsoProvisioner>();
        s.AddSingleton<BackupService>();
        if (!host.LocalMode) s.AddHostedService<BackupScheduler>();

        if (settings.Security.TrustForwardedHeaders)
            s.Configure<ForwardedHeadersOptions>(o =>
            {
                o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
                o.KnownIPNetworks.Clear();
                o.KnownProxies.Clear();
            });

        // Sign-in attempts per client IP (password and SSO); excess attempts bounce back to the sign-in page.
        s.AddRateLimiter(o =>
        {
            o.AddPolicy(LoginRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(MeridianAuth.Ip(http) ?? "unknown", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, settings.Security.LoginAttemptsPerMinute), Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
            }));
            o.OnRejected = async (ctx, _) =>
            {
                var http = ctx.HttpContext;
                await http.RequestServices.GetRequiredService<IAuditLog>().RecordAsync(AuditActions.LoginRateLimited, null, target: http.Request.Path, ip: MeridianAuth.Ip(http), actorName: "anonymous");
                http.Response.StatusCode = StatusCodes.Status302Found;
                http.Response.Headers.Location = "/login?error=" + Uri.EscapeDataString("Too many sign-in attempts. Wait a minute and try again.");
            };
        });

        // Disabled or deleted accounts lose their session on the next request, not when the cookie expires.
        s.PostConfigure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, o =>
        {
            var previous = o.Events.OnValidatePrincipal;
            o.Events.OnValidatePrincipal = async ctx =>
            {
                await previous(ctx);
                if (host.LocalMode || ctx.Principal is null || MeridianAuth.UserId(ctx.Principal) is not { } id) return;
                var state = await ctx.HttpContext.RequestServices.GetRequiredService<AdminAccess>().StateAsync(id);
                if (state is null || state.Disabled)
                {
                    ctx.RejectPrincipal();
                    await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }
            };
        });

        var auth = s.AddAuthentication();
        foreach (var provider in settings.Sso.Providers)
        {
            var p = provider;
            auth.AddOpenIdConnect(p.Scheme, p.Label, o =>
            {
                o.Authority = p.Authority;
                o.ClientId = p.ClientId;
                o.ClientSecret = p.ClientSecret;
                o.ResponseType = "code";
                o.UsePkce = true;
                o.SaveTokens = false;
                o.MapInboundClaims = false;
                o.GetClaimsFromUserInfoEndpoint = true;
                o.RequireHttpsMetadata = p.RequireHttpsMetadata;
                o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.CallbackPath = $"/signin-oidc-{p.Name.ToLowerInvariant()}";
                o.SignedOutCallbackPath = $"/signout-callback-oidc-{p.Name.ToLowerInvariant()}";
                o.Scope.Clear();
                foreach (var scope in p.Scopes.Append("openid").Distinct()) o.Scope.Add(scope);
                o.TokenValidationParameters.NameClaimType = "name";
                o.Events.OnTicketReceived = async ctx =>
                {
                    var http = ctx.HttpContext;
                    var outcome = await http.RequestServices.GetRequiredService<SsoProvisioner>().ResolveAsync(p, ctx.Principal!, MeridianAuth.Ip(http));
                    if (outcome.User is null)
                    {
                        http.Response.Redirect("/login?error=" + Uri.EscapeDataString(outcome.Error ?? "Single sign-on failed."));
                        ctx.HandleResponse();
                        return;
                    }
                    if (outcome.Provisioned)
                        await http.RequestServices.GetRequiredService<IMeridianStore>()
                            .CreateProjectAsync("Vehicle System (sample)", ProjectFile.Serialize(SampleModels.Vehicle()), outcome.User.Id);
                    ctx.Principal = MeridianAuth.Principal(outcome.User, "sso:" + p.Name);
                    ctx.Properties!.IsPersistent = true;
                };
                o.Events.OnRemoteFailure = async ctx =>
                {
                    var http = ctx.HttpContext;
                    await http.RequestServices.GetRequiredService<IAuditLog>().RecordAsync(AuditActions.SsoDenied, null,
                        details: new { provider = p.Name, error = ctx.Failure?.Message }, ip: MeridianAuth.Ip(http), actorName: "anonymous");
                    http.Response.Redirect("/login?error=" + Uri.EscapeDataString($"Sign-in with {p.Label} failed. Try again or contact your administrator."));
                    ctx.HandleResponse();
                };
            });
        }
    }

    // ================================================================== middleware (before authentication)

    public static void UseMeridianAdmin(this WebApplication app)
    {
        if (app.Services.GetRequiredService<AdminSettings>().Security.TrustForwardedHeaders) app.UseForwardedHeaders();
        app.UseMeridianSecurityHeaders();
        app.UseRateLimiter();
    }

    // ================================================================== password sign-in & registration (called from /account endpoints)

    public static bool LocalPasswordsDisabled(HttpContext http) =>
        http.RequestServices.GetRequiredService<AdminSettings>().Sso.DisableLocalPasswords;

    /// <summary>POST /account/login: lockout-aware password sign-in with audit.</summary>
    public static async Task<IResult> PasswordLoginAsync(HttpContext http, IMeridianStore db)
    {
        static IResult Back(string error) => Results.Redirect("/login?mode=signin&error=" + Uri.EscapeDataString(error));
        if (LocalPasswordsDisabled(http)) return Back("Password sign-in is disabled on this server. Use single sign-on.");
        var form = await http.Request.ReadFormAsync();
        var username = form["username"].ToString().Trim();
        var audit = http.RequestServices.GetRequiredService<IAuditLog>();
        var ip = MeridianAuth.Ip(http);
        var result = await db.AuthenticateAsync(username, form["password"].ToString(), LoginPolicy.MaxFailures, LoginPolicy.Lockout);
        switch (result.Status)
        {
            case LoginStatus.Success:
                await MeridianAuth.SignInAsync(http, result.User!, "password");
                await audit.RecordAsync(AuditActions.LoginSuccess, result.User, target: result.User!.Username, ip: ip);
                return Results.Redirect("/");
            case LoginStatus.Disabled:
                await audit.RecordAsync(AuditActions.LoginDisabled, null, target: username, ip: ip, actorName: username);
                return Back("This account has been disabled. Contact your administrator.");
            case LoginStatus.LockedOut:
                await audit.RecordAsync(AuditActions.LoginLocked, null, target: username, details: new { until = result.LockedUntil }, ip: ip, actorName: username);
                var minutes = result.LockedUntil is { } until ? Math.Max(1, (int)Math.Ceiling((until - DateTime.UtcNow).TotalMinutes)) : 15;
                return Back($"Too many failed attempts. This account is locked for {minutes} minute{(minutes == 1 ? "" : "s")}.");
            default:
                await audit.RecordAsync(AuditActions.LoginFailure, null, target: username, details: new { knownUser = result.UserId is not null }, ip: ip, actorName: username);
                return Back("Wrong username or password.");
        }
    }

    /// <summary>Why the sign-in page may not create accounts right now, or null when it may.</summary>
    public static string? RegistrationBlocked(HttpContext http)
    {
        var s = http.RequestServices.GetRequiredService<AdminSettings>();
        if (s.Sso.DisableLocalPasswords) return "Accounts are created through single sign-on on this server.";
        return s.Security.AllowRegistration ? null : "Self-registration is turned off. Ask an administrator for an account.";
    }

    /// <summary>After /account/register created <paramref name="user"/>: first account becomes admin, and the event is audited.</summary>
    public static async Task OnRegisteredAsync(HttpContext http, IMeridianStore db, UserAccount user)
    {
        await db.EnsureAdminAsync();
        if (http.RequestServices.GetRequiredService<AdminSettings>().Security.Admins.Contains(user.Username)) await db.SetUserAdminAsync(user.Id, true);
        await http.RequestServices.GetRequiredService<IAuditLog>().RecordAsync(AuditActions.UserRegister, user, target: user.Username, ip: MeridianAuth.Ip(http));
    }

    // ================================================================== startup + endpoints

    public static async Task MapMeridianAdminAsync(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<AdminSettings>();
        var host = app.Services.GetRequiredService<MeridianHostInfo>();
        var db = app.Services.GetRequiredService<IMeridianStore>();

        // Server roles: configured administrators, and otherwise the first registered account.
        if (!host.LocalMode)
        {
            foreach (var name in settings.Security.Admins)
                if (await db.FindUserAsync(name) is { } u && await db.GetUserAsync(u.Id) is { IsAdmin: false })
                    await db.SetUserAdminAsync(u.Id, true);
            await db.EnsureAdminAsync();
        }

        // Start single sign-on with a provider: /account/sso/{name}
        app.MapGet("/account/sso/{name}", async (HttpContext http, ILogger<SsoProvisioner> log, string name) =>
        {
            if (settings.Sso.Find(name) is not { } p) { http.Response.Redirect("/login?error=" + Uri.EscapeDataString("Unknown sign-in provider.")); return; }
            try
            {
                await http.ChallengeAsync(p.Scheme, new AuthenticationProperties { RedirectUri = "/" });
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException)
            {
                // Typically the provider's discovery document can't be reached or is misconfigured.
                log.LogError(ex, "Could not start single sign-on with {Provider}", p.Name);
                if (!http.Response.HasStarted)
                {
                    http.Response.Clear();
                    http.Response.Redirect("/login?error=" + Uri.EscapeDataString($"{p.Label} sign-in is unavailable right now. Contact your administrator."));
                }
            }
        }).AllowAnonymous().RequireRateLimiting(LoginRateLimit);

        var admin = app.MapGroup("/admin/api").RequireAuthorization();

        admin.MapGet("/backup", async (HttpContext http, AdminAccess access, BackupService backups, IAuditLog audit, bool? users) =>
        {
            if (!await access.IsAdminAsync(http.User)) return Results.NotFound();
            var me = MeridianAuth.User(http.User)!;
            var temp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
            var manifest = await backups.WriteAsync(temp, users ?? true, me.Username, http.RequestAborted);
            temp.Position = 0;
            var name = BackupService.FileName(DateTime.UtcNow);
            await audit.RecordAsync(AuditActions.BackupDownload, me, target: name, details: new { projects = manifest.Projects.Count, users = users ?? true, bytes = temp.Length }, ip: MeridianAuth.Ip(http));
            return Results.File(temp, "application/zip", name);
        });

        admin.MapGet("/backups/{file}", async (HttpContext http, AdminAccess access, BackupService backups, IAuditLog audit, string file) =>
        {
            if (!await access.IsAdminAsync(http.User)) return Results.NotFound();
            var match = backups.ListFiles().FirstOrDefault(f => f.Name == file); // only files the scheduler wrote, never arbitrary paths
            if (match is null) return Results.NotFound();
            await audit.RecordAsync(AuditActions.BackupDownload, MeridianAuth.User(http.User), target: match.Name, details: new { scheduled = true, bytes = match.Length }, ip: MeridianAuth.Ip(http));
            return Results.File(match.FullName, "application/zip", match.Name);
        });

        admin.MapGet("/audit.csv", async (HttpContext http, AdminAccess access, IMeridianStore store, IAuditLog audit,
            string? action, string? actor, string? text, Guid? project, DateTime? from, DateTime? to) =>
        {
            if (!await access.IsAdminAsync(http.User)) return Results.NotFound();
            var page = await store.QueryAuditAsync(new AuditQuery(action, actor, project, text, from, to, 0, 100_000));
            await audit.RecordAsync(AuditActions.AuditExport, MeridianAuth.User(http.User), details: new { action, actor, text, project, from, to, rows = page.Items.Count }, ip: MeridianAuth.Ip(http));
            return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(AuditLog.ToCsv(page.Items))).ToArray(),
                "text/csv", $"meridian-audit-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
        });
    }

    /// <summary>Opens an uploaded backup for reading (it must be a seekable stream, e.g. a temp file).</summary>
    public static ZipArchive OpenBackup(Stream seekable) => new(seekable, ZipArchiveMode.Read, leaveOpen: true);
}
