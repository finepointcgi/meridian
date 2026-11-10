using Meridian.Core.Model;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>Per-package permissions (docs/PERMISSIONS.md): the package context menu's "Permissions…".</summary>
public partial class Workbench
{
    private IEnumerable<MenuEntry> PermissionEntries(Element e)
    {
        if (Host.LocalMode || Ws.Session is null || e.Kind is not (ElementKind.Package or ElementKind.Model)) yield break;
        yield return MenuEntry.Async("Permissions…", () => OpenPermissions(e));
    }

    public async Task OpenPermissions(Element package)
    {
        if (Ws.Session is null) return;
        await Dialogs.ShowAsync<PackagePermissionsDialog>("Permissions",
            new DialogParameters<PackagePermissionsDialog> { { x => x.Package, package } },
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true, CloseOnEscapeKey = true });
    }
}
