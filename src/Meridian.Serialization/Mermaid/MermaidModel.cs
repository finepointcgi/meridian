namespace Meridian.Serialization.Mermaid;

/// <summary>The Mermaid diagram types the importer understands.</summary>
public enum MermaidDiagramKind { Flowchart, Class, State, Sequence, Requirement, EntityRelationship, C4, Ishikawa, Radar, Treemap, Unsupported }

/// <summary>A parsed node: flowchart shape, class, state, participant, requirement, entity or C4 element.</summary>
internal sealed class MNode
{
    public required string Id { get; init; }
    public string Label { get; set; } = "";
    /// <summary>Shape or role: "rect", "round", "diamond", "circle", "dcircle", "db", "fork", "initial", "final", "choice", "participant", "actor", "interface"…</summary>
    public string Shape { get; set; } = "";
    /// <summary>Subgraph / namespace / boundary id holding this node.</summary>
    public string? Group { get; set; }
    /// <summary>Composite state holding this state.</summary>
    public string? Parent { get; set; }
    public List<string> Members { get; } = new();
    public Dictionary<string, string> Props { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Line { get; init; }
    /// <summary>Was the node declared with a label or shape (rather than only mentioned in an edge)?</summary>
    public bool Declared { get; set; }
}

internal sealed class MEdge
{
    public required string From { get; init; }
    public required string To { get; init; }
    public string Label { get; set; } = "";
    /// <summary>Relationship tag: "flow", "dotted", "thick", "generalization", "composition", "satisfies"…</summary>
    public string Kind { get; set; } = "";
    public string FromCard { get; set; } = "";
    public string ToCard { get; set; } = "";
    /// <summary>Arrow heads at both ends (<c>&lt;--&gt;</c>).</summary>
    public bool Bidirectional { get; set; }
    public int Line { get; init; }
}

internal sealed class MGroup
{
    public required string Id { get; init; }
    public string Label { get; set; } = "";
    public string? Parent { get; set; }
}

internal sealed class MNote
{
    public string? Target { get; init; }
    public string Text { get; init; } = "";
}

/// <summary>Sequence diagram events, in order.</summary>
internal abstract record MSeq(int Line);
internal sealed record MSeqMessage(int Line, string From, string To, string Text, string Sort, bool Activate, bool Deactivate) : MSeq(Line);
internal sealed record MSeqFragmentStart(int Line, string Operator, string Guard) : MSeq(Line);
internal sealed record MSeqFragmentElse(int Line, string Guard) : MSeq(Line);
internal sealed record MSeqFragmentEnd(int Line) : MSeq(Line);
internal sealed record MSeqNote(int Line, IReadOnlyList<string> Targets, string Text) : MSeq(Line);

/// <summary>One parsed Mermaid diagram, independent of the model (used for the preview and by the builder).</summary>
internal sealed class MGraph
{
    public required MermaidDiagramKind Kind { get; init; }
    public required string Keyword { get; init; }
    public required int Line { get; init; }
    public string? Title { get; set; }
    /// <summary>TB, BT, LR or RL.</summary>
    public string Direction { get; set; } = "TB";

    public List<MNode> Nodes { get; } = new();
    public Dictionary<string, MNode> ById { get; } = new(StringComparer.Ordinal);
    public List<MEdge> Edges { get; } = new();
    public List<MGroup> Groups { get; } = new();
    public List<MNote> Notes { get; } = new();
    public List<MSeq> Sequence { get; } = new();
    /// <summary>The data of an Ishikawa, radar or treemap chart (drawn on a whiteboard).</summary>
    public MChart? Chart { get; set; }
    public List<string> Warnings { get; } = new();

    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    public bool NodeLimitHit, EdgeLimitHit;

    public void Warn(int line, string message) => Warnings.Add(line > 0 ? $"{Describe()} line {line}: {message}" : $"{Describe()} {message}");

    /// <summary>Warns once per <paramref name="key"/>.</summary>
    public void WarnOnce(string key, int line, string message)
    {
        if (_warned.Add(key)) Warn(line, message);
    }

    public string Describe() => Title is { Length: > 0 } t ? $"{Keyword} “{t}”," : $"{Keyword} (line {Line}),";

    /// <summary>The node with <paramref name="id"/>, created on first mention; null once the node limit is reached.</summary>
    public MNode? Node(string id, int line)
    {
        if (ById.TryGetValue(id, out var n)) return n;
        if (Nodes.Count >= MermaidLimits.MaxNodes)
        {
            if (!NodeLimitHit) Warn(line, $"more than {MermaidLimits.MaxNodes} nodes; the rest were ignored.");
            NodeLimitHit = true;
            return null;
        }
        n = new MNode { Id = id, Label = id, Line = line };
        Nodes.Add(n);
        ById[id] = n;
        return n;
    }

    public MEdge? Edge(string from, string to, int line)
    {
        if (Edges.Count >= MermaidLimits.MaxEdges)
        {
            if (!EdgeLimitHit) Warn(line, $"more than {MermaidLimits.MaxEdges} edges; the rest were ignored.");
            EdgeLimitHit = true;
            return null;
        }
        var e = new MEdge { From = from, To = to, Line = line };
        Edges.Add(e);
        return e;
    }

    public bool LeftToRight => Direction is "LR" or "RL";
    public bool Reversed => Direction is "BT" or "RL";
}
