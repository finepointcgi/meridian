using System.Text;
using System.Text.RegularExpressions;

namespace Meridian.Serialization;

/// <summary>
/// Hand-written recursive-descent parser for the SysML v2 textual notation. It covers what <see cref="SysMLv2Text"/> exports
/// plus common real-world syntax (definitions and usages of parts, ports, attributes, items, enums, constraints, requirements,
/// actions, states, use cases, interfaces and connections; specialization, redefinition, typing, multiplicities, values;
/// successions, transitions, satisfy / verify / allocate / dependencies, connect / bind / flow, imports, doc and comments).
/// It never throws: problems become <see cref="SysmlDiagnostic"/>s with line and column, and parsing resumes at the next statement.
/// </summary>
public static partial class SysMLv2Parser
{
    public static SysmlDocument Parse(string text) => Parse(text, lint: true);

    /// <summary>Parses; with <paramref name="lint"/> also checks units in quantity literals against the standard library.</summary>
    internal static SysmlDocument Parse(string text, bool lint)
    {
        var doc = new SysmlDocument { Text = text ?? "" };
        var lexer = new Lexer(doc.Text, doc.Diagnostics);
        var p = new Parser(doc, lexer.Tokenize());
        p.ParseTop();
        if (lint) SysMLv2Library.CheckUnits(doc);
        return doc;
    }

    // ================================================================== lexer

    internal enum Tk { Ident, Name, String, Number, Sym, Comment, Eof }

    internal readonly record struct Token(Tk Kind, string Text, int Start, int End, int Line, int Col)
    {
        public TextSpan Span => new(Start, End, Line, Col);
    }

    [GeneratedRegex(@"^@id\s+(\S+)$")]
    private static partial Regex IdTag();

    internal static string CleanComment(string body) =>
        string.Join("\n", body.Replace("\r", "").Split('\n').Select(l => l.Trim().TrimStart('*').Trim())).Trim();

    /// <summary>Returns the id carried by a <c>/* @id _abc */</c> comment, or null.</summary>
    internal static string? IdOf(string commentText) => IdTag().Match(commentText) is { Success: true } m ? m.Groups[1].Value : null;

    private sealed class Lexer(string s, List<SysmlDiagnostic> diags)
    {
        private int _i, _line = 1, _lineStart;

        private static readonly string[] Symbols = [":>>", "::>", "===", "!==", ":>", "::", ":=", "..", "->", "=>", "==", "!=", "<=", ">=", "**"];

        private Token Make(Tk k, string text, int start, int line, int col) => new(k, text, start, _i, line, col);

        public List<Token> Tokenize()
        {
            var list = new List<Token>();
            while (_i < s.Length)
            {
                var c = s[_i];
                if (c == '\n') { _i++; _line++; _lineStart = _i; continue; }
                if (char.IsWhiteSpace(c)) { _i++; continue; }
                var start = _i; var line = _line; var col = _i - _lineStart + 1;
                if (c == '/' && At(1) == '/')
                {
                    if (At(2) == '*')
                    {   // "//* … */" multi-line note: ignored
                        var close = s.IndexOf("*/", _i + 3, StringComparison.Ordinal);
                        Advance(close < 0 ? s.Length : close + 2);
                        continue;
                    }
                    while (_i < s.Length && s[_i] != '\n') _i++;
                    continue;
                }
                if (c == '/' && At(1) == '*')
                {
                    var close = s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        diags.Add(new(SysmlSeverity.Error, "Unterminated comment: missing '*/'", new(start, start + 2, line, col)));
                        close = s.Length;
                    }
                    var body = s[(_i + 2)..Math.Min(close, s.Length)];
                    Advance(Math.Min(close + 2, s.Length));
                    list.Add(Make(Tk.Comment, CleanComment(body), start, line, col));
                    continue;
                }
                if (c is '\'' or '"')
                {
                    var sb = new StringBuilder();
                    _i++;
                    var closed = false;
                    while (_i < s.Length && s[_i] != '\n')
                    {
                        if (s[_i] == '\\' && _i + 1 < s.Length) { _i++; sb.Append(s[_i] switch { 'n' => '\n', 't' => '\t', var x => x }); _i++; continue; }
                        if (s[_i] == c) { _i++; closed = true; break; }
                        sb.Append(s[_i++]);
                    }
                    if (!closed) diags.Add(new(SysmlSeverity.Error, c == '\'' ? "Unterminated name: missing closing '" : "Unterminated string: missing closing \"", new(start, _i, line, col)));
                    list.Add(Make(c == '\'' ? Tk.Name : Tk.String, sb.ToString(), start, line, col));
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    while (_i < s.Length && (char.IsLetterOrDigit(s[_i]) || s[_i] == '_')) _i++;
                    list.Add(Make(Tk.Ident, s[start.._i], start, line, col));
                    continue;
                }
                if (char.IsDigit(c))
                {
                    while (_i < s.Length && char.IsDigit(s[_i])) _i++;
                    if (At(0) == '.' && char.IsDigit(At(1))) { _i++; while (_i < s.Length && char.IsDigit(s[_i])) _i++; }
                    if (At(0) is 'e' or 'E' && (char.IsDigit(At(1)) || At(1) is '+' or '-' && char.IsDigit(At(2))))
                    {
                        _i += 2;
                        while (_i < s.Length && char.IsDigit(s[_i])) _i++;
                    }
                    list.Add(Make(Tk.Number, s[start.._i], start, line, col));
                    continue;
                }
                var sym = Symbols.FirstOrDefault(x => string.CompareOrdinal(s, _i, x, 0, x.Length) == 0);
                _i += sym?.Length ?? 1;
                list.Add(Make(Tk.Sym, sym ?? c.ToString(), start, line, col));
            }
            list.Add(new Token(Tk.Eof, "", s.Length, s.Length, _line, s.Length - _lineStart + 1));
            return list;
        }

        private char At(int k) => _i + k < s.Length ? s[_i + k] : '\0';

