using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization.Spreadsheets;

/// <summary>A row that cannot be imported, and why. Errored rows are skipped; the list can be exported as CSV.</summary>
public sealed record SheetRowError(int Row, string Key, string Column, string Message);

/// <summary>One reviewable change of a spreadsheet import: a new element, an update, or an element missing from the sheet.</summary>
public sealed class SheetImportRow
{
    public required SheetChangeKind Kind { get; init; }
    /// <summary>1-based sheet row (0 for removals).</summary>
    public int Row { get; init; }
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public required SheetKind Target { get; init; }
    public Element? Existing { get; init; }
    public IReadOnlyList<SheetFieldChange> Fields { get; internal set; } = [];
    /// <summary>Adds and changes default to accepted; removals must be opted into.</summary>
    public bool Accept { get; set; }

    internal SheetImporter.RowState? State { get; init; }
}

/// <summary>The outcome of planning an import: reviewable rows, row errors, warnings and the types that could be created.</summary>
public sealed class SheetImportPlan
{
    public required SheetMapping Mapping { get; init; }
    public required Element Scope { get; init; }
    public List<SheetImportRow> Rows { get; } = [];
    public List<SheetRowError> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public int Unchanged { get; internal set; }
    /// <summary>Type names that were not found; created on apply when <see cref="SheetMapping.CreateMissingTypes"/> is on.</summary>
    public List<string> MissingTypes { get; } = [];
    public int DataRows { get; internal set; }
    public IReadOnlyList<string> Headers { get; internal set; } = [];

    internal Dictionary<string, ElementKind> TypesToCreate { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Count(SheetChangeKind k) => Rows.Count(r => r.Kind == k);

    /// <summary>The errors as CSV (Row, Key, Column, Message) for fixing the sheet.</summary>
    public string ErrorsCsv()
    {
        var sb = new StringBuilder("Row,Key,Column,Message\n");
        foreach (var e in Errors)
            sb.Append(e.Row.ToString(CultureInfo.InvariantCulture)).Append(',').Append(SheetExport.Quote(e.Key)).Append(',')
              .Append(SheetExport.Quote(e.Column)).Append(',').Append(SheetExport.Quote(e.Message)).Append('\n');
        return sb.ToString();
    }
}

public sealed record SheetApplyResult(int Added, int Changed, int Removed, int TypesCreated, IReadOnlyList<string> Warnings);

/// <summary>
/// Spreadsheet import and round-trip sync for any element kind: plans a reviewable diff from a sheet and a
/// <see cref="SheetMapping"/>, then applies the accepted rows as one undo step.
/// </summary>
public static class SheetImport
{
    public static SheetImportPlan Plan(ModelRepository model, SheetTable table, SheetMapping mapping, Element? scope = null) =>
        new SheetImporter(model, table, mapping, scope ?? model.Root).Plan();

    public static SheetApplyResult Apply(ModelRepository model, SheetImportPlan plan) => SheetImporter.Apply(model, plan);

    /// <summary>Validates a multiplicity ("1", "0..1", "1..*", "*", "2..4").</summary>
    public static bool IsMultiplicity(string s, out string normalized)
    {
        normalized = Regex.Replace(s, @"\s+", "");
        var m = Regex.Match(normalized, @"^(\d+|\*)(?:\.\.(\d+|\*))?$");
        if (!m.Success) return false;
        if (!m.Groups[2].Success) return true;
        if (m.Groups[1].Value == "*") return false;
        return m.Groups[2].Value == "*" || long.TryParse(m.Groups[1].Value, out var lo) && long.TryParse(m.Groups[2].Value, out var hi) && lo <= hi;
    }

    /// <summary>Splits "1450 kg" into a value and a unit (only when the trailing text parses as a unit).</summary>
    public static (string Value, string? Unit) SplitValue(string text)
    {
        var t = text.Trim();
        var m = Regex.Match(t, @"^([+-]?(?:\d+(?:[.,]\d*)?|[.,]\d+)(?:[eE][+-]?\d+)?)\s*(\S.*)?$");
        if (!m.Success || !m.Groups[2].Success) return (t, null);
        var unit = m.Groups[2].Value.Trim();
        return Units.TryParse(unit, out _, out _) ? (m.Groups[1].Value, unit) : (t, null);
    }

    /// <summary>Splits a list cell ("REQ-001; REQ-002", one per line also works).</summary>
    public static List<string> SplitList(string cell) =>
        cell.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Splits an owner path "a::b::c" or "a/b/c".</summary>
    public static List<string> SplitPath(string path)
    {
        var p = path.Trim();
        var parts = p.Contains("::") ? p.Split("::") : p.Contains('/') ? p.Split('/') : [p];
        return parts.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }
}

/// <summary>The planning and apply logic behind <see cref="SheetImport"/>.</summary>
internal sealed class SheetImporter
{
    private readonly ModelRepository _m;
    private readonly SheetTable _table;
    private readonly SheetMapping _map;
    private readonly Element _scope;
    private readonly SheetImportPlan _plan;
    private readonly Dictionary<SheetColumn, int> _col = [];
    private readonly List<RowState> _rows = [];
    private Dictionary<string, List<Element>>? _byName;
    private Dictionary<string, Element>? _byQn;
    private Dictionary<string, List<Element>>? _byKeyField;
    private Dictionary<string, List<Element>>? _byReqId;

    internal SheetImporter(ModelRepository m, SheetTable table, SheetMapping map, Element scope)
    {
        _m = m;
        _table = table;
        _map = map;
        _scope = scope;
        _plan = new SheetImportPlan { Mapping = map, Scope = scope };
    }

    // ------------------------------------------------------------------ row state

    /// <summary>Owner of a new row: an existing element, another (new) row, or packages to create below an element.</summary>
    internal sealed record OwnerRef(Element? Element, RowState? Row, IReadOnlyList<string> CreatePath)
    {
        public ElementKind Kind => CreatePath.Count > 0 ? ElementKind.Package : Row?.Target.Kind ?? Element!.Kind;
        public string Label => Element is { } e && CreatePath.Count == 0 ? (e.Owner == null ? e.DisplayName : e.QualifiedName)
            : Row != null ? Row.DisplayName
            : string.Join("::", new[] { Element is { Owner: not null } b ? b.QualifiedName : "" }.Where(s => s.Length > 0).Concat(CreatePath));
    }

    /// <summary>A relationship / part-of end: an existing element or a new row.</summary>
    internal sealed record TargetRef(Element? Element, RowState? Row)
    {
        public ElementKind Kind => Element?.Kind ?? Row!.Target.Kind;
    }

    internal sealed record RelSpec(SheetColumn Column, ElementKind Kind, bool Incoming, List<TargetRef> Targets);

    internal sealed class RowState
    {
        public int Line;               // 1-based
        public int Index;              // index into the table rows
        public string Key = "";
        public SheetKind Target = null!;
        public Element? Existing;
        public string? Name;           // null: no name column
        public int Level = -1;         // indentation / outline level (-1: none)
        public string ParentText = "";
        public RowState? ParentRow;
        public Element? ParentElement;
        public RowState? MergedInto;   // duplicate rows of a bill of materials
        public string OwnerText = "";
        public OwnerRef? Owner;
        public readonly Dictionary<string, (string Label, string Value)> Fields = new(StringComparer.Ordinal);
        public HashSet<string>? Stereotypes;
        public string? TypeText;
        public string? TypeLiteral;    // element id or primitive name
        public string? TypePending;    // missing type to create
        public string? Multiplicity;
        public readonly Dictionary<string, (string? Value, string? Unit)> Props = new(StringComparer.OrdinalIgnoreCase);
        public string? OwnValue;       // null: no value column
        public string? OwnUnit;        // null: leave the unit alone
        public bool StereotypesExact;  // a stereotypes column is mapped: the sheet lists them all
        public readonly List<RelSpec> Rels = [];
        public readonly List<(TargetRef Parent, string Multiplicity)> PartOf = [];
        public bool PartOfMapped;
        public readonly List<SheetRowError> Errors = [];
        public SheetImportRow? Review;
        public Element? Created;

        public string DisplayName => Name is { Length: > 0 } n ? n : Key.Length > 0 ? Key : $"row {Line}";
        public bool Failed => Errors.Count > 0;
    }

    private string Cell(RowState r, SheetColumn c) =>
        _col.TryGetValue(c, out var i) && i < _table.Rows[r.Index].Count ? _table.Rows[r.Index][i].Trim() : "";

    private void Error(RowState r, string column, string message) => r.Errors.Add(new SheetRowError(r.Line, r.Key, column, message));

