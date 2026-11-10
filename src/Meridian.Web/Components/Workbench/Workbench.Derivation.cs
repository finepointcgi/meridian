using Meridian.Core.Derivation;
using Meridian.Core.Model;
using Meridian.Web.Services;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>
/// "Create diagrams from diagrams" (Meridian.Core.Derivation): the tree / canvas context menus, the ribbon's "Create from…",
/// ⌘K entries, the "source changed" badge's "Update from source…" and the inspector's derivation chain all come here.
/// </summary>
public partial class Workbench
{
    /// <summary>The optional AI fill-in, when the assistant is on and this user accepted its data notice.</summary>
    private IDerivationAssistant? DerivationAssistant() =>
        (Services.GetService(typeof(AssistantService)) as AssistantService)?.DerivationAssistant();

    /// <summary>Reviews and applies one derivation (one undo step). Created diagrams open as tabs; an updated one is brought forward.</summary>
    public async Task DeriveAsync(DerivationOffer offer)
    {
        if (Ws.ReadOnly) { Ws.SetStatus("View-only: you can't derive diagrams in this project"); return; }
        if (!Ws.Model.Contains(offer.Source)) return;
        var parameters = new DialogParameters<DeriveDialog>
        {
            { x => x.Kind, offer.Kind }, { x => x.Source, offer.Source }, { x => x.Target, offer.Target }, { x => x.Assistant, DerivationAssistant() },
        };
        var dialog = await Dialogs.ShowAsync<DeriveDialog>(offer.Label, parameters,
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true, CloseOnEscapeKey = true });
        if (await dialog.Result is not { Canceled: false, Data: DeriveDialog.Answer answer }) return;
        var plan = answer.Plan;
        if (answer.Dismissed)
        {
            var n = DiagramDerivation.Dismiss(Ws.Model, plan.Kind, plan.Source, plan.Target);
            Ws.SetStatus(n > 0 ? $"Kept the derived {DerivationKinds.Product(plan.Kind)} as it is; {plan.Source.DisplayName}'s change is dismissed (⌘Z to undo)" : "Nothing to dismiss");
            return;
        }
        if (Ws.IsLockedForMe(plan.Source))
        {
            Ws.SetStatus($"'{plan.Source.DisplayName}' is locked by {Ws.LockOf(plan.Source)?.User.DisplayName ?? "someone else"}");
            return;
        }
        DerivationResult result;
        try { result = DiagramDerivation.Apply(Ws.Model, plan); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            Ws.SetStatus("Derivation failed: " + ex.Message);
            Snackbar.Add("Derivation failed: " + ex.Message, Severity.Error);
            return;
        }
        // New diagrams open as tabs by themselves (the last one ends up active); an updated diagram is brought forward.
        if (plan.IsUpdate && result.Primary is { } updated) Ws.Open(updated);
        Ws.Log(result.Summary);
        Ws.SetStatus(result.Summary);
    }

    /// <summary>The "source changed" badge's action: the update dialog for the derivation that made <paramref name="d"/>.</summary>
    public Task UpdateFromSource(Diagram d)
    {
        if (DiagramDerivation.Origin(Ws.Model, d) is not { } o) return Task.CompletedTask;
        return DeriveAsync(new DerivationOffer(o.Kind, o.Source, o.Target, $"Update {DerivationKinds.Product(o.Kind)} from source", true));
    }

    /// <summary>The inspector chain's links: open the diagram (or select the element).</summary>
    public void OpenChainLink(Element e)
    {
        if (e is Diagram d) Ws.Open(d);
        else Navigate(e);
    }

    private IReadOnlyList<MenuEntry> DerivationMenuEntries(Element? e) => DerivationUi.MenuEntries(Ws.Model, e, Ws.ReadOnly, DeriveAsync);

    /// <summary>The ribbon's "Create from…": derivations of the selection, else of the active diagram.</summary>
    private IReadOnlyList<DerivationOffer> RibbonDerivations() =>
        Ws.Selected is { } s && DiagramDerivation.Offers(Ws.Model, s) is { Count: > 0 } fromSelection ? fromSelection
            : Ws.Active is { } c ? DiagramDerivation.Offers(Ws.Model, c.Diagram) : [];

    private IEnumerable<CommandPalette.Command> DerivationPaletteCommands()
    {
        if (Ws.ReadOnly) yield break;
        foreach (var o in RibbonDerivations())
        {
            var offer = o;
            yield return new($"{offer.Label} ({offer.Source.DisplayName})", "Command", Icons.Material.Outlined.AccountTree, () => DeriveAsync(offer));
        }
        if (Ws.Active?.Diagram is { } d && DiagramDerivation.IsStale(Ws.Model, d))
            yield return new($"Update “{d.Name}” from source", "Command", Icons.Material.Outlined.Sync, () => UpdateFromSource(d));
    }

    // Stale flags for the tab strip, recomputed only when the model changes.
    private int _staleVersion = -1;
    private readonly Dictionary<string, bool> _stale = new();

    private bool IsStale(Diagram d)
    {
        if (_staleVersion != Ws.ModelVersion) { _stale.Clear(); _staleVersion = Ws.ModelVersion; }
        if (!_stale.TryGetValue(d.Id, out var v)) _stale[d.Id] = v = Ws.Model.Contains(d) && DiagramDerivation.IsStale(Ws.Model, d);
        return v;
    }
}
