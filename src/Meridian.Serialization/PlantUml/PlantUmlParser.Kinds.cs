using System.Text.RegularExpressions;
using Meridian.Serialization.Mermaid;

namespace Meridian.Serialization.PlantUml;

internal static partial class PlantUmlParser
{
    // ================================================================== sequence diagrams

    [GeneratedRegex(@"^(?<kw>participant|actor|boundary|control|entity|database|collections|queue)\s+(?<rest>.+)$", Opts, 1000)]
    private static partial Regex ParticipantRx();

    [GeneratedRegex(@"^(?<a>""[^""]+""|[^\s""<>\-\\/]+?)\s*(?<arr>(?:<<?|[xo]|\\\\?|//?)?-{1,2}(?:\[[^\]]*\]-?)?(?:>>?|\\\\?|//?)?[xo]?)\s*(?<b>""[^""]+""|[^\s:""<>\-\\/]+?)\s*(?<act>\+\+|--|\*\*|!!)?\s*(?::\s*(?<text>.*))?$", Opts, 1000)]
    private static partial Regex MessageRx();

    [GeneratedRegex(@"^(?<op>alt|opt|loop|par|break|critical|group|neg|strict|seq)\b\s*(?<guard>.*)$", Opts, 1000)]
    private static partial Regex FragmentRx();

    private static void ParseSequence(MGraph g, List<Stmt> s)
    {
        var calls = new Stack<(string From, string To)>();
        MNode? Participant(string token, int line)
        {
            var id = Unquote(token);
            if (id.Length == 0 || id is "[" or "]") return null;
            var n = g.Node(id, line);
            if (n != null && !n.Declared) n.Label = Label(token);
            return n;
        }
        for (var i = 0; i < s.Count; i++)
        {
            var (no, t) = s[i];
            if (ParticipantRx().Match(t) is { Success: true } p)
            {
                var (id, label) = Alias(p.Groups["rest"].Value);
                if (g.Node(id, no) is { } n)
                {
                    n.Label = label;
                    n.Shape = p.Groups["kw"].Value.ToLowerInvariant() == "actor" ? "actor" : "participant";
                    n.Declared = true;
                }
                continue;
            }
            if (Note(s, ref i) is { } note)
            {
                g.Sequence.Add(new MSeqNote(no, note.Targets, note.Text));
                continue;
            }
            if (Regex.IsMatch(t, @"^end\s*$", Opts, TimeSpan.FromSeconds(1))) { g.Sequence.Add(new MSeqFragmentEnd(no)); continue; }
            if (Regex.Match(t, @"^else\b\s*(?<g>.*)$", Opts, TimeSpan.FromSeconds(1)) is { Success: true } el) { g.Sequence.Add(new MSeqFragmentElse(no, Label(el.Groups["g"].Value))); continue; }
            if (FragmentRx().Match(t) is { Success: true } f)
            {
                var op = f.Groups["op"].Value.ToLowerInvariant();
                g.Sequence.Add(new MSeqFragmentStart(no, op == "group" ? "seq" : op, Label(f.Groups["guard"].Value)));
                continue;
            }
            if (Regex.Match(t, @"^return\b\s*(?<t>.*)$", Opts, TimeSpan.FromSeconds(1)) is { Success: true } ret)
            {
                if (calls.Count == 0) { g.WarnOnce("return", no, "a “return” without an open call was ignored."); continue; }
                var (from, to) = calls.Pop();
                g.Sequence.Add(new MSeqMessage(no, to, from, Label(ret.Groups["t"].Value), "reply", false, false));
                continue;
            }
            if (Regex.IsMatch(t, @"^(activate|deactivate|destroy|create)\b", Opts, TimeSpan.FromSeconds(1)))
            {
                g.WarnOnce("activation", no, "activation bars are derived from call / reply messages in Meridian; activate / deactivate lines were not stored.");
                continue;
            }
            if (Regex.IsMatch(t, @"^(==.*==|\.\.\..*|\|\|\d*\|\||box\b.*|end\s*box|ref\b.*|end\s*ref|delay\b.*)$", Opts, TimeSpan.FromSeconds(1)))
            {
                if (t.StartsWith("ref", StringComparison.OrdinalIgnoreCase) && !t.Contains(':')) while (++i < s.Count && !Regex.IsMatch(s[i].Text, @"^end\s*ref$", Opts, TimeSpan.FromSeconds(1))) { }
                if (t.StartsWith("ref", StringComparison.OrdinalIgnoreCase)) g.WarnOnce("ref", no, "references to other interactions (ref) were not imported.");
                continue;
            }
            if (MessageRx().Match(t) is { Success: true } msg && IsArrow(msg.Groups["arr"].Value))
            {
                var arr = msg.Groups["arr"].Value;
                if (msg.Groups["a"].Value is "[" || msg.Groups["b"].Value is "]" || t.StartsWith('[') || t.Contains("]", StringComparison.Ordinal) && !arr.Contains('['))
                {
                    g.WarnOnce("foundlost", no, "messages from or to outside the diagram ([-> and ->]) were not imported.");
                    continue;
                }
                var reverse = arr.StartsWith('<') && !arr.EndsWith('>');
                var a = Participant(reverse ? msg.Groups["b"].Value : msg.Groups["a"].Value, no);
                var b = Participant(reverse ? msg.Groups["a"].Value : msg.Groups["b"].Value, no);
                if (a == null || b == null) continue;
                var dashed = arr.Contains("--", StringComparison.Ordinal);
                var async = arr.Contains(">>", StringComparison.Ordinal) || arr.Contains("<<", StringComparison.Ordinal) || arr.Contains('\\') || arr.Contains('/');
                var sort = dashed ? "reply" : async ? "asynchCall" : "synchCall";
                if (arr.StartsWith('<') && arr.EndsWith('>')) g.WarnOnce("bidi", no, "two-way messages (<->) were imported as one message.");
                g.Sequence.Add(new MSeqMessage(no, a.Id, b.Id, Label(msg.Groups["text"].Value), sort, false, false));
                if (sort == "synchCall" && a.Id != b.Id) calls.Push((a.Id, b.Id));
                else if (sort == "reply" && calls.Count > 0 && calls.Peek() == (b.Id, a.Id)) calls.Pop();
                continue;
            }
            g.WarnOnce("seq:" + t, no, $"could not read “{Shorten(t)}”; skipped.");
        }
    }

