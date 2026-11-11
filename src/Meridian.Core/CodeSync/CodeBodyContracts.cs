using System.Text;

namespace Meridian.Core.CodeSync;

// Method bodies as small control-flow trees ("behaviour from code"): the readers fill CodeMember.Body, the behaviour
// mapper (CodeBehaviourMapper) turns them into activities and interactions. Everything here is additive: older stored
// code models have no bodies and read back unchanged. The enum numbers are stored: never renumber.

/// <summary>What one statement of a method body is.</summary>
public enum CodeStatementKind
{
    /// <summary>Anything else worth showing (a <c>++</c>, a <c>break</c>, a local function call that resolves to nothing).</summary>
    Other = 0,
    /// <summary>A call whose result is not kept. The call fields hold it.</summary>
    Call = 1,
    /// <summary><c>new T(…)</c> as a statement; <see cref="CodeStatement.Method"/> is the type as written,
    /// <see cref="CodeStatement.ReceiverType"/> the scanned type created.</summary>
    Create = 2,
    /// <summary>An assignment or a local with an initializer; <see cref="CodeStatement.Text"/> is the target. When the value
    /// is a call, the call fields hold it.</summary>
    Assign = 3,
    /// <summary><c>return</c>; <see cref="CodeStatement.Text"/> is the value. When the value is a call, the call fields hold it.</summary>
    Return = 4,
    /// <summary><c>throw</c>; <see cref="CodeStatement.Text"/> is the expression, <see cref="CodeStatement.Method"/> the
    /// exception type when it is created there.</summary>
    Throw = 5,
    /// <summary><c>if</c>: <see cref="CodeStatement.Text"/> is the condition, <see cref="CodeStatement.Body"/> the then
    /// block, <see cref="CodeStatement.Else"/> the else block (an else-if chain is one <c>If</c> in it).</summary>
    If = 6,
    /// <summary><c>switch</c>: <see cref="CodeStatement.Text"/> is the subject, <see cref="CodeStatement.Branches"/> the cases.</summary>
    Switch = 7,
    /// <summary>A loop: <see cref="CodeStatement.Loop"/> says which, <see cref="CodeStatement.Text"/> is its header,
    /// <see cref="CodeStatement.Body"/> its body.</summary>
    Loop = 8,
    /// <summary><c>try</c>: <see cref="CodeStatement.Body"/>, catch clauses in <see cref="CodeStatement.Branches"/> (their
    /// label is the exception type, "" for a catch-all), <see cref="CodeStatement.Finally"/>.</summary>
    Try = 9,
    /// <summary><c>using (x) { … }</c> / Python <c>with</c>: <see cref="CodeStatement.Text"/> is the resource.</summary>
    Using = 10,
    /// <summary><c>lock (x) { … }</c> / Java <c>synchronized</c>.</summary>
    Lock = 11,
    /// <summary><c>yield return x</c> / <c>yield x</c>; <c>yield break</c> has the text "break".</summary>
    Yield = 12,
}

/// <summary>Which loop a <see cref="CodeStatementKind.Loop"/> is.</summary>
public enum CodeLoopKind { For = 0, ForEach = 1, While = 2, Do = 3 }

/// <summary>
/// One statement of a method body, with its 1-based source line. Texts are short (at most
/// <see cref="CodeBodies.MaxText"/> characters, whitespace collapsed). The call fields (<see cref="Method"/>,
/// <see cref="Receiver"/>, <see cref="ReceiverType"/>, <see cref="Operation"/>, <see cref="Arguments"/>,
/// <see cref="IsAwait"/>, <see cref="IsStatic"/>) describe the statement's own call: a <see cref="CodeStatementKind.Call"/>,
/// or the call whose result an <see cref="CodeStatementKind.Assign"/>, <see cref="CodeStatementKind.Return"/> or
/// <see cref="CodeStatementKind.Yield"/> keeps. Other calls of scanned types in the statement (in conditions, arguments,
/// lambdas) are in <see cref="Calls"/>, in evaluation order.
/// </summary>
public sealed record CodeStatement(CodeStatementKind Kind, int Line)
{
    /// <summary>Condition, header, subject, assignment target, returned / thrown value or the statement itself.</summary>
    public string? Text { get; init; }
    /// <summary>The called method's name; for <see cref="CodeStatementKind.Create"/> and <see cref="CodeStatementKind.Throw"/>
    /// the type created.</summary>
    public string? Method { get; init; }
    /// <summary>The receiver as written: "this", "battery", "_runner", "Battery" (a static call); null for a plain call
    /// (<c>Charge()</c>, the method's own type or a local function).</summary>
    public string? Receiver { get; init; }
    /// <summary>The scanned type whose method is called (or that is created); null when it is not one of the scanned types.</summary>
    public string? ReceiverType { get; init; }
    /// <summary>The called operation's symbol ("Sat.Power.Battery.Charge(double)") when the reader resolved the overload (C#).
    /// The mapper otherwise matches by <see cref="ReceiverType"/>, <see cref="Method"/> and the argument count.</summary>
    public string? Operation { get; init; }
    /// <summary>The arguments as written, without the parentheses.</summary>
    public string? Arguments { get; init; }
    /// <summary>Number of arguments (to pick an overload when <see cref="Operation"/> is not known).</summary>
    public int ArgumentCount { get; init; }
    public bool IsAwait { get; init; }
    /// <summary>A call of a static method (or an extension method called as one).</summary>
    public bool IsStatic { get; init; }
    /// <summary>Loops: which loop.</summary>
    public CodeLoopKind? Loop { get; init; }
    /// <summary>If: the then block; Loop, Try, Using, Lock: the body.</summary>
    public IReadOnlyList<CodeStatement> Body { get; init; } = [];
    /// <summary>If: the else block (an else-if is one If statement here).</summary>
    public IReadOnlyList<CodeStatement> Else { get; init; } = [];
    /// <summary>Switch: the cases; Try: the catch clauses.</summary>
    public IReadOnlyList<CodeBranch> Branches { get; init; } = [];
    /// <summary>Try: the finally block.</summary>
    public IReadOnlyList<CodeStatement> Finally { get; init; } = [];
    /// <summary>Other calls of scanned types in this statement's expressions, in evaluation order.</summary>
    public IReadOnlyList<CodeStatement> Calls { get; init; } = [];

