using Meridian.Core.Model;
using Microsoft.JSInterop;

namespace Meridian.Web.Services;

/// <summary>Follows shape hyperlinks: web links open in a new tab (or the system browser on desktop), internal ones navigate.</summary>
public static class DiagramLinks
{
    public static async Task FollowAsync(Workspace ws, IJSRuntime js, string? link)
    {
        if (string.IsNullOrEmpty(link)) return;
        if (DiagramExtras.InternalTarget(link) is { } id)
        {
            Navigate(ws, id);
            return;
        }
        if (DiagramExtras.NormalizeLink(link) is not { } url) { ws.SetStatus("That link isn't a valid web address"); return; }
        await js.InvokeVoidAsync("meridian.openUrl", url);
        ws.SetStatus($"Opened {url}");
    }

    /// <summary>Opens a linked diagram, or shows a linked element: on the active diagram if it's there, else on the first diagram showing it.</summary>
    public static void Navigate(Workspace ws, string elementId)
    {
        var e = ws.Model.Find(elementId);
        if (e == null) { ws.SetStatus("The linked element no longer exists"); return; }
        if (e is Diagram d)
        {
            ws.Open(d);
            ws.SetStatus($"Opened {d.Name}");
            return;
        }
        var host = ws.Active != null && ws.Active.Diagram.Shows(e) ? ws.Active.Diagram
            : ws.Model.Diagrams.Where(x => x.Shows(e)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (host != null)
        {
            var c = ws.Open(host);
            ws.Select(e);
            if (host.NodeFor(e) is { } node) c.EnsureVisible(c.BoundsOf(node));
        }
        else ws.Select(e);
        ws.SetStatus($"Went to {e.DisplayName}");
    }

    /// <summary>Short human description of a link for tooltips and the link dialog.</summary>
    public static string Describe(Workspace ws, string? link)
    {
        if (string.IsNullOrEmpty(link)) return "No link";
        if (DiagramExtras.InternalTarget(link) is { } id)
            return ws.Model.Find(id) is { } e ? $"{(e is Diagram ? "Diagram" : UiKinds.KindName(e))}: {e.DisplayName}" : "Missing element";
        return link;
    }
}