    private static bool IsArrow(string arr) => arr.Contains('-') && (arr.Contains('>') || arr.Contains('<') || arr.Contains('\\') || arr.Contains('/'));

    private static string Shorten(string s) => s.Length > 60 ? s[..60] + "…" : s;

    // ================================================================== class diagrams

    [GeneratedRegex(@"^(?<kw>abstract\s+class|abstract|class|interface|enum|annotation|struct|exception|record|dataclass|entity|protocol|metaclass|stereotype)\s+(?<rest>.*?)\s*(?<open>\{)?\s*$", Opts, 1000)]
    private static partial Regex ClassDeclRx();

    [GeneratedRegex(@"^(?<kw>package|namespace|folder|frame|rectangle|node|cloud|database|together)\b\s*(?<rest>.*?)\s*(?<open>\{)?\s*$", Opts, 1000)]
    private static partial Regex GroupRx();

    private const string Head = @"(?:<\|?|\^|\*|o|\+|\#|x|\}|\(|0)";
    private const string Tail = @"(?:\|?>|\^|\*|o|\+|\#|x|\{|\)|0)";
    private const string Body = @"(?:-+|\.+)(?:(?:up|down|left|right|le|ri|do|u|d|l|r)?(?:\[[^\]]*\])?(?:-+|\.+))?";

    private static readonly Regex RelationRx = new(
        $@"^(?<a>""[^""]+""|[\w.$:]+)\s*(?:""(?<ac>[^""]*)"")?\s*(?<arr>{Head}?{Body}{Tail}?)\s*(?:""(?<bc>[^""]*)"")?\s*(?<b>""[^""]+""|[\w.$:]+)\s*(?::\s*(?<label>.*))?$",
        Opts, TimeSpan.FromSeconds(1));