    /// <summary>The statement keeps a call (its call fields are set).</summary>
    public bool HasCall => !string.IsNullOrEmpty(Method) && Kind is CodeStatementKind.Call or CodeStatementKind.Assign
        or CodeStatementKind.Return or CodeStatementKind.Yield;

    /// <summary>"battery.Charge()" / "Battery.Create()" / "Charge()".</summary>
    public string CallLabel => string.IsNullOrEmpty(Receiver) || Receiver is "this" or "self" ? $"{Method}()" : $"{Receiver}.{Method}()";
}

/// <summary>A case of a switch ("case Mode.Safe, Mode.Off", "default") or a catch clause (the exception type, "" for any).</summary>
public sealed record CodeBranch(string Label, int Line)
{
    public IReadOnlyList<CodeStatement> Body { get; init; } = [];
}

/// <summary>A method body: its statements and how many statements the per-method cap left out ("+N more").</summary>
public sealed record CodeBody(IReadOnlyList<CodeStatement> Statements)
{
    /// <summary>Statements (counting nested ones) left out by <see cref="CodeBodies.MaxStatements"/>.</summary>
    public int Omitted { get; init; }
}

/// <summary>Limits and helpers shared by the body readers.</summary>
public static class CodeBodies
{
    /// <summary>Longest text kept on a statement (characters); longer texts end with "…".</summary>
    public const int MaxText = 80;

    /// <summary>Statements (nested ones and <see cref="CodeStatement.Calls"/> included) kept per method.</summary>
    public const int MaxStatements = 300;

    /// <summary>Statements kept per read in total (about 200 bytes each), so very large code stays bounded; bodies of the
    /// methods after that are left out with a warning.</summary>
    public const int DefaultMaxTotalStatements = 2_000_000;

