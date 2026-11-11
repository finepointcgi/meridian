using Meridian.Core.CodeSync;

namespace Meridian.Serialization.CodeSync.Parsing;

/// <summary>
/// Statements of a Python method body from its logical lines (indentation and tokens): if / elif / else, for, while,
/// try / except / else / finally, with, match / case, return, raise, yield, break, continue, assignments (also
/// annotated and augmented ones) and calls. Nested def and class are skipped; lambdas and comprehensions stay part of
/// their statement. Single-line bodies after a colon are read too. It never throws.
/// </summary>
internal sealed class PythonBodyParser(IReadOnlyList<(int Indent, List<Tok> Toks)> lines)
{
    private int _budget = CodeBodies.MaxStatements * 2;

    public RawBody Parse(int from, int to, List<Tok>? inline = null, int inlineFrom = 0)
    {
        var body = new RawBody();
        try
        {
            if (inline != null && inlineFrom < inline.Count) Simple(inline, inlineFrom, inline.Count, body.Statements, body);
            Block(from, to, body.Statements, body);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidOperationException) { }
        return body;
    }

    private int End(int i, int to)
    {
        var e = i + 1;
        while (e < to && lines[e].Indent > lines[i].Indent) e++;
        return e;
    }

    private void Block(int i, int to, List<CodeStatement> list, RawBody body)
    {
        while (i < to && _budget > 0) i = Statement(i, to, list, body);
    }

    /// <summary>The colon that ends a header line (at depth 0), or -1.</summary>
    private static int Colon(List<Tok> toks, int from)
    {
        var d = 0;
        for (var k = from; k < toks.Count; k++)
        {
            var t = toks[k];
            if (t.Kind == TokKind.Punct && t.Text is "(" or "[" or "{") d++;
            else if (t.Kind == TokKind.Punct && t.Text is ")" or "]" or "}") d--;
            else if (d == 0 && t.Is(":") && !(k > from && toks[k - 1].Is("lambda"))) return k;
            else if (t.Is("lambda")) return -2;   // a lambda's colon is not the header's
        }
        return -1;
    }

    /// <summary>The body of the header at line <paramref name="i"/>: the rest of its line after the colon, else the
    /// indented lines below it. Returns the line after the body.</summary>
    private List<CodeStatement> Suite(int i, int to, int colon, RawBody body, out int next)
    {
        var list = new List<CodeStatement>();
        var toks = lines[i].Toks;
        next = End(i, to);
        if (colon >= 0 && colon + 1 < toks.Count) Simple(toks, colon + 1, toks.Count, list, body);
        Block(i + 1, next, list, body);
        return list;
    }

