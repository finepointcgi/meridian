using Meridian.Web.Services;
using Microsoft.AspNetCore.Components;

namespace Meridian.Web.Components.Workbench;

/// <summary>Base for panels that re-render whenever the workspace (model, selection, diagrams) changes.</summary>
public abstract class WorkspaceComponent : ComponentBase, IDisposable
{
    [Inject] protected Workspace Ws { get; set; } = default!;
    [CascadingParameter] public Workbench Shell { get; set; } = default!;

    protected override void OnInitialized() => Ws.Changed += Handle;

    private void Handle() => _ = InvokeAsync(() => { OnWorkspaceChanged(); StateHasChanged(); });

    protected virtual void OnWorkspaceChanged() { }

    public virtual void Dispose() => Ws.Changed -= Handle;
}
