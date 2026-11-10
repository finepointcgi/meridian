namespace Meridian.Web.Admin;

/// <summary>
/// Baseline response headers: no MIME sniffing, no framing by other sites (clickjacking), no referrer to other origins.
/// OSLC delegated dialogs (<c>/oslc/…</c>) are meant to be framed by other tools, so they keep framing allowed and set
/// their own policy. A Content-Security-Policy already set by an endpoint (images, OSLC) is left alone.
/// </summary>
public static class SecurityHeaders
{
    public const string Policy = "frame-ancestors 'self'; object-src 'none'; base-uri 'self'";

    public static void UseMeridianSecurityHeaders(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        http.Response.OnStarting(() =>
        {
            Apply(http);
            return Task.CompletedTask;
        });
        await next();
    });

    public static void Apply(HttpContext http)
    {
        var h = http.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        if (!h.ContainsKey("Referrer-Policy")) h["Referrer-Policy"] = "same-origin";
        if (http.Request.Path.StartsWithSegments("/oslc")) return;
        h.XFrameOptions = "SAMEORIGIN";
        if (!h.ContainsKey("Content-Security-Policy")) h.ContentSecurityPolicy = Policy;
    }
}
