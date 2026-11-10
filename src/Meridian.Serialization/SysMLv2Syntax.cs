namespace Meridian.Serialization;

// Syntax tree produced by SysMLv2Parser. Nodes carry source spans so editors can map text <-> model.

/// <summary>A range of source text: [Start, End) offsets plus the 1-based line and column of Start.</summary>
public readonly record struct TextSpan(int Start, int End, int Line, int Column)
{
    public int Length => End - Start;
    public bool Contains(int offset) => offset >= Start && offset <= End;
    public static TextSpan Cover(TextSpan a, TextSpan b) => new(a.Start, Math.Max(a.End, b.End), a.Line, a.Column);
}

public enum SysmlSeverity { Error, Warning, Info }

/// <summary>A parse or mapping problem located in the text.</summary>
public sealed record SysmlDiagnostic(SysmlSeverity Severity, string Message, TextSpan Span)
{
    public bool IsError => Severity == SysmlSeverity.Error;
    public override string ToString() => $"Line {Span.Line}, col {Span.Column}: {Message}";
}

public abstract class SysmlNode
{
    public TextSpan Span { get; internal set; }
}

/// <summary>A reference to a named element: a qualified name (A::B) or a feature chain (a.b).</summary>
public sealed class SysmlRef : SysmlNode
{
    public SysmlRef(IReadOnlyList<string> segments, bool isChain, TextSpan span)
    {
        Segments = segments;
        IsChain = isChain;
        Span = span;
    }

    public IReadOnlyList<string> Segments { get; }

    /// <summary>True for dotted feature chains (engine.fuelIn), false for qualified names (Pkg::Engine).</summary>
    public bool IsChain { get; }

    /// <summary>A leading '~' (conjugated port type).</summary>
    public bool Conjugated { get; init; }

    /// <summary>The unquoted keywords <c>start</c> / <c>done</c> (initial and final nodes), as opposed to elements named so.</summary>
    public bool IsKeyword { get; init; }

    public string Simple => Segments[^1];
    public string Text => string.Join(IsChain ? "." : "::", Segments);
    public override string ToString() => Text;
}

/// <summary>Anything that may appear in a namespace body.</summary>
public abstract class SysmlMember : SysmlNode { }

/// <summary>A definition or usage: <c>part def Vehicle :> Base { … }</c>, <c>attribute mass : Real = 1450;</c>, <c>package P { … }</c>.</summary>
public sealed class SysmlDecl : SysmlMember
{
    /// <summary>The kind keyword, normalized: "package", "part", "port", "attribute", "item", "enum", "constraint", "requirement",
    /// "verification", "action", "state", "use case", "occurrence", "interface", "connection", "allocation", "subject", "flow" …</summary>
    public string Keyword { get; internal set; } = "";
    public bool IsDef { get; internal set; }
    /// <summary>The keyword phrase exactly as written, without modifiers and 'def' ("part", "analysis case", "perform action");
    /// empty when the declaration has no keyword (<c>in x : Real;</c>, <c>:>> mass = 1;</c>, an enum literal shorthand).</summary>
    public string KeywordText { get; internal set; } = "";
    public bool ImplicitKeyword => KeywordText.Length == 0;
    /// <summary>Prefix keywords other than the direction, in source order: visibility, abstract, readonly, derived, end, ref,
    /// variation, variant, individual, constant, standard, library …</summary>
    public List<string> Modifiers { get; } = new();
    public string? Visibility => Modifiers.FirstOrDefault(m => m is "public" or "private" or "protected");
    public string? Name { get; internal set; }
    public TextSpan NameSpan { get; internal set; }
    public string? ShortName { get; internal set; }
    public bool IsAbstract { get; internal set; }
    public string? Direction { get; internal set; }
    public List<string> Tags { get; } = new();
    public List<SysmlRef> Types { get; } = new();
    public List<SysmlRef> Specializes { get; } = new();
    public List<SysmlRef> Redefines { get; } = new();
    /// <summary><c>::></c> / <c>references</c> targets.</summary>
    public List<SysmlRef> References { get; } = new();
    public string? Multiplicity { get; internal set; }
    /// <summary>Header flags after the name: ordered, nonunique, parallel.</summary>
    public List<string> Flags { get; } = new();
    public string? Value { get; internal set; }
    /// <summary>How the value is bound: "=", ":=", "default", "default =", "default :=".</summary>
    public string? ValueOp { get; internal set; }
    public TextSpan ValueSpan { get; internal set; }
    /// <summary>True when the header has clauses the mapper doesn't model (allocate / from / of / about / via …).</summary>
    public bool OpaqueHeader { get; internal set; }
    /// <summary>Action-body statements (accept, send, assign, if, while, for, terminate) and state actions (entry, do, exit):
    /// the statement text after the keyword, without the final ';'.</summary>
    public string? Raw { get; internal set; }
    public string? Id { get; internal set; }
    /// <summary>The <c>connect a to b</c> clause of connection / interface usages.</summary>
    public (SysmlRef From, SysmlRef To)? Connect { get; internal set; }
    public bool HasBody { get; internal set; }
    public List<SysmlMember> Body { get; } = new();
    /// <summary>Name used to address an anonymous control node (decision1, fork2 …), matching the exporter's convention.</summary>
    public string? SyntheticName { get; internal set; }
    public TextSpan HeaderSpan { get; internal set; }

