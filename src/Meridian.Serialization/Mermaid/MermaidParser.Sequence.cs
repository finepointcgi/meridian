using System.Text.RegularExpressions;

namespace Meridian.Serialization.Mermaid;

/// <summary>Sequence diagrams: participants, messages, fragments, notes.</summary>
internal static partial class MermaidParser
{
    private static readonly Regex SeqMessage = new(
        @"^(?<a>[^\s:]+?)\s*(?<arrow><<-->>|<<->>|-->>|->>|--x|-x|--\)|-\)|-->|->)\s*(?<mod>[+-]?)\s*(?<b>[^:]+?)\s*(?::(?<text>.*))?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex SeqParticipant = new(
        @"^(?:create\s+)?(?<kind>participant|actor)\s+(?<id>[^\s@]+)(?:@\{(?<data>[^}]*)\})?(?:\s+as\s+(?<alias>.+))?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex SeqNote = new(
        @"^note\s+(?:(?:right|left)\s+of|over)\s+(?<targets>[^:]+?)\s*:(?<text>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static readonly HashSet<string> FragmentStarts = new(StringComparer.Ordinal) { "loop", "alt", "opt", "par", "critical", "break" };
    private static readonly HashSet<string> FragmentSeparators = new(StringComparer.Ordinal) { "else", "and", "option" };

    private static void Sequence(MGraph g, MermaidBlock block)
    {
        var open = new Stack<string>();   // "fragment", "box" or "rect"
        var activations = false;
        foreach (var s in Statements(g, block, splitSemicolons: false))
        {
            var t = s.Text;
            var sp = t.IndexOf(' ');
            var head = sp > 0 ? t[..sp] : t;
            var rest = sp > 0 ? t[(sp + 1)..].Trim() : "";

            if (head == "end")
            {
                if (open.Count == 0) { g.Warn(s.No, "“end” without an open block was ignored."); continue; }
                if (open.Pop() == "fragment") g.Sequence.Add(new MSeqFragmentEnd(s.No));
                continue;
            }
            if (FragmentStarts.Contains(head))
            {
                if (open.Count >= MermaidLimits.MaxDepth) { g.WarnOnce("depth", s.No, $"blocks nested deeper than {MermaidLimits.MaxDepth} levels were ignored."); open.Push("rect"); continue; }
                open.Push("fragment");
                g.Sequence.Add(new MSeqFragmentStart(s.No, head, MermaidSource.Name(rest)));
                continue;
            }
            if (FragmentSeparators.Contains(head))
            {
                if (open.Count > 0 && open.Peek() == "fragment") g.Sequence.Add(new MSeqFragmentElse(s.No, MermaidSource.Name(rest)));
                else g.Warn(s.No, $"“{head}” outside a fragment was ignored.");
                continue;
            }
            if (head is "rect" or "box")
            {
                open.Push(head);
                g.WarnOnce(head, s.No, head == "box" ? "participant boxes are not imported (the participants are)." : "background highlights (rect) are not imported (their messages are).");
                continue;
            }
            if (head is "activate" or "deactivate")
            {
                activations = true;
                if (rest.Length > 0) SeqParticipantNode(g, rest, s.No, null);
                continue;
            }
            if (head == "destroy")
            {
                g.WarnOnce("destroy", s.No, "participant creation and destruction are not imported (the participants are).");
                continue;
            }
            if (head is "title" or "title:") { g.Title ??= MermaidSource.Name(rest.TrimStart(':')); continue; }
            if (IsStyling(t)) { Styling(g, s); continue; }
            if (head.Equals("note", StringComparison.OrdinalIgnoreCase))
            {
                var nm = SeqNote.Match(t);
                if (!nm.Success) { g.Warn(s.No, $"could not read the note “{Trunc(t)}”."); continue; }
                var targets = nm.Groups["targets"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(2).ToList();
                foreach (var target in targets) SeqParticipantNode(g, target, s.No, null);
                g.Sequence.Add(new MSeqNote(s.No, targets, MermaidSource.Clean(nm.Groups["text"].Value)));
                continue;
            }
            var pm = SeqParticipant.Match(t);
            if (pm.Success)
            {
                if (t.StartsWith("create", StringComparison.Ordinal))
                    g.WarnOnce("destroy", s.No, "participant creation and destruction are not imported (the participants are).");
                var kind = pm.Groups["kind"].Value;
                if (pm.Groups["data"].Success && Regex.IsMatch(pm.Groups["data"].Value, @"""type""\s*:\s*""actor""", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) kind = "actor";
                if (SeqParticipantNode(g, pm.Groups["id"].Value, s.No, kind) is { } n)
                {
                    n.Declared = true;
                    n.Shape = kind;
                    if (pm.Groups["alias"].Success) n.Label = MermaidSource.Name(pm.Groups["alias"].Value);
                }
                continue;
            }
            var m = SeqMessage.Match(t);
            if (m.Success)
            {
                var arrow = m.Groups["arrow"].Value;
                var sort = arrow switch
                {
                    "->>" or "->" => "synchCall",
                    "-->>" or "-->" => "reply",
                    "-)" or "--)" => "asynchCall",
                    "-x" or "--x" => "asynchCall",
                    _ => "synchCall",
                };
                if (arrow is "-x" or "--x") g.WarnOnce("lost", s.No, "lost messages (-x) are imported as asynchronous messages.");
                if (arrow.StartsWith("<<", StringComparison.Ordinal)) g.WarnOnce("bidi", s.No, "bidirectional messages (<<->>) are imported as one call.");
                var from = m.Groups["a"].Value.Trim();
                var to = m.Groups["b"].Value.Trim();
                if (SeqParticipantNode(g, from, s.No, null) is null || SeqParticipantNode(g, to, s.No, null) is null) continue;
                var mod = m.Groups["mod"].Value;
                if (mod.Length > 0) activations = true;
                g.Sequence.Add(new MSeqMessage(s.No, from, to, MermaidSource.Clean(m.Groups["text"].Value), sort, mod == "+", mod == "-"));
                if (g.Sequence.Count > MermaidLimits.MaxEdges)
                {
                    g.Warn(s.No, $"more than {MermaidLimits.MaxEdges} messages; the rest were ignored.");
                    break;
                }
                continue;
            }
            g.Warn(s.No, $"could not read “{Trunc(t)}”; it was skipped.");
        }
        if (open.Count(o => o == "fragment") is var unclosed && unclosed > 0)
        {
            g.Warn(block.Line, $"{unclosed} fragment(s) were not closed with “end”.");
            for (var i = 0; i < unclosed; i++) g.Sequence.Add(new MSeqFragmentEnd(block.Line));
        }
        if (activations)
            g.WarnOnce("activation", block.Line, "activation bars are derived from call / reply messages in Meridian; explicit activate / deactivate markers were not stored.");
    }

    private static MNode? SeqParticipantNode(MGraph g, string id, int line, string? kind)
    {
        id = id.Trim();
        if (id.Length == 0) return null;
        var n = g.Node(id, line);
        if (n != null && n.Shape.Length == 0) n.Shape = kind ?? "participant";
        return n;
    }
}
