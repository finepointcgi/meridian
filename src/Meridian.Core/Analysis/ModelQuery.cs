using System.Globalization;
using System.Text;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

public enum QueryOp { Eq, Ne, Lt, Gt, Le, Ge, Contains, Empty, NotEmpty }

public enum RelDirection { Any, Out, In }

/// <summary>A condition tree node. Evaluate with <see cref="Eval"/>; print with <see cref="ToString"/> (round-trips through the parser).</summary>
public abstract record QueryExpr
{
    public abstract bool Eval(ModelRepository m, Element e);
    internal abstract int Precedence { get; }   // 0 = or, 1 = and, 2 = unary / atom
}

/// <summary><c>field op value</c>, e.g. <c>mass &gt; 50</c>, <c>name contains Engine</c>, <c>tag:priority is empty</c>.</summary>
public sealed record CompareExpr(string Field, QueryOp Op, string Value = "") : QueryExpr
{
    internal override int Precedence => 2;

    public override bool Eval(ModelRepository m, Element e)
    {
        var values = ModelValues.GetAll(m, e, Field);
        switch (Op)
        {
            case QueryOp.Empty: return values.All(string.IsNullOrWhiteSpace);
            case QueryOp.NotEmpty: return values.Any(v => !string.IsNullOrWhiteSpace(v));
            case QueryOp.Contains: return values.Any(v => v.Contains(Value, StringComparison.OrdinalIgnoreCase));
            case QueryOp.Eq: return values.Any(v => Compare(v, Value) == 0) || values.Count == 0 && Value.Length == 0;
            case QueryOp.Ne: return !values.Any(v => Compare(v, Value) == 0) && !(values.Count == 0 && Value.Length == 0);
            default:
                foreach (var v in values)
                {
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    var c = Compare(v, Value);
                    if (Op switch { QueryOp.Lt => c < 0, QueryOp.Gt => c > 0, QueryOp.Le => c <= 0, _ => c >= 0 }) return true;
                }
                return false;
        }
    }

