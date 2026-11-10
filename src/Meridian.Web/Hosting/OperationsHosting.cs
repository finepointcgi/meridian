using System.Diagnostics;
using Meridian.Data;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Meridian.Web.Hosting;

/// <summary>
/// Production operations: health endpoints, structured logging, request logging and OpenTelemetry export.
/// Everything is opt-in through configuration, so the desktop app and dev runs behave as before. See docs/DEPLOYMENT.md.
/// <list type="bullet">
/// <item><c>/healthz</c>: liveness (the process answers HTTP). <c>/readyz</c>: readiness (the database answers).</item>
/// <item><c>Meridian:Logging:Format=json</c>: one JSON object per log line on stdout.</item>
/// <item><c>Meridian:Logging:Requests=true</c>: one line per HTTP request (method, path, status, duration; never query strings, headers, cookies or bodies).</item>
/// <item><c>Meridian:Telemetry:OtlpEndpoint</c> (or the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>): traces and metrics over OTLP.</item>
/// </list>
/// </summary>
public static class OperationsHosting
{
    public const string LivenessPath = "/healthz";
    public const string ReadinessPath = "/readyz";
    private const string ReadyTag = "ready";

    public static void AddMeridianOperations(this WebApplicationBuilder builder)
    {
        var c = builder.Configuration;

        if (string.Equals(c["Meridian:Logging:Format"], "json", StringComparison.OrdinalIgnoreCase))
            builder.Logging.AddJsonConsole(o =>
            {
                o.IncludeScopes = true;
                o.UseUtcTimestamp = true;
                o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            });

        builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: [ReadyTag]);

        var endpoint = c["Meridian:Telemetry:OtlpEndpoint"] is { Length: > 0 } e ? e : c["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(endpoint)) return;

        var protocol = string.Equals(c["Meridian:Telemetry:Protocol"] ?? c["OTEL_EXPORTER_OTLP_PROTOCOL"], "http/protobuf", StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;
        var serviceName = c["Meridian:Telemetry:ServiceName"] is { Length: > 0 } n ? n : c["OTEL_SERVICE_NAME"] is { Length: > 0 } sn ? sn : "meridian";
        var version = typeof(OperationsHosting).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceVersion: version, serviceInstanceId: Environment.MachineName))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o =>
                {
                    o.Filter = http => !IsProbe(http.Request.Path);   // probes every few seconds are noise
                    o.RecordException = true;
                })
                .AddHttpClientInstrumentation()
                .AddSource("Npgsql"))                                // Npgsql emits its own activities
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Npgsql", "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.Http.Connections"));
        if (string.Equals(c["Meridian:Telemetry:ExportLogs"], "true", StringComparison.OrdinalIgnoreCase))
            otel.WithLogging(_ => { }, o => o.IncludeFormattedMessage = true);
        otel.UseOtlpExporter(protocol, new Uri(endpoint));
    }

    /// <summary>Call first after <c>Build()</c>, so the request log sees the final status code of every request.</summary>
    public static void UseMeridianRequestLogging(this WebApplication app)
    {
        if (!string.Equals(app.Configuration["Meridian:Logging:Requests"], "true", StringComparison.OrdinalIgnoreCase)) return;
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Meridian.Requests");
        app.Use(async (http, next) =>
        {
            if (IsProbe(http.Request.Path)) { await next(); return; }
            var start = Stopwatch.GetTimestamp();
            try { await next(); }
            finally
            {
                // Path only: query strings can carry launch tokens or search text, and headers carry cookies / bearer tokens.
                log.LogInformation("HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs:0.0} ms",
                    http.Request.Method, http.Request.Path.Value, http.Response.StatusCode, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        });
    }

    public static void MapMeridianHealth(this WebApplication app)
    {
        app.MapHealthChecks(LivenessPath, new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks(ReadinessPath, new HealthCheckOptions { Predicate = r => r.Tags.Contains(ReadyTag) }).AllowAnonymous();
    }

    private static bool IsProbe(PathString path) => path.StartsWithSegments(LivenessPath) || path.StartsWithSegments(ReadinessPath);
}

/// <summary>Readiness: one cheap indexed lookup against the configured store (PostgreSQL or SQLite), with a short timeout.</summary>
public sealed class DatabaseHealthCheck(IMeridianStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await store.FindUserAsync("__meridian_readiness_probe__").WaitAsync(TimeSpan.FromSeconds(5), ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // No exception text in the response: it can contain host names from the connection string.
            return HealthCheckResult.Unhealthy("Database unavailable", ex);
        }
    }
}

/// <summary>
/// <c>dotnet Meridian.Web.dll --healthcheck [url]</c>: exits 0 when the URL answers 2xx. Used by the container HEALTHCHECK,
/// so the runtime image needs no curl/wget.
/// </summary>
public static class HealthProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';', ',')[0] is { Length: > 0 } p ? p : "8080";
        var url = args.Length > 1 ? args[1]
            : Environment.GetEnvironmentVariable("MERIDIAN_HEALTHCHECK_URL") is { Length: > 0 } u ? u
            : $"http://127.0.0.1:{port}{OperationsHosting.LivenessPath}";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var response = await client.GetAsync(url);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch { return 1; }
    }
}
