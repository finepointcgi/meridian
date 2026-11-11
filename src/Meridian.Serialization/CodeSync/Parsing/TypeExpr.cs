using System.Text;

namespace Meridian.Serialization.CodeSync.Parsing;

/// <summary>
/// A parsed type reference: a (dotted) name with type arguments and array rank, or a union of alternatives. Constructs the
/// readers do not model (function types, object literals, tuples, conditional types …) have an empty <see cref="Name"/>
/// and keep the names they mention in <see cref="Refs"/>, so they still count as usage.
/// </summary>
internal sealed class TypeExpr
{
    public string Name { get; init; } = "";
    public List<TypeExpr> Args { get; init; } = [];
    public int Array { get; set; }
    /// <summary>Alternatives of a union (<c>A | B</c>); null when this is not a union.</summary>
    public List<TypeExpr>? Union { get; init; }
    /// <summary>Other names mentioned (opaque constructs, intersections, wildcard bounds).</summary>
    public List<string> Refs { get; init; } = [];

    /// <summary>A fresh empty expression (callers may mutate <see cref="Array"/>, so it is never shared).</summary>
    public static TypeExpr Empty => new();

    /// <summary>Every dotted name in the expression, recursively.</summary>
    public IEnumerable<string> Names()
    {
        if (Name.Length > 0) yield return Name;
        foreach (var r in Refs) yield return r;
        foreach (var a in Args) foreach (var x in a.Names()) yield return x;
        if (Union != null) foreach (var u in Union) foreach (var x in u.Names()) yield return x;
    }

    public string LastSegment => Name.Contains('.') ? Name[(Name.LastIndexOf('.') + 1)..] : Name;

    /// <summary>1-based line where the expression starts; 0 when unknown (built by a reader, not parsed).</summary>
    public int Line { get; set; }

    /// <summary>Every dotted name with its line (a part without a line of its own takes its parent's, then
    /// <paramref name="fallback"/>).</summary>
    public IEnumerable<(string Name, int Line)> NamesAt(int fallback)
    {
        var line = Line > 0 ? Line : fallback;
        if (Name.Length > 0) yield return (Name, line);
        foreach (var r in Refs) yield return (r, line);
        foreach (var a in Args) foreach (var x in a.NamesAt(line)) yield return x;
        if (Union != null) foreach (var u in Union) foreach (var x in u.NamesAt(line)) yield return x;
    }

    /// <summary>The names of this expression as uses of one kind (a field's type, a parameter's type …).</summary>
    public IEnumerable<NameRef> Uses(int fallback, Meridian.Core.CodeSync.CodeDependencyKind kind) =>
        NamesAt(fallback).Select(x => new NameRef(x.Name, x.Line, kind));

    /// <summary>Moves the expression and its parts to <paramref name="line"/> (a type parsed from a string literal).</summary>
    public void Relocate(int line)
    {
        Line = line;
        foreach (var a in Args) a.Relocate(line);
        if (Union != null) foreach (var u in Union) u.Relocate(line);
    }
}

/// <summary>A name used in code: the dotted name as written, its 1-based line and how it is used.</summary>
internal readonly record struct NameRef(string Name, int Line, Meridian.Core.CodeSync.CodeDependencyKind Kind)
{
    /// <summary>The file it is in when that is not the owning type's (declaration merging across files).</summary>
    public string? File { get; init; }
}

/// <summary>Per-language knowledge for unwrapping a member's type to the element it holds.</summary>
internal sealed class TypeVocabulary
{
    /// <summary>Generic types whose first argument is held many times (List, Set, list, Sequence …).</summary>
    public required HashSet<string> Collections { get; init; }
    /// <summary>Generic types whose last argument is held many times (Map, dict, Record …).</summary>
    public required HashSet<string> Maps { get; init; }
    /// <summary>Generic types whose first argument may be absent (Optional).</summary>
    public HashSet<string> Optionals { get; init; } = [];
    /// <summary>Generic wrappers that hold their first argument once (Promise, Readonly, Final …).</summary>
    public HashSet<string> Wrappers { get; init; } = [];
    /// <summary>Names that stand for "no value" in a union (null, undefined, None).</summary>
    public HashSet<string> NullNames { get; init; } = [];
}

