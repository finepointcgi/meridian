using System.Text.Json;
using System.Text.Json.Nodes;
using Meridian.Core.Analysis;
using Meridian.Core.Language;

namespace Meridian.Core.Model;

/// <summary>
/// One colour rule of a diagram: shapes whose element matches <see cref="Query"/> (the model query text form, e.g.
/// <c>kind:Requirement where verificationResult contains fail</c>) are drawn with the rule's colours and badge.
/// Empty colours leave that part of the shape as it would otherwise be.
/// </summary>
public sealed record ColorRule(string Label = "", string Query = "", string? Fill = null, string? Line = null, string? Text = null, string? Badge = null)
{
    /// <summary>What the legend shows: the label, or the condition when there is no label.</summary>
    public string LegendText => !string.IsNullOrWhiteSpace(Label) ? Label.Trim() : string.IsNullOrWhiteSpace(Query) ? "Every element" : Query.Trim();

    /// <summary>The swatch colour for the legend and the editor (fill, else line, else text colour).</summary>
    public string? SwatchColor => Fill ?? Line ?? Text;
}

/// <summary>
/// Data-driven colour rules on diagrams (Lucidchart's conditional formatting, Cameo's legend colour rules).
/// <para>
/// A diagram keeps an ordered list of rules in its <c>colorRules</c> field (a JSON array), so the rules are replicated,
/// undoable, saved in .meridian files and carried by XMI with no new file format; every change is one field write and
/// therefore one undo step. The condition of a rule is a <see cref="ModelQuery"/>, so it understands everything the
/// query panel does: kinds, stereotypes, fields, tagged values (<c>tag:ASIL</c>), value properties, relationships and
/// verification results.
/// </para>
/// <para>
/// The first rule that matches a shape wins. A rule's colours override the shape's own (manual) colours, but only the
/// parts the rule sets: a rule with just a fill keeps a manual line colour.
/// </para>
/// </summary>
public static class ColorRules
{
    public const string Field = "colorRules";

    /// <summary>Longest badge text kept (a short marker such as "!" or "ASIL D").</summary>
    public const int MaxBadgeLength = 12;

    /// <summary>Rules of a diagram, in order (first match wins). Malformed entries are skipped.</summary>
    public static IReadOnlyList<ColorRule> Of(Diagram d) => Parse(d.Get(Field));

    public static bool HasRules(Diagram d) => d.Get(Field).Length > 2;

