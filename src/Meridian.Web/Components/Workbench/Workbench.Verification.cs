using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Web.Services;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>Simulation-driven verification and the variant view: dock tab, ribbon button and ⌘K commands.</summary>
public partial class Workbench
{
    /// <summary>Bottom-dock index of the verification results.</summary>
    public const int VerificationDock = 20;

    /// <summary>Bottom-dock index of variants &amp; trade studies.</summary>
    private const int VariantsDock = 11;

    /// <summary>Opens the results tab and verifies (the selected run, or everything).</summary>
    public async Task VerifyRequirements()
    {
        OpenDock(VerificationDock);
        StateHasChanged();
        var run = Ws.Selected is { } s && Verification.IsRun(s) ? s : null;
        await VerificationPanel.RunAndReport(Ws, Services, run);
    }

    private IEnumerable<CommandPalette.Command> VerificationPaletteCommands()
    {
        CommandPalette.Command C(string title, string icon, Action a) => new(title, "Command", icon, () => { a(); return Task.CompletedTask; });
        CommandPalette.Command CA(string title, string icon, Func<Task> a) => new(title, "Command", icon, a);
        var list = new List<CommandPalette.Command>
        {
            CA("Verify requirements (run criteria)", Icons.Material.Outlined.Rule, VerifyRequirements),
            C("Verification results", Icons.Material.Outlined.Rule, () => OpenDock(VerificationDock)),
            C(Ws.Verification.ShowOnDiagrams ? "Hide verification results on diagrams" : "Show verification results on diagrams",
              Icons.Material.Outlined.Palette, () => Ws.Verification.SetShowOnDiagrams(!Ws.Verification.ShowOnDiagrams)),
            C("Feature model editor", Icons.Material.Outlined.AccountTree, () => OpenDock(VariantsDock)),
        };
        foreach (var c in Ws.Model.OfKind(ElementKind.Configuration).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var config = c;
            list.Add(C($"Show variant on diagrams: {config.DisplayName} (grey out)", Icons.Material.Outlined.Balance,
                () => Ws.VariantView.Show(config.Id, VariantDisplay.Grey)));
            list.Add(C($"Show variant on diagrams: {config.DisplayName} (hide)", Icons.Material.Outlined.VisibilityOff,
                () => Ws.VariantView.Show(config.Id, VariantDisplay.Hide)));
        }
        if (Ws.VariantView.Active) list.Add(C("Show variant on diagrams: off (whole model)", Icons.Material.Outlined.Visibility, () => Ws.VariantView.SetMode(VariantDisplay.Off)));
        return list;
    }
}
