using Meridian.Web.CodeSync;
using Meridian.Web.Services;
using Meridian.Web.Settings;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>
/// What the "code changes waiting for review" notices share (the banner over the canvas, its phone version and the
/// empty-project hint): their text, the dismissal kept in the user's preferences, and accepting into main with the
/// usual confirmation and conflict dialog (also used by the "What changed" tab).
/// </summary>
public static class CodeReviewNotice
{
    /// <summary>"12 code changes from acme/sat are waiting for review (commit abc1234)".</summary>
    public static string Text(CodeSyncReview review)
    {
        var newest = review.Runs.OrderByDescending(r => r.CreatedAt).First();
        var n = review.Changes.Count;
        return $"{n} code change{(n == 1 ? "" : "s")} from {newest.Repo} {(n == 1 ? "is" : "are")} waiting for review (commit {newest.ShortCommit})";
    }

    /// <summary>When this user dismissed the notice of the open project (null: never, or not signed in).</summary>
    public static DateTime? DismissedAt(Workspace ws) =>
        ws.Session is { } s && ws.Preferences.CodeReviewsDismissed.TryGetValue(s.ProjectId.ToString(), out var at) ? at : null;

    /// <summary>Hides the notice until a newer push arrives: remembers the newest waiting push, in the user's preferences.</summary>
    public static async Task DismissAsync(Workspace ws, ThemeService? theme, IReadOnlyList<CodeSyncReview> shown)
    {
        if (ws.Session is not { } s || shown.Count == 0) return;
        var key = s.ProjectId.ToString();
        var newest = shown.SelectMany(r => r.Runs).Max(r => r.CreatedAt);
        if (theme is not null && theme.UserId is not null) await theme.UpdateAsync(p => p.CodeReviewsDismissed[key] = newest);
        else
        {
            ws.Preferences.CodeReviewsDismissed[key] = newest;   // no signed-in preferences: for this tab only
            ws.Bump();
        }
    }

    /// <summary>Switches to the review branch and opens "What changed" (on a phone, the phone's code changes view).</summary>
    public static async Task OpenBranchAsync(Workbench? shell, Workspace ws, CodeSyncReview review, MobileUiState? phone = null)
    {
        if (shell is not null && review.Branch.BranchId != ws.BranchId) await shell.SwitchBranch(review.Branch);
        if (phone is { IsPhone: true }) phone.Show(MobileUiState.View.CodeChanges);
        else ws.OpenView(ViewTabKind.CodeChanges);
    }

    /// <summary>"Accept all into main" for one review: asks first, then accepts its runs (the merge dialog settles conflicts).</summary>
    public static async Task<bool> AcceptAllAsync(Workspace ws, CodeSyncReviewService service, IDialogService dialogs, ISnackbar snackbar, CodeSyncReview review)
    {
        var n = review.Changes.Count;
        var (title, message) = ("Accept code changes", $"Merge {n} code change{(n == 1 ? "" : "s")} from {review.Branch.Name} into main?");
        if (ws.ConfirmAction is { } ask ? !await ask(title, message, "Accept") : ws.Confirm is { } confirm && !await confirm(title, message))
            return false;
        return await AcceptAsync(ws, service, dialogs, snackbar, review.Runs.Select(r => r.Id).ToList(), null);
    }

    /// <summary>
    /// Accepts code changes into main: <paramref name="runIds"/> (null = every open run) limited to <paramref name="elementIds"/>
    /// (null = all). Conflicts with edits on main open the merge dialog until they are settled or the user stops. Reports the
    /// result in a snackbar and the log; returns whether anything was decided.
    /// </summary>
    public static async Task<bool> AcceptAsync(Workspace ws, CodeSyncReviewService service, IDialogService dialogs, ISnackbar snackbar,
                                               IReadOnlyCollection<Guid>? runIds, IReadOnlyCollection<string>? elementIds)
    {
        if (ws.Session is not { } s || ws.User is not { } me) return false;
        try
        {
            var result = await service.AcceptAsync(s.ProjectId, runIds, elementIds, me);
            while (result.NeedsResolution is { } pending)
            {
                var dialog = await dialogs.ShowAsync<MergeDialog>("Accept code changes", new DialogParameters<MergeDialog> { { x => x.Plan, pending.Plan } },
                    new DialogOptions { MaxWidth = MaxWidth.ExtraLarge, FullWidth = true });
                if (await dialog.Result is not { Canceled: false, Data: MergeDialog.Decision decision })
                {
                    snackbar.Add("Nothing more was accepted; the changes are still waiting for review.", Severity.Info);
                    return false;
                }
                try { result = await service.CompleteAcceptAsync(pending, decision.Merged, me); }
                catch (MergeOutdatedException ex)
                {
                    snackbar.Add(ex.Message, Severity.Info);
                    result = await service.AcceptAsync(s.ProjectId, runIds, elementIds, me);
                }
            }
            ws.Log(result.Message);
            snackbar.Add(result.Message, Severity.Success);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            snackbar.Add(ex.Message, Severity.Warning);
            return false;
        }
    }
}
