namespace Meridian.Core.Validation;

/// <summary>
/// Process-wide registry of additional validation rules (contributed by plugins). Every new <see cref="ModelValidator"/>
/// includes the rules registered at the time it is constructed.
/// </summary>
public static class ExternalValidationRules
{
    private static readonly object Gate = new();
    private static readonly List<IValidationRule> Registered = new();

    public static void Register(IValidationRule rule)
    {
        lock (Gate)
        {
            Registered.RemoveAll(r => r.Id == rule.Id);
            Registered.Add(rule);
        }
    }

    public static bool Unregister(string id)
    {
        lock (Gate) return Registered.RemoveAll(r => r.Id == id) > 0;
    }

    public static IReadOnlyList<IValidationRule> Snapshot()
    {
        lock (Gate) return Registered.ToList();
    }
}

public sealed partial class ModelValidator
{
    public ModelValidator()
    {
        Rules.AddRange(ExternalValidationRules.Snapshot().Select(r => (IValidationRule)new IsolatedRule(r)));
    }

    /// <summary>Wraps an external rule so a buggy plugin reports one issue instead of breaking validation.</summary>
    private sealed class IsolatedRule(IValidationRule inner) : IValidationRule
    {
        public string Id => inner.Id;
        public string Description => inner.Description;

        public IEnumerable<ValidationIssue> Check(Model.ModelRepository model)
        {
            List<ValidationIssue> issues;
            try { issues = inner.Check(model).ToList(); }
            catch (Exception e) { issues = [new ValidationIssue(Severity.Warning, inner.Id, $"Validation rule {inner.Id} failed: {e.Message}", model.Root)]; }
            return issues;
        }
    }
}
