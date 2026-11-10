using Meridian.Web.Services;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>A request for the assistant panel (focus the question box, or start a quick action) made before it renders.</summary>
public sealed class AssistantLauncher
{
    public sealed record Request(bool Focus, QuickAction? Action);

    private Request? _pending;

    public void Ask() => _pending = new Request(true, null);
    public void Run(QuickAction action) => _pending = new Request(false, action);

    /// <summary>Returns and clears the pending request.</summary>
    public Request? Take()
    {
        var r = _pending;
        _pending = null;
        return r;
    }
}

/// <summary>Workbench glue for the AI assistant (dock 13): ribbon button and ⌘K entries.</summary>
public partial class Workbench
{
    /// <summary>Bottom-dock index of the assistant panel.</summary>
    public const int AssistantDock = 13;

    private readonly AssistantLauncher _assistant = new();

    /// <summary>Opens the assistant with the question box focused.</summary>
    public void OpenAssistant()
    {
        _assistant.Ask();
        OpenDock(AssistantDock);
        StateHasChanged();
    }

    private void ToggleAssistant()
    {
        if (_dockOpen && _dock == AssistantDock) _dockOpen = false;
        else OpenAssistant();
    }

    private void RunAssistant(QuickAction action)
    {
        _assistant.Run(action);
        OpenDock(AssistantDock);
        StateHasChanged();
    }

    private IEnumerable<CommandPalette.Command> AssistantPaletteCommands()
    {
        yield return new("Ask the assistant", "Command", Icons.Material.Outlined.AutoAwesome, () => { OpenAssistant(); return Task.CompletedTask; });
        yield return new("Assistant: Generate requirements from text…", "Command", Icons.Material.Outlined.PlaylistAdd, () => { RunAssistant(QuickAction.GenerateRequirements); return Task.CompletedTask; });
        yield return new("Assistant: Suggest trace links", "Command", Icons.Material.Outlined.Link, () => { RunAssistant(QuickAction.SuggestTraceLinks); return Task.CompletedTask; });
        yield return new("Assistant: Review this model", "Command", Icons.Material.Outlined.Rule, () => { RunAssistant(QuickAction.ReviewModel); return Task.CompletedTask; });
        yield return new("Assistant: Diagram my model", "Command", Icons.Material.Outlined.AccountTree, () => { RunAssistant(QuickAction.DiagramModel); return Task.CompletedTask; });
        yield return new("Assistant: Diagram from description…", "Command", Icons.Material.Outlined.Schema, () => { RunAssistant(QuickAction.DiagramFromDescription); return Task.CompletedTask; });
    }
}
