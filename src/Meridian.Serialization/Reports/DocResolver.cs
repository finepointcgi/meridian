using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Validation;

namespace Meridian.Serialization.Reports;

public sealed class ResolveOptions
{
    /// <summary>Maximum iterations per loop (0 = unlimited); the template editor's live preview uses a small number.</summary>
    public int LoopLimit { get; init; }
    /// <summary>Render diagram SVG (false leaves a placeholder figure, for fast previews).</summary>
    public bool RenderDiagrams { get; init; } = true;
    public DateTime? Date { get; init; }
    /// <summary>Upper bound on loop iterations in the whole document, nested loops included (a template is model data,
    /// so a few nested loops over every element must not tie up the server).</summary>
    public int MaxIterations { get; init; } = 1_000_000;
    /// <summary>Upper bound on the number of paragraphs, headings, tables and figures produced.</summary>
    public int MaxNodes { get; init; } = 500_000;
    /// <summary>Limit the report to these elements and what they contain (ids; packages or the current selection). Queries,
    /// diagram lists, tables, statistics and validation only see elements inside; links to elements outside still show.</summary>
    public IReadOnlyCollection<string>? Scope { get; init; }
    /// <summary>With a value above 0: leave out elements nested more than this many packages below the scope (or the model root).</summary>
    public int ScopeDepth { get; init; }
    /// <summary>One part per top-level package (of the scope, or of the model): a level-1 heading for the package, and the
    /// template's sections below it, each limited to that package. The table of contents lists every part.</summary>
    public bool SplitByPackage { get; init; }
    /// <summary>Rendered diagrams, shared between passes and documents of one report (a new cache when null).</summary>
    public DiagramImageCache? Images { get; init; }
    /// <summary>Live counters (section, elements processed) for progress reporting from another thread.</summary>
    public ReportCounters? Counters { get; init; }

    internal bool Scoped => Scope is { Count: > 0 } || ScopeDepth > 0;
}

/// <summary>What the resolver has done so far, readable from another thread while it runs.</summary>
public sealed class ReportCounters
{
    private int _section, _sections;
    private long _elements;
    public int Section { get => Volatile.Read(ref _section); internal set => Volatile.Write(ref _section, value); }
    public int Sections { get => Volatile.Read(ref _sections); internal set => Volatile.Write(ref _sections, value); }
    public long Elements => Interlocked.Read(ref _elements);
    internal void AddElements(long n) => Interlocked.Add(ref _elements, n);
}

/// <summary>
/// Evaluates a <see cref="DocTemplate"/> against a model: runs queries and loops, substitutes placeholders, numbers
/// headings / figures / tables and collects cross-reference targets. The result is model-free and can be written
/// to any format on a background thread.
/// </summary>
public sealed class DocResolver
{
    private sealed class Scope
    {
        public Element? Current;
        public Dictionary<string, Element> Vars = new(StringComparer.OrdinalIgnoreCase);
        public int Index;
        public int Count;
        public Scope With(Element e, string? variable, int index, int count)
        {
            var s = new Scope { Current = e, Vars = new(Vars, StringComparer.OrdinalIgnoreCase), Index = index, Count = count };
            if (!string.IsNullOrWhiteSpace(variable)) s.Vars[variable.Trim()] = e;
            return s;
        }
    }

    private readonly ModelRepository _m;
    private readonly DocTemplate _t;
    private readonly ResolveOptions _o;
    private readonly IProgress<ReportProgress>? _progress;
    private readonly CancellationToken _ct;
    private ResolvedDocument _doc = null!;

    // Where nodes go: the document's list (buffered), a consumer (streamed), or nowhere (the numbering pass only counts).
    private readonly Action<DocNode>? _sink;
    private readonly bool _indexOnly;
    /// <summary>Streaming: the numbering pass's result, which knows every cross-reference target before anything is written.</summary>
    private readonly ResolvedDocument? _known;
    private DocNode? _last;
    private bool _pendingBreak;
    private long _emitted;

    private readonly int[] _counters = new int[10];
    private int _chapter, _figSeq, _tabSeq, _anchor;
    private long _iterations;
    private bool _landscape, _wantLandscape;
    private readonly DiagramImageCache _images;
    private readonly ReportCounters _live;
    private Dictionary<Element, int>? _reqOrder;
    private IReadOnlyList<TraceRow>? _traceRows;
    private IReadOnlyList<ValidationIssue>? _issues;
    private readonly string _date;

    // Scope ("limit to selection / package", depth, split per package).
    private readonly HashSet<string>? _scopeIds;
    private Element? _part;
    private readonly Dictionary<Element, bool> _inScope = new();