    private static void ParseClass(MGraph g, List<Stmt> s)
    {
        var groups = new Stack<string>();
        var notes = new Dictionary<string, (string Text, string? Target)>(StringComparer.Ordinal);
        var bareNotes = new List<MNote>();
        string? Group() => groups.Count > 0 ? groups.Peek() : null;

        MNode? Class(string token, int line)
        {
            var id = Unquote(token);
            if (id.Length == 0) return null;
            var n = g.Node(id, line);
            if (n != null && !n.Declared && n.Group == null) n.Group = Group();
            return n;
        }

        for (var i = 0; i < s.Count; i++)
        {
            var (no, t) = s[i];
            if (t == "}")
            {
                if (groups.Count > 0) groups.Pop();
                continue;
            }
            if (Regex.IsMatch(t, @"^end\s*(package|namespace|folder|frame|rectangle|node)$", Opts, TimeSpan.FromSeconds(1)))
            {
                if (groups.Count > 0) groups.Pop();
                continue;
            }
            if (ClassDeclRx().Match(t) is { Success: true } cd)
            {
                DeclareClass(g, s, ref i, cd, Group());
                continue;
            }
            if (GroupRx().Match(t) is { Success: true } gr)
            {
                var (_, label) = Alias(gr.Groups["rest"].Value);
                if (label.Length == 0 || gr.Groups["kw"].Value.Equals("together", StringComparison.OrdinalIgnoreCase))
                {
                    if (gr.Groups["open"].Success) groups.Push(Group() ?? "");
                    continue;
                }
                if (!g.Groups.Any(x => x.Id == label)) g.Groups.Add(new MGroup { Id = label, Label = label, Parent = Group() is { Length: > 0 } pg ? pg : null });
                groups.Push(label);
                continue;
            }
            if (FloatingNoteRx().Match(t) is { Success: true } fn)
            {
                var id = fn.Groups["id"].Success ? fn.Groups["id"].Value : fn.Groups["id2"].Value;
                var text = fn.Groups["text"].Success ? Label(fn.Groups["text"].Value) : NoteBody(s, ref i);
                notes[id] = (text, null);
                continue;
            }
            if (Note(s, ref i) is { } note)
            {
                bareNotes.Add(new MNote { Target = note.Targets.FirstOrDefault(), Text = note.Text });
                continue;
            }
            if (RelationRx.Match(t) is { Success: true } r && Regex.IsMatch(r.Groups["arr"].Value, @"-|\.\.", RegexOptions.None, TimeSpan.FromSeconds(1)))
            {
                var aId = Unquote(r.Groups["a"].Value);
                var bId = Unquote(r.Groups["b"].Value);
                // A line to a note attaches the note.
                if (notes.TryGetValue(aId, out var na)) { notes[aId] = (na.Text, bId); continue; }
                if (notes.TryGetValue(bId, out var nb)) { notes[bId] = (nb.Text, aId); continue; }
                if (Class(r.Groups["a"].Value, no) is not { } a || Class(r.Groups["b"].Value, no) is not { } b) continue;
                ClassRelation(g, no, a.Id, b.Id, r.Groups["arr"].Value, r.Groups["ac"].Value, r.Groups["bc"].Value, r.Groups["label"].Value);
                continue;
            }
            // "Name : member" adds one member.
            if (Regex.Match(t, @"^(?<c>""[^""]+""|[\w.$]+)\s*:\s*(?<m>.+)$", Opts, TimeSpan.FromSeconds(1)) is { Success: true } mem && g.ById.ContainsKey(Unquote(mem.Groups["c"].Value)))
            {
                g.ById[Unquote(mem.Groups["c"].Value)].Members.Add(Member(mem.Groups["m"].Value));
                continue;
            }
            g.WarnOnce("class:" + t, no, $"could not read “{Shorten(t)}”; skipped.");
        }
        foreach (var n in notes.Values) g.Notes.Add(new MNote { Target = n.Target, Text = n.Text });
        g.Notes.AddRange(bareNotes);
    }

