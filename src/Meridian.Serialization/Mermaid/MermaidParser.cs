using System.Text.RegularExpressions;

namespace Meridian.Serialization.Mermaid;

/// <summary>
/// Hand-written parser for the Mermaid diagram types Meridian imports. Every method is total: bad input produces
/// warnings, never exceptions. Statements beyond <see cref="MermaidLimits"/> are dropped with a warning.
/// </summary>
internal static partial class MermaidParser
{
    public static MermaidDiagramKind KindOf(string keyword) => keyword switch
    {
        "flowchart" or "graph" => MermaidDiagramKind.Flowchart,
        "classDiagram" or "classDiagram-v2" => MermaidDiagramKind.Class,
        "stateDiagram" or "stateDiagram-v2" => MermaidDiagramKind.State,
        "sequenceDiagram" => MermaidDiagramKind.Sequence,
        "requirementDiagram" => MermaidDiagramKind.Requirement,
        "erDiagram" => MermaidDiagramKind.EntityRelationship,
        "C4Context" or "C4Container" or "C4Component" => MermaidDiagramKind.C4,
        _ => MermaidDiagramKind.Unsupported,
    };

    public static MGraph Parse(MermaidBlock block)
    {
        var g = new MGraph { Kind = KindOf(block.Keyword), Keyword = block.Keyword, Line = block.Line, Title = block.Title };
        try
        {
            switch (g.Kind)
            {
                case MermaidDiagramKind.Flowchart: Flowchart(g, block); break;
                case MermaidDiagramKind.State: State(g, block); break;
                case MermaidDiagramKind.Class: Class(g, block); break;
                case MermaidDiagramKind.Sequence: Sequence(g, block); break;
                case MermaidDiagramKind.Requirement: Requirement(g, block); break;
                case MermaidDiagramKind.EntityRelationship: Er(g, block); break;
                case MermaidDiagramKind.C4: C4(g, block); break;
                default:
                    g.Warnings.Add($"{block.Keyword} (line {block.Line}): this Mermaid diagram type has no SysML equivalent and was skipped.");
                    break;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException or FormatException or RegexMatchTimeoutException)
        {
            // Defensive: a parser bug must not stop the import of the other diagrams.
            g.Warnings.Add($"{g.Describe()} could not be read completely ({ex.Message}).");
        }
        return g;
    }

    /// <summary>The statements of a block (lines split on top-level ';' where the type allows it), capped at the statement limit.</summary>
    private static IEnumerable<MLine> Statements(MGraph g, MermaidBlock block, bool splitSemicolons)
    {
        var count = 0;
        foreach (var line in block.Body)
        {
            var parts = splitSemicolons ? MermaidSource.SplitTop(line.Text, ';') : [line.Text];
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length == 0) continue;
                if (++count > MermaidLimits.MaxStatements)
                {
                    g.Warn(line.No, $"more than {MermaidLimits.MaxStatements} statements; the rest were ignored.");
                    yield break;
                }
                yield return new MLine(line.No, t);
            }
        }
    }

    private static bool IsStyling(string t) =>
        Word(t, "classDef") || Word(t, "class") || Word(t, "style") || Word(t, "linkStyle") || Word(t, "cssClass") || Word(t, "click")
        || Word(t, "callback") || Word(t, "link") || Word(t, "links") || Word(t, "autonumber") || Word(t, "hide") || Word(t, "scale")
        || Word(t, "UpdateElementStyle") || Word(t, "UpdateRelStyle") || Word(t, "UpdateBoundaryStyle") || Word(t, "UpdateLayoutConfig");

    private static void Styling(MGraph g, MLine s) =>
        g.WarnOnce("style", s.No, "styling, class and click statements (classDef, class, style, linkStyle, click, autonumber…) are not imported.");

    /// <summary>Does <paramref name="t"/> start with the keyword <paramref name="w"/> followed by a space or the end?</summary>
    private static bool Word(string t, string w) =>
        t.StartsWith(w, StringComparison.Ordinal) && (t.Length == w.Length || char.IsWhiteSpace(t[w.Length]));

    private static string AfterWord(string t, string w) => t.Length > w.Length ? t[w.Length..].Trim() : "";

    private static string? DirectionOf(string s) => s.Trim().ToUpperInvariant() switch
    {
        "TB" or "TD" => "TB",
        "BT" => "BT",
        "LR" => "LR",
        "RL" => "RL",
        _ => null,
    };

    // ================================================================== flowchart

    private static readonly Regex LinkWithText = new(
        @"\G\s*(?<start>[<xo]?)(?<l>--|==|-\.)(?!-|=|\.|>)\s*(?<text>""[^""]*""|[^\-=.>|][^\n]*?)\s*(?<r>-{2,}|={2,}|\.+-)(?<head>[>xo]?)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex LinkPlain = new(
        @"\G\s*(?<start>[<xo]?)(?<body>-{2,}|={2,}|-\.+-|~{3,})(?<head>[>xo]?)(?:\s*\|(?<pipe>[^|]*)\|)?",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly (string Open, string Close, string Shape)[] Shapes =
    [
        ("(((", ")))", "dcircle"), ("((", "))", "circle"), ("([", "])", "stadium"), ("[[", "]]", "subroutine"), ("[(", ")]", "db"),
        ("[/", "/]", "para"), ("[\\", "\\]", "para"), ("[/", "\\]", "trap"), ("[\\", "/]", "trap"), ("{{", "}}", "hex"),
        ("(", ")", "round"), ("[", "]", "rect"), ("{", "}", "diamond"), (">", "]", "asym"),
    ];

    private static void Flowchart(MGraph g, MermaidBlock block)
    {
        g.Direction = DirectionOf(block.HeaderRest) ?? "TB";
        var groups = new Stack<MGroup>();
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in Statements(g, block, splitSemicolons: true))
        {
            var t = s.Text;
            if (Word(t, "subgraph"))
            {
                if (groups.Count >= MermaidLimits.MaxDepth)
                {
                    g.WarnOnce("depth", s.No, $"subgraphs nested deeper than {MermaidLimits.MaxDepth} levels were flattened.");
                    groups.Push(groups.Peek());
                    continue;
                }
                var (id, label) = SubgraphHeader(AfterWord(t, "subgraph"), g.Groups.Count + 1);
                var grp = g.Groups.FirstOrDefault(x => x.Id == id);
                if (grp == null)
                {
                    grp = new MGroup { Id = id, Label = label, Parent = groups.Count > 0 ? groups.Peek().Id : null };
                    g.Groups.Add(grp);
                    groupIds.Add(id);
                }
                groups.Push(grp);
                continue;
            }
            if (t == "end" || Word(t, "end"))
            {
                if (groups.Count > 0) groups.Pop();
                else g.Warn(s.No, "“end” without a matching subgraph was ignored.");
                continue;
            }
            if (Word(t, "direction"))
            {
                if (groups.Count == 0 && DirectionOf(AfterWord(t, "direction")) is { } d) g.Direction = d;
                else if (groups.Count > 0) g.WarnOnce("subdir", s.No, "a direction inside a subgraph is not imported (the whole diagram follows one direction).");
                continue;
            }
            if (IsStyling(t)) { Styling(g, s); continue; }
            if (t.StartsWith("title ", StringComparison.Ordinal)) { g.Title ??= MermaidSource.Name(t[6..]); continue; }
            FlowChain(g, s, groups.Count > 0 ? groups.Peek().Id : null);
        }
        if (groups.Count > 0) g.Warn(block.Line, $"{groups.Count} subgraph(s) were not closed with “end”.");
        foreach (var n in g.Nodes.Where(n => groupIds.Contains(n.Id) && !n.Declared)) n.Shape = "subgraph";
    }

    private static (string Id, string Label) SubgraphHeader(string rest, int index)
    {
        rest = rest.Trim();
        if (rest.Length == 0) return ("subgraph" + index, "Group " + index);
        if (rest[0] == '"') return (MermaidSource.Name(rest), MermaidSource.Name(rest));
        var b = rest.IndexOf('[');
        if (b > 0 && rest.EndsWith(']'))
            return (rest[..b].Trim(), MermaidSource.Name(rest[(b + 1)..^1]));
        var sp = rest.IndexOf(' ');
        return sp < 0 ? (rest, rest) : (rest, MermaidSource.Name(rest));   // "subgraph Two words": the id is the whole title
    }

    /// <summary>A statement of node groups joined by links: <c>A &amp; B --&gt;|yes| C --&gt; D</c>.</summary>
    private static void FlowChain(MGraph g, MLine s, string? group)
    {
        var t = s.Text;
        var pos = 0;
        var left = NodeGroup(g, t, ref pos, s.No, group);
        if (left == null)
        {
            g.Warn(s.No, $"could not read “{Trunc(t)}”; it was skipped.");
            return;
        }
        while (true)
        {
            SkipSpace(t, ref pos);
            if (pos >= t.Length) return;
            var link = Link(t, pos);
            if (link == null)
            {
                g.Warn(s.No, $"unexpected “{Trunc(t[pos..])}”; the rest of the statement was skipped.");
                return;
            }
            pos = link.Value.End;
            var right = NodeGroup(g, t, ref pos, s.No, group);
            if (right == null)
            {
                g.Warn(s.No, $"a link in “{Trunc(t)}” has no target; it was skipped.");
                return;
            }
            if (link.Value.Kind == "invisible")
                g.WarnOnce("invisible", s.No, "invisible links (~~~) only affect Mermaid's layout and were not imported.");
            else
                foreach (var a in left)
                    foreach (var b in right)
                    {
                        if (g.Edge(a.Id, b.Id, s.No) is not { } e) return;
                        e.Kind = link.Value.Kind;
                        e.Label = link.Value.Text;
                        e.Bidirectional = link.Value.Both;
                    }
            left = right;
        }
    }

    private readonly record struct LinkMatch(int End, string Kind, string Text, bool Both);

    private static LinkMatch? Link(string t, int pos)
    {
        var m = LinkWithText.Match(t, pos);
        if (m.Success && m.Groups["r"].Value.Length > 0)
        {
            var l = m.Groups["l"].Value;
            return new LinkMatch(m.Index + m.Length, l == "==" ? "thick" : l == "-." ? "dotted" : "flow", MermaidSource.Clean(m.Groups["text"].Value),
                m.Groups["start"].Value == "<" && m.Groups["head"].Value == ">");
        }
        m = LinkPlain.Match(t, pos);
        if (!m.Success) return null;
        var body = m.Groups["body"].Value;
        var kind = body.StartsWith('~') ? "invisible" : body.Contains('.') ? "dotted" : body.StartsWith('=') ? "thick" : "flow";
        return new LinkMatch(m.Index + m.Length, kind, m.Groups["pipe"].Success ? MermaidSource.Clean(m.Groups["pipe"].Value) : "",
            m.Groups["start"].Value == "<" && m.Groups["head"].Value == ">");
    }

    private static void SkipSpace(string t, ref int pos)
    {
        while (pos < t.Length && char.IsWhiteSpace(t[pos])) pos++;
    }

    /// <summary><c>A</c>, <c>A[text] &amp; B{text}</c> … Returns null when no node could be read.</summary>
    private static List<MNode>? NodeGroup(MGraph g, string t, ref int pos, int line, string? group)
    {
        var list = new List<MNode>();
        while (true)
        {
            SkipSpace(t, ref pos);
            var n = FlowNode(g, t, ref pos, line, group);
            if (n == null) return list.Count > 0 ? list : null;
            list.Add(n);
            SkipSpace(t, ref pos);
            if (pos < t.Length && t[pos] == '&') { pos++; continue; }
            return list;
        }
    }

    private static MNode? FlowNode(MGraph g, string t, ref int pos, int line, string? group)
    {
        var start = pos;
        while (pos < t.Length)
        {
            var c = t[pos];
            if (char.IsWhiteSpace(c) || c is '[' or '(' or '{' or '&' or ';' or '|' or '"') break;
            if (c == '>' && pos > start) break;
            if (c == '@' && pos + 1 < t.Length && t[pos + 1] == '{') break;
            if (c is '-' or '=' or '~' or '.' or '<' && pos > start && Link(t, pos) != null) break;
            if (c == ':' && pos + 2 < t.Length && t[pos + 1] == ':' && t[pos + 2] == ':') break;
            pos++;
        }
        var id = t[start..pos];
        if (id.Length == 0) return null;
        var node = g.Node(id, line);
        if (node == null) { SkipShape(t, ref pos); return new MNode { Id = id }; }
        if (group != null && node.Group == null && !g.Groups.Any(x => x.Id == id)) node.Group = group;

        if (pos < t.Length && t[pos] == '@' && pos + 1 < t.Length && t[pos + 1] == '{')
        {
            var close = t.IndexOf('}', pos);
            if (close < 0) close = t.Length - 1;
            ShapeData(node, t[(pos + 2)..close]);
            pos = close + 1;
        }
        else if (pos < t.Length && t[pos] is '[' or '(' or '{' or '>')
        {
            foreach (var (open, closeStr, shape) in Shapes)
            {
                if (string.CompareOrdinal(t, pos, open, 0, open.Length) != 0) continue;
                var bodyStart = pos + open.Length;
                var end = FindClose(t, bodyStart, closeStr);
                if (end < 0) continue;
                node.Label = MermaidSource.Clean(t[bodyStart..end]);
                node.Shape = shape;
                node.Declared = true;
                pos = end + closeStr.Length;
                break;
            }
        }
        if (pos + 2 < t.Length && t[pos] == ':' && t[pos + 1] == ':' && t[pos + 2] == ':')
        {
            pos += 3;
            while (pos < t.Length && !char.IsWhiteSpace(t[pos]) && t[pos] is not ('&' or '-' or '=' or '.' or '~' or '<')) pos++;
        }
        return node;
    }

    private static void SkipShape(string t, ref int pos)
    {
        if (pos >= t.Length || t[pos] is not ('[' or '(' or '{' or '>' or '@')) return;
        foreach (var (open, close, _) in Shapes)
            if (string.CompareOrdinal(t, pos, open, 0, open.Length) == 0 && FindClose(t, pos + open.Length, close) is var end && end >= 0)
            {
                pos = end + close.Length;
                return;
            }
    }

    /// <summary>Index of <paramref name="close"/> after <paramref name="from"/>, skipping a quoted label.</summary>
    private static int FindClose(string t, int from, string close)
    {
        var i = from;
        while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
        if (i < t.Length && t[i] == '"')
        {
            var q = t.IndexOf('"', i + 1);
            if (q > 0) return t.IndexOf(close, q + 1, StringComparison.Ordinal);
        }
        return t.IndexOf(close, from, StringComparison.Ordinal);
    }

    /// <summary>Mermaid 11 shape data: <c>A@{ shape: diamond, label: "Ok?" }</c>.</summary>
    private static void ShapeData(MNode node, string data)
    {
        node.Declared = true;
        foreach (var part in MermaidSource.SplitTop(data, ','))
        {
            var c = part.IndexOf(':');
            if (c <= 0) continue;
            var key = part[..c].Trim();
            var value = MermaidSource.Unquote(part[(c + 1)..].Trim());
            if (key == "label") node.Label = MermaidSource.Clean(value);
            else if (key == "shape")
                node.Shape = value.ToLowerInvariant() switch
                {
                    "diamond" or "decision" or "diam" or "question" => "diamond",
                    "circle" or "circ" => "circle",
                    "sm-circ" or "small-circle" or "start" => "initial",
                    "fr-circ" or "framed-circle" or "stop" or "f-circ" or "filled-circle" or "junction" => "final",
                    "dbl-circ" or "double-circle" => "dcircle",
                    "cyl" or "cylinder" or "database" or "db" or "lin-cyl" or "disk" or "lined-cylinder" => "db",
                    "h-cyl" or "das" or "horizontal-cylinder" => "buffer",
                    "doc" or "document" => "object",
                    "fork" or "join" => "fork",
                    "hex" or "hexagon" or "prepare" => "hex",
                    "rounded" or "event" => "round",
                    "stadium" or "pill" or "terminal" => "stadium",
                    _ => "rect",
                };
        }
    }

    private static string Trunc(string s) => s.Length > 60 ? s[..57] + "…" : s;

    // ================================================================== state diagrams

    private static readonly Regex StateDecl = new(@"^state\s+(?:""(?<label>[^""]*)""\s+as\s+(?<id>[^\s{:]+)|(?<id2>[^\s{:""]+)(?:\s+as\s+""?(?<label2>[^""{]*)""?)?)\s*(?<rest>.*)$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static void State(MGraph g, MermaidBlock block)
    {
        var scopes = new Stack<string>();   // composite state ids
        string Scope() => scopes.Count > 0 ? scopes.Peek() : "";
        using var it = Statements(g, block, splitSemicolons: false).GetEnumerator();   // entry / effect code often holds ';' 
        while (it.MoveNext())
        {
            var s = it.Current;
            var t = s.Text;
            if (t == "}")
            {
                if (scopes.Count > 0) scopes.Pop();
                else g.Warn(s.No, "“}” without an open composite state was ignored.");
                continue;
            }
            if (t == "--")
            {
                g.WarnOnce("regions", s.No, "concurrent regions (--) are imported as one region of the composite state.");
                continue;
            }
            if (Word(t, "direction"))
            {
                if (scopes.Count == 0 && DirectionOf(AfterWord(t, "direction")) is { } d) g.Direction = d;
                continue;
            }
            if (IsStyling(t)) { Styling(g, s); continue; }
            if (Word(t, "note"))
            {
                StateNote(g, s, it);
                continue;
            }
            if (Word(t, "state"))
            {
                var m = StateDecl.Match(t);
                if (!m.Success) { g.Warn(s.No, $"could not read “{Trunc(t)}”."); continue; }
                var id = m.Groups["id"].Success ? m.Groups["id"].Value : m.Groups["id2"].Value;
                var label = m.Groups["label"].Success ? m.Groups["label"].Value : m.Groups["label2"].Success ? m.Groups["label2"].Value.Trim() : null;
                var rest = m.Groups["rest"].Value.Trim();
                var cls = id.IndexOf(":::", StringComparison.Ordinal);
                if (cls > 0) { id = id[..cls]; Styling(g, s); }
                if (StateNode(g, id, s.No, Scope()) is not { } n) continue;
                n.Declared = true;
                if (label != null) n.Label = MermaidSource.Name(label);
                if (rest.StartsWith("<<", StringComparison.Ordinal))
                {
                    var kind = rest.Trim('<', '>', ' ').ToLowerInvariant();
                    n.Shape = kind is "choice" or "fork" or "join" or "history" or "deephistory" ? kind : "";
                    if (n.Shape.Length == 0) g.Warn(s.No, $"«{kind}» is not a known state kind and was ignored.");
                    rest = rest[(rest.IndexOf(">>", StringComparison.Ordinal) is var x && x > 0 ? x + 2 : rest.Length)..].Trim();
                }
                if (rest.StartsWith(':')) StateDescription(n, rest[1..]);
                else if (rest.StartsWith('{'))
                {
                    if (scopes.Count >= MermaidLimits.MaxDepth)
                        g.WarnOnce("depth", s.No, $"composite states nested deeper than {MermaidLimits.MaxDepth} levels were flattened.");
                    else
                    {
                        n.Shape = "composite";
                        scopes.Push(id);
                    }
                }
                continue;
            }
            var arrow = t.IndexOf("-->", StringComparison.Ordinal);
            if (arrow > 0)
            {
                var from = t[..arrow].Trim();
                var rest = t[(arrow + 3)..];
                var colon = rest.IndexOf(':');
                var to = (colon >= 0 ? rest[..colon] : rest).Trim();
                var label = colon >= 0 ? rest[(colon + 1)..].Trim() : "";
                from = StripClass(g, s, from);
                to = StripClass(g, s, to);
                if (from.Length == 0 || to.Length == 0) { g.Warn(s.No, $"could not read the transition “{Trunc(t)}”."); continue; }
                var a = from == "[*]" ? Pseudo(g, "initial", Scope(), s.No) : StateNode(g, from, s.No, Scope());
                var b = to == "[*]" ? Pseudo(g, "final", Scope(), s.No) : StateNode(g, to, s.No, Scope());
                if (a == null || b == null) continue;
                if (g.Edge(a.Id, b.Id, s.No) is { } e) { e.Kind = "transition"; e.Label = MermaidSource.Name(label); }
                continue;
            }
            var c2 = t.IndexOf(':');
            if (c2 > 0)
            {
                var id = StripClass(g, s, t[..c2].Trim());
                if (id.Length > 0 && !id.Contains(' ') && StateNode(g, id, s.No, Scope()) is { } n) { n.Declared = true; StateDescription(n, t[(c2 + 1)..]); }
                else g.Warn(s.No, $"could not read “{Trunc(t)}”.");
                continue;
            }
            var bare = StripClass(g, s, t);
            if (!bare.Contains(' ') && bare != "[*]" && StateNode(g, bare, s.No, Scope()) is { } bn) { bn.Declared = true; continue; }
            g.Warn(s.No, $"could not read “{Trunc(t)}”; it was skipped.");
        }
        if (scopes.Count > 0) g.Warn(block.Line, $"{scopes.Count} composite state(s) were not closed with “}}”.");
    }

    private static string StripClass(MGraph g, MLine s, string id)
    {
        var cls = id.IndexOf(":::", StringComparison.Ordinal);
        if (cls < 0) return id;
        Styling(g, s);
        return id[..cls].Trim();
    }

    private static MNode? StateNode(MGraph g, string id, int line, string scope)
    {
        var known = g.ById.ContainsKey(id);
        var n = g.Node(id, line);
        if (n == null) return null;
        if (!known && scope.Length > 0 && id != scope) n.Parent = scope;
        return n;
    }

    private static MNode? Pseudo(MGraph g, string kind, string scope, int line)
    {
        var n = g.Node($"[*]{kind}:{scope}", line);
        if (n == null) return null;
        n.Shape = kind;
        n.Label = "";
        n.Parent = scope.Length > 0 ? scope : null;
        return n;
    }

    /// <summary>"entry / x", "exit / y", "do / z" lines go to the matching fields; anything else is description.</summary>
    private static void StateDescription(MNode n, string text)
    {
        var d = MermaidSource.Clean(text);
        foreach (var line in d.Split('\n'))
        {
            var l = line.Trim();
            var slash = l.IndexOf('/');
            var key = slash > 0 ? l[..slash].Trim().ToLowerInvariant() : "";
            if (key is "entry" or "exit" or "do") n.Props[key] = l[(slash + 1)..].Trim();
            else if (l.Length > 0) n.Props["description"] = n.Props.TryGetValue("description", out var old) ? old + "\n" + l : l;
        }
    }

    private static void StateNote(MGraph g, MLine s, IEnumerator<MLine> it)
    {
        var t = AfterWord(s.Text, "note");
        var m = Regex.Match(t, @"^(?:(?:right|left)\s+of|over)\s+(?<target>[^\s:]+)\s*(?::\s*(?<text>.*))?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!m.Success)
        {
            // Floating note: note "text" as N1
            var q = Regex.Match(t, @"^""(?<text>[^""]*)""", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (q.Success) g.Notes.Add(new MNote { Text = MermaidSource.Clean(q.Groups["text"].Value) });
            else g.Warn(s.No, $"could not read the note “{Trunc(s.Text)}”.");
            return;
        }
        string text;
        if (m.Groups["text"].Success) text = m.Groups["text"].Value;
        else
        {
            var lines = new List<string>();
            while (it.MoveNext())
            {
                if (it.Current.Text == "end note" || it.Current.Text == "endnote") break;
                if (lines.Count < 200) lines.Add(it.Current.Text);
            }
            text = string.Join("\n", lines);
        }
        g.Notes.Add(new MNote { Target = m.Groups["target"].Value, Text = MermaidSource.Clean(text) });
    }
}
