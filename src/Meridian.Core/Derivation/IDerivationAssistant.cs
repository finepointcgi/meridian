using Meridian.Core.Model;

namespace Meridian.Core.Derivation;

/// <summary>What the AI fill-in is asked: the derivation, its source, and the context the rules already worked out.</summary>
/// <param name="Description">Use case → activity: the use case's free-text description (may be empty).</param>
/// <param name="Actors">Use case → activity: the actors that may start a step.</param>
/// <param name="Messages">Activity → sequence: the messages the rules found ("msg key", "from → to", "target action").</param>
public sealed record DerivationAiRequest(
    DerivationKind Kind, string SourceName, string Description, IReadOnlyList<string> Actors,
    IReadOnlyList<(string Key, string From, string To, string Action)> Messages);

/// <summary>The AI fill-in's answer, validated by <see cref="DerivationAi.Validate"/> before it is used.</summary>
public sealed record DerivationAiResult(bool Ok, string? Error, IReadOnlyList<string> Steps, IReadOnlyDictionary<string, string> MessageNames)
{
    public static DerivationAiResult Fail(string error) => new(false, error, [], new Dictionary<string, string>());
}

/// <summary>
/// Optional AI help for derivations: writes what the rules can't (a use case's steps and alternate flows from free text,
/// meaningful message names). Implemented on top of the assistant's providers (Claude, local models, Claude Code); never
/// required. Its output only ever feeds a plan that is previewed before anything changes.
/// </summary>
public interface IDerivationAssistant
{
    /// <summary>Who answers ("Claude", "The local model"), for the preview.</summary>
    string Name { get; }

    Task<DerivationAiResult> FillInAsync(DerivationAiRequest request, CancellationToken ct = default);
}

/// <summary>Builds AI requests from a source and turns validated answers into <see cref="DerivationOptions"/>.</summary>
public static class DerivationAi
{
    public const int MaxStepLength = 160;

    public static DerivationAiRequest? Request(ModelRepository m, DerivationKind kind, Element source)
    {
        switch (kind)
        {
            case DerivationKind.ActivityFromUseCase:
                return new DerivationAiRequest(kind, source.Name, source.Documentation, UseCaseToActivity.Actors(m, source).Select(a => a.Name).ToList(), []);
            case DerivationKind.SequenceFromActivity:
                var g = new ActivityGraph(m, source);
                var msgs = ActivityToSequence.Messages(m, g).Select(p => (p.Key, p.FromLane.Name, p.ToLane.Name, p.Action.Name)).ToList();
                return msgs.Count == 0 ? null : new DerivationAiRequest(kind, source.Name, source.Documentation, [], msgs);
            default:
                return null;
        }
    }

    /// <summary>
    /// Checks an answer against the request: steps are trimmed, de-braced and capped; message names must be identifiers for
    /// keys that were asked about. Returns null (with <paramref name="error"/>) when nothing usable is left.
    /// </summary>
    public static DerivationOptions? Validate(DerivationAiRequest request, DerivationAiResult result, string who, out string? error)
    {
        error = null;
        if (!result.Ok) { error = result.Error ?? $"{who} did not answer."; return null; }
        if (request.Kind == DerivationKind.ActivityFromUseCase)
        {
            var steps = result.Steps.Select(s => s.Replace('{', '(').Replace('}', ')').Replace('\n', ' ').Trim())
                                    .Select(s => System.Text.RegularExpressions.Regex.Replace(s, @"^\s*(?:\d+[.)]|[-*•])\s*", ""))
                                    .Where(s => s.Length > 0).Select(s => s.Length > MaxStepLength ? s[..MaxStepLength] : s)
                                    .Take(UseCaseSteps.MaxSteps).ToList();
            if (steps.Count == 0) { error = $"{who} did not write any steps."; return null; }
            return new DerivationOptions { Steps = steps, AiNote = $"Steps written by {who}" };
        }
        if (request.Kind == DerivationKind.SequenceFromActivity)
        {
            var asked = request.Messages.Select(x => x.Key).ToHashSet();
            var names = result.MessageNames.Where(kv => asked.Contains(kv.Key))
                              .Select(kv => (kv.Key, Name: DeriveText.Identifier(kv.Value)))
                              .Where(kv => kv.Name.Length is > 0 and <= 60).ToDictionary(kv => kv.Key, kv => kv.Name);
            if (names.Count == 0) { error = $"{who} did not name any of the messages."; return null; }
            return new DerivationOptions { MessageNames = names, AiNote = $"Message names written by {who}" };
        }
        error = "AI fill-in is not available for this derivation.";
        return null;
    }
}
