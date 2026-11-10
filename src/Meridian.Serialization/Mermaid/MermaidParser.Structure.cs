using System.Text.RegularExpressions;

namespace Meridian.Serialization.Mermaid;

/// <summary>Class, entity-relationship, requirement and C4 diagrams.</summary>
internal static partial class MermaidParser
{
    // ================================================================== class diagrams

    private static readonly Regex ClassRel = new(
        @"^(?<a>[^\s""]+)\s+(?:""(?<ca>[^""]*)""\s*)?(?<lm><\||\*|o|<)?(?<line>--|\.\.)(?<rm>\|>|\*|o|>)?\s+(?:""(?<cb>[^""]*)""\s+)?(?<b>[^\s:""]+)\s*(?::(?<label>.*))?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex ClassDecl = new(
        @"^class\s+(?<id>[^\s{\[""~:<]+)(?<gen>~[^~]*~)?(?:\[""?(?<label>[^""\]]*)""?\])?(?<cls>:::\S+)?\s*(?<ann><<[^>]*>>)?\s*(?<open>\{)?\s*(?<inline>.*)$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static void Class(MGraph g, MermaidBlock block)
    {
        MNode? openClass = null;
        var namespaces = new Stack<string>();
        foreach (var s in Statements(g, block, splitSemicolons: false))
        {
            var t = s.Text;
            if (openClass != null)
            {
                if (t == "}") { openClass = null; continue; }
                var close = t.EndsWith('}') && !t.Contains('{');
                var member = close ? t[..^1].Trim() : t;
                if (member.Length > 0) ClassMember(g, openClass, member, s.No);
                if (close) openClass = null;
                continue;
            }
            if (t == "}")
            {
                if (namespaces.Count > 0) namespaces.Pop();
                else g.Warn(s.No, "“}” without an open class or namespace was ignored.");
                continue;
            }
            if (Word(t, "direction")) { if (DirectionOf(AfterWord(t, "direction")) is { } d) g.Direction = d; continue; }
            if (Word(t, "namespace"))
            {
                var name = AfterWord(t, "namespace").TrimEnd('{').Trim();
                if (namespaces.Count >= MermaidLimits.MaxDepth) { g.WarnOnce("depth", s.No, "namespaces nested too deeply were flattened."); namespaces.Push(namespaces.Peek()); continue; }
                var id = MermaidSource.Name(name);
                if (!g.Groups.Any(x => x.Id == id)) g.Groups.Add(new MGroup { Id = id, Label = id, Parent = namespaces.Count > 0 ? namespaces.Peek() : null });
                namespaces.Push(id);
                continue;
            }
            if (Word(t, "note"))
            {
                var m = Regex.Match(AfterWord(t, "note"), @"^(?:for\s+(?<target>\S+)\s+)?""(?<text>.*)""\s*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                if (m.Success) g.Notes.Add(new MNote { Target = m.Groups["target"].Success ? StripGeneric(m.Groups["target"].Value) : null, Text = MermaidSource.Clean(m.Groups["text"].Value.Replace("\\n", "\n")) });
                else g.Warn(s.No, $"could not read the note “{Trunc(t)}”.");
                continue;
            }
            if (IsStyling(t) && !Word(t, "class")) { Styling(g, s); continue; }
            if (t.StartsWith("<<", StringComparison.Ordinal))
            {
                var end = t.IndexOf(">>", StringComparison.Ordinal);
                if (end > 0 && ClassNode(g, StripGeneric(t[(end + 2)..].Trim()), s.No, namespaces) is { } an)
                    an.Props["annotation"] = t[2..end].Trim().ToLowerInvariant();
                else g.Warn(s.No, $"could not read “{Trunc(t)}”.");
                continue;
            }
            if (Word(t, "class"))
            {
                var m = ClassDecl.Match(t);
                if (!m.Success) { g.Warn(s.No, $"could not read “{Trunc(t)}”."); continue; }
                if (ClassNode(g, m.Groups["id"].Value, s.No, namespaces) is not { } n) continue;
                n.Declared = true;
                var gen = m.Groups["gen"].Success ? "<" + m.Groups["gen"].Value.Trim('~') + ">" : "";
                if (m.Groups["label"].Success) n.Label = MermaidSource.Name(m.Groups["label"].Value);
                else if (gen.Length > 0) n.Label = n.Id + gen;
                if (m.Groups["cls"].Success) Styling(g, s);
                if (m.Groups["ann"].Success) n.Props["annotation"] = m.Groups["ann"].Value.Trim('<', '>', ' ').ToLowerInvariant();
                if (m.Groups["open"].Success)
                {
                    var inline = m.Groups["inline"].Value.Trim();
                    if (inline.EndsWith('}'))
                    {
                        foreach (var part in inline[..^1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) ClassMember(g, n, part, s.No);
                    }
                    else
                    {
                        if (inline.Length > 0) ClassMember(g, n, inline, s.No);
                        openClass = n;
                    }
                }
                continue;
            }
            var r = ClassRel.Match(t);
            if (r.Success)
            {
                ClassRelationship(g, r, s, namespaces);
                continue;
            }
            var colon = t.IndexOf(':');
            if (colon > 0 && !t[..colon].Trim().Contains(' '))
            {
                if (ClassNode(g, StripGeneric(t[..colon].Trim()), s.No, namespaces) is { } n)
                {
                    var member = t[(colon + 1)..].Trim();
                    if (member.StartsWith("<<", StringComparison.Ordinal)) n.Props["annotation"] = member.Trim('<', '>', ' ').ToLowerInvariant();
                    else if (member.Length > 0) ClassMember(g, n, member, s.No);
                }
                continue;
            }
            if (!t.Contains(' ') && ClassNode(g, StripGeneric(t), s.No, namespaces) is { } bare) { bare.Declared = true; continue; }
            g.Warn(s.No, $"could not read “{Trunc(t)}”; it was skipped.");
        }
        if (openClass != null) g.Warn(block.Line, $"the body of class {openClass.Id} was not closed with “}}”.");
    }

    private static string StripGeneric(string id)
    {
        var i = id.IndexOf('~');
        return i > 0 ? id[..i] : id;
    }

    private static MNode? ClassNode(MGraph g, string id, int line, Stack<string> namespaces)
    {
        if (id.Length == 0) return null;
        var known = g.ById.ContainsKey(id);
        var n = g.Node(id, line);
        if (n != null && !known && namespaces.Count > 0) n.Group = namespaces.Peek();
        return n;
    }

    private static void ClassMember(MGraph g, MNode n, string member, int line)
    {
        member = member.Trim();
        if (member.StartsWith("<<", StringComparison.Ordinal))
        {
            n.Props["annotation"] = member.Trim('<', '>', ' ').ToLowerInvariant();
            return;
        }
        if (n.Members.Count >= 500) { g.WarnOnce("members:" + n.Id, line, $"class {n.Id} has more than 500 members; the rest were ignored."); return; }
        n.Members.Add(member);
    }

    private static void ClassRelationship(MGraph g, Match r, MLine s, Stack<string> namespaces)
    {
        var a = StripGeneric(r.Groups["a"].Value);
        var b = StripGeneric(r.Groups["b"].Value);
        var lm = r.Groups["lm"].Value;
        var rm = r.Groups["rm"].Value;
        var dashed = r.Groups["line"].Value == "..";
        if (lm.Length > 0 && rm.Length > 0) g.Warn(s.No, $"two-way relationship “{Trunc(s.Text)}” imported as its left end only.");
        string kind;
        bool reverse;   // true: the relationship goes from b to a
        var marker = lm.Length > 0 ? lm : rm;
        var atLeft = lm.Length > 0;
        switch (marker)
        {
            case "<|" or "|>": kind = dashed ? "realization" : "generalization"; reverse = atLeft; break;   // specific -> general
            case "*": kind = "composition"; reverse = !atLeft; break;                                       // whole -> part
            case "o": kind = "aggregation"; reverse = !atLeft; break;
            case "<" or ">": kind = dashed ? "dependency" : "association"; reverse = atLeft; break;          // source -> arrow end
            default: kind = dashed ? "dependency" : "link"; reverse = false; break;
        }
        if (ClassNode(g, a, s.No, namespaces) is null || ClassNode(g, b, s.No, namespaces) is null) return;
        var (from, to, fc, tc) = reverse ? (b, a, r.Groups["cb"].Value, r.Groups["ca"].Value) : (a, b, r.Groups["ca"].Value, r.Groups["cb"].Value);
        if (g.Edge(from, to, s.No) is not { } e) return;
        e.Kind = kind;
        e.FromCard = fc.Trim();
        e.ToCard = tc.Trim();
        e.Label = r.Groups["label"].Success ? MermaidSource.Name(r.Groups["label"].Value) : "";
    }

    // ================================================================== ER diagrams

    private static readonly Regex ErRel = new(
        @"^(?<a>""[^""]+""|[^\s""]+?)\s*(?<l>\|o|\|\||\}o|\}\||o\||o\{|\|\{)(?<line>--|\.\.)(?<r>o\||\|\||o\{|\|\{|\}o|\}\|)\s*(?<b>""[^""]+""|[^\s:""]+)\s*(?::\s*(?<label>.*))?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static string ErCard(string marker) => marker switch
    {
        "|o" or "o|" => "0..1",
        "||" => "1",
        "}o" or "o{" => "0..*",
        "}|" or "|{" => "1..*",
        _ => "",
    };

    private static void Er(MGraph g, MermaidBlock block)
    {
        MNode? open = null;
        foreach (var s in Statements(g, block, splitSemicolons: false))
        {
            var t = s.Text;
            if (open != null)
            {
                if (t == "}") { open = null; continue; }
                var close = t.EndsWith('}');
                var attr = close ? t[..^1].Trim() : t;
                if (attr.Length > 0)
                {
                    if (open.Members.Count < 500) open.Members.Add(attr);
                    else g.WarnOnce("members:" + open.Id, s.No, $"entity {open.Id} has more than 500 attributes; the rest were ignored.");
                }
                if (close) open = null;
                continue;
            }
            if (Word(t, "direction")) { if (DirectionOf(AfterWord(t, "direction")) is { } d) g.Direction = d; continue; }
            if (IsStyling(t)) { Styling(g, s); continue; }
            var r = ErRel.Match(t);
            if (r.Success)
            {
                var a = ErEntity(g, r.Groups["a"].Value, s.No);
                var b = ErEntity(g, r.Groups["b"].Value, s.No);
                if (a == null || b == null) continue;
                if (g.Edge(a.Id, b.Id, s.No) is not { } e) continue;
                e.Kind = r.Groups["line"].Value == "--" ? "identifying" : "nonIdentifying";
                e.FromCard = ErCard(r.Groups["l"].Value);
                e.ToCard = ErCard(r.Groups["r"].Value);
                e.Label = r.Groups["label"].Success ? MermaidSource.Name(r.Groups["label"].Value) : "";
                continue;
            }
            var m = Regex.Match(t, @"^(?<id>""[^""]+""|[^\s{\[""]+)(?:\[(?<alias>[^\]]*)\])?\s*(?<open>\{)?\s*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (m.Success && ErEntity(g, m.Groups["id"].Value, s.No) is { } n)
            {
                n.Declared = true;
                if (m.Groups["alias"].Success) n.Label = MermaidSource.Name(m.Groups["alias"].Value);
                if (m.Groups["open"].Success) open = n;
                continue;
            }
            g.Warn(s.No, $"could not read “{Trunc(t)}”; it was skipped.");
        }
        if (open != null) g.Warn(block.Line, $"the attributes of {open.Id} were not closed with “}}”.");
    }

    private static MNode? ErEntity(MGraph g, string raw, int line)
    {
        var id = MermaidSource.Unquote(raw);
        var n = g.Node(id, line);
        if (n != null && n.Label == id) n.Label = MermaidSource.Name(id);
        return n;
    }

    // ================================================================== requirement diagrams

    private static readonly HashSet<string> RequirementTypes = new(StringComparer.Ordinal)
        { "requirement", "functionalRequirement", "interfaceRequirement", "performanceRequirement", "physicalRequirement", "designConstraint" };

    private static readonly HashSet<string> RequirementRelations = new(StringComparer.Ordinal)
        { "contains", "copies", "derives", "satisfies", "verifies", "refines", "traces" };

    private static void Requirement(MGraph g, MermaidBlock block)
    {
        MNode? open = null;
        foreach (var s in Statements(g, block, splitSemicolons: false))
        {
            var t = s.Text;
            if (open != null)
            {
                if (t == "}") { open = null; continue; }
                var close = t.EndsWith('}');
                var body = close ? t[..^1].Trim() : t;
                var c = body.IndexOf(':');
                if (c > 0) open.Props[body[..c].Trim()] = MermaidSource.Clean(body[(c + 1)..].Trim());
                else if (body.Length > 0) g.Warn(s.No, $"could not read “{Trunc(body)}” in {open.Id}.");
                if (close) open = null;
                continue;
            }
            if (Word(t, "direction")) { if (DirectionOf(AfterWord(t, "direction")) is { } d) g.Direction = d; continue; }
            if (IsStyling(t)) { Styling(g, s); continue; }
            var sp = t.IndexOf(' ');
            var head = sp > 0 ? t[..sp] : t;
            if (RequirementTypes.Contains(head) || head == "element")
            {
                var rest = t[head.Length..].Trim();
                var brace = rest.EndsWith('{');
                var name = MermaidSource.Unquote(brace ? rest[..^1].Trim() : rest);
                var cls = name.IndexOf(":::", StringComparison.Ordinal);
                if (cls > 0) { name = name[..cls]; Styling(g, s); }
                if (name.Length == 0) { g.Warn(s.No, $"a {head} without a name was skipped."); continue; }
                if (g.Node(name, s.No) is not { } n) continue;
                n.Declared = true;
                n.Label = MermaidSource.Name(name);
                n.Shape = head == "element" ? "element" : head;
                if (brace) open = n;
                else g.Warn(s.No, $"{head} {name} has no {{ … }} body.");
                continue;
            }
            var m = Regex.Match(t, @"^(?<a>""[^""]+""|\S+)\s+-\s*(?<rel>\w+)\s*->\s+(?<b>""[^""]+""|\S+)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            var back = Regex.Match(t, @"^(?<b>""[^""]+""|\S+)\s+<-\s*(?<rel>\w+)\s*-\s+(?<a>""[^""]+""|\S+)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            var rm = m.Success ? m : back.Success ? back : null;
            if (rm != null)
            {
                var rel = rm.Groups["rel"].Value.ToLowerInvariant();
                if (!RequirementRelations.Contains(rel)) { g.Warn(s.No, $"unknown relationship “{rel}”; it was skipped."); continue; }
                var a = MermaidSource.Unquote(rm.Groups["a"].Value);
                var b = MermaidSource.Unquote(rm.Groups["b"].Value);
                if (g.Node(a, s.No) is null || g.Node(b, s.No) is null) continue;
                if (g.Edge(a, b, s.No) is { } e) e.Kind = rel;
                continue;
            }
            g.Warn(s.No, $"could not read “{Trunc(t)}”; it was skipped.");
        }
        if (open != null) g.Warn(block.Line, $"the body of {open.Id} was not closed with “}}”.");
    }

    // ================================================================== C4 context / container / component

    private static void C4(MGraph g, MermaidBlock block)
    {
        var boundaries = new Stack<string>();
        foreach (var s in Statements(g, block, splitSemicolons: false))
        {
            var t = s.Text;
            if (t == "}") { if (boundaries.Count > 0) boundaries.Pop(); continue; }
            if (Word(t, "title")) { g.Title ??= MermaidSource.Name(AfterWord(t, "title")); continue; }
            if (IsStyling(t)) { Styling(g, s); continue; }
            var p = t.IndexOf('(');
            var close = t.LastIndexOf(')');
            if (p <= 0 || close < p) { g.Warn(s.No, $"could not read “{Trunc(t)}”; it was skipped."); continue; }
            var fn = t[..p].Trim();
            if (fn.StartsWith("Update", StringComparison.Ordinal) || fn.StartsWith("Add", StringComparison.Ordinal) && fn.EndsWith("Tag", StringComparison.Ordinal)
                || fn.StartsWith("LAYOUT", StringComparison.Ordinal) || fn is "SHOW_LEGEND" or "HIDE_STEREOTYPE" or "SHOW_FLOATING_LEGEND")
            {
                Styling(g, s);
                continue;
            }
            var args = MermaidSource.SplitTop(t[(p + 1)..close], ',').Select(a => a.Trim()).Where(a => !a.StartsWith('$')).Select(a => MermaidSource.Unquote(a)).ToList();
            if (fn.Contains("Boundary", StringComparison.Ordinal))
            {
                g.WarnOnce("boundary", s.No, "C4 boundaries are not imported as elements; their contents are.");
                if (t.TrimEnd().EndsWith('{') && boundaries.Count < MermaidLimits.MaxDepth) boundaries.Push(args.ElementAtOrDefault(0) ?? "");
                continue;
            }
            if (fn.StartsWith("Rel", StringComparison.Ordinal) || fn == "BiRel")
            {
                if (args.Count < 2) { g.Warn(s.No, $"could not read “{Trunc(t)}”."); continue; }
                var reverse = fn == "Rel_Back";
                var (a, b) = reverse ? (args[1], args[0]) : (args[0], args[1]);
                if (g.Node(a, s.No) is null || g.Node(b, s.No) is null) continue;
                if (g.Edge(a, b, s.No) is { } e)
                {
                    e.Kind = "dependency";
                    e.Label = MermaidSource.Name(args.ElementAtOrDefault(2));
                    e.Bidirectional = fn == "BiRel";
                    if (args.ElementAtOrDefault(3) is { Length: > 0 } tech) e.ToCard = tech;
                }
                continue;
            }
            if (args.Count == 0 || args[0].Length == 0) { g.Warn(s.No, $"could not read “{Trunc(t)}”."); continue; }
            if (g.Node(args[0], s.No) is not { } n) continue;
            n.Declared = true;
            n.Shape = fn.StartsWith("Person", StringComparison.Ordinal) ? "person" : "system";
            n.Label = MermaidSource.Name(args.ElementAtOrDefault(1) ?? args[0]);
            var isContainer = fn.StartsWith("Container", StringComparison.Ordinal) || fn.StartsWith("Component", StringComparison.Ordinal);
            var techn = isContainer ? args.ElementAtOrDefault(2) : null;
            var descr = isContainer ? args.ElementAtOrDefault(3) : args.ElementAtOrDefault(2);
            n.Props["kind"] = fn;
            if (techn is { Length: > 0 }) n.Props["technology"] = techn;
            if (descr is { Length: > 0 }) n.Props["description"] = MermaidSource.Clean(descr);
            if (boundaries.Count > 0) n.Group = boundaries.Peek();
        }
    }
}