    private static void DeclareClass(MGraph g, List<Stmt> s, ref int i, Match cd, string? group)
    {
        var no = s[i].No;
        var kw = Regex.Replace(cd.Groups["kw"].Value.ToLowerInvariant(), @"\s+", " ");
        var rest = cd.Groups["rest"].Value;
        var stereo = Stereotype(rest)?.ToLowerInvariant();
        // "extends A, B" and "implements C".
        var parents = new List<(string Id, bool Interface)>();
        var ext = Regex.Match(rest, @"\s+(?<kw>extends|implements)\s+(?<list>.+)$", Opts, TimeSpan.FromSeconds(1));
        if (ext.Success)
        {
            var list = ext.Groups["list"].Value;
            var impl = Regex.Match(list, @"\s+implements\s+(?<l>.+)$", Opts, TimeSpan.FromSeconds(1));
            var firstList = impl.Success ? list[..impl.Index] : list;
            parents.AddRange(firstList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => (Unquote(p), ext.Groups["kw"].Value.Equals("implements", StringComparison.OrdinalIgnoreCase))));
            if (impl.Success) parents.AddRange(impl.Groups["l"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => (Unquote(p), true)));
            rest = rest[..ext.Index];
        }
        var (id, label) = Alias(rest);
        if (g.Node(id, no) is not { } n) return;
        n.Label = label;
        n.Declared = true;
        n.Group ??= group;
        var annotation = kw switch
        {
            "interface" or "protocol" => "interface",
            "enum" => "enumeration",
            "abstract" or "abstract class" => "abstract",
            "struct" or "record" or "dataclass" => "struct",
            _ => stereo is "interface" or "enumeration" or "enum" or "datatype" or "valuetype" or "struct" or "primitive" or "abstract" ? stereo : null,
        };
        if (annotation != null) n.Props["annotation"] = annotation;
        foreach (var (parent, iface) in parents)
            if (g.Node(parent, no) is { } pn && g.Edge(id, pn.Id, no) is { } e)
            {
                e.Kind = iface ? "realization" : "generalization";
                if (iface && !pn.Declared) pn.Props["annotation"] = "interface";
            }
        if (!cd.Groups["open"].Success) return;
        while (++i < s.Count && s[i].Text != "}")
        {
            var line = s[i].Text;
            if (Regex.IsMatch(line, @"^(--|==|\.\.|__)", RegexOptions.None, TimeSpan.FromSeconds(1))) continue;   // separators
            n.Members.Add(annotation == "enumeration" ? line.TrimEnd(',', ';').Trim() : Member(line));
        }
    }

    /// <summary>A PlantUML member as the Mermaid builder reads members ({static} → $, {abstract} → *).</summary>
    private static string Member(string raw)
    {
        var s = raw.Trim();
        var isStatic = Regex.IsMatch(s, @"\{(static|classifier)\}", Opts, TimeSpan.FromSeconds(1));
        var isAbstract = s.Contains("{abstract}", StringComparison.OrdinalIgnoreCase);
        s = Regex.Replace(s, @"\{[a-z]+\}", "", Opts, TimeSpan.FromSeconds(1)).Trim();
        return s + (isAbstract ? "*" : "") + (isStatic ? "$" : "");
    }

    private static void ClassRelation(MGraph g, int no, string a, string b, string arr, string ac, string bc, string label)
    {
        var dotted = arr.Contains("..", StringComparison.Ordinal) || arr.Contains('.') && !arr.Contains('-');
        var left = Regex.Match(arr, $"^{Head}", RegexOptions.None, TimeSpan.FromSeconds(1)).Value;
        var right = Regex.Match(arr, $"{Tail}$", RegexOptions.None, TimeSpan.FromSeconds(1)).Value;
        // Direction hints ("label >") are drawing hints only.
        label = Label(label.Trim().TrimEnd('>', '<').TrimStart('<', '>').Trim());
        void Add(string from, string to, string kind, string fromCard, string toCard)
        {
            if (g.Edge(from, to, no) is not { } e) return;
            e.Kind = kind;
            e.FromCard = fromCard.Trim();
            e.ToCard = toCard.Trim();
            e.Label = label;
        }
        if (left is "<|" or "^") Add(b, a, dotted ? "realization" : "generalization", bc, ac);
        else if (right is "|>" or "^") Add(a, b, dotted ? "realization" : "generalization", ac, bc);
        else if (left == "*") Add(a, b, "composition", ac, bc);
        else if (right == "*") Add(b, a, "composition", bc, ac);
        else if (left == "o") Add(a, b, "aggregation", ac, bc);
        else if (right == "o") Add(b, a, "aggregation", bc, ac);
        else if (left == "<" && right != ">") Add(b, a, dotted ? "dependency" : "association", bc, ac);
        else if (right == ">") Add(a, b, dotted ? "dependency" : "association", ac, bc);
        else Add(a, b, dotted ? "dependency" : "association", ac, bc);
    }

    // ================================================================== use case diagrams

    private const string UcEnd = @"\([^)]+\)|:[^:]+:|""[^""]+""|[\w.$]+";

    private static readonly Regex UcRelationRx = new(
        $@"^(?<a>{UcEnd})\s*(?<arr>(?:<\|?|\^)?{Body}(?:\|?>|\^)?)\s*(?<b>{UcEnd})\s*(?::\s*(?<label>.*))?$", Opts, TimeSpan.FromSeconds(1));

    private static void ParseUseCase(MGraph g, List<Stmt> s)
    {
        var groups = new Stack<string>();
        string? Group() => groups.Count > 0 && groups.Peek().Length > 0 ? groups.Peek() : null;

        MNode? Declare(string id, string label, string shape, int line)
        {
            if (g.Node(id, line) is not { } n) return null;
            n.Label = label;
            n.Shape = shape;
            n.Declared = true;
            if (shape != "actor") n.Group ??= Group();
            return n;
        }

        // A name in a relation: (Use case), :Actor:, an alias, or a bare name (an actor, as in PlantUML).
        MNode? End(string token, int line)
        {
            token = token.Trim();
            if (token.StartsWith('(') && token.EndsWith(')'))
            {
                var label = Label(token[1..^1]);
                var existing = g.Nodes.FirstOrDefault(n => n.Shape == "usecase" && n.Label == label);
                return existing ?? Declare("(" + label + ")", label, "usecase", line);
            }
            if (token.Length > 2 && token.StartsWith(':') && token.EndsWith(':'))
            {
                var label = Label(token[1..^1]);
                var existing = g.Nodes.FirstOrDefault(n => n.Shape == "actor" && n.Label == label);
                return existing ?? Declare(":" + label + ":", label, "actor", line);
            }
            var id = Unquote(token);
            if (g.ById.TryGetValue(id, out var known)) return known;
            return Declare(id, Label(token), "actor", line);
        }

        for (var i = 0; i < s.Count; i++)
        {
            var (no, t) = s[i];
            if (t == "}") { if (groups.Count > 0) groups.Pop(); continue; }
            if (Regex.Match(t, @"^actor/?\s+(?<rest>.+)$", Opts, TimeSpan.FromSeconds(1)) is { Success: true } ad)
            {
                var rest = ad.Groups["rest"].Value.Trim();
                var colon = Regex.Match(rest, @"^:(?<l>[^:]+):(?:\s+as\s+(?<id>\S+))?", Opts, TimeSpan.FromSeconds(1));
                var (id, label) = colon.Success ? (colon.Groups["id"].Success ? colon.Groups["id"].Value : ":" + Label(colon.Groups["l"].Value) + ":", Label(colon.Groups["l"].Value)) : Alias(rest);
                Declare(id, label, "actor", no);
                continue;
            }
            if (Regex.Match(t, @"^usecase/?\s+(?<rest>.+)$", Opts, TimeSpan.FromSeconds(1)) is { Success: true } ud)
            {
                var rest = ud.Groups["rest"].Value.Trim();
                var paren = Regex.Match(rest, @"^\((?<l>[^)]+)\)(?:\s+as\s+(?<id>\S+))?", Opts, TimeSpan.FromSeconds(1));
                var (id, label) = paren.Success ? (paren.Groups["id"].Success ? paren.Groups["id"].Value : "(" + Label(paren.Groups["l"].Value) + ")", Label(paren.Groups["l"].Value)) : Alias(rest);
                Declare(id, label, "usecase", no);
                continue;
            }
            if (Regex.Match(t, @"^:(?<l>[^:]+):(?:\s+as\s+(?<id>\S+))?\s*$", Opts, TimeSpan.FromSeconds(1)) is { Success: true } ac)
            {
                var label = Label(ac.Groups["l"].Value);
                Declare(ac.Groups["id"].Success ? ac.Groups["id"].Value : ":" + label + ":", label, "actor", no);
                continue;
            }
            if (Regex.Match(t, @"^\((?<l>[^)]+)\)(?:\s+as\s+(?<id>\S+))?\s*$", Opts, TimeSpan.FromSeconds(1)) is { Success: true } uc)
            {
                var label = Label(uc.Groups["l"].Value);
                Declare(uc.Groups["id"].Success ? uc.Groups["id"].Value : "(" + label + ")", label, "usecase", no);
                continue;
            }
            if (GroupRx().Match(t) is { Success: true } gr)
            {
                var (_, label) = Alias(gr.Groups["rest"].Value);
                if (!gr.Groups["open"].Success) continue;
                if (label.Length > 0 && !g.Groups.Any(x => x.Id == label)) g.Groups.Add(new MGroup { Id = label, Label = label, Parent = Group() });
                groups.Push(label);
                continue;
            }
            if (Note(s, ref i) is { } note)
            {
                g.Notes.Add(new MNote { Target = note.Targets.FirstOrDefault() is { } nt && g.ById.ContainsKey(nt) ? nt : null, Text = note.Text });
                continue;
            }
            if (UcRelationRx.Match(t) is { Success: true } r)
            {
                var arr = r.Groups["arr"].Value;
                var rawLabel = r.Groups["label"].Value;
                var lower = rawLabel.ToLowerInvariant();
                var reverse = arr.StartsWith('<') && !arr.StartsWith("<|", StringComparison.Ordinal) && !arr.EndsWith('>');
                if (End(reverse ? r.Groups["b"].Value : r.Groups["a"].Value, no) is not { } a || End(reverse ? r.Groups["a"].Value : r.Groups["b"].Value, no) is not { } b) continue;
                string kind;
                var (from, to) = (a.Id, b.Id);
                if (lower.Contains("include")) kind = "include";
                else if (lower.Contains("extend")) kind = "extend";
                else if (arr.StartsWith("<|", StringComparison.Ordinal) || arr.StartsWith('^')) { kind = "generalization"; (from, to) = (b.Id, a.Id); }
                else if (arr.EndsWith("|>", StringComparison.Ordinal) || arr.EndsWith('^')) kind = "generalization";
                else if (arr.Contains('.')) kind = "dependency";
                else kind = "association";
                if (g.Edge(from, to, no) is { } e)
                {
                    e.Kind = kind;
                    e.Label = kind is "association" or "dependency" ? Label(StereotypeRx().Replace(rawLabel, "")) : "";
                }
                continue;
            }
            g.WarnOnce("uc:" + t, no, $"could not read “{Shorten(t)}”; skipped.");
        }
    }

    // ================================================================== activity diagrams (current syntax)

    private abstract class Frame;
    private sealed class IfFrame(string decision) : Frame
    {
        public string Decision = decision;
        public readonly List<(string Id, string? Label)> Ends = [];
        public bool HadElse;
    }
    private sealed class WhileFrame(string decision) : Frame { public readonly string Decision = decision; }
    private sealed class RepeatFrame : Frame { public string? First; }
    private sealed class ForkFrame(string bar) : Frame
    {
        public readonly string Bar = bar;
        public readonly List<(string Id, string? Label)> Ends = [];
    }

    private static void ParseActivity(MGraph g, List<Stmt> s)
    {
        var preds = new List<(string Id, string? Label)>();
        var frames = new Stack<Frame>();
        string? lane = null;
        string? last = null;
        var counter = 0;

        string NewNode(string shape, string label, int line)
        {
            var id = $"n{++counter}";
            if (g.Node(id, line) is { } n)
            {
                n.Shape = shape;
                n.Label = label;
                n.Declared = true;
                n.Group = lane;
            }
            return id;
        }
        void Link(string id, int line)
        {
            foreach (var (p, label) in preds)
                if (g.Edge(p, id, line) is { } e) e.Label = label ?? "";
            preds = [(id, null)];
            last = id;
            foreach (var r in frames.OfType<RepeatFrame>()) r.First ??= id;
        }
        T? Top<T>(int line, string what) where T : Frame
        {
            if (frames.Count > 0 && frames.Peek() is T t) return t;
            g.WarnOnce("unbalanced:" + what, line, $"“{what}” without its opening statement was ignored.");
            return null;
        }
        static string Paren(Group grp) => grp.Success ? Label(grp.Value) : "";

        for (var i = 0; i < s.Count; i++)
        {
            var (no, t) = s[i];
            var lower = t.ToLowerInvariant();
            if (lower == "start") { Link(NewNode("initial", "", no), no); continue; }
            if (lower is "stop" or "end" or "kill") { Link(NewNode("final", "", no), no); preds = []; continue; }
            if (lower == "detach") { preds = []; continue; }
            if (t.StartsWith(':'))
            {
                var text = t[1..];
                while (!EndsAction(text) && i + 1 < s.Count) text += "\n" + s[++i].Text;
                text = text.TrimEnd();
                if (EndsAction(text)) text = text[..^1];
                Link(NewNode("rect", Label(text), no), no);
                continue;
            }
            if (Regex.Match(t, @"^(?<arrow>-+>)\s*(?<label>[^;]*);?$", Opts, TimeSpan.FromSeconds(1)) is { Success: true } arrow)
            {
                var label = Label(arrow.Groups["label"].Value);
                if (label.Length > 0) preds = preds.Select(p => (p.Id, (string?)label)).ToList();
                continue;
            }
            if (Regex.Match(t, @"^if\s*\((?<c>.*?)\)\s*(?:is\s*\((?<is>[^)]*)\)\s*)?(?:then\s*(?:\((?<y>[^)]*)\))?)?\s*$", Opts, TimeSpan.FromSeconds(1)) is { Success: true } iff)
            {
                var cond = Label(iff.Groups["c"].Value + (iff.Groups["is"].Success ? " " + iff.Groups["is"].Value : ""));
                var d = NewNode("diamond", cond, no);
                Link(d, no);
                frames.Push(new IfFrame(d));
                preds = [(d, Paren(iff.Groups["y"]))];
                continue;
            }
            if (Regex.Match(t, @"^else\s*if\s*\((?<c>.*?)\)\s*(?:then\s*(?:\((?<y>[^)]*)\))?)?\s*$", Opts, TimeSpan.FromSeconds(1)) is { Success: true } elif)
            {
                if (Top<IfFrame>(no, "elseif") is not { } f) continue;
                f.Ends.AddRange(preds);
                preds = [(f.Decision, null)];
                var d = NewNode("diamond", Label(elif.Groups["c"].Value), no);
                Link(d, no);
                f.Decision = d;
                preds = [(d, Paren(elif.Groups["y"]))];
                continue;
            }
            if (Regex.Match(t, @"^else\b\s*(?:\((?<n>[^)]*)\))?\s*$", Opts, TimeSpan.FromSeconds(1)) is { Success: true } els)
            {
                if (Top<IfFrame>(no, "else") is not { } f) continue;
                f.Ends.AddRange(preds);
                f.HadElse = true;
                preds = [(f.Decision, Paren(els.Groups["n"]))];
                continue;
            }
            if (Regex.IsMatch(t, @"^end\s*if$", Opts, TimeSpan.FromSeconds(1)))
            {
                if (Top<IfFrame>(no, "endif") is not { } f) continue;
                frames.Pop();
                f.Ends.AddRange(preds);
                if (!f.HadElse) f.Ends.Add((f.Decision, null));
                preds = f.Ends.ToList();
                continue;
            }
            if (Regex.Match(t, @"^switch\s*\((?<c>.*?)\)\s*$", Opts, TimeSpan.FromSeconds(1)) is { Success: true } sw)
            {
                var d = NewNode("diamond", Label(sw.Groups["c"].Value), no);
                Link(d, no);
                frames.Push(new IfFrame(d) { HadElse = true });
                preds = [];
                continue;
            }
            if (Regex.Match(t, @"^case\s*\((?<v>[^)]*)\)\s*$", Opts, TimeSpan.FromSeconds(1)) is { Success: true } cs)
            {
                if (Top<IfFrame>(no, "case") is not { } f) continue;
                f.Ends.AddRange(preds);
                preds = [(f.Decision, Label(cs.Groups["v"].Value))];
                continue;
            }
            if (Regex.IsMatch(t, @"^end\s*switch$", Opts, TimeSpan.FromSeconds(1)))
            {
                if (Top<IfFrame>(no, "endswitch") is not { } f) continue;
                frames.Pop();
                f.Ends.AddRange(preds);
                preds = f.Ends.ToList();
                continue;
            }
            if (Regex.Match(t, @"^while\s*\((?<c>.*?)\)\s*(?:is\s*\((?<y>[^)]*)\))?\s*$", Opts, TimeSpan.FromSeconds(1)) is { Success: true } wh)
            {
                var d = NewNode("diamond", Label(wh.Groups["c"].Value), no);
                Link(d, no);
                frames.Push(new WhileFrame(d));
                preds = [(d, Paren(wh.Groups["y"]))];
                continue;
            }
            if (Regex.Match(t, @"^end\s*while\s*(?:\((?<n>[^)]*)\))?\s*$", Opts, TimeSpan.FromSeconds(1)) is { Success: true } ew)
            {
                if (Top<WhileFrame>(no, "endwhile") is not { } f) continue;
                frames.Pop();
                foreach (var (p, label) in preds)
                    if (g.Edge(p, f.Decision, no) is { } back) back.Label = label ?? "";
                preds = [(f.Decision, Paren(ew.Groups["n"]))];
                continue;
            }
            if (Regex.Match(t, @"^repeat\s*(?<rest>:.*)?$", Opts, TimeSpan.FromSeconds(1)) is { Success: true } rp && !lower.StartsWith("repeat while", StringComparison.Ordinal) && !lower.StartsWith("repeatwhile", StringComparison.Ordinal))
            {
                frames.Push(new RepeatFrame());
                if (rp.Groups["rest"].Success)
                {
                    var text = rp.Groups["rest"].Value[1..].TrimEnd();
                    if (EndsAction(text)) text = text[..^1];
                    Link(NewNode("rect", Label(text), no), no);
                }
                continue;
            }
            if (Regex.Match(t, @"^repeat\s*while\s*\((?<c>.*?)\)\s*(?:is\s*\((?<y>[^)]*)\))?\s*(?:not\s*\((?<n>[^)]*)\))?\s*$", Opts, TimeSpan.FromSeconds(1)) is { Success: true } rw)
            {
                if (Top<RepeatFrame>(no, "repeat while") is not { } f) continue;
                frames.Pop();
                var d = NewNode("diamond", Label(rw.Groups["c"].Value), no);
                Link(d, no);
                if (f.First != null && g.Edge(d, f.First, no) is { } back) back.Label = Paren(rw.Groups["y"]);
                preds = [(d, Paren(rw.Groups["n"]))];
                continue;
            }
            if (lower is "fork" or "split")
            {
                var bar = NewNode("fork", "", no);
                Link(bar, no);
                frames.Push(new ForkFrame(bar));
                continue;
            }
            if (lower is "fork again" or "split again")
            {
                if (Top<ForkFrame>(no, lower) is not { } f) continue;
                f.Ends.AddRange(preds);
                preds = [(f.Bar, null)];
                continue;
            }
            if (Regex.IsMatch(t, @"^end\s*(fork|merge|split)\b.*$", Opts, TimeSpan.FromSeconds(1)))
            {
                if (Top<ForkFrame>(no, "end fork") is not { } f) continue;
                frames.Pop();
                f.Ends.AddRange(preds);
                preds = f.Ends.ToList();
                Link(NewNode("fork", "", no), no);
                continue;
            }
            if (Regex.Match(t, @"^\|(?:#?[\w]+\|)?(?<lane>[^|]+)\|$", Opts, TimeSpan.FromSeconds(1)) is { Success: true } ln)
            {
                lane = Label(ln.Groups["lane"].Value);
                if (!g.Groups.Any(x => x.Id == lane)) g.Groups.Add(new MGroup { Id = lane, Label = lane });
                continue;
            }
            if (Note(s, ref i) is { } note)
            {
                g.Notes.Add(new MNote { Target = last, Text = note.Text });
                continue;
            }
            if (Regex.IsMatch(t, @"^(partition|group|package|rectangle|card)\b.*$|^\}$|^end\s*group$", Opts, TimeSpan.FromSeconds(1))) continue;
            g.WarnOnce("act:" + t, no, $"could not read “{Shorten(t)}”; skipped.");
        }
        if (frames.Count > 0) g.Warn(0, $"{frames.Count} if / while / repeat / fork block(s) were not closed.");
        // With swimlanes, steps before the first lane belong to it.
        if (g.Groups.Count > 0)
            foreach (var n in g.Nodes.Where(n => n.Group == null)) n.Group = g.Groups[0].Id;
    }

    /// <summary>An action ends with ";" (or one of the SDL endings | &lt; &gt; / ] }).</summary>
    private static bool EndsAction(string text)
    {
        var t = text.TrimEnd();
        return t.Length > 0 && t[^1] is ';' or '|' or '<' or '>' or '/' or ']' or '}';
    }

    // ================================================================== state diagrams

    [GeneratedRegex(@"^state\s+(?<rest>.+?)\s*(?<open>\{)?\s*$", Opts, 1000)]
    private static partial Regex StateDeclRx();

    private const string StEnd = @"\[\*\]|\[H\*?\]|""[^""]+""|[\w.$]+";

    private static readonly Regex TransitionRx = new(
        $@"^(?<a>{StEnd})\s*(?<arr><?-+(?:(?:up|down|left|right|u|d|l|r)?(?:\[[^\]]*\])?-*)>?)\s*(?<b>{StEnd})\s*(?::\s*(?<label>.*))?$", Opts, TimeSpan.FromSeconds(1));

    private static void ParseState(MGraph g, List<Stmt> s)
    {
        var scopes = new Stack<string>();
        string Scope() => scopes.Count > 0 ? scopes.Peek() : "";

        MNode? State(string token, int line)
        {
            var id = Unquote(token);
            var known = g.ById.ContainsKey(id);
            var n = g.Node(id, line);
            if (n == null) return null;
            if (!known)
            {
                n.Label = Label(token);
                if (Scope().Length > 0 && id != Scope()) n.Parent = Scope();
            }
            return n;
        }
        MNode? Pseudo(string kind, int line)
        {
            var scope = Scope();
            var n = g.Node($"[*]{kind}:{scope}", line);
            if (n == null) return null;
            n.Shape = kind;
            n.Label = "";
            n.Parent = scope.Length > 0 ? scope : null;
            return n;
        }
        MNode? End(string token, bool source, int line) => token switch
        {
            "[*]" => Pseudo(source ? "initial" : "final", line),
            "[H]" or "[h]" => Pseudo("history", line),
            "[H*]" or "[h*]" => Pseudo("deephistory", line),
            _ => State(token, line),
        };

        for (var i = 0; i < s.Count; i++)
        {
            var (no, t) = s[i];
            if (t == "}") { if (scopes.Count > 0) scopes.Pop(); continue; }
            if (t is "--" or "||") { g.WarnOnce("regions", no, "concurrent regions (-- or ||) are imported as one region of the composite state."); continue; }
            if (StateDeclRx().Match(t) is { Success: true } sd)
            {
                var rest = sd.Groups["rest"].Value;
                var stereo = Stereotype(rest)?.ToLowerInvariant();
                string? desc = null;
                var colon = rest.IndexOf(':');
                if (colon > 0 && rest.IndexOf('"') is var q && (q < 0 || q > colon || rest.IndexOf('"', q + 1) < colon))
                {
                    desc = rest[(colon + 1)..].Trim();
                    rest = rest[..colon];
                }
                var (id, label) = Alias(rest);
                if (State(id, no) is not { } n) continue;
                n.Label = label;
                n.Declared = true;
                n.Shape = stereo switch
                {
                    "choice" => "choice",
                    "fork" => "fork",
                    "join" => "join",
                    "end" => "final",
                    "start" => "initial",
                    "history" => "history",
                    "history*" => "deephistory",
                    _ => sd.Groups["open"].Success ? "composite" : n.Shape,
                };
                if (desc is { Length: > 0 }) StateText(n, desc);
                if (sd.Groups["open"].Success) scopes.Push(id);
                continue;
            }
            if (Note(s, ref i) is { } note)
            {
                g.Notes.Add(new MNote { Target = note.Targets.FirstOrDefault(), Text = note.Text });
                continue;
            }
            if (TransitionRx.Match(t) is { Success: true } tr)
            {
                var arr = tr.Groups["arr"].Value;
                var reverse = arr.StartsWith('<') && !arr.EndsWith('>');
                var (ta, tb) = reverse ? (tr.Groups["b"].Value, tr.Groups["a"].Value) : (tr.Groups["a"].Value, tr.Groups["b"].Value);
                if (End(ta, true, no) is not { } a || End(tb, false, no) is not { } b) continue;
                if (g.Edge(a.Id, b.Id, no) is { } e) e.Label = Label(tr.Groups["label"].Value);
                continue;
            }
            if (Regex.Match(t, @"^(?<id>""[^""]+""|[\w.$]+)\s*:\s*(?<text>.+)$", Opts, TimeSpan.FromSeconds(1)) is { Success: true } line && State(line.Groups["id"].Value, no) is { } sn)
            {
                StateText(sn, line.Groups["text"].Value);
                continue;
            }
            g.WarnOnce("state:" + t, no, $"could not read “{Shorten(t)}”; skipped.");
        }
        if (scopes.Count > 0) g.Warn(0, $"{scopes.Count} composite state(s) were not closed with “}}”.");
    }

    /// <summary>"entry / x", "exit / y", "do / z" go to the matching fields; other lines add to the description.</summary>
    private static void StateText(MNode n, string text)
    {
        var d = Label(text);
        var m = Regex.Match(d, @"^(?<k>entry|exit|do)\s*/\s*(?<v>.*)$", Opts, TimeSpan.FromSeconds(1));
        if (m.Success) { n.Props[m.Groups["k"].Value.ToLowerInvariant()] = m.Groups["v"].Value.Trim(); return; }
        n.Props["description"] = n.Props.TryGetValue("description", out var old) && old.Length > 0 ? old + "\n" + d : d;
    }
}