    private DocResolver(ModelRepository m, DocTemplate t, ResolveOptions? o, IProgress<ReportProgress>? progress, CancellationToken ct,
                        Action<DocNode>? sink = null, bool indexOnly = false, ResolvedDocument? known = null)
    {
        _m = m; _t = t; _o = o ?? new ResolveOptions(); _progress = progress; _ct = ct;
        _sink = sink; _indexOnly = indexOnly; _known = known;
        _images = _o.Images ?? new DiagramImageCache();
        _live = _o.Counters ?? new ReportCounters();
        _date = (_o.Date ?? DateTime.Now).ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        if (_o.Scope is { Count: > 0 } scope) _scopeIds = scope.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Resolves the whole document into memory (<see cref="ResolvedDocument.Nodes"/>): previews, tests and small reports.</summary>
    public static ResolvedDocument Resolve(ModelRepository m, DocTemplate t, ResolveOptions? options = null,
                                           IProgress<ReportProgress>? progress = null, CancellationToken ct = default) =>
        new DocResolver(m, t, options, progress, ct).Run();

    /// <summary>
    /// The numbering pass of a streamed report: numbers headings, figures and tables, collects cross-reference targets,
    /// warnings and <see cref="ResolvedDocument.Stats"/>, but keeps no nodes and renders no diagrams. Its result is the
    /// size estimate and the front matter (table of contents, lists of figures / tables) of the streamed document.
    /// </summary>
    public static ResolvedDocument Index(ModelRepository m, DocTemplate t, ResolveOptions? options = null,
                                         IProgress<ReportProgress>? progress = null, CancellationToken ct = default) =>
        new DocResolver(m, t, options, progress, ct, indexOnly: true).Run();

    /// <summary>
    /// The writing pass of a streamed report: the document's nodes, produced on a worker thread while the caller consumes
    /// them, with at most a few hundred in memory. <paramref name="index"/> is <see cref="Index"/>'s result for the same
    /// model, template and options; it resolves the cross-references in headings and table cells. Enumerate once.
    /// </summary>
    public static IEnumerable<DocNode> Stream(ModelRepository m, DocTemplate t, ResolvedDocument index, ResolveOptions? options = null,
                                              CancellationToken ct = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var queue = new BlockingCollection<DocNode>(boundedCapacity: 256);
        Exception? failure = null;
        var producer = Task.Factory.StartNew(() =>
        {
            try { new DocResolver(m, t, options, null, stop.Token, n => queue.Add(n, stop.Token), known: index).Run(); }
            catch (Exception ex) { failure = ex; }
            finally { queue.CompleteAdding(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            foreach (var n in queue.GetConsumingEnumerable(stop.Token)) yield return n;
            producer.Wait(CancellationToken.None);
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            // The consumer stopped early (an exception, or it was disposed): stop the producer and wait for it.
            stop.Cancel();
            try { producer.Wait(CancellationToken.None); } catch (AggregateException) { }
        }
    }

    /// <summary>The packages a split report has one part for: the top-level packages of the scope (a single package in scope)
    /// or of the model, without the report-template package.</summary>
    public static IReadOnlyList<Element> SplitPackages(ModelRepository m, IReadOnlyCollection<string>? scope)
    {
        var parent = scope is { Count: 1 } && m.Find(scope.First()) is { } s ? s : m.Root;
        IEnumerable<Element> list = parent.OwnedElements.Where(e => e.Kind == ElementKind.Package && e.Name != ReportTemplates.PackageName);
        if (scope is { Count: > 1 })
            list = scope.Select(m.Find).OfType<Element>().Where(e => e.Kind == ElementKind.Package);
        return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private ResolvedDocument Run()
    {
        var root = new Scope { Current = _m.Root };
        string S(string v) => Sub(v, root);
        string title = S(_t.Title), subtitle = S(_t.Subtitle), number = S(_t.DocumentNumber), revision = S(_t.Revision),
               classification = S(_t.Classification), author = S(_t.Author), organization = S(_t.Organization);
        _doc = new ResolvedDocument
        {
            Template = _t, Title = title, Subtitle = subtitle, DocumentNumber = number, Revision = revision,
            Classification = classification, Author = author, Organization = organization, Date = _date, ModelName = _m.Root.Name,
            TitleNotes = DocMarkdown.Parse(Sub(_t.TitleNotes, root)),
        };

        // Front matter: each part on its own page.
        if (_t.TitlePage) { Emit(new TitlePageNode()); Emit(new PageBreakNode()); }
        if (_t.RevisionHistory) { Emit(new RevisionHistoryNode()); Emit(new PageBreakNode()); }
        if (_t.TableOfContents) { Emit(new ListingNode(ListingKind.Contents)); Emit(new PageBreakNode()); }
        if (_t.ListOfFigures) { Emit(new ListingNode(ListingKind.Figures)); Emit(new PageBreakNode()); }
        if (_t.ListOfTables) { Emit(new ListingNode(ListingKind.Tables)); Emit(new PageBreakNode()); }

        if (_o.SplitByPackage)
        {
            var parts = SplitPackages(_m, _o.Scope);
            _live.Sections = parts.Count;
            for (var p = 0; p < parts.Count; p++)
            {
                _ct.ThrowIfCancellationRequested();
                _live.Section = p + 1;
                _progress?.Report(new ReportProgress($"Resolving package {p + 1} of {parts.Count}: {parts[p].Name}", (double)p / parts.Count, p + 1, parts.Count, _live.Elements));
                _part = parts[p];
                _inScope.Clear();
                Heading(parts[p].Name, 1, true, true, null);
                foreach (var s in _t.Sections) Section(s, 2, root, false);
            }
            _part = null;
            if (parts.Count == 0) Emit(ParagraphNode.Plain("There are no packages to report on.", italic: true));
        }
        else
        {
            _live.Sections = _t.Sections.Count;
            for (var i = 0; i < _t.Sections.Count; i++)
            {
                _ct.ThrowIfCancellationRequested();
                _live.Section = i + 1;
                _progress?.Report(new ReportProgress($"Resolving section {i + 1} of {_t.Sections.Count}", (double)i / Math.Max(1, _t.Sections.Count), i + 1, _t.Sections.Count, _live.Elements));
                Section(_t.Sections[i], 1, root, false);
            }
        }
        _pendingBreak = false;   // never end on a page break
        FixPlainReferences();
        return _doc;
    }

    // ================================================================== structure

    /// <summary>
    /// Adds a node, collapsing page breaks so there are no blank pages. A page break is held back until the next node
    /// arrives (it may turn out to be redundant), so nodes can be streamed out as soon as they are made.
    /// </summary>
    private void Emit(DocNode n)
    {
        if (_emitted >= _o.MaxNodes)
            throw new InvalidOperationException($"The document is too large: more than {_o.MaxNodes:N0} paragraphs, tables and figures. Narrow the template's loops or queries.");
        if (n is not (TitlePageNode or ListingNode or RevisionHistoryNode or PageBreakNode) && _wantLandscape != _landscape)
        {
            _landscape = _wantLandscape;
            _pendingBreak = false;   // the section break starts a new page anyway
            Put(new OrientationNode(_landscape));
        }
        var last = _pendingBreak ? PendingBreak : _last;
        // Never produce blank pages: collapse breaks at the top of a page into one.
        if (n is PageBreakNode)
        {
            if (last is not (null or PageBreakNode or OrientationNode)) _pendingBreak = true;
            return;
        }
        if (n is HeadingNode { PageBreakBefore: true }) _pendingBreak = false;   // the heading breaks the page itself
        if (n is HeadingNode { PageBreakBefore: true } h && last is null or OrientationNode) n = h with { PageBreakBefore = false };
        if (_pendingBreak) { _pendingBreak = false; Put(new PageBreakNode()); }
        Put(n);
    }

    private static readonly PageBreakNode PendingBreak = new();

    private void Put(DocNode n)
    {
        if (_known != null) n = FixPlain(n, _known);
        _emitted++;
        _last = n;
        _doc.Stats.Add(n);
        if (_indexOnly) return;
        if (_sink != null) _sink(n);
        else _doc.Nodes.Add(n);
    }

    private void Counted(long elements = 1)
    {
        _doc.Stats.Elements += elements;
        _live.AddElements(elements);
    }

    // ================================================================== scope

    private bool Scoped => _o.Scoped || _part != null;

    /// <summary>Inside the report's scope: below a scope element (or the root), within the package depth, inside the current part.</summary>
    private bool InScope(Element e)
    {
        if (!Scoped) return true;
        if (_inScope.TryGetValue(e, out var known)) return known;
        var inPart = _part == null;
        var packages = 0;
        var ok = false;
        for (var a = e; a != null; a = a.Owner)
        {
            if (!inPart && ReferenceEquals(a, _part)) inPart = true;
            if (_scopeIds != null ? _scopeIds.Contains(a.Id) : a.Owner == null) { ok = true; break; }
            if (!ReferenceEquals(a, e) && a.Kind == ElementKind.Package) packages++;
        }
        ok = ok && inPart && (_o.ScopeDepth <= 0 || packages <= _o.ScopeDepth);
        _inScope[e] = ok;
        return ok;
    }

    private IReadOnlyList<Element> Run(ModelQuery q)
    {
        var all = q.Run(_m);
        return Scoped ? all.Where(InScope).ToList() : all;
    }

    private void Section(DocSection s, int level, Scope sc, bool parentLandscape)
    {
        _wantLandscape = parentLandscape || s.Landscape;
        Heading(Sub(s.Title, sc), level, s.Numbered, s.PageBreakBefore, s.Ref is { Length: > 0 } r ? Sub(r, sc) : null);
        Blocks(s.Blocks, level + 1, sc);
        foreach (var child in s.Sections) Section(child, level + 1, sc, parentLandscape || s.Landscape);
        _wantLandscape = parentLandscape;
    }

    private void Heading(string text, int level, bool numbered, bool pageBreak, string? refName)
    {
        level = Math.Clamp(level, 1, 9);
        var number = "";
        if (numbered)
        {
            _counters[level]++;
            for (var i = level + 1; i < _counters.Length; i++) _counters[i] = 0;
            for (var i = 1; i < level; i++) if (_counters[i] == 0) _counters[i] = 1;
            number = string.Join(".", _counters.Skip(1).Take(level));
            if (level == 1 && _chapter != _counters[1])
            {
                _chapter = _counters[1];
                if (ChapterCaptions) { _figSeq = 0; _tabSeq = 0; }
            }
        }
        var anchor = $"_Toc{++_anchor:000000}";
        var h = new HeadingNode(level, number, text, anchor, pageBreak);
        Emit(h);
        _doc.Headings.Add(h);
        Register(refName, new RefTarget(TargetKind.Section, number.Length > 0 ? "Section " + number : text, text, anchor));
    }

    private bool ChapterCaptions => !string.Equals(_t.CaptionNumbering, "sequential", StringComparison.OrdinalIgnoreCase);

    private Caption NewCaption(string kind, string text, string? refName)
    {
        var seq = kind == "Figure" ? ++_figSeq : ++_tabSeq;
        var number = ChapterCaptions && _chapter > 0 ? $"{_chapter}-{seq}" : seq.ToString(CultureInfo.InvariantCulture);
        var c = new Caption(kind, ChapterCaptions ? _chapter : 0, seq, number, text, $"_Ref{++_anchor:000000}");
        (kind == "Figure" ? _doc.Figures : _doc.Tables).Add(c);
        Register(refName, new RefTarget(kind == "Figure" ? TargetKind.Figure : TargetKind.Table, c.Label, text, c.Anchor));
        return c;
    }

    private void Register(string? name, RefTarget t)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!_doc.Targets.TryAdd(name.Trim(), t)) _doc.Warnings.Add($"Duplicate reference name '{name}' — the first one is used.");
    }

    // ================================================================== blocks

    private void Blocks(IEnumerable<DocBlock> blocks, int level, Scope sc)
    {
        foreach (var b in blocks)
        {
            _ct.ThrowIfCancellationRequested();
            try { Block(b, level, sc); }
            catch (QueryParseException ex)
            {
                _doc.Warnings.Add($"{b.TypeLabel}: invalid query — {ex.Message}");
                Emit(ParagraphNode.Plain($"[{b.TypeLabel}: invalid query — {ex.Message}]", italic: true));
            }
        }
    }

    private void Block(DocBlock b, int level, Scope sc)
    {
        switch (b)
        {
            case TextBlock t:
                foreach (var n in DocMarkdown.Parse(Sub(t.Markdown, sc))) Emit(n);
                break;
            case DocumentationBlock d:
            {
                var e = sc.Current ?? _m.Root;
                var text = Val(e, d.Key);
                if (string.IsNullOrWhiteSpace(text)) text = Sub(d.EmptyText, sc);
                foreach (var n in DocMarkdown.Parse(XRefsOnly(text))) Emit(n);
                break;
            }
            case ForEachBlock f: ForEach(f, level, sc); break;
            case IfBlock i: Blocks(Test(i, sc) ? i.Blocks : i.Else, level, sc); break;
            case FieldsBlock f: Fields(f, sc); break;
            case TableBlock t: Table(t, sc); break;
            case RequirementTableBlock r: Requirements(r, sc); break;
            case TraceTableBlock t: TraceTable(t, sc); break;
            case TraceMatrixBlock x: Matrix(x, sc); break;
            case CoverageBlock c: Coverage(c, sc); break;
            case DiagramBlock d: Diagrams(d, sc); break;
            case RelationMapBlock r: RelationMap(r, sc); break;
            case ElementListBlock l: List(l, sc); break;
            case StatisticsBlock s: Statistics(s, sc); break;
            case ValidationBlock v: Validation(v, sc); break;
            case FmeaBlock f: FmeaTable(f, sc); break;
            case FaultTreeBlock ft: FaultTreeTables(ft, sc); break;
            case PageBreakBlock: Emit(new PageBreakNode()); break;
            case InstancesBlock i: Instances(i, sc); break;
        }
    }

    private IReadOnlyList<Element> Select(string? query, string? related, Scope sc)
    {
        var q = string.IsNullOrWhiteSpace(query) ? null : ModelQuery.Parse(Sub(query, sc, quote: true));
        if (string.IsNullOrWhiteSpace(related)) return q == null ? [] : Run(q);
        var cur = sc.Current ?? _m.Root;
        IEnumerable<Element> items;
        var key = related.Trim();
        if (key.Equals("children", StringComparison.OrdinalIgnoreCase) || key.Equals("owned", StringComparison.OrdinalIgnoreCase))
            items = cur.OwnedElements.Where(InScope);
        else if (key.Equals("descendants", StringComparison.OrdinalIgnoreCase))
            items = cur.Descendants().Where(InScope);
        else if (ModelValues.TryRel(ModelValues.Canonical(key), out var kind, out var dir))
            items = ModelValues.Related(_m, cur, kind, dir).Distinct();
        else throw new QueryParseException($"Unknown relation '{related}' (use children, descendants, satisfiedBy, rel:in:Verify …)", 0);
        return items.Where(e => q == null || q.Matches(_m, e)).OrderBy(e => e.QualifiedName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void ForEach(ForEachBlock f, int level, Scope sc)
    {
        var items = Select(f.Query, f.Related, sc).AsEnumerable();
        items = Sort(items, f.SortBy, f.Descending);
        if (f.Limit > 0) items = items.Take(f.Limit);
        if (_o.LoopLimit > 0) items = items.Take(_o.LoopLimit);
        var list = items.ToList();
        if (list.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(f.EmptyText)) foreach (var n in DocMarkdown.Parse(Sub(f.EmptyText, sc))) Emit(n);
            return;
        }
        var top = level <= 2;
        for (var i = 0; i < list.Count; i++)
        {
            _ct.ThrowIfCancellationRequested();
            if (++_iterations > _o.MaxIterations)
                throw new InvalidOperationException($"The document is too large: its loops run more than {_o.MaxIterations:N0} times in all. Narrow the template's loops (nested loops multiply).");
            Counted();
            if (top && i % 25 == 0) _progress?.Report(new ReportProgress($"Resolving {i + 1} of {list.Count}", double.NaN, _live.Section, _live.Sections, _live.Elements));
            var inner = sc.With(list[i], f.Variable, i + 1, list.Count);
            if (!string.IsNullOrWhiteSpace(f.Heading))
            {
                Heading(Sub(f.Heading, inner), level, true, f.PageBreakEach && i > 0,
                        f.HeadingRef is { Length: > 0 } r ? Sub(r, inner) : null);
                Blocks(f.Blocks, level + 1, inner);
            }
            else
            {
                if (f.PageBreakEach && i > 0) Emit(new PageBreakNode());
                Blocks(f.Blocks, level, inner);
            }
        }
    }

    private IEnumerable<Element> Sort(IEnumerable<Element> items, string? key, bool desc)
    {
        if (string.IsNullOrWhiteSpace(key)) return items;
        return desc ? items.OrderByDescending(e => Val(e, key), NaturalComparer.Instance) : items.OrderBy(e => Val(e, key), NaturalComparer.Instance);
    }

    private bool Test(IfBlock i, Scope sc)
    {
        var ok = true;
        if (!string.IsNullOrWhiteSpace(i.Condition))
        {
            var cond = ModelQuery.ParseCondition(Sub(i.Condition, sc, quote: true));
            ok = cond == null || cond.Eval(_m, sc.Current ?? _m.Root);
        }
        if (ok && !string.IsNullOrWhiteSpace(i.Exists)) ok = Run(ModelQuery.Parse(Sub(i.Exists, sc, quote: true))).Count > 0;
        return ok;
    }

    private void Fields(FieldsBlock f, Scope sc)
    {
        var e = sc.Current ?? _m.Root;
        var rows = new List<IReadOnlyList<string>>();
        void Add(string label, string value)
        {
            if (value.Length == 0 && !f.IncludeEmpty) return;
            if (rows.Any(r => r[0] == label)) return;
            rows.Add([label, value]);
        }
        if (f.Keys.Count > 0)
            foreach (var k in f.Keys) Add(ModelValues.Label(k), Val(e, k));
        else
        {
            Add("Kind", Metamodel.DisplayName(e.Kind));
            if (e.Kind == ElementKind.Requirement)
            {
                Add("Id", e.Get(FieldKeys.RequirementId));
                Add("Text", e.Get(FieldKeys.Text));
                foreach (var (key, label, _) in ReqFields.Standard) Add(label, e.Get(key));
                foreach (var a in ReqFields.CustomAttributes(e)) Add(a.Key, a.Value);
            }
            Add("Qualified name", e.QualifiedName);
            Add("Stereotypes", string.Join(", ", e.Stereotypes));
            if (Metamodel.IsTypedFeature(e.Kind)) Add("Type", _m.TypeDisplayName(e));
        }
        if (f.IncludeTags) foreach (var t in e.TaggedValues.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase)) Add(t.Key, t.Value);
        if (rows.Count == 0) return;
        if (string.Equals(f.Layout, "inline", StringComparison.OrdinalIgnoreCase))
            foreach (var r in rows) Emit(new ParagraphNode([new TextRun(r[0] + ": ", Bold: true), new TextRun(r[1])]));
        else Emit(new TableNode(null, ["Attribute", "Value"], rows, [1, 3], Compact: true, KeyValue: true));
    }

    private void Table(TableBlock t, Scope sc)
    {
        TableDefinition def;
        if (!string.IsNullOrWhiteSpace(t.TableView))
        {
            var name = Sub(t.TableView, sc);
            var view = _m.OfKind(ElementKind.TableView).FirstOrDefault(v => v.Id == name || string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)
                                                                               || string.Equals(v.QualifiedName, name, StringComparison.OrdinalIgnoreCase));
            if (view == null)
            {
                _doc.Warnings.Add($"Table view '{name}' not found.");
                Emit(ParagraphNode.Plain($"[Table view '{name}' not found]", italic: true));
                return;
            }
            def = TableDefinition.FromElement(view).Clone();
            def.Query = Sub(def.Query, sc, quote: true);
        }
        else
        {
            var q = Sub(t.Query, sc, quote: true);
            def = new TableDefinition
            {
                Query = q,
                Columns = t.Columns.Count > 0 ? t.Columns.Select(c => new TableViewColumn(c.Key, c.Label) { Filter = c.Filter, Width = c.Width }).ToList()
                                              : TableDefinition.DefaultColumns(ModelQuery.Parse(q).Kinds is { Count: 1 } k ? k[0] : null),
                SortKey = t.SortBy, SortDescending = t.Descending,
            };
        }
        var rows = BuildRows(def);
        Counted(rows.Count);
        var cap = NewCaption("Table", Sub(Default(t.Caption, "{count} elements"), sc, count: rows.Count), Ref(t, sc));
        if (rows.Count == 0) { Emit(new TableNode(cap, def.Columns.Select(c => c.Header).ToList(), [[Sub(t.EmptyText, sc), .. Enumerable.Repeat("", Math.Max(0, def.Columns.Count - 1))]])); return; }
        var widths = def.Columns.Select(c => c.Width > 0 ? c.Width / 60.0 : WidthFor(c.Key)).ToList();
        Emit(new TableNode(cap, def.Columns.Select(c => c.Header).ToList(), rows.Select(r => r.Cells).ToList(), widths));
    }

    /// <summary>Like <see cref="ModelTables.Build"/>, but cells use the report value keys (source / target / diagramType …) and natural sorting.</summary>
    private List<ModelTableRow> BuildRows(TableDefinition def)
    {
        var elements = Run(ModelQuery.Parse(def.Query)).AsEnumerable();
        if (_o.LoopLimit > 0) elements = elements.Take(_o.LoopLimit * 4);
        var rows = elements.Select(e => new ModelTableRow(e, def.Columns.Select(c => Val(e, c.Key)).ToList())).ToList();
        for (var i = 0; i < def.Columns.Count; i++)
        {
            var f = def.Columns[i].Filter;
            if (string.IsNullOrWhiteSpace(f)) continue;
            var col = i;
            rows = rows.Where(r => ModelTables.CellMatches(r.Cells[col], f)).ToList();
        }
        if (!string.IsNullOrWhiteSpace(def.SortKey))
        {
            var idx = def.Columns.FindIndex(c => string.Equals(c.Key, def.SortKey, StringComparison.OrdinalIgnoreCase));
            Func<ModelTableRow, string> key = idx >= 0 ? r => r.Cells[idx] : r => Val(r.Element, def.SortKey!);
            rows = (def.SortDescending ? rows.OrderByDescending(key, NaturalComparer.Instance) : rows.OrderBy(key, NaturalComparer.Instance)).ToList();
        }
        return rows;
    }

    private static double WidthFor(string key) => ModelValues.Canonical(key) switch
    {
        "field:reqId" or "kind" or "field:multiplicity" or "priority" or "status" or "risk" or "verifyMethod" or "field:direction" => 0.8,
        "field:text" or "documentation" => 3,
        _ => 1.4,
    };

    private void Requirements(RequirementTableBlock r, Scope sc)
    {
        var reqs = OrderRequirements(Run(ModelQuery.Parse(Sub(r.Query, sc, quote: true))));
        if (_o.LoopLimit > 0) reqs = reqs.Take(_o.LoopLimit * 4).ToList();
        Counted(reqs.Count);
        var attrs = r.Attributes.Count > 0 ? r.Attributes : ["reqId", "name", "text"];
        if (string.Equals(r.Style, "detailed", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var q in reqs)
            {
                Emit(new ParagraphNode([new TextRun(TraceabilityTable.Label(q), Bold: true)], ParaKind.Subheading));
                if (q.Get(FieldKeys.Text) is { Length: > 0 } text) foreach (var n in DocMarkdown.Parse(XRefsOnly(text))) Emit(n);
                var extra = attrs.Where(a => ModelValues.Canonical(a) is not ("field:reqId" or "name" or "field:text"))
                                 .Select(a => (Label: AttrLabel(a), Value: Val(q, a))).Where(x => x.Value.Length > 0).ToList();
                if (extra.Count > 0)
                    Emit(new ParagraphNode(extra.SelectMany((x, i) => new Inline[] { new TextRun((i > 0 ? "   " : "") + x.Label + ": ", Bold: true), new TextRun(x.Value) }).ToList()));
            }
            if (reqs.Count == 0) Emit(ParagraphNode.Plain("No requirements.", italic: true));
            return;
        }
        var cap = NewCaption("Table", Sub(Default(r.Caption, "Requirements"), sc, count: reqs.Count), Ref(r, sc));
        var rows = reqs.Select(q => (IReadOnlyList<string>)attrs.Select(a => Val(q, a)).ToList()).ToList();
        if (rows.Count == 0) rows.Add([.. attrs.Select((_, i) => i == 0 ? "None." : "")]);
        Emit(new TableNode(cap, attrs.Select(AttrLabel).ToList(), rows, attrs.Select(WidthFor).ToList()));
    }

    private static string AttrLabel(string key)
    {
        foreach (var (k, label, _) in ReqFields.Standard) if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return label;
        return ModelValues.Label(key);
    }

    private List<Element> OrderRequirements(IEnumerable<Element> reqs)
    {
        _reqOrder ??= RequirementIds.InDocumentOrder(_m).Select((e, i) => (e, i)).ToDictionary(x => x.e, x => x.i);
        return reqs.OrderBy(e => _reqOrder.TryGetValue(e, out var i) ? i : int.MaxValue).ThenBy(e => e.QualifiedName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void TraceTable(TraceTableBlock t, Scope sc)
    {
        var q = ModelQuery.Parse(Sub(Default(t.Query, "kind:Requirement"), sc, quote: true));
        var rows = TraceRows().Where(r => q.Matches(_m, r.Requirement) && (!t.GapsOnly || r.SatisfyGap || r.VerifyGap)).ToList();
        if (_o.LoopLimit > 0) rows = rows.Take(_o.LoopLimit * 4).ToList();
        Counted(rows.Count);
        var cols = t.Columns.Count > 0 ? t.Columns : [TraceColumn.SatisfiedBy, TraceColumn.VerifiedBy];
        var headers = new List<string> { "Id", "Requirement" };
        headers.AddRange(cols.Select(TraceabilityTable.Title));
        if (t.ShowCoverage) headers.Add("Coverage");
        var cells = rows.Select(r =>
        {
            var row = new List<string> { r.Id, r.Name };
            row.AddRange(cols.Select(c => string.Join("; ", r[c].Select(l => TraceabilityTable.Label(l.Other) + (l.Suspect ? " (suspect)" : "")))));
            if (t.ShowCoverage) row.Add(r.Coverage switch { TraceCoverage.Full => "Full", TraceCoverage.Partial => "Partial", _ => r.IsLeaf ? "None" : "—" });
            return (IReadOnlyList<string>)row;
        }).ToList();
        if (cells.Count == 0) cells.Add([.. headers.Select((_, i) => i == 0 ? "None." : "")]);
        var widths = new List<double> { 0.8, 1.6 };
        widths.AddRange(cols.Select(_ => 1.6));
        if (t.ShowCoverage) widths.Add(0.7);
        Emit(new TableNode(NewCaption("Table", Sub(Default(t.Caption, "Requirements traceability"), sc, count: rows.Count), Ref(t, sc)), headers, cells, widths));
    }

    private void Matrix(TraceMatrixBlock x, Scope sc)
    {
        var rows = Run(ModelQuery.Parse(Sub(x.RowQuery, sc, quote: true)));
        var cols = Run(ModelQuery.Parse(Sub(x.ColumnQuery, sc, quote: true)));
        if (rows.Any(r => r.Kind == ElementKind.Requirement)) rows = OrderRequirements(rows);
        if (cols.Any(r => r.Kind == ElementKind.Requirement)) cols = OrderRequirements(cols);
        if (_o.LoopLimit > 0) { rows = rows.Take(_o.LoopLimit * 2).ToList(); cols = cols.Take(_o.LoopLimit * 2).ToList(); }
        var kinds = x.RelationshipKinds.Count > 0 ? x.RelationshipKinds.ToHashSet() : null;
        // Links by element (both directions), so hiding empty rows / columns costs O(rows + links), not O(rows × columns).
        var links = new Dictionary<Element, HashSet<Element>>();
        void Link(Element a, Element b)
        {
            if (!links.TryGetValue(a, out var set)) links[a] = set = new HashSet<Element>();
            set.Add(b);
        }
        foreach (var r in _m.Relationships)
            if (kinds == null || kinds.Contains(r.Kind)) { Link(r.Source, r.Target); Link(r.Target, r.Source); }
        bool Linked(Element a, Element b) => links.TryGetValue(a, out var set) && set.Contains(b);

        var caption = Sub(Default(x.Caption, "Traceability matrix"), sc, count: rows.Count);
        var per = Math.Clamp(x.MaxColumns, 3, 60);
        if (x.HideEmpty)
        {
            var rowSet = rows.ToHashSet();
            cols = cols.Where(c => links.TryGetValue(c, out var set) && set.Any(rowSet.Contains)).ToList();
        }
        var chunks = cols.Count == 0 ? 1 : (cols.Count + per - 1) / per;
        // With HideEmpty, the rows of each part: rows linked to one of its columns, in row order.
        List<Element>[]? rowsOf = null;
        if (x.HideEmpty)
        {
            var colIndex = new Dictionary<Element, int>(cols.Count);
            for (var i = 0; i < cols.Count; i++) colIndex.TryAdd(cols[i], i);
            rowsOf = Enumerable.Range(0, chunks).Select(_ => new List<Element>()).ToArray();
            var hit = new HashSet<int>();
            foreach (var r in rows)
            {
                if (!links.TryGetValue(r, out var set)) continue;
                hit.Clear();
                foreach (var other in set)
                    if (colIndex.TryGetValue(other, out var ci) && hit.Add(ci / per)) rowsOf[ci / per].Add(r);
            }
        }
        Caption? first = null;
        for (var c = 0; c < chunks; c++)
        {
            _ct.ThrowIfCancellationRequested();
            var part = cols.Count == 0 ? [] : cols.Skip(c * per).Take(Math.Min(per, cols.Count - c * per)).ToList();
            var headers = new List<string> { "" };
            headers.AddRange(part.Select(Short));
            var partRows = rowsOf?[c] ?? (IReadOnlyList<Element>)rows;
            Counted(partRows.Count);
            var body = partRows.Select(r =>
            {
                var row = new List<string> { Short(r) };
                row.AddRange(part.Select(col => Linked(r, col) ? "X" : ""));
                return (IReadOnlyList<string>)row;
            }).ToList();
            if (body.Count == 0) body.Add([.. headers.Select((_, i) => i == 0 ? "None." : "")]);
            var widths = new List<double> { Math.Max(2.0, part.Count / 6.0) };
            widths.AddRange(part.Select(_ => 1.0));
            if (c == 0) first = NewCaption("Table", chunks > 1 ? $"{caption} (part 1 of {chunks})" : caption, Ref(x, sc));
            else Emit(ParagraphNode.Plain($"{first!.Label} (part {c + 1} of {chunks})", italic: true));
            Emit(new TableNode(c == 0 ? first : null, headers, body, widths, Compact: true, Matrix: true));
        }
    }

    private static string Short(Element e)
    {
        var s = TraceabilityTable.Label(e);
        return s.Length > 40 ? s[..40] + "…" : s;
    }

    private void Coverage(CoverageBlock c, Scope sc)
    {
        var traceRows = TraceRows();
        var leaves = traceRows.Where(r => r.IsLeaf && r.Status != "rejected").ToList();
        int sat = leaves.Count(r => r.Satisfied), ver = leaves.Count(r => r.Verified), full = leaves.Count(r => r.Coverage == TraceCoverage.Full);
        string Pct(int n) => leaves.Count == 0 ? "—" : (100.0 * n / leaves.Count).ToString("0.0", CultureInfo.InvariantCulture) + " %";
        Emit(new TableNode(NewCaption("Table", Sub(Default(c.Caption, "Requirement coverage summary"), sc), Ref(c, sc)),
            ["Measure", "Count", "Percent"],
            [
                ["Requirements (all)", traceRows.Count.ToString(CultureInfo.InvariantCulture), ""],
                ["Leaf requirements", leaves.Count.ToString(CultureInfo.InvariantCulture), ""],
                ["Satisfied", sat.ToString(CultureInfo.InvariantCulture), Pct(sat)],
                ["Verified", ver.ToString(CultureInfo.InvariantCulture), Pct(ver)],
                ["Fully covered", full.ToString(CultureInfo.InvariantCulture), Pct(full)],
                ["Suspect links", traceRows.Count(r => r.HasSuspect).ToString(CultureInfo.InvariantCulture), ""],
            ], [3, 1, 1], Compact: true));
        if (c.ByPackage)
        {
            var pk = TraceabilityTable.CoverageByPackage(traceRows);
            if (pk.Count > 1)
                Emit(new TableNode(NewCaption("Table", "Coverage by package", null), ["Package", "Requirements", "Satisfied", "Verified", "Suspect"],
                    pk.Select(p => (IReadOnlyList<string>)[p.Package.QualifiedName, p.Total.ToString(CultureInfo.InvariantCulture),
                        $"{p.Satisfied} ({p.SatisfiedRatio:P0})", $"{p.Verified} ({p.VerifiedRatio:P0})", p.Suspect.ToString(CultureInfo.InvariantCulture)]).ToList(),
                    [3, 1, 1.2, 1.2, 0.8], Compact: true));
        }
        if (c.ListGaps)
        {
            var gaps = traceRows.Where(r => r.SatisfyGap || r.VerifyGap).ToList();
            if (gaps.Count == 0) Emit(ParagraphNode.Plain("Every leaf requirement is satisfied and verified."));
            else
            {
                Emit(new ParagraphNode([new TextRun($"{gaps.Count} requirement(s) have coverage gaps:", Bold: true)]));
                foreach (var g in gaps.Take(_o.LoopLimit > 0 ? _o.LoopLimit : int.MaxValue))
                    Emit(new ParagraphNode([new TextRun(TraceabilityTable.Label(g.Requirement) + " — " +
                        string.Join(", ", new[] { g.SatisfyGap ? "not satisfied" : null, g.VerifyGap ? "not verified" : null }.Where(s => s != null)))], ParaKind.Bullet));
            }
        }
    }

    private void Diagrams(DiagramBlock d, Scope sc)
    {
        var list = new List<Diagram>();
        if (!string.IsNullOrWhiteSpace(d.Diagram))
        {
            var key = Sub(d.Diagram, sc);
            var hit = _m.Find(key) as Diagram ?? _m.Diagrams.FirstOrDefault(x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase) || string.Equals(x.QualifiedName, key, StringComparison.OrdinalIgnoreCase));
            if (hit == null) { _doc.Warnings.Add($"Diagram '{key}' not found."); Emit(ParagraphNode.Plain($"[Diagram '{key}' not found]", italic: true)); return; }
            list.Add(hit);
        }
        else if (string.IsNullOrWhiteSpace(d.Query) && sc.Current is Diagram cur) list.Add(cur);
        else
        {
            list.AddRange(string.IsNullOrWhiteSpace(d.Query)
                ? _m.Diagrams.Where(InScope).OrderBy(x => x.QualifiedName, StringComparer.OrdinalIgnoreCase)
                : Select(d.Query, null, sc).OfType<Diagram>());
        }
        if (d.DiagramTypes.Count > 0) list = list.Where(x => d.DiagramTypes.Contains(x.DiagramType)).ToList();
        if (_o.LoopLimit > 0) list = list.Take(_o.LoopLimit).ToList();
        foreach (var dg in list)
        {
            _ct.ThrowIfCancellationRequested();
            var inner = sc.With(dg, null, sc.Index, sc.Count);
            if (d.IncludeDocumentation && !string.IsNullOrWhiteSpace(dg.Documentation))
                foreach (var n in DocMarkdown.Parse(XRefsOnly(dg.Documentation))) Emit(n);
            Counted();
            var (svg, w, h) = _indexOnly ? ("", 0d, 0d) : Svg(dg);
            var cap = NewCaption("Figure", Sub(Default(d.Caption, "{name}"), inner), Ref(d, inner));
            Emit(new FigureNode(cap, svg, w, h, Math.Clamp(d.MaxHeight, 0.1, 1), dg.Id));
        }
    }

    private void RelationMap(RelationMapBlock b, Scope sc)
    {
        RelationMapSettings settings;
        if (!string.IsNullOrWhiteSpace(b.Map))
        {
            var key = Sub(b.Map, sc);
            var saved = _m.Find(key) is { Kind: ElementKind.RelationMap } byId ? byId
                : RelationMaps.Saved(_m).FirstOrDefault(x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase) || string.Equals(x.QualifiedName, key, StringComparison.OrdinalIgnoreCase));
            if (saved == null) { _doc.Warnings.Add($"Relation map '{key}' not found."); Emit(ParagraphNode.Plain($"[Relation map '{key}' not found]", italic: true)); return; }
            settings = RelationMaps.Load(saved);
        }
        else
        {
            Element? root = sc.Current;
            if (!string.IsNullOrWhiteSpace(b.Root))
            {
                var key = Sub(b.Root, sc).Trim();
                root = _m.Find(key) ?? _m.AllElements.FirstOrDefault(x => x is not Relationship && (string.Equals(x.QualifiedName, key, StringComparison.OrdinalIgnoreCase)
                    || x.Kind == ElementKind.Requirement && string.Equals(x.Get(FieldKeys.RequirementId), key, StringComparison.OrdinalIgnoreCase)))
                    ?? _m.AllElements.FirstOrDefault(x => x is not Relationship && string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase));
                if (root == null) { _doc.Warnings.Add($"Relation map root '{key}' not found."); Emit(ParagraphNode.Plain($"[Relation map root '{key}' not found]", italic: true)); return; }
            }
            if (root == null || root.Kind == ElementKind.Model && string.IsNullOrWhiteSpace(b.Root))
            {
                _doc.Warnings.Add("A relation map block needs a root element (or a loop element).");
                Emit(ParagraphNode.Plain("[Relation map: no root element]", italic: true));
                return;
            }
            settings = b.Settings(root);
        }
        Counted();
        var rootEl = _m.Find(settings.RootId);
        var inner = rootEl != null ? sc.With(rootEl, null, sc.Index, sc.Count) : sc;
        var cap = NewCaption("Figure", Sub(Default(b.Caption, "Relation map of {name}"), inner), Ref(b, inner));
        string svg = ""; double w = 0, h = 0;
        if (!_indexOnly)
        {
            var g = Meridian.Core.Analysis.RelationMaps.Build(_m, settings);
            svg = RelationMapSvg.Render(g, title: cap.Display);
            (w, h) = (Math.Max(g.Width, 200), Math.Max(g.Height, 80));
        }
        Emit(new FigureNode(cap, svg, w, h, Math.Clamp(b.MaxHeight, 0.1, 1), "relmap:" + settings.ToJson()));
    }

    private (string Svg, double W, double H) Svg(Diagram d)
    {
        if (!_o.RenderDiagrams)
            return ($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"400\" height=\"120\"><rect width=\"400\" height=\"120\" fill=\"#f3f5f9\" stroke=\"#99a\"/><text x=\"200\" y=\"64\" text-anchor=\"middle\" font-family=\"sans-serif\" font-size=\"14\">{System.Net.WebUtility.HtmlEncode(d.Name)}</text></svg>", 400, 120);
        var img = _images.Get(_m, d);
        return (img.Svg, img.Width, img.Height);
    }

    internal static (double W, double H) SvgSize(string svg)
    {
        double Attr(string name)
        {
            var head = svg.IndexOf('>') is var gt and > 0 ? svg[..gt] : svg;
            var i = head.IndexOf($" {name}=\"", StringComparison.Ordinal);
            if (i < 0) return 400;
            var start = i + name.Length + 3;
            var end = head.IndexOf('"', start);
            return end > start && double.TryParse(head[start..end].Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 400;
        }
        return (Math.Max(50, Attr("width")), Math.Max(50, Attr("height")));
    }

    private void List(ElementListBlock l, Scope sc)
    {
        var items = Select(l.Query, l.Related, sc);
        if (_o.LoopLimit > 0) items = items.Take(_o.LoopLimit).ToList();
        if (items.Count == 0) { if (!string.IsNullOrWhiteSpace(l.EmptyText)) Emit(ParagraphNode.Plain(Sub(l.EmptyText, sc), italic: true)); return; }
        Counted(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var inner = sc.With(items[i], null, i + 1, items.Count);
            var text = Sub(l.Format, inner).Trim().TrimEnd('—', '-', ' ');
            Emit(new ParagraphNode(DocMarkdown.Inlines(text), l.Numbered ? ParaKind.Numbered : ParaKind.Bullet, i + 1));
        }
    }

    private void Statistics(StatisticsBlock s, Scope sc)
    {
        var rows = _m.AllElements.Where(e => e.Kind != ElementKind.Model && InScope(e)).GroupBy(e => e.Kind).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.ToString())
            .Select(g => (IReadOnlyList<string>)[Metamodel.DisplayName(g.Key), g.Count().ToString(CultureInfo.InvariantCulture)]).ToList();
        Emit(new TableNode(NewCaption("Table", Sub(Default(s.Caption, "Model contents by element kind"), sc), Ref(s, sc)), ["Element kind", "Count"], rows, [3, 1], Compact: true));
    }

    private void Validation(ValidationBlock v, Scope sc)
    {
        _issues ??= new ModelValidator().Validate(_m);
        var list = _issues.Where(i => (v.IncludeInfo || i.Severity != Severity.Info) && InScope(i.Element)).OrderBy(i => i.Severity).ThenBy(i => i.RuleId).Take(Math.Max(1, v.Limit)).ToList();
        if (list.Count == 0) { Emit(ParagraphNode.Plain("No validation issues were found.")); return; }
        Emit(new TableNode(NewCaption("Table", Sub(Default(v.Caption, "Validation issues"), sc), Ref(v, sc)), ["Severity", "Rule", "Element", "Message"],
            list.Select(i => (IReadOnlyList<string>)[i.Severity.ToString(), i.RuleId, i.Element.QualifiedName, i.Message]).ToList(), [0.8, 1, 2, 3], Compact: true));
    }

    private void Instances(InstancesBlock b, Scope sc)
    {
        Element? classifier = null;
        if (!string.IsNullOrWhiteSpace(b.Classifier))
        {
            var name = Sub(b.Classifier, sc);
            classifier = _m.Find(name) ?? _m.AllElements.FirstOrDefault(e => Core.Instances.InstanceModel.CanClassify(e.Kind)
                             && (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(e.QualifiedName, name, StringComparison.OrdinalIgnoreCase)));
            if (classifier == null)
            {
                _doc.Warnings.Add($"Instances: classifier '{name}' not found.");
                Emit(ParagraphNode.Plain($"[Classifier '{name}' not found]", italic: true));
                return;
            }
        }
        else if (sc.Current is { } cur && Core.Instances.InstanceModel.CanClassify(cur.Kind)) classifier = cur;
        var table = Core.Instances.InstanceTable.Build(_m, classifier, b.IncludeNested);
        var rows = table.Rows.Where(r => InScope(r.Instance)).ToList();
        Counted(rows.Count);
        var title = classifier == null ? "Instances" : $"Instances of {classifier.Name}";
        if (b.Layout == "slots")
        {
            if (rows.Count == 0) { Emit(ParagraphNode.Plain(Sub(b.EmptyText, sc))); return; }
            foreach (var r in rows)
            {
                var cells = table.Columns.Select((c, i) => (IReadOnlyList<string>)[c.Header, r.Cells[i]]).Where(x => x[1].Length > 0).ToList();
                Emit(new TableNode(NewCaption("Table", Sub(Default(b.Caption, "{name}"), sc.With(r.Instance, null, 1, 1)), Ref(b, sc)), ["Feature", "Value"], cells, [1, 2], Compact: true, KeyValue: true));
            }
            return;
        }
        var headers = table.Columns.Select(c => c.Header).Prepend("Instance").ToList();
        var cap = NewCaption("Table", Sub(Default(b.Caption, title), sc, count: rows.Count), Ref(b, sc));
        if (rows.Count == 0) { Emit(new TableNode(cap, headers, [[Sub(b.EmptyText, sc), .. Enumerable.Repeat("", headers.Count - 1)]])); return; }
        Emit(new TableNode(cap, headers, rows.Select(r => (IReadOnlyList<string>)r.Cells.Prepend(r.Label).ToList()).ToList(), Compact: true));
    }

    private void FmeaTable(FmeaBlock f, Scope sc)
    {
        var scope = f.CurrentElementOnly ? sc.Current : null;
        var rows = Core.Safety.Fmea.Rows(_m, scope)
            .Where(r => r.Rpn >= f.MinRpn || f.MinRpn <= 0)
            .Where(r => !f.HighRiskOnly || Core.Safety.Fmea.IsHighRisk(_m, r.FailureMode)).ToList();
        if (rows.Count == 0) { Emit(ParagraphNode.Plain("No failure modes.")); return; }
        Emit(new TableNode(NewCaption("Table", Sub(Default(f.Caption, "FMEA worksheet"), sc), Ref(f, sc)),
            ["Item", "Failure mode", "Effect", "Cause", "S", "O", "D", "RPN", "AP", "Recommended actions", "Mitigated by"],
            rows.Select(r =>
            {
                var c = Core.Safety.Fmea.Cells(r);
                return (IReadOnlyList<string>)[c[0], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], c[11], c[14]];
            }).ToList(), [1.2, 1.4, 1.6, 1.6, 0.35, 0.35, 0.35, 0.5, 0.4, 1.6, 1.4], Compact: true));
    }

