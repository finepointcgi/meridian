using System.Globalization;
using System.Text;

namespace Meridian.Web.Integrations;

/// <summary>An OSLC query parameter Meridian cannot evaluate; answered with 400 and an <c>oslc:Error</c>.</summary>
public sealed class OslcQueryException(string message) : Exception(message);

/// <summary>
/// The subset of the OSLC Query syntax (OSLC Core 2.0 / Query 3.0) the Meridian provider evaluates:
/// <list type="bullet">
/// <item><c>oslc.where</c>: terms joined by <c>and</c>; each term is <c>prop op value</c> with op one of
/// <c>= != &lt; &gt; &lt;= &gt;=</c>, or <c>prop in [v1, v2]</c>. Values are <c>"strings"</c> (optionally
/// <c>^^xsd:type</c> or <c>@lang</c>, which are ignored), numbers, <c>true</c>/<c>false</c> or <c>&lt;uri&gt;</c>.
/// Nested <c>prop{…}</c> terms are refused.</item>
/// <item><c>oslc.select</c> and <c>oslc.properties</c>: a comma-separated property list; <c>*</c> means all;
/// nested selections <c>prop{…}</c> select <c>prop</c>.</item>
/// <item><c>oslc.orderBy</c>: <c>+prop</c> / <c>-prop</c>, comma-separated.</item>
/// <item><c>oslc.prefix</c>: <c>p=&lt;uri&gt;,q=&lt;uri&gt;</c>, added to the standard prefixes.</item>
/// </list>
/// Properties are compared as full IRIs, so any declared prefix works.
/// </summary>
public static class OslcQuery
{
    public enum Op { Eq, Ne, Lt, Gt, Le, Ge, In }

    /// <summary>A query value: a literal or an IRI.</summary>
    public sealed record Value(string Text, bool IsIri);

    public sealed record Term(string Property, Op Op, IReadOnlyList<Value> Values);

    public sealed record Order(string Property, bool Descending);

