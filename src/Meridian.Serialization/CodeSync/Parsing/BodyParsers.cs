using System.Text;
using Meridian.Core.CodeSync;

namespace Meridian.Serialization.CodeSync.Parsing;

/// <summary>
/// A method body read by a hand parser: the statement tree (receivers as written, not yet resolved) and the declared
/// types of its locals ("battery" → "Battery"), which <see cref="HandParsedCodeReader"/> uses to resolve receivers.
/// </summary>
internal sealed class RawBody
{
    public List<CodeStatement> Statements { get; } = [];
    public Dictionary<string, string> Locals { get; } = new(StringComparer.Ordinal);
}

/// <summary>Token helpers shared by the body parsers.</summary>
internal static class BodyText
{
    /// <summary>Tokens [from, to) as source-like text: no spaces around '.', brackets and before ',' or ';'.</summary>
    public static string Join(List<Tok> toks, int from, int to, int max = CodeBodies.MaxText)
    {
        var sb = new StringBuilder();
        Tok? prev = null;
        for (var i = from; i < to && i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Kind == TokKind.Newline) continue;
            if (prev != null && Space(prev, t)) sb.Append(' ');
            sb.Append(t.Kind == TokKind.String ? "\"" + t.Text + "\"" : t.Text);
            prev = t;
            if (sb.Length > max * 2) break;
        }
        return CodeBodies.Clip(sb.ToString(), max);
    }

    private static bool Space(Tok a, Tok b)
    {
        if (a.Text is "." or "(" or "[" or "?." or "!" && a.Kind == TokKind.Punct) return false;
        if (b.Kind == TokKind.Punct && b.Text is "." or "(" or ")" or "[" or "]" or "," or ";" or "?." or ":") return false;
        if (a.Kind == TokKind.Punct && a.Text is "@") return false;
        return true;
    }

    /// <summary>The call at the end of [from, to): the last top-level "(…)" that closes at <paramref name="to"/>, with the
    /// dotted name before it ("this.battery.charge"). Null when the expression does not end in a call.</summary>
    public static (string? Receiver, string Method, int ArgFrom, int ArgTo, int ArgCount)? TrailingCall(List<Tok> toks, int from, int to)
    {
        if (to - from < 3 || !toks[to - 1].Is(")")) return null;
        // Find the matching '(' backwards.
        var depth = 0;
        var open = -1;
        for (var i = to - 1; i >= from; i--)
        {
            if (toks[i].Kind == TokKind.Punct && toks[i].Text is ")" or "]" or "}") depth++;
            else if (toks[i].Kind == TokKind.Punct && toks[i].Text is "(" or "[" or "{")
            {
                depth--;
                if (depth == 0) { open = i; break; }
            }
        }
        if (open <= from || toks[open - 1].Kind != TokKind.Ident) return null;
        var name = toks[open - 1].Text;
        if (name is "if" or "while" or "for" or "switch" or "catch" or "function" or "return" or "typeof") return null;
        // The receiver: everything from 'from' up to the '.' before the name.
        string? receiver = null;
        var k = open - 2;
        if (k >= from && toks[k].Text is "." or "?.")
        {
            receiver = Join(toks, from, k, 60);
            if (receiver.StartsWith("await ", StringComparison.Ordinal)) receiver = receiver[6..];
            if (receiver.StartsWith("new ", StringComparison.Ordinal)) receiver = receiver[4..];
        }
        else if (k >= from && !(toks[k].Is("await") || toks[k].Is("new") || toks[k].Is("yield"))) return null;
        var count = 0;
        var d = 0;
        for (var i = open + 1; i < to - 1; i++)
        {
            if (count == 0) count = 1;
            var t = toks[i];
            if (t.Kind == TokKind.Punct && t.Text is "(" or "[" or "{") d++;
            else if (t.Kind == TokKind.Punct && t.Text is ")" or "]" or "}") d--;
            else if (d == 0 && t.Is(",")) count++;
        }
        return (receiver, name, open + 1, to - 1, count);
    }

    /// <summary>Calls with a dotted receiver anywhere in [from, to) ("battery.charge(" / "self.x.y("), in source order.</summary>
    public static IEnumerable<CodeStatement> InnerCalls(List<Tok> toks, int from, int to, int skipOpen = -1)
    {
        for (var i = from + 2; i < to; i++)
        {
            if (!toks[i].Is("(") || i == skipOpen || toks[i - 1].Kind != TokKind.Ident || toks[i - 2].Text is not ("." or "?.")) continue;
            // Walk back over "a.b.c".
            var s = i - 1;
            while (s - 2 >= from && toks[s - 1].Text is "." or "?." && toks[s - 2].Kind == TokKind.Ident) s -= 2;
            if (s == i - 1) continue;
            var receiver = Join(toks, s, i - 2, 60);
            yield return new CodeStatement(CodeStatementKind.Call, toks[i].Line) { Receiver = receiver, Method = toks[i - 1].Text };
        }
    }
}