    private void FaultTreeTables(FaultTreeBlock b, Scope sc)
    {
        var key = string.IsNullOrWhiteSpace(b.TopEvent) ? null : Sub(b.TopEvent, sc);
        var tops = key != null
            ? _m.OfKind(ElementKind.FaultTreeEvent).Where(e => e.Id == key || string.Equals(e.Name, key, StringComparison.OrdinalIgnoreCase)).Take(1).ToList()
            : sc.Current is { Kind: ElementKind.FaultTreeEvent } cur ? [cur] : Core.Safety.FaultTree.TopEvents(_m).ToList();
        if (tops.Count == 0) { Emit(ParagraphNode.Plain("No fault tree found.")); return; }
        foreach (var top in tops)
        {
            var r = Core.Safety.FaultTree.Analyze(_m, top);
            string P(double p) => Core.Safety.SafetyFields.Format(p);
            Emit(new TableNode(NewCaption("Table", Sub(Default(b.Caption, $"Fault tree analysis: {top.DisplayName}"), sc), Ref(b, sc)), ["Measure", "Value"],
            [
                ["Top event", top.DisplayName],
                ["Probability (exact)", r.Probability is { } p ? P(p) : "—"],
                ["Rare-event approximation", P(r.RareEvent)],
                ["Min-cut upper bound", P(r.MinCutUpperBound)],
                ["Minimal cut sets", r.CutSets.Count.ToString(CultureInfo.InvariantCulture) + (r.Truncated ? "+" : "")],
                ["Basic events", r.BasicEvents.Count.ToString(CultureInfo.InvariantCulture)],
            ], [2, 2], Compact: true, KeyValue: true));
            if (r.CutSets.Count > 0)
                Emit(new TableNode(NewCaption("Table", $"Minimal cut sets: {top.DisplayName}", null), ["#", "Order", "Basic events", "Probability"],
                    r.CutSets.Take(Math.Max(1, b.MaxCutSets)).Select((c, i) => (IReadOnlyList<string>)[(i + 1).ToString(CultureInfo.InvariantCulture), c.Order.ToString(CultureInfo.InvariantCulture),
                        string.Join(", ", c.Events.Select(e => e.DisplayName)), P(c.Probability)]).ToList(), [0.4, 0.5, 4, 1], Compact: true));
        }
    }

