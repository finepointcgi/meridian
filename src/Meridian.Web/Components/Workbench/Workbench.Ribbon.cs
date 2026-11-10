using Microsoft.JSInterop;

namespace Meridian.Web.Components.Workbench;

/// <summary>
/// The tabbed ribbon: the group names form a tab strip and only the chosen group's commands show, with their captions,
/// so the ribbon fits any window without scrolling sideways. The chosen tab is remembered per browser.
/// </summary>
public partial class Workbench
{
    private const string RibbonTabKey = "meridian.ribbonTab";
    private const string DefaultRibbonTab = "create";
    private string _ribbonTab = DefaultRibbonTab;

    /// <summary>The ribbon tabs in order; Code only when the project has code changes or a connected repository.</summary>
    private IEnumerable<(string Id, string Label)> RibbonTabs()
    {
        yield return ("create", "Create");
        yield return ("diagram", "Diagram");
        yield return ("requirements", "Requirements");
        yield return ("trace", "Traceability");
        yield return ("review", "Review");
        yield return ("views", "Model views");
        yield return ("simulate", "Simulate");
        yield return ("generate", "Generate");
        yield return ("extend", "Extend");
        yield return ("ai", "AI");
        if (ShowDatabaseTools) yield return ("database", "Database");
        if (HasCodeGroup) yield return ("code", "Code");
    }

    private bool HasCodeGroup => Ws.CodeChanges is not null || HasArchitecture;

    /// <summary>The tab showing now (falls back to Create when the remembered tab no longer exists, e.g. Code).</summary>
    private string ActiveRibbonTab =>
        (_ribbonTab == "code" && !HasCodeGroup) || (_ribbonTab == "database" && !ShowDatabaseTools) ? DefaultRibbonTab : _ribbonTab;

    /// <summary>"active" for the group of the tab showing now (the CSS hides the other groups).</summary>
    private string RibbonGroupClass(string id) => ActiveRibbonTab == id ? "active" : "";

    private async Task SelectRibbonTab(string id)
    {
        _ribbonTab = id;
        try { await JS.InvokeVoidAsync("meridian.setFlag", RibbonTabKey, id); } catch (JSException) { }
    }

    private async Task RestoreRibbonTabAsync()
    {
        try
        {
            if (await JS.InvokeAsync<string?>("meridian.getFlag", RibbonTabKey) is { Length: > 0 } saved && saved != _ribbonTab)
            {
                _ribbonTab = saved;
                StateHasChanged();
            }
        }
        catch (JSException) { }
    }

    /// <summary>
    /// Shows the ribbon tab holding a tour target (<c>[data-tour=…]</c>) so the tour can spotlight it.
    /// </summary>
    private Task RevealForTour(string selector)
    {
        string? tab = selector switch
        {
            _ when selector.Contains("data-tour=create-from") || selector.Contains("data-tour=auto-diagram") => "diagram",
            _ when selector.Contains("data-tour=assistant") => "ai",
            _ when selector.Contains("data-tour=code-group") && HasCodeGroup => "code",
            _ => null,
        };
        if (tab is not null && tab != ActiveRibbonTab)
        {
            _ribbonTab = tab;
            StateHasChanged();
        }
        return Task.CompletedTask;
    }
}