    private int Statement(int i, int to, List<CodeStatement> list, RawBody body)
    {
        _budget--;
        var toks = lines[i].Toks;
        if (toks.Count == 0) return i + 1;
        var k = toks[0].Is("async") ? 1 : 0;
        var head = toks[k];
        var line = head.Line;
        var colon = Colon(toks, k);
        switch (head.Text)
        {
            case "def" or "class" when head.Kind == TokKind.Ident:
                return End(i, to);
            case "@":
                return i + 1;
            case "if" when colon > 0:
            {
                var cond = BodyText.Join(toks, k + 1, colon);
                var then = Suite(i, to, colon, body, out var n);
                var els = new List<CodeStatement>();
                if (n < to && lines[n].Indent == lines[i].Indent && lines[n].Toks.Count > 0)
                {
                    var nt = lines[n].Toks;
                    if (nt[0].Is("elif"))
                    {
                        // "elif c:" is an if in the else branch.
                        var copy = new List<Tok>(nt) { [0] = nt[0] with { Text = "if" } };
                        var shim = new PythonBodyParser(Replace(n, copy)) { _budget = _budget };
                        var nested = new List<CodeStatement>();
                        n = shim.Statement(n, to, nested, body);
                        _budget = shim._budget;
                        els = nested;
                    }
                    else if (nt[0].Is("else") && Colon(nt, 0) is var ec && ec > 0)
                        els = Suite(n, to, ec, body, out n);
                }
                list.Add(new CodeStatement(CodeStatementKind.If, line) { Text = cond, Body = then, Else = els, Calls = BodyText.InnerCalls(toks, k + 1, colon).ToList() });
                return n;
            }
            case "for" when colon > 0:
            {
                var inAt = toks.FindIndex(k, t => t.Is("in"));
                var header = inAt > 0 && inAt < colon ? $"{BodyText.Join(toks, k + 1, inAt)} in {BodyText.Join(toks, inAt + 1, colon)}" : BodyText.Join(toks, k + 1, colon);
                var inner = Suite(i, to, colon, body, out var n);
                if (n < to && lines[n].Indent == lines[i].Indent && lines[n].Toks.Count > 0 && lines[n].Toks[0].Is("else") && Colon(lines[n].Toks, 0) > 0)
                    n = End(n, to);   // for … else: the else runs after the loop; left out
                list.Add(new CodeStatement(CodeStatementKind.Loop, line) { Loop = CodeLoopKind.ForEach, Text = header, Body = inner, Calls = BodyText.InnerCalls(toks, k + 1, colon).ToList() });
                return n;
            }
            case "while" when colon > 0:
            {
                var inner = Suite(i, to, colon, body, out var n);
                list.Add(new CodeStatement(CodeStatementKind.Loop, line) { Loop = CodeLoopKind.While, Text = BodyText.Join(toks, k + 1, colon), Body = inner, Calls = BodyText.InnerCalls(toks, k + 1, colon).ToList() });
                return n;
            }
            case "with" when colon > 0:
            {
                var asAt = toks.FindIndex(k, t => t.Is("as"));
                if (asAt > 0 && asAt + 1 < colon && toks[asAt + 1].Kind == TokKind.Ident && BodyText.TrailingCall(toks, k + 1, asAt) is { Receiver: null } c)
                    body.Locals.TryAdd(toks[asAt + 1].Text, c.Method);
                var inner = Suite(i, to, colon, body, out var n);
                list.Add(new CodeStatement(CodeStatementKind.Using, line) { Text = BodyText.Join(toks, k + 1, colon), Body = inner, Calls = BodyText.InnerCalls(toks, k + 1, colon).ToList() });
                return n;
            }
            case "try" when colon > 0:
            {
                var tryBody = Suite(i, to, colon, body, out var n);
                var catches = new List<CodeBranch>();
                var fin = new List<CodeStatement>();
                while (n < to && lines[n].Indent == lines[i].Indent && lines[n].Toks.Count > 0)
                {
                    var nt = lines[n].Toks;
                    var c = Colon(nt, 0);
                    if (c < 0) break;
                    if (nt[0].Is("except"))
                    {
                        var asAt = nt.FindIndex(t => t.Is("as"));
                        var typeEnd = asAt > 0 && asAt < c ? asAt : c;
                        var label = BodyText.Join(nt, nt.Count > 1 && nt[1].Is("*") ? 2 : 1, typeEnd).Trim('(', ')');
                        catches.Add(new CodeBranch(label, nt[0].Line) { Body = Suite(n, to, c, body, out n) });
                    }
                    else if (nt[0].Is("else")) tryBody.AddRange(Suite(n, to, c, body, out n));
                    else if (nt[0].Is("finally")) fin = Suite(n, to, c, body, out n);
                    else break;
                }
                list.Add(new CodeStatement(CodeStatementKind.Try, line) { Body = tryBody, Branches = catches, Finally = fin });
                return n;
            }
            case "match" when colon > 0 && k + 1 < colon:
            {
                var subject = BodyText.Join(toks, k + 1, colon);
                var n = End(i, to);
                var branches = new List<CodeBranch>();
                for (var c = i + 1; c < n;)
                {
                    var ct = lines[c].Toks;
                    var cc = Colon(ct, 0);
                    if (ct.Count > 0 && ct[0].Is("case") && cc > 0)
                    {
                        var label = BodyText.Join(ct, 1, cc);
                        branches.Add(new CodeBranch(label == "_" ? "default" : label, ct[0].Line) { Body = Suite(c, n, cc, body, out c) });
                    }
                    else c = End(c, n);
                }
                list.Add(new CodeStatement(CodeStatementKind.Switch, line) { Text = subject, Branches = branches });
                return n;
            }
        }
        if (colon > 0 && head.Text is "elif" or "else" or "except" or "finally")
            return End(i, to);   // an orphan clause (its statement was cut): skipped
        Simple(toks, k, toks.Count, list, body);
        return End(i, to);
    }

    private List<(int, List<Tok>)> Replace(int n, List<Tok> toks)
    {
        var copy = lines.ToList();
        copy[n] = (copy[n].Indent, toks);
        return copy;
    }

    /// <summary>Simple statements of one line ("a = 1; b()").</summary>
    private void Simple(List<Tok> toks, int from, int to, List<CodeStatement> list, RawBody body)
    {
        var start = from;
        var d = 0;
        for (var k = from; k <= to; k++)
        {
            if (k < to)
            {
                var t = toks[k];
                if (t.Kind == TokKind.Punct && t.Text is "(" or "[" or "{") d++;
                else if (t.Kind == TokKind.Punct && t.Text is ")" or "]" or "}") d--;
                if (!(d == 0 && t.Is(";"))) continue;
            }
            if (k > start && One(toks, start, k, body) is { } st) list.Add(st);
            start = k + 1;
        }
    }