    /// <summary>
    /// Numeric when both sides are numbers, also with units ("1.2 t" &gt; "900 kg"; a bare number compares with the number
    /// of a quantity), otherwise case-insensitive text comparison.
    /// </summary>
    internal static int Compare(string a, string b)
    {
        if (ModelValues.TryNumber(a, out var x) && ModelValues.TryNumber(b, out var y)) return x.CompareTo(y);
        if (ModelValues.TryQuantity(a, out var qa, out var ua) && ModelValues.TryQuantity(b, out var qb, out var ub))
        {
            if (ua == null || ub == null) return qa.CompareTo(qb);
            if (Units.Compatible(ua, ub)) return ua.ToSi(qa).CompareTo(ub.ToSi(qb));
        }
        return string.Compare(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => Op switch
    {
        QueryOp.Empty => $"{ModelQuery.Quote(Field)} is empty",
        QueryOp.NotEmpty => $"{ModelQuery.Quote(Field)} is not empty",
        _ => $"{ModelQuery.Quote(Field)} {ModelQuery.OpText(Op)} {ModelQuery.Quote(Value)}",
    };
}

/// <summary>Relationship existence: <c>has:satisfy</c>, <c>has:out:allocate</c>, <c>has:in:verify</c>.</summary>
public sealed record RelationExpr(ElementKind Kind, RelDirection Direction = RelDirection.Any) : QueryExpr
{
    internal override int Precedence => 2;
    public override bool Eval(ModelRepository m, Element e) => ModelValues.Related(m, e, Kind, Direction).Any();
    public override string ToString() =>
        "has:" + (Direction == RelDirection.Any ? "" : Direction.ToString().ToLowerInvariant() + ":") + Kind.ToString().ToLowerInvariant();
}

public sealed record NotExpr(QueryExpr Inner) : QueryExpr
{
    internal override int Precedence => 2;
    public override bool Eval(ModelRepository m, Element e) => !Inner.Eval(m, e);
    public override string ToString() => "not " + (Inner.Precedence < 2 ? $"({Inner})" : Inner.ToString());
}

public sealed record AndExpr(IReadOnlyList<QueryExpr> Items) : QueryExpr
{
    internal override int Precedence => 1;
    public override bool Eval(ModelRepository m, Element e) => Items.All(i => i.Eval(m, e));
    public override string ToString() => string.Join(" and ", Items.Select(i => i.Precedence < 1 ? $"({i})" : i.ToString()));
}

public sealed record OrExpr(IReadOnlyList<QueryExpr> Items) : QueryExpr
{
    internal override int Precedence => 0;
    public override bool Eval(ModelRepository m, Element e) => Items.Any(i => i.Eval(m, e));
    public override string ToString() => string.Join(" or ", Items.Select(i => i.ToString()));
}

public sealed class QueryParseException(string message, int position) : Exception(message)
{
    public int Position { get; } = position;
}

/// <summary>
/// A model query, comparable to EA model search / Cameo generic table criteria: an element-kind filter, an optional
/// stereotype and package scope, and a condition tree. The compact text form is
/// <code>kind:Block,InterfaceBlock stereotype:critical in:Vehicle::Structure where mass &gt; 50 and not has:satisfy</code>
/// Conditions: <c>field = value</c> (also <c>!=, &lt;, &gt;, &lt;=, &gt;=, contains, is empty, is not empty</c>),
/// <c>has:[out:|in:]relationKind</c>, <c>no:…</c> (= <c>not has:…</c>), <c>not</c>, <c>and</c>, <c>or</c> and parentheses.
/// Fields use the <see cref="ModelValues"/> key vocabulary.
/// </summary>
public sealed class ModelQuery
{
    /// <summary>Element kinds to include; empty means every ordinary model element.</summary>
    public List<ElementKind> Kinds { get; } = new();

    /// <summary>Only elements carrying this stereotype (case-insensitive).</summary>
    public string? Stereotype { get; set; }

    /// <summary>Only elements inside this package / element (id, qualified name or unique name).</summary>
    public string? Scope { get; set; }

    public QueryExpr? Where { get; set; }

    public ModelQuery() { }

    public ModelQuery(ElementKind kind, QueryExpr? where = null)
    {
        Kinds.Add(kind);
        Where = where;
    }

    // ------------------------------------------------------------------ evaluation

    /// <summary>Elements matching the query, ordered by qualified name.</summary>
    public IReadOnlyList<Element> Run(ModelRepository m)
    {
        IEnumerable<Element> source;
        if (!string.IsNullOrWhiteSpace(Scope))
        {
            var scope = ResolveScope(m);
            if (scope == null) return [];
            source = scope.Descendants();
        }
        else source = m.AllElements;
        return source.Where(e => MatchesFilters(e) && (Where == null || Where.Eval(m, e)))
                     .OrderBy(e => e.QualifiedName, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id, StringComparer.Ordinal)
                     .ToList();
    }

    public bool Matches(ModelRepository m, Element e)
    {
        if (!MatchesFilters(e)) return false;
        if (!string.IsNullOrWhiteSpace(Scope) && (ResolveScope(m) is not { } scope || !scope.IsAncestorOf(e))) return false;
        return Where == null || Where.Eval(m, e);
    }

    private bool MatchesFilters(Element e)
    {
        if (e.Kind == ElementKind.Model) return false;
        if (Kinds.Count > 0 ? !Kinds.Contains(e.Kind) : e.IsRelationship || e.Kind is ElementKind.Diagram or ElementKind.TableView or ElementKind.QueryView or ElementKind.ValidationRule or ElementKind.RelationMap)
            return false;
        return string.IsNullOrWhiteSpace(Stereotype) || e.Stereotypes.Contains(Stereotype.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    public Element? ResolveScope(ModelRepository m)
    {
        if (string.IsNullOrWhiteSpace(Scope)) return null;
        var s = Scope.Trim();
        if (m.Find(s) is { } byId) return byId;
        if (s.StartsWith(m.Root.Name + "::", StringComparison.OrdinalIgnoreCase)) s = s[(m.Root.Name.Length + 2)..];   // "Model::Pkg"
        if (string.Equals(s, m.Root.Name, StringComparison.OrdinalIgnoreCase)) return m.Root;
        var all = m.AllElements.Where(e => e is not Relationship && e.Kind != ElementKind.Diagram).ToList();
        return all.FirstOrDefault(e => string.Equals(e.QualifiedName, s, StringComparison.OrdinalIgnoreCase))
               ?? all.Where(e => string.Equals(e.Name, s, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(e => e.Kind is ElementKind.Package or ElementKind.Model ? 0 : 1).ThenBy(e => e.AncestorsAndSelf().Count()).FirstOrDefault();
    }

    // ------------------------------------------------------------------ text form

    public override string ToString()
    {
        var parts = new List<string>();
        if (Kinds.Count > 0) parts.Add("kind:" + string.Join(",", Kinds));
        if (!string.IsNullOrWhiteSpace(Stereotype)) parts.Add("stereotype:" + Quote(Stereotype.Trim()));
        if (!string.IsNullOrWhiteSpace(Scope)) parts.Add("in:" + Quote(Scope.Trim()));
        if (Where != null) parts.Add("where " + Where);
        return string.Join(" ", parts);
    }

    public static ModelQuery Parse(string text) => new Parser(text).ParseQuery();

    public static bool TryParse(string? text, out ModelQuery query, out string? error)
    {
        try { query = Parse(text ?? ""); error = null; return true; }
        catch (QueryParseException ex) { query = new ModelQuery(); error = ex.Message; return false; }
    }

    /// <summary>Parses just a condition expression (no kind / scope clauses).</summary>
    public static QueryExpr? ParseCondition(string text)
    {
        var q = Parse(string.IsNullOrWhiteSpace(text) ? "" : "where " + text);
        return q.Where;
    }

    public static bool TryParseKind(string s, out ElementKind kind)
    {
        s = s.Trim().Replace(" ", "");
        if (Enum.TryParse(s, true, out kind) && Enum.IsDefined(kind) && !int.TryParse(s, out _)) return true;
        foreach (var k in Enum.GetValues<ElementKind>())
            if (string.Equals(Metamodel.DisplayName(k).Replace(" ", "").Replace("/", ""), s, StringComparison.OrdinalIgnoreCase)) { kind = k; return true; }
        if (s.Length > 3 && s.EndsWith("ies", StringComparison.OrdinalIgnoreCase)) return TryParseKind(s[..^3] + "y", out kind);   // "Activities"
        if (s.Length > 1 && s.EndsWith('s')) return TryParseKind(s[..^1], out kind);   // "Blocks"
        if (s.Equals("Satisfies", StringComparison.OrdinalIgnoreCase)) { kind = ElementKind.Satisfy; return true; }
        return false;
    }

    internal static string OpText(QueryOp op) => op switch
    {
        QueryOp.Eq => "=", QueryOp.Ne => "!=", QueryOp.Lt => "<", QueryOp.Gt => ">", QueryOp.Le => "<=", QueryOp.Ge => ">=",
        QueryOp.Contains => "contains", QueryOp.Empty => "is empty", _ => "is not empty",
    };

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase) { "and", "or", "not", "where", "is", "empty", "contains" };

    /// <summary>Quotes a word when it would not survive tokenization as a bare word.</summary>
    public static string Quote(string s)
    {
        if (s.Length > 0 && !Keywords.Contains(s) && s.All(c => !char.IsWhiteSpace(c) && !Parser.Special(c)))
            return s;
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    // ------------------------------------------------------------------ parser

    private enum T { Word, Str, Op, LParen, RParen, Comma, End }

    private readonly record struct Tok(T Type, string Text, int Pos);

    private sealed class Parser
    {
        /// <summary>Deepest nesting of parentheses / 'not' accepted (deeper input would overflow the stack).</summary>
        internal const int MaxDepth = 200;
        private readonly List<Tok> _toks;
        private int _i, _depth;

        public Parser(string text) => _toks = Lex(text ?? "");

        internal static bool Special(char c) => c is '(' or ')' or ',' or '=' or '!' or '<' or '>' or '~' or '"' or '\'';

        private static List<Tok> Lex(string s)
        {
            var list = new List<Tok>();
            var i = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                var start = i;
                switch (c)
                {
                    case '(': list.Add(new(T.LParen, "(", i++)); continue;
                    case ')': list.Add(new(T.RParen, ")", i++)); continue;
                    case ',': list.Add(new(T.Comma, ",", i++)); continue;
                    case '"' or '\'':
                    {
                        var sb = new StringBuilder();
                        i++;
                        while (i < s.Length && s[i] != c)
                        {
                            if (s[i] == '\\' && i + 1 < s.Length) i++;
                            sb.Append(s[i++]);
                        }
                        if (i >= s.Length) throw new QueryParseException($"Unterminated string starting at {start + 1}", start);
                        i++;
                        // A prefix word directly followed by a quoted string (kind:"Use Case") forms one word.
                        if (list.Count > 0 && list[^1] is { Type: T.Word } prev && prev.Text.EndsWith(':') && prev.Pos + prev.Text.Length == start)
                            list[^1] = new Tok(T.Word, prev.Text + sb, prev.Pos);
                        else list.Add(new(T.Str, sb.ToString(), start));
                        continue;
                    }
                    case '=' or '!' or '<' or '>' or '~':
                    {
                        var two = i + 1 < s.Length ? s.Substring(i, 2) : "";
                        if (two is "!=" or "<=" or ">=" or "==" or "<>") { list.Add(new(T.Op, two == "==" ? "=" : two == "<>" ? "!=" : two, i)); i += 2; }
                        else if (c == '!') list.Add(new(T.Word, "not", i++));
                        else list.Add(new(T.Op, c.ToString(), i++));
                        continue;
                    }
                }
                while (i < s.Length && !char.IsWhiteSpace(s[i]) && !Special(s[i])) i++;
                var w = s[start..i];
                if (w is "&&") w = "and";
                else if (w is "||") w = "or";
                list.Add(new(T.Word, w, start));
            }
            list.Add(new(T.End, "", s.Length));
            return list;
        }

        private Tok Peek(int ahead = 0) => _toks[Math.Min(_i + ahead, _toks.Count - 1)];
        private Tok Next() => _toks[Math.Min(_i++, _toks.Count - 1)];
        private bool IsWord(string w, int ahead = 0) => Peek(ahead) is { Type: T.Word } t && string.Equals(t.Text, w, StringComparison.OrdinalIgnoreCase);

        private static QueryParseException Error(Tok t, string message) =>
            new(t.Type == T.End ? $"{message} at end of query" : $"{message} at '{t.Text}' (position {t.Pos + 1})", t.Pos);

        public ModelQuery ParseQuery()
        {
            var q = new ModelQuery();
            while (Peek().Type == T.Word && ClausePrefix(Peek().Text) is { } prefix)
            {
                var tok = Next();
                var value = tok.Text[(tok.Text.IndexOf(':') + 1)..];
                if (value.Length == 0 && Peek().Type is T.Str or T.Word) value = Next().Text;
                switch (prefix)
                {
                    case "kind":
                        var names = new List<(string, Tok)> { (value, tok) };
                        while (Peek().Type == T.Comma) { Next(); var n = Next(); names.Add((n.Text, n)); }
                        foreach (var (name, at) in names.Where(n => n.Item1.Length > 0))
                        {
                            if (!TryParseKind(name, out var k)) throw Error(at, $"Unknown element kind '{name}'");
                            if (!q.Kinds.Contains(k)) q.Kinds.Add(k);
                        }
                        break;
                    case "stereotype": q.Stereotype = value; break;
                    case "in": q.Scope = value; break;
                }
            }
            if (IsWord("where") || IsWord("and") && q.Kinds.Count + (q.Scope != null ? 1 : 0) + (q.Stereotype != null ? 1 : 0) > 0) Next();
            if (Peek().Type != T.End) q.Where = ParseOr();
            if (Peek().Type != T.End) throw Error(Peek(), "Unexpected input");
            return q;
        }

        private static string? ClausePrefix(string w)
        {
            var colon = w.IndexOf(':');
            if (colon < 0) return null;
            return w[..colon].ToLowerInvariant() switch
            {
                "kind" or "kinds" => "kind",
                "stereotype" or "st" => "stereotype",
                "in" or "scope" or "package" => "in",
                _ => null,
            };
        }

        private QueryExpr ParseOr()
        {
            var items = new List<QueryExpr> { ParseAnd() };
            while (IsWord("or")) { Next(); items.Add(ParseAnd()); }
            return items.Count == 1 ? items[0] : new OrExpr(items);
        }

        private QueryExpr ParseAnd()
        {
            var items = new List<QueryExpr> { ParseUnary() };
            while (IsWord("and")) { Next(); items.Add(ParseUnary()); }
            return items.Count == 1 ? items[0] : new AndExpr(items);
        }

        private QueryExpr ParseUnary()
        {
            if (++_depth > MaxDepth) throw Error(Peek(), $"The query is nested too deeply (more than {MaxDepth} levels)");
            try
            {
                if (IsWord("not")) { Next(); return new NotExpr(ParseUnary()); }
                if (Peek().Type == T.LParen)
                {
                    Next();
                    var inner = ParseOr();
                    if (Peek().Type != T.RParen) throw Error(Peek(), "Expected ')'");
                    Next();
                    return inner;
                }
                return ParseAtom();
            }
            finally { _depth--; }
        }

        private QueryExpr ParseAtom()
        {
            var t = Next();
            if (t.Type is not (T.Word or T.Str)) throw Error(t, "Expected a condition");
            if (t.Type == T.Word && (t.Text.StartsWith("has:", StringComparison.OrdinalIgnoreCase) || t.Text.StartsWith("no:", StringComparison.OrdinalIgnoreCase)))
            {
                var negate = t.Text.StartsWith("no:", StringComparison.OrdinalIgnoreCase);
                var parts = t.Text.Split(':');
                var dir = RelDirection.Any;
                string kindName;
                if (parts.Length == 2) kindName = parts[1];
                else if (parts.Length == 3 && ModelValues.TryDirection(parts[1], out dir)) kindName = parts[2];
                else throw Error(t, "Expected has:[out:|in:]relationship");
                if (!TryParseKind(kindName, out var k) || !Metamodel.IsRelationship(k)) throw Error(t, $"Unknown relationship kind '{kindName}'");
                QueryExpr rel = new RelationExpr(k, dir);
                return negate ? new NotExpr(rel) : rel;
            }
            if (t.Type == T.Word && Keywords.Contains(t.Text)) throw Error(t, "Expected a field name");
            var field = t.Text;

            if (IsWord("is"))
            {
                Next();
                var not = false;
                if (IsWord("not")) { Next(); not = true; }
                if (!IsWord("empty")) throw Error(Peek(), "Expected 'empty'");
                Next();
                return new CompareExpr(field, not ? QueryOp.NotEmpty : QueryOp.Empty);
            }
            if (IsWord("empty")) { Next(); return new CompareExpr(field, QueryOp.Empty); }
            if (IsWord("not") && IsWord("contains", 1)) { Next(); Next(); return new NotExpr(new CompareExpr(field, QueryOp.Contains, Value())); }
            if (IsWord("contains")) { Next(); return new CompareExpr(field, QueryOp.Contains, Value()); }
            var op = Peek();
            if (op.Type != T.Op) throw Error(op, $"Expected an operator after '{field}'");
            Next();
            var qop = op.Text switch
            {
                "=" => QueryOp.Eq, "!=" => QueryOp.Ne, "<" => QueryOp.Lt, ">" => QueryOp.Gt, "<=" => QueryOp.Le, ">=" => QueryOp.Ge, "~" => QueryOp.Contains,
                _ => throw Error(op, "Unknown operator"),
            };
            return new CompareExpr(field, qop, Value());
        }

        private string Value()
        {
            var v = Next();
            if (v.Type is T.Str) return v.Text;
            if (v.Type is T.Word && !(v.Text is "and" or "or")) return v.Text;
            throw Error(v, "Expected a value");
        }
    }
}

/// <summary>
/// One row of the flat search builder: <c>[and|or] [not] field op value</c> or <c>[and|or] has|no relationship</c>.
/// A flat row list maps to an OR of AND groups (AND binds tighter, as in the text syntax).
/// </summary>
public sealed class QueryRow
{
    public bool Or { get; set; }
    public bool IsRelation { get; set; }
    public bool Negate { get; set; }
    public string Field { get; set; } = "name";
    public QueryOp Op { get; set; } = QueryOp.Contains;
    public string Value { get; set; } = "";
    public ElementKind RelKind { get; set; } = ElementKind.Satisfy;
    public RelDirection RelDirection { get; set; } = RelDirection.Any;

    public QueryExpr? ToExpr()
    {
        QueryExpr? x = IsRelation ? new RelationExpr(RelKind, RelDirection)
            : string.IsNullOrWhiteSpace(Field) ? null : new CompareExpr(Field.Trim(), Op, Value ?? "");
        return x != null && Negate ? new NotExpr(x) : x;
    }

    public static QueryExpr? Combine(IEnumerable<QueryRow> rows)
    {
        var groups = new List<List<QueryExpr>>();
        foreach (var r in rows)
        {
            if (r.ToExpr() is not { } x) continue;
            if (groups.Count == 0 || r.Or) groups.Add(new List<QueryExpr>());
            groups[^1].Add(x);
        }
        var ands = groups.Select(g => g.Count == 1 ? g[0] : new AndExpr(g)).ToList();
        return ands.Count switch { 0 => null, 1 => ands[0], _ => new OrExpr(ands) };
    }

    /// <summary>Flattens a condition tree into builder rows; null when it needs nesting the flat builder can't show.</summary>
    public static List<QueryRow>? Flatten(QueryExpr? where)
    {
        var rows = new List<QueryRow>();
        if (where == null) return rows;
        var groups = where is OrExpr o ? o.Items : [where];
        foreach (var g in groups)
        {
            var items = g is AndExpr a ? a.Items : [g];
            var first = true;
            foreach (var item in items)
            {
                var row = FromAtom(item);
                if (row == null) return null;
                row.Or = first && rows.Count > 0;
                first = false;
                rows.Add(row);
            }
        }
        return rows;
    }

    private static QueryRow? FromAtom(QueryExpr x)
    {
        var negate = false;
        if (x is NotExpr n) { negate = true; x = n.Inner; }
        return x switch
        {
            CompareExpr c => new QueryRow { Negate = negate, Field = c.Field, Op = c.Op, Value = c.Value },
            RelationExpr r => new QueryRow { Negate = negate, IsRelation = true, RelKind = r.Kind, RelDirection = r.Direction },
            _ => null,
        };
    }
}
