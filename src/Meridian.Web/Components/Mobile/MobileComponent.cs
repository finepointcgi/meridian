using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.AspNetCore.Components;

namespace Meridian.Web.Components.Mobile;

/// <summary>Base for the phone / tablet shell pieces: re-renders on workspace changes and on <see cref="MobileUiState"/> changes.</summary>
public abstract class MobileComponent : WorkspaceComponent
{
    [Inject] protected MobileUiState Ui { get; set; } = default!;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Ui.Changed += OnUiChanged;
    }

    private void OnUiChanged() => _ = InvokeAsync(StateHasChanged);

    public override void Dispose()
    {
        Ui.Changed -= OnUiChanged;
        base.Dispose();
    }
}
