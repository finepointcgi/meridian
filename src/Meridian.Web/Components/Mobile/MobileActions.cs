using Meridian.Core.Model;
using Meridian.Web.Services;

namespace Meridian.Web.Components.Mobile;

/// <summary>The element actions shared by the bottom sheet and the long-press menu: Comment, Open diagram, Show in tree.</summary>
public static class MobileActions
{
    /// <summary>The diagram an element opens: itself, else the first diagram it owns.</summary>
    public static Diagram? DiagramFor(Element e) => Workbench.Workbench.DiagramFor(e);

    public static void Comment(Workspace ws, MobileUiState ui, Element e)
    {
        ws.Select(e);
        if (ui.IsPhone) ui.Show(MobileUiState.View.Comments);
        else ui.OpenSheet(e, expanded: true);
    }

    public static bool OpenDiagram(Workspace ws, MobileUiState ui, Element e)
    {
        if (DiagramFor(e) is not { } d) return false;
        ws.Open(d);
        if (ui.IsPhone) ui.Show(MobileUiState.View.Diagram);
        else { ui.CloseMenu(); ui.CloseSheet(); }
        return true;
    }

    public static void ShowInTree(Workspace ws, MobileUiState ui, Element e)
    {
        ws.Select(e);
        ui.ShowInTree(e);
    }

    /// <summary>A model field key as a label: "reqId" → "Req id".</summary>
    public static string FieldLabel(string key)
    {
        if (key.Length == 0) return key;
        var sb = new System.Text.StringBuilder();
        sb.Append(char.ToUpperInvariant(key[0]));
        for (var i = 1; i < key.Length; i++)
        {
            if (char.IsUpper(key[i])) sb.Append(' ').Append(char.ToLowerInvariant(key[i]));
            else sb.Append(key[i]);
        }
        return sb.ToString();
    }
}
