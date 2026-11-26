using Meridian.Core.Model;

namespace Meridian.Core.CodeSync;

/// <summary>One thing the AI pass is asked to name: <see cref="Kind"/> is "subsystem", "package", "block", "interface",
/// "decision" or "step"; <see cref="Code"/> is what the code says (identifier, signature, condition or call);
/// <see cref="Context"/> a few lines about it (namespace, documentation, members, the method it is in).</summary>
public sealed record CodeDescribeItem(string Key, string Kind, string Code, string CurrentName, string Context);

/// <summary>What the AI pass wrote for one item: a readable name (a question for a decision, a step for an action) and,
/// for structure, a one-line purpose ("" when it has none).</summary>
public sealed record CodeDescription(string Key, string Name, string Purpose);

public sealed record CodeDescribeResult(IReadOnlyList<CodeDescription> Items, string? Error = null)
{
    public static CodeDescribeResult Fail(string error) => new([], error);
}

/// <summary>
/// The optional AI pass of code sync (per repository, off by default): a language model names subsystems, packages,
/// key blocks, interfaces and the decisions and steps of behaviour diagrams in plain words, with a one-line purpose for
/// structure. Implemented outside Core (Meridian.Assistant) with the configured provider; tests use a fake.
/// </summary>
public interface ICodeDescriber
{
    /// <summary>"Claude", "Local model" …, for messages.</summary>
    string Name { get; }

    Task<CodeDescribeResult> DescribeAsync(IReadOnlyList<CodeDescribeItem> items, CancellationToken ct = default);
}

/// <summary>
/// Picks what the AI pass describes and writes the answers back. Each described element remembers a hash of what it was
/// described from (<see cref="HashKey"/>): it is sent again only when that changes, so a re-sync of unchanged code sends
/// nothing. Names go into <see cref="CodeModelMapper.LabelKey"/> (code sync keeps them on re-syncs; behaviour nodes keep
/// them while their code, <see cref="SourceKey"/>, is unchanged). A purpose is written as documentation only where the code
/// has none (a doc comment always wins) or where the AI pass wrote it before (<see cref="WrittenKey"/>).
/// The code symbol never changes, so matching on re-syncs is unaffected.
/// </summary>
public static class CodeAiDescriptions
{
    /// <summary>Hash of what an element was described from.</summary>
    public const string HashKey = "code.aiHash";

    /// <summary>On behaviour nodes: the code (their documentation) the AI wording was written for.</summary>
    public const string SourceKey = "code.aiSource";

    /// <summary>"true" when the element's documentation was written by the AI pass.</summary>
    public const string WrittenKey = "code.aiDoc";

    /// <summary>Most items one pass sends (the most important first: subsystems, packages, key blocks, then behaviour).</summary>
    public const int DefaultMaxItems = 300;

