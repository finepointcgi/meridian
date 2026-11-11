using System.Security.Claims;
using System.Text;
using Meridian.Data;
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
        // Same rule as /api: "single sign-on only" also turns off username/password Basic auth.
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) || Admin.AdminHosting.LocalPasswordsDisabled(http)) return null;
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var colon = raw.IndexOf(':');
            return colon < 0 ? null : await db.VerifyUserAsync(raw[..colon], raw[(colon + 1)..]);
        }
        catch (FormatException) { return null; }
    }
}

/// <summary>Registers issue trackers, webhooks, requirements tools, personal access tokens and the OSLC provider.</summary>
public static class IntegrationsSetup
{
    public static IServiceCollection AddMeridianIntegrations(this IServiceCollection services)
    {
        services.AddDataProtection();
        services.AddHttpClient(IntegrationService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(20))
            .RedactLoggedHeaders(_ => true)   // never log Authorization or signature headers
            // Webhook and tracker URLs are chosen by project owners: on a team server they must not reach the server's own
            // network (cloud metadata, admin ports, internal services) unless an administrator allows private networks.
            .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<MeridianHostInfo>().LocalMode
                ? new SocketsHttpHandler()
                : OutboundGuard.CreateHandler(string.Equals(sp.GetRequiredService<IConfiguration>()[OutboundGuard.AllowPrivateNetworksKey], "true", StringComparison.OrdinalIgnoreCase)));
        services.AddSingleton(new WebhookOptions());
        services.AddSingleton<ProjectEvents>();
        services.AddSingleton<IntegrationService>();
        services.AddSingleton<Databases.DatabaseConnectionService>();   // live schema import (Integrations ▸ Databases)
        services.AddSingleton<WebhookDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<WebhookDispatcher>());
        Requirements.RequirementsToolsSetup.AddMeridianRequirementsTools(services);   // DOORS Next / Polarion connectors (Requirements/)
        return services;
    }

    public static WebApplication MapMeridianIntegrations(this WebApplication app)
    {
        app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<ProjectEvents>().FlushAll());
        // OSLC is for team servers; the desktop app's private local server is not reachable by other tools.
        if (!app.Services.GetRequiredService<MeridianHostInfo>().LocalMode) { OslcEndpoints.Map(app); McpEndpoints.Map(app); }   // MCP for AI clients (docs-site/docs/mcp.md)
        return app;
    }
}
