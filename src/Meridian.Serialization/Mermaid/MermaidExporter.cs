using System.Text;
using System.Text.RegularExpressions;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization.Mermaid;

/// <summary>
/// Writes a diagram as Mermaid text: activity diagrams as flowcharts, state machine diagrams as stateDiagram-v2,
/// sequence diagrams as sequenceDiagram, block definition / class diagrams as classDiagram and requirement diagrams as
/// requirementDiagram. <see cref="MermaidImporter"/> reads the result back with the same elements and relationships.
/// </summary>
public static class MermaidExporter
{
    public static bool CanExport(Diagram? d) => d?.DiagramType is DiagramType.Activity or DiagramType.StateMachine or DiagramType.Sequence
        or DiagramType.BlockDefinition or DiagramType.Class or DiagramType.Requirement;

    /// <summary>The diagram as Mermaid text (with a front matter title). Throws <see cref="InvalidOperationException"/> for other diagram types.</summary>
    public static string Export(ModelRepository m, Diagram d)
    {
        var sb = new StringBuilder();
        sb.Append("---\ntitle: \"").Append(Plain(d.Name).Replace('"', '\'')).Append("\"\n---\n");
        switch (d.DiagramType)
        {
            case DiagramType.Activity: Activity(d, sb); break;
            case DiagramType.StateMachine: State(d, sb); break;
            case DiagramType.Sequence: Sequence(m, d, sb); break;
            case DiagramType.BlockDefinition or DiagramType.Class: Class(m, d, sb); break;
            case DiagramType.Requirement: Requirement(d, sb); break;
            default: throw new InvalidOperationException($"{Metamodel.DisplayName(d.DiagramType)}s cannot be exported as Mermaid.");
        }
        return sb.ToString();
    }

    // ================================================================== text helpers

    private static string Plain(string s) => Regex.Replace(s, @"\s*\r?\n\s*", " ").Trim();

    /// <summary>A quoted Mermaid label: quotes as #quot;, line breaks as &lt;br&gt;.</summary>
    private static string Q(string s) => "\"" + s.Replace("#", "#35;").Replace("\"", "#quot;").Replace("\r", "").Replace("\n", "<br>") + "\"";

    private static bool LeftToRight(Diagram d)
    {
        double dx = 0, dy = 0;
        foreach (var e in d.Edges)
        {
            var a = e.Source.Bounds.Center;
            var b = e.Target.Bounds.Center;
            dx += Math.Abs(a.X - b.X);
            dy += Math.Abs(a.Y - b.Y);
        }
        return dx > dy * 1.2;
    }

    private sealed class Ids
    {
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);
        private readonly Dictionary<Element, string> _ids = new();
        public string this[Element e] => _ids.TryGetValue(e, out var id) ? id : _ids[e] = MermaidSource.IdFrom(e.Name, _used, Prefix(e.Kind));
        public bool Has(Element e) => _ids.ContainsKey(e);

        /// <summary>Gives <paramref name="e"/> an id made from <paramref name="name"/> rather than from its own name.</summary>
        public string Assign(Element e, string name) => _ids[e] = MermaidSource.IdFrom(name, _used, Prefix(e.Kind));

