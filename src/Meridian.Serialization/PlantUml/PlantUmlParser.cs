using System.Text.RegularExpressions;
using Meridian.Serialization.Mermaid;

namespace Meridian.Serialization.PlantUml;

/// <summary>
/// Reads PlantUML text (one or several <c>@startuml … @enduml</c> blocks) into the same parsed form as Mermaid, so the
/// Mermaid builder makes the model elements and lays them out: sequence diagrams → interactions, class diagrams → BDDs,
/// use case diagrams → use cases and actors, activity diagrams (the current syntax) → activities, state diagrams → state
/// machines. Nothing here throws on bad input; what is skipped is reported as a warning. See docs/PLANTUML.md.
/// </summary>
internal static partial class PlantUmlParser
{
    private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    /// <summary>One <c>@startuml</c> block: its first line, its kind ("uml", "mindmap"…) and its lines.</summary>
    internal sealed record Block(int Line, string Keyword, List<(int No, string Text)> Lines);

    /// <summary>Does <paramref name="text"/> start a PlantUML block (rather than Mermaid)?</summary>
    public static bool LooksLikePlantUml(string? text) =>
        text != null && StartRx().IsMatch(text);

    [GeneratedRegex(@"^\s*@start[a-z]+", Opts | RegexOptions.Multiline, 1000)]
    private static partial Regex StartRx();

