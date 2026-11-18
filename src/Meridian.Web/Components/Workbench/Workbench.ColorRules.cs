using Meridian.Core.Model;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>Colour rules on diagrams (⌘K). The editor is the inspector's "Colour rules" section (ColourRulesSection.razor).</summary>
public partial class Workbench
{
    private IEnumerable<CommandPalette.Command> ColorRulePaletteCommands()
    {
        if (Ws.Active?.Diagram is not { } d) yield break;
        yield return new("Colour rules for this diagram", "Command", Icons.Material.Outlined.FormatColorFill, () => { ShowColorRules(d, add: false); return Task.CompletedTask; });
        if (Ws.CanEdit(d))
            yield return new("Add colour rule to this diagram", "Command", Icons.Material.Outlined.FormatColorFill, () => { ShowColorRules(d, add: true); return Task.CompletedTask; });
    }

    /// <summary>Selects the diagram so the inspector shows its colour rules, optionally adding a new rule first.</summary>
    private void ShowColorRules(Diagram d, bool add)
    {
        Ws.Select(d, syncCanvas: false);
        if (add)
        {
            try { Ws.Model.AddColorRule(d); }
            catch (InvalidOperationException ex) { Ws.SetStatus(ex.Message); return; }
        }
        Ws.SetStatus(add ? "Rule added: set its condition and colours under Colour rules in Properties" : "Colour rules are in Properties");
    }
}
