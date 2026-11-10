using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Web.Settings;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>Workbench glue for Settings: the title-bar gear, avatar menu, ⌘, and ⌘K entries, and diagram defaults.</summary>
public partial class Workbench
{
    [Inject] private ThemeService Theme { get; set; } = default!;

    /// <summary>Opens Settings (optionally on one section).</summary>
    public async Task OpenSettings(SettingsSection? section = null)
    {
        var parameters = new DialogParameters<SettingsDialog> { { x => x.Section, section }, { x => x.Desktop, _desktop } };
        var dialog = await Dialogs.ShowAsync<SettingsDialog>("Settings", parameters, SettingsDialog.Options);
        if ((await dialog.Result)?.Data is string action)
            switch (action)
            {
                case SettingsDialog.TourAction: await StartTour(); break;
                case SettingsDialog.ShortcutsAction: await ShowHelp(); break;
            }
    }

    private IEnumerable<CommandPalette.Command> SettingsPaletteCommands()
    {
        CommandPalette.Command S(string title, string icon, SettingsSection? section) => new(title, "Settings", icon, () => OpenSettings(section));
        yield return S("Settings", Icons.Material.Outlined.Settings, null);
        yield return S("Settings: Appearance (theme, accent colour, density)", Icons.Material.Outlined.Palette, SettingsSection.Appearance);
        yield return S("Settings: Diagram defaults", Icons.Material.Outlined.Schema, SettingsSection.Diagrams);
        yield return S("Settings: AI assistant", Icons.Material.Outlined.AutoAwesome, SettingsSection.Assistant);
        if (!Host.LocalMode) yield return S("Settings: Account", Icons.Material.Outlined.AccountCircle, SettingsSection.Account);
    }

    /// <summary>Connector routing and line jumps for a new diagram, from Settings ▸ Diagrams.</summary>
    private void ApplyDiagramDefaults(Diagram d)
    {
        var p = Ws.Preferences;
        if (p.ConnectorRoutingValue is { } routing && d.DiagramType is not (DiagramType.Sequence or DiagramType.Timing))
            Ws.Model.Set(d, EdgeRouter.DiagramKey, routing);
        if (p.LineJumps) Ws.Model.Set(d, EdgeRouter.LineJumpsKey, "true");
    }

    private async Task<bool> ConfirmAsync(string title, string message) =>
        await Dialogs.ShowMessageBox(title, message, yesText: "Delete", cancelText: "Cancel") == true;
}
