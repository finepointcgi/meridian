using System.Text.RegularExpressions;
using Meridian.Core.Analysis;
using Meridian.Core.Model;

namespace Meridian.Core.Validation;

/// <summary>
/// Evaluates the model's own <see cref="ElementKind.ValidationRule"/> elements: every element matched by a rule's query is
/// reported with the rule's severity and message. Message placeholders such as <c>{name}</c>, <c>{owner}</c> or <c>{mass}</c>
/// use the <see cref="ModelValues"/> key vocabulary.
/// </summary>
public sealed partial class QueryRules : IValidationRule
{
    public const string InvalidRuleId = "USR-000";

    public string Id => "USR";
    public string Description => "User-defined query rules";

    public IEnumerable<ValidationIssue> Check(ModelRepository model)
    {
        foreach (var rule in model.OfKind(ElementKind.ValidationRule).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList())
        {
            var text = rule.Get(ViewFields.Query);
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!ModelQuery.TryParse(text, out var query, out var error))
            {
                yield return new ValidationIssue(Severity.Error, InvalidRuleId, $"Validation rule '{rule.DisplayName}' has an invalid query: {error}", rule);
                continue;
            }
            var severity = ParseSeverity(rule.Get(ViewFields.Severity));
            var id = RuleId(rule);
            foreach (var e in query.Run(model))
                yield return new ValidationIssue(severity, id, Format(model, rule, e), e);
        }
    }

    public static Severity ParseSeverity(string s) => s.Trim().ToLowerInvariant() switch
    {
        "error" => Severity.Error,
        "info" or "information" or "note" => Severity.Info,
        _ => Severity.Warning,
    };

    public static string RuleId(Element rule) => string.IsNullOrWhiteSpace(rule.Name) ? "USR" : rule.Name.Trim();

    public static string Format(ModelRepository m, Element rule, Element e)
    {
        var template = rule.Get(ViewFields.Message);
        if (string.IsNullOrWhiteSpace(template)) return $"{e.DisplayName} matches rule '{rule.DisplayName}'";
        return Placeholder().Replace(template, x => ModelValues.Get(m, e, x.Groups[1].Value) is { Length: > 0 } v ? v : "∅");
    }

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex Placeholder();
}
