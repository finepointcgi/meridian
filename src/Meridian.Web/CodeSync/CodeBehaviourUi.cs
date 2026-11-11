using Meridian.Core.CodeSync;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Web.CodeSync.Conformance;
using Meridian.Web.CodeSync.Hosts;
using Meridian.Web.Components.Workbench;

namespace Meridian.Web.CodeSync;

/// <summary>
/// "Generate activity / sequence from code" on an operation that came from code (docs/CODE-SYNC.md, "Behaviour from
/// code"): the context-menu, inspector and ⌘K entries, and the generation itself from the code the last sync of the
/// operation's repository stored (the architecture check keeps it per connection).
/// </summary>
public static class CodeBehaviourUi
{
    public const string ActivityLabel = "Generate activity from code";
    public const string SequenceLabel = "Generate sequence from code";

    /// <summary>An operation code sync made (it has a code symbol and a repository).</summary>
    public static bool IsCodeOperation(Element? e) =>
        e is { Kind: ElementKind.Operation } && e.Get(CodeFieldKeys.Symbol).Length > 0 && Guid.TryParse(e.Get(CodeFieldKeys.Repo), out _);

    /// <summary>The two entries for a code operation (none when it is not one or the project is read-only).</summary>
    public static IReadOnlyList<MenuEntry> MenuEntries(Element? e, bool readOnly, Func<Element, bool, Task> run)
    {
        if (readOnly || !IsCodeOperation(e)) return [];
        var op = e!;
        return
        [
            MenuEntry.Title("Behaviour from code"),
            MenuEntry.Async(ActivityLabel, () => run(op, false)),
            MenuEntry.Async(SequenceLabel, () => run(op, true)),
        ];
    }

    /// <summary>
    /// Generates (or brings up to date) the operation's activity or sequence in <paramref name="model"/> from the stored
    /// code of its repository, as one undo step. Returns the diagram, or null and why not.
    /// </summary>
    public static async Task<(Diagram? Diagram, string Message)> GenerateAsync(IMeridianStore db, Guid projectId, ModelRepository model, Element operation, bool sequence)
    {
        if (!IsCodeOperation(operation)) return (null, $"{operation.DisplayName} did not come from code.");
        var connectionId = Guid.Parse(operation.Get(CodeFieldKeys.Repo));
        var connection = await db.FindIntegrationAsync(connectionId);
        if (connection is null || connection.ProjectId != projectId || connection.Kind != CodeRepoKinds.CodeRepo)
            return (null, "The repository this operation came from is no longer connected.");
        var config = CodeRepoConfig.Parse(connection.ConfigJson);
        if (config.EffectiveBehaviour == CodeBehaviourMode.Off)
            return (null, $"Behaviour diagrams are off for {config.Display}. Turn them on in the repository's settings.");
        var rec = await new ArchitectureStore(db).FindAsync(projectId, connectionId);
        if (rec?.Code() is not { } code) return (null, $"No code of {config.Display} is stored yet; sync the repository first.");
        var options = new CodeSyncOptions { RepoId = connectionId.ToString(), Commit = rec.Commit, Behaviour = config.EffectiveBehaviour };
        return CodeBehaviourMapper.Generate(model, code, operation, sequence, options);
    }
}
