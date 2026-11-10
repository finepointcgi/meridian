using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Web.Services;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>Branches, merges, baselines and element locks (Teamwork-Cloud-style version control).</summary>
public partial class Workbench
{
    /// <summary>Re-opens the current project on another line (main or a branch). Collaboration sessions are per branch.</summary>
    public async Task SwitchBranch(BranchRef line)
    {
        if (Ws.Session is not { } s || Ws.User is null) return;
        if (s.BranchId == line.BranchId) return;
        var role = await Db.RoleOfAsync(s.ProjectId, Ws.User.Id);
        if (role is null) { Snackbar.Add("You don't have access to that project.", Severity.Warning); return; }
        await s.PersistAsync();
        if (!await Ws.JoinAsync(Hub, s.ProjectId, role.Value, line.BranchId))
        {
            Snackbar.Add($"Branch “{line.Name}” no longer exists.", Severity.Warning);
            await Ws.JoinAsync(Hub, s.ProjectId, role.Value);
            return;
        }
        _csvTarget = Ws.Model.Root;
        Ws.Log($"Switched to branch “{line.Name}”");
        Ws.SetStatus($"On branch {line.Name}");
    }

    /// <summary>Creates a branch (from the current head, or from a committed version) and switches to it.</summary>
    public async Task NewBranch(long? fromVersionId = null)
    {
        if (Ws.Session is not { } s || Ws.User is null || Ws.ReadOnly) return;
        var parameters = new DialogParameters<NewBranchDialog> { { x => x.FromVersionId, fromVersionId } };
        var dialog = await Dialogs.ShowAsync<NewBranchDialog>("New branch", parameters, new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        if (await dialog.Result is not { Canceled: false, Data: NewBranchDialog.Request req }) return;
        try
        {
            var from = new BranchRef(s.BranchId, s.BranchName);
            var id = await VersionControl.CreateBranchAsync(Db, Hub, s.ProjectId, req.Name, from, req.VersionId, Ws.User.Id);
            Snackbar.Add($"Created branch “{req.Name.Trim()}”", Severity.Success);
            await SwitchBranch(new BranchRef(id, req.Name.Trim()));
        }
        catch (InvalidOperationException ex) { Snackbar.Add(ex.Message, Severity.Warning); }
    }

    public async Task ShowBranches()
    {
        if (Ws.Session is null) return;
        var dialog = await Dialogs.ShowAsync<BranchesDialog>("Branches", new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        switch ((await dialog.Result)?.Data)
        {
            case BranchesDialog.Action { Kind: "open" } a: await SwitchBranch(a.Line); break;
            case BranchesDialog.Action { Kind: "merge-into" } a: await Merge(Current, a.Line); break;
            case BranchesDialog.Action { Kind: "merge-from" } a: await Merge(a.Line, Current); break;
            case BranchesDialog.Action { Kind: "new" }: await NewBranch(); break;
        }
    }

    public async Task ShowBaselines()
    {
        if (Ws.Session is null) return;
        await Dialogs.ShowAsync<BaselinesDialog>("Baselines", new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true });
    }

    private BranchRef Current => new(Ws.Session?.BranchId, Ws.BranchName);

    /// <summary>Three-way merge of <paramref name="source"/> into <paramref name="target"/>; conflicts are resolved in the merge dialog.</summary>
    public async Task Merge(BranchRef source, BranchRef target)
    {
        if (Ws.Session is not { } s || Ws.User is null || Ws.ReadOnly) return;
        while (true)
        {
            MergePlan plan;
            try { plan = await VersionControl.PrepareMergeAsync(Db, Hub, s.ProjectId, source, target); }
            catch (InvalidOperationException ex) { Snackbar.Add(ex.Message, Severity.Warning); return; }

            var dialog = await Dialogs.ShowAsync<MergeDialog>("Merge", new DialogParameters<MergeDialog> { { x => x.Plan, plan } },
                new DialogOptions { MaxWidth = MaxWidth.ExtraLarge, FullWidth = true });
            if (await dialog.Result is not { Canceled: false, Data: MergeDialog.Decision decision }) return;
            try
            {
                var v = await VersionControl.CompleteMergeAsync(Db, Hub, plan, decision.Merged, decision.Message, Ws.User.Id);
                Ws.Log($"Merged “{source.Name}” into “{target.Name}” as v{v.Number}");
                if (target.BranchId == Ws.BranchId) Snackbar.Add($"Merged “{source.Name}” into “{target.Name}” (v{v.Number})", Severity.Success);
                else
                    Snackbar.Add($"Merged “{source.Name}” into “{target.Name}” (v{v.Number})", Severity.Success, c =>
                    {
                        c.Action = $"Open {target.Name}";
                        c.OnClick = _ => SwitchBranch(target);
                    });
                return;
            }
            catch (MergeOutdatedException ex) { Snackbar.Add(ex.Message, Severity.Info); }   // prepare again with the newer target
            catch (InvalidOperationException ex) { Snackbar.Add(ex.Message, Severity.Error); return; }
        }
    }

    // ------------------------------------------------------------ element locks (server mode)

    private IEnumerable<MenuEntry> LockEntries(Element e)
    {
        if (Host.LocalMode || Ws.Session is not { } s || Ws.User is not { } me || Ws.ReadOnly || e.Owner is null) yield break;
        var held = s.Locks.FirstOrDefault(l => l.ElementId == e.Id);
        if (held != null && held.User.Id == me.Id)
        {
            yield return MenuEntry.Do("Unlock", () => Unlock(e));
            yield break;
        }
        if (Ws.LockOf(e) is { } other && other.User.Id != me.Id)
        {
            yield return MenuEntry.Title($"Locked by {other.User.DisplayName}");
            if (Ws.Role == ProjectRole.Owner && held != null) yield return MenuEntry.Do("Break lock", () => Unlock(e, force: true));
            yield break;
        }
        yield return MenuEntry.Do("Lock for editing", () => Lock(e, subtree: false));
        if (e.OwnedElements.Count > 0) yield return MenuEntry.Do("Lock with contents", () => Lock(e, subtree: true));
    }

    private void Lock(Element e, bool subtree)
    {
        if (Ws.Session is not { } s) return;
        var problem = s.TryLock(Ws.ParticipantId, e.Id, subtree);
        Ws.SetStatus(problem ?? $"Locked {e.DisplayName}{(subtree ? " and its contents" : "")} — others can't edit it until you unlock or leave");
        if (problem != null) Snackbar.Add(problem, Severity.Warning);
    }

    private void Unlock(Element e, bool force = false)
    {
        if (Ws.Session is not { } s || Ws.User is null) return;
        Ws.SetStatus(s.Unlock(e.Id, Ws.User.Id, force) ? $"Unlocked {e.DisplayName}" : "That lock is no longer held");
    }
}
