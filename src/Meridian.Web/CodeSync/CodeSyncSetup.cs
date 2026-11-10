using Meridian.Core.CodeSync;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meridian.Web.CodeSync;

/// <summary>Registers the code-sync review branch runner and the review (accept / reject) service.</summary>
public static class CodeSyncSetup
{
    public static IServiceCollection AddMeridianCodeSync(this IServiceCollection services)
    {
        services.AddSingleton<CodeSyncEvents>();
        // The code-to-model mapper; a host may register another applier first.
        services.TryAddSingleton<ICodeModelApplier, CodeModelApplier>();
        services.AddSingleton<CodeSyncRunner>();
        // A plain Add, so it wins over the fallback runner the git-host integration registers with TryAdd (in either order).
        services.AddSingleton<ICodeSyncRunner>(sp => sp.GetRequiredService<CodeSyncRunner>());
        services.AddSingleton<CodeSyncReviewService>();
        // "auto" first: the runner takes the first reader that handles the pushed files, and the host has already narrowed
        // them to the connection's language. The single-language readers are found by their Language name.
        services.AddSingleton<ICodeReader, Meridian.Serialization.CodeSync.MultiLanguageCodeReader>();
        services.AddSingleton<ICodeReader, Meridian.Serialization.CodeSync.CSharpCodeReader>();
        services.AddSingleton<ICodeReader, Meridian.Serialization.CodeSync.TypeScriptCodeReader>();
        services.AddSingleton<ICodeReader, Meridian.Serialization.CodeSync.JavaCodeReader>();
        services.AddSingleton<ICodeReader, Meridian.Serialization.CodeSync.PythonCodeReader>();
        services.AddHostedService<CodeSyncStatusBridge>();   // reviewed runs turn the commit status green
        services.AddScoped<CodeSyncState>();   // per browser tab: badge, "What changed" tab, diagram highlights
        return services;
    }
}

/// <summary>Posts the final commit status to the git host when a code-sync run has been reviewed (accepted or rejected).</summary>
public sealed class CodeSyncStatusBridge(CodeSyncReviewService review, IServiceProvider services, ILogger<CodeSyncStatusBridge> log) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        review.Accepted += OnReviewed;
        review.Rejected += OnReviewed;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        review.Accepted -= OnReviewed;
        review.Rejected -= OnReviewed;
        return Task.CompletedTask;
    }

    private void OnReviewed(CodeSyncOutcome outcome, Guid connectionId, string commit) => _ = ReportAsync(outcome, connectionId, commit);

    private async Task ReportAsync(CodeSyncOutcome outcome, Guid connectionId, string commit)
    {
        try
        {
            if (services.GetService<Hosts.CodeSyncHostService>() is { } hosts) await hosts.ReportReviewedAsync(connectionId, commit, outcome);
        }
        catch (Exception ex) { log.LogWarning(ex, "Reporting a reviewed code sync for {Connection} failed", connectionId); }
    }
}
