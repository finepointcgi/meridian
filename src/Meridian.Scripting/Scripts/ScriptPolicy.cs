using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Meridian.Scripting;

/// <summary>
/// Best-effort guard rails for console scripts. THIS IS NOT A SANDBOX: scripts run as ordinary .NET code inside the
/// server process with its full permissions. The policy only makes the obvious footguns (file system, processes,
/// network, reflection, environment) fail to compile so that scripts stay model scripts by default.
/// </summary>
public static class ScriptPolicy
{
    /// <summary>Namespaces whose types and members scripts may not use.</summary>
    public static readonly IReadOnlyList<string> DeniedNamespaces =
    [
        "System.IO", "System.Diagnostics", "System.Net", "System.Reflection", "System.Runtime.InteropServices",
        "System.Runtime.Loader", "System.Runtime.CompilerServices", "System.Threading", "System.Security", "System.Resources",
        "Microsoft.Win32", "Microsoft.CodeAnalysis", "Meridian.Core", "Meridian.Data", "Meridian.Web",
    ];

    /// <summary>Individual types that are allowed although their namespace is denied.</summary>
    public static readonly IReadOnlySet<string> AllowedTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.IO.StringWriter", "System.IO.StringReader", "System.IO.TextWriter", "System.IO.TextReader", "System.IO.MemoryStream",
        "System.Diagnostics.Stopwatch", "System.Threading.CancellationToken", "System.Threading.Tasks.Task", "System.Threading.Tasks.Task`1",
        "System.Runtime.CompilerServices.TaskAwaiter", "System.Runtime.CompilerServices.TaskAwaiter`1",
    };

    /// <summary>Individual types in otherwise allowed namespaces that are denied.</summary>
    public static readonly IReadOnlySet<string> DeniedTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Environment", "System.AppDomain", "System.Activator", "System.GC", "System.Console", "System.Type",
        "System.RuntimeTypeHandle", "System.RuntimeMethodHandle", "System.RuntimeFieldHandle", "System.Delegate", "System.MulticastDelegate",
        "Meridian.Scripting.ModelApi", "Meridian.Scripting.IWorkbenchBridge", "Meridian.Scripting.HeadlessBridge",
        "Meridian.Scripting.PluginManager", "Meridian.Scripting.ScriptEngine",
    };

    /// <summary>Syntax-level checks: no #r / #load, no goto, no unsafe, no dynamic, no external aliases.</summary>
    public static IEnumerable<Diagnostic> CheckSyntax(SyntaxTree tree)
    {
        var root = tree.GetRoot();
        foreach (var d in root.DescendantTrivia(descendIntoTrivia: true))
            if (d.IsKind(SyntaxKind.ReferenceDirectiveTrivia) || d.IsKind(SyntaxKind.LoadDirectiveTrivia))
                yield return Error("MS001", "#r and #load are not allowed in Meridian scripts", d.GetLocation());
        foreach (var n in root.DescendantNodes())
        {
            switch (n)
            {
                case GotoStatementSyntax g:
                    yield return Error("MS002", "goto is not allowed (loops are made cancellable instead)", g.GetLocation());
                    break;
                case UnsafeStatementSyntax or FixedStatementSyntax or PointerTypeSyntax or FunctionPointerTypeSyntax:
                    yield return Error("MS003", "unsafe code is not allowed", n.GetLocation());
                    break;
                case ExternAliasDirectiveSyntax:
                    yield return Error("MS004", "extern alias is not allowed", n.GetLocation());
                    break;
            }
        }
    }

    /// <summary>Semantic checks: every symbol and expression type must come from an allowed namespace / type.</summary>
    public static IEnumerable<Diagnostic> CheckSemantics(Compilation compilation)
    {
        var reported = new HashSet<(int, string)>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                if (node is not (ExpressionSyntax or TypeSyntax)) continue;
                var symbol = model.GetSymbolInfo(node).Symbol;
                var type = model.GetTypeInfo(node).Type;
                foreach (var problem in new[] { Problem(symbol), Problem(type) })
                {
                    if (problem is null) continue;
                    var line = node.GetLocation().GetLineSpan().StartLinePosition.Line;
                    if (reported.Add((line, problem)))
                        yield return Error("MS010", $"'{problem}' is not available to Meridian scripts (blocked by the script policy)", node.GetLocation());
                }
                if (type is IDynamicTypeSymbol)
                    yield return Error("MS011", "dynamic is not allowed", node.GetLocation());
            }
        }
    }

    private static string? Problem(ISymbol? symbol)
    {
        if (symbol is null) return null;
        if (symbol is IArrayTypeSymbol a) return Problem(a.ElementType);
        if (symbol is INamedTypeSymbol { IsGenericType: true } g && !ReferenceEquals(g, g.OriginalDefinition))
        {
            foreach (var t in g.TypeArguments)
                if (Problem(t) is { } p) return p;
            return Problem(g.OriginalDefinition);
        }
        var typeSymbol = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        // Walk out of nested types to the outermost one.
        while (typeSymbol?.ContainingType is { } outer) typeSymbol = outer;
        // Namespaces themselves are fine ("System.IO.StringWriter" names one); the types and members used are what count.
        if (typeSymbol is null) return null;
        var ns2 = typeSymbol.ContainingNamespace?.ToDisplayString() ?? "";
        var full = string.IsNullOrEmpty(ns2) || typeSymbol.ContainingNamespace!.IsGlobalNamespace ? typeSymbol.MetadataName : ns2 + "." + typeSymbol.MetadataName;
        return IsDenied(ns2, full) ? full : null;
    }

    private static bool IsDenied(string ns, string? fullType)
    {
        if (fullType != null && AllowedTypes.Contains(fullType)) return false;
        if (fullType != null && DeniedTypes.Contains(fullType)) return true;
        return DeniedNamespaces.Any(d => ns == d || ns.StartsWith(d + ".", StringComparison.Ordinal));
    }

    private static Diagnostic Error(string id, string message, Location location) =>
        Diagnostic.Create(new DiagnosticDescriptor(id, "Script policy", message, "Meridian", DiagnosticSeverity.Error, true), location);
}

