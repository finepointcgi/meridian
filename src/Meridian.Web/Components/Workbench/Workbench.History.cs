using Meridian.Core.Model;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>Element history: "History…" in the context menus and the read-only view of the model at a version.</summary>
public partial class Workbench
{
    private IEnumerable<MenuEntry> HistoryMenuEntries(Element e)
    {
        if (Ws.Session is null || Ws.Pinned != null || Ws.LibraryOf(e) is not null || e.Kind == ElementKind.Model) yield break;
        yield return MenuEntry.Async("History…", () => ShowElementHistory(e));
    }

    /// <summary>Opens an element's history (who changed it and when, with field diffs).</summary>
    public async Task ShowElementHistory(Element e)
    {
        if (Ws.Session is null) return;
        var parameters = new DialogParameters<ElementHistoryDialog> { { x => x.Element, e }, { x => x.Host, this } };
        await Dialogs.ShowAsync<ElementHistoryDialog>($"History of {e.DisplayName}", parameters,
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true, CloseButton = true });
    }

    /// <summary>"Show model at this version": the read-only historical view, with the element selected if it was there.</summary>
    public async Task ShowModelAtVersion(long versionId, int number, string? elementId)
    {
        if (await Ws.OpenVersionAsync(Db, Hub, versionId, number) is { } why) { Snackbar.Add(why, Severity.Warning); return; }
        _csvTarget = Ws.Model.Root;
        if (Ws.Model.Find(elementId) is { } there) Navigate(there);
        Ws.Log($"Opened version {number} (read-only)");
    }
}