/// <summary>The element a type holds, as the code-sync readers need it.</summary>
internal readonly record struct ElementInfo(string? Symbol, bool Many, bool Optional);

internal static class TypeExprs
{
    /// <summary>
    /// Unwraps unions with null, arrays, optionals, wrappers, collections and maps (unless the generic type itself is a
    /// scanned type) down to the held element, and resolves it.
    /// </summary>
    public static ElementInfo Element(TypeExpr? t, TypeVocabulary vocab, Func<string, string?> resolve)
    {
        if (t == null) return default;
        var many = false;
        var optional = false;
        for (var guard = 0; guard < 12; guard++)
        {
            if (t.Union != null)
            {
                var real = t.Union.Where(u => !(u.Name.Length > 0 && vocab.NullNames.Contains(u.Name) && u.Args.Count == 0 && u.Array == 0)).ToList();
                if (real.Count < t.Union.Count) optional = true;
                if (real.Count != 1) return new ElementInfo(null, false, optional);
                t = real[0];
                continue;
            }
            if (t.Array > 0)
            {
                many = true;
                t = new TypeExpr { Name = t.Name, Args = t.Args, Refs = t.Refs };
                continue;
            }
            if (t.Name.Length == 0) break;
            var last = t.LastSegment;
            if (t.Args.Count >= 1 && resolve(t.Name) == null)
            {
                if (vocab.Optionals.Contains(last)) { optional = true; t = t.Args[0]; continue; }
                if (vocab.Wrappers.Contains(last)) { t = t.Args[0]; continue; }
                if (vocab.Collections.Contains(last)) { many = true; t = t.Args[0]; continue; }
                if (vocab.Maps.Contains(last) && t.Args.Count >= 2) { many = true; t = t.Args[^1]; continue; }
            }
            break;
        }
        var s = t.Name.Length > 0 ? resolve(t.Name) : null;
        return new ElementInfo(s, s != null && many, optional && !many);
    }

    /// <summary>
    /// Parses a type from <paramref name="toks"/>[<paramref name="pos"/>..<paramref name="end"/>), advancing
    /// <paramref name="pos"/> past it. <paramref name="python"/> uses <c>X[args]</c> generics and string annotations; the
    /// brace languages use <c>X&lt;args&gt;</c>, <c>T[]</c> arrays, <c>?</c> wildcards and <c>@Annotations</c>. Never throws.
    /// </summary>
    public static TypeExpr Parse(List<Tok> toks, ref int pos, int end, bool python, int depth = 0)
    {
        if (depth > 24 || pos >= end) return TypeExpr.Empty;
        var startLine = toks[pos].Line;
        var alts = new List<TypeExpr>();
        if (pos < end && toks[pos].Is("|")) pos++;   // leading '|' in TypeScript unions
        while (pos < end)
        {
            var one = ParseIntersection(toks, ref pos, end, python, depth);
            alts.Add(one);
            if (pos < end && toks[pos].Is("|")) { pos++; continue; }
            break;
        }
        var result = alts.Count == 1 ? alts[0] : new TypeExpr { Union = alts };
        if (result.Line == 0) result.Line = startLine;
        return result;
    }