    /// <summary>The items under <paramref name="codeRoot"/> that need a description (new, or their code changed).</summary>
    public static IReadOnlyList<CodeDescribeItem> Collect(ModelRepository m, Element codeRoot, string repoId, int maxItems = DefaultMaxItems)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(codeRoot);
        var items = new List<(int Rank, CodeDescribeItem Item)>();
        foreach (var e in codeRoot.Descendants())
        {
            if (e is Relationship or Diagram || e.Get(CodeFieldKeys.Repo) != repoId || e.Get(CodeModelMapper.RemovedKey) == "true") continue;
            if (Describe(m, e) is not { } d) continue;
            if (e.Get(HashKey) == Hash(d.Item)) continue;
            items.Add(d);
        }
        return items.OrderBy(x => x.Rank).ThenBy(x => x.Item.Key, StringComparer.Ordinal).Take(maxItems).Select(x => x.Item).ToList();
    }

    private static string Hash(CodeDescribeItem i) => Derivation.DeriveFields.Hash($"{i.Kind}\n{i.Code}\n{i.Context}")[..16];

    private static (int Rank, CodeDescribeItem Item)? Describe(ModelRepository m, Element e)
    {
        var symbol = e.Get(CodeFieldKeys.Symbol);
        if (symbol.StartsWith(CodeModelMapper.ArchitecturePrefix + "subsystem:", StringComparison.Ordinal))
            return (0, new(e.Id, "subsystem", symbol[(CodeModelMapper.ArchitecturePrefix.Length + 10)..], e.Name, Lines(e.Get(CodeFieldKeys.File), Doc(e))));
        if (CodeModelMapper.IsNamespacePackage(e))
        {
            var types = e.OwnedElements.Where(c => c.Get(CodeModelMapper.KeyKey) == "true").Select(c => CodeName(c)).Take(12);
            return (1, new(e.Id, "package", symbol[3..], e.Name, Lines("types: " + string.Join(", ", types))));
        }
        if (e.Get(CodeModelMapper.KeyKey) == "true" && e.Kind is ElementKind.Block or ElementKind.InterfaceBlock)
        {
            var members = e.OwnedElements.Where(c => c.Kind is ElementKind.Property or ElementKind.Operation).Select(c => c.Kind == ElementKind.Operation ? c.Name + "()" : c.Name).Distinct().Take(15);
            var bases = m.Outgoing(e, ElementKind.Generalization).Concat(m.Outgoing(e, ElementKind.Realization)).Select(r => CodeName(r.Target)).Take(5);
            return (2, new(e.Id, e.Kind == ElementKind.InterfaceBlock ? "interface" : "block", symbol, e.Name,
                Lines(Doc(e), "members: " + string.Join(", ", members), bases.Any() ? "is a: " + string.Join(", ", bases) : "")));
        }
        if (e.Get(CodeBehaviourMapper.RoleKey) == "node" && e.Documentation is { Length: > 0 } code
            && (e.Kind == ElementKind.DecisionNode || e.Kind == ElementKind.Action && e.Get(CodeBehaviourMapper.NodeKey).Contains("fold:") == false))
        {
            var behaviour = e.Owner?.Name ?? "";
            return (3, new(e.Id, e.Kind == ElementKind.DecisionNode ? "decision" : "step", code, e.Name, Lines("in: " + behaviour)));
        }
        return null;
    }

    private static string CodeName(Element e)
    {
        var s = e.Get(CodeFieldKeys.Symbol);
        var dot = s.LastIndexOf('.');
        return dot >= 0 ? s[(dot + 1)..] : e.Name;
    }

    private static string Doc(Element e) => e.Documentation.Length > 0 && e.Get(WrittenKey) != "true" ? "doc: " + e.Documentation.Split('\n')[0] : "";

    private static string Lines(params string[] lines) => string.Join("\n", lines.Where(l => l.Length > 0));

    /// <summary>
    /// Writes the answers onto the model (one undo step): names (cleaned, at most 60 characters), purposes where allowed,
    /// and the hashes so the same items are not asked again. Answers for unknown keys or with empty names are skipped.
    /// Returns how many elements changed.
    /// </summary>
    public static int Apply(ModelRepository m, IReadOnlyList<CodeDescribeItem> asked, IReadOnlyList<CodeDescription> answers)
    {
        ArgumentNullException.ThrowIfNull(m);
        var byKey = asked.ToDictionary(i => i.Key, StringComparer.Ordinal);
        var changed = 0;
        using (m.History.Transaction("Code sync: AI descriptions"))
            foreach (var a in answers)
            {
                if (!byKey.TryGetValue(a.Key, out var item) || m.Find(a.Key) is not { } e) continue;
                var name = Clean(a.Name, 60);
                if (name.Length == 0) continue;
                m.Set(e, CodeModelMapper.LabelKey, name);
                if (e.Name != name) m.Rename(e, name);
                if (item.Kind is "decision" or "step") m.Set(e, SourceKey, e.Documentation);
                else if (Clean(a.Purpose, 400) is { Length: > 0 } purpose && (e.Documentation.Length == 0 || e.Get(WrittenKey) == "true"))
                {
                    m.Set(e, FieldKeys.Documentation, purpose);
                    m.Set(e, WrittenKey, "true");
                }
                m.Set(e, HashKey, Hash(item));
                changed++;
            }
        return changed;
    }

    private static string Clean(string? s, int max)
    {
        var t = string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim().Trim('"');
        return t.Length <= max ? t : t[..(max - 1)].TrimEnd() + "…";
    }
}