    public static IReadOnlyList<ColorRule> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            if (JsonNode.Parse(json) is not JsonArray arr) return [];
            var list = new List<ColorRule>();
            foreach (var n in arr)
            {
                if (n is not JsonObject o) continue;
                list.Add(Clean(new ColorRule(Str(o, "label") ?? "", Str(o, "query") ?? "", Str(o, "fill"), Str(o, "line"), Str(o, "text"), Str(o, "badge"))));
            }
            return list;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return []; }
    }

    private static string? Str(JsonObject o, string key)
    {
        try { return o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null; }
        catch (InvalidOperationException) { return null; }
    }

    public static string Serialize(IEnumerable<ColorRule> rules)
    {
        var arr = new JsonArray();
        foreach (var r in rules.Select(Clean))
        {
            var o = new JsonObject { ["label"] = r.Label, ["query"] = r.Query };
            if (r.Fill != null) o["fill"] = r.Fill;
            if (r.Line != null) o["line"] = r.Line;
            if (r.Text != null) o["text"] = r.Text;
            if (r.Badge != null) o["badge"] = r.Badge;
            arr.Add(o);
        }
        return arr.ToJsonString();
    }

    /// <summary>Trims text, drops empty values and colours that aren't plain CSS colours, and shortens the badge.</summary>
    public static ColorRule Clean(ColorRule r)
    {
        static string? Col(string? c) => string.IsNullOrWhiteSpace(c) ? null : IsSafeColor(c.Trim()) ? c.Trim() : null;
        var badge = string.IsNullOrWhiteSpace(r.Badge) ? null : r.Badge.Trim();
        if (badge is { Length: > MaxBadgeLength }) badge = badge[..MaxBadgeLength];
        return new ColorRule((r.Label ?? "").Trim(), (r.Query ?? "").Trim(), Col(r.Fill), Col(r.Line), Col(r.Text), badge);
    }

    /// <summary>Hex, a colour name, or rgb()/hsl(): nothing that could carry markup into an SVG attribute.</summary>
    public static bool IsSafeColor(string c) =>
        c.Length <= 64 && System.Text.RegularExpressions.Regex.IsMatch(c, @"^(?:#[0-9a-fA-F]{3,8}|[a-zA-Z]{1,32}|(?:rgb|rgba|hsl|hsla)\([0-9.,%\s/+-]{1,48}\))$");

    /// <summary>Error message of a rule's condition, or null when it parses.</summary>
    public static string? QueryError(string? query) => ModelQuery.TryParse(query, out _, out var error) ? null : error;

    // ================================================================ editing (one undo step each)

    /// <summary>Replaces every rule of a diagram. One undo step.</summary>
    public static void SetColorRules(this ModelRepository m, Diagram d, IEnumerable<ColorRule> rules)
    {
        var list = rules.ToList();
        m.Set(d, Field, list.Count == 0 ? null : Serialize(list));
    }

    /// <summary>Adds a rule at the end (lowest priority) and returns its index.</summary>
    public static int AddColorRule(this ModelRepository m, Diagram d, ColorRule? rule = null)
    {
        var list = Of(d).ToList();
        list.Add(rule ?? new ColorRule($"Rule {list.Count + 1}", "", Fill: "#fbdede"));
        m.SetColorRules(d, list);
        return list.Count - 1;
    }

    public static void UpdateColorRule(this ModelRepository m, Diagram d, int index, ColorRule rule)
    {
        var list = Of(d).ToList();
        if (index < 0 || index >= list.Count) return;
        list[index] = rule;
        m.SetColorRules(d, list);
    }

    public static void RemoveColorRule(this ModelRepository m, Diagram d, int index)
    {
        var list = Of(d).ToList();
        if (index < 0 || index >= list.Count) return;
        list.RemoveAt(index);
        m.SetColorRules(d, list);
    }

    /// <summary>Moves a rule up (<paramref name="delta"/> &lt; 0, higher priority) or down.</summary>
    public static void MoveColorRule(this ModelRepository m, Diagram d, int index, int delta)
    {
        var list = Of(d).ToList();
        var j = index + Math.Sign(delta);
        if (index < 0 || index >= list.Count || j < 0 || j >= list.Count) return;
        (list[index], list[j]) = (list[j], list[index]);
        m.SetColorRules(d, list);
    }

    // ================================================================ evaluation

    /// <summary>The rules of a diagram with their conditions parsed once (invalid conditions never match).</summary>
    public sealed class Compiled
    {
        private readonly ModelRepository _m;
        private readonly List<(ColorRule Rule, ModelQuery? Query)> _rules;

        public Compiled(ModelRepository m, IReadOnlyList<ColorRule> rules)
        {
            _m = m;
            _rules = rules.Select(r => (r, ModelQuery.TryParse(r.Query, out var q, out _) ? q : (ModelQuery?)null)).ToList();
            // Resolve "in:Pkg" once: by id it's a lookup instead of a search per shape on every render.
            foreach (var (_, q) in _rules)
                if (q != null && !string.IsNullOrWhiteSpace(q.Scope) && q.ResolveScope(m) is { } scope) q.Scope = scope.Id;
            Rules = rules;
        }

        public IReadOnlyList<ColorRule> Rules { get; }

        public bool IsEmpty => _rules.Count == 0;

        /// <summary>The first rule matching the element, or null.</summary>
        public ColorRule? Match(Element e) => MatchIndex(e) is var i and >= 0 ? _rules[i].Rule : null;

        /// <summary>Index of the first rule matching the element, or -1.</summary>
        public int MatchIndex(Element e)
        {
            if (Permissions.ModelRedactor.IsPlaceholder(e)) return -1;   // hidden elements give nothing away
            for (var i = 0; i < _rules.Count; i++)
            {
                if (_rules[i].Query is not { } q) continue;
                try { if (q.Matches(_m, e)) return i; }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException) { }
            }
            return -1;
        }
    }

    public static Compiled Compile(ModelRepository m, Diagram d) => new(m, Of(d));

    /// <summary>The first rule of <paramref name="d"/> matching <paramref name="e"/>, or null.</summary>
    public static ColorRule? Match(ModelRepository m, Diagram d, Element e) => Compile(m, d).Match(e);

    /// <summary>Which rule colours each shape and path of a diagram (first match wins).</summary>
    public sealed record Matches(IReadOnlyList<ColorRule> Rules, Dictionary<string, int> Nodes, Dictionary<string, int> Edges)
    {
        public static readonly Matches None = new([], new(), new());

        public ColorRule? ForNode(string nodeId) => Nodes.TryGetValue(nodeId, out var i) ? Rules[i] : null;
        public ColorRule? ForEdge(string edgeId) => Edges.TryGetValue(edgeId, out var i) ? Rules[i] : null;

        /// <summary>Rules colouring at least one shape or path, in rule order (what the legend lists).</summary>
        public IReadOnlyList<ColorRule> Active =>
            Rules.Where((_, i) => Nodes.ContainsValue(i) || Edges.ContainsValue(i)).ToList();
    }

    /// <summary>Evaluates the rules of a diagram against everything shown on it (by shape / path id).</summary>
    public static Matches Evaluate(ModelRepository m, Diagram d)
    {
        if (!HasRules(d)) return Matches.None;
        var c = Compile(m, d);
        if (c.IsEmpty) return Matches.None;
        var nodes = new Dictionary<string, int>(StringComparer.Ordinal);
        var edges = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in d.Nodes)
            if (c.MatchIndex(n.Element) is var i and >= 0) nodes[n.Id] = i;
        foreach (var e in d.Edges)
            if (c.MatchIndex(e.Relationship) is var i and >= 0) edges[e.Id] = i;
        return new Matches(c.Rules, nodes, edges);
    }

    /// <summary>How many shapes on the diagram each rule colours (first match wins), in rule order.</summary>
    public static IReadOnlyList<int> MatchCounts(ModelRepository m, Diagram d)
    {
        var c = Compile(m, d);
        var counts = new int[c.Rules.Count];
        foreach (var n in d.Nodes)
            if (c.MatchIndex(n.Element) is var i and >= 0) counts[i]++;
        return counts;
    }

    // ================================================================ simple conditions (the editor's builder)

    /// <summary>
    /// A condition the editor can show as pickers: an optional element kind, then <c>field op value</c>.
    /// Null fields mean "not used". Anything more complex is edited as query text.
    /// </summary>
    public sealed record SimpleCondition(ElementKind? Kind, string? Field, QueryOp Op = QueryOp.Eq, string Value = "")
    {
        public string ToQuery()
        {
            var parts = new List<string>();
            if (Kind is { } k) parts.Add("kind:" + k);
            if (!string.IsNullOrWhiteSpace(Field))
                parts.Add("where " + new CompareExpr(Field.Trim(), Op, Op is QueryOp.Empty or QueryOp.NotEmpty ? "" : Value ?? ""));
            return string.Join(" ", parts);
        }
    }

    /// <summary>The condition as pickers, or null when the query text is not that simple.</summary>
    public static SimpleCondition? AsSimple(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return new SimpleCondition(null, null);
        if (!ModelQuery.TryParse(query, out var q, out _)) return null;
        if (q.Kinds.Count > 1 || !string.IsNullOrWhiteSpace(q.Stereotype) || !string.IsNullOrWhiteSpace(q.Scope)) return null;
        ElementKind? kind = q.Kinds.Count == 1 ? q.Kinds[0] : null;
        return q.Where switch
        {
            null => new SimpleCondition(kind, null),
            CompareExpr c => new SimpleCondition(kind, c.Field, c.Op, c.Value),
            _ => null,
        };
    }
}
