namespace Meridian.Serialization.Reports;

// The resolved document: every query, loop and placeholder evaluated, headings and captions numbered.
// The PDF, Word and HTML writers only lay this out; they never touch the model.

public enum XRefPart { Label, Page, Title }

/// <summary>A run of inline content.</summary>
public abstract record Inline;
public sealed record TextRun(string Text, bool Bold = false, bool Italic = false, bool Code = false) : Inline;
/// <summary>A cross-reference to a heading, figure or table, resolved by <see cref="ResolvedDocument.Targets"/>.</summary>
public sealed record XRefRun(string Name, XRefPart Part) : Inline;

public abstract record DocNode;

/// <summary>Front-matter placeholders, expanded by each writer.</summary>
public sealed record TitlePageNode : DocNode;
public sealed record RevisionHistoryNode : DocNode;
public enum ListingKind { Contents, Figures, Tables }
public sealed record ListingNode(ListingKind Kind) : DocNode;

/// <summary>A heading. <see cref="Number"/> is "" for unnumbered headings; <see cref="Level"/> is 1-based.</summary>
public sealed record HeadingNode(int Level, string Number, string Text, string Anchor, bool PageBreakBefore, bool InToc = true) : DocNode
{
    public string Display => Number.Length == 0 ? Text : Number + "  " + Text;
}

public enum ParaKind { Normal, Bullet, Numbered, Quote, Code, Subheading }
public sealed record ParagraphNode(IReadOnlyList<Inline> Inlines, ParaKind Kind = ParaKind.Normal, int ListNumber = 0, int Indent = 0) : DocNode
{
    public static ParagraphNode Plain(string text, bool italic = false) => new([new TextRun(text, Italic: italic)]);
}

/// <summary>"Figure 3-2: Vehicle structure" — <see cref="Kind"/> is "Figure" or "Table".</summary>
public sealed record Caption(string Kind, int Chapter, int Seq, string Number, string Text, string Anchor)
{
    public string Label => $"{Kind} {Number}";
    public string Display => Text.Length == 0 ? Label : $"{Label}: {Text}";
}

public sealed record TableNode(Caption? Caption, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows,
                               IReadOnlyList<double>? Widths = null, bool Compact = false, bool KeyValue = false, bool Matrix = false) : DocNode;

public sealed record FigureNode(Caption Caption, string Svg, double Width, double Height, double MaxHeightFraction, string Key) : DocNode;

public sealed record PageBreakNode : DocNode;

/// <summary>Starts a new page run with the given orientation (a Word section break / a new QuestPDF page set).</summary>
public sealed record OrientationNode(bool Landscape) : DocNode;

public enum TargetKind { Section, Figure, Table }
public sealed record RefTarget(TargetKind Kind, string Label, string Title, string Anchor);

public sealed class ResolvedDocument
{
    public required DocTemplate Template { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string DocumentNumber { get; init; } = "";
    public string Revision { get; init; } = "";
    public string Classification { get; init; } = "";
    public string Author { get; init; } = "";
    public string Organization { get; init; } = "";
    public string Date { get; init; } = "";
    public string ModelName { get; init; } = "";
    public IReadOnlyList<DocNode> TitleNotes { get; init; } = [];
    public List<DocNode> Nodes { get; } = new();
    public List<HeadingNode> Headings { get; } = new();
    public List<Caption> Figures { get; } = new();
    public List<Caption> Tables { get; } = new();
    /// <summary>Cross-reference targets by name (case-insensitive).</summary>
    public Dictionary<string, RefTarget> Targets { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; } = new();

    /// <summary>Text for a cross-reference when the writer cannot produce a live field ("Figure 3-2", "?" for pages).</summary>
    public string XRefText(XRefRun x, string? page = null)
    {
        if (!Targets.TryGetValue(x.Name, out var t)) return $"[missing reference: {x.Name}]";
        return x.Part switch { XRefPart.Page => page ?? t.Label, XRefPart.Title => t.Title, _ => t.Label };
    }

    public string HeaderLeft => DocumentNumber.Length > 0 ? DocumentNumber + (Revision.Length > 0 ? " Rev " + Revision : "") : Title;
}

public sealed record ReportProgress(string Phase, double Fraction);
