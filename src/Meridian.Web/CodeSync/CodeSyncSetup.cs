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
        services.AddScoped<CodeSyncState>();   // per browser tab: badge, "What changed" tab, diagram highlights
        return services;
    }
}