    // ------------------------------------------------------------------ planning

    internal SheetImportPlan Plan()
    {
        var rows = _table.Rows;
        if (rows.Count == 0) { _plan.Warnings.Add("The sheet is empty"); return _plan; }
        var headerRow = _map.HeaderRow is { } h && h >= 0 && h < rows.Count ? h : SheetReader.DetectHeaderRow(rows);
        var headers = rows[headerRow].Select(x => x.Trim()).ToList();
        _plan.Headers = headers;
        foreach (var c in _map.Columns.Where(c => c.Role != SheetRole.Ignore))
        {
            var i = headers.FindIndex(x => string.Equals(x, c.Column.Trim(), StringComparison.OrdinalIgnoreCase));
            if (i < 0 && c.Column.StartsWith('#') && int.TryParse(c.Column[1..], out var n)) i = n - 1;
            if (i >= 0) _col[c] = i;
            else _plan.Warnings.Add($"Column '{c.Column}' ({c.Describe}) is not in the sheet");
        }
        var keyCol = _map.Columns.FirstOrDefault(c => c.Role == SheetRole.Key && _col.ContainsKey(c));
        var nameCol = _map.Columns.FirstOrDefault(c => c.Role == SheetRole.Name && _col.ContainsKey(c));
        var kindCol = _map.Columns.FirstOrDefault(c => c.Role == SheetRole.Kind && _col.ContainsKey(c));
        var levelCol = _map.Columns.FirstOrDefault(c => c.Role == SheetRole.Level && _col.ContainsKey(c));
        var parentCol = _map.Columns.FirstOrDefault(c => c.Role == SheetRole.Parent && _col.ContainsKey(c));
        var ownerCol = _map.Columns.FirstOrDefault(c => c.Role == SheetRole.Owner && _col.ContainsKey(c));
        var fixedKind = SheetKinds.ById(_map.Kind);
        if (kindCol == null && fixedKind == null) throw new FormatException("Choose a target kind, or map a column to Kind");
        if (keyCol == null && nameCol == null && _map.KeyMatch != SheetKeyMatch.QualifiedName)
            throw new FormatException("Map at least a key or a name column");
        if (_map.KeyMatch == SheetKeyMatch.QualifiedName && keyCol == null && nameCol == null)
            throw new FormatException("Map a qualified-name key column, or a name column (with an owner column)");

        // ---- pass 1: key, kind, name, level, match
        for (var i = headerRow + 1; i < rows.Count; i++)
        {
            if (rows[i].All(string.IsNullOrWhiteSpace)) continue;
            if (_rows.Count >= SheetLimits.MaxRows) throw new FormatException($"The sheet has more than {SheetLimits.MaxRows:N0} rows; split it into smaller files");
            var r = new RowState { Line = i + 1, Index = i };
            _rows.Add(r);
            if (keyCol != null) r.Key = Cell(r, keyCol);
            if (nameCol != null)
            {
                r.Name = Cell(r, nameCol);
                if (_map.IndentHierarchy) r.Level = _table.IndentOf(i, _col[nameCol]);
            }
            if (levelCol != null)
            {
                var lt = Cell(r, levelCol);
                if (lt.Length == 0) r.Level = 0;
                else if (int.TryParse(lt.Trim('.'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lv) && lv >= 0) r.Level = lv;
                else if (lt.Contains('.') && lt.Split('.').All(p => int.TryParse(p, out _))) r.Level = lt.Split('.').Length;   // "1.2.3" outline numbers
                else Error(r, levelCol.Column, $"Level '{lt}' is not a whole number");
            }
            if (parentCol != null) r.ParentText = Cell(r, parentCol);
            if (ownerCol != null) r.OwnerText = Cell(r, ownerCol);
            var kindText = kindCol != null ? Cell(r, kindCol) : "";
            var parsed = kindText.Length > 0 ? SheetKinds.Parse(kindText) : null;
            if (kindText.Length > 0 && parsed == null) Error(r, kindCol!.Column, $"Unknown element kind '{kindText}'");
            r.Target = parsed ?? fixedKind ?? SheetKinds.ById("Block")!;
            var kindKnown = parsed != null || fixedKind != null;
            // A QN key gives the owner path and the name when no columns say otherwise.
            if (_map.KeyMatch == SheetKeyMatch.QualifiedName && r.Key.Length > 0)
            {
                var path = SheetImport.SplitPath(r.Key);
                if (r.Name is null or "" && path.Count > 0) r.Name = path[^1];
                if (r.OwnerText.Length == 0 && path.Count > 1 && parentCol == null && levelCol == null && !_map.IndentHierarchy)
                    r.OwnerText = string.Join("::", path.Take(path.Count - 1));
            }
            if (r.Key.Length > 0 && _map.KeyMatch != SheetKeyMatch.QualifiedName) r.Existing = MatchKey(r, r.Key);
            else if (r.Key.Length > 0) r.Existing = FindQn(r.Key);
            if (!kindKnown && r.Existing != null) r.Target = SheetKinds.Of(_m, r.Existing);
            else if (!kindKnown && r.Errors.Count == 0) Error(r, kindCol?.Column ?? "", "No element kind: fill the kind column");
        }
        _plan.DataRows = _rows.Count;

        // ---- duplicate keys (merged for bills of materials, otherwise errors)
        var seen = new Dictionary<string, RowState>(StringComparer.Ordinal);
        foreach (var r in _rows.Where(r => r.Key.Length > 0))
        {
            if (!seen.TryGetValue(r.Key, out var first)) { seen[r.Key] = r; continue; }
            if (_map.Hierarchy == SheetHierarchy.PartOf) r.MergedInto = first;
            else Error(r, keyCol?.Column ?? "", $"Key '{r.Key}' is used by row {first.Line} too; only the first row is imported");
        }

        // ---- hierarchy: level / indentation stack, or the parent column
        if (levelCol != null || _map.IndentHierarchy)
        {
            var stack = new List<RowState>();
            foreach (var r in _rows)
            {
                if (r.Level < 0) continue;
                while (stack.Count > 0 && stack[^1].Level >= r.Level) stack.RemoveAt(stack.Count - 1);
                if (stack.Count > 0) r.ParentRow = Primary(stack[^1]);
                stack.Add(r);
            }
        }
        if (parentCol != null)
            foreach (var r in _rows.Where(r => r.ParentText.Length > 0))
                ResolveParent(r, parentCol);

        // Circular hierarchy: a row that is its own ancestor (through rows, or through existing elements).
        foreach (var r in _rows)
        {
            var visited = new HashSet<RowState>();
            for (var p = r.ParentRow; p != null; p = p.ParentRow)
            {
                if (ReferenceEquals(p, Primary(r)) || !visited.Add(p))
                {
                    Error(r, parentCol?.Column ?? levelCol?.Column ?? "", "Circular hierarchy: the row is its own ancestor");
                    break;
                }
            }
            if (r.ParentElement != null && r.Existing != null && (ReferenceEquals(r.ParentElement, r.Existing) || r.Existing.IsAncestorOf(r.ParentElement)))
                Error(r, parentCol?.Column ?? "", $"Circular hierarchy: '{r.ParentElement.DisplayName}' is inside '{r.Existing.DisplayName}'");
        }

        // QN matching without a key column: owner path (or parent) plus name.
        if (_map.KeyMatch == SheetKeyMatch.QualifiedName && keyCol == null)
            foreach (var r in _rows.Where(r => r.Name is { Length: > 0 }))
            {
                var ownerQn = r.ParentElement != null ? QnOf(r.ParentElement)
                    : r.ParentRow != null ? (r.ParentRow.Existing is { } pe ? QnOf(pe) : null)
                    : r.OwnerText.Length > 0 ? (ResolveExistingOwner(r.OwnerText) is { } oe ? QnOf(oe) : null)
                    : QnOf(_scope);
                if (ownerQn == null) continue;
                r.Key = ownerQn.Length == 0 ? r.Name! : ownerQn + "::" + r.Name;
                r.Existing = FindQn(r.Key);
            }
        _rowsByKey = null;   // keys were just computed
        // No key, or an external key the model has not seen yet: a unique name among the target kind in scope still matches
        // (the first import of a parts list into a model whose blocks have no part numbers; the key is stored then).
        if (_map.KeyMatch is SheetKeyMatch.ExternalId or SheetKeyMatch.Name)
        {
            var taken = _rows.Where(x => x.Existing != null).Select(x => x.Existing!).ToHashSet();
            foreach (var r in _rows.Where(r => r.Existing == null && r.MergedInto == null && (r.Key.Length == 0 || _map.KeyMatch == SheetKeyMatch.ExternalId) && r.Name is { Length: > 0 }))
            {
                var hits = InScope(r.Target).Where(e => e.Name == r.Name && !taken.Contains(e)).ToList();
                if (hits.Count == 1 && (_map.KeyMatch == SheetKeyMatch.Name || _map.KeyField.Length == 0 || hits[0].Get(_map.KeyField).Length == 0))
                {
                    r.Existing = hits[0];
                    taken.Add(hits[0]);
                }
                else if (hits.Count > 1 && _map.KeyMatch == SheetKeyMatch.Name) Error(r, nameCol?.Column ?? "", $"{hits.Count} elements are named '{r.Name}'; use a key column");
            }
        }
        foreach (var r in _rows.Where(r => r.Existing != null && r.MergedInto == null))
            if (!r.Target.Matches(_m, r.Existing!) && r.Existing!.Kind != r.Target.Kind)
                Error(r, kindCol?.Column ?? keyCol?.Column ?? "", $"'{r.Existing.DisplayName}' is a {Metamodel.DisplayName(r.Existing.Kind)}, not a {r.Target.Label}");
        // An element matched by two rows.
        var claimed = new Dictionary<Element, RowState>();
        foreach (var r in _rows.Where(r => r.Existing != null && r.MergedInto == null && !r.Failed))
            if (!claimed.TryAdd(r.Existing!, r))
                Error(r, keyCol?.Column ?? nameCol?.Column ?? "", $"Row {claimed[r.Existing!].Line} already updates '{r.Existing!.DisplayName}'");

        // ---- pass 2: values
        foreach (var r in _rows) ResolveValues(r);

        // ---- skip errored rows; rows depending on them fail too
        bool changed;
        do
        {
            changed = false;
            foreach (var r in _rows.Where(r => !r.Failed))
            {
                var p = r.ParentRow;
                if (r.MergedInto is { Failed: true }) { Error(r, "", $"Row {r.MergedInto.Line} (same key) has errors"); changed = true; }
                else if (p is { Failed: true }) { Error(r, "", $"Parent row {p.Line} has errors"); changed = true; }
                else if (r.Owner?.Row is { Failed: true } o) { Error(r, "", $"Owner row {o.Line} has errors"); changed = true; }
                else if (r.Rels.SelectMany(x => x.Targets).Concat(r.PartOf.Select(x => x.Parent)).FirstOrDefault(t => t.Row is { Failed: true }) is { } bad)
                { Error(r, "", $"Target row {bad.Row!.Line} has errors"); changed = true; }
            }
        } while (changed);
        foreach (var r in _rows) _plan.Errors.AddRange(r.Errors);

        // ---- review rows
        foreach (var r in _rows.Where(r => !r.Failed && r.MergedInto == null))
        {
            if (r.Existing == null)
            {
                var fields = AddedFields(r);
                r.Review = new SheetImportRow { Kind = SheetChangeKind.Added, Row = r.Line, Key = r.Key, Name = r.DisplayName, Target = r.Target, Fields = fields, Accept = true, State = r };
                _plan.Rows.Add(r.Review);
            }
            else
            {
                var fields = ChangedFields(r);
                if (fields.Count == 0) { _plan.Unchanged++; continue; }
                r.Review = new SheetImportRow { Kind = SheetChangeKind.Changed, Row = r.Line, Key = r.Key, Name = r.Existing.DisplayName, Target = r.Target, Existing = r.Existing, Fields = fields, Accept = true, State = r };
                _plan.Rows.Add(r.Review);
            }
        }

        // ---- elements of the target kind(s) in scope that the sheet no longer lists (offered for deletion, unticked)
        var matched = _rows.Where(r => r.Existing != null).Select(r => r.Existing!).ToHashSet();
        var kinds = _rows.Select(r => r.Target).Distinct().ToList();
        if (kinds.Count == 0 && fixedKind != null) kinds.Add(fixedKind);
        var removals = InScope(kinds).Where(e => !matched.Contains(e) && e.Name.Length > 0
                                                 && (_map.KeyMatch != SheetKeyMatch.ExternalId || keyCol == null || e.Get(_map.KeyField).Length > 0)).ToList();
        var removalSet = removals.ToHashSet();
        foreach (var e in removals.Where(e => !e.AncestorsAndSelf().Skip(1).Any(removalSet.Contains)))   // deleting a parent deletes its children
            _plan.Rows.Add(new SheetImportRow { Kind = SheetChangeKind.Removed, Key = KeyOf(e), Name = e.DisplayName, Target = SheetKinds.Of(_m, e), Existing = e, Accept = false });

        _plan.MissingTypes.AddRange(_plan.TypesToCreate.Keys.Order(StringComparer.OrdinalIgnoreCase));
        if (_table.FormulaCells > 0 && !_plan.Warnings.Any(w => w.Contains("formula")))
            _plan.Warnings.Add($"{_table.FormulaCells} cell(s) hold formulas: only the values Excel last saved were read");
        return _plan;
    }

    private static RowState Primary(RowState r) => r.MergedInto ?? r;

    private static bool SameTarget(TargetRef a, TargetRef b) =>
        a.Element != null ? ReferenceEquals(a.Element, b.Element) : ReferenceEquals(a.Row, b.Row);

    private string KeyOf(Element e) => _map.KeyMatch switch
    {
        SheetKeyMatch.ElementId => e.Id,
        SheetKeyMatch.ExternalId => e.Get(_map.KeyField),
        SheetKeyMatch.QualifiedName => e.QualifiedName,
        _ => e.Name,
    };

    private static string QnOf(Element e) => e.Owner == null ? "" : e.QualifiedName;

    /// <summary>Elements in scope of the given target kinds (the scope itself excluded).</summary>
    private IEnumerable<Element> InScope(params IEnumerable<SheetKind> kinds)
    {
        var list = kinds.ToList();
        var stereo = _map.Stereotype;
        return _scope.Descendants().Where(e => !e.IsRelationship && list.Any(k => k.Matches(_m, e))
                                               && (string.IsNullOrEmpty(stereo) || e.Stereotypes.Contains(stereo, StringComparer.OrdinalIgnoreCase)));
    }

    private readonly Dictionary<string, List<Element>> _inScope = [];

    private List<Element> InScope(SheetKind kind)
    {
        if (!_inScope.TryGetValue(kind.Id, out var list)) _inScope[kind.Id] = list = InScope([kind]).ToList();
        return list;
    }

    // ------------------------------------------------------------------ lookups

    private Dictionary<string, RowState>? _rowsByKey;
    private Dictionary<string, List<RowState>>? _rowsByName;

    /// <summary>Sheet rows by key (qualified-name keys normalised to "a::b"); the first row wins.</summary>
    private Dictionary<string, RowState> RowsByKey
    {
        get
        {
            if (_rowsByKey != null) return _rowsByKey;
            _rowsByKey = new Dictionary<string, RowState>(StringComparer.Ordinal);
            foreach (var r in _rows.Where(r => r.Key.Length > 0))
                _rowsByKey.TryAdd(_map.KeyMatch == SheetKeyMatch.QualifiedName ? string.Join("::", SheetImport.SplitPath(r.Key)) : r.Key, r);
            return _rowsByKey;
        }
    }

    private Dictionary<string, List<RowState>> RowsByName => _rowsByName ??= _rows.Where(r => r.MergedInto == null && r.Name is { Length: > 0 })
        .GroupBy(r => r.Name!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

    /// <summary>How the review shows a related element: a requirement by its id, anything else by name.</summary>
    private static string Display(Element e) => e.Kind == ElementKind.Requirement && e.Get(FieldKeys.RequirementId) is { Length: > 0 } id ? id : e.DisplayName;
    private ILookup<string, Element>? _parts;
    private ILookup<string, Element> Parts => _parts ??= SheetExport.PartIndex(_m);

    private Dictionary<string, List<Element>> ByName => _byName ??= _m.AllElements.Where(e => !e.IsRelationship && e.Kind is not (ElementKind.Diagram or ElementKind.Model) && e.Name.Length > 0)
        .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, Element> ByQn
    {
        get
        {
            if (_byQn != null) return _byQn;
            _byQn = new Dictionary<string, Element>(StringComparer.Ordinal);
            foreach (var e in _m.AllElements.Where(e => !e.IsRelationship && e.Owner != null)) _byQn.TryAdd(e.QualifiedName, e);
            return _byQn;
        }
    }

    private Dictionary<string, List<Element>> ByKeyField => _byKeyField ??= _m.AllElements.Where(e => _map.KeyField.Length > 0 && e.Get(_map.KeyField).Length > 0)
        .GroupBy(e => e.Get(_map.KeyField), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

    private Dictionary<string, List<Element>> ByReqId => _byReqId ??= _m.OfKind(ElementKind.Requirement).Where(e => e.Get(FieldKeys.RequirementId).Length > 0)
        .GroupBy(e => e.Get(FieldKeys.RequirementId), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

    private Element? FindQn(string text)
    {
        var path = SheetImport.SplitPath(text);
        if (path.Count == 0) return null;
        var qn = string.Join("::", path);
        if (ByQn.TryGetValue(qn, out var e)) return e;
        var rootName = _m.Root.Name;
        if (path.Count > 1 && path[0] == rootName && ByQn.TryGetValue(string.Join("::", path.Skip(1)), out e)) return e;
        if (_scope.Owner != null && ByQn.TryGetValue(_scope.QualifiedName + "::" + qn, out e)) return e;
        return null;
    }

    private Element? MatchKey(RowState r, string key)
    {
        switch (_map.KeyMatch)
        {
            case SheetKeyMatch.ElementId:
                return _m.Find(key) is { IsRelationship: false } e ? e : null;
            case SheetKeyMatch.ExternalId:
                if (!ByKeyField.TryGetValue(key, out var list)) return null;
                var fit = list.Where(x => r.Target.Kind == x.Kind).ToList();
                if (fit.Count > 1) _plan.Warnings.Add($"Key {key} is used by {fit.Count} elements; the first one is updated");
                return fit.FirstOrDefault() ?? list[0];
            case SheetKeyMatch.Name:
                var hits = InScope(r.Target).Where(x => x.Name == key).ToList();
                return hits.Count == 1 ? hits[0] : null;
            default:
                return FindQn(key);
        }
    }

    /// <summary>Finds a target named in a cell. Rows of this sheet win over the model (they may be new).</summary>
    private TargetRef? FindTarget(string text, SheetTargetMatch match, out string? problem)
    {
        problem = null;
        var t = text.Trim();
        if (t.Length == 0) return null;
        TargetRef FromRow(RowState row) => Primary(row).Existing is { } existing ? new TargetRef(existing, null) : new TargetRef(null, Primary(row));
        bool Try(SheetTargetMatch m) => match == SheetTargetMatch.Auto || match == m;

        if (Try(SheetTargetMatch.Key) && _map.KeyMatch != SheetKeyMatch.QualifiedName && RowsByKey.TryGetValue(t, out var byKey)) return FromRow(byKey);
        if (Try(SheetTargetMatch.ElementId) && _m.Find(t) is { IsRelationship: false } byId) return new TargetRef(byId, null);
        if (Try(SheetTargetMatch.RequirementId))
        {
            // "REQ-001 Acceleration" (an exported label) also matches by its leading id.
            var id = t.Split(' ', 2)[0];
            if (ByReqId.TryGetValue(t, out var reqs) || ByReqId.TryGetValue(id, out reqs)) return new TargetRef(reqs[0], null);
        }
        if (Try(SheetTargetMatch.Key) && _map.KeyMatch == SheetKeyMatch.ExternalId && ByKeyField.TryGetValue(t, out var keyed)) return new TargetRef(keyed[0], null);
        if (Try(SheetTargetMatch.Key) && _map.KeyMatch == SheetKeyMatch.ElementId && _m.Find(t) is { IsRelationship: false } byKeyId) return new TargetRef(byKeyId, null);
        if (Try(SheetTargetMatch.QualifiedName) || match == SheetTargetMatch.Key && _map.KeyMatch == SheetKeyMatch.QualifiedName)
        {
            if (_map.KeyMatch == SheetKeyMatch.QualifiedName && RowsByKey.TryGetValue(string.Join("::", SheetImport.SplitPath(t)), out var qnRow)) return FromRow(qnRow);
            if ((t.Contains("::") || t.Contains('/') || match != SheetTargetMatch.Auto) && FindQn(t) is { } byQn) return new TargetRef(byQn, null);
        }
        if (Try(SheetTargetMatch.Name) || match == SheetTargetMatch.Key && _map.KeyMatch == SheetKeyMatch.Name)
        {
            var rowHits = RowsByName.TryGetValue(t, out var named) ? named : [];
            if (rowHits.Count == 1) return FromRow(rowHits[0]);
            if (ByName.TryGetValue(t, out var hits))
            {
                var exact = hits.Where(h => h.Name == t).ToList();
                if (exact.Count == 0) exact = hits;
                if (exact.Count == 1) return new TargetRef(exact[0], null);
                problem = $"'{t}' matches {exact.Count} elements; use a qualified name";
                return null;
            }
        }
        problem = $"'{t}' not found";
        return null;
    }

    private void ResolveParent(RowState r, SheetColumn col)
    {
        var target = FindTarget(r.ParentText, col.Match == SheetTargetMatch.Auto ? SheetTargetMatch.Auto : col.Match, out var problem);
        if (target == null) { Error(r, col.Column, $"Parent {problem ?? "not found"}"); return; }
        if (target.Row != null) r.ParentRow = target.Row;
        else r.ParentElement = target.Element;
    }

    /// <summary>An owner path that already exists (no packages created).</summary>
    private Element? ResolveExistingOwner(string text)
    {
        var path = SheetImport.SplitPath(text);
        if (path.Count == 0) return null;
        if (FindQn(text) is { } e) return e;
        if (path.Count == 1 && ByName.TryGetValue(path[0], out var hits))
        {
            var ns = hits.Where(h => h.Name == path[0] && Metamodel.IsNamespace(h.Kind)).ToList();
            if (ns.Count == 1) return ns[0];
        }
        return null;
    }

    /// <summary>Owner path → element, a new row, or packages to create (only when the row's kind may live in a package).</summary>
    private OwnerRef? ResolveOwner(RowState r, string text, string column)
    {
        if (ResolveExistingOwner(text) is { } e) return new OwnerRef(e, null, []);
        var path = SheetImport.SplitPath(text);
        // A new row of this sheet whose qualified-name key is the path ("Ops::Start" owning "Ops::Start::Crank").
        if (_map.KeyMatch == SheetKeyMatch.QualifiedName)
        {
            var joined = string.Join("::", path);
            if (RowsByKey.TryGetValue(joined, out var keyed) && !ReferenceEquals(keyed, r))
                return keyed.Existing != null ? new OwnerRef(keyed.Existing, null, []) : new OwnerRef(null, Primary(keyed), []);
        }
        if (path.Count == 1)
        {
            var rowHits = (RowsByName.TryGetValue(path[0], out var named) ? named : []).Where(x => !ReferenceEquals(x, r) && x.Existing == null && Metamodel.IsNamespace(x.Target.Kind)).ToList();
            if (rowHits.Count == 1) return new OwnerRef(null, rowHits[0], []);
            if (ByName.TryGetValue(path[0], out var hits) && hits.Count(h => Metamodel.IsNamespace(h.Kind)) > 1)
            {
                Error(r, column, $"Owner '{text}' matches {hits.Count(h => Metamodel.IsNamespace(h.Kind))} elements; use a qualified name (a::b)");
                return null;
            }
        }
        if (!Metamodel.CanOwn(ElementKind.Package, r.Target.Kind))
        {
            Error(r, column, $"Owner '{text}' not found (a {r.Target.Label} needs an existing owner)");
            return null;
        }
        // Deepest existing prefix from the model root (or the import scope), then packages for the rest.
        Element baseEl = _m.Root;
        var i = 0;
        if (path.Count > 1 && path[0] == _m.Root.Name && _m.Root.OwnedElements.All(c => c.Name != path[0])) i = 1;
        var start = path.Count > i && _m.Root.OwnedElements.Any(c => c.Name == path[i]) ? _m.Root : _scope;
        baseEl = start;
        for (; i < path.Count; i++)
        {
            var next = baseEl.OwnedElements.FirstOrDefault(c => c.Name == path[i] && !c.IsRelationship);
            if (next == null) break;
            baseEl = next;
        }
        if (!Metamodel.CanOwn(baseEl.Kind, ElementKind.Package))
        {
            Error(r, column, $"Cannot create package '{path[i]}' inside the {Metamodel.DisplayName(baseEl.Kind)} '{baseEl.DisplayName}'");
            return null;
        }
        return new OwnerRef(baseEl, null, path.Skip(i).ToList());
    }

    private void ResolveValues(RowState r)
    {
        if (r.Failed) return;
        if (r.MergedInto != null)
        {
            // Merged duplicates (one part used in several assemblies) only contribute their parent and quantity.
            if (_map.Columns.FirstOrDefault(c => c.Role == SheetRole.Multiplicity && _col.ContainsKey(c)) is { } mc && Cell(r, mc) is { Length: > 0 } mv)
            {
                if (SheetImport.IsMultiplicity(mv, out var norm)) r.Multiplicity = norm;
                else { Error(r, mc.Column, $"Bad multiplicity '{mv}' (use 1, 0..1, 1..*, *)"); return; }
            }
            ResolvePartOf(r);
            return;
        }
        var kind = r.Target.Kind;
        var isFeature = Metamodel.IsTypedFeature(kind);

        // Owner: hierarchy (containment) wins, then the owner column, else the scope for new rows.
        var containment = _map.Hierarchy == SheetHierarchy.Containment;
        var ownerCol = _map.Columns.FirstOrDefault(c => c.Role == SheetRole.Owner && _col.ContainsKey(c));
        if (containment && r.ParentRow != null) r.Owner = new OwnerRef(null, r.ParentRow, []);
        else if (containment && r.ParentElement != null) r.Owner = new OwnerRef(r.ParentElement, null, []);
        else if (r.OwnerText.Length > 0) r.Owner = ResolveOwner(r, r.OwnerText, ownerCol?.Column ?? "Key");
        if (r.Failed) return;
        if (r.Owner != null && !Metamodel.CanOwn(r.Owner.Kind, kind))
            Error(r, ownerCol?.Column ?? "", $"A {Metamodel.DisplayName(r.Owner.Kind)} cannot own a {r.Target.Label}");
        else if (r.Owner == null && r.Existing == null && !Metamodel.CanOwn(_scope.Kind, kind))
            Error(r, ownerCol?.Column ?? "", $"A {r.Target.Label} needs an owner: map an owner column (the {Metamodel.DisplayName(_scope.Kind)} '{_scope.DisplayName}' cannot own it)");
        if (r.Existing == null && r.Name is null or "" && r.Key.Length == 0 && Metamodel.NeedsName(kind))
            Error(r, _map.Column(SheetRole.Name)?.Column ?? "", "No name");

        var stereo = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (r.Target.Stereotype is { } ks) stereo.Add(ks);
        if (!string.IsNullOrEmpty(_map.Stereotype)) stereo.Add(_map.Stereotype!);
        var stereoMapped = false;
        var partOfCells = new List<(SheetColumn Column, string Cell)>();
        string? parsedUnit = null, unitCell = null;
        // The source key is stored on the element, so the next import updates it instead of creating a duplicate.
        if (_map.KeyMatch == SheetKeyMatch.ExternalId && r.Key.Length > 0 && _map.KeyField.Length > 0)
            r.Fields[_map.KeyField] = (_map.Column(SheetRole.Key)?.Column ?? "Key", r.Key);

        foreach (var c in _map.Columns.Where(c => _col.ContainsKey(c)))
        {
            var v = Cell(r, c);
            switch (c.Role)
            {
                case SheetRole.Documentation: r.Fields[FieldKeys.Documentation] = (c.Column, v); break;
                case SheetRole.Tag when !string.IsNullOrWhiteSpace(c.Name): r.Fields[FieldKeys.TagPrefix + c.Name!.Trim()] = (c.Column, v); break;
                case SheetRole.Attribute when !string.IsNullOrWhiteSpace(c.Name): r.Fields[ReqFields.CustomPrefix + c.Name!.Trim()] = (c.Column, v); break;
                case SheetRole.Field when !string.IsNullOrWhiteSpace(c.Name):
                    var key = c.Name!.Trim();
                    if (key is FieldKeys.Name or FieldKeys.Stereotypes or FieldKeys.Type) { Error(r, c.Column, $"Map '{key}' with its own role"); break; }
                    if (string.Equals(key, _map.KeyField, StringComparison.Ordinal) && _map.KeyMatch == SheetKeyMatch.ExternalId && _map.Column(SheetRole.Key) != null) break;
                    r.Fields[key] = (c.Column, v);
                    break;
                case SheetRole.Stereotypes:
                    stereoMapped = true;
                    foreach (var s in v.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) stereo.Add(s.Trim('«', '»'));
                    break;
                case SheetRole.Type:
                    if (kind == ElementKind.Column) r.Fields[FieldKeys.SqlType] = (c.Column, v);
                    else if (isFeature) ResolveType(r, c, v);
                    break;
                case SheetRole.Multiplicity:
                    if (v.Length == 0) { if (isFeature) r.Multiplicity = ""; break; }
                    if (!SheetImport.IsMultiplicity(v, out var mult)) { Error(r, c.Column, $"Bad multiplicity '{v}' (use 1, 0..1, 1..*, *)"); break; }
                    r.Multiplicity = mult;
                    break;
                case SheetRole.Value:
                {
                    var (value, unit) = v.Length == 0 ? ("", null) : SheetImport.SplitValue(v);
                    if (!string.IsNullOrWhiteSpace(c.Name))
                    {
                        var cur = r.Props.TryGetValue(c.Name!.Trim(), out var p) ? p : (null, null);
                        r.Props[c.Name.Trim()] = (value, cur.Unit ?? unit);
                    }
                    else if (kind is ElementKind.Property or ElementKind.Port) { r.OwnValue = value; parsedUnit = unit; }
                    else if (v.Length > 0) Error(r, c.Column, $"A {r.Target.Label} has no value of its own; name the value property in the mapping");
                    break;
                }
                case SheetRole.Unit:
                    if (!string.IsNullOrWhiteSpace(c.Name))
                    {
                        var cur = r.Props.TryGetValue(c.Name!.Trim(), out var p) ? p : (null, null);
                        r.Props[c.Name.Trim()] = (cur.Value, v.Length > 0 ? v : cur.Unit);
                    }
                    else if (kind is ElementKind.Property or ElementKind.Port) unitCell = v;
                    break;
                case SheetRole.Relationship:
                    ResolveRelationship(r, c, v);
                    break;
                case SheetRole.PartOf:
                    r.PartOfMapped = true;
                    partOfCells.Add((c, v));
                    break;
            }
        }
        // Unit column wins; else the unit written after the value; a value without any unit clears a stale one.
        if (unitCell is { Length: > 0 }) r.OwnUnit = unitCell;
        else if (r.OwnValue != null || unitCell != null) r.OwnUnit = parsedUnit ?? "";
        foreach (var (c, v) in partOfCells)
            foreach (var t in SplitTargets(v, c, r)) AddPartOf(r, t, c.Column);
        if (stereoMapped || stereo.Count > 0) r.Stereotypes = stereo;
        r.StereotypesExact = stereoMapped;
        if (_map.Hierarchy == SheetHierarchy.PartOf) ResolvePartOf(r);
    }

    private void ResolvePartOf(RowState r)
    {
        if (r.ParentRow == null && r.ParentElement == null) return;
        r.PartOfMapped = true;
        var target = r.ParentRow != null ? new TargetRef(r.ParentRow.Existing, r.ParentRow.Existing == null ? r.ParentRow : null) : new TargetRef(r.ParentElement, null);
        AddPartOf(Primary(r), target, _map.Column(SheetRole.Level)?.Column ?? _map.Column(SheetRole.Parent)?.Column ?? "", r.Multiplicity);
    }

    private void AddPartOf(RowState r, TargetRef parent, string column, string? multiplicity = null)
    {
        if (parent.Kind is not (ElementKind.Block or ElementKind.Class or ElementKind.Component))
        {
            Error(r, column, $"Part of: '{(parent.Element?.DisplayName ?? parent.Row!.DisplayName)}' is a {Metamodel.DisplayName(parent.Kind)}, not a block");
            return;
        }
        if (r.Target.Kind is not (ElementKind.Block or ElementKind.Class or ElementKind.Component or ElementKind.InterfaceBlock or ElementKind.ConstraintBlock))
        {
            Error(r, column, $"Part of: a {r.Target.Label} cannot type a part property");
            return;
        }
        if (parent.Row != null && ReferenceEquals(parent.Row, r) || parent.Element != null && ReferenceEquals(parent.Element, r.Existing))
        {
            Error(r, column, "Circular hierarchy: a part of itself");
            return;
        }
        if (r.PartOf.Any(x => SameTarget(x.Parent, parent))) return;
        r.PartOf.Add((parent, multiplicity ?? r.Multiplicity ?? "1"));
    }

    private IEnumerable<TargetRef> SplitTargets(string cell, SheetColumn c, RowState r)
    {
        foreach (var item in SheetImport.SplitList(cell))
        {
            var t = FindTarget(item, c.Match, out var problem);
            if (t == null && item.Contains(','))
            {
                // "A, B" when the sheet used commas.
                var parts = item.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => FindTarget(p, c.Match, out _)).ToList();
                if (parts.All(p => p != null)) { foreach (var p in parts) yield return p!; continue; }
            }
            if (t == null) { Error(r, c.Column, $"{c.Describe}: {problem ?? $"'{item}' not found"}"); continue; }
            yield return t;
        }
    }

    private void ResolveRelationship(RowState r, SheetColumn c, string v)
    {
        if (c.RelationshipKind is not { } kind) { Error(r, c.Column, $"'{c.Relationship}' is not a relationship kind"); return; }
        var targets = new List<TargetRef>();
        foreach (var t in SplitTargets(v, c, r))
        {
            var (src, tgt) = c.Incoming ? (t.Kind, r.Target.Kind) : (r.Target.Kind, t.Kind);
            if (Metamodel.CheckEnds(kind, src, tgt) is { } problem) { Error(r, c.Column, problem); continue; }
            if (!targets.Any(x => SameTarget(x, t))) targets.Add(t);
        }
        r.Rels.Add(new RelSpec(c, kind, c.Incoming, targets));
    }

    private static readonly ElementKind[] PartTypeKinds = [ElementKind.Block, ElementKind.Class, ElementKind.Component, ElementKind.ConstraintBlock];
    private static readonly ElementKind[] ValueTypeKinds = [ElementKind.ValueType, ElementKind.DataType, ElementKind.Enumeration];
    private static readonly ElementKind[] PortTypeKinds = [ElementKind.InterfaceBlock, ElementKind.Interface, ElementKind.Block, ElementKind.Class];

    private void ResolveType(RowState r, SheetColumn c, string text)
    {
        r.TypeText = text;
        if (text.Length == 0) { r.TypeLiteral = ""; return; }
        var prim = Metamodel.PrimitiveTypes.FirstOrDefault(p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase));
        if (prim != null) { r.TypeLiteral = prim; return; }
        var preferred = r.Target.Id switch
        {
            SheetKinds.PartProperty => PartTypeKinds,
            SheetKinds.ValueProperty => ValueTypeKinds,
            _ when r.Target.Kind == ElementKind.Port => PortTypeKinds,
            _ => [],
        };
        Element? found = null;
        if (text.Contains("::") || text.Contains('/')) found = FindQn(text) is { } q && Metamodel.IsClassifier(q.Kind) ? q : null;
        else if (ByName.TryGetValue(text, out var hits))
        {
            var classifiers = hits.Where(h => Metamodel.IsClassifier(h.Kind) && h.Name == text).ToList();
            if (classifiers.Count == 0) classifiers = hits.Where(h => Metamodel.IsClassifier(h.Kind)).ToList();
            if (classifiers.Count > 1 && preferred.Length > 0) { var p = classifiers.Where(h => preferred.Contains(h.Kind)).ToList(); if (p.Count > 0) classifiers = p; }
            if (classifiers.Count > 1) { Error(r, c.Column, $"Type '{text}' matches {classifiers.Count} elements; use a qualified name"); return; }
            found = classifiers.FirstOrDefault();
        }
        if (found != null) { r.TypeLiteral = found.Id; return; }
        // A classifier created by another row of this sheet.
        var row = (RowsByName.TryGetValue(text, out var named) ? named : []).FirstOrDefault(x => x.Existing == null && Metamodel.IsClassifier(x.Target.Kind));
        if (row != null) { r.TypePending = "row:" + row.Line; return; }
        var createKind = r.Target.Id == SheetKinds.PartProperty ? ElementKind.Block
            : r.Target.Id == SheetKinds.ValueProperty || r.Target.Kind == ElementKind.Property ? ElementKind.ValueType
            : r.Target.Kind == ElementKind.Port ? ElementKind.InterfaceBlock : ElementKind.Block;
        var name = SheetImport.SplitPath(text).LastOrDefault() ?? text;
        _plan.TypesToCreate.TryAdd(name, createKind);
        if (_map.CreateMissingTypes) r.TypePending = name;
        else Error(r, c.Column, $"Unknown type '{text}' (tick \"create missing types\" to add it)");
    }

    // ------------------------------------------------------------------ diffs

    private string Label(TargetRef t) => t.Element != null ? Display(t.Element) : t.Row!.DisplayName + " (new)";

    private static string StereoText(IEnumerable<string> s) => string.Join(", ", s);

    private List<SheetFieldChange> AddedFields(RowState r)
    {
        var list = new List<SheetFieldChange>();
        void Add(string key, string label, string value) { if (value.Length > 0) list.Add(new SheetFieldChange(key, label, "", value)); }
        Add("kind", "Kind", r.Target.Label);
        if (r.Owner != null) Add("owner", "Owner", r.Owner.Label + (r.Owner.CreatePath.Count > 0 ? " (new packages)" : r.Owner.Row != null ? " (new)" : ""));
        foreach (var (key, (label, value)) in r.Fields) Add(key, label, value);
        if (r.Stereotypes is { Count: > 0 }) Add(FieldKeys.Stereotypes, "Stereotypes", StereoText(r.Stereotypes));
        if (r.TypeText is { Length: > 0 }) Add(FieldKeys.Type, "Type", r.TypeText + (r.TypePending is { } p && !p.StartsWith("row:") ? " (new type)" : ""));
        if (r.Multiplicity is { Length: > 0 } && Metamodel.IsTypedFeature(r.Target.Kind)) Add(FieldKeys.Multiplicity, "Multiplicity", r.Multiplicity);
        if (r.OwnValue != null) Add(FieldKeys.DefaultValue, "Value", JoinValue(r.OwnValue, r.OwnUnit));
        else if (r.OwnUnit is { Length: > 0 }) Add(ExtFieldKeys.Unit, "Unit", r.OwnUnit);
        foreach (var (prop, (value, unit)) in r.Props) Add("prop:" + prop, prop, JoinValue(value, unit));
        foreach (var rel in r.Rels) Add("rel:" + rel.Column.Column, rel.Column.Column, string.Join("; ", rel.Targets.Select(Label)));
        if (r.PartOf.Count > 0) Add("partOf", "Part of", string.Join("; ", r.PartOf.Select(p => PartLabel(Label(p.Parent), p.Multiplicity))));
        return list;
    }

    private static string PartLabel(string parent, string mult) => mult is "" or "1" ? parent : $"{parent} [{mult}]";

    private static string JoinValue(string? value, string? unit) => unit is { Length: > 0 } ? $"{value} {unit}".Trim() : value ?? "";

    private List<SheetFieldChange> ChangedFields(RowState r)
    {
        var e = r.Existing!;
        var list = new List<SheetFieldChange>();
        void Cmp(string key, string label, string old, string @new) { if (old != @new) list.Add(new SheetFieldChange(key, label, old, @new)); }

        if (r.Name is { Length: > 0 } name) Cmp(FieldKeys.Name, _map.Column(SheetRole.Name)?.Column ?? "Name", e.Name, name);
        // Owner: move when the sheet names a different owner (or parent), or when a nested element became top-level.
        var desired = DesiredOwner(r);
        if (desired is { } d && !(d.Element != null && d.CreatePath.Count == 0 && ReferenceEquals(d.Element, e.Owner)))
            list.Add(new SheetFieldChange("owner", r.ParentRow != null || r.ParentElement != null ? "Parent" : "Owner", e.Owner?.DisplayName ?? "", d.Label + (d.Row != null ? " (new)" : d.CreatePath.Count > 0 ? " (new packages)" : "")));
        foreach (var (key, (label, value)) in r.Fields) Cmp(key, label, e.Get(key), value);
        if (r.Stereotypes != null)
        {
            var cur = e.Stereotypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var want = r.StereotypesExact ? r.Stereotypes : cur.Union(r.Stereotypes, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!cur.SetEquals(want)) list.Add(new SheetFieldChange(FieldKeys.Stereotypes, "Stereotypes", StereoText(e.Stereotypes), StereoText(Ordered(e.Stereotypes, want))));
        }
        if (r.TypeText != null)
        {
            var cur = e.TypeRef;
            var same = r.TypePending == null && cur == (r.TypeLiteral ?? "")
                       || r.TypeLiteral != null && _m.Find(cur) is null && string.Equals(cur, r.TypeLiteral, StringComparison.OrdinalIgnoreCase);
            if (!same) list.Add(new SheetFieldChange(FieldKeys.Type, "Type", _m.TypeDisplayName(e), r.TypeText + (r.TypePending is { } p && !p.StartsWith("row:") ? " (new type)" : "")));
        }
        if (r.Multiplicity is { Length: > 0 } && Metamodel.IsTypedFeature(e.Kind)) Cmp(FieldKeys.Multiplicity, "Multiplicity", e.Multiplicity, r.Multiplicity);
        if (r.OwnValue != null) Cmp(FieldKeys.DefaultValue, "Value", e.Get(FieldKeys.DefaultValue), r.OwnValue);
        if (r.OwnUnit != null) Cmp(ExtFieldKeys.Unit, "Unit", e.Get(ExtFieldKeys.Unit), r.OwnUnit);
        foreach (var (prop, (value, unit)) in r.Props)
        {
            var p = OwnedValueProperty(e, prop);
            var oldText = p == null ? "" : JoinValue(p.Get(FieldKeys.DefaultValue), p.Get(ExtFieldKeys.Unit));
            var newText = JoinValue(value ?? p?.Get(FieldKeys.DefaultValue), DesiredUnit(p, value, unit));
            if (oldText != newText && !(p == null && newText.Length == 0)) list.Add(new SheetFieldChange("prop:" + prop, prop, oldText, newText));
        }
        foreach (var rel in r.Rels)
        {
            var current = ModelValues.Related(_m, e, rel.Kind, rel.Incoming ? RelDirection.In : RelDirection.Out).Distinct().ToList();
            var want = rel.Targets;
            var same = want.All(t => t.Element != null && current.Contains(t.Element)) && current.All(c => want.Any(t => ReferenceEquals(t.Element, c)));
            if (!same)
                list.Add(new SheetFieldChange("rel:" + rel.Column.Column, rel.Column.Column, string.Join("; ", current.Select(c => Display(c))), string.Join("; ", want.Select(Label))));
        }
        if (r.PartOfMapped)
        {
            var current = SheetExport.PartUsages(Parts, e).ToList();
            var same = r.PartOf.Count == current.Count && r.PartOf.All(p => p.Parent.Element != null && current.Any(c => ReferenceEquals(c.Parent, p.Parent.Element) && c.Property.Multiplicity == p.Multiplicity));
            if (!same)
                list.Add(new SheetFieldChange("partOf", "Part of", string.Join("; ", current.Select(c => PartLabel(Display(c.Parent), c.Property.Multiplicity))),
                    string.Join("; ", r.PartOf.Select(p => PartLabel(Label(p.Parent), p.Multiplicity)))));
        }
        return list;
    }

    /// <summary>A value property's unit after import: the given unit, cleared by a value without one, else unchanged.</summary>
    private static string? DesiredUnit(Element? p, string? value, string? unit) => unit ?? (value != null ? "" : p?.Get(ExtFieldKeys.Unit));

    private static IEnumerable<string> Ordered(IEnumerable<string> current, HashSet<string> want) =>
        current.Where(want.Contains).Concat(want.Where(w => !current.Contains(w, StringComparer.OrdinalIgnoreCase)));

    /// <summary>Where an existing element should live, or null to leave it.</summary>
    private OwnerRef? DesiredOwner(RowState r)
    {
        if (r.Owner != null) return r.Owner;
        var e = r.Existing!;
        // A top-level row of a containment hierarchy whose element is still nested in one of its own kind moves up.
        var hierarchy = _map.Hierarchy == SheetHierarchy.Containment && (_map.IndentHierarchy || _map.Column(SheetRole.Level) is { } l && _col.ContainsKey(l)
                         || _map.Column(SheetRole.Parent) is { } pc && _col.ContainsKey(pc));
        if (hierarchy && r.ParentRow == null && r.ParentElement == null && e.Owner is { } o && o.Kind == e.Kind)
        {
            var up = e.AncestorsAndSelf().Skip(1).FirstOrDefault(a => a.Kind != e.Kind && Metamodel.CanOwn(a.Kind, e.Kind));
            if (up != null) return new OwnerRef(up, null, []);
        }
        return null;
    }

    internal static Element? OwnedValueProperty(Element e, string name) =>
        e.OwnedElements.FirstOrDefault(c => c.Kind == ElementKind.Property && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ apply

    internal static SheetApplyResult Apply(ModelRepository m, SheetImportPlan plan)
    {
        var accepted = plan.Rows.Where(r => r.Accept).ToList();
        var warnings = new List<string>();
        if (accepted.Count == 0) return new SheetApplyResult(0, 0, 0, 0, warnings);
        int added = 0, changed = 0, removed = 0, types = 0;
        var packages = new Dictionary<string, Element>(StringComparer.Ordinal);
        var newTypes = new Dictionary<string, Element>(StringComparer.OrdinalIgnoreCase);
        var acceptedStates = accepted.Where(r => r.State != null).Select(r => r.State!).ToHashSet();
        var parts = SheetExport.PartIndex(m);

        Element TypeHome()
        {
            var home = plan.Scope;
            while (!Metamodel.CanOwn(home.Kind, ElementKind.Block) && home.Owner != null) home = home.Owner;
            return home;
        }

        Element? Ensure(RowState s, HashSet<RowState> visiting)
        {
            if (s.Existing != null) return m.Contains(s.Existing) ? s.Existing : null;
            if (s.Created != null) return s.Created;
            if (!acceptedStates.Contains(s) || !visiting.Add(s)) return null;
            Element? owner;
            if (s.Owner is { } o)
            {
                if (o.Row != null) owner = Ensure(o.Row, visiting);
                else if (o.CreatePath.Count > 0) owner = EnsurePackages(m, o.Element!, o.CreatePath, packages);
                else owner = m.Contains(o.Element!) ? o.Element : null;
                if (owner == null) { warnings.Add($"Row {s.Line}: its owner was not imported, so '{s.DisplayName}' was skipped"); return null; }
            }
            else owner = plan.Scope;
            var name = s.Name is { Length: > 0 } n ? n : s.Key.Length > 0 && plan.Mapping.KeyMatch != SheetKeyMatch.ElementId ? SheetImport.SplitPath(s.Key).LastOrDefault() ?? s.Key : null;
            try { s.Created = m.Create(owner, s.Target.Kind, name); }
            catch (InvalidOperationException ex) { warnings.Add($"Row {s.Line}: {ex.Message}"); return null; }
            if (plan.Mapping.KeyMatch == SheetKeyMatch.ExternalId && s.Key.Length > 0 && plan.Mapping.KeyField.Length > 0)
                m.Set(s.Created, plan.Mapping.KeyField, s.Key);
            added++;
            return s.Created;
        }

        Element? Resolve(TargetRef t) => t.Element != null ? (m.Contains(t.Element) ? t.Element : null) : t.Row!.Created ?? t.Row.Existing;

        using (m.History.Transaction($"Import spreadsheet ({accepted.Count} rows)"))
        {
            // Missing types referenced by accepted rows.
            foreach (var s in acceptedStates.Where(s => s.TypePending is { } p && !p.StartsWith("row:")))
            {
                if (newTypes.ContainsKey(s.TypePending!)) continue;
                var kind = plan.TypesToCreate.TryGetValue(s.TypePending!, out var k) ? k : ElementKind.Block;
                newTypes[s.TypePending!] = m.Create(TypeHome(), kind, s.TypePending);
                types++;
            }
            foreach (var s in plan.Rows.Where(r => r.Accept && r.Kind == SheetChangeKind.Added).Select(r => r.State!)) Ensure(s, []);

            foreach (var row in accepted.Where(r => r.Kind != SheetChangeKind.Removed))
            {
                var s = row.State!;
                var e = s.Created ?? s.Existing;
                if (e == null || !m.Contains(e)) continue;
                if (row.Kind == SheetChangeKind.Changed)
                {
                    if (s.Name is { Length: > 0 } nm && e.Name != nm) m.Set(e, FieldKeys.Name, nm);
                    if (row.Fields.Any(f => f.Key == "owner"))
                    {
                        var o = s.Owner ?? DefaultUp(e);
                        Element? target = o == null ? null : o.Row != null ? Ensure(o.Row, []) : o.CreatePath.Count > 0 ? EnsurePackages(m, o.Element!, o.CreatePath, packages) : o.Element;
                        if (target != null && !ReferenceEquals(target, e.Owner))
                        {
                            if (ReferenceEquals(target, e) || e.IsAncestorOf(target)) warnings.Add($"Row {s.Line}: cannot move '{e.DisplayName}' into itself");
                            else
                            {
                                try { m.Move(e, target); }
                                catch (InvalidOperationException ex) { warnings.Add($"Row {s.Line}: {ex.Message}"); }
                            }
                        }
                    }
                    changed++;
                }
                foreach (var (key, (_, value)) in s.Fields) if (e.Get(key) != value) m.Set(e, key, value);
                if (s.Stereotypes != null)
                {
                    var want = row.Kind == SheetChangeKind.Added || s.StereotypesExact ? Ordered(e.Stereotypes, s.Stereotypes) : Ordered(e.Stereotypes, e.Stereotypes.Union(s.Stereotypes, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase));
                    var text = string.Join(",", want);
                    if (e.Get(FieldKeys.Stereotypes) != text) m.Set(e, FieldKeys.Stereotypes, text);
                }
                if (s.TypeText != null)
                {
                    string? typeRef = s.TypeLiteral;
                    if (s.TypePending is { } p)
                    {
                        if (p.StartsWith("row:")) typeRef = plan.Rows.Select(x => x.State).FirstOrDefault(x => x != null && "row:" + x.Line == p) is { } tr ? (tr.Created ?? tr.Existing)?.Id : null;
                        else typeRef = newTypes.TryGetValue(p, out var t) ? t.Id : null;
                    }
                    if (typeRef != null && e.TypeRef != typeRef) m.Set(e, FieldKeys.Type, typeRef);
                }
                if (s.Multiplicity is { Length: > 0 } && Metamodel.IsTypedFeature(e.Kind) && e.Multiplicity != s.Multiplicity) m.Set(e, FieldKeys.Multiplicity, s.Multiplicity);
                if (s.OwnValue != null && e.Get(FieldKeys.DefaultValue) != s.OwnValue) m.Set(e, FieldKeys.DefaultValue, s.OwnValue);
                if (s.OwnUnit != null && e.Get(ExtFieldKeys.Unit) != s.OwnUnit) m.Set(e, ExtFieldKeys.Unit, s.OwnUnit);
                foreach (var (prop, (value, unit)) in s.Props)
                {
                    var p = OwnedValueProperty(e, prop);
                    if (p == null)
                    {
                        if (string.IsNullOrEmpty(value) && string.IsNullOrEmpty(unit)) continue;
                        if (!Metamodel.CanOwn(e.Kind, ElementKind.Property)) { warnings.Add($"Row {s.Line}: a {Metamodel.DisplayName(e.Kind)} cannot own the value property '{prop}'"); continue; }
                        p = m.Create(e, ElementKind.Property, prop);
                        if (ModelValues.TryNumber(value ?? "", out _)) m.Set(p, FieldKeys.Type, "Real");
                    }
                    if (value != null && p.Get(FieldKeys.DefaultValue) != value) m.Set(p, FieldKeys.DefaultValue, value);
                    var u = DesiredUnit(p, value, unit) ?? "";
                    if (p.Get(ExtFieldKeys.Unit) != u) m.Set(p, ExtFieldKeys.Unit, u);
                }
            }

            // Relationships and part-of once every new element exists.
            foreach (var row in accepted.Where(r => r.Kind != SheetChangeKind.Removed))
            {
                var s = row.State!;
                var e = s.Created ?? s.Existing;
                if (e == null || !m.Contains(e)) continue;
                foreach (var rel in s.Rels)
                {
                    if (row.Kind == SheetChangeKind.Changed && !row.Fields.Any(f => f.Key == "rel:" + rel.Column.Column)) continue;
                    var dir = rel.Incoming ? RelDirection.In : RelDirection.Out;
                    var want = rel.Targets.Select(Resolve).ToList();
                    foreach (var miss in rel.Targets.Where((t, i) => want[i] == null)) warnings.Add($"Row {s.Line}: {rel.Column.Column} target '{miss.Row?.DisplayName}' was not imported");
                    var wanted = want.Where(x => x != null).Select(x => x!).ToList();
                    foreach (var r in m.RelationshipsOf(e).Where(r => r.Kind == rel.Kind && (rel.Incoming ? ReferenceEquals(r.Target, e) : ReferenceEquals(r.Source, e))).ToList())
                        if (!wanted.Contains(rel.Incoming ? r.Source : r.Target)) m.Delete(r);
                    var current = ModelValues.Related(m, e, rel.Kind, dir).ToHashSet();
                    foreach (var t in wanted.Where(t => !current.Contains(t)))
                    {
                        try { if (rel.Incoming) m.Connect(rel.Kind, t, e); else m.Connect(rel.Kind, e, t); }
                        catch (InvalidOperationException ex) { warnings.Add($"Row {s.Line}: {ex.Message}"); }
                    }
                }
                if (s.PartOfMapped && (row.Kind == SheetChangeKind.Added || row.Fields.Any(f => f.Key == "partOf")))
                    SyncParts(m, parts, e, s.PartOf.Select(p => (Resolve(p.Parent), p.Multiplicity)).Where(p => p.Item1 != null).Select(p => (p.Item1!, p.Multiplicity)).ToList(), warnings, s.Line);
            }

            var deletions = accepted.Where(r => r.Kind == SheetChangeKind.Removed && r.Existing is { } x && m.Contains(x)).Select(r => r.Existing!).ToList();
            if (deletions.Count > 0) { m.Delete(deletions); removed = deletions.Count; }
        }
        return new SheetApplyResult(added, changed, removed, types, warnings);
    }

    private static OwnerRef? DefaultUp(Element e)
    {
        var up = e.AncestorsAndSelf().Skip(1).FirstOrDefault(a => a.Kind != e.Kind && Metamodel.CanOwn(a.Kind, e.Kind));
        return up == null ? null : new OwnerRef(up, null, []);
    }

    private static Element EnsurePackages(ModelRepository m, Element baseEl, IReadOnlyList<string> path, Dictionary<string, Element> cache)
    {
        var current = baseEl;
        var key = baseEl.Id;
        foreach (var seg in path)
        {
            key += "::" + seg;
            if (cache.TryGetValue(key, out var hit) && m.Contains(hit)) { current = hit; continue; }
            var next = current.OwnedElements.FirstOrDefault(c => c.Name == seg && c.Kind == ElementKind.Package) ?? m.Create(current, ElementKind.Package, seg);
            cache[key] = next;
            current = next;
        }
        return current;
    }

    /// <summary>Makes the element a part of exactly the given blocks: part properties (with a composition) are added, updated or removed.</summary>
    private static void SyncParts(ModelRepository m, ILookup<string, Element> index, Element part, List<(Element Parent, string Multiplicity)> parents, List<string> warnings, int line)
    {
        var usages = SheetExport.PartUsages(index, part).Where(u => m.Contains(u.Property)).ToList();
        foreach (var u in usages.Where(u => !parents.Any(p => ReferenceEquals(p.Parent, u.Parent))))
        {
            foreach (var c in m.Outgoing(u.Parent, ElementKind.Composition).Where(c => ReferenceEquals(c.Target, part)).ToList()) m.Delete(c);
            if (m.Contains(u.Property)) m.Delete(u.Property);
        }
        foreach (var (parent, mult) in parents)
        {
            if (ReferenceEquals(parent, part)) continue;
            var usage = usages.FirstOrDefault(u => ReferenceEquals(u.Parent, parent)).Property;
            if (usage == null)
            {
                if (!Metamodel.CanOwn(parent.Kind, ElementKind.Property)) { warnings.Add($"Row {line}: '{parent.DisplayName}' cannot own parts"); continue; }
                var name = PartName(parent, part.Name);
                usage = m.Create(parent, ElementKind.Property, name);
                m.Set(usage, FieldKeys.Type, part.Id);
                if (!m.Outgoing(parent, ElementKind.Composition).Any(c => ReferenceEquals(c.Target, part)))
                {
                    try { m.Connect(ElementKind.Composition, parent, part); }
                    catch (InvalidOperationException ex) { warnings.Add($"Row {line}: {ex.Message}"); }
                }
            }
            if (usage.Multiplicity != mult) m.Set(usage, FieldKeys.Multiplicity, mult);
        }
    }

    private static string PartName(Element parent, string typeName)
    {
        var baseName = typeName.Length == 0 ? "part" : char.ToLowerInvariant(typeName[0]) + typeName[1..].Replace(" ", "");
        var names = parent.OwnedElements.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        if (!names.Contains(baseName)) return baseName;
        for (var i = 2; ; i++) if (!names.Contains(baseName + i)) return baseName + i;
    }
}