/// <summary>
/// Inserts <c>ThrowIfCancelled();</c> at the top of every loop body without adding line breaks, so loops honour
/// Cancel / the time limit and reported line numbers stay unchanged.
/// </summary>
internal sealed class CancellationInjector : CSharpSyntaxRewriter
{
    private static StatementSyntax Check() =>
        SyntaxFactory.ParseStatement("ThrowIfCancelled();").WithoutTrivia().WithTrailingTrivia(SyntaxFactory.Space);

    private static StatementSyntax Inject(StatementSyntax body)
    {
        if (body is BlockSyntax b) return b.WithStatements(b.Statements.Insert(0, Check()));
        // "while (x) y();" becomes "while (x) { ThrowIfCancelled(); y(); }" on the same lines.
        var inner = body.WithLeadingTrivia().WithTrailingTrivia();
        return SyntaxFactory.Block(Check(), inner)
            .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken).WithTrailingTrivia(SyntaxFactory.Space))
            .WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken).WithLeadingTrivia(SyntaxFactory.Space))
            .WithLeadingTrivia(body.GetLeadingTrivia())
            .WithTrailingTrivia(body.GetTrailingTrivia());
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        var n = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
        return n.WithStatement(Inject(n.Statement));
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
        var n = (DoStatementSyntax)base.VisitDoStatement(node)!;
        return n.WithStatement(Inject(n.Statement));
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        var n = (ForStatementSyntax)base.VisitForStatement(node)!;
        return n.WithStatement(Inject(n.Statement));
    }

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        var n = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
        return n.WithStatement(Inject(n.Statement));
    }

    public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
    {
        var n = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!;
        return n.WithStatement(Inject(n.Statement));
    }
}
