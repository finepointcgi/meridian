using Meridian.Core.CodeSync;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Meridian.Serialization.CodeSync;

/// <summary>
/// Reads a C# method body into a <see cref="CodeBody"/> with Roslyn's semantic model. Calls resolve to the method Roslyn
/// binds (this calls, static calls, calls on fields, properties, parameters and locals of a known type; extension
/// methods by their declaring class); when the declaring type is scanned the call carries its type symbol and the exact
/// operation symbol. Lambdas (LINQ) stay part of their statement: only their calls of scanned types are listed in
/// <see cref="CodeStatement.Calls"/>. Local function declarations are skipped (their calls show where they are made).
/// </summary>
internal sealed class CSharpBodyReader(SemanticModel model, Func<ITypeSymbol?, string?> scanned, CancellationToken ct)
{
    /// <summary>The symbol code sync gives an operation: "Type.Method(int,string)" (CodeModelMapper.OperationSymbol).</summary>
    public static string OperationSymbol(string typeSymbol, IMethodSymbol m) =>
        $"{typeSymbol}.{m.Name}({string.Join(",", m.Parameters.Select(p => string.Concat(p.Type.ToDisplayString(CSharpCodeReader.TypeDisplay).Where(c => !char.IsWhiteSpace(c)))))})";

    public CodeBody? Read(SyntaxNode decl)
    {
        switch (decl)
        {
            case BaseMethodDeclarationSyntax { Body: { } body }:
                return CodeBodies.Capped(Block(body.Statements));
            case BaseMethodDeclarationSyntax { ExpressionBody: { } eb } m:
            {
                var returnsValue = m is MethodDeclarationSyntax md && md.ReturnType.ToString() is not ("void" or "Task" or "ValueTask");
                var list = new List<CodeStatement>();
                if (returnsValue) list.Add(Valued(CodeStatementKind.Return, eb.Expression, eb.Expression));
                else Expression(eb.Expression, eb.Expression, list);
                return CodeBodies.Capped(list);
            }
            default:
                return null;
        }
    }

    private static int LineOf(SyntaxNode n) => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private List<CodeStatement> Block(IEnumerable<StatementSyntax> statements)
    {
        var list = new List<CodeStatement>();
        foreach (var s in statements) Statement(s, list);
        return list;
    }

    private List<CodeStatement> Body(StatementSyntax? s)
    {
        var list = new List<CodeStatement>();
        if (s != null) Statement(s, list);
        return list;
    }