    public static Dictionary<string, string> Prefixes(string? oslcPrefix)
    {
        var map = RdfPrefixes.Default.ToDictionary(p => p.Prefix, p => p.Ns);
        if (string.IsNullOrWhiteSpace(oslcPrefix)) return map;
        foreach (var part in SplitTopLevel(oslcPrefix, ','))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) throw new OslcQueryException($"oslc.prefix: “{part}” is not prefix=<uri>.");
            var name = part[..eq].Trim();
            var uri = part[(eq + 1)..].Trim();
            if (!uri.StartsWith('<') || !uri.EndsWith('>')) throw new OslcQueryException($"oslc.prefix: the namespace of “{name}” must be written <uri>.");
            map[name] = uri[1..^1];
        }
        return map;
    }

    private static string Expand(string name, IReadOnlyDictionary<string, string> prefixes, string param)
    {
        name = name.Trim();
        if (name.StartsWith('<') && name.EndsWith('>')) return name[1..^1];
        var colon = name.IndexOf(':');
        if (colon <= 0) throw new OslcQueryException($"{param}: “{name}” needs a prefix, e.g. dcterms:title.");
        if (!prefixes.TryGetValue(name[..colon], out var ns)) throw new OslcQueryException($"{param}: unknown prefix “{name[..colon]}” (declare it with oslc.prefix).");
        return ns + name[(colon + 1)..];
    }

    // ---------------------------------------------------------------- oslc.where

    public static IReadOnlyList<Term> ParseWhere(string? where, IReadOnlyDictionary<string, string> prefixes)
    {
        if (string.IsNullOrWhiteSpace(where)) return [];
        var s = new Scanner(where);
        var terms = new List<Term>();
        while (true)
        {
            s.Skip();
            var prop = s.Name();
            if (prop.Length == 0) throw new OslcQueryException($"oslc.where: expected a property at position {s.Pos + 1}.");
            s.Skip();
            if (s.Peek == '{') throw new OslcQueryException("oslc.where: nested (scoped) terms are not supported.");
            var iri = Expand(prop, prefixes, "oslc.where");
            Op op;
            if (s.TryWord("in"))
            {
                s.Skip();
                s.Expect('[');
                var values = new List<Value>();
                while (true)
                {
                    s.Skip();
                    values.Add(s.ReadValue(prefixes));
                    s.Skip();
                    if (s.Peek == ',') { s.Pos++; continue; }
                    s.Expect(']');
                    break;
                }
                terms.Add(new Term(iri, Op.In, values));
            }
            else
            {
                op = s.ReadOp();
                s.Skip();
                terms.Add(new Term(iri, op, [s.ReadValue(prefixes)]));
            }
            s.Skip();
            if (s.End) break;
            if (!s.TryWord("and")) throw new OslcQueryException($"oslc.where: expected “and” at position {s.Pos + 1} (only “and” is supported).");
        }
        return terms;
    }

    /// <summary>True when <paramref name="values"/> (the resource's values of the term's property) satisfy the term.</summary>
    public static bool Matches(Term t, IReadOnlyList<string> values)
    {
        if (t.Op == Op.Ne) return values.All(v => !t.Values.Any(q => Equal(v, q.Text)));
        if (t.Op == Op.In) return values.Any(v => t.Values.Any(q => Equal(v, q.Text)));
        var q0 = t.Values[0].Text;
        return values.Any(v => t.Op switch
        {
            Op.Eq => Equal(v, q0),
            Op.Lt => Compare(v, q0) < 0,
            Op.Gt => Compare(v, q0) > 0,
            Op.Le => Compare(v, q0) <= 0,
            Op.Ge => Compare(v, q0) >= 0,
            _ => false,
        });
    }

    // Identifiers and titles compare case-insensitively (as Meridian's own search does).
    private static bool Equal(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static int Compare(string a, string b) =>
        double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            ? x.CompareTo(y)
            : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- oslc.select / oslc.properties

    /// <summary>The selected top-level properties as IRIs, or null for "all" (no parameter, or <c>*</c>).</summary>
    public static IReadOnlySet<string>? ParseSelect(string? select, IReadOnlyDictionary<string, string> prefixes, string param = "oslc.select")
    {
        if (string.IsNullOrWhiteSpace(select)) return null;
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in SplitTopLevel(select, ','))
        {
            var item = raw.Trim();
            var brace = item.IndexOf('{');
            if (brace >= 0)
            {
                if (!item.EndsWith('}')) throw new OslcQueryException($"{param}: unbalanced braces in “{item}”.");
                item = item[..brace].Trim();
            }
            if (item == "*") return null;
            if (item.Length == 0) throw new OslcQueryException($"{param}: empty property.");
            result.Add(Expand(item, prefixes, param));
        }
        return result;
    }

    // ---------------------------------------------------------------- oslc.orderBy

    public static IReadOnlyList<Order> ParseOrderBy(string? orderBy, IReadOnlyDictionary<string, string> prefixes)
    {
        if (string.IsNullOrWhiteSpace(orderBy)) return [];
        var list = new List<Order>();
        foreach (var raw in SplitTopLevel(orderBy, ','))
        {
            var item = raw.Trim();
            if (item.Contains('{')) throw new OslcQueryException("oslc.orderBy: nested sort keys are not supported.");
            var desc = item.StartsWith('-');
            if (item.StartsWith('-') || item.StartsWith('+')) item = item[1..];
            else throw new OslcQueryException($"oslc.orderBy: “{raw.Trim()}” must start with + or -.");
            list.Add(new Order(Expand(item, prefixes, "oslc.orderBy"), desc));
        }
        return list;
    }

    // ---------------------------------------------------------------- evaluation over any resource type

    /// <summary>
    /// Filters, sorts and returns <paramref name="all"/> by the parsed query. <paramref name="values"/> gives the values
    /// of one property IRI for a resource (literal text or IRI string); unknown properties give no values.
    /// </summary>
    public static IEnumerable<T> Apply<T>(IEnumerable<T> all, IReadOnlyList<Term> where, IReadOnlyList<Order> orderBy, Func<T, string, IReadOnlyList<string>> values)
    {
        var q = all.Where(r => where.All(t => Matches(t, values(r, t.Property))));
        IOrderedEnumerable<T>? sorted = null;
        foreach (var o in orderBy)
        {
            string Key(T r) => values(r, o.Property).FirstOrDefault() ?? "";
            var cmp = Comparer<string>.Create(Compare);
            sorted = sorted is null
                ? (o.Descending ? q.OrderByDescending(Key, cmp) : q.OrderBy(Key, cmp))
                : (o.Descending ? sorted.ThenByDescending(Key, cmp) : sorted.ThenBy(Key, cmp));
        }
        return sorted ?? q;
    }

    // ---------------------------------------------------------------- scanning

    private static IEnumerable<string> SplitTopLevel(string s, char sep)
    {
        var depth = 0;
        var inString = false;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inString) { if (c == '\\') i++; else if (c == '"') inString = false; continue; }
            if (c == '"') inString = true;
            else if (c is '{' or '[' or '<') depth++;
            else if (c is '}' or ']' or '>') depth--;
            else if (c == sep && depth == 0) { yield return s[start..i]; start = i + 1; }
        }
        yield return s[start..];
    }

    private sealed class Scanner(string s)
    {
        public int Pos;
        public bool End => Pos >= s.Length;
        public char Peek => End ? '\0' : s[Pos];

        public void Skip() { while (!End && char.IsWhiteSpace(s[Pos])) Pos++; }

        public void Expect(char c)
        {
            if (Peek != c) throw new OslcQueryException($"oslc.where: expected “{c}” at position {Pos + 1}.");
            Pos++;
        }

        public string Name()
        {
            if (Peek == '<')
            {
                var close = s.IndexOf('>', Pos);
                if (close < 0) throw new OslcQueryException("oslc.where: unterminated <uri>.");
                var iri = s[Pos..(close + 1)];
                Pos = close + 1;
                return iri;
            }
            var start = Pos;
            while (!End && (char.IsLetterOrDigit(Peek) || Peek is '_' or '-' or ':' or '.')) Pos++;
            return s[start..Pos];
        }

        public bool TryWord(string word)
        {
            if (string.Compare(s, Pos, word, 0, word.Length, StringComparison.Ordinal) != 0) return false;
            var after = Pos + word.Length;
            if (after < s.Length && (char.IsLetterOrDigit(s[after]) || s[after] == '_')) return false;
            Pos = after;
            return true;
        }

        public Op ReadOp()
        {
            foreach (var (text, op) in new[] { ("!=", Op.Ne), ("<=", Op.Le), (">=", Op.Ge), ("=", Op.Eq), ("<", Op.Lt), (">", Op.Gt) })
                if (string.Compare(s, Pos, text, 0, text.Length, StringComparison.Ordinal) == 0) { Pos += text.Length; return op; }
            throw new OslcQueryException($"oslc.where: expected a comparison (=, !=, <, >, <=, >=) or “in” at position {Pos + 1}.");
        }

        public Value ReadValue(IReadOnlyDictionary<string, string> prefixes)
        {
            if (Peek == '"')
            {
                Pos++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (End) throw new OslcQueryException("oslc.where: unterminated string.");
                    var c = s[Pos++];
                    if (c == '"') break;
                    if (c == '\\' && !End) { sb.Append(s[Pos++] switch { 'n' => '\n', 't' => '\t', var x => x }); continue; }
                    sb.Append(c);
                }
                if (Peek == '^' && Pos + 1 < s.Length && s[Pos + 1] == '^') { Pos += 2; Name(); }   // typed literal: type ignored
                else if (Peek == '@') { Pos++; while (!End && (char.IsLetterOrDigit(Peek) || Peek == '-')) Pos++; }
                return new Value(sb.ToString(), false);
            }
            if (Peek == '<')
            {
                var iri = Name();
                return new Value(iri[1..^1], true);
            }
            var start = Pos;
            while (!End && !char.IsWhiteSpace(Peek) && Peek is not (',' or ']')) Pos++;
            var word = s[start..Pos];
            if (word.Length == 0) throw new OslcQueryException($"oslc.where: expected a value at position {Pos + 1}.");
            if (double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out _) || word is "true" or "false") return new Value(word, false);
            if (word.Contains(':')) return new Value(Expand(word, prefixes, "oslc.where"), true);   // prefixed name as a value
            throw new OslcQueryException($"oslc.where: “{word}” is not a value — quote strings, e.g. \"{word}\".");
        }
    }
}