    /// <summary>The blocks of <paramref name="text"/>; text without <c>@start…</c> is one block.</summary>
    public static List<Block> Split(string? text, List<string> warnings)
    {
        text ??= "";
        if (text.Length > MermaidLimits.MaxInputChars)
        {
            warnings.Add($"PlantUML: the input is longer than {MermaidLimits.MaxInputChars:N0} characters; the rest was ignored.");
            text = text[..MermaidLimits.MaxInputChars];
        }
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new List<Block>();
        if (!LooksLikePlantUml(text))
        {
            blocks.Add(new Block(1, "uml", lines.Select((l, i) => (i + 1, l)).ToList()));
            return blocks;
        }
        Block? current = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith("@start", StringComparison.OrdinalIgnoreCase))
            {
                if (current != null) blocks.Add(current);
                var kw = new string(t[6..].TakeWhile(char.IsLetter).ToArray()).ToLowerInvariant();
                current = new Block(i + 1, kw.Length > 0 ? kw : "uml", []);
                continue;
            }
            if (t.StartsWith("@end", StringComparison.OrdinalIgnoreCase))
            {
                if (current != null) blocks.Add(current);
                current = null;
                continue;
            }
            current?.Lines.Add((i + 1, lines[i]));
        }
        if (current != null) blocks.Add(current);
        if (blocks.Count > MermaidLimits.MaxDiagrams)
        {
            warnings.Add($"PlantUML: more than {MermaidLimits.MaxDiagrams} diagrams; only the first {MermaidLimits.MaxDiagrams} were read.");
            blocks = blocks.Take(MermaidLimits.MaxDiagrams).ToList();
        }
        return blocks;
    }

    /// <summary>Parses one block.</summary>
    public static MGraph Parse(Block block)
    {
        var (statements, title, direction) = Prepare(block);
        if (block.Keyword != "uml")
        {
            var u = new MGraph { Kind = MermaidDiagramKind.Unsupported, Keyword = $"PlantUML @start{block.Keyword}", Line = block.Line, Title = title };
            u.Warn(0, $"@start{block.Keyword} diagrams are not supported; skipped.");
            return u;
        }
        var kind = Classify(statements);
        var g = new MGraph
        {
            Kind = kind switch
            {
                Kind.Sequence => MermaidDiagramKind.Sequence,
                Kind.Class => MermaidDiagramKind.Class,
                Kind.UseCase => MermaidDiagramKind.UseCase,
                Kind.Activity => MermaidDiagramKind.Flowchart,
                Kind.State => MermaidDiagramKind.State,
                _ => MermaidDiagramKind.Unsupported,
            },
            Keyword = kind switch
            {
                Kind.Sequence => "PlantUML sequence diagram",
                Kind.Class => "PlantUML class diagram",
                Kind.UseCase => "PlantUML use case diagram",
                Kind.Activity => "PlantUML activity diagram",
                Kind.State => "PlantUML state diagram",
                _ => "PlantUML diagram",
            },
            Line = block.Line,
            Title = title,
            Direction = direction,
        };
        if (statements.Count > MermaidLimits.MaxStatements)
        {
            g.Warn(0, $"more than {MermaidLimits.MaxStatements} statements; the rest were ignored.");
            statements = statements.Take(MermaidLimits.MaxStatements).ToList();
        }
        switch (kind)
        {
            case Kind.Sequence: ParseSequence(g, statements); break;
            case Kind.Class: ParseClass(g, statements); break;
            case Kind.UseCase: ParseUseCase(g, statements); break;
            case Kind.Activity: ParseActivity(g, statements); break;
            case Kind.State: ParseState(g, statements); break;
            case Kind.OldActivity: g.Warn(0, "activity diagrams in the old syntax ((*) --> \"Step\") are not supported; use the current syntax (start, :step;, stop)."); break;
            default: g.Warn(0, "the kind of diagram could not be recognised (component, deployment, object and timing diagrams are not supported); skipped."); break;
        }
        return g;
    }

    // ================================================================== preparing the lines

    private record struct Stmt(int No, string Text);

    private static readonly string[] SkipPrefixes =
        ["skinparam", "!", "hide ", "show ", "scale", "autonumber", "autoactivate", "allowmixing", "set ", "caption", "newpage", "mainframe", "sprite", "style", "<style"];

    /// <summary>Drops comments, styling and layout hints; reads the title and the direction.</summary>
    private static (List<Stmt> Statements, string? Title, string Direction) Prepare(Block block)
    {
        var list = new List<Stmt>();
        string? title = null;
        var direction = "TB";
        var inBlockComment = false;
        string? skipUntil = null;
        foreach (var (no, raw) in block.Lines)
        {
            var t = raw.Trim();
            if (inBlockComment)
            {
                var end = t.IndexOf("'/", StringComparison.Ordinal);
                if (end < 0) continue;
                inBlockComment = false;
                t = t[(end + 2)..].Trim();
            }
            var open = t.IndexOf("/'", StringComparison.Ordinal);
            if (open >= 0)
            {
                var close = t.IndexOf("'/", open + 2, StringComparison.Ordinal);
                if (close < 0) { inBlockComment = true; t = t[..open].Trim(); }
                else t = (t[..open] + t[(close + 2)..]).Trim();
            }
            if (t.Length == 0 || t.StartsWith('\'')) continue;
            if (skipUntil != null)
            {
                if (Regex.IsMatch(t, skipUntil, Opts, TimeSpan.FromSeconds(1))) skipUntil = null;
                continue;
            }
            var lower = t.ToLowerInvariant();
            if (lower is "legend" || lower.StartsWith("legend ")) { skipUntil = @"^end\s*legend$"; continue; }
            if (lower is "header" or "footer") { skipUntil = $@"^end\s*{lower}$"; continue; }
            if (lower.StartsWith("header ") || lower.StartsWith("footer ")) continue;
            if (lower == "title") { skipUntil = @"^end\s*title$"; continue; }
            if (lower.StartsWith("title ")) { title = Label(t[6..]); continue; }
            if (lower.StartsWith("skinparam") && t.EndsWith('{')) { skipUntil = @"^\}$"; continue; }
            if (lower.StartsWith("<style>")) { skipUntil = @"^</style>$"; continue; }
            if (lower == "left to right direction") { direction = "LR"; continue; }
            if (lower == "top to bottom direction") { direction = "TB"; continue; }
            if (SkipPrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal))) continue;
            list.Add(new Stmt(no, t));
        }
        return (list, title, direction);
    }

    // ================================================================== recognising the kind

    private enum Kind { Unknown, Sequence, Class, UseCase, Activity, State, OldActivity }

    // "end" alone is left out: it also closes sequence fragments.
    [GeneratedRegex(@"^(start|stop|fork|fork again|end fork|end merge|endif|endwhile|repeat|detach|kill|split|end split)$|^if\s*\(.*\)\s*then|^while\s*\(|^repeat\s+while|^:.*;$|^switch\s*\(", Opts, 1000)]
    private static partial Regex ActivityMarker();

    [GeneratedRegex(@"\[\*\]|^state\s", Opts, 1000)]
    private static partial Regex StateMarker();

    [GeneratedRegex(@"^(abstract\s+class|abstract|class|interface|enum|annotation|struct|exception|record|dataclass|protocol|metaclass)\s|^entity\s+\S+.*\{\s*$", Opts, 1000)]
    private static partial Regex ClassMarker();

    // Inheritance and composition arrows: class diagrams, but checked after use case markers (actors inherit too).
    [GeneratedRegex(@"<\|--|--\|>|<\|\.\.|\.\.\|>|\*--|--\*|o--|--o\s", Opts, 1000)]
    private static partial Regex ClassArrowMarker();

    [GeneratedRegex(@"^usecase\s|^\([^)]+\)|\s\([^)]+\)\s*(:.*)?$|^:[^:;]+:|^rectangle\s", Opts, 1000)]
    private static partial Regex UseCaseMarker();

    [GeneratedRegex(@"^(participant|boundary|control|database|collections|queue)\s|^(alt|opt|loop|par|group|critical|break)\b|^note\s+over\b|^(activate|deactivate|return)\b|^\S+\s*-+>>?\s*\S+\s*:", Opts, 1000)]
    private static partial Regex SequenceMarker();

    [GeneratedRegex(@"\(\*\)", Opts, 1000)]
    private static partial Regex OldActivityMarker();

    private static Kind Classify(List<Stmt> s)
    {
        bool Has(Regex r) => s.Any(x => r.IsMatch(x.Text));
        if (Has(OldActivityMarker())) return Kind.OldActivity;
        if (Has(ActivityMarker())) return Kind.Activity;
        if (Has(StateMarker())) return Kind.State;
        if (Has(ClassMarker())) return Kind.Class;
        if (Has(UseCaseMarker())) return Kind.UseCase;
        if (Has(ClassArrowMarker())) return Kind.Class;
        if (Has(SequenceMarker())) return Kind.Sequence;
        if (s.Any(x => x.Text.StartsWith("actor ", StringComparison.OrdinalIgnoreCase)) && s.Any(x => x.Text.Contains("->", StringComparison.Ordinal))) return Kind.Sequence;
        return Kind.Unknown;
    }

    // ================================================================== shared helpers

    /// <summary>A label as written: quotes removed, PlantUML's "\n" turned into a line break, creole bold / italics markup dropped.</summary>
    internal static string Label(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        s = s.Replace("\\n", "\n", StringComparison.Ordinal);
        s = Regex.Replace(s, @"\*\*|//|__|""""", "", RegexOptions.None, TimeSpan.FromSeconds(1));
        return MermaidSource.Name(s);
    }

    [GeneratedRegex(@"<<[^>]*>>", Opts, 1000)]
    private static partial Regex StereotypeRx();

    [GeneratedRegex(@"\s#[\w#.;:\-]+", Opts, 1000)]
    private static partial Regex ColourRx();

    [GeneratedRegex(@"^(?<a>""[^""]*""|\S+)\s+as\s+(?<b>""[^""]*""|\S+)$", Opts, 1000)]
    private static partial Regex AliasRx();

    /// <summary>"Long name" as X, X as "Long name", Name as X, or a bare name: the id and the label.</summary>
    private static (string Id, string Label) Alias(string rest)
    {
        rest = ColourRx().Replace(" " + StereotypeRx().Replace(rest, ""), "").Trim();
        rest = Regex.Replace(rest, @"\s+order\s+-?\d+$", "", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Trim();
        var m = AliasRx().Match(rest);
        if (!m.Success) return (Unquote(rest), Label(rest));
        var a = m.Groups["a"].Value;
        var b = m.Groups["b"].Value;
        if (b.StartsWith('"') && !a.StartsWith('"')) return (a, Label(b));
        return (Unquote(b), Label(a));
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
    }

    private static string? Stereotype(string s) => StereotypeRx().Match(s) is { Success: true } m ? m.Value[2..^2].Trim() : null;

    /// <summary>Collects the lines of a multi-line note (until "end note") starting after <paramref name="i"/>; moves <paramref name="i"/> past it.</summary>
    private static string NoteBody(List<Stmt> s, ref int i)
    {
        var lines = new List<string>();
        while (++i < s.Count && !Regex.IsMatch(s[i].Text, @"^end\s*[hr]?note$", Opts, TimeSpan.FromSeconds(1))) lines.Add(s[i].Text);
        return MermaidSource.Clean(string.Join("\n", lines));
    }

    [GeneratedRegex(@"^[hr]?note\s+(?<side>left|right|top|bottom|over)(?:\s+of)?(?:\s+(?<targets>[^:]+?))?\s*(?::\s*(?<text>.*))?$", Opts, 1000)]
    private static partial Regex NoteRx();

    [GeneratedRegex(@"^note\s+""(?<text>[^""]*)""\s+as\s+(?<id>\S+)$|^note\s+as\s+(?<id2>\S+)$", Opts, 1000)]
    private static partial Regex FloatingNoteRx();

    /// <summary>Reads a note statement at <paramref name="i"/>: its targets and text; null when the statement is not a note.</summary>
    private static (List<string> Targets, string Text)? Note(List<Stmt> s, ref int i)
    {
        var m = NoteRx().Match(s[i].Text);
        if (!m.Success) return null;
        var targets = m.Groups["targets"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Unquote).ToList();
        var text = m.Groups["text"].Success ? Label(m.Groups["text"].Value) : NoteBody(s, ref i);
        return (targets, text);
    }
}
