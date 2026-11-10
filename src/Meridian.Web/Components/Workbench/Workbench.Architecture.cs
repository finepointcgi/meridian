using Meridian.Web.CodeSync.Conformance;
using Meridian.Web.Services;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>
/// Architecture checks (docs/ARCHITECTURE-CHECKS.md): the "Architecture" view tab, the "Check architecture" ribbon button
/// and ⌘K command, and the deep link a "meridian/architecture" commit status points at.
/// </summary>
public partial class Workbench
{
    private ArchitectureState? _architecture;

    /// <summary>The per-tab architecture state (optional, so hosts and tests without it still render).</summary>
    private ArchitectureState? Architecture => _architecture ??= Services.GetService<ArchitectureState>();

    /// <summary>Show the ribbon button: the project has a connected code repository.</summary>
    private bool HasArchitecture => Architecture is { Repos.Count: > 0 };

    /// <summary>Opens (or shows) the "Architecture" tab.</summary>
    public void OpenArchitecture() => Ws.OpenView(ViewTabKind.Architecture);

    /// <summary>"Check architecture": checks the last synced code against the open model and shows the result.</summary>
    public async Task CheckArchitecture()
    {
        OpenArchitecture();
        if (Architecture is not { } a) return;
        await a.CheckAsync();
        var errors = a.ErrorCount;
        Ws.SetStatus(errors == 0 ? "Architecture check: no violations" : $"Architecture check: {errors} violation{(errors == 1 ? "" : "s")}");
    }

    private IEnumerable<CommandPalette.Command> ArchitecturePaletteCommands() =>
    [
        new("Check architecture (code against the model)", "Command", Icons.Material.Outlined.Rule, CheckArchitecture),
        new("Architecture violations", "Command", Icons.Material.Outlined.Rule, () => { OpenArchitecture(); return Task.CompletedTask; }),
    ];
}
