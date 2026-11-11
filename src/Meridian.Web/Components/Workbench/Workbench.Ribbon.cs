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
        yield return ("views", "Model views");
        yield return ("simulate", "Simulate");
        yield return ("generate", "Generate");
        yield return ("ai", "AI");
        if (ShowDatabaseTools) yield return ("database", "Database");
        if (HasCodeGroup) yield return ("code", "Code");
    }

    private bool HasCodeGroup => Ws.CodeChanges is not null || HasArchitecture;

    /// <summary>
    /// Tabs merged into others in 0.7.1 (docs/UX-AUDIT-0.7.md, decision 1): Review went into Traceability and Extend into
    /// Generate. A tab id remembered from an older version opens the tab its buttons moved to.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> MovedRibbonTabs = new Dictionary<string, string>
    {
        ["review"] = "trace",
        ["extend"] = "generate",
    };

    /// <summary>A remembered or requested tab id with merged tabs mapped to where their buttons are now.</summary>
    public static string CurrentRibbonTabId(string id) => MovedRibbonTabs.TryGetValue(id, out var moved) ? moved : id;

    /// <summary>The tab showing now (falls back to Create when the remembered tab no longer exists, e.g. Code or Database).</summary>
    private string ActiveRibbonTab => RibbonTabs().Any(t => t.Id == _ribbonTab) ? _ribbonTab : DefaultRibbonTab;

    /// <summary>"active" for the group of the tab showing now (the CSS hides the other groups).</summary>
    private string RibbonGroupClass(string id) => ActiveRibbonTab == id ? "active" : "";

    private async Task SelectRibbonTab(string id)
    {
        id = CurrentRibbonTabId(id);
        _ribbonTab = id;
        try { await JS.InvokeVoidAsync("meridian.setFlag", RibbonTabKey, id); } catch (JSException) { }
    }

    private async Task RestoreRibbonTabAsync()
    {
        try
        {
            if (await JS.InvokeAsync<string?>("meridian.getFlag", RibbonTabKey) is { Length: > 0 } saved && CurrentRibbonTabId(saved) != _ribbonTab)
            {
                _ribbonTab = CurrentRibbonTabId(saved);
                StateHasChanged();
            }
        }
        catch (JSException) { }
    }

    /// <summary>The ribbon tab that holds each tour target (<c>data-tour</c> value), so the tour can switch to it first.</summary>
    public static readonly IReadOnlyDictionary<string, string> TourTargetTabs = new Dictionary<string, string>
    {
        ["create-from"] = "diagram",
        ["auto-diagram"] = "diagram",
        ["assistant"] = "ai",
        ["code-group"] = "code",
        ["verify"] = "simulate",
        ["used-projects"] = "views",
        ["database"] = "database",
    };

    /// <summary>The ribbon tab of the first <c>[data-tour=…]</c> in a tour step's selector, or null.</summary>
    public static string? TourTargetTab(string selector)
    {
        var m = System.Text.RegularExpressions.Regex.Match(selector, @"data-tour=([\w-]+)");
        return m.Success && TourTargetTabs.TryGetValue(m.Groups[1].Value, out var tab) ? tab : null;
    }

    /// <summary>
    /// Shows the ribbon tab holding a tour target (<c>[data-tour=…]</c>) so the tour can spotlight it.
    /// </summary>
    private Task RevealForTour(string selector)
    {
        var tab = TourTargetTab(selector);
        if (tab == "code" && !HasCodeGroup || tab == "database" && !ShowDatabaseTools) tab = null;
        if (tab is not null && tab != ActiveRibbonTab)
        {
            _ribbonTab = tab;
            StateHasChanged();
        }
        return Task.CompletedTask;
    }
}