    private static CodeStatement? One(List<Tok> toks, int from, int to, RawBody body)
    {
        var t = toks[from];
        var line = t.Line;
        switch (t.Text)
        {
            case "pass" or "global" or "nonlocal" or "assert" or "del" or "import" or "from" when t.Kind == TokKind.Ident:
                return t.Text == "pass" ? null : new CodeStatement(CodeStatementKind.Other, line) { Text = BodyText.Join(toks, from, to) };
            case "return":
                return Valued(CodeStatementKind.Return, toks, from + 1, to, line);
            case "raise":
            {
                var st = new CodeStatement(CodeStatementKind.Throw, line) { Text = BodyText.Join(toks, from + 1, to) };
                return from + 1 < to && toks[from + 1].Kind == TokKind.Ident ? st with { Method = toks[from + 1].Text, Calls = BodyText.InnerCalls(toks, from + 1, to).ToList() } : st;
            }
            case "yield":
                return from + 1 < to && toks[from + 1].Is("from")
                    ? Valued(CodeStatementKind.Yield, toks, from + 2, to, line)
                    : Valued(CodeStatementKind.Yield, toks, from + 1, to, line);
            case "break" or "continue":
                return new CodeStatement(CodeStatementKind.Other, line) { Text = t.Text };
        }
        // Assignment: a top-level '=' (augmented "+=" is '+' '='), with an optional annotation "x: T = …".
        var d = 0;
        for (var k = from; k < to; k++)
        {
            var x = toks[k];
            if (x.Kind == TokKind.Punct && x.Text is "(" or "[" or "{") d++;
            else if (x.Kind == TokKind.Punct && x.Text is ")" or "]" or "}") d--;
            else if (d == 0 && x.Is("=") && !(k + 1 < to && toks[k + 1].Is("=")) && !(k > from && toks[k - 1].Text is "=" or "!" or "<" or ">"))
            {
                var targetEnd = k > from && toks[k - 1].Kind == TokKind.Punct && toks[k - 1].Text is "+" or "-" or "*" or "/" or "%" or "|" or "&" or "@" or "**" ? k - 1 : k;
                var colon = -1;
                for (var c = from; c < targetEnd; c++) if (toks[c].Is(":")) { colon = c; break; }
                var target = BodyText.Join(toks, from, colon > 0 ? colon : targetEnd);
                if (from + 1 == (colon > 0 ? colon : targetEnd) && toks[from].Kind == TokKind.Ident)
                {
                    if (colon > 0 && colon + 1 < targetEnd && toks[colon + 1].Kind == TokKind.Ident) body.Locals[toks[from].Text] = toks[colon + 1].Text;
                    else if (BodyText.TrailingCall(toks, k + 1, to) is { Receiver: null } c && c.Method.Length > 0 && char.IsUpper(c.Method[0]))
                        body.Locals.TryAdd(toks[from].Text, c.Method);
                }
                return Valued(CodeStatementKind.Assign, toks, k + 1, to, line) with { Text = target };
            }
        }
        if (to - from >= 3 && toks[from].Kind == TokKind.Ident && toks[from + 1].Is(":") && toks[from + 2].Kind == TokKind.Ident)
        {
            body.Locals[toks[from].Text] = toks[from + 2].Text;   // "x: T" declares a local
            return null;
        }
        var st2 = Valued(CodeStatementKind.Call, toks, from, to, line);
        return st2.Method != null ? st2 : st2 with { Kind = CodeStatementKind.Other };
    }

    private static CodeStatement Valued(CodeStatementKind kind, List<Tok> toks, int from, int to, int line)
    {
        var st = new CodeStatement(kind, line) { Text = BodyText.Join(toks, from, to) };
        if (from >= to) return st;
        var await = toks[from].Is("await");
        var f = await ? from + 1 : from;
        if (BodyText.TrailingCall(toks, f, to) is { } call)
            return st with
            {
                Receiver = call.Receiver, Method = call.Method, IsAwait = await, ArgumentCount = call.ArgCount,
                Arguments = BodyText.Join(toks, call.ArgFrom, call.ArgTo), Calls = BodyText.InnerCalls(toks, f, to, call.ArgFrom - 1).ToList(),
            };
        return st with { Calls = BodyText.InnerCalls(toks, f, to).ToList() };
    }
}
