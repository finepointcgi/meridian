using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>The one confirmation for actions that cannot be undone: "Delete X?" with what is lost, Cancel and a named action.</summary>
public static class Confirm
{
    /// <summary>True when the user confirmed. <paramref name="consequence"/> says what is lost; the action button is <paramref name="action"/>.</summary>
    public static async Task<bool> AskAsync(IDialogService dialogs, string title, string consequence, string action = "Delete") =>
        await dialogs.ShowMessageBox(title, consequence + " This cannot be undone.", yesText: action, cancelText: "Cancel") == true;
}