        private void Advance(int to)
        {
            for (; _i < to; _i++)
                if (s[_i] == '\n') { _line++; _lineStart = _i + 1; }
        }
    }

    // ================================================================== parser

    private sealed class ParseError(string message, TextSpan span) : Exception(message)
    {
        public TextSpan Span { get; } = span;
    }

    /// <summary>Keywords that begin a statement; used to detect a missing ';' and to tell names from keywords.</summary>
    internal static readonly HashSet<string> StatementKeywords = new(StringComparer.Ordinal)
    {
        "package", "library", "standard", "part", "port", "attribute", "item", "enum", "constraint", "requirement", "verification", "action",
        "state", "use", "occurrence", "interface", "connection", "allocation", "doc", "comment", "import", "alias", "satisfy", "verify",
        "allocate", "dependency", "include", "message", "connect", "bind", "flow", "first", "then", "succession", "transition", "decide",
        "merge", "fork", "join", "entry", "exit", "subject", "objective", "require", "assume", "perform", "exhibit", "calc", "analysis",
        "case", "view", "viewpoint", "rendering", "metadata", "concern", "stakeholder", "actor", "ref", "abstract", "public", "private",
        "protected", "in", "out", "inout", "accept", "send", "assert", "return", "individual", "snapshot", "timeslice", "event",
        "variation", "variant", "end", "readonly", "derived", "constant", "frame", "expose", "filter", "if", "else", "assign", "loop",
        "while", "until", "for", "terminate", "do", "render", "rep", "language",
    };

    /// <summary>Statements inside action bodies that Meridian keeps as action nodes with verbatim text.</summary>
    internal static readonly HashSet<string> ActionBodyKeywords = new(StringComparer.Ordinal)
        { "accept", "send", "assign", "if", "while", "loop", "for", "terminate" };

    /// <summary>Words that continue a declaration header and therefore can't be its name.</summary>
    private static readonly HashSet<string> HeaderWords = new(StringComparer.Ordinal)
    {
        "def", "defined", "typed", "by", "specializes", "subsets", "redefines", "references", "connect", "to", "from", "of", "default",
        "ordered", "nonunique", "about", "parallel", "accept", "via", "if", "do", "then", "first", "all", "allocate", "locale",
    };

    /// <summary>Declaration keywords Meridian maps to model elements, plus ones it parses but ignores.</summary>
    private static readonly HashSet<string> DeclKeywords = new(StringComparer.Ordinal)
    {
        "part", "port", "attribute", "item", "enum", "constraint", "requirement", "verification", "action", "state", "occurrence",
        "interface", "connection", "allocation", "calc", "analysis", "case", "view", "viewpoint", "rendering", "metadata", "concern",
        "subject", "actor", "stakeholder", "objective", "event", "flow",
    };

    private static readonly HashSet<string> ExpressionContexts = new(StringComparer.Ordinal)
        { "constraint", "calc", "analysis", "require", "assume", "assert", "case", "requirement", "concern", "verification", "viewpoint" };

    private sealed class Parser(SysmlDocument doc, List<Token> toks)
    {
        private int _i = Skip(toks, 0);
        private int _last = -1;       // index of the last consumed (non-comment) token
        private int _watermark = -1;  // comment tokens at or below this index were taken by doc / comment / @id
        private readonly string _src = doc.Text;

        private Token Cur => toks[_i];
        private Token LastTok => _last >= 0 ? toks[_last] : toks[0];

        private static int Skip(List<Token> t, int i)
        {
            while (i < t.Count - 1 && t[i].Kind == Tk.Comment) i++;
            return i;
        }

        private Token PeekTok(int k = 1)
        {
            var i = _i;
            for (var n = 0; n < k; n++) i = Skip(toks, Math.Min(i + 1, toks.Count - 1));
            return toks[i];
        }

        private Token Next()
        {
            var t = toks[_i];
            _last = _i;
            if (_i < toks.Count - 1) _i = Skip(toks, _i + 1);
            return t;
        }

        private bool Is(string text) => Cur.Kind is Tk.Ident or Tk.Sym && Cur.Text == text;
        private bool IsName => Cur.Kind == Tk.Name || Cur.Kind == Tk.Ident && !HeaderWords.Contains(Cur.Text) && !IsStatementStartOnNewLine();
        private bool Accept(string text) { if (!Is(text)) return false; Next(); return true; }

        private bool IsStatementStartOnNewLine() =>
            Cur.Kind == Tk.Ident && StatementKeywords.Contains(Cur.Text) && _last >= 0 && Cur.Line > LastTok.Line;

        private static string Describe(Token t) => t.Kind switch
        {
            Tk.Eof => "end of file",
            Tk.Name => $"'{t.Text}'",
            Tk.String => $"\"{t.Text}\"",
            _ => $"'{t.Text}'",
        };

        private ParseError Error(string message, Token? at = null) => new(message, (at ?? Cur).Span);

        private void Expect(string text, string what)
        {
            if (Accept(text)) return;
            throw MissingAfterLast($"Expected '{text}' {what} but found {Describe(Cur)}");
        }

        /// <summary>An error positioned just after the previous token (for a missing ';' and the like) when the next token is on another line.</summary>
        private ParseError MissingAfterLast(string message)
        {
            if (_last >= 0 && Cur.Line > LastTok.Line)
            {
                var t = LastTok;
                return new ParseError(message, new TextSpan(t.End, t.End + 1, t.Line, t.Col + (t.End - t.Start)));
            }
            return Error(message);
        }

        private void AddDiag(SysmlSeverity s, string msg, TextSpan span) => doc.Diagnostics.Add(new(s, msg, span));

        // ------------------------------------------------------------ comments

        /// <summary>Comment tokens between the previous consumed token and the current one that nobody has taken yet.</summary>
        private IEnumerable<(int Index, Token Tok)> PendingComments()
        {
            for (var k = Math.Max(_last, _watermark) + 1; k < _i; k++)
                if (toks[k].Kind == Tk.Comment) yield return (k, toks[k]);
        }

        /// <summary>Takes the comment that directly follows the last consumed token (doc / comment bodies).</summary>
        private Token? TakeComment()
        {
            var k = _last + 1;
            if (k < toks.Count && k > _watermark && toks[k].Kind == Tk.Comment) { _watermark = k; return toks[k]; }
            return null;
        }

        /// <summary>Scans comments inside [fromIndex, current) for an @id tag and marks them taken.</summary>
        private string? TakeIdBetween(int fromIndex)
        {
            string? id = null;
            for (var k = Math.Max(fromIndex, _watermark + 1); k < _i; k++)
                if (toks[k].Kind == Tk.Comment && IdOf(toks[k].Text) is { } x) { id = x; _watermark = Math.Max(_watermark, k); }
            return id;
        }

        // ------------------------------------------------------------ names and expressions

        private (string Name, TextSpan Span) ReadName(string what)
        {
            if (Cur.Kind is Tk.Ident or Tk.Name) { var t = Next(); return (t.Text, t.Span); }
            throw MissingAfterLast($"Expected {what} but found {Describe(Cur)}");
        }

        private SysmlRef ReadRef(string what = "a name")
        {
            var start = Cur;
            var conj = Accept("~");
            if (Cur.Kind is not (Tk.Ident or Tk.Name)) throw MissingAfterLast($"Expected {what} but found {Describe(Cur)}");
            var keyword = Cur.Kind == Tk.Ident && Cur.Text is "start" or "done";
            var segs = new List<string> { Next().Text };
            var chain = false;
            while (Is("::") || Is("."))
            {
                if (Is(".") && PeekTok().Kind is not (Tk.Ident or Tk.Name)) break;
                if (Is("::") && PeekTok() is { Kind: Tk.Sym, Text: "*" or "**" }) break;   // import wildcard
                chain |= Is(".");
                Next();
                segs.Add(ReadName(what).Name);
            }
            return new SysmlRef(segs, chain, TextSpan.Cover(start.Span, LastTok.Span)) { Conjugated = conj, IsKeyword = keyword && segs.Count == 1 };
        }

        private List<SysmlRef> ReadRefList(string what)
        {
            var list = new List<SysmlRef> { ReadRef(what) };
            while (Accept(",")) list.Add(ReadRef(what));
            return list;
        }

        /// <summary>Reads raw source text up to a stop token at nesting depth 0. Returns null if empty.</summary>
        private string? ReadExpr(bool stopAtBrace, params string[] stopWords)
        {
            var depth = 0;
            int? first = null; var lastEnd = 0;
            while (Cur.Kind != Tk.Eof)
            {
                if (depth == 0)
                {
                    if (Is(";") || Is("}") || (stopAtBrace && Is("{"))) break;
                    if (Cur.Kind == Tk.Ident && stopWords.Contains(Cur.Text)) break;
                    if (first != null && IsStatementStartOnNewLine() && Cur.Text is not ("if" or "then" or "else")) break;
                }
                if (Cur.Kind == Tk.Sym)
                {
                    if (Cur.Text is "(" or "[" or "{") depth++;
                    else if (Cur.Text is ")" or "]" or "}") depth--;
                }
                var t = Next();
                first ??= t.Start;
                lastEnd = t.End;
            }
            return first == null ? null : _src[first.Value..lastEnd].Trim();
        }

        // ------------------------------------------------------------ bodies

        public void ParseTop()
        {
            var ctx = new BodyContext(null);
            ParseBody(doc.Members, ctx, top: true, opener: null);
        }

        private sealed class BodyContext(SysmlDecl? owner)
        {
            public SysmlDecl? Owner { get; } = owner;
            public string? Previous;      // last action / state / node for "then" shorthand
            public string? LastDecision;  // last decide / merge for "if … then" shorthand
            public int Decisions, Forks;
            public readonly Dictionary<string, int> Counters = new(StringComparer.Ordinal);
            public bool Expressions => Owner != null && ExpressionContexts.Contains(Owner.Keyword);
            public bool IsMetadata => Owner?.Keyword == "metadata";
            public bool IsEnum => Owner is { Keyword: "enum", IsDef: true };
            public bool IsState => Owner?.Keyword is "state" or "exhibit state";
        }

        private void ParseBody(List<SysmlMember> into, BodyContext ctx, bool top, Token? opener)
        {
            while (true)
            {
                CollectComments(into);
                if (Cur.Kind == Tk.Eof)
                {
                    if (!top && opener is { } o)
                        AddDiag(SysmlSeverity.Error, $"Missing '}}' to close the block opened at line {o.Line}" + (ctx.Owner?.Name is { } n ? $" ('{n}')" : ""), o.Span);
                    return;
                }
                if (Is("}"))
                {
                    if (!top) { Next(); return; }
                    AddDiag(SysmlSeverity.Error, "Unexpected '}': no block is open here", Cur.Span);
                    Next();
                    continue;
                }
                var before = _i;
                try
                {
                    ParseStatement(into, ctx);
                }
                catch (ParseError e)
                {
                    AddDiag(SysmlSeverity.Error, e.Message, e.Span);
                    Recover();
                }
                if (_i == before && Cur.Kind != Tk.Eof && !Is("}")) Next();   // always make progress
            }
        }

        /// <summary>Error recovery: skip to the end of the broken statement (';' or a balanced block) or the next statement keyword on a new line.</summary>
        private void Recover()
        {
            var depth = 0;
            var startLine = Cur.Line;
            while (Cur.Kind != Tk.Eof)
            {
                if (depth == 0 && Cur.Line > startLine && IsStatementStartOnNewLine()) return;
                if (Is("{")) depth++;
                else if (Is("}"))
                {
                    if (depth == 0) return;
                    depth--;
                    Next();
                    if (depth == 0) return;
                    continue;
                }
                else if (Is(";") && depth == 0) { Next(); return; }
                Next();
            }
        }

        private string? _pendingId;

        private void CollectComments(List<SysmlMember> into)
        {
            foreach (var (k, t) in PendingComments().ToList())
            {
                _watermark = Math.Max(_watermark, k);
                if (IdOf(t.Text) is { } id) { _pendingId = id; continue; }
                into.Add(new SysmlComment { Body = t.Text, Span = t.Span });
            }
        }

        private void Terminate(SysmlNode node, Token start, bool allowBody = false)
        {
            if (allowBody && Is("{")) SkipBlock();
            else if (!Accept(";")) throw MissingAfterLast($"Expected ';' but found {Describe(Cur)}");
            node.Span = TextSpan.Cover(start.Span, LastTok.Span);
        }

        private void SkipBlock()
        {
            var open = Next();
            var depth = 1;
            while (Cur.Kind != Tk.Eof && depth > 0)
            {
                if (Is("{")) depth++;
                else if (Is("}")) depth--;
                Next();
            }
            if (depth > 0) throw new ParseError($"Missing '}}' to close the block opened at line {open.Line}", open.Span);
        }

        // ------------------------------------------------------------ statements

        private void ParseStatement(List<SysmlMember> into, BodyContext ctx)
        {
            var start = Cur;
            var startIndex = _i;
            var leadingId = _pendingId;
            _pendingId = null;

            if (Accept(";")) return;

            // prefixes
            string? direction = null; var isAbstract = false; var tags = new List<string>(); string? prefixKeyword = null;
            var modifiers = new List<string>();
            while (true)
            {
                if (Is("public") || Is("private") || Is("protected") || Is("readonly") || Is("derived") || Is("constant") || Is("end")
                    || Is("ordered") || Is("nonunique") || Is("variation") || Is("variant") || Is("individual")) { modifiers.Add(Next().Text); continue; }
                if (Is("abstract")) { Next(); isAbstract = true; modifiers.Add("abstract"); continue; }
                if (Is("in") || Is("out") || Is("inout")) { direction = Next().Text; continue; }
                if (Is("#")) { Next(); tags.Add(ReadRef("a metadata name").Text); continue; }
                if (Is("ref") && PeekTok() is { Kind: Tk.Ident } p && (DeclKeywords.Contains(p.Text) || p.Text is "use" or "analysis" or "verification"))
                { Next(); modifiers.Add("ref"); continue; }
                if (Is("perform") || Is("exhibit") || Is("snapshot") || Is("timeslice") || Is("event"))
                {
                    prefixKeyword ??= Cur.Text;
                    Next();
                    continue;
                }
                break;
            }

            if (Cur.Kind == Tk.Eof || Is("}")) throw Error($"Expected a declaration but found {Describe(Cur)}");
            var kw = Cur.Kind == Tk.Ident ? Cur.Text : Cur.Kind == Tk.Sym && Cur.Text == "@" ? "@" : null;

            SysmlDecl NewDecl(string keyword, string keywordText) => new SysmlDecl
            {
                Keyword = keyword, KeywordText = keywordText, IsAbstract = isAbstract, Direction = direction,
            }.With(tags, modifiers);

            switch (kw)
            {
                case "doc":
                {
                    Next();
                    var body = TakeComment();
                    if (body == null && IsName) { Next(); body = TakeComment(); }
                    if (body == null && Accept("locale")) { if (Cur.Kind == Tk.String) Next(); body = TakeComment(); }
                    if (body == null) throw MissingAfterLast("Expected a /* comment */ after 'doc'");
                    Accept(";");
                    into.Add(new SysmlDoc { Text = body.Value.Text, Span = TextSpan.Cover(start.Span, body.Value.Span) });
                    return;
                }
                case "comment":
                {
                    Next();
                    string? name = null;
                    var about = new List<SysmlRef>();
                    var body = TakeComment();
                    if (body == null && IsName && !Is("about")) { name = Next().Text; body = TakeComment(); }
                    if (body == null && Accept("about")) { about.AddRange(ReadRefList("an annotated element")); body = TakeComment(); }
                    if (body == null && Accept("locale")) { if (Cur.Kind == Tk.String) Next(); body = TakeComment(); }
                    if (body == null) throw MissingAfterLast("Expected a /* comment */ after 'comment'");
                    Accept(";");
                    var c = new SysmlComment { Name = name, Body = body.Value.Text, Span = TextSpan.Cover(start.Span, body.Value.Span) };
                    c.About.AddRange(about);
                    into.Add(c);
                    return;
                }
                case "rep" or "language":
                {   // textual representation: rep [name] language "ocl" /* … */
                    Next();
                    if (kw == "rep")
                    {
                        if (IsName && !Is("language")) Next();
                        Expect("language", "in 'rep … language \"…\"'");
                    }
                    if (Cur.Kind != Tk.String) throw MissingAfterLast($"Expected a language name string but found {Describe(Cur)}");
                    Next();
                    var body = TakeComment() ?? throw MissingAfterLast("Expected a /* body */ after the language name");
                    Accept(";");
                    into.Add(new SysmlUnsupported { Keyword = "rep", Span = TextSpan.Cover(start.Span, body.Span) });
                    return;
                }
                case "import":
                {
                    Next();
                    Accept("all");
                    var r = ReadRef("an imported namespace");
                    var target = r.Text;
                    while (Is("::") && PeekTok() is { Kind: Tk.Sym, Text: "*" or "**" }) { Next(); target += "::" + Next().Text; }
                    if (Is("[")) ReadExpr(true);   // filter condition
                    var imp = new SysmlImport { Target = target, Namespace = r };
                    Terminate(imp, start, allowBody: true);
                    into.Add(imp);
                    return;
                }
                case "alias":
                {
                    Next();
                    if (Is("<")) { Next(); ReadName("a short name"); Expect(">", "to close the short name"); }
                    var u = new SysmlUnsupported { Keyword = "alias", Name = Cur.Kind is Tk.Ident or Tk.Name ? Cur.Text : null };
                    ReadExpr(false);
                    Terminate(u, start);
                    into.Add(u);
                    return;
                }
                case "library" or "standard" or "package":
                {
                    while (Is("standard") || Is("library")) modifiers.Add(Next().Text);
                    Expect("package", "after 'library'");
                    var d = NewDecl("package", "package");
                    ParseDeclRest(d, start, startIndex, leadingId, into, ctx);
                    return;
                }
                case "satisfy" or "verify":
                {
                    Next();
                    var complex = Accept("requirement");
                    if (Is("not")) { Next(); complex = true; }
                    var target = ReadRef("a requirement");
                    if (Accept(":")) { target = ReadRef("a requirement"); complex = true; }
                    SysmlRef? by = null;
                    if (Accept("by")) by = ReadRef("the satisfying element");
                    complex |= Is("{");
                    var rel = new SysmlRelation { Keyword = kw, Source = by, Target = target, Complex = complex };
                    Terminate(rel, start, allowBody: true);
                    into.Add(rel);
                    return;
                }
                case "assert":
                    Next();
                    SkipUnsupported(into, start, "assert");
                    return;
                case "allocate":
                {
                    Next();
                    if (Is("("))
                    {
                        SkipUnsupported(into, start, "allocate");
                        return;
                    }
                    var a = ReadRef("an allocated element");
                    Expect("to", "in 'allocate … to …'");
                    var b = ReadRef("an allocation target");
                    var rel = new SysmlRelation { Keyword = "allocate", Source = a, Target = b, Complex = Is("{") };
                    Terminate(rel, start, allowBody: true);
                    into.Add(rel);
                    return;
                }
                case "dependency":
                {
                    Next();
                    string? name = null;
                    if (IsName && PeekTok() is { Kind: Tk.Ident, Text: "from" }) name = Next().Text;
                    Accept("from");
                    var sources = ReadRefList("a client element");
                    Expect("to", "in 'dependency … to …'");
                    var targets = ReadRefList("a supplier element");
                    var tag = tags.FirstOrDefault(t => t is "derivation" or "refinement" or "trace" or "extend" or "derive" or "refine");
                    var keyword = tag switch { "derive" => "derivation", "refine" => "refinement", null => "dependency", _ => tag };
                    var complex = Is("{") || tags.Count > (tag == null ? 0 : 1) || sources.Count * targets.Count > 1;
                    var first = true;
                    foreach (var s in sources)
                        foreach (var t in targets)
                        {
                            var rel = new SysmlRelation { Keyword = keyword, Name = name, Source = s, Target = t, Complex = complex };
                            if (first) { Terminate(rel, start, allowBody: true); first = false; }
                            else rel.Span = TextSpan.Cover(start.Span, LastTok.Span);
                            into.Add(rel);
                        }
                    return;
                }
                case "include":
                {
                    Next();
                    Accept("use"); Accept("case");
                    var target = ReadRef("a use case");
                    var complex = false;
                    if (Accept(":")) { target = ReadRef("a use case definition"); complex = true; }
                    complex |= Is("{");
                    var rel = new SysmlRelation { Keyword = "include", Target = target, Complex = complex };
                    Terminate(rel, start, allowBody: true);
                    into.Add(rel);
                    return;
                }
                case "message":
                {
                    Next();
                    string? name = null; string? item = null;
                    var complex = false;
                    if (IsName && !Is("from") && !Is("of")) name = Next().Text;
                    if (Accept(":")) { ReadRef("a message type"); complex = true; }
                    if (Accept("of")) { item = ReadRef("a message payload").Text; if (Accept(":")) { ReadRef("a payload type"); complex = true; } }
                    if (!Is("from")) { SkipUnsupported(into, start, "message"); return; }
                    Expect("from", "in 'message … from … to …'");
                    var a = ReadRef("the sending lifeline");
                    Expect("to", "in 'message … from … to …'");
                    var b = ReadRef("the receiving lifeline");
                    var sort = tags.Contains("reply") ? "reply" : tags.Contains("async") ? "asynchCall" : "synchCall";
                    var rel = new SysmlRelation { Keyword = "message", Name = name, Source = a, Target = b, Detail = sort, Complex = complex || item != null || Is("{") };
                    Terminate(rel, start, allowBody: true);
                    into.Add(rel);
                    return;
                }
                case "connect":
                {
                    Next();
                    if (Is("(")) { SkipUnsupported(into, start, "connect"); return; }
                    var a = ReadRef("a connector end");
                    Expect("to", "in 'connect … to …'");
                    var b = ReadRef("a connector end");
                    var rel = new SysmlRelation { Keyword = "connect", Source = a, Target = b, Complex = Is("{") };
                    Terminate(rel, start, allowBody: true);
                    into.Add(rel);
                    return;
                }
                case "bind":
                {
                    Next();
                    var a = ReadRef("a bound feature");
                    Expect("=", "in 'bind … = …'");
                    var b = ReadRef("a bound feature");
                    var rel = new SysmlRelation { Keyword = "bind", Source = a, Target = b, Complex = Is("{") };
                    Terminate(rel, start, allowBody: true);
                    into.Add(rel);
                    return;
                }
                case "succession" when PeekTok() is { Kind: Tk.Ident, Text: "flow" } && PeekTok(2) is not { Kind: Tk.Ident, Text: "def" }:
                    Next();
                    ParseFlow(into, start, complex: true);
                    return;
                case "flow" when !(PeekTok() is { Kind: Tk.Ident, Text: "def" }):
                    ParseFlow(into, start, complex: false);
                    return;
                case "succession" when !(PeekTok() is { Kind: Tk.Ident, Text: "def" or "flow" }):
                {
                    Next();
                    string? name = null;
                    if (IsName && !Is("first")) name = Next().Text;
                    ParseSuccession(into, ctx, start, name, transition: false);
                    return;
                }
                case "first":
                    ParseSuccession(into, ctx, start, null, transition: false);
                    return;
                case "transition":
                {
                    Next();
                    string? name = null;
                    if (IsName && !Is("first") && !Is("accept")) name = Next().Text;
                    ParseSuccession(into, ctx, start, name, transition: true);
                    return;
                }
                case "accept" when ctx.IsState:
                    ParseSuccession(into, ctx, start, null, transition: true);
                    return;
                case "then":
                {
                    Next();
                    if (Cur.Kind == Tk.Ident && (DeclKeywords.Contains(Cur.Text) || ActionBodyKeywords.Contains(Cur.Text) && !(ctx.IsState && Cur.Text == "accept")
                        || Cur.Text is "decide" or "merge" or "fork" or "join" or "done" or "use" or "perform" or "exhibit" or "ref" or "event" or "include"
                            or "snapshot" or "timeslice" or "analysis" or "verification" or "private" or "public" or "protected" or "abstract"))
                    {
                        if (Is("done"))
                        {
                            var r = ReadRef();
                            AddSuccession(into, ctx, start, r, null);
                            Terminate(into[^1], start);
                            return;
                        }
                        var from = ctx.Previous;
                        var before = into.Count;
                        ParseStatement(into, ctx);
                        if (into.Skip(before).OfType<SysmlDecl>().FirstOrDefault() is { } target && target.EffectiveName is { } tn)
                            into.Add(new SysmlSuccession
                            {
                                IsTransition = ctx.IsState, First = from == null ? null : Ref(from, start), Then = Ref(tn, start, target.NameSpan),
                                Span = TextSpan.Cover(start.Span, LastTok.Span), Shorthand = target,
                            });
                        else if (into.Count > before)
                            into[before].Span = TextSpan.Cover(start.Span, into[before].Span);   // "then include …": keep the 'then' with it
                        return;
                    }
                    var to = ReadRef("a successor");
                    AddSuccession(into, ctx, start, to, null);
                    Terminate(into[^1], start, allowBody: false);
                    return;
                }
                case "if" when !ctx.Expressions:
                {
                    var kwTok = Next();
                    var guard = ReadExpr(true, "then");
                    if (Is("{"))
                    {   // structured "if c { … } else { … }" action
                        ParseActionNode(into, ctx, start, startIndex, leadingId, kwTok, "if", NewDecl("if", "if"));
                        return;
                    }
                    Expect("then", "after the guard");
                    var to = ReadRef("a successor");
                    AddSuccession(into, ctx, start, to, guard, fromDecision: true);
                    Terminate(into[^1], start);
                    return;
                }
                case "else" when !ctx.Expressions:
                {
                    Next();
                    var to = ReadRef("a successor");
                    AddSuccession(into, ctx, start, to, "else", fromDecision: true);
                    Terminate(into[^1], start);
                    return;
                }
                case "entry" or "do" or "exit":
                {
                    var kwTok = Next();
                    if (kw == "entry") ctx.Previous = "start";
                    if (Accept(";")) return;
                    var d = NewDecl(kw, kw);
                    ParseActionNode(into, ctx, start, startIndex, leadingId, kwTok, kw, d, updatePrevious: false);
                    return;
                }
                case "accept" or "send" or "assign" or "while" or "loop" or "for" or "terminate":
                {
                    var kwTok = Next();
                    ParseActionNode(into, ctx, start, startIndex, leadingId, kwTok, kw, NewDecl(kw, kw));
                    return;
                }
                case "require" or "assume" or "frame" or "expose" or "filter" or "until" or "render":
                    Next();
                    SkipUnsupported(into, start, kw);
                    return;
                case "return":
                {
                    Next();
                    var d = Cur.Kind == Tk.Ident && DeclKeywords.Contains(Cur.Text)
                        ? NewDecl("return", "return " + Next().Text)
                        : NewDecl("return", "return");
                    ParseDeclRest(d, start, startIndex, leadingId, into, ctx);
                    return;
                }
                case "decide" or "merge" or "fork" or "join":
                {
                    Next();
                    var d = NewDecl(kw, kw);
                    if (IsName) { var (n, sp) = ReadName("a name"); d.Name = n; d.NameSpan = sp; }
                    else
                    {
                        d.SyntheticName = kw is "decide" or "merge" ? $"decision{++ctx.Decisions}" : $"fork{++ctx.Forks}";
                        d.NameSpan = start.Span;
                    }
                    if (d.Name != null) { if (kw is "decide" or "merge") ctx.Decisions++; else ctx.Forks++; }
                    d.Id = TakeIdBetween(startIndex) ?? leadingId;
                    Terminate(d, start);
                    d.HeaderSpan = d.Span;
                    into.Add(d);
                    ctx.Previous = d.EffectiveName;
                    ctx.LastDecision = kw is "decide" or "merge" ? d.EffectiveName : ctx.LastDecision;
                    return;
                }
                case "use":
                {
                    Next();
                    Expect("case", "after 'use'");
                    var d = NewDecl("use case", prefixKeyword != null ? prefixKeyword + " use case" : "use case");
                    ParseDeclRest(d, start, startIndex, leadingId, into, ctx);
                    return;
                }
                case "analysis" or "verification":
                {
                    Next();
                    var text = Accept("case") ? kw + " case" : kw;
                    var d = NewDecl(kw, prefixKeyword != null ? prefixKeyword + " " + text : text);
                    ParseDeclRest(d, start, startIndex, leadingId, into, ctx);
                    return;
                }
                case "@":
                    ParseAnnotation(into, start);
                    return;
            }

            if (kw != null && (DeclKeywords.Contains(kw) || kw == "ref"))
            {
                Next();
                var k = prefixKeyword != null ? prefixKeyword + " " + kw : kw;
                var d = NewDecl(k, k);
                ParseDeclRest(d, start, startIndex, leadingId, into, ctx);
                return;
            }

            if (prefixKeyword is "snapshot" or "timeslice" or "event" && Cur.Kind is Tk.Ident or Tk.Name)
            {   // "snapshot s : S;" without a usage keyword
                var d = NewDecl(prefixKeyword, prefixKeyword);
                ParseDeclRest(d, start, startIndex, leadingId, into, ctx);
                return;
            }

            if (prefixKeyword != null && (Cur.Kind is Tk.Ident or Tk.Name))
            {   // "perform x;" / "exhibit s;" referencing an existing usage
                var target = ReadRef();
                var rel = new SysmlRelation { Keyword = prefixKeyword, Target = target, Complex = Is("{") || tags.Count > 0 || modifiers.Count > 0 };
                Terminate(rel, start, allowBody: true);
                into.Add(rel);
                return;
            }

            // ":>> mass = 10;" — a redefinition without a keyword
            if (Is(":>>") || Is("redefines"))
            {
                var d = NewDecl("ref", "");
                ParseDeclRest(d, start, startIndex, leadingId, into, ctx);
                return;
            }

            // "in x : Real;" — a directed parameter without a usage keyword
            if (direction != null && Cur.Kind is Tk.Ident or Tk.Name)
            {
                var d = NewDecl("ref", "");
                ParseDeclRest(d, start, startIndex, leadingId, into, ctx);
                return;
            }

            // enum literal shorthand: "red;" inside an enum def
            if (ctx.IsEnum && Cur.Kind is Tk.Ident or Tk.Name)
            {
                var d = NewDecl("enum", "");
                ParseDeclRest(d, start, startIndex, leadingId, into, ctx);
                return;
            }

            // "isMandatory = true;" inside metadata
            if (ctx.IsMetadata && Cur.Kind is Tk.Ident or Tk.Name && PeekTok() is { Kind: Tk.Sym, Text: "=" or ":=" or ":" or ":>" or ";" })
            {
                var d = NewDecl("ref", "");
                ParseDeclRest(d, start, startIndex, leadingId, into, ctx);
                return;
            }

            if (ctx.Expressions && Cur.Kind is not Tk.Eof)
            {
                var text = ReadExpr(false);
                if (text == null) throw Error($"Unexpected {Describe(Cur)}");
                var ex = new SysmlExpression { Text = Dedent(text, start.Start), Span = TextSpan.Cover(start.Span, LastTok.Span) };
                Accept(";");
                into.Add(ex);
                return;
            }

            throw Error(Cur.Kind == Tk.Ident && !StatementKeywords.Contains(Cur.Text)
                ? $"Unexpected {Describe(Cur)}: expected a declaration (part, attribute, port …) or a relationship"
                : $"Unexpected {Describe(Cur)}");
        }

        private void ParseFlow(List<SysmlMember> into, Token start, bool complex)
        {
            Expect("flow", "");
            string? name = null, item = null;
            if (IsName && !Is("of") && !Is("from") && PeekTok() is not { Kind: Tk.Sym, Text: "." or "::" }) name = Next().Text;
            if (Accept(":")) { ReadRef("a flow type"); complex = true; }
            if (Accept("of")) { item = ReadRef("the conveyed item").Text; if (Accept(":")) item = ReadRef("the conveyed item type").Text; }
            Accept("from");
            var a = ReadRef("a flow source");
            Expect("to", "in 'flow … to …'");
            var b = ReadRef("a flow target");
            var rel = new SysmlRelation { Keyword = "flow", Name = name, Source = a, Target = b, Detail = item, Complex = complex || Is("{") };
            Terminate(rel, start, allowBody: true);
            into.Add(rel);
        }

        /// <summary><c>@Safety { isMandatory = true; }</c> / <c>@Safety about x;</c></summary>
        private void ParseAnnotation(List<SysmlMember> into, Token start)
        {
            Next();
            var md = new SysmlMetadata { Type = ReadRef("a metadata definition") };
            if (Accept(":")) { ReadRef("a metadata definition"); md.Complex = true; }
            if (Accept("about")) { ReadRefList("an annotated element"); md.Complex = true; }
            if (Is("{"))
            {
                var open = Next();
                var owner = new SysmlDecl { Keyword = "metadata", HasBody = true };
                ParseBody(owner.Body, new BodyContext(owner), top: false, opener: open);
                foreach (var x in owner.Body)
                {
                    if (x is SysmlDecl { ImplicitKeyword: true, HasBody: false, Types.Count: 0, Value: { } v } d && d.EffectiveName is { } n
                        && (d.Name != null ^ d.Redefines.Count == 1) && d.Modifiers.Count == 0 && d.ValueOp == "=" && !v.Contains('\n'))
                        md.Values.Add((n, v));
                    else md.Complex = true;
                }
            }
            else if (!Accept(";")) throw MissingAfterLast($"Expected ';' or '{{' after the metadata annotation but found {Describe(Cur)}");
            md.Span = TextSpan.Cover(start.Span, LastTok.Span);
            into.Add(md);
        }

        /// <summary>An action-body statement (accept, send, assign, if, while, for, terminate) or a state action (entry, do, exit):
        /// the text after the keyword is kept verbatim in <see cref="SysmlDecl.Raw"/>.</summary>
        private void ParseActionNode(List<SysmlMember> into, BodyContext ctx, Token start, int startIndex, string? leadingId, Token kwTok,
                                     string kw, SysmlDecl d, bool updatePrevious = true)
        {
            var rawEnd = ConsumeRawStatement(kw);
            var raw = _src[kwTok.End..Math.Max(kwTok.End, rawEnd)].Trim();
            d.Raw = raw.Length == 0 ? null : Dedent(raw, kwTok.Start);
            if (updatePrevious)
            {
                ctx.Counters.TryGetValue(kw, out var n);
                ctx.Counters[kw] = ++n;
                if (d.Name == null) d.SyntheticName = kw + n;
            }
            if (d.Name == null) d.NameSpan = kwTok.Span;
            d.Id = TakeIdBetween(startIndex) ?? leadingId;
            d.Span = TextSpan.Cover(start.Span, LastTok.Span);
            d.HeaderSpan = TextSpan.Cover(start.Span, kwTok.Span);
            into.Add(d);
            if (updatePrevious && d.EffectiveName is { } en) ctx.Previous = en;
        }

        /// <summary>Removes the indentation of the line holding offset <paramref name="at"/> from every continuation line of <paramref name="text"/>.</summary>
        private string Dedent(string text, int at)
        {
            var lineStart = at == 0 ? 0 : _src.LastIndexOf('\n', at - 1) + 1;
            var indent = 0;
            while (lineStart + indent < _src.Length && _src[lineStart + indent] is ' ' or '\t') indent++;
            return SysMLv2Parser.Dedent(text, indent);
        }

        private static readonly HashSet<string> RawStops = new(StringComparer.Ordinal)
        {
            "part", "port", "attribute", "item", "action", "state", "first", "then", "transition", "succession", "import", "doc", "comment",
            "accept", "send", "assign", "entry", "exit", "decide", "merge", "fork", "join", "perform", "exhibit", "requirement", "constraint",
        };

        /// <summary>Consumes a statement to its ';' (or the end of its block, continuing over else / until). Returns the end offset of its text.</summary>
        private int ConsumeRawStatement(string kw)
        {
            var depth = 0;
            var consumed = false;
            while (Cur.Kind != Tk.Eof)
            {
                if (depth == 0)
                {
                    if (Is(";")) { var end = Cur.Start; Next(); return end; }
                    if (Is("}")) throw MissingAfterLast($"Expected ';' after '{kw}' but found {Describe(Cur)}");
                    if (consumed && Cur.Kind == Tk.Ident && RawStops.Contains(Cur.Text) && IsStatementStartOnNewLine())
                        throw MissingAfterLast($"Expected ';' after '{kw}' but found {Describe(Cur)}");
                }
                if (Is("{") || Is("(") || Is("["))
                {
                    depth++;
                    Next();
                    consumed = true;
                    continue;
                }
                if (Is("}") || Is(")") || Is("]"))
                {
                    depth--;
                    var t = Next();
                    consumed = true;
                    if (depth == 0 && t.Text == "}")
                    {
                        if (Is("else") || Is("until")) continue;
                        return t.End;
                    }
                    continue;
                }
                Next();
                consumed = true;
            }
            return LastTok.End;
        }

        private SysmlRef Ref(string name, Token at, TextSpan? span = null) =>
            new([name], false, span ?? at.Span) { IsKeyword = name is "start" or "done" };

        private void AddSuccession(List<SysmlMember> into, BodyContext ctx, Token start, SysmlRef to, string? guard, bool fromDecision = false)
        {
            var from = fromDecision ? ctx.LastDecision ?? ctx.Previous : ctx.Previous;
            into.Add(new SysmlSuccession
            {
                IsTransition = ctx.IsState,
                First = from == null ? null : Ref(from, start),
                Then = to,
                Guard = guard,
                Span = TextSpan.Cover(start.Span, LastTok.Span),
            });
            if (!fromDecision) ctx.Previous = to.Simple;
        }

        private void SkipUnsupported(List<SysmlMember> into, Token start, string keyword)
        {
            var u = new SysmlUnsupported { Keyword = keyword };
            var depth = 0;
            while (Cur.Kind != Tk.Eof)
            {
                if (depth == 0 && Is(";")) { Next(); break; }
                if (Is("{")) depth++;
                else if (Is("}"))
                {
                    if (depth == 0) break;
                    depth--;
                    Next();
                    if (depth == 0 && !Is(";")) break;
                    continue;
                }
                Next();
            }
            u.Span = TextSpan.Cover(start.Span, LastTok.Span);
            into.Add(u);
        }

        private void ParseSuccession(List<SysmlMember> into, BodyContext ctx, Token start, string? name, bool transition)
        {
            SysmlRef? from = null;
            if (Accept("first")) from = ReadRef("the source of the succession");
            else if (ctx.Previous != null) from = Ref(ctx.Previous, start);
            string? trigger = null, guard = null, effect = null;
            if (Accept("accept")) trigger = ReadExpr(false, "if", "do", "then");
            if (Accept("if")) guard = ReadExpr(false, "do", "then");
            if (Accept("do")) effect = ReadExpr(false, "then");
            if (!Accept("then"))
            {
                // "first x;" just sets the predecessor for following "then" shorthands
                if (!transition && from != null && trigger == null && guard == null && effect == null && Accept(";"))
                {
                    ctx.Previous = from.Simple;
                    into.Add(new SysmlUnsupported { Keyword = "first", Span = TextSpan.Cover(start.Span, LastTok.Span) });
                    return;
                }
                throw MissingAfterLast($"Expected 'then' in the {(transition ? "transition" : "succession")} but found {Describe(Cur)}");
            }
            var to = ReadRef("the target of the succession");
            if (from == null) throw new ParseError($"The {(transition ? "transition" : "succession")} has no source: write 'first <name>'", start.Span);
            var s = new SysmlSuccession
            {
                IsTransition = transition || ctx.IsState,
                Name = name,
                First = from,
                Then = to,
                Trigger = trigger,
                Guard = guard,
                Effect = effect,
            };
            Terminate(s, start, allowBody: true);
            into.Add(s);
            if (!transition && !ctx.IsState) ctx.Previous = to.Simple;
        }

        // ------------------------------------------------------------ declarations

        private void ParseDeclRest(SysmlDecl d, Token start, int startIndex, string? leadingId, List<SysmlMember> into, BodyContext ctx)
        {
            if (Accept("def")) d.IsDef = true;
            if (d.Keyword == "package" && d.IsDef) throw Error("'package' cannot be followed by 'def'");

            if (Is("<"))
            {
                Next();
                d.ShortName = ReadName("a short name").Name;
                Expect(">", "to close the short name");
            }
            if (IsName)
            {
                var (n, sp) = ReadName("a name");
                d.Name = n;
                d.NameSpan = sp;
            }
            else d.NameSpan = start.Span;
            if (d.Keyword == "package" && d.Name == null) throw MissingAfterLast("Expected a package name");

            while (true)
            {
                if (Is(":") || Is("defined") || Is("typed"))
                {
                    if (!Accept(":")) { Next(); Expect("by", "after 'defined' / 'typed'"); }
                    d.Types.AddRange(ReadRefList("a type"));
                    continue;
                }
                if (Is(":>") || Is("specializes") || Is("subsets"))
                {
                    Next();
                    d.Specializes.AddRange(ReadRefList("a specialized element"));
                    continue;
                }
                if (Is(":>>") || Is("redefines"))
                {
                    Next();
                    d.Redefines.AddRange(ReadRefList("a redefined feature"));
                    continue;
                }
                if (Is("::>") || Is("references")) { Next(); d.References.AddRange(ReadRefList("a referenced feature")); continue; }
                if (Is("["))
                {
                    var open = Next();
                    var depth = 1;
                    var from = Cur.Start;
                    while (Cur.Kind != Tk.Eof && !Is(";") && !Is("{"))
                    {
                        if (Is("[")) depth++;
                        else if (Is("]") && --depth == 0) break;
                        Next();
                    }
                    if (!Is("]")) throw new ParseError("Missing ']' to close the multiplicity", open.Span);
                    d.Multiplicity = string.Join(" ", _src[from..Cur.Start].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    Next();
                    continue;
                }
                if (Is("=") || Is(":=") || Is("default"))
                {
                    var op = Next().Text;
                    if (op == "default" && (Is("=") || Is(":="))) op += " " + Next().Text;
                    d.ValueOp = op;
                    var vstart = Cur;
                    var v = ReadExpr(true) ?? throw MissingAfterLast("Expected a value after '='");
                    d.Value = Dedent(v, vstart.Start);
                    d.ValueSpan = TextSpan.Cover(vstart.Span, LastTok.Span);
                    continue;
                }
                if (Is("connect") && !d.IsDef)
                {
                    Next();
                    if (Is("(")) { d.OpaqueHeader = true; ReadExpr(true); continue; }
                    var a = ReadRef("a connector end");
                    Expect("to", "in 'connect … to …'");
                    d.Connect = (a, ReadRef("a connector end"));
                    continue;
                }
                if (Is("parallel") || Is("ordered") || Is("nonunique")) { d.Flags.Add(Next().Text); continue; }
                if (Is("allocate") || Is("from") || Is("of") || Is("about") || Is("via") || Is("to") || Is("chains") || Is("inverse")
                    || Is("crosses") || Is("=>") || Is("conjugates") || Is("~"))
                {   // allocation / flow usages, annotations …: the declaration is kept verbatim
                    Next();
                    ReadExpr(true, "to", "from", "via", "of", "about");
                    d.OpaqueHeader = true;
                    continue;
                }
                break;
            }

            // "action trigger accept s : Signal via port;" — a named action-body statement
            if (d.Keyword == "action" && !d.IsDef && Cur.Kind == Tk.Ident && ActionBodyKeywords.Contains(Cur.Text) && d.Types.Count == 0)
            {
                var kwTok = Next();
                d.Keyword = d.KeywordText = kwTok.Text;
                ParseActionNode(into, ctx, start, startIndex, leadingId, kwTok, kwTok.Text, d);
                return;
            }

            d.Id = TakeIdBetween(startIndex) ?? leadingId;
            d.HeaderSpan = TextSpan.Cover(start.Span, LastTok.Span);
            if (Is("{"))
            {
                var open = Next();
                d.HasBody = true;
                into.Add(d);
                UpdatePrevious(d, ctx);
                ParseBody(d.Body, new BodyContext(d), top: false, opener: open);
                d.Span = TextSpan.Cover(start.Span, LastTok.Span);
                return;
            }
            if (!Accept(";"))
            {
                var what = d.Name != null ? $"'{d.Keyword}{(d.IsDef ? " def" : "")} {d.Name}'" : $"'{d.Keyword}'";
                var err = MissingAfterLast($"Expected ';' or '{{' after {what} but found {Describe(Cur)}");
                // Missing ';' before a new statement: keep the declaration and report the error.
                if (IsStatementStartOnNewLine() || Is("}"))
                {
                    AddDiag(SysmlSeverity.Error, err.Message, err.Span);
                    d.Span = TextSpan.Cover(start.Span, LastTok.Span);
                    into.Add(d);
                    UpdatePrevious(d, ctx);
                    return;
                }
                throw err;
            }
            d.Span = TextSpan.Cover(start.Span, LastTok.Span);
            into.Add(d);
            UpdatePrevious(d, ctx);
        }

        private static void UpdatePrevious(SysmlDecl d, BodyContext ctx)
        {
            if (!d.IsDef && d.Keyword is "action" or "state" or "perform action" or "exhibit state" && d.EffectiveName is { } n) ctx.Previous = n;
        }
    }

    /// <summary>Removes up to <paramref name="indent"/> leading spaces / tabs from every line after the first.</summary>
    internal static string Dedent(string text, int indent)
    {
        if (!text.Contains('\n')) return text;
        var lines = text.Replace("\r", "").Split('\n');
        for (var k = 1; k < lines.Length; k++)
        {
            var n = 0;
            while (n < indent && n < lines[k].Length && lines[k][n] is ' ' or '\t') n++;
            lines[k] = lines[k][n..].TrimEnd();
        }
        return string.Join("\n", lines);
    }

    private static SysmlDecl With(this SysmlDecl d, IEnumerable<string> tags, IEnumerable<string> modifiers)
    {
        d.Tags.AddRange(tags);
        d.Modifiers.AddRange(modifiers);
        return d;
    }
}