/// <summary>
/// Statements of a TypeScript or Java method body from its tokens: blocks, if / else, for / for-of / for-in / for-each,
/// while, do-while, switch, try / catch / finally, return, throw, break, continue, Java <c>synchronized</c>, and
/// expression statements (calls, assignments, declarations, <c>new</c>). Nested function and class declarations are
/// skipped; arrow functions stay part of their statement. It never throws: anything it does not understand becomes an
/// "Other" statement.
/// </summary>
internal sealed class CLikeBodyParser(List<Tok> toks, bool typeScript)
{
    private int _budget = CodeBodies.MaxStatements * 2;

    public RawBody Parse(int from, int to)
    {
        var body = new RawBody();
        try { Block(from, to, body.Statements, body); }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidOperationException) { }
        return body;
    }

    private int Matching(int open, int end) => Math.Min(TypeExprs.Matching(toks, open, end), end);

    private void Block(int i, int end, List<CodeStatement> list, RawBody body)
    {
        while (i < end && _budget > 0)
        {
            var next = Statement(i, end, list, body);
            i = next <= i ? i + 1 : next;
        }
    }

    private List<CodeStatement> Sub(int i, int end, RawBody body, out int next)
    {
        var list = new List<CodeStatement>();
        if (i < end && toks[i].Is("{"))
        {
            var c = Matching(i, end);
            Block(i + 1, c, list, body);
            next = c + 1;
        }
        else next = i < end ? Statement(i, end, list, body) : end;
        return list;
    }

    /// <summary>End of a simple statement: ';' at depth 0, a '}' closing the block, or (TypeScript, no semicolon) a new line
    /// after a token that can end an expression.</summary>
    private int StatementEnd(int i, int end)
    {
        var depth = 0;
        var startLine = toks[i].Line;
        for (var k = i; k < end; k++)
        {
            var t = toks[k];
            if (t.Kind == TokKind.Punct && t.Text is "(" or "[" or "{") depth++;
            else if (t.Kind == TokKind.Punct && t.Text is ")" or "]" or "}")
            {
                if (depth == 0) return k;
                depth--;
            }
            else if (depth == 0 && t.Is(";")) return k;
            else if (typeScript && depth == 0 && k > i && t.Line > toks[k - 1].Line
                     && (toks[k - 1].Kind is TokKind.Ident or TokKind.Number or TokKind.String || toks[k - 1].Text is ")" or "]" or "}")
                     && !(t.Kind == TokKind.Punct && t.Text is "." or "?." or "+" or "-" or "*" or "/" or "&&" or "||" or "?" or ":" or "=" or ")"))
                return k;
            _ = startLine;
        }
        return end;
    }

    private int Statement(int i, int end, List<CodeStatement> list, RawBody body)
    {
        var t = toks[i];
        var line = t.Line;
        if (t.Is(";")) return i + 1;
        if (t.Is("{")) { var c = Matching(i, end); Block(i + 1, c, list, body); return c + 1; }
        _budget--;
        if (t.Kind == TokKind.Ident)
        {
            switch (t.Text)
            {
                case "if" when i + 1 < end && toks[i + 1].Is("("):
                {
                    var c = Matching(i + 1, end);
                    var cond = BodyText.Join(toks, i + 2, c);
                    var then = Sub(c + 1, end, body, out var n);
                    var els = new List<CodeStatement>();
                    if (n < end && toks[n].Is("else")) els = Sub(n + 1, end, body, out n);
                    list.Add(new CodeStatement(CodeStatementKind.If, line) { Text = cond, Body = then, Else = els, Calls = BodyText.InnerCalls(toks, i + 2, c).ToList() });
                    return n;
                }
                case "for" when i + 1 < end && (toks[i + 1].Is("(") || toks[i + 1].Is("await")):
                {
                    var open = toks[i + 1].Is("await") ? i + 2 : i + 1;
                    var c = Matching(open, end);
                    var header = BodyText.Join(toks, open + 1, c);
                    var each = false;
                    var colon = -1;
                    var d = 0;
                    for (var k = open + 1; k < c; k++)
                    {
                        if (toks[k].Text is "(" or "[" or "{") d++;
                        else if (toks[k].Text is ")" or "]" or "}") d--;
                        else if (d == 0 && (toks[k].Is("of") || toks[k].Is("in") || !typeScript && toks[k].Is(":"))) { each = true; colon = k; break; }
                        else if (d == 0 && toks[k].Is(";")) break;
                    }
                    if (each && colon > open + 1)
                    {
                        // "const x of xs" / "T x : xs" → "x in xs"
                        header = $"{toks[colon - 1].Text} in {BodyText.Join(toks, colon + 1, c)}";
                        if (!typeScript && colon - 2 > open) body.Locals.TryAdd(toks[colon - 1].Text, toks[colon - 2].Text);
                    }
                    var inner = Sub(c + 1, end, body, out var n);
                    list.Add(new CodeStatement(CodeStatementKind.Loop, line) { Loop = each ? CodeLoopKind.ForEach : CodeLoopKind.For, Text = header, Body = inner, Calls = BodyText.InnerCalls(toks, open + 1, c).ToList() });
                    return n;
                }
                case "while" when i + 1 < end && toks[i + 1].Is("("):
                {
                    var c = Matching(i + 1, end);
                    var inner = Sub(c + 1, end, body, out var n);
                    list.Add(new CodeStatement(CodeStatementKind.Loop, line) { Loop = CodeLoopKind.While, Text = BodyText.Join(toks, i + 2, c), Body = inner, Calls = BodyText.InnerCalls(toks, i + 2, c).ToList() });
                    return n;
                }
                case "do":
                {
                    var inner = Sub(i + 1, end, body, out var n);
                    var cond = "";
                    if (n < end && toks[n].Is("while") && n + 1 < end && toks[n + 1].Is("("))
                    {
                        var c = Matching(n + 1, end);
                        cond = BodyText.Join(toks, n + 2, c);
                        n = c + 1;
                        if (n < end && toks[n].Is(";")) n++;
                    }
                    list.Add(new CodeStatement(CodeStatementKind.Loop, line) { Loop = CodeLoopKind.Do, Text = cond, Body = inner });
                    return n;
                }
                case "switch" when i + 1 < end && toks[i + 1].Is("("):
                {
                    var c = Matching(i + 1, end);
                    var subject = BodyText.Join(toks, i + 2, c);
                    var n = c + 1;
                    var branches = new List<CodeBranch>();
                    if (n < end && toks[n].Is("{"))
                    {
                        var close = Matching(n, end);
                        var k = n + 1;
                        List<string> labels = [];
                        var bodyList = new List<CodeStatement>();
                        var bline = line;
                        void Flush()
                        {
                            if (labels.Count == 0) return;
                            if (bodyList.Count > 0 && bodyList[^1] is { Kind: CodeStatementKind.Other, Text: "break" }) bodyList.RemoveAt(bodyList.Count - 1);
                            branches.Add(new CodeBranch(CodeBodies.Clip(string.Join(", ", labels)), bline) { Body = bodyList });
                            labels = [];
                            bodyList = [];
                        }
                        while (k < close)
                        {
                            if (toks[k].Is("case") || toks[k].Is("default"))
                            {
                                if (bodyList.Count > 0) Flush();
                                var colon = k + 1;
                                var dd = 0;
                                while (colon < close && !(dd == 0 && (toks[colon].Is(":") || toks[colon].Is("->")))) { if (toks[colon].Text is "(" or "[" or "{") dd++; else if (toks[colon].Text is ")" or "]" or "}") dd--; colon++; }
                                if (labels.Count == 0) bline = toks[k].Line;
                                labels.Add(toks[k].Is("default") ? "default" : BodyText.Join(toks, k + 1, colon));
                                k = colon + 1;
                                continue;
                            }
                            var nx = Statement(k, close, bodyList, body);
                            k = nx <= k ? k + 1 : nx;
                        }
                        Flush();
                        n = close + 1;
                    }
                    list.Add(new CodeStatement(CodeStatementKind.Switch, line) { Text = subject, Branches = branches, Calls = BodyText.InnerCalls(toks, i + 2, c).ToList() });
                    return n;
                }
                case "try":
                {
                    var n = i + 1;
                    if (n < end && toks[n].Is("(")) n = Matching(n, end) + 1;   // Java try-with-resources
                    var tryBody = Sub(n, end, body, out n);
                    var catches = new List<CodeBranch>();
                    var fin = new List<CodeStatement>();
                    while (n < end && toks[n].Is("catch"))
                    {
                        var cline = toks[n].Line;
                        var label = "";
                        n++;
                        if (n < end && toks[n].Is("("))
                        {
                            var c = Matching(n, end);
                            if (c - n > 2) label = BodyText.Join(toks, n + 1, c - 1);   // the type(s), without the variable
                            if (typeScript) label = "";
                            if (c - n >= 2 && toks[c - 1].Is(":")) label = "";
                            n = c + 1;
                        }
                        catches.Add(new CodeBranch(label, cline) { Body = Sub(n, end, body, out n) });
                    }
                    if (n < end && toks[n].Is("finally")) fin = Sub(n + 1, end, body, out n);
                    list.Add(new CodeStatement(CodeStatementKind.Try, line) { Body = tryBody, Branches = catches, Finally = fin });
                    return n;
                }
                case "synchronized" when !typeScript && i + 1 < end && toks[i + 1].Is("("):
                {
                    var c = Matching(i + 1, end);
                    var inner = Sub(c + 1, end, body, out var n);
                    list.Add(new CodeStatement(CodeStatementKind.Lock, line) { Text = BodyText.Join(toks, i + 2, c), Body = inner });
                    return n;
                }
                case "return":
                case "throw":
                case "yield" when typeScript:
                {
                    var e = StatementEnd(i + 1, end);
                    var kind = t.Text == "return" ? CodeStatementKind.Return : t.Text == "throw" ? CodeStatementKind.Throw : CodeStatementKind.Yield;
                    list.Add(Valued(kind, i + 1, e, line, body));
                    return Semi(e, end);
                }
                case "break" or "continue":
                {
                    var e = StatementEnd(i, end);
                    list.Add(new CodeStatement(CodeStatementKind.Other, line) { Text = t.Text });
                    return Semi(e, end);
                }
                case "function" or "class" or "interface" or "enum" or "record" when i + 1 < end && toks[i + 1].Kind == TokKind.Ident:
                {
                    // A nested declaration: skipped up to its body's end.
                    var k = i;
                    while (k < end && !toks[k].Is("{")) k++;
                    return k < end ? Matching(k, end) + 1 : end;
                }
            }
        }
        var stop = StatementEnd(i, end);
        if (stop == i) return i + 1;
        list.Add(Expression(i, stop, line, body));
        return Semi(stop, end);
    }

    private int Semi(int e, int end) => e < end && toks[e].Is(";") ? e + 1 : e;

    private CodeStatement Valued(CodeStatementKind kind, int from, int to, int line, RawBody body)
    {
        var st = new CodeStatement(kind, line) { Text = BodyText.Join(toks, from, to) };
        if (from >= to) return st;
        var await = toks[from].Is("await");
        var f = await ? from + 1 : from;
        if (kind == CodeStatementKind.Throw && f < to && toks[f].Is("new") && f + 1 < to)
            return st with { Method = toks[f + 1].Text, Calls = BodyText.InnerCalls(toks, f, to).ToList() };
        if (f < to && toks[f].Is("new") && f + 1 < to && toks[f + 1].Kind == TokKind.Ident && kind is CodeStatementKind.Create)
            return st with { Method = toks[f + 1].Text, Receiver = toks[f + 1].Text, Calls = BodyText.InnerCalls(toks, f, to).ToList() };
        if (BodyText.TrailingCall(toks, f, to) is { } call && !toks[f].Is("new"))
            return st with
            {
                Receiver = call.Receiver, Method = call.Method, IsAwait = await, ArgumentCount = call.ArgCount,
                Arguments = BodyText.Join(toks, call.ArgFrom, call.ArgTo), Calls = BodyText.InnerCalls(toks, f, to, call.ArgFrom - 1).ToList(),
            };
        return st with { Calls = BodyText.InnerCalls(toks, f, to).ToList() };
    }

    private CodeStatement Expression(int from, int to, int line, RawBody body)
    {
        var f = from;
        // Declarations: TypeScript const / let / var; Java "var x" or "Type x" (Type maybe generic or an array).
        string? declared = null;
        string? declType = null;
        if (typeScript && toks[f].Text is "const" or "let" or "var" && f + 1 < to && toks[f + 1].Kind == TokKind.Ident)
        {
            declared = toks[f + 1].Text;
            f += 2;
            if (f < to && toks[f].Is(":") && f + 1 < to && toks[f + 1].Kind == TokKind.Ident) declType = toks[f + 1].Text;
        }
        else if (!typeScript && toks[f].Kind == TokKind.Ident)
        {
            var k = f;
            if (toks[k].Is("final")) k++;
            if (k < to && toks[k].Kind == TokKind.Ident)
            {
                var typeName = toks[k].Text;
                var p = k + 1;
                if (p < to && toks[p].Is("<")) p = Math.Min(TypeExprs.Matching(toks, p, to) + 1, to);
                while (p + 1 < to && toks[p].Is("[") && toks[p + 1].Is("]")) p += 2;
                if (p < to && toks[p].Kind == TokKind.Ident && (p + 1 >= to || toks[p + 1].Is("=")))
                {
                    declared = toks[p].Text;
                    if (typeName != "var") declType = typeName;
                    f = p + 1;
                }
            }
        }
        if (declared != null)
        {
            if (f >= to || !toks[f].Is("="))
            {
                if (declType != null) body.Locals.TryAdd(declared, declType);
                return new CodeStatement(CodeStatementKind.Other, line) { Text = BodyText.Join(toks, from, to) };
            }
            var eq = f;
            while (eq < to && !toks[eq].Is("=")) eq++;
            if (eq + 2 < to && toks[eq + 1].Is("new") && toks[eq + 2].Kind == TokKind.Ident) declType ??= toks[eq + 2].Text;
            if (declType != null) body.Locals.TryAdd(declared, declType);
            return Valued(CodeStatementKind.Assign, eq + 1, to, line, body) with { Text = declared };
        }
        // Assignment: a top-level '=' (also "+=" as '+' '='), not "==", "=>", "<=", ">=", "!=".
        var depth = 0;
        for (var k = from; k < to; k++)
        {
            var t = toks[k];
            if (t.Kind == TokKind.Punct && t.Text is "(" or "[" or "{") depth++;
            else if (t.Kind == TokKind.Punct && t.Text is ")" or "]" or "}") depth--;
            else if (depth == 0 && t.Is("=") && !(k + 1 < to && toks[k + 1].Text is "=" or ">") && !(k > from && toks[k - 1].Text is "=" or "!" or "<" or ">"))
            {
                var targetEnd = k > from && toks[k - 1].Kind == TokKind.Punct && toks[k - 1].Text is "+" or "-" or "*" or "/" or "%" or "|" or "&" or "?" ? k - 1 : k;
                return Valued(CodeStatementKind.Assign, k + 1, to, line, body) with { Text = BodyText.Join(toks, from, targetEnd) };
            }
        }
        if (to - from >= 2 && (toks[to - 1].Is("+") && toks[to - 2].Is("+") || toks[to - 1].Is("-") && toks[to - 2].Is("-") || toks[from].Text is "+" or "-" && toks[from + 1].Text is "+" or "-"))
            return new CodeStatement(CodeStatementKind.Assign, line) { Text = BodyText.Join(toks, from, to) };
        var g = toks[from].Is("await") ? from + 1 : from;
        if (g < to && toks[g].Is("new")) return Valued(CodeStatementKind.Create, from, to, line, body);
        var st = Valued(CodeStatementKind.Call, from, to, line, body);
        return st.Method != null ? st : st with { Kind = CodeStatementKind.Other };
    }
}
