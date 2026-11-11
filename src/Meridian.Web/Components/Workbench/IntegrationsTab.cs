namespace Meridian.Web.Components.Workbench;

/// <summary>
/// The tabs of the Integrations dialog, by key. Entry points that open a specific tab ("Connect a code repository…",
/// "Connect to a database…") name it with this, so adding a tab never sends them to the wrong one.
/// </summary>
public enum IntegrationsTab
{
    IssueTrackers,
    Webhooks,
    RequirementsTools,
    CodeRepositories,
    Databases,
}
