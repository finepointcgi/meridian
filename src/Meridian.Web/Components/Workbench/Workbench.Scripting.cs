using Meridian.Scripting;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>Workbench glue for the script console (dock 10) and plugins.</summary>
public partial class Workbench
{
    [Inject] private PluginManager Plugins { get; set; } = default!;
    private PluginsMenu? _plugins;

    /// <summary>Palette (⌘K) entries for the script console and every plugin command, exporter and importer.</summary>
    private IEnumerable<CommandPalette.Command> ScriptingPaletteCommands()
    {
        yield return new("Script console", "Command", Icons.Material.Outlined.DataObject, () => { OpenDock(10); StateHasChanged(); return Task.CompletedTask; });
        yield return new("Loaded plugins…", "Command", Icons.Material.Outlined.Extension, () => _plugins?.ShowPlugins() ?? Task.CompletedTask);
        foreach (var c in Plugins.Commands)
        {
            var cmd = c;
            yield return new(cmd.Title, "Command", Icons.Material.Outlined.Extension, () => { _plugins?.RunCommand(cmd); return Task.CompletedTask; });
        }
        foreach (var x in Plugins.Exporters)
        {
            var exporter = x;
            yield return new($"Export: {exporter.Name}", "Command", Icons.Material.Outlined.Download, () => _plugins?.Export(exporter) ?? Task.CompletedTask);
        }
        foreach (var x in Plugins.Importers)
        {
            var importer = x;
            yield return new($"Import: {importer.Name}…", "Command", Icons.Material.Outlined.UploadFile, () => _plugins?.PickImport(importer) ?? Task.CompletedTask);
        }
    }
}