    private static string Default(string? s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;
    private string? Ref(CaptionedBlock b, Scope sc) => string.IsNullOrWhiteSpace(b.Ref) ? null : Sub(b.Ref, sc);

    // ================================================================== values and placeholders

    private string Val(Element e, string key)
    {
        var k = key.Trim();
        switch (k.ToLowerInvariant())
        {
            case "diagramtype": return e is Diagram d ? Metamodel.DisplayName(d.DiagramType) : "";
            case "source": return e is Relationship r ? TraceabilityTable.Label(r.Source) : "";
            case "target": return e is Relationship r2 ? TraceabilityTable.Label(r2.Target) : "";
            case "label": return TraceabilityTable.Label(e);
            case "verifymethod": return e.Get(ReqFields.VerificationMethod);
        }
        return ModelValues.Get(_m, e, k);
    }

    private static string XRefsOnly(string text)
    {
        if (!text.Contains("{ref:") && !text.Contains("{page:") && !text.Contains("{title:")) return text;
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                foreach (var p in new[] { "ref:", "page:", "title:" })
                {
                    if (string.CompareOrdinal(text, i + 1, p, 0, p.Length) != 0) continue;
                    var end = text.IndexOf('}', i);
                    if (end < 0) break;
                    sb.Append(DocMarkdown.RefOpen).Append(text, i + 1, end - i - 1).Append(DocMarkdown.RefClose);
                    i = end;
                    goto next;
                }
            }
            sb.Append(text[i]);
        next:;
        }
        return sb.ToString();
    }

    /// <summary>Substitutes placeholders; cross-references become marker tokens for the inline parser.</summary>
    private string Sub(string? text, Scope sc, bool quote = false, int? count = null)
    {
        if (string.IsNullOrEmpty(text) || (text.IndexOf('{') < 0 && text.IndexOf('}') < 0)) return text ?? "";
        var sb = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{' && i + 1 < text.Length && text[i + 1] == '{') { sb.Append('{'); i++; continue; }
            if (c == '}' && i + 1 < text.Length && text[i + 1] == '}') { sb.Append('}'); i++; continue; }
            if (c != '{') { sb.Append(c); continue; }
            // find the matching brace (placeholders may nest: {ref:fig-{name}})
            int depth = 0, end = -1;
            for (var j = i; j < text.Length; j++)
            {
                if (text[j] == '{') depth++;
                else if (text[j] == '}' && --depth == 0) { end = j; break; }
            }
            if (end < 0) { sb.Append(c); continue; }
            var inner = text[(i + 1)..end];
            if (inner.Contains('{')) inner = Sub(inner, sc, false, count);
            sb.Append(Placeholder(inner, sc, quote, count));
            i = end;
        }
        return sb.ToString();
    }

    private string Placeholder(string key, Scope sc, bool quote, int? count)
    {
        var k = key.Trim();
        foreach (var p in new[] { "ref:", "page:", "title:" })
            if (k.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return quote ? "" : $"{DocMarkdown.RefOpen}{p[..^1]}:{k[p.Length..].Trim()}{DocMarkdown.RefClose}";
        string? v = k.ToLowerInvariant() switch
        {
            "doc.title" => _doc?.Title ?? Sub(_t.Title, new Scope { Current = _m.Root }),
            "doc.subtitle" => _doc?.Subtitle,
            "doc.number" => _doc?.DocumentNumber ?? _t.DocumentNumber,
            "doc.revision" => _doc?.Revision ?? _t.Revision,
            "doc.classification" => _doc?.Classification ?? _t.Classification,
            "doc.author" => _doc?.Author ?? _t.Author,
            "doc.organization" => _doc?.Organization ?? _t.Organization,
            "model.name" => _m.Root.Name,
            "model.documentation" => _m.Root.Documentation,
            "date" => _date,
            "index" => sc.Index.ToString(CultureInfo.InvariantCulture),
            "count" => (count ?? sc.Count).ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
        if (v == null)
        {
            var dot = k.IndexOf('.');
            if (dot > 0 && sc.Vars.TryGetValue(k[..dot], out var ve)) v = Val(ve, k[(dot + 1)..]);
            else if (k.StartsWith("model.", StringComparison.OrdinalIgnoreCase)) v = Val(_m.Root, k[6..]);
            else v = Val(sc.Current ?? _m.Root, k);
        }
        return quote ? ModelQuery.Quote(v) : v;
    }

    private IReadOnlyList<TraceRow> TraceRows()
    {
        _traceRows ??= TraceabilityTable.Build(_m);
        return Scoped ? _traceRows.Where(r => InScope(r.Requirement)).ToList() : _traceRows;
    }

    /// <summary>Headings, captions and table cells are plain text: render any cross-reference tokens in them now. One pass
    /// over the nodes and one over the heading list (the table of contents), so linear in the document size.</summary>
    private void FixPlainReferences()
    {
        for (var i = 0; i < _doc.Headings.Count; i++)
            if (_doc.Headings[i].Text.IndexOf(DocMarkdown.RefOpen) >= 0)
                _doc.Headings[i] = _doc.Headings[i] with { Text = FixText(_doc.Headings[i].Text, _doc) };
        for (var i = 0; i < _doc.Nodes.Count; i++) _doc.Nodes[i] = FixPlain(_doc.Nodes[i], _doc);
    }

    private static string FixText(string s, ResolvedDocument targets) =>
        s.IndexOf(DocMarkdown.RefOpen) < 0 ? s : DocMarkdown.PlainText(DocMarkdown.Inlines(s), x => targets.XRefText(x));

    private static DocNode FixPlain(DocNode n, ResolvedDocument targets) => n switch
    {
        HeadingNode h when h.Text.IndexOf(DocMarkdown.RefOpen) >= 0 => h with { Text = FixText(h.Text, targets) },
        TableNode t when t.Rows.Any(r => r.Any(c => c.IndexOf(DocMarkdown.RefOpen) >= 0)) =>
            t with { Rows = t.Rows.Select(r => (IReadOnlyList<string>)r.Select(c => FixText(c, targets)).ToList()).ToList() },
        _ => n,
    };
}

/// <summary>Orders "REQ-2" before "REQ-10": digit runs compare numerically.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        a ??= ""; b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0'); var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                var c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
