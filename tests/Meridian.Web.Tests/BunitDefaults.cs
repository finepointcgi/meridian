using System.Runtime.CompilerServices;

namespace Meridian.Web.Tests;

/// <summary>
/// bUnit's "WaitFor" helpers (WaitForAssertion, WaitForState, WaitForElement) give up after 1 second unless a test passes
/// its own timeout. Components that render after background work (report jobs, code sync, collaboration) can take longer
/// than that on a busy CI runner, which failed the 0.11.0 release's test job. A wait returns as soon as its condition
/// holds, so a longer default only changes how long a test that really fails takes to say so.
/// </summary>
internal static class BunitDefaults
{
    [ModuleInitializer]
    internal static void Initialize() => Bunit.TestContextBase.DefaultWaitTimeout = TimeSpan.FromSeconds(10);
}