        private static string Prefix(ElementKind k) => k switch
        {
            ElementKind.DecisionNode => "decision",
            ElementKind.InitialNode or ElementKind.InitialState => "start",
            ElementKind.ActivityFinalNode or ElementKind.FinalState => "stop",
            ElementKind.ForkNode => "fork",
            ElementKind.Partition => "lane",
            ElementKind.Pseudostate => "choice",
            _ => "n",
        };
    }

    // ================================================================== activity → flowchart

    private static void Activity(Diagram d, StringBuilder sb)
    {
        sb.Append("flowchart ").Append(LeftToRight(d) ? "LR" : "TD").Append('\n');
        var ids = new Ids();
        var shown = d.Nodes.Where(n => n.Element.Kind is not (ElementKind.Partition or ElementKind.Comment) && !Metamodel.IsPin(n.Element.Kind)).ToList();
        string Node(Element e)
        {
            var id = ids[e];
            return e.Kind switch
            {
                ElementKind.InitialNode => $"{id}(({Q(e.Name.Length > 0 ? e.Name : "start")}))",
                ElementKind.ActivityFinalNode => $"{id}((({Q(e.Name.Length > 0 ? e.Name : "end")})))",
                ElementKind.DecisionNode => $"{id}{{{Q(e.Name.Length > 0 ? e.Name : " ")}}}",
                ElementKind.ForkNode => $"{id}@{{ shape: fork, label: {Q(e.Name)} }}",
                ElementKind.ObjectNode when e.Get(FieldKeys.Subtype) == Subtypes.DataStore => $"{id}[({Q(e.DisplayName)})]",
                ElementKind.ObjectNode when e.Get(FieldKeys.Subtype) == Subtypes.CentralBuffer => $"{id}@{{ shape: h-cyl, label: {Q(e.DisplayName)} }}",
                ElementKind.ObjectNode => $"{id}@{{ shape: doc, label: {Q(e.DisplayName)} }}",
                _ => $"{id}({Q(e.DisplayName)})",
            };
        }
        var lanes = d.Nodes.Where(n => n.Element.Kind == ElementKind.Partition).ToList();
        var inLane = new HashSet<DiagramNode>();
        foreach (var lane in lanes)
        {
            var members = shown.Where(n => n.ParentId == lane.Id || n.ParentId == null && lane.Bounds.Contains(n.Bounds.Center)).ToList();
            sb.Append("    subgraph ").Append(ids[lane.Element]).Append('[').Append(Q(lane.Element.DisplayName)).Append("]\n");
            foreach (var n in members.Where(inLane.Add)) sb.Append("        ").Append(Node(n.Element)).Append('\n');
            sb.Append("    end\n");
        }
        foreach (var n in shown.Where(n => !inLane.Contains(n))) sb.Append("    ").Append(Node(n.Element)).Append('\n');
        var set = shown.Select(n => n.Element).ToHashSet();
        foreach (var e in d.Edges.Where(e => e.Relationship.Kind is ElementKind.ControlFlow or ElementKind.ObjectFlow))
        {
            if (!set.Contains(e.Source.Element) || !set.Contains(e.Target.Element)) continue;
            var guard = e.Relationship.Get(FieldKeys.Guard);
            var label = guard.Length > 0 ? guard : e.Relationship.Name;
            sb.Append("    ").Append(ids[e.Source.Element]).Append(" -->");
            if (label.Length > 0) sb.Append('|').Append(Q(Plain(label))).Append('|');
            sb.Append(' ').Append(ids[e.Target.Element]).Append('\n');
        }
    }

    // ================================================================== state machine → stateDiagram-v2

    private static void State(Diagram d, StringBuilder sb)
    {
        sb.Append("stateDiagram-v2\n");
        if (LeftToRight(d)) sb.Append("    direction LR\n");
        var ids = new Ids();
        var shown = d.Nodes.Select(n => n.Element).Where(e => Metamodel.IsVertex(e.Kind)).ToHashSet();
        var transitions = d.Edges.Where(e => e.Relationship.Kind == ElementKind.Transition && shown.Contains(e.Source.Element) && shown.Contains(e.Target.Element))
            .Select(e => e.Relationship).ToList();
        // A vertex's scope: the nearest composite state shown on the diagram (null = top level).
        Element? Scope(Element e) => e.AncestorsAndSelf().Skip(1).FirstOrDefault(a => a.Kind == ElementKind.State && shown.Contains(a));
        bool Pseudo(Element e) => e.Kind is ElementKind.InitialState or ElementKind.FinalState;

        void Write(Element? scope, int depth)
        {
            var pad = new string(' ', 4 * depth);
            foreach (var s in shown.Where(e => e.Kind == ElementKind.State && ReferenceEquals(Scope(e), scope)))
            {
                var id = ids[s];
                if (id != s.Name) sb.Append(pad).Append("state ").Append(Q(Plain(s.DisplayName))).Append(" as ").Append(id).Append('\n');
                foreach (var (key, label) in new[] { (FieldKeys.Entry, "entry"), (FieldKeys.DoActivity, "do"), (FieldKeys.Exit, "exit") })
                    if (s.Get(key) is { Length: > 0 } v) sb.Append(pad).Append(id).Append(" : ").Append(label).Append(" / ").Append(Plain(v)).Append('\n');
                foreach (var line in s.Documentation.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    sb.Append(pad).Append(id).Append(" : ").Append(line.Replace(":", "#colon;")).Append('\n');
                if (shown.Any(c => ReferenceEquals(Scope(c), s)))
                {
                    sb.Append(pad).Append("state ").Append(id).Append(" {\n");
                    Write(s, depth + 1);
                    sb.Append(pad).Append("}\n");
                }
                else if (id == s.Name && s.Documentation.Length == 0 && !new[] { FieldKeys.Entry, FieldKeys.DoActivity, FieldKeys.Exit }.Any(k => s.Get(k).Length > 0))
                    sb.Append(pad).Append(id).Append('\n');
            }
            // Pseudostates: Mermaid has choice, fork, join and (in Meridian's reading) history; junctions are written as choices.
            foreach (var p in shown.Where(e => e.Kind == ElementKind.Pseudostate && ReferenceEquals(Scope(e), scope)))
            {
                var kind = Pseudostates.KindOf(p);
                var stereo = kind switch
                {
                    Pseudostates.Fork => "fork",
                    Pseudostates.Join => "join",
                    Pseudostates.ShallowHistory => "history",
                    Pseudostates.DeepHistory => "deephistory",
                    Pseudostates.Choice or Pseudostates.Junction => "choice",
                    _ => null,
                };
                if (stereo != null) sb.Append(pad).Append("state ").Append(ids[p]).Append(" <<").Append(stereo).Append(">>\n");
                else sb.Append(pad).Append("%% ").Append(ids[p]).Append(" is a ").Append(Subtypes.Display(kind!).ToLowerInvariant()).Append(" (no Mermaid notation)\n")
                      .Append(pad).Append(ids[p]).Append('\n');
            }
            // Transitions from / to this scope's [*] belong inside it; all others go at the top level after every state is declared.
            foreach (var t in transitions.Where(t => Pseudo(t.Source) && ReferenceEquals(Scope(t.Source), scope) || Pseudo(t.Target) && !Pseudo(t.Source) && ReferenceEquals(Scope(t.Target), scope)
                                                     || scope == null && !Pseudo(t.Source) && !Pseudo(t.Target)))
                sb.Append(pad).Append(End(t.Source)).Append(" --> ").Append(End(t.Target)).Append(Label(t)).Append('\n');
        }
        string End(Element e) => Pseudo(e) ? "[*]" : ids[e];
        static string Label(Relationship t)
        {
            var parts = new List<string>();
            if (t.Name.Length > 0) parts.Add(Plain(t.Name));
            if (t.Get(FieldKeys.Guard) is { Length: > 0 } g) parts.Add($"[{Plain(g)}]");
            if (t.Get(FieldKeys.Effect) is { Length: > 0 } ef) parts.Add($"/ {Plain(ef)}");
            return parts.Count > 0 ? " : " + string.Join(" ", parts).Replace(":", "#colon;") : "";
        }
        Write(null, 1);
    }

    // ================================================================== interaction → sequenceDiagram

    private static void Sequence(ModelRepository m, Diagram d, StringBuilder sb)
    {
        sb.Append("sequenceDiagram\n");
        var ids = new Ids();
        var lifelines = d.Nodes.Where(n => n.Element.Kind == ElementKind.Lifeline).OrderBy(n => n.Bounds.X).ToList();
        foreach (var l in lifelines)
        {
            var id = ids[l.Element];
            var isActor = m.ResolveType(l.Element) is { Kind: ElementKind.Actor };
            sb.Append("    ").Append(isActor ? "actor " : "participant ").Append(id);
            if (id != l.Element.Name) sb.Append(" as ").Append(Plain(l.Element.DisplayName));
            sb.Append('\n');
        }
        // Events by time: fragment starts, operand separators, messages, notes, fragment ends.
        var events = new List<(double Y, int Order, string Text, SeqEvent Kind)>();
        foreach (var e in d.Edges.Where(e => e.Relationship.Kind == ElementKind.Message && ids.Has(e.Source.Element) && ids.Has(e.Target.Element)))
        {
            var arrow = e.Relationship.Get(FieldKeys.MessageSort) switch { "reply" => "-->>", "asynchCall" => "-)", _ => "->>" };
            events.Add((Notation.MessageY(e), 1, $"{ids[e.Source.Element]}{arrow}{ids[e.Target.Element]}: {Plain(e.Relationship.Name).Replace(";", "#59;")}", SeqEvent.Line));
        }
        foreach (var f in d.Nodes.Where(n => n.Element.Kind == ElementKind.CombinedFragment))
        {
            var op = f.Element.Get(FieldKeys.Operator) is { Length: > 0 } o ? o : "alt";
            var sep = op == "par" ? "and" : op == "critical" ? "option" : "else";
            string Separator(string g) => $"{sep} {(g == "else" ? "" : Plain(g))}".TrimEnd();
            var bands = InteractionOperands.Layout(f, f.Bounds);
            if (bands[0].Operand != null)
            {
                // Real operands: each separator at the height it is drawn, so the messages fall in the right operand.
                events.Add((f.Bounds.Y, 0, $"{op} {Plain(bands[0].Operand!.Get(FieldKeys.Guard))}".TrimEnd(), SeqEvent.Start));
                foreach (var band in bands.Skip(1)) events.Add((band.Top, 0, Separator(band.Operand!.Get(FieldKeys.Guard)), SeqEvent.Separator));
            }
            else
            {
                // A fragment without operand elements (older files kept merged guards separated by " | ").
                var guards = f.Element.Get(FieldKeys.Guard).Split(" | ", StringSplitOptions.TrimEntries);
                var start = new StringBuilder($"{op} {Plain(guards[0])}".TrimEnd());
                foreach (var g in guards.Skip(1)) start.Append('\n').Append(Separator(g));
                events.Add((f.Bounds.Y, 0, start.ToString(), SeqEvent.Start));
            }
            events.Add((f.Bounds.Bottom, 2, "end", SeqEvent.End));
        }
        foreach (var c in d.Nodes.Where(n => n.Element.Kind == ElementKind.Comment))
        {
            var over = lifelines.Where(l => l.Bounds.Center.X >= c.Bounds.X - 20 && l.Bounds.Center.X <= c.Bounds.Right + 20).Select(l => ids[l.Element]).ToList();
            if (over.Count == 0 && lifelines.Count > 0) over.Add(ids[lifelines.OrderBy(l => Math.Abs(l.Bounds.Center.X - c.Bounds.Center.X)).First().Element]);
            if (over.Count == 0) continue;
            var target = over.Count > 1 ? $"{over[0]},{over[^1]}" : over[0];
            var text = c.Element.Get(FieldKeys.Body) is { Length: > 0 } b ? b : c.Element.Name;
            events.Add((c.Bounds.Y + 14, 1, $"Note over {target}: {text.Replace("\r", "").Replace("\n", "<br>").Replace(";", "#59;")}", SeqEvent.Line));
        }
        var depth = 1;
        foreach (var ev in events.OrderBy(e => e.Y).ThenBy(e => e.Order))
        {
            if (ev.Kind == SeqEvent.End) depth = Math.Max(1, depth - 1);
            var lines = ev.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var separator = ev.Kind == SeqEvent.Separator || ev.Kind == SeqEvent.Start && i > 0;
                sb.Append(new string(' ', 4 * Math.Max(1, separator ? depth - 1 : depth))).Append(lines[i].TrimEnd()).Append('\n');
            }
            if (ev.Kind == SeqEvent.Start) depth++;
        }
    }

    private enum SeqEvent { Line, Start, Separator, End }

    // ================================================================== BDD / class → classDiagram

    private static readonly ElementKind[] ClassKinds =
    [
        ElementKind.Block, ElementKind.InterfaceBlock, ElementKind.ValueType, ElementKind.Enumeration, ElementKind.Class, ElementKind.Interface,
        ElementKind.DataType, ElementKind.Component, ElementKind.ConstraintBlock, ElementKind.Actor,
    ];

    private static void Class(ModelRepository m, Diagram d, StringBuilder sb)
    {
        sb.Append("classDiagram\n");
        if (LeftToRight(d)) sb.Append("    direction LR\n");
        var ids = new Ids();
        var classes = d.Nodes.Select(n => n.Element).Where(e => ClassKinds.Contains(e.Kind)).Distinct().ToList();
        foreach (var c in classes)
        {
            // Box<T> in Mermaid's own generic notation: class Box~T~.
            var generic = Regex.Match(c.Name, @"^([A-Za-z_][A-Za-z0-9_]*)<([A-Za-z0-9_]+)>$", RegexOptions.None, TimeSpan.FromSeconds(1));
            var id = generic.Success ? ids.Assign(c, generic.Groups[1].Value) : ids[c];
            sb.Append("    class ").Append(id);
            if (generic.Success && id == generic.Groups[1].Value) sb.Append('~').Append(generic.Groups[2].Value).Append('~');
            else if (id != c.Name) sb.Append('[').Append(Q(Plain(c.DisplayName))).Append(']');
            var ann = c.Kind switch
            {
                ElementKind.InterfaceBlock or ElementKind.Interface => "interface",
                ElementKind.Enumeration => "enumeration",
                ElementKind.ValueType or ElementKind.DataType => "valueType",
                _ => c.IsAbstract ? "abstract" : null,
            };
            var members = c.OwnedElements.Where(f => f.Kind is ElementKind.EnumerationLiteral or ElementKind.Property or ElementKind.Operation).ToList();
            if (ann == null && members.Count == 0) { sb.Append('\n'); continue; }
            sb.Append(" {\n");
            if (ann != null) sb.Append("        <<").Append(ann).Append(">>\n");
            foreach (var f in members)
            {
                var vis = f.Get(FieldKeys.Visibility) switch { "public" => "+", "private" => "-", "protected" => "#", "package" => "~", _ => "" };
                switch (f.Kind)
                {
                    case ElementKind.EnumerationLiteral:
                        sb.Append("        ").Append(Member(f.Name)).Append('\n');
                        break;
                    case ElementKind.Property:
                    {
                        var mult = f.Multiplicity is { Length: > 0 } mu && mu != "1" ? $"[{mu}]" : "";
                        var type = m.TypeDisplayName(f);
                        sb.Append("        ").Append(vis).Append(Member(f.Name)).Append(mult);
                        if (type.Length > 0) sb.Append(" : ").Append(Member(type));
                        if (f.Get(FieldKeys.DefaultValue) is { Length: > 0 } dv) sb.Append(" = ").Append(Member(dv));
                        sb.Append('\n');
                        break;
                    }
                    case ElementKind.Operation:
                    {
                        var sig = f.Get(FieldKeys.Signature);
                        var (pars, ret) = SplitSignature(sig);
                        sb.Append("        ").Append(vis).Append(Member(f.Name)).Append('(').Append(Member(pars)).Append(')');
                        if (ret.Length > 0) sb.Append(' ').Append(Member(ret));
                        if (f.IsAbstract) sb.Append('*');
                        sb.Append('\n');
                        break;
                    }
                }
            }
            sb.Append("    }\n");
        }
        var set = classes.ToHashSet();
        foreach (var e in d.Edges)
        {
            var r = e.Relationship;
            if (!set.Contains(r.Source) || !set.Contains(r.Target)) continue;
            string S(Element x) => ids[x];
            string Card(string c) => c.Length > 0 ? $" \"{c}\"" : "";
            var mult = r.Multiplicity;
            var src = Card(r.Get(FieldKeys.SourceMultiplicity));
            var label = r.Name.Length > 0 ? " : " + Member(Plain(r.Name)) : "";
            switch (r.Kind)
            {
                case ElementKind.Generalization: sb.Append($"    {S(r.Target)} <|-- {S(r.Source)}\n"); break;
                case ElementKind.Realization: sb.Append($"    {S(r.Target)} <|.. {S(r.Source)}\n"); break;
                case ElementKind.Composition:
                {
                    var part = r.Source.OwnedElements.FirstOrDefault(p => p.Kind == ElementKind.Property && p.TypeRef == r.Target.Id);
                    var partLabel = part != null ? " : " + part.Name : label;
                    sb.Append($"    {S(r.Source)}{src} *--{Card(mult)} {S(r.Target)}{partLabel}\n");
                    break;
                }
                case ElementKind.Aggregation: sb.Append($"    {S(r.Source)}{src} o--{Card(mult)} {S(r.Target)}{label}\n"); break;
                case ElementKind.Association: sb.Append($"    {S(r.Source)}{src} -->{Card(mult)} {S(r.Target)}{label}\n"); break;
                case ElementKind.Dependency: sb.Append($"    {S(r.Source)} ..> {S(r.Target)}{label}\n"); break;
                default: sb.Append($"    %% {Plain(r.Source.DisplayName)} «{Metamodel.DisplayName(r.Kind)}» {Plain(r.Target.DisplayName)} has no Mermaid class notation\n"); break;
            }
        }
        foreach (var c in d.Nodes.Where(n => n.Element.Kind == ElementKind.Comment))
        {
            var text = c.Element.Get(FieldKeys.Body) is { Length: > 0 } b ? b : c.Element.Name;
            sb.Append("    note \"").Append(text.Replace("\"", "'").Replace("\r", "").Replace("\n", "\\n")).Append("\"\n");
        }
    }

    /// <summary>Member text: no characters Mermaid would read as structure.</summary>
    private static string Member(string s) => Plain(s).Replace("<", "~").Replace(">", "~").Replace("{", "(").Replace("}", ")");

    private static (string Params, string Return) SplitSignature(string sig)
    {
        var s = sig.Trim();
        if (!s.StartsWith('(')) return ("", s.TrimStart(':').Trim());
        var close = s.LastIndexOf(')');
        if (close < 0) return (s[1..], "");
        return (s[1..close], s[(close + 1)..].Trim().TrimStart(':').Trim());
    }

    // ================================================================== requirement diagram

    private static readonly HashSet<string> RequirementTypes = new(StringComparer.Ordinal)
        { "requirement", "functionalRequirement", "interfaceRequirement", "performanceRequirement", "physicalRequirement", "designConstraint" };

    private static void Requirement(Diagram d, StringBuilder sb)
    {
        sb.Append("requirementDiagram\n");
        var elements = d.Nodes.Select(n => n.Element).Where(e => e.Kind != ElementKind.Comment && e is not Relationship).Distinct().ToList();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var names = new Dictionary<Element, string>();
        string NameOf(Element e)
        {
            if (names.TryGetValue(e, out var n)) return n;
            var plain = Plain(e.DisplayName).Replace("\"", "'");
            var name = Regex.IsMatch(plain, @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.None, TimeSpan.FromSeconds(1)) ? plain : $"\"{plain}\"";
            for (var i = 2; !used.Add(name); i++) name = $"\"{plain} {i}\"";
            return names[e] = name;
        }
        foreach (var r in elements.Where(e => e.Kind == ElementKind.Requirement))
        {
            var type = r.Get(ReqFields.CustomPrefix + "Type") is { } t && RequirementTypes.Contains(t) ? t : "requirement";
            sb.Append("    ").Append(type).Append(' ').Append(NameOf(r)).Append(" {\n");
            if (r.Get(FieldKeys.RequirementId) is { Length: > 0 } id) sb.Append("        id: ").Append(Plain(id).Replace("\"", "'")).Append('\n');
            if (r.Get(FieldKeys.Text) is { Length: > 0 } text) sb.Append("        text: \"").Append(Plain(text).Replace("\"", "'")).Append("\"\n");
            if (r.Get(ReqFields.Risk) is { Length: > 0 } risk) sb.Append("        risk: ").Append(Cap(risk)).Append('\n');
            if (r.Get(ReqFields.VerificationMethod) is { Length: > 0 } vm) sb.Append("        verifymethod: ").Append(Cap(vm)).Append('\n');
            sb.Append("    }\n");
        }
        foreach (var e in elements.Where(e => e.Kind != ElementKind.Requirement))
        {
            sb.Append("    element ").Append(NameOf(e)).Append(" {\n");
            sb.Append("        type: \"").Append(e.Kind == ElementKind.TestCase ? "test case" : Metamodel.DisplayName(e.Kind)).Append("\"\n");
            var docref = e.Documentation.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("docref:", StringComparison.Ordinal));
            if (docref != null) sb.Append("        docref: ").Append(docref["docref:".Length..].Trim()).Append('\n');
            sb.Append("    }\n");
        }
        var set = elements.ToHashSet();
        foreach (var r in elements.Where(e => e.Kind == ElementKind.Requirement && e.Owner is { Kind: ElementKind.Requirement } p && set.Contains(p)))
            sb.Append("    ").Append(NameOf(r.Owner!)).Append(" - contains -> ").Append(NameOf(r)).Append('\n');
        foreach (var e in d.Edges)
        {
            var r = e.Relationship;
            if (!set.Contains(r.Source) || !set.Contains(r.Target)) continue;
            var verb = r.Kind switch
            {
                ElementKind.Satisfy => "satisfies",
                ElementKind.Verify => "verifies",
                ElementKind.Trace => "traces",
                ElementKind.DeriveReqt => "derives",
                ElementKind.Refine => "refines",
                ElementKind.Copy => "copies",
                _ => null,
            };
            if (verb == null) continue;
            sb.Append("    ").Append(NameOf(r.Source)).Append(" - ").Append(verb).Append(" -> ").Append(NameOf(r.Target)).Append('\n');
        }
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
