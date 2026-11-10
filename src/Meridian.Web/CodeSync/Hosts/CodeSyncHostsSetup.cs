using Meridian.Core.CodeSync;
using Meridian.Web.Integrations;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meridian.Web.CodeSync.Hosts;

/// <summary>Registers git host connections, the webhook endpoint and the background sync worker.</summary>
public static class CodeSyncHostsSetup
{
    public static IServiceCollection AddMeridianCodeSyncHosts(this IServiceCollection services)
    {
        // Same outbound guard as issue trackers and webhooks (no loopback / private / metadata addresses on a team server,
        // redirects included). A separate client only because archive downloads need a longer timeout than API calls.
        services.AddHttpClient(CodeSyncHostService.HttpClientName, c => c.Timeout = GitHostBase.DownloadTimeout + TimeSpan.FromMinutes(1))
            .RedactLoggedHeaders(_ => true)
            .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<MeridianHostInfo>().LocalMode
                ? new SocketsHttpHandler()
                : OutboundGuard.CreateHandler(string.Equals(sp.GetRequiredService<IConfiguration>()[OutboundGuard.AllowPrivateNetworksKey], "true", StringComparison.OrdinalIgnoreCase)));
        services.TryAddSingleton(_ => new CodeSyncQueue());
        services.TryAddSingleton(_ => new CodeSyncRateLimiter());
        services.TryAddSingleton<CodeSyncHostService>();
        services.AddHostedService<CodeSyncWorker>();
        // The review-branch runner replaces this; CodeSyncHostService.ResolveRunner prefers any real runner regardless of order.
        services.TryAddSingleton<ICodeSyncRunner, NullCodeSyncRunner>();
        return services;
    }

    public static WebApplication MapMeridianCodeSyncHosts(this WebApplication app)
    {
        // Called by GitHub / GitLab: authenticated by the per-connection webhook secret, not by a user session.
        app.MapPost("/api/code-sync/{connectionId:guid}/webhook", async (HttpContext http, Guid connectionId, CodeSyncHostService service) =>
        {
            if (http.Request.ContentLength > CodeSyncHostService.MaxWebhookBytes) return Results.Text("Payload too large.", statusCode: 413);
            var body = await ReadBodyAsync(http.Request.Body, CodeSyncHostService.MaxWebhookBytes, http.RequestAborted);
            if (body is null) return Results.Text("Payload too large.", statusCode: 413);
            var result = await service.HandleWebhookAsync(connectionId, http.Request.Headers, body, http.RequestAborted);
            return Results.Text(result.Message, statusCode: result.StatusCode);
        }).AllowAnonymous().DisableAntiforgery();
        return app;
    }

    /// <summary>Reads at most <paramref name="max"/> bytes; null if the body is longer.</summary>
    internal static async Task<byte[]?> ReadBodyAsync(Stream body, int max, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await body.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > max) return null;
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }
}
