using Meridian.Core.CodeSync;
using Meridian.Core.Model;
using Meridian.Web.CodeSync;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>
/// Behaviour from code on demand: "Generate activity from code" and "Generate sequence from code" for an operation that
/// came from code, in its context menus, the inspector and ⌘K (docs/CODE-SYNC.md, "Behaviour from code").
/// </summary>
public partial class Workbench
{
    /// <summary>Generates the operation's activity or sequence from the last synced code and opens its diagram.</summary>
    public async Task GenerateBehaviourAsync(Element operation, bool sequence)
    {
        if (Ws.ReadOnly) { Ws.SetStatus("View-only: you can't generate diagrams in this project"); return; }
        if (Ws.Session is not { } s || !Ws.Model.Contains(operation)) return;
        int? depth = null;
        if (sequence)
        {
            // How deep to follow calls: the depth the sequence was made with, else the default.
            var current = CodeBehaviourMapper.StoredDepth(CodeBehaviourMapper.Find(Ws.Model, operation, sequence: true)) ?? new CodeBehaviourLimits().SequenceDepth;
            depth = await SequenceDepthDialog.AskAsync(Dialogs, operation.DisplayName, current);
            if (depth is null) return;
        }
        (Diagram? Diagram, string Message) result;
        try { result = await CodeBehaviourUi.GenerateAsync(Db, s.ProjectId, Ws.Model, operation, sequence, depth); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            result = (null, "Generating from code failed: " + ex.Message);
        }
        if (result.Diagram is { } d) Ws.Open(d);
        else Snackbar.Add(result.Message, Severity.Warning);
        Ws.SetStatus(result.Message);
    }

    private IReadOnlyList<MenuEntry> CodeBehaviourMenuEntries(Element? e) => CodeBehaviourUi.MenuEntries(e, Ws.ReadOnly, GenerateBehaviourAsync);

    private IEnumerable<CommandPalette.Command> CodeBehaviourPaletteCommands()
    {
        if (Ws.ReadOnly || !CodeBehaviourUi.IsCodeOperation(Ws.Selected)) yield break;
        var op = Ws.Selected!;
        yield return new($"{CodeBehaviourUi.ActivityLabel} ({op.DisplayName})", "Command", Icons.Material.Outlined.AccountTree, () => GenerateBehaviourAsync(op, false));
        yield return new($"{CodeBehaviourUi.SequenceLabel} ({op.DisplayName})", "Command", Icons.Material.Outlined.ViewTimeline, () => GenerateBehaviourAsync(op, true));
    }
}
