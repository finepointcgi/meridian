using System.Globalization;
using System.Text.RegularExpressions;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization.Mermaid;

namespace Meridian.Serialization.DrawIo;

/// <summary>What a draw.io page becomes.</summary>
public enum DrawIoPageKind { Drawing, UseCase, Class, Activity, State, Sequence, Requirement, Empty, InternalBlock, Parametric }

public sealed record DrawIoImportOptions
{
    /// <summary>Turn UML pages (use case, class, activity, state, sequence) into model elements; false keeps every page a drawing.</summary>
    public bool ModelElements { get; init; } = true;

    /// <summary>How blocks, actors and use cases relate to elements already in the model.</summary>
    public MermaidMatchMode Match { get; init; } = MermaidMatchMode.CreateNew;
}

/// <summary>What one page will become, for the preview.</summary>
public sealed record DrawIoPageSummary(string Page, string DiagramName, DrawIoPageKind Kind, int Shapes, int Lines)
{
    public string Becomes => DrawIoImporter.Becomes(Kind);
    public bool Supported => Kind != DrawIoPageKind.Empty;
    public string Counts => $"{Shapes} shape{(Shapes == 1 ? "" : "s")} · {Lines} line{(Lines == 1 ? "" : "s")}";
}

/// <summary>
/// Imports draw.io / diagrams.net pages. A page drawn with UML shapes becomes model elements and a SysML diagram (use case,
/// block definition, activity, state machine or sequence) at the positions it had in draw.io; any other page becomes a
/// whiteboard diagram of free shapes and lines with its colours and layout. See docs/DRAWIO.md.
/// </summary>
public static class DrawIoImporter
{
    public static string Becomes(DrawIoPageKind kind) => kind switch
    {
        DrawIoPageKind.UseCase => "Use case diagram",
        DrawIoPageKind.Class => "Block definition diagram",
        DrawIoPageKind.Activity => "Activity diagram",
        DrawIoPageKind.State => "State machine diagram",
        DrawIoPageKind.Sequence => "Sequence diagram",
        DrawIoPageKind.Requirement => "Requirement diagram",
        DrawIoPageKind.InternalBlock => "Internal block diagram",
        DrawIoPageKind.Parametric => "Parametric diagram",
        DrawIoPageKind.Drawing => "Whiteboard drawing",
        _ => "Skipped (empty page)",
    };

    /// <summary>Reads a file without touching any model: what each page will become.</summary>
    public static IReadOnlyList<DrawIoPageSummary> Preview(string fileName, IReadOnlyList<DrawIoPage> pages, DrawIoImportOptions? options = null)
    {
        options ??= new DrawIoImportOptions();
        var list = new List<DrawIoPageSummary>();
        foreach (var (page, i) in pages.Select((p, i) => (p, i)))
        {
            var g = DGraph.From(page);
            var kind = Classify(g, options.ModelElements);
            // Rows of class boxes, activation bars and edge labels are parts of other shapes, not shapes of their own.
            list.Add(new DrawIoPageSummary(page.Name, DiagramName(fileName, pages, i), kind, TopShapes(g).Count, g.Edges.Count));
        }
        return list;
    }

    /// <summary>Imports every page of a file into <paramref name="target"/> as one undo step.</summary>
    public static MermaidImportResult Import(ModelRepository m, Element target, string fileName, IReadOnlyList<DrawIoPage> pages, DrawIoImportOptions? options = null)
    {
        options ??= new DrawIoImportOptions();
        if (!m.Contains(target) || target.Kind is not (ElementKind.Package or ElementKind.Model)) target = m.Root;
        var warnings = new List<string>();
        var elementsBefore = m.AllElements.Count(e => e is not Relationship && e is not Diagram);
        var relationshipsBefore = m.Relationships.Count();
        List<Diagram> diagrams;
        using (m.History.Transaction("Import " + SourceOf(fileName)))
        {
            var builder = new MermaidBuilder(m, target, new MermaidImportOptions { Match = options.Match }, warnings) { Source = SourceOf(fileName) };
            diagrams = ImportPages(m, builder, fileName, pages, options, warnings);
        }
        return new MermaidImportResult(diagrams,
            m.AllElements.Count(e => e is not Relationship && e is not Diagram) - elementsBefore,
            m.Relationships.Count() - relationshipsBefore, warnings);
    }

    /// <summary>A new model named after the file holding its pages ("Open a file as a new project").</summary>
    public static (ModelRepository Model, ImportReport Report) ImportAsNewProject(string fileName, byte[] bytes)
    {
        var name = BaseName(fileName);
        var model = new ModelRepository(name.Length > 0 ? name : SourceOf(fileName) + " import");
        var result = Import(model, model.Root, fileName, DrawIoFile.Read(fileName, bytes));
        model.History.Clear();
        return (model, result.ToReport());
    }