    /// <summary>Name if given, else the redefined feature's name, else the synthetic name.</summary>
    public string? EffectiveName => Name ?? (Redefines.Count > 0 ? Redefines[0].Simple : null) ?? SyntheticName;

    public IEnumerable<SysmlDecl> Decls => Body.OfType<SysmlDecl>();
    public override string ToString() => $"{Keyword}{(IsDef ? " def" : "")} {Name}";
}

/// <summary><c>doc /* text */</c></summary>
public sealed class SysmlDoc : SysmlMember
{
    public string Text { get; init; } = "";
}

/// <summary><c>comment [name] [about x] /* body */</c> or a bare <c>/* body */</c>.</summary>
public sealed class SysmlComment : SysmlMember
{
    public string? Name { get; init; }
    public string Body { get; init; } = "";
    public List<SysmlRef> About { get; } = new();
}

/// <summary><c>import A::B::*;</c></summary>
public sealed class SysmlImport : SysmlMember
{
    public string Target { get; init; } = "";
    /// <summary>The imported namespace (or member) without the wildcard.</summary>
    public SysmlRef? Namespace { get; init; }
}

/// <summary>A metadata annotation <c>@Safety { isMandatory = true; }</c> (or <c>@Safety;</c>).</summary>
public sealed class SysmlMetadata : SysmlMember
{
    public SysmlRef Type { get; init; } = default!;
    public List<(string Name, string Value)> Values { get; } = new();
    /// <summary>True when the annotation has more than simple <c>name = value;</c> entries (about, nested declarations …).</summary>
    public bool Complex { get; internal set; }
}

/// <summary>Binary relationship statements: satisfy, verify, allocate, dependency (and tagged variants), include, message, connect, bind, flow.</summary>
public sealed class SysmlRelation : SysmlMember
{
    /// <summary>"satisfy", "verify", "allocate", "dependency", "derivation", "refinement", "trace", "include", "extend",
    /// "message", "connect", "bind", "flow".</summary>
    public string Keyword { get; init; } = "";
    public string? Name { get; init; }
    /// <summary>Null when the source is implied by the context (e.g. <c>satisfy R;</c> inside a part def).</summary>
    public SysmlRef? Source { get; init; }
    public SysmlRef? Target { get; init; }
    /// <summary>message sort (synchCall / asynchCall / reply) or the item conveyed by a flow.</summary>
    public string? Detail { get; init; }
    /// <summary>True when the statement has parts the mapper doesn't model (kept as text).</summary>
    public bool Complex { get; init; }
}

/// <summary>A succession or transition: <c>first a then b;</c>, <c>transition t first s1 accept e if g do x then s2;</c>, <c>then b;</c>.</summary>
public sealed class SysmlSuccession : SysmlMember
{
    public bool IsTransition { get; init; }
    public string? Name { get; init; }
    public SysmlRef? First { get; init; }
    public SysmlRef? Then { get; init; }
    public string? Trigger { get; init; }
    public string? Guard { get; init; }
    public string? Effect { get; init; }
    /// <summary>For <c>then action x;</c>: the declaration the shorthand introduced (the succession span covers it).</summary>
    public SysmlDecl? Shorthand { get; init; }
}

/// <summary>A bare expression (constraint bodies) kept verbatim.</summary>
public sealed class SysmlExpression : SysmlMember
{
    public string Text { get; init; } = "";
}

/// <summary>Syntactically valid SysML v2 that Meridian does not map to the model; kept so it can be reported.</summary>
public sealed class SysmlUnsupported : SysmlMember
{
    public string Keyword { get; init; } = "";
    /// <summary>The name an alias declares.</summary>
    public string? Name { get; init; }
}

/// <summary>The result of parsing a .sysml text.</summary>
public sealed class SysmlDocument
{
    public required string Text { get; init; }
    public List<SysmlMember> Members { get; } = new();
    public List<SysmlDiagnostic> Diagnostics { get; } = new();
    public bool HasErrors => Diagnostics.Any(d => d.IsError);
    public IEnumerable<SysmlDiagnostic> Errors => Diagnostics.Where(d => d.IsError);

    public IEnumerable<SysmlDecl> AllDecls() => Walk(Members);

    private static IEnumerable<SysmlDecl> Walk(IEnumerable<SysmlMember> members)
    {
        foreach (var d in members.OfType<SysmlDecl>())
        {
            yield return d;
            foreach (var c in Walk(d.Body)) yield return c;
        }
    }
}