    /// <summary>
    /// Dotted names in a token range with their line and how they are used: after <c>new</c> a creation, followed by
    /// <c>(</c> a call (a call of a type itself is a creation; the reader decides once names are resolved), else a use.
    /// </summary>
    public static IEnumerable<NameRef> RefsIn(List<Tok> toks, int from, int end)
    {
        for (var i = Math.Max(0, from); i < end && i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Kind != TokKind.Ident) continue;
            if (i > 0 && (toks[i - 1].Is(".") || toks[i - 1].Is("?."))) continue;
            var sb = new StringBuilder(t.Text);
            var j = i + 1;
            while (j + 1 < end && toks[j].Is(".") && toks[j + 1].Kind == TokKind.Ident)
            {
                sb.Append('.').Append(toks[j + 1].Text);
                j += 2;
            }
            var kind = i > 0 && toks[i - 1].Is("new") ? Meridian.Core.CodeSync.CodeDependencyKind.New
                : j < end && j < toks.Count && toks[j].Is("(") ? Meridian.Core.CodeSync.CodeDependencyKind.Call
                : Meridian.Core.CodeSync.CodeDependencyKind.Use;
            yield return new NameRef(sb.ToString(), t.Line, kind);
        }
    }

    private static TypeExpr ParseIntersection(List<Tok> toks, ref int pos, int end, bool python, int depth)
    {
        var first = ParsePostfix(toks, ref pos, end, python, depth);
        if (python || pos >= end || !toks[pos].Is("&")) return first;
        var refs = new List<string>(first.Names());
        while (pos < end && toks[pos].Is("&"))
        {
            pos++;
            refs.AddRange(ParsePostfix(toks, ref pos, end, python, depth).Names());
        }
        return new TypeExpr { Refs = refs };
    }

    private static TypeExpr ParsePostfix(List<Tok> toks, ref int pos, int end, bool python, int depth)
    {
        var t = ParsePrimary(toks, ref pos, end, python, depth);
        if (python) return t;
        while (pos + 1 < end && toks[pos].Is("[") && toks[pos + 1].Is("]"))
        {
            t.Array++;
            pos += 2;
        }
        // Indexed access (T["key"]) and similar: keep the names, drop the structure.
        while (pos < end && toks[pos].Is("["))
        {
            var close = Matching(toks, pos, end);
            var refs = new List<string>(t.Names());
            refs.AddRange(NamesIn(toks, pos + 1, close));
            t = new TypeExpr { Refs = refs };
            pos = Math.Min(end, close + 1);
        }
        return t;
    }

    private static readonly HashSet<string> TsPrefixes = new(StringComparer.Ordinal) { "readonly", "keyof", "typeof", "unique", "infer", "asserts", "out", "in", "const" };

    private static TypeExpr ParsePrimary(List<Tok> toks, ref int pos, int end, bool python, int depth)
    {
        if (pos >= end) return TypeExpr.Empty;
        var tok = toks[pos];
        if (!python)
        {
            // Annotations on types (Java): @NonNull String.
            while (pos < end && toks[pos].Is("@") && pos + 1 < end && toks[pos + 1].Kind == TokKind.Ident)
            {
                pos += 2;
                while (pos + 1 < end && toks[pos].Is(".") && toks[pos + 1].Kind == TokKind.Ident) pos += 2;
                if (pos < end && toks[pos].Is("(")) pos = Math.Min(end, Matching(toks, pos, end) + 1);
            }
            if (pos >= end) return TypeExpr.Empty;
            tok = toks[pos];
            if (tok.Kind == TokKind.Ident && TsPrefixes.Contains(tok.Text) && pos + 1 < end && toks[pos + 1].Kind is TokKind.Ident or TokKind.Punct && !toks[pos + 1].Is(",") && !toks[pos + 1].Is(">") && !toks[pos + 1].Is(")") && !toks[pos + 1].Is("|"))
            {
                pos++;
                return ParsePostfix(toks, ref pos, end, python, depth + 1);
            }
            if (tok.Is("?"))
            {
                // Java wildcard: ? / ? extends T / ? super T.
                pos++;
                if (pos < end && toks[pos].Kind == TokKind.Ident && toks[pos].Text is "extends" or "super")
                {
                    pos++;
                    return ParsePostfix(toks, ref pos, end, python, depth + 1);
                }
                return TypeExpr.Empty;
            }
        }
        if (tok.Kind == TokKind.String)
        {
            pos++;
            if (!python) return TypeExpr.Empty;   // a string literal type
            // A forward reference: "Battery" or "list[Cell]".
            var inner = Lexer.Python(tok.Text).Where(x => x.Kind != TokKind.Newline).ToList();
            var ip = 0;
            var parsed = Parse(inner, ref ip, inner.Count, python, depth + 1);
            parsed.Relocate(tok.Line);
            return parsed;
        }
        if (tok.Kind == TokKind.Number) { pos++; return TypeExpr.Empty; }
        if (tok.Kind == TokKind.Ident)
        {
            var sb = new StringBuilder(tok.Text);
            if (!python && tok.Is("import") && pos + 4 < end && toks[pos + 1].Is("(") && toks[pos + 2].Kind == TokKind.String && toks[pos + 3].Is(")")
                && toks[pos + 4].Is("."))
            {
                // A TypeScript inline import type: import("./battery").Battery → name "import(./battery).Battery".
                sb.Clear().Append("import(").Append(toks[pos + 2].Text).Append(')');
                pos += 3;
            }
            pos++;
            while (pos + 1 < end && toks[pos].Is(".") && toks[pos + 1].Kind == TokKind.Ident)
            {
                sb.Append('.').Append(toks[pos + 1].Text);
                pos += 2;
            }
            var args = new List<TypeExpr>();
            var open = python ? "[" : "<";
            if (pos < end && toks[pos].Is(open))
            {
                var close = Matching(toks, pos, end);
                var p = pos + 1;
                while (p < close)
                {
                    var before = p;
                    if (python && toks[p].Is("["))
                    {
                        // Callable[[A, B], R]: the parameter list.
                        var c2 = Matching(toks, p, close);
                        args.Add(new TypeExpr { Refs = [.. NamesIn(toks, p + 1, c2)] });
                        p = Math.Min(close, c2 + 1);
                    }
                    else args.Add(Parse(toks, ref p, close, python, depth + 1));
                    if (p < close && toks[p].Is(",")) p++;
                    else if (p < close)
                    {
                        // Something we do not understand inside the arguments: keep its names and move on.
                        var next = NextAtDepth(toks, p, close, ",");
                        if (args.Count > 0) args[^1].Refs.AddRange(NamesIn(toks, p, next));
                        p = next < close ? next + 1 : close;
                    }
                    if (p == before) p++;
                }
                pos = Math.Min(end, close + 1);
            }
            var dotted = sb.ToString();
            // typing.Union[A, B] is the same as A | B.
            if (python && args.Count > 0 && (dotted == "Union" || dotted.EndsWith(".Union", StringComparison.Ordinal))) return new TypeExpr { Union = args };
            return new TypeExpr { Name = dotted, Args = args };
        }
        if (tok.Is("(") || tok.Is("{") || tok.Is("["))
        {
            // Parenthesised type, function type, object literal type or tuple: opaque.
            var close = Matching(toks, pos, end);
            if (tok.Is("(") && !python)
            {
                var after = close + 1;
                if (after < end && toks[after].Is("=>"))
                {
                    var refs = NamesIn(toks, pos + 1, close).ToList();
                    pos = after + 1;
                    refs.AddRange(Parse(toks, ref pos, end, python, depth + 1).Names());
                    return new TypeExpr { Refs = refs };
                }
                var p = pos + 1;
                var inner = Parse(toks, ref p, close, python, depth + 1);
                if (p == close)
                {
                    pos = Math.Min(end, close + 1);
                    return inner;
                }
            }
            var names = NamesIn(toks, pos + 1, close).ToList();
            pos = Math.Min(end, close + 1);
            return new TypeExpr { Refs = names };
        }
        if (!python && tok.Is("new"))
        {
            pos++;
            return ParsePrimary(toks, ref pos, end, python, depth + 1);
        }
        pos++;
        return TypeExpr.Empty;
    }

    /// <summary>Index of the bracket closing the one at <paramref name="open"/> (any of ( [ { &lt;), or <paramref name="end"/>.</summary>
    public static int Matching(List<Tok> toks, int open, int end)
    {
        var stack = new Stack<string>();
        for (var i = open; i < end; i++)
        {
            var t = toks[i];
            if (t.Kind != TokKind.Punct) continue;
            switch (t.Text)
            {
                case "(": stack.Push(")"); break;
                case "[": stack.Push("]"); break;
                case "{": stack.Push("}"); break;
                case "<" when toks[open].Is("<"): stack.Push(">"); break;
                case ")" or "]" or "}" or ">":
                    if (t.Text == ">" && !toks[open].Is("<")) break;
                    // Tolerate mismatches: pop until the matching kind (or give up on that bracket).
                    if (stack.Count > 0 && stack.Peek() == t.Text) stack.Pop();
                    else if (stack.Contains(t.Text)) { while (stack.Pop() != t.Text) { } }
                    else break;
                    if (stack.Count == 0) return i;
                    break;
            }
        }
        return end;
    }

    /// <summary>The first index in [from, end) holding <paramref name="text"/> outside brackets, or end.</summary>
    public static int NextAtDepth(List<Tok> toks, int from, int end, params string[] text)
    {
        var depth = 0;
        for (var i = from; i < end; i++)
        {
            var t = toks[i];
            if (t.Kind == TokKind.Punct)
            {
                if (depth == 0 && text.Contains(t.Text)) return i;
                if (t.Text is "(" or "[" or "{") depth++;
                else if (t.Text is ")" or "]" or "}") { if (depth == 0) return i; depth--; }
            }
            else if (depth == 0 && t.Kind == TokKind.Ident && text.Contains(t.Text)) return i;
        }
        return end;
    }

    /// <summary>
    /// Splits [from, end) at commas outside brackets. With <paramref name="angles"/>, '&lt;' … '&gt;' count as brackets too
    /// (parameter lists, where they only appear in generics).
    /// </summary>
    public static List<(int From, int End)> SplitTopLevel(List<Tok> toks, int from, int end, bool angles)
    {
        var parts = new List<(int, int)>();
        var depth = 0;
        var angle = 0;
        var start = from;
        for (var i = from; i < end && i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Kind != TokKind.Punct) continue;
            switch (t.Text)
            {
                case "(" or "[" or "{": depth++; break;
                case ")" or "]" or "}": depth = Math.Max(0, depth - 1); break;
                case "<" when angles: angle++; break;
                case ">" when angles: angle = Math.Max(0, angle - 1); break;
                case "," when depth == 0 && angle == 0:
                    parts.Add((start, i));
                    start = i + 1;
                    break;
            }
        }
        if (start < end) parts.Add((start, end));
        return parts;
    }

    /// <summary>Dotted names in a token range: an identifier not preceded by '.', with its '.'-joined continuation.</summary>
    public static IEnumerable<string> NamesIn(List<Tok> toks, int from, int end)
    {
        for (var i = Math.Max(0, from); i < end && i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Kind != TokKind.Ident) continue;
            if (i > 0 && (toks[i - 1].Is(".") || toks[i - 1].Is("?."))) continue;
            var sb = new StringBuilder(t.Text);
            var j = i + 1;
            while (j + 1 < end && toks[j].Is(".") && toks[j + 1].Kind == TokKind.Ident)
            {
                sb.Append('.').Append(toks[j + 1].Text);
                j += 2;
            }
            yield return sb.ToString();
        }
    }

    /// <summary>Source-like text of a token range: spaces only between words and after commas / around '|', '&amp;' and '=&gt;'.</summary>
    public static string Text(List<Tok> toks, int from, int end)
    {
        var sb = new StringBuilder();
        Tok? prev = null;
        for (var i = Math.Max(0, from); i < end && i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Kind == TokKind.Newline) continue;
            if (prev != null)
            {
                var word = prev.Kind is TokKind.Ident or TokKind.Number && t.Kind is TokKind.Ident or TokKind.Number;
                if (word || prev.Is(",") || prev.Is(":") || t.Is("|") || prev.Is("|") || t.Is("&") || prev.Is("&") || t.Is("=>") || prev.Is("=>")
                    || (prev.Kind == TokKind.Ident && t.Is("{")) || prev.Is("?") && t.Kind == TokKind.Ident || prev.Is("{") || t.Is("}") || prev.Is(";"))
                    sb.Append(' ');
            }
            sb.Append(t.Kind == TokKind.String ? Quote(t.Text) : t.Text);
            prev = t;
        }
        return sb.ToString();
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
}