    /// <summary>"architecture.drawio.svg" → "architecture".</summary>
    public static string BaseName(string? fileName)
    {
        var n = Path.GetFileName(fileName ?? "");
        foreach (var ext in DrawIoFile.Extensions.OrderByDescending(e => e.Length))
            if (n.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return n[..^ext.Length];
        return Path.GetFileNameWithoutExtension(n);
    }

    /// <summary>"Gliffy" for a .gliffy file, "Visio" for a .vsdx, else "draw.io" (for warnings and the diagrams' documentation).</summary>
    internal static string SourceOf(string? fileName) =>
        Gliffy.GliffyFile.IsGliffyFile(fileName) ? "Gliffy" : Visio.VisioFile.IsVisioFile(fileName) ? "Visio" : "draw.io";

    private static bool IsDefaultPageName(string name) => Regex.IsMatch(name, @"^(Page|Seite|Página|Pagina|Страница)[- ]?\d+$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    /// <summary>The page's name, or the file's for a single page left with draw.io's default name.</summary>
    internal static string DiagramName(string fileName, IReadOnlyList<DrawIoPage> pages, int index)
    {
        var page = pages[index].Name.Trim();
        var file = BaseName(fileName);
        if (file.Length == 0) file = SourceOf(fileName);
        if (!IsDefaultPageName(page) && page.Length > 0) return page;
        return pages.Count == 1 ? file : $"{file} {page}";
    }

    internal static List<Diagram> ImportPages(ModelRepository m, MermaidBuilder builder, string fileName, IReadOnlyList<DrawIoPage> pages, DrawIoImportOptions options, List<string> warnings)
    {
        var diagrams = new List<Diagram>();
        var source = SourceOf(fileName);
        for (var i = 0; i < pages.Count; i++)
        {
            var g = DGraph.From(pages[i]);
            warnings.AddRange(pages[i].Warnings);
            warnings.AddRange(g.Warnings);
            var kind = Classify(g, options.ModelElements);
            if (kind == DrawIoPageKind.Empty) { warnings.Add($"{source} page “{pages[i].Name}” is empty; skipped."); continue; }
            var name = Unique(m, DiagramName(fileName, pages, i));
            var page = new PageBuilder(m, builder, g, name, warnings, source);
            try
            {
                var d = kind switch
                {
                    DrawIoPageKind.UseCase => page.UseCase(),
                    DrawIoPageKind.Class => page.Class(),
                    DrawIoPageKind.Activity => page.Activity(),
                    DrawIoPageKind.State => page.State(),
                    DrawIoPageKind.Sequence => page.Sequence(),
                    DrawIoPageKind.Requirement => page.Requirement(),
                    DrawIoPageKind.InternalBlock => page.Structure(DiagramType.InternalBlock),
                    DrawIoPageKind.Parametric => page.Structure(DiagramType.Parametric),
                    _ => page.Drawing(),
                };
                if (d != null) diagrams.Add(d);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                warnings.Add($"{source} page “{pages[i].Name}” could not be imported completely: {ex.Message}");
            }
        }
        return diagrams;
    }

    private static string Unique(ModelRepository m, string name)
    {
        var used = m.Diagrams.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var candidate = name;
        for (var i = 2; used.Contains(candidate); i++) candidate = $"{name} {i}";
        return candidate;
    }

    // ================================================================== recognising shapes

    internal static bool IsLifeline(DCell c) => c.Vertex && c.Shape == "umlLifeline";
    internal static bool IsActor(DCell c) => c.Vertex && c.Shape == "umlActor";
    internal static bool IsStart(DCell c) => c.Vertex && (c.Shape is "startState" or "mxgraph.flowchart.start_2" || c.Shape == "ellipse" && IsBlack(c.S("fillColor")) && Math.Max(c.Local.Width, c.Local.Height) <= 50 && c.Text.Length == 0);
    internal static bool IsEnd(DCell c) => c.Vertex && c.Shape is "endState";
    internal static bool IsDecision(DCell c) => c.Vertex && c.Shape is "rhombus" or "mxgraph.flowchart.decision";
    internal static bool IsBar(DCell c) => c.Vertex && (c.Shape == "line" && c.Text.Length == 0 || IsBlack(c.S("fillColor")) && c.Text.Length == 0 && Math.Min(c.Local.Width, c.Local.Height) <= 12 && Math.Max(c.Local.Width, c.Local.Height) >= 30);
    internal static bool IsNote(DCell c) => c.Vertex && c.Shape is "note" or "mxgraph.flowchart.annotation_1" or "mxgraph.flowchart.annotation_2";
    internal static bool IsFrame(DCell c) => c.Vertex && c.Shape == "umlFrame";
    internal static bool IsText(DCell c) => c.Vertex && (c.BaseShape == "text" || c.Shape == "text");
    internal static bool IsSwimlane(DCell c) => c.Vertex && (c.BaseShape == "swimlane" || c.Has("swimlane"));
    internal static bool IsUseCaseEllipse(DCell c) => c.Vertex && c.BaseShape == "ellipse" && !c.Has("shape") && c.Text.Length > 0;

    private static bool IsBlack(string color) => color.Equals("#000000", StringComparison.OrdinalIgnoreCase) || color.Equals("#000", StringComparison.OrdinalIgnoreCase) || color.Equals("black", StringComparison.OrdinalIgnoreCase);

    private static readonly Regex StereotypeLine = new(@"^\s*(«|<<)\s*(?<s>[^»>]+?)\s*(»|>>)\s*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly HashSet<string> ClassKindNames =
        ["Block", "Class", "InterfaceBlock", "Interface", "ValueType", "Enumeration", "DataType", "Component", "ConstraintBlock"];

    /// <summary>A box whose first line is «requirement».</summary>
    internal static bool IsRequirementBox(DCell c) =>
        c.Vertex && (c.S("meridian") == nameof(ElementKind.Requirement)
                     || StereotypeLine.Match(c.Text.Split('\n')[0]) is { Success: true } s && s.Groups["s"].Value.Trim().ToLowerInvariant() is "requirement" or "functionalrequirement" or "performancerequirement" or "designconstraint");

    /// <summary>A UML class box: a stack-layout swimlane with rows, an HTML label with compartments, or an ER table.</summary>
    internal static bool IsClassBox(DCell c)
    {
        if (!c.Vertex || IsRequirementBox(c)) return false;
        if (c.S("meridian") is { Length: > 0 } hint) return ClassKindNames.Contains(hint);
        if (IsSwimlane(c) && c.S("childLayout") == "stackLayout") return true;
        if (c.Shape == "table" && c.Children.Count > 0) return true;
        if (c.RawLabel.Contains("<hr", StringComparison.OrdinalIgnoreCase) && c.Shape is not ("note" or "umlFrame")) return true;
        var first = c.Text.Split('\n')[0];
        return c.Shape is "" or "rounded" && StereotypeLine.Match(first) is { Success: true } s
            && s.Groups["s"].Value.ToLowerInvariant() is "block" or "class" or "interface" or "interfaceblock" or "enumeration" or "enum" or "valuetype" or "datatype" or "abstract";
    }

    /// <summary>Vertices that are not parts of a class box, table or lifeline (rows, activation bars).</summary>
    internal static List<DCell> TopShapes(DGraph g)
    {
        var inside = new HashSet<DCell>();
        void Mark(DCell c) { foreach (var ch in c.Children) if (inside.Add(ch)) Mark(ch); }
        foreach (var v in g.Vertices.Where(v => IsClassBox(v) || IsLifeline(v) || v.Shape is "tableRow" or "partialRectangle"))
            if (!inside.Contains(v)) Mark(v);
        return g.Vertices.Where(v => !inside.Contains(v) && v.S("style") != "group").ToList();
    }

    internal static DrawIoPageKind Classify(DGraph g, bool modelElements)
    {
        if (g.Vertices.Count == 0) return DrawIoPageKind.Empty;
        if (!modelElements) return DrawIoPageKind.Drawing;
        var top = TopShapes(g);
        // Pages exported by Meridian name each shape's kind ("meridian=State"), which draw.io keeps.
        var hints = top.Select(v => v.S("meridian")).Where(h => h.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (hints.Count > 0)
        {
            // Internal block and parametric diagrams written by Meridian name their kind.
            if (top.Select(v => v.S("meridianDiagram")).FirstOrDefault(s => s.Length > 0) is { } structure)
            {
                if (structure == nameof(DiagramType.InternalBlock)) return DrawIoPageKind.InternalBlock;
                if (structure == nameof(DiagramType.Parametric)) return DrawIoPageKind.Parametric;
            }
            if (hints.Contains(nameof(ElementKind.Lifeline))) return DrawIoPageKind.Sequence;
            if (hints.Overlaps(["State", "InitialState", "FinalState", "Pseudostate"])) return DrawIoPageKind.State;
            if (hints.Overlaps(["Action", "InitialNode", "ActivityFinalNode", "DecisionNode", "ForkNode"])) return DrawIoPageKind.Activity;
            if (hints.Contains(nameof(ElementKind.UseCase))) return DrawIoPageKind.UseCase;
            var reqs = top.Count(v => v.S("meridian") == nameof(ElementKind.Requirement));
            var blocks = top.Count(v => v.S("meridian") is { Length: > 0 } h && ClassKindNames.Contains(h));
            if (reqs > 0 && reqs >= blocks) return DrawIoPageKind.Requirement;
            if (blocks > 0) return DrawIoPageKind.Class;
            return DrawIoPageKind.Drawing;
        }
        var lifelines = top.Where(IsLifeline).ToList();
        if (lifelines.Count >= 2 || lifelines.Count == 1 && g.Edges.Any(e => g.End(e.Source, IsLifeline) != null))
            return DrawIoPageKind.Sequence;
        var actors = top.Count(IsActor);
        var useCases = top.Count(IsUseCaseEllipse);
        if (actors > 0 && useCases > 0) return DrawIoPageKind.UseCase;
        var classes = top.Count(IsClassBox);
        var requirements = top.Count(IsRequirementBox);
        var ends = top.Count(c => IsStart(c) || IsEnd(c));
        if (requirements > 0 && requirements >= classes) return DrawIoPageKind.Requirement;
        if (classes > 0 && classes >= ends) return DrawIoPageKind.Class;
        if (ends > 0) return LooksLikeStateMachine(g, top) ? DrawIoPageKind.State : DrawIoPageKind.Activity;
        return DrawIoPageKind.Drawing;
    }

    /// <summary>Labels on lines that do not leave a decision ("evt / action", "timeout") read as transitions; guards and yes/no as flows.</summary>
    private static bool LooksLikeStateMachine(DGraph g, List<DCell> top)
    {
        if (top.Any(c => IsSwimlane(c) && c.Children.Any(ch => ch.Vertex && (IsStart(ch) || ch.S("rounded") == "1")) && c.S("horizontal") != "0" && !c.Children.Any(IsDecision)))
            return true;
        var labelled = g.Edges.Where(e => e.FullText.Length > 0).ToList();
        if (labelled.Count == 0) return false;
        var fromDecision = labelled.Count(e => e.Source != null && g.ById.TryGetValue(e.Source, out var s) && IsDecision(s));
        var guards = labelled.Count(e => e.FullText.StartsWith('[') && e.FullText.EndsWith(']'));
        return labelled.Count - fromDecision - guards > labelled.Count / 2;
    }

    internal static double Num(string s, double fallback = 0) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : fallback;

    // ================================================================== building one page

    private sealed class PageBuilder(ModelRepository m, MermaidBuilder builder, DGraph g, string name, List<string> warnings, string source = "draw.io")
    {
        private readonly MGraph _mg = new() { Kind = MermaidDiagramKind.Flowchart, Keyword = source + " page", Line = 0, Title = name };
        private Element Target => builder.Target;

        /// <summary>Page coordinates moved so the drawing starts at (40, 40).</summary>
        private readonly Lazy<Pt> _origin = new(() =>
        {
            var vs = g.Vertices.Where(v => v.Local.Width > 0 || v.Local.Height > 0).ToList();
            return vs.Count == 0 ? new Pt(0, 0) : new Pt(vs.Min(v => v.Abs.X) - 40, vs.Min(v => v.Abs.Y) - 40);
        });

        private Bounds Place(DCell c) => new(
            Math.Round(c.Abs.X - _origin.Value.X), Math.Round(c.Abs.Y - _origin.Value.Y),
            Math.Max(10, Math.Round(c.Abs.Width)), Math.Max(10, Math.Round(c.Abs.Height)));

        private Pt Place(Pt p) => new(Math.Round(p.X - _origin.Value.X), Math.Round(p.Y - _origin.Value.Y));

        private void Warn(string message) => warnings.Add($"{source} page “{name}”: {message}");

        private static string Name(string text) => MermaidSource.Name(text);

        // ---------------------------------------------------------------- internal block and parametric diagrams

        private static string Unhint(string s)
        {
            try { return Uri.UnescapeDataString(s); }
            catch (UriFormatException) { return s; }
        }

        /// <summary>
        /// An internal block or parametric diagram written by Meridian: the block it belongs to (by name, else a new block),
        /// its parts, ports and parameters ("name : Type", owned by the block / the part's type), and connectors and binding
        /// connectors with the parts their ends sit on. Elements of the same name are reused, so a page read back into the
        /// model it came from lands on the same parts.
        /// </summary>
        public Diagram Structure(DiagramType type)
        {
            var hinted = g.Vertices.Where(v => v.S("meridian").Length > 0).ToList();
            var contextName = Unhint(hinted.Select(v => v.S("meridianContext")).FirstOrDefault(s => s.Length > 0) ?? "");
            if (contextName.Length == 0) contextName = name;
            var context = builder.FindShared(contextName, ElementKind.Block, ElementKind.ConstraintBlock, ElementKind.Class)
                          ?? builder.MakeShared(Target, ElementKind.Block, contextName);
            var d = m.CreateDiagram(context, type, name);
            m.Set(d, FieldKeys.Documentation, $"Imported from {source}.");
            var made = new Dictionary<string, (Element E, DiagramNode N)>(StringComparer.Ordinal);
            int Depth(DCell c)
            {
                var depth = 0;
                for (var p = c.Parent; p != null && depth < 50 && g.ById.TryGetValue(p, out var pc) && pc.Vertex; p = pc.Parent) depth++;
                return depth;
            }
            var skipped = 0;
            foreach (var v in g.Vertices.OrderBy(Depth))
            {
                var hint = v.S("meridian");
                if (hint is "Context") continue;
                var parent = v.Parent != null && made.TryGetValue(v.Parent, out var p) ? p : default;
                Element? e;
                switch (hint)
                {
                    case nameof(ElementKind.Property) or nameof(ElementKind.Port):
                    {
                        var kind = hint == nameof(ElementKind.Port) ? ElementKind.Port : ElementKind.Property;
                        var (partName, typeName) = NameAndType(v.Text);
                        if (Unhint(v.S("meridianType")) is { Length: > 0 } t) typeName = t;
                        // A port or parameter drawn on a part belongs to the part's type; anything else to the block.
                        Element? partType = null;
                        if (parent.E is { } pe && v.Parent != null && g.ById.TryGetValue(v.Parent, out var pcell))
                        {
                            partType = pe.TypeRef.Length > 0 ? m.Find(pe.TypeRef) : null;
                            // A part typed only by name (read from a class row): find or make its type from the hint.
                            if (partType == null && Unhint(pcell.S("meridianType")) is { Length: > 0 } ptName && m.Find(TypeFor(ptName, pcell.S("meridianTypeKind"))) is { } resolved)
                            {
                                partType = resolved;
                                m.Set(pe, FieldKeys.Type, resolved.Id);
                            }
                        }
                        var owner = partType != null && Metamodel.CanOwn(partType.Kind, kind) ? partType : context;
                        e = owner.OwnedElements.FirstOrDefault(c => c.Kind == kind && c.Name == partName) ?? m.Create(owner, kind, partName.Length > 0 ? partName : null);
                        if (typeName.Length > 0 && e.TypeRef.Length == 0) m.Set(e, FieldKeys.Type, TypeFor(typeName, v.S("meridianTypeKind")));
                        if (Unhint(v.S("meridianMult")) is { Length: > 0 } mult && e.Get(FieldKeys.Multiplicity).Length == 0) m.Set(e, FieldKeys.Multiplicity, mult);
                        break;
                    }
                    case nameof(ElementKind.Comment):
                        e = m.Create(context, ElementKind.Comment);
                        m.Set(e, FieldKeys.Body, v.Text);
                        break;
                    case "":
                        if (v.Local.Width <= 0 && v.Local.Height <= 0 || v.S("style") == "group") continue;
                        e = AddFreeShape(d, v, context).Shape;
                        made[v.Id] = (e, d.NodeFor(e)!);
                        continue;
                    default:
                        // A block, constraint block or other element shown on the diagram: reused by name.
                        if (!Enum.TryParse<ElementKind>(hint, out var other) || Metamodel.IsRelationship(other)) { skipped++; continue; }
                        var label = Name(v.Text.Split('\n')[^1]);
                        e = builder.FindShared(label, other) ?? (Metamodel.CanOwn(Target.Kind, other) ? builder.MakeShared(Target, other, label) : null);
                        if (e == null) { skipped++; continue; }
                        break;
                }
                if (d.Nodes.Any(n => ReferenceEquals(n.Element, e) && ReferenceEquals(d.ParentOf(n), parent.N))) { skipped++; continue; }
                made[v.Id] = (e, m.AddNode(d, e, Place(v), showRelationships: false, parent.N));
            }
            if (skipped > 0) Warn($"{skipped} shape{(skipped == 1 ? " was" : "s were")} left out (shown twice, or of a kind that has no place here).");

            foreach (var x in g.Edges)
            {
                if (x.Source == null || x.Target == null || !made.TryGetValue(x.Source, out var a) || !made.TryGetValue(x.Target, out var b)) continue;
                var hint = x.S("meridian");
                var kind = Enum.TryParse<ElementKind>(hint, out var k) && Metamodel.IsRelationship(k) ? k
                    : a.E.Kind == ElementKind.Shape || b.E.Kind == ElementKind.Shape ? ElementKind.Line
                    : type == DiagramType.Parametric ? ElementKind.Binding : ElementKind.Connector;
                Relationship? r;
                try { r = m.Connect(kind, a.E, b.E, kind is ElementKind.Connector or ElementKind.Binding ? context : null); }
                catch (InvalidOperationException ex) { Warn($"{a.E.DisplayName} → {b.E.DisplayName}: {ex.Message}; skipped."); continue; }
                if (d.ParentOf(a.N) is { } sp && sp.Element.Kind == ElementKind.Property) m.Set(r, FieldKeys.SourcePart, sp.Element.Id);
                if (d.ParentOf(b.N) is { } tp && tp.Element.Kind == ElementKind.Property) m.Set(r, FieldKeys.TargetPart, tp.Element.Id);
                var item = Unhint(x.S("meridianItem"));
                if (item.Length > 0)
                {
                    m.Set(r, FieldKeys.ItemFlow, item);
                    m.Set(r, FieldKeys.FlowDirection, x.S("meridianFlow") == "reverse" ? "reverse" : "forward");
                }
                if (x.FullText.Length > 0 && x.FullText != item && kind != ElementKind.Binding) m.Set(r, FieldKeys.Name, x.FullText);
                if (m.AddEdge(d, r) is { } edge && x.Points.Count > 0) m.SetWaypoints(d, edge, x.Points.Select(Place).ToList());
            }
            EdgeRouter.Invalidate(d);
            return d;
        }

        /// <summary>"engine : Engine" → ("engine", "Engine"); a name alone has no type.</summary>
        private static (string Name, string Type) NameAndType(string text)
        {
            var t = Name(text);
            var colon = t.IndexOf(':');
            return colon < 0 ? (t, "") : (t[..colon].Trim(), t[(colon + 1)..].Trim().TrimEnd(']').Split('[')[0].Trim());
        }

        /// <summary>The id of the element called <paramref name="typeName"/> (made when missing, of the hinted kind), or a
        /// primitive type's name as Meridian stores it (Real, Integer…).</summary>
        private string TypeFor(string typeName, string kindHint)
        {
            ElementKind[] kinds = [ElementKind.Block, ElementKind.ConstraintBlock, ElementKind.InterfaceBlock, ElementKind.ValueType, ElementKind.Enumeration,
                ElementKind.DataType, ElementKind.Class, ElementKind.Interface, ElementKind.Component];
            if (builder.FindShared(typeName, kinds) is { } found) return found.Id;
            var kind = Enum.TryParse<ElementKind>(kindHint, out var k) && kinds.Contains(k) ? k : (ElementKind?)null;
            var primitive = MermaidBuilder.Primitive(typeName);
            if (kind == null && (primitive != typeName || primitive is "Real" or "Integer" or "Boolean" or "String" or "Number")) return primitive;
            return builder.MakeShared(Target, kind ?? ElementKind.Block, typeName).Id;
        }

        // ---------------------------------------------------------------- drawing (whiteboard)

        public Diagram Drawing()
        {
            var d = m.CreateDiagram(Target, DiagramType.Whiteboard, name);
            m.Set(d, FieldKeys.Documentation, $"Imported from {source}.");
            var made = new Dictionary<string, Element>(StringComparer.Ordinal);
            var icons = 0;
            foreach (var v in g.Vertices)
            {
                if (v.S("style") == "group" || v.BaseShape == "group") continue;   // invisible grouping
                if (v.Local.Width <= 0 && v.Local.Height <= 0) continue;
                var (shape, iconLike) = AddFreeShape(d, v, Target);
                if (iconLike) icons++;
                made[v.Id] = shape;
            }
            if (icons > 0) Warn($"{icons} library icon{(icons == 1 ? "" : "s")} became rectangles with {(icons == 1 ? "its" : "their")} label.");
            AddLines(d, made, null);
            EdgeRouter.Invalidate(d);
            return d;
        }

        /// <summary>A free shape for a draw.io vertex, with its colours. Returns whether it was a library icon drawn as a rectangle.</summary>
        private (Element Shape, bool Icon) AddFreeShape(Diagram d, DCell v, Element owner)
        {
            var (subtype, icon) = FreeSubtype(v);
            var e = m.Create(owner, ElementKind.Shape, v.Text.Length > 0 ? v.Text : null);
            m.Set(e, FieldKeys.Subtype, subtype);
            if (subtype == DiagramExtras.ImageSubtype)
            {
                if (ImageData(v.S("image")) is { } data) m.Set(e, DiagramExtras.ImageField, data);
                else { m.Set(e, FieldKeys.Subtype, "rectangle"); Warn($"the picture “{Shorten(v.S("image"))}” is linked, not embedded, so it was left out."); }
            }
            var node = m.AddNode(d, e, Place(v), showRelationships: false);
            ApplyColours(d, node, v, subtype);
            return (e, icon);
        }

        private void ApplyColours(Diagram d, DiagramNode node, DCell v, string subtype)
        {
            if (Colour(v.S("fillColor")) is { } fill && subtype != "text") m.SetNodeStyle(d, node, "fill", fill);
            else if (IsSwimlane(v) || v.Has("container") && v.S("container") == "1") m.SetNodeStyle(d, node, "fill", "none");
            if (Colour(v.S("strokeColor")) is { } stroke) m.SetNodeStyle(d, node, "stroke", stroke);
            if (Colour(v.S("fontColor")) is { } text) m.SetNodeStyle(d, node, "text", text);
            // draw.io's text is black: on a filled shape keep it readable in a dark theme too.
            else if (subtype != "text" && Colour(v.S("fillColor")) is { } f && f != "none" && Luminance(f) is { } l)
                m.SetNodeStyle(d, node, "text", l > 0.45 ? "#1f2328" : "#ffffff");
        }

        /// <summary>Relative luminance (0 dark … 1 light) of a #rgb / #rrggbb colour.</summary>
        internal static double? Luminance(string hex)
        {
            var h = hex.TrimStart('#');
            if (h.Length is 3 or 4) h = string.Concat(h[..3].Select(c => $"{c}{c}"));
            if (h.Length < 6) return null;
            static double Ch(string s) => int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return 0.2126 * Ch(h[..2]) + 0.7152 * Ch(h[2..4]) + 0.0722 * Ch(h[4..6]);
        }

        private static string? Colour(string value)
        {
            if (value.Length == 0 || value.Equals("default", StringComparison.OrdinalIgnoreCase)) return null;
            if (value.Equals("none", StringComparison.OrdinalIgnoreCase)) return "none";
            return Regex.IsMatch(value, "^#[0-9a-fA-F]{3,8}$", RegexOptions.None, TimeSpan.FromSeconds(1)) ? value : null;
        }

        private static string Shorten(string s) => s.Length > 60 ? s[..57] + "…" : s;

        /// <summary>draw.io writes embedded pictures as "data:image/png,BASE64" (no ";base64", since ';' separates styles).</summary>
        internal static string? ImageData(string image)
        {
            if (!image.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
            var comma = image.IndexOf(',');
            if (comma < 0) return null;
            var header = image[5..comma];
            if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) header += ";base64";
            var uri = $"data:{header},{image[(comma + 1)..]}";
            return DiagramExtras.IsSafeImageDataUri(uri) ? uri : null;
        }

        internal static (string Subtype, bool Icon) FreeSubtype(DCell v)
        {
            if (v.Has("image") || v.Shape is "image" or "label" && v.Has("image")) return (DiagramExtras.ImageSubtype, false);
            var s = v.Shape;
            if (IsText(v)) return ("text", false);
            switch (s)
            {
                case "ellipse" or "doubleEllipse" or "mxgraph.flowchart.start_1" or "mxgraph.flowchart.start_2" or "mxgraph.flowchart.on-page_reference" or "startState" or "endState":
                    return ("ellipse", false);
                case "rhombus" or "mxgraph.flowchart.decision":
                    return ("diamond", false);
                case "hexagon" or "mxgraph.flowchart.preparation":
                    return ("hexagon", false);
                case "parallelogram" or "mxgraph.flowchart.data":
                    return ("parallelogram", false);
                case "triangle" or "mxgraph.flowchart.extract_or_measurement":
                    return ("triangle", false);
                case "document" or "mxgraph.flowchart.document" or "note":
                    return ("document", false);
                case "cloud":
                    return ("cloud", false);
                case "callout":
                    return ("callout", false);
                case "mxgraph.flowchart.terminator":
                    return ("rounded", false);
                case "process" or "mxgraph.flowchart.process" or "swimlane" or "table" or "umlFrame" or "folder":
                    return ("rectangle", false);
            }
            if (s.Contains("cylinder", StringComparison.OrdinalIgnoreCase) || s is "datastore" or "mxgraph.flowchart.database" or "mxgraph.flowchart.stored_data" or "mxgraph.flowchart.direct_data")
                return ("cylinder", false);
            if (v.S("rounded") == "1") return ("rounded", false);
            var icon = s.StartsWith("mxgraph.", StringComparison.Ordinal) || v.BaseShape.StartsWith("sketch", StringComparison.Ordinal) || s.StartsWith("img/", StringComparison.Ordinal);
            return ("rectangle", icon);
        }

        /// <summary>Plain lines for edges between two placed shapes; elements already related are skipped.</summary>
        private void AddLines(Diagram d, Dictionary<string, Element> made, Func<DCell, Element, Element, bool>? handled)
        {
            var dangling = 0;
            foreach (var e in g.Edges)
            {
                var a = g.End(e.Source, c => made.ContainsKey(c.Id));
                var b = g.End(e.Target, c => made.ContainsKey(c.Id));
                if (a == null || b == null) { dangling++; continue; }
                var ea = made[a.Id];
                var eb = made[b.Id];
                if (handled != null && handled(e, ea, eb)) continue;
                if (Metamodel.CheckEnds(ElementKind.Line, ea.Kind, eb.Kind) != null) continue;
                var line = m.Connect(ElementKind.Line, ea, eb, d.Owner);
                m.Set(line, FieldKeys.Subtype, LineSubtype(e));
                if (e.FullText is { Length: > 0 } label) m.Set(line, FieldKeys.Name, label);
                if (m.AddEdge(d, line) is { } edge)
                {
                    if (e.Points.Count > 0) m.SetWaypoints(d, edge, e.Points.Select(Place).ToList());
                    if (Colour(e.S("strokeColor")) is { } stroke and not "none") m.SetEdgeStyle(d, edge, "stroke", stroke);
                }
            }
            if (dangling > 0) Warn($"{dangling} line{(dangling == 1 ? " is" : "s are")} not attached to shapes at both ends and {(dangling == 1 ? "was" : "were")} left out.");
        }

        internal static string LineSubtype(DCell e)
        {
            var start = e.S("startArrow");
            var end = e.Has("endArrow") ? e.S("endArrow") : "classic";
            var noEnd = end is "" or "none";
            var noStart = start is "" or "none";
            var dashed = e.S("dashed") == "1";
            var dotted = dashed && e.S("dashPattern") is { Length: > 0 } p && p.Split(' ').All(x => Num(x) <= 2);
            if (!noStart && !noEnd) return "bidirectional";
            if (dotted) return "dotted";
            if (dashed) return noEnd && noStart ? "dashed" : "dashedArrow";
            return noEnd && noStart ? "line" : "arrow";
        }

        /// <summary>Comments for draw.io notes, placed where they were.</summary>
        private void AddNotes(Diagram d, Element owner, IEnumerable<DCell> notes)
        {
            foreach (var v in notes)
            {
                var c = m.Create(owner, ElementKind.Comment);
                m.Set(c, FieldKeys.Body, v.Text);
                m.AddNode(d, c, Place(v), showRelationships: false);
            }
        }

        /// <summary>Moves the shapes the builder laid out to where they were in draw.io, and lets lines re-route.</summary>
        private void ApplyGeometry(Diagram d, Dictionary<string, Element>? made)
        {
            if (made == null) return;
            var moves = new Dictionary<DiagramNode, Bounds>();
            foreach (var (id, el) in made)
                if (g.ById.TryGetValue(id, out var c) && d.NodeFor(el) is { } n && !moves.ContainsKey(n)) moves[n] = Place(c);
            if (moves.Count == 0) return;
            m.SetBounds(d, moves, "Layout");
            foreach (var edge in d.Edges.Where(x => x.Waypoints.Count > 0).ToList()) m.SetWaypoints(d, edge, []);
            EdgeRouter.Invalidate(d);
        }

        private void Skipped(IEnumerable<DCell> cells, string what)
        {
            var n = cells.Count();
            if (n > 0) Warn($"{n} {what} that {(n == 1 ? "isn't" : "aren't")} part of the UML diagram {(n == 1 ? "was" : "were")} left out (turn off “Recognise UML” to keep the page as a drawing).");
        }

        // ---------------------------------------------------------------- use case

        public Diagram UseCase()
        {
            var d = m.CreateDiagram(Target, DiagramType.UseCase, name);
            m.Set(d, FieldKeys.Documentation, $"Imported from {source}.");
            var made = new Dictionary<string, Element>(StringComparer.Ordinal);
            var top = TopShapes(g);
            // Larger shapes first, so a system boundary is drawn behind its use cases.
            foreach (var v in top.OrderByDescending(v => v.Abs.Width * v.Abs.Height))
            {
                if (v.Local.Width <= 0 && v.Local.Height <= 0) continue;
                if (IsActor(v) || IsUseCaseEllipse(v))
                {
                    var kind = IsActor(v) ? ElementKind.Actor : ElementKind.UseCase;
                    var label = Name(v.Text);
                    var e = builder.FindShared(label, kind) ?? builder.MakeShared(Target, kind, label.Length > 0 ? label : null);
                    made[v.Id] = e;
                    var b = Place(v);
                    // draw.io actors are thin figures with the name underneath; Meridian draws the name inside the shape.
                    if (kind == ElementKind.Actor) b = new Bounds(b.X + b.Width / 2 - 40, b.Y, 80, Math.Max(b.Height + 20, 90));
                    if (d.NodeFor(e) == null) m.AddNode(d, e, b, showRelationships: false);
                }
                else if (IsNote(v)) AddNotes(d, Target, [v]);
                else
                {
                    var (shape, _) = AddFreeShape(d, v, Target);
                    made[v.Id] = shape;
                    // A system boundary: a rectangle around use cases, drawn as an outline.
                    if (d.NodeFor(shape) is { } boundary && !v.Has("fillColor")) m.SetNodeStyle(d, boundary, "fill", "none");
                }
            }
            AddLines(d, made, (e, a, b) =>
            {
                if (a.Kind is not (ElementKind.Actor or ElementKind.UseCase) || b.Kind is not (ElementKind.Actor or ElementKind.UseCase)) return false;
                var text = e.FullText.ToLowerInvariant();
                var end = e.Has("endArrow") ? e.S("endArrow") : "classic";
                ElementKind kind;
                if (text.Contains("include")) kind = ElementKind.Include;
                else if (text.Contains("extend")) kind = ElementKind.Extend;
                else if (end is "block" or "blockThin" && e.S("endFill") == "0" && a.Kind == b.Kind) kind = ElementKind.Generalization;
                else if (e.S("dashed") == "1") kind = ElementKind.Dependency;
                else kind = ElementKind.Association;
                if (builder.ConnectShared(_mg, kind, a, b) is { } r)
                {
                    if (kind is ElementKind.Association or ElementKind.Dependency && e.FullText.Length > 0 && r.Name.Length == 0) m.Set(r, FieldKeys.Name, e.FullText);
                    m.AddEdge(d, r);
                }
                return true;
            });
            warnings.AddRange(_mg.Warnings);
            EdgeRouter.Invalidate(d);
            return d;
        }

        // ---------------------------------------------------------------- class diagram → BDD

        public Diagram? Class()
        {
            var g2 = new MGraph { Kind = MermaidDiagramKind.Class, Keyword = source + " page", Line = 0, Title = name };
            var top = TopShapes(g);
            var classes = top.Where(IsClassBox).ToList();
            // Packages drawn as folders (or UML frames) holding classes become packages.
            foreach (var p in top.Where(v => v.Shape is "folder" or "umlFrame" && classes.Any(c => Inside(c, v))))
                g2.Groups.Add(new MGroup { Id = p.Id, Label = Name(p.Text.Split('\n')[0]) });
            foreach (var c in classes)
            {
                var (label, annotation, members) = ReadClass(c);
                var n = g2.Node(c.Id, 0)!;
                n.Label = label.Length > 0 ? label : "Block";
                n.Declared = true;
                if (annotation.Length > 0) n.Props["annotation"] = annotation;
                n.Members.AddRange(members);
                n.Group = g2.Groups.Where(p => g.ById.TryGetValue(p.Id, out var pc) && Inside(c, pc))
                    .OrderBy(p => g.ById[p.Id].Abs.Width * g.ById[p.Id].Abs.Height).FirstOrDefault()?.Id;
            }
            foreach (var e in g.Edges)
            {
                var a = g.End(e.Source, c => g2.ById.ContainsKey(c.Id));
                var b = g.End(e.Target, c => g2.ById.ContainsKey(c.Id));
                if (a == null || b == null) continue;
                var (kind, swap) = ClassEdgeKind(e);
                var (from, to) = swap ? (b, a) : (a, b);
                var x = g2.Edge(from.Id, to.Id, 0)!;
                x.Kind = kind;
                var (fromCard, toCard, label) = Cardinalities(e, swap);
                x.Label = label;
                x.FromCard = fromCard;
                x.ToCard = toCard;
            }
            var d = builder.Build(g2, name);
            warnings.AddRange(g2.Warnings);
            if (d == null) return null;
            if (builder.LastMade is { } classMade) PortRows(classes, classMade);
            ApplyGeometry(d, builder.LastMade);
            AddNotes(d, Target, top.Where(IsNote));
            Skipped(top.Where(v => !IsClassBox(v) && !IsNote(v) && !g2.Groups.Any(p => p.Id == v.Id) && !IsText(v)), "shape");
            return d;
        }

        /// <summary>Rows Meridian wrote for ports ("meridian=Port") come back as ports rather than properties.</summary>
        private void PortRows(List<DCell> classes, Dictionary<string, Element> made)
        {
            foreach (var c in classes)
            {
                if (!made.TryGetValue(c.Id, out var block) || !Metamodel.CanOwn(block.Kind, ElementKind.Port)) continue;
                foreach (var row in c.Children.Where(r => r.S("meridian") == nameof(ElementKind.Port)))
                {
                    var portName = Name(row.Text.TrimStart('+', '-', '#', '~', ' ').Split(':')[0].Split('[')[0]);
                    if (block.OwnedElements.Any(p => p.Kind == ElementKind.Port && p.Name == portName)) continue;
                    if (block.OwnedElements.FirstOrDefault(p => p.Kind == ElementKind.Property && p.Name == portName) is not { } prop) continue;
                    var port = m.Create(block, ElementKind.Port, portName);
                    foreach (var key in new[] { FieldKeys.Type, FieldKeys.Multiplicity, FieldKeys.Visibility, FieldKeys.DefaultValue })
                        if (prop.Get(key) is { Length: > 0 } v) m.Set(port, key, v);
                    m.Delete(prop);
                }
            }
        }

        private static bool Inside(DCell inner, DCell outer) =>
            inner.Abs.X >= outer.Abs.X - 1 && inner.Abs.Y >= outer.Abs.Y - 1 && inner.Abs.Right <= outer.Abs.Right + 1 && inner.Abs.Bottom <= outer.Abs.Bottom + 1 && !ReferenceEquals(inner, outer);

        /// <summary>The class name, its «annotation» and member lines (Mermaid's member syntax reads UML's "+ name: Type").</summary>
        private (string Label, string Annotation, List<string> Members) ReadClass(DCell c)
        {
            var members = new List<string>();
            List<string> header;
            if (c.Shape == "table")
            {
                header = c.Text.Split('\n').ToList();
                foreach (var row in c.Children.Where(r => r.Vertex).OrderBy(r => r.Local.Y))
                {
                    var cells = row.Children.Where(x => x.Vertex).OrderBy(x => x.Local.X).Select(x => x.Text.Trim()).Where(t => t.Length > 0).ToList();
                    if (cells.Count == 0 && row.Text.Length > 0) cells.Add(row.Text);
                    // Key columns ("PK", "FK", "PK,FK") are dropped; "name type" becomes "name: type".
                    cells.RemoveAll(t => Regex.IsMatch(t, @"^(PK|FK|UK|AK|PK,\s*FK|FK,\s*PK|\d+)$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)));
                    if (cells.Count == 0) continue;
                    var text = string.Join(" ", cells);
                    var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    members.Add(text.Contains(':') || parts.Length < 2 ? text : $"{parts[0]}: {parts[1]}");
                }
            }
            else if (IsSwimlane(c) && c.S("childLayout") == "stackLayout")
            {
                header = c.Text.Split('\n').ToList();
                foreach (var row in c.Children.Where(r => r.Vertex && r.Shape != "line" && r.Text.Length > 0).OrderBy(r => r.Local.Y))
                    members.AddRange(row.Text.Split('\n').Where(l => l.Trim().Length > 0));
            }
            else if (c.RawLabel.Contains("<hr", StringComparison.OrdinalIgnoreCase))
            {
                var parts = DGraph.Compartments(c.RawLabel);
                header = parts[0].Split('\n').ToList();
                foreach (var part in parts.Skip(1)) members.AddRange(part.Split('\n').Where(l => l.Trim().Length > 0));
            }
            else
            {
                var lines = c.Text.Split('\n').ToList();
                header = lines.Take(2).ToList();
                members.AddRange(lines.Skip(2));
            }
            var annotation = "";
            var nameLine = "";
            foreach (var line in header.Where(l => l.Trim().Length > 0))
            {
                if (StereotypeLine.Match(line) is { Success: true } st) annotation = NormaliseAnnotation(st.Groups["s"].Value);
                else if (nameLine.Length == 0) nameLine = line.Trim();
            }
            if (c.S("fontStyle") is { Length: > 0 } fs && (int)Num(fs) is var f && (f & 2) != 0 && annotation.Length == 0) annotation = "abstract";
            return (nameLine, annotation, members.Select(x => x.Trim()).Where(x => x.Length > 0 && !x.All(ch => ch is '-' or '_')).ToList());
        }

        private static string NormaliseAnnotation(string s) => s.Trim().ToLowerInvariant() switch
        {
            "interface" or "interfaceblock" => "interface",
            "enumeration" or "enum" => "enumeration",
            "valuetype" or "datatype" => "valuetype",
            "abstract" => "abstract",
            _ => "",
        };

        /// <summary>The relationship an edge's arrow heads stand for, and whether its ends must be swapped (the head is at the source).</summary>
        internal static (string Kind, bool Swap) ClassEdgeKind(DCell e)
        {
            var start = e.S("startArrow");
            var end = e.Has("endArrow") ? e.S("endArrow") : "classic";
            var startFill = e.Has("startFill") ? e.S("startFill") : "1";
            var endFill = e.Has("endFill") ? e.S("endFill") : "1";
            var dashed = e.S("dashed") == "1";
            static bool Block(string a) => a is "block" or "blockThin";
            static bool Diamond(string a) => a is "diamond" or "diamondThin";
            if (Block(end) && endFill == "0") return (dashed ? "realization" : "generalization", false);
            if (Block(start) && startFill == "0") return (dashed ? "realization" : "generalization", true);
            if (Diamond(start)) return (startFill == "0" ? "aggregation" : "composition", false);
            if (Diamond(end)) return (endFill == "0" ? "aggregation" : "composition", true);
            if (dashed) return ("dependency", false);
            return ("association", false);
        }

        private static readonly Regex Multiplicity = new(@"^(\d+|\*|n|\d+\.\.(\d+|\*|n))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        /// <summary>Labels near an end that look like multiplicities ("1", "0..*") are cardinalities; others name the relationship.</summary>
        private static (string From, string To, string Label) Cardinalities(DCell e, bool swap)
        {
            string near(Func<double, bool> at) => e.EdgeLabels.Where(l => at(l.RelX) && Multiplicity.IsMatch(l.Text.Trim())).Select(l => l.Text.Trim()).FirstOrDefault() ?? "";
            var src = near(x => x <= -0.5);
            var tgt = near(x => x >= 0.5);
            var label = string.Join(" ", new[] { e.Text }.Concat(e.EdgeLabels.Where(l => !Multiplicity.IsMatch(l.Text.Trim())).Select(l => l.Text)).Where(t => t.Length > 0));
            return swap ? (tgt, src, label) : (src, tgt, label);
        }

        // ---------------------------------------------------------------- requirements

        public Diagram? Requirement()
        {
            var g2 = new MGraph { Kind = MermaidDiagramKind.Requirement, Keyword = source + " page", Line = 0, Title = name };
            var top = TopShapes(g);
            foreach (var v in top)
            {
                if (IsRequirementBox(v))
                {
                    var n = g2.Node(v.Id, 0)!;
                    n.Declared = true;
                    n.Shape = "requirement";
                    var lines = v.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !StereotypeLine.IsMatch(l)).ToList();
                    n.Label = lines.FirstOrDefault() ?? "Requirement";
                    var text = new List<string>();
                    foreach (var l in lines.Skip(1))
                    {
                        var id = Regex.Match(l, @"^id\s*[:=]\s*(.+)$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                        if (id.Success) n.Props["id"] = id.Groups[1].Value.Trim();
                        else text.Add(l.StartsWith("text", StringComparison.OrdinalIgnoreCase) && l.Contains(':') ? l[(l.IndexOf(':') + 1)..].Trim() : l);
                    }
                    if (text.Count > 0) n.Props["text"] = string.Join(" ", text);
                }
                else if (IsClassBox(v) || IsActor(v) || v.S("meridian") is { Length: > 0 } && !IsNote(v))
                {
                    var n = g2.Node(v.Id, 0)!;
                    n.Declared = true;
                    n.Shape = "element";
                    n.Label = IsClassBox(v) ? ReadClass(v).Label : v.Text.Split('\n').Last(l => !StereotypeLine.IsMatch(l));
                    if (v.S("meridian") == nameof(ElementKind.TestCase)) n.Props["type"] = "testCase";
                }
            }
            foreach (var e in g.Edges)
            {
                var a = g.End(e.Source, c => g2.ById.ContainsKey(c.Id));
                var b = g.End(e.Target, c => g2.ById.ContainsKey(c.Id));
                if (a == null || b == null) continue;
                var label = e.FullText.ToLowerInvariant();
                var start = e.S("startArrow");
                var end = e.S("endArrow");
                string kind;
                if (end == "circlePlus") { (a, b) = (b, a); kind = "contains"; }
                else if (start == "circlePlus" || label.Contains("contain")) kind = "contains";
                else if (label.Contains("satisf")) kind = "satisfies";
                else if (label.Contains("verif")) kind = "verifies";
                else if (label.Contains("derive")) kind = "derives";
                else if (label.Contains("refine")) kind = "refines";
                else if (label.Contains("copy") || label.Contains("copies")) kind = "copies";
                else kind = "traces";
                g2.Edge(a.Id, b.Id, 0)!.Kind = kind;
            }
            var d = builder.Build(g2, name);
            warnings.AddRange(g2.Warnings);
            if (d == null) return null;
            ApplyGeometry(d, builder.LastMade);
            AddNotes(d, Target, top.Where(IsNote));
            return d;
        }

        // ---------------------------------------------------------------- activity

        public Diagram? Activity()
        {
            var g2 = new MGraph { Kind = MermaidDiagramKind.Flowchart, Keyword = source + " page", Line = 0, Title = name };
            var top = TopShapes(g);
            bool IsLane(DCell c) => IsSwimlane(c) && c.Children.Any(ch => ch.Vertex && !IsSwimlane(ch));
            var nodes = top.Where(v => !IsLane(v) && !IsSwimlane(v) && !IsNote(v) && !IsText(v) && !IsFrame(v) && (v.Local.Width > 0 || v.Local.Height > 0)).ToList();
            foreach (var lane in top.Where(IsLane)) g2.Groups.Add(new MGroup { Id = lane.Id, Label = Name(lane.Text) });
            foreach (var v in nodes)
            {
                var n = g2.Node(v.Id, 0)!;
                n.Declared = true;
                n.Label = v.Text;
                n.Shape = IsStart(v) ? "initial" : IsEnd(v) ? "final" : IsDecision(v) ? "diamond" : IsBar(v) ? "fork"
                    : FreeSubtype(v).Subtype == "cylinder" ? "db" : "round";
                n.Group = Ancestors(v).FirstOrDefault(a => g2.Groups.Any(x => x.Id == a.Id))?.Id;
            }
            foreach (var e in g.Edges)
            {
                var a = g.End(e.Source, c => g2.ById.ContainsKey(c.Id));
                var b = g.End(e.Target, c => g2.ById.ContainsKey(c.Id));
                if (a == null || b == null) continue;
                var x = g2.Edge(a.Id, b.Id, 0)!;
                x.Label = e.FullText;
                x.Kind = "flow";
            }
            var d = builder.Build(g2, name);
            warnings.AddRange(g2.Warnings);
            if (d == null) return null;
            if (g2.Groups.Count == 0) ApplyGeometry(d, builder.LastMade);
            else Warn($"swimlanes were laid out again, one lane per {source} lane.");
            AddNotes(d, d.Owner ?? Target, top.Where(v => IsNote(v) || IsText(v) && v.Text.Length > 0));
            return d;
        }

        private IEnumerable<DCell> Ancestors(DCell c)
        {
            for (var (id, depth) = (c.Parent, 0); id != null && depth < 20 && g.ById.TryGetValue(id, out var p); id = p.Parent, depth++)
                yield return p;
        }

        // ---------------------------------------------------------------- state machine

        public Diagram? State()
        {
            var g2 = new MGraph { Kind = MermaidDiagramKind.State, Keyword = source + " page", Line = 0, Title = name };
            var top = TopShapes(g);
            var states = top.Where(v => !IsNote(v) && !IsText(v) && (v.Local.Width > 0 || v.Local.Height > 0)).ToList();
            var ids = states.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var composite = false;
            foreach (var v in states)
            {
                var n = g2.Node(v.Id, 0)!;
                n.Declared = true;
                var lines = v.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                n.Label = lines.FirstOrDefault() ?? "";
                foreach (var l in lines.Skip(1))
                {
                    var mm = Regex.Match(l, @"^(entry|exit|do)\s*/\s*(.+)$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                    if (mm.Success) n.Props[mm.Groups[1].Value.ToLowerInvariant()] = mm.Groups[2].Value.Trim();
                }
                n.Shape = IsStart(v) ? "initial" : IsEnd(v) ? "final" : IsDecision(v) ? "choice" : IsBar(v) ? "fork" : "";
                n.Parent = Ancestors(v).FirstOrDefault(a => ids.Contains(a.Id))?.Id;
                if (n.Parent != null) composite = true;
            }
            foreach (var e in g.Edges)
            {
                var a = g.End(e.Source, c => g2.ById.ContainsKey(c.Id));
                var b = g.End(e.Target, c => g2.ById.ContainsKey(c.Id));
                if (a == null || b == null) continue;
                g2.Edge(a.Id, b.Id, 0)!.Label = e.FullText;
            }
            var d = builder.Build(g2, name);
            warnings.AddRange(g2.Warnings);
            if (d == null) return null;
            if (!composite) ApplyGeometry(d, builder.LastMade);
            AddNotes(d, d.Owner ?? Target, top.Where(v => IsNote(v) || IsText(v) && v.Text.Length > 0));
            return d;
        }

        // ---------------------------------------------------------------- sequence

        public Diagram? Sequence()
        {
            var g2 = new MGraph { Kind = MermaidDiagramKind.Sequence, Keyword = source + " page", Line = 0, Title = name };
            var top = TopShapes(g);
            var lifelines = top.Where(IsLifeline).OrderBy(l => l.Abs.X).ToList();
            foreach (var l in lifelines)
            {
                var n = g2.Node(l.Id, 0)!;
                n.Declared = true;
                n.Label = l.Text.Replace('\n', ' ');
                if (l.S("participant") == "umlActor") n.Shape = "actor";
            }
            DCell? Lifeline(string? id) => g.End(id, IsLifeline);
            DCell? Nearest(Pt? p) => p is { } pt ? lifelines.Where(l => pt.X >= l.Abs.X - 10 && pt.X <= l.Abs.Right + 10).OrderBy(l => Math.Abs(l.Abs.Center.X - pt.X)).FirstOrDefault() : null;

            var events = new List<(double Y, int Order, int Seq, MSeq Event)>();
            var seq = 0;
            var lost = 0;
            foreach (var e in g.Edges)
            {
                var a = Lifeline(e.Source) ?? Nearest(e.SourcePoint);
                var b = Lifeline(e.Target) ?? Nearest(e.TargetPoint);
                if (a == null || b == null) { lost++; continue; }
                var dashed = e.S("dashed") == "1";
                var end = e.Has("endArrow") ? e.S("endArrow") : "classic";
                var sort = dashed ? "reply" : end is "open" or "openThin" or "openAsync" ? "asynchCall" : "synchCall";
                events.Add((MessageY(e), 1, seq++, new MSeqMessage(0, a.Id, b.Id, MermaidSource.Name(e.FullText), sort, false, false)));
            }
            foreach (var f in top.Where(IsFrame))
            {
                var lines = f.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                var op = (lines.FirstOrDefault() ?? "").ToLowerInvariant();
                if (!Metamodel.FragmentOperators.Contains(op)) op = op.StartsWith("loop") ? "loop" : op.StartsWith("opt") ? "opt" : "alt";
                var guard = lines.Skip(1).Concat(g.Vertices.Where(v => IsText(v) && Inside(v, f) && v.Text.StartsWith('[')).Select(v => v.Text))
                    .Select(t => t.Trim('[', ']', ' ')).FirstOrDefault(t => t.Length > 0) ?? "";
                events.Add((f.Abs.Y, 0, seq++, new MSeqFragmentStart(0, op, guard)));
                // Dashed lines across the frame separate its operands.
                foreach (var sep in g.Vertices.Where(v => v.Shape == "line" && v.S("dashed") == "1" && Inside(v, f)).OrderBy(v => v.Abs.Y))
                {
                    var operandGuard = g.Vertices.Where(v => IsText(v) && Inside(v, f) && v.Abs.Y >= sep.Abs.Y && v.Abs.Y <= sep.Abs.Y + 30 && v.Text.StartsWith('['))
                        .Select(v => v.Text.Trim('[', ']', ' ')).FirstOrDefault() ?? "";
                    events.Add((sep.Abs.Y, 0, seq++, new MSeqFragmentElse(0, operandGuard)));
                }
                events.Add((f.Abs.Bottom, 2, seq++, new MSeqFragmentEnd(0)));
            }
            foreach (var note in top.Where(IsNote))
            {
                var targets = lifelines.Where(l => l.Abs.Center.X >= note.Abs.X - 40 && l.Abs.Center.X <= note.Abs.Right + 40).Select(l => l.Id).ToList();
                if (targets.Count == 0 && lifelines.Count > 0) targets.Add(lifelines.OrderBy(l => Math.Abs(l.Abs.Center.X - note.Abs.Center.X)).First().Id);
                events.Add((note.Abs.Y, 1, seq++, new MSeqNote(0, targets, note.Text)));
            }
            // Fragment ends close inner fragments first: at the same height the smaller frame ends first.
            g2.Sequence.AddRange(events.OrderBy(x => x.Y).ThenBy(x => x.Order).ThenBy(x => x.Seq).Select(x => x.Event));
            if (lost > 0) Warn($"{lost} message{(lost == 1 ? "" : "s")} not attached to a lifeline {(lost == 1 ? "was" : "were")} left out.");
            var d = builder.Build(g2, name);
            warnings.AddRange(g2.Warnings);
            return d;
        }

        /// <summary>The height of a message: its fixed end, its first bend, or where it leaves its source.</summary>
        private double MessageY(DCell e)
        {
            if (e.SourcePoint is { } sp) return sp.Y;
            if (e.Points.Count > 0) return e.Points[0].Y;
            if (e.Source != null && g.ById.TryGetValue(e.Source, out var s))
            {
                if (e.Has("exitY")) return s.Abs.Y + Num(e.S("exitY")) * s.Abs.Height;
                if (e.Target != null && g.ById.TryGetValue(e.Target, out var t) && e.Has("entryY")) return t.Abs.Y + Num(e.S("entryY")) * t.Abs.Height;
                if (!IsLifeline(s)) return s.Abs.Y + Math.Min(s.Abs.Height / 2, 10);
            }
            if (e.TargetPoint is { } tp) return tp.Y;
            if (e.Target != null && g.ById.TryGetValue(e.Target, out var tt) && !IsLifeline(tt)) return tt.Abs.Y + Math.Min(tt.Abs.Height / 2, 10);
            return 0;
        }
    }
}