    /// <summary>Whitespace collapsed, at most <paramref name="max"/> characters ("…" at the end when cut).</summary>
    public static string Clip(string? text, int max = MaxText)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, max + 1));
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
            if (sb.Length > max) break;
        }
        if (sb.Length <= max) return sb.ToString();
        return sb.ToString(0, max - 1).TrimEnd() + "…";
    }

    /// <summary>Statements in the tree, nested ones and calls included.</summary>
    public static int Count(IEnumerable<CodeStatement> statements)
    {
        var n = 0;
        foreach (var s in statements)
        {
            n++;
            n += Count(s.Body) + Count(s.Else) + Count(s.Finally) + Count(s.Calls);
            foreach (var b in s.Branches) n += Count(b.Body);
        }
        return n;
    }

    /// <summary>Every statement of the tree, depth first in source order (calls before the statement that holds them).</summary>
    public static IEnumerable<CodeStatement> Walk(IEnumerable<CodeStatement> statements)
    {
        foreach (var s in statements)
        {
            foreach (var c in Walk(s.Calls)) yield return c;
            yield return s;
            foreach (var x in Walk(s.Body)) yield return x;
            foreach (var x in Walk(s.Else)) yield return x;
            foreach (var b in s.Branches)
                foreach (var x in Walk(b.Body)) yield return x;
            foreach (var x in Walk(s.Finally)) yield return x;
        }
    }

    /// <summary>
    /// Keeps a body within <see cref="MaxStatements"/>: statements are taken in source order (depth first) until the budget
    /// is spent; the rest are dropped and counted in <see cref="CodeBody.Omitted"/>.
    /// </summary>
    public static CodeBody Capped(IReadOnlyList<CodeStatement> statements, int max = MaxStatements)
    {
        var total = Count(statements);
        if (total <= max) return new CodeBody(statements);
        var budget = max;
        var kept = Take(statements, ref budget);
        return new CodeBody(kept) { Omitted = total - Count(kept) };
    }

    private static List<CodeStatement> Take(IReadOnlyList<CodeStatement> list, ref int budget)
    {
        var result = new List<CodeStatement>();
        foreach (var s in list)
        {
            if (budget <= 0) break;
            budget--;
            var calls = Take(s.Calls, ref budget);
            var body = Take(s.Body, ref budget);
            var els = Take(s.Else, ref budget);
            var branches = new List<CodeBranch>();
            foreach (var b in s.Branches)
            {
                if (budget <= 0) break;
                branches.Add(b with { Body = Take(b.Body, ref budget) });
            }
            var fin = Take(s.Finally, ref budget);
            result.Add(s with { Calls = calls, Body = body, Else = els, Branches = branches, Finally = fin });
        }
        return result;
    }

    /// <summary>Control flow in the tree: branches (if, switch, try with catches) and loops.</summary>
    public static (int Branches, int Loops, int Calls) Shape(IEnumerable<CodeStatement> statements)
    {
        int branches = 0, loops = 0, calls = 0;
        foreach (var s in Walk(statements))
        {
            if (s.Kind is CodeStatementKind.If or CodeStatementKind.Switch || s.Kind == CodeStatementKind.Try && s.Branches.Count > 0) branches++;
            if (s.Kind == CodeStatementKind.Loop) loops++;
            if (s.HasCall) calls++;
        }
        return (branches, loops, calls);
    }
}

/// <summary>A repository's "Behaviour diagrams" setting: which methods get activities and sequences from code.</summary>
public enum CodeBehaviourMode
{
    /// <summary>None, and existing ones are left alone.</summary>
    Off = 0,
    /// <summary>Only when asked for ("Generate activity / sequence from code"); those are kept up to date. The default for
    /// existing connections.</summary>
    OnDemand = 1,
    /// <summary>Activities for public methods with real control flow and sequences for entry points, capped per namespace
    /// and in total. The default for new connections.</summary>
    EntryPoints = 2,
    /// <summary>Every public method with a body (capped in total).</summary>
    AllPublic = 3,
}

/// <summary>Limits of behaviour diagrams from code.</summary>
public sealed record CodeBehaviourLimits
{
    /// <summary>Deepest sequence depth "Generate sequence from code" offers.</summary>
    public const int MaxSequenceDepth = 4;
    /// <summary>Message cap of a sequence generated on demand (the user picked its depth, so it may be longer).</summary>
    public const int OnDemandMaxMessages = 60;

    /// <summary>How deep a sequence follows calls into scanned types (1 = only the method's own calls).</summary>
    public int SequenceDepth { get; init; } = 2;
    /// <summary>Messages per sequence; the rest are left out and counted in a "… N more calls" note.</summary>
    public int MaxMessages { get; init; } = 25;
    public int ActivitiesPerNamespace { get; init; } = 20;
    public int SequencesPerNamespace { get; init; } = 10;
    /// <summary>New activities and sequences per sync, together ("Entry points" and "All public methods").</summary>
    public int MaxTotal { get; init; } = 300;
    /// <summary>"Entry points": an activity without a branch or loop needs at least this many calls, and blocks (try,
    /// using, lock) that give it shape; a plain run of calls folds into one action and is not worth a diagram.</summary>
    public int MinCallsForActivity { get; init; } = 5;
    /// <summary>Activities: this many consecutive plain statements (calls, assignments, creations) or more fold into one
    /// summary action ("12 calls: AddBlock, Connect, …"), the list in its documentation.</summary>
    public int FoldRun { get; init; } = 4;
    /// <summary>Activities: shapes and flows drawn at most (about). Beyond it the control flow stays and leaf blocks fold.</summary>
    public int MaxActivityShapes { get; init; } = 60;
    /// <summary>"Entry points": a method with no branch or loop and at least this many plain statements is a builder
    /// (sample data, wiring) and gets no activity or sequence of its own.</summary>
    public int BuilderStatements { get; init; } = 8;

    /// <summary>The limits of "Generate sequence from code" at <paramref name="depth"/> (1 to <see cref="MaxSequenceDepth"/>).</summary>
    public CodeBehaviourLimits ForDemand(int depth) =>
        this with { SequenceDepth = Math.Clamp(depth, 1, MaxSequenceDepth), MaxMessages = Math.Max(MaxMessages, OnDemandMaxMessages) };
}
