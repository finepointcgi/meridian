using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Web.Services;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>Review workflow and notification navigation commands (dock tab 9).</summary>
public partial class Workbench
{
    public const int ReviewsDock = 9;

    /// <summary>The review the Reviews panel should show; the nonce lets the same review be re-focused.</summary>
    private (long Id, int Nonce)? _reviewFocus;

    /// <summary>The elements a new review starts from: the canvas selection, else the selected element.</summary>
    private List<Element> SelectionForReview()
    {
        var list = new List<Element>();
        if (Ws.Active is { } c) list.AddRange(c.SelectedNodes.Select(n => n.Element));
        if (Ws.Selected is { } s && !list.Contains(s)) list.Insert(0, s);
        return list.Where(e => e.Owner != null && Ws.Model.Contains(e)).Distinct().ToList();
    }

    /// <summary>Opens the request-review dialog for the given elements (default: the selection) and shows the new review.</summary>
    public async Task RequestReview(IEnumerable<Element>? elements = null)
    {
        if (Ws.Session is null) { Snackbar.Add("Open a project first.", Severity.Info); return; }
        var initial = elements?.ToList() ?? SelectionForReview();
        var dialog = await Dialogs.ShowAsync<RequestReviewDialog>(Host.LocalMode ? "New review checklist" : "Request review",
            new DialogParameters<RequestReviewDialog> { { x => x.Initial, initial } },
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        var result = await dialog.Result;
        if (result is { Canceled: false, Data: long id })
        {
            Snackbar.Add(Host.LocalMode ? "Review checklist created" : "Review requested", Severity.Success);
            OpenReview(id);
        }
    }

    /// <summary>Shows a review in the Reviews dock panel.</summary>
    public void OpenReview(long id)
    {
        _reviewFocus = (id, (_reviewFocus?.Nonce ?? 0) + 1);
        OpenDock(ReviewsDock);
        StateHasChanged();
    }

    /// <summary>Follows a notification: opens its project if needed, then selects the element and/or opens the review.</summary>
    public async Task GoToNotificationAsync(NotificationInfo n)
    {
        if (n is { Kind: NotificationKinds.LibraryUpdate, ProjectId: { } usingProject })
        {
            await ReviewLibraryUpdateAsync(usingProject, n.ElementId);   // "Review update" (Workbench.Libraries.cs)
            StateHasChanged();
            return;
        }
        if (n.ProjectId is { } pid && Ws.Session?.ProjectId != pid) await OpenProject(pid);
        if (n.ProjectId is { } p && Ws.Session?.ProjectId != p) return;
        if (n.ElementId != null && Ws.Model.Find(n.ElementId) is { } e) Navigate(e);
        if (n.ReviewId is { } r) OpenReview(r);
        StateHasChanged();
    }
}
