using System.Globalization;
using System.Text;
using Meridian.Core.Derivation;
using Meridian.Core.Model;

namespace Meridian.Core.CodeSync;

/// <summary>An element a behaviour plan wants: a stable key, its kind, name and fields, its owner (another planned item
/// or the behaviour itself) and where it goes on the diagram.</summary>
internal sealed class PlannedNode
{
    public required string Key { get; init; }
    public required ElementKind Kind { get; init; }
    public string Name { get; set; } = "";
    public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);
    /// <summary>Key of the planned owner (operands are owned by their fragment); null = the behaviour.</summary>
    public string? OwnerKey { get; init; }
    /// <summary>Where it is drawn; null = not drawn (operands).</summary>
    public Bounds? Bounds { get; set; }
    /// <summary>Fragments: (operand key, offset of its top from the fragment's top) for every operand after the first.</summary>
    public List<(string Operand, double Offset)> Separators { get; } = [];
}

/// <summary>A relationship a behaviour plan wants (control flow, message).</summary>
internal sealed class PlannedEdge
{
    public required string Key { get; init; }
    public required ElementKind Kind { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public string Name { get; set; } = "";
    public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);
    /// <summary>Messages: the height on the sequence diagram.</summary>
    public double? MessageY { get; set; }
}

/// <summary>An activity or interaction planned from a method body, with its diagram.</summary>
internal sealed class BehaviourPlan
{
    public required string Symbol { get; init; }
    public required ElementKind Kind { get; init; }
    public required DiagramType DiagramType { get; init; }
    public required string Name { get; init; }
    public string Documentation { get; set; } = "";
    public List<PlannedNode> Nodes { get; } = [];
    public List<PlannedEdge> Edges { get; } = [];
    public List<string> Warnings { get; } = [];

    /// <summary>Hash of everything the plan says, except where things are drawn and source lines: equal hashes mean the
    /// behaviour did not change.</summary>
    public string Hash()
    {
        var sb = new StringBuilder();
        sb.Append(Kind).Append('|').Append(Name).Append('|').Append(Documentation).Append('\n');
        foreach (var n in Nodes)
        {
            sb.Append("N ").Append(n.Key).Append(' ').Append(n.Kind).Append(' ').Append(n.Name).Append(' ').Append(n.OwnerKey);
            foreach (var (k, v) in n.Fields.OrderBy(f => f.Key, StringComparer.Ordinal)) sb.Append(' ').Append(k).Append('=').Append(v);
            sb.Append('\n');
        }
        foreach (var e in Edges)
        {
            sb.Append("E ").Append(e.Key).Append(' ').Append(e.Kind).Append(' ').Append(e.From).Append('>').Append(e.To).Append(' ').Append(e.Name);
            foreach (var (k, v) in e.Fields.OrderBy(f => f.Key, StringComparer.Ordinal)) sb.Append(' ').Append(k).Append('=').Append(v);
            sb.Append('\n');
        }
        return DeriveFields.Hash(sb.ToString());
    }
}

/// <summary>Stable keys for statements: the path of their parents plus a slug of what they are, numbered among equal
/// siblings, so inserting an unrelated statement keeps every other key (and the layout drawn for it).</summary>
internal sealed class KeyScope(string path)
{
    private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);
    public string Path { get; } = path;

    public string Next(string slug)
    {
        slug = Slug(slug);
        var n = _seen[slug] = _seen.GetValueOrDefault(slug) + 1;
        return $"{Path}/{slug}{(n > 1 ? "#" + n.ToString(CultureInfo.InvariantCulture) : "")}";
    }

    private static string Slug(string s)
    {
        var sb = new StringBuilder(Math.Min(s.Length, 48));
        foreach (var c in s)
        {
            if (sb.Length >= 48) break;
            if (char.IsLetterOrDigit(c) || c is '.' or '_' or ':') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().TrimEnd('-');
    }

    /// <summary>The key as stored: short keys as they are, long ones hashed (still unique, still stable).</summary>
    public static string Stored(string key) => key.Length <= 120 ? key : "h:" + DeriveFields.Hash(key);

    public static string For(CodeStatement s) => s.Kind switch
    {
        CodeStatementKind.If => "if:" + s.Text,
        CodeStatementKind.Switch => "switch:" + s.Text,
        CodeStatementKind.Loop => "loop:" + s.Text,
        CodeStatementKind.Try => "try",
        CodeStatementKind.Using => "using:" + s.Text,
        CodeStatementKind.Lock => "lock:" + s.Text,
        CodeStatementKind.Return => "return:" + (s.HasCall ? s.CallLabel : s.Text),
        CodeStatementKind.Throw => "throw:" + (s.Method ?? s.Text),
        _ when s.HasCall => "call:" + s.CallLabel,
        _ => s.Kind.ToString().ToLowerInvariant() + ":" + s.Text,
    };
}
