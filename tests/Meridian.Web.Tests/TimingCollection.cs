namespace Meridian.Web.Tests;

/// <summary>
/// Tests that assert latency or duration budgets run in this collection: xUnit runs it on its own, after the parallel
/// collections, so the budgets measure Meridian and not the other tests competing for the same cores (CI runners have few).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing budgets";
}
