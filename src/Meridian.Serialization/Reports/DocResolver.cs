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

    private readonly int[] _counters = new int[10];
    private int _chapter, _figSeq, _tabSeq, _anchor;
    private long _iterations;
    private bool _landscape, _wantLandscape;
    private readonly Dictionary<string, (string Svg, double W, double H)> _svgCache = new();
    private Dictionary<Element, int>? _reqOrder;
    private IReadOnlyList<TraceRow>? _traceRows;
    private IReadOnlyList<ValidationIssue>? _issues;
    private readonly string _date;

    private DocResolver(ModelRepository m, DocTemplate t, ResolveOptions? o, IProgress<ReportProgress>? progress, CancellationToken ct)
    {
        _m = m; _t = t; _o = o ?? new ResolveOptions(); _progress = progress; _ct = ct;
        _date = (_o.Date ?? DateTime.Now).ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
    }

    public static ResolvedDocument Resolve(ModelRepository m, DocTemplate t, ResolveOptions? options = null,
                                           IProgress<ReportProgress>? progress = null, CancellationToken ct = default) =>
        new DocResolver(m, t, options, progress, ct).Run();

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

        for (var i = 0; i < _t.Sections.Count; i++)
        {
            _ct.ThrowIfCancellationRequested();
            _progress?.Report(new ReportProgress($"Resolving section {i + 1} of {_t.Sections.Count}", (double)i / Math.Max(1, _t.Sections.Count)));
            Section(_t.Sections[i], 1, root, false);
        }
        while (_doc.Nodes.Count > 0 && _doc.Nodes[^1] is PageBreakNode or OrientationNode) _doc.Nodes.RemoveAt(_doc.Nodes.Count - 1);
        FixPlainReferences();
        return _doc;
    }

    // ================================================================== structure

    private void Emit(DocNode n)
    {
        if (_doc.Nodes.Count >= _o.MaxNodes)
            throw new InvalidOperationException($"The document is too large: more than {_o.MaxNodes:N0} paragraphs, tables and figures. Narrow the template's loops or queries.");
        if (n is not (TitlePageNode or ListingNode or RevisionHistoryNode or PageBreakNode) && _wantLandscape != _landscape)
        {
            _landscape = _wantLandscape;
            if (_doc.Nodes.Count > 0 && _doc.Nodes[^1] is PageBreakNode) _doc.Nodes.RemoveAt(_doc.Nodes.Count - 1);
            _doc.Nodes.Add(new OrientationNode(_landscape));
        }
        var last = _doc.Nodes.Count > 0 ? _doc.Nodes[^1] : null;
        // Never produce blank pages: collapse breaks at the top of a page into one.
        if (n is PageBreakNode && last is null or PageBreakNode or OrientationNode) return;
        if (n is HeadingNode { PageBreakBefore: true } && last is PageBreakNode) _doc.Nodes.RemoveAt(_doc.Nodes.Count - 1);
        if (n is HeadingNode { PageBreakBefore: true } h && last is null or OrientationNode) n = h with { PageBreakBefore = false };
        _doc.Nodes.Add(n);
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
            case ElementListBlock l: List(l, sc); break;
            case StatisticsBlock s: Statistics(s, sc); break;
            case ValidationBlock v: Validation(v, sc); break;
            case PageBreakBlock: Emit(new PageBreakNode()); break;
        }
    }

    private IReadOnlyList<Element> Select(string? query, string? related, Scope sc)
    {
        var q = string.IsNullOrWhiteSpace(query) ? null : ModelQuery.Parse(Sub(query, sc, quote: true));
        if (string.IsNullOrWhiteSpace(related)) return q?.Run(_m) ?? [];
        var cur = sc.Current ?? _m.Root;
        IEnumerable<Element> items;
        var key = related.Trim();
        if (key.Equals("children", StringComparison.OrdinalIgnoreCase) || key.Equals("owned", StringComparison.OrdinalIgnoreCase))
            items = cur.OwnedElements;
        else if (key.Equals("descendants", StringComparison.OrdinalIgnoreCase))
            items = cur.Descendants();
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
            if (top && i % 25 == 0) _progress?.Report(new ReportProgress($"Resolving {i + 1} of {list.Count}", double.NaN));
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
        if (ok && !string.IsNullOrWhiteSpace(i.Exists)) ok = ModelQuery.Parse(Sub(i.Exists, sc, quote: true)).Run(_m).Count > 0;
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
        var cap = NewCaption("Table", Sub(Default(t.Caption, "{count} elements"), sc, count: rows.Count), Ref(t, sc));
        if (rows.Count == 0) { Emit(new TableNode(cap, def.Columns.Select(c => c.Header).ToList(), [[Sub(t.EmptyText, sc), .. Enumerable.Repeat("", Math.Max(0, def.Columns.Count - 1))]])); return; }
        var widths = def.Columns.Select(c => c.Width > 0 ? c.Width / 60.0 : WidthFor(c.Key)).ToList();
        Emit(new TableNode(cap, def.Columns.Select(c => c.Header).ToList(), rows.Select(r => r.Cells).ToList(), widths));
    }

    /// <summary>Like <see cref="ModelTables.Build"/>, but cells use the report value keys (source / target / diagramType …) and natural sorting.</summary>
    private List<ModelTableRow> BuildRows(TableDefinition def)
    {
        var elements = ModelQuery.Parse(def.Query).Run(_m).AsEnumerable();
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
        var reqs = OrderRequirements(ModelQuery.Parse(Sub(r.Query, sc, quote: true)).Run(_m));
        if (_o.LoopLimit > 0) reqs = reqs.Take(_o.LoopLimit * 4).ToList();
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
        _traceRows ??= TraceabilityTable.Build(_m);
        var q = ModelQuery.Parse(Sub(Default(t.Query, "kind:Requirement"), sc, quote: true));
        var rows = _traceRows.Where(r => q.Matches(_m, r.Requirement) && (!t.GapsOnly || r.SatisfyGap || r.VerifyGap)).ToList();
        if (_o.LoopLimit > 0) rows = rows.Take(_o.LoopLimit * 4).ToList();
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
        var rows = ModelQuery.Parse(Sub(x.RowQuery, sc, quote: true)).Run(_m);
        var cols = ModelQuery.Parse(Sub(x.ColumnQuery, sc, quote: true)).Run(_m);
        if (rows.Any(r => r.Kind == ElementKind.Requirement)) rows = OrderRequirements(rows);
        if (cols.Any(r => r.Kind == ElementKind.Requirement)) cols = OrderRequirements(cols);
        if (_o.LoopLimit > 0) { rows = rows.Take(_o.LoopLimit * 2).ToList(); cols = cols.Take(_o.LoopLimit * 2).ToList(); }
        var kinds = x.RelationshipKinds.Count > 0 ? x.RelationshipKinds.ToHashSet() : null;
        var links = new HashSet<(Element, Element)>();
        foreach (var r in _m.Relationships)
            if (kinds == null || kinds.Contains(r.Kind)) { links.Add((r.Source, r.Target)); links.Add((r.Target, r.Source)); }

        var caption = Sub(Default(x.Caption, "Traceability matrix"), sc, count: rows.Count);
        var per = Math.Clamp(x.MaxColumns, 3, 60);
        if (cols.Count == 0) cols = [];
        var chunks = cols.Count == 0 ? 1 : (cols.Count + per - 1) / per;
        Caption? first = null;
        for (var c = 0; c < chunks; c++)
        {
            var part = cols.Skip(c * per).Take(per).ToList();
            var headers = new List<string> { "" };
            headers.AddRange(part.Select(Short));
            var body = rows.Select(r =>
            {
                var row = new List<string> { Short(r) };
                row.AddRange(part.Select(col => links.Contains((r, col)) ? "X" : ""));
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
        _traceRows ??= TraceabilityTable.Build(_m);
        var leaves = _traceRows.Where(r => r.IsLeaf && r.Status != "rejected").ToList();
        int sat = leaves.Count(r => r.Satisfied), ver = leaves.Count(r => r.Verified), full = leaves.Count(r => r.Coverage == TraceCoverage.Full);
        string Pct(int n) => leaves.Count == 0 ? "—" : (100.0 * n / leaves.Count).ToString("0.0", CultureInfo.InvariantCulture) + " %";
        Emit(new TableNode(NewCaption("Table", Sub(Default(c.Caption, "Requirement coverage summary"), sc), Ref(c, sc)),
            ["Measure", "Count", "Percent"],
            [
                ["Requirements (all)", _traceRows.Count.ToString(CultureInfo.InvariantCulture), ""],
                ["Leaf requirements", leaves.Count.ToString(CultureInfo.InvariantCulture), ""],
                ["Satisfied", sat.ToString(CultureInfo.InvariantCulture), Pct(sat)],
                ["Verified", ver.ToString(CultureInfo.InvariantCulture), Pct(ver)],
                ["Fully covered", full.ToString(CultureInfo.InvariantCulture), Pct(full)],
                ["Suspect links", _traceRows.Count(r => r.HasSuspect).ToString(CultureInfo.InvariantCulture), ""],
            ], [3, 1, 1], Compact: true));
        if (c.ByPackage)
        {
            var pk = TraceabilityTable.CoverageByPackage(_traceRows);
            if (pk.Count > 1)
                Emit(new TableNode(NewCaption("Table", "Coverage by package", null), ["Package", "Requirements", "Satisfied", "Verified", "Suspect"],
                    pk.Select(p => (IReadOnlyList<string>)[p.Package.QualifiedName, p.Total.ToString(CultureInfo.InvariantCulture),
                        $"{p.Satisfied} ({p.SatisfiedRatio:P0})", $"{p.Verified} ({p.VerifiedRatio:P0})", p.Suspect.ToString(CultureInfo.InvariantCulture)]).ToList(),
                    [3, 1, 1.2, 1.2, 0.8], Compact: true));
        }
        if (c.ListGaps)
        {
            var gaps = _traceRows.Where(r => r.SatisfyGap || r.VerifyGap).ToList();
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
                ? _m.Diagrams.OrderBy(x => x.QualifiedName, StringComparer.OrdinalIgnoreCase)
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
            var (svg, w, h) = Svg(dg);
            var cap = NewCaption("Figure", Sub(Default(d.Caption, "{name}"), inner), Ref(d, inner));
            Emit(new FigureNode(cap, svg, w, h, Math.Clamp(d.MaxHeight, 0.1, 1), dg.Id));
        }
    }

    private (string Svg, double W, double H) Svg(Diagram d)
    {
        if (_svgCache.TryGetValue(d.Id, out var c)) return c;
        if (!_o.RenderDiagrams)
            return _svgCache[d.Id] = ($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"400\" height=\"120\"><rect width=\"400\" height=\"120\" fill=\"#f3f5f9\" stroke=\"#99a\"/><text x=\"200\" y=\"64\" text-anchor=\"middle\" font-family=\"sans-serif\" font-size=\"14\">{System.Net.WebUtility.HtmlEncode(d.Name)}</text></svg>", 400, 120);
        var svg = SvgRenderer.Render(_m, d);
        var (w, h) = SvgSize(svg);
        return _svgCache[d.Id] = (svg, w, h);
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
        for (var i = 0; i < items.Count; i++)
        {
            var inner = sc.With(items[i], null, i + 1, items.Count);
            var text = Sub(l.Format, inner).Trim().TrimEnd('—', '-', ' ');
            Emit(new ParagraphNode(DocMarkdown.Inlines(text), l.Numbered ? ParaKind.Numbered : ParaKind.Bullet, i + 1));
        }
    }

    private void Statistics(StatisticsBlock s, Scope sc)
    {
        var rows = _m.AllElements.Where(e => e.Kind != ElementKind.Model).GroupBy(e => e.Kind).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.ToString())
            .Select(g => (IReadOnlyList<string>)[Metamodel.DisplayName(g.Key), g.Count().ToString(CultureInfo.InvariantCulture)]).ToList();
        Emit(new TableNode(NewCaption("Table", Sub(Default(s.Caption, "Model contents by element kind"), sc), Ref(s, sc)), ["Element kind", "Count"], rows, [3, 1], Compact: true));
    }

    private void Validation(ValidationBlock v, Scope sc)
    {
        _issues ??= new ModelValidator().Validate(_m);
        var list = _issues.Where(i => v.IncludeInfo || i.Severity != Severity.Info).OrderBy(i => i.Severity).ThenBy(i => i.RuleId).Take(Math.Max(1, v.Limit)).ToList();
        if (list.Count == 0) { Emit(ParagraphNode.Plain("No validation issues were found.")); return; }
        Emit(new TableNode(NewCaption("Table", Sub(Default(v.Caption, "Validation issues"), sc), Ref(v, sc)), ["Severity", "Rule", "Element", "Message"],
            list.Select(i => (IReadOnlyList<string>)[i.Severity.ToString(), i.RuleId, i.Element.QualifiedName, i.Message]).ToList(), [0.8, 1, 2, 3], Compact: true));
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

    /// <summary>Headings, captions and table cells are plain text: render any cross-reference tokens in them now.</summary>
    private void FixPlainReferences()
    {
        string Fix(string s)
        {
            if (s.IndexOf(DocMarkdown.RefOpen) < 0) return s;
            return DocMarkdown.PlainText(DocMarkdown.Inlines(s), x => _doc.XRefText(x));
        }
        for (var i = 0; i < _doc.Nodes.Count; i++)
        {
            switch (_doc.Nodes[i])
            {
                case HeadingNode h when h.Text.IndexOf(DocMarkdown.RefOpen) >= 0:
                    var nh = h with { Text = Fix(h.Text) };
                    _doc.Nodes[i] = nh;
                    _doc.Headings[_doc.Headings.IndexOf(h)] = nh;
                    break;
                case TableNode t when t.Rows.Any(r => r.Any(c => c.IndexOf(DocMarkdown.RefOpen) >= 0)):
                    _doc.Nodes[i] = t with { Rows = t.Rows.Select(r => (IReadOnlyList<string>)r.Select(Fix).ToList()).ToList() };
                    break;
            }
        }
    }
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
