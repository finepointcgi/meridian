using Meridian.Web.Hosting;
using Microsoft.JSInterop;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>
/// The bundled user guide (/guide, Hosting/UserGuide.cs): Help ▸ User guide, ⌘K, the phone account menu, and "Learn more" on
/// error messages. It opens in a new browser tab, or in the system browser from the desktop app.
/// </summary>
public partial class Workbench
{
    /// <summary>Opens the user guide at a page (<see cref="UserGuide.Pages"/>), or its home page.</summary>
    public async Task OpenGuide(string? page = null)
    {
        try { await JS.InvokeVoidAsync("meridian.openGuide", UserGuide.Page(page)); }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException) { }
    }

    /// <summary>An error snackbar with a "Learn more" button that opens the guide page about what failed.</summary>
    private void ErrorWithGuide(string message, string page) =>
        Snackbar.Add(message, Severity.Error, o =>
        {
            o.Action = "Learn more";
            o.ActionColor = Color.Inherit;
            o.OnClick = _ => OpenGuide(page);
        });
}