    private void Statement(StatementSyntax s, List<CodeStatement> list)
    {
        ct.ThrowIfCancellationRequested();
        switch (s)
        {
            case BlockSyntax b:
                foreach (var x in b.Statements) Statement(x, list);
                break;
            case CheckedStatementSyntax c:
                Statement(c.Block, list);
                break;
            case UnsafeStatementSyntax u:
                Statement(u.Block, list);
                break;
            case FixedStatementSyntax f:
                Statement(f.Statement, list);
                break;
            case LabeledStatementSyntax l:
                Statement(l.Statement, list);
                break;
            case EmptyStatementSyntax or LocalFunctionStatementSyntax:
                break;
            case ExpressionStatementSyntax es:
                Expression(es.Expression, es, list);
                break;
            case LocalDeclarationStatementSyntax ld:
            {
                var vars = ld.Declaration.Variables.Where(v => v.Initializer != null).ToList();
                if (vars.Count == 0) break;
                var target = string.Join(", ", vars.Select(v => v.Identifier.Text));
                if (ld.UsingKeyword != default(SyntaxToken) && ld.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)) target = "using " + target;
                var value = vars.Count == 1 ? vars[0].Initializer!.Value : null;
                var st = value != null ? Valued(CodeStatementKind.Assign, value, ld) : new CodeStatement(CodeStatementKind.Assign, LineOf(ld))
                {
                    Calls = Calls(ld.Declaration, null),
                };
                list.Add(st with { Text = CodeBodies.Clip(target) });
                break;
            }
            case ReturnStatementSyntax r:
                list.Add(r.Expression is { } re ? Valued(CodeStatementKind.Return, re, r) : new CodeStatement(CodeStatementKind.Return, LineOf(r)));
                break;
            case ThrowStatementSyntax t:
            {
                var created = Unwrap(t.Expression) as BaseObjectCreationExpressionSyntax;
                list.Add(new CodeStatement(CodeStatementKind.Throw, LineOf(t))
                {
                    Text = CodeBodies.Clip(t.Expression?.ToString() ?? "throw"),
                    Method = created switch { ObjectCreationExpressionSyntax oc => SimpleTypeName(oc.Type), _ when created != null => model.GetTypeInfo(created, ct).Type?.Name, _ => null },
                    Calls = t.Expression is { } te ? Calls(te, null) : [],
                });
                break;
            }
            case YieldStatementSyntax y:
                list.Add(y.Expression is { } ye && y.IsKind(SyntaxKind.YieldReturnStatement)
                    ? Valued(CodeStatementKind.Yield, ye, y)
                    : new CodeStatement(CodeStatementKind.Yield, LineOf(y)) { Text = "break" });
                break;
            case BreakStatementSyntax or ContinueStatementSyntax or GotoStatementSyntax:
                list.Add(new CodeStatement(CodeStatementKind.Other, LineOf(s)) { Text = CodeBodies.Clip(s.ToString().TrimEnd(';')) });
                break;
            case IfStatementSyntax ifs:
                list.Add(new CodeStatement(CodeStatementKind.If, LineOf(ifs))
                {
                    Text = CodeBodies.Clip(ifs.Condition.ToString()),
                    Calls = Calls(ifs.Condition, null),
                    Body = Body(ifs.Statement),
                    Else = Body(ifs.Else?.Statement),
                });
                break;
            case SwitchStatementSyntax sw:
                list.Add(new CodeStatement(CodeStatementKind.Switch, LineOf(sw))
                {
                    Text = CodeBodies.Clip(sw.Expression.ToString()),
                    Calls = Calls(sw.Expression, null),
                    Branches = sw.Sections.Select(sec => new CodeBranch(CodeBodies.Clip(string.Join(", ", sec.Labels.Select(Label))), LineOf(sec))
                    {
                        // The break that ends a section is how C# ends a case, not a step of its own.
                        Body = Block(sec.Statements is { Count: > 0 } st && st[^1] is BreakStatementSyntax ? st.Take(st.Count - 1) : sec.Statements),
                    }).ToList(),
                });
                break;
            case ForStatementSyntax f:
                list.Add(new CodeStatement(CodeStatementKind.Loop, LineOf(f))
                {
                    Loop = CodeLoopKind.For,
                    Text = CodeBodies.Clip($"{f.Declaration?.ToString() ?? string.Join(", ", f.Initializers)}; {f.Condition}; {string.Join(", ", f.Incrementors)}"),
                    Calls = Calls(f, f.Statement),
                    Body = Body(f.Statement),
                });
                break;
            case CommonForEachStatementSyntax fe:
            {
                var variable = fe switch { ForEachStatementSyntax x => x.Identifier.Text, ForEachVariableStatementSyntax v => v.Variable.ToString(), _ => "" };
                list.Add(new CodeStatement(CodeStatementKind.Loop, LineOf(fe))
                {
                    Loop = CodeLoopKind.ForEach,
                    Text = CodeBodies.Clip($"{(fe.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword) ? "await " : "")}{variable} in {fe.Expression}"),
                    Calls = Calls(fe.Expression, null),
                    Body = Body(fe.Statement),
                });
                break;
            }
            case WhileStatementSyntax w:
                list.Add(new CodeStatement(CodeStatementKind.Loop, LineOf(w))
                {
                    Loop = CodeLoopKind.While, Text = CodeBodies.Clip(w.Condition.ToString()), Calls = Calls(w.Condition, null), Body = Body(w.Statement),
                });
                break;
            case DoStatementSyntax d:
                list.Add(new CodeStatement(CodeStatementKind.Loop, LineOf(d))
                {
                    Loop = CodeLoopKind.Do, Text = CodeBodies.Clip(d.Condition.ToString()), Calls = Calls(d.Condition, null), Body = Body(d.Statement),
                });
                break;
            case TryStatementSyntax tr:
                list.Add(new CodeStatement(CodeStatementKind.Try, LineOf(tr))
                {
                    Body = Block(tr.Block.Statements),
                    Branches = tr.Catches.Select(c => new CodeBranch(c.Declaration is { } cd ? SimpleTypeName(cd.Type) : "", LineOf(c))
                    {
                        Body = Block(c.Block.Statements),
                    }).ToList(),
                    Finally = tr.Finally is { } fin ? Block(fin.Block.Statements) : [],
                });
                break;
            case UsingStatementSyntax us:
                list.Add(new CodeStatement(CodeStatementKind.Using, LineOf(us))
                {
                    Text = CodeBodies.Clip(us.Declaration?.ToString() ?? us.Expression?.ToString() ?? ""),
                    Calls = Calls((SyntaxNode?)us.Declaration ?? us.Expression!, null),
                    Body = Body(us.Statement),
                });
                break;
            case LockStatementSyntax lk:
                list.Add(new CodeStatement(CodeStatementKind.Lock, LineOf(lk))
                {
                    Text = CodeBodies.Clip(lk.Expression.ToString()), Calls = Calls(lk.Expression, null), Body = Body(lk.Statement),
                });
                break;
            default:
                list.Add(new CodeStatement(CodeStatementKind.Other, LineOf(s)) { Text = CodeBodies.Clip(s.ToString()), Calls = Calls(s, null) });
                break;
        }
    }

    private static string Label(SwitchLabelSyntax l) => l switch
    {
        CaseSwitchLabelSyntax c => c.Value.ToString(),
        CasePatternSwitchLabelSyntax p => p.Pattern + (p.WhenClause is { } w ? " " + w : ""),
        DefaultSwitchLabelSyntax => "default",
        _ => l.ToString(),
    };

    private static string SimpleTypeName(TypeSyntax t) => t switch
    {
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        AliasQualifiedNameSyntax a => a.Name.Identifier.Text,
        SimpleNameSyntax s => s.Identifier.Text,
        _ => t.ToString(),
    };

    /// <summary>An expression statement: a call, a creation, an assignment, or something else.</summary>
    private void Expression(ExpressionSyntax e, SyntaxNode at, List<CodeStatement> list)
    {
        var inner = Unwrap(e);
        switch (inner)
        {
            case AssignmentExpressionSyntax a:
            {
                var v = Valued(CodeStatementKind.Assign, a.Right, at);
                var left = Calls(a.Left, null);
                list.Add(v with { Text = CodeBodies.Clip(a.Left.ToString()), Calls = left.Count == 0 ? v.Calls : [.. left, .. v.Calls] });
                break;
            }
            case InvocationExpressionSyntax:
                list.Add(Valued(CodeStatementKind.Call, e, at) with { Text = CodeBodies.Clip(e.ToString()) });
                break;
            case BaseObjectCreationExpressionSyntax oc:
                list.Add(Creation(oc, at));
                break;
            case PostfixUnaryExpressionSyntax or PrefixUnaryExpressionSyntax:
                list.Add(new CodeStatement(CodeStatementKind.Assign, LineOf(at)) { Text = CodeBodies.Clip(inner.ToString()), Calls = Calls(inner, null) });
                break;
            default:
                list.Add(new CodeStatement(CodeStatementKind.Other, LineOf(at)) { Text = CodeBodies.Clip(e.ToString()), Calls = Calls(e, null) });
                break;
        }
    }

    private CodeStatement Creation(BaseObjectCreationExpressionSyntax oc, SyntaxNode at)
    {
        var type = model.GetTypeInfo(oc, ct).Type;
        return new CodeStatement(CodeStatementKind.Create, LineOf(at))
        {
            Text = CodeBodies.Clip(oc.ToString()),
            Method = oc is ObjectCreationExpressionSyntax o ? SimpleTypeName(o.Type) : type?.Name,
            ReceiverType = scanned(type),
            Arguments = oc.ArgumentList is { } al ? CodeBodies.Clip(string.Join(", ", al.Arguments)) : null,
            ArgumentCount = oc.ArgumentList?.Arguments.Count ?? 0,
            Calls = Calls(oc, null),
        };
    }

    /// <summary>Parentheses, casts, <c>!</c> and <c>await</c> (and <c>.ConfigureAwait(…)</c>) around the value.</summary>
    private static ExpressionSyntax Unwrap(ExpressionSyntax? e)
    {
        while (true)
        {
            switch (e)
            {
                case ParenthesizedExpressionSyntax p: e = p.Expression; continue;
                case CastExpressionSyntax c: e = c.Expression; continue;
                case PostfixUnaryExpressionSyntax pu when pu.IsKind(SyntaxKind.SuppressNullableWarningExpression): e = pu.Operand; continue;
                case AwaitExpressionSyntax a: e = a.Expression; continue;
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ConfigureAwait", Expression: var inner } }:
                    e = inner; continue;
            }
            return e!;
        }
    }

    private static bool IsAwaited(ExpressionSyntax e)
    {
        while (true)
        {
            switch (e)
            {
                case AwaitExpressionSyntax: return true;
                case ParenthesizedExpressionSyntax p: e = p.Expression; continue;
                case CastExpressionSyntax c: e = c.Expression; continue;
                default: return false;
            }
        }
    }

    /// <summary>A statement of <paramref name="kind"/> whose value is <paramref name="value"/>: when the value is a call,
    /// the call fields hold it; other scanned calls go to <see cref="CodeStatement.Calls"/>.</summary>
    private CodeStatement Valued(CodeStatementKind kind, ExpressionSyntax value, SyntaxNode at)
    {
        var inner = Unwrap(value);
        var st = new CodeStatement(kind, LineOf(at)) { Text = CodeBodies.Clip(value.ToString()) };
        if (inner is InvocationExpressionSyntax inv)
        {
            st = WithCall(st, inv) with { IsAwait = IsAwaited(value) };
            return st with { Calls = Calls(inv, inv) };
        }
        if (kind == CodeStatementKind.Call) return st with { Kind = CodeStatementKind.Other, Calls = Calls(value, null) };
        return st with { Calls = Calls(value, null) };
    }

    private CodeStatement WithCall(CodeStatement st, InvocationExpressionSyntax inv)
    {
        var info = model.GetSymbolInfo(inv, ct);
        var method = (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
        string? receiver = inv.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Expression.ToString(),
            MemberBindingExpressionSyntax => "?",
            _ => null,
        };
        var name = inv.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            MemberBindingExpressionSyntax mb => mb.Name.Identifier.Text,
            SimpleNameSyntax sn => sn.Identifier.Text,
            _ => method?.Name ?? CodeBodies.Clip(inv.Expression.ToString(), 40),
        };
        if (inv.Expression is MemberBindingExpressionSyntax && inv.Parent is ConditionalAccessExpressionSyntax ca) receiver = ca.Expression.ToString();
        string? type = null, op = null;
        var isStatic = false;
        if (method != null && method.MethodKind != MethodKind.LocalFunction)
        {
            var decl = (method.ReducedFrom ?? method).OriginalDefinition;
            isStatic = method.IsStatic || method.ReducedFrom != null;
            if (scanned(decl.ContainingType) is { } t)
            {
                type = t;
                op = OperationSymbol(t, decl);
            }
            if (receiver is null && !method.IsStatic) receiver = "this";
        }
        return st with
        {
            Method = name, Receiver = receiver is null ? null : CodeBodies.Clip(receiver, 40), ReceiverType = type, Operation = op, IsStatic = isStatic,
            Arguments = CodeBodies.Clip(string.Join(", ", inv.ArgumentList.Arguments)), ArgumentCount = inv.ArgumentList.Arguments.Count,
        };
    }

    /// <summary>The calls of scanned types inside <paramref name="node"/> (lambdas included), in source order, except
    /// <paramref name="skip"/> itself (the statement's own call) and anything inside <paramref name="stop"/> (a nested body).</summary>
    private List<CodeStatement> Calls(SyntaxNode node, SyntaxNode? skip)
    {
        List<(int End, CodeStatement Call)>? list = null;
        foreach (var n in node.DescendantNodesAndSelf(x => x == node || x is not StatementSyntax || InLambda(x)))
        {
            if (n is not InvocationExpressionSyntax inv || ReferenceEquals(n, skip)) continue;
            if (skip is StatementSyntax body && !ReferenceEquals(body, node) && inv.Ancestors().Contains(body)) continue;
            var st = WithCall(new CodeStatement(CodeStatementKind.Call, LineOf(inv)), inv);
            if (st.ReceiverType is null) continue;
            (list ??= []).Add((inv.Span.End, st with { Text = CodeBodies.Clip(inv.ToString()), IsAwait = inv.Parent is AwaitExpressionSyntax }));
        }
        // Inner calls end first, so ordering by their end puts arguments before the call that uses them.
        return list is null ? [] : list.OrderBy(x => x.End).Select(x => x.Call).ToList();
    }

    private static bool InLambda(SyntaxNode x)
    {
        for (var p = x.Parent; p != null && p is not MemberDeclarationSyntax; p = p.Parent)
            if (p is AnonymousFunctionExpressionSyntax) return true;
        return false;
    }
}
