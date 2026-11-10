using System.Text;
using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>
/// Generic tables, model queries and saved views (bottom dock tab 8). Table mode shows any element kind as rows with
/// user-chosen columns, inline editing (undoable), sorting, per-column filters, add/delete row and CSV export.
/// Query mode is a search builder (or the compact text syntax) with live results; queries can be saved, opened as a
/// table or saved as validation rules.
/// </summary>
public partial class TablesPanel
{
    public enum Mode { Table, Query }

    /// <summary>A request from the shell to switch mode and optionally open a saved view. A new instance re-applies.</summary>
    public sealed record Request(Mode Mode, Element? View = null);

    [Parameter] public Request? Open { get; set; }

    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IDialogService Dialogs { get; set; } = default!;

    internal const int MaxRows = 1000;

    private Mode _mode = Mode.Table;
    private Request? _applied;

    // table state
    private TableDefinition _def = new() { Query = "kind:Block", Columns = TableDefinition.DefaultColumns(ElementKind.Block) };
    private ModelQuery _rowQuery = new(ElementKind.Block);
    private string _rowWhere = "";
    private Element? _view;

    // query state
    private bool _textMode;
    private ModelQuery _q = new(ElementKind.Block);
    private List<QueryRow> _rows = [new() { IsRelation = true, Negate = true, RelKind = ElementKind.Satisfy }];
    private string _queryText = "";
    private string? _queryError;
    private ModelQuery _parsed = new();
    private Element? _queryView;

    private Element? Current => _mode == Mode.Table ? _view : _queryView;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        RebuildQuery();
    }

    protected override void OnParametersSet()
    {
        if (Open == null || ReferenceEquals(Open, _applied)) return;
        _applied = Open;
        _mode = Open.Mode;
        if (Open.View != null) Load(Open.View);
    }

    protected override void OnWorkspaceChanged()
    {
        // Undo, a collaborator or the tree may have removed the view we are editing.
        if (_view != null && !Ws.Model.Contains(_view)) _view = null;
        if (_queryView != null && !Ws.Model.Contains(_queryView)) _queryView = null;
    }

    // ================================================================ saved views

    public void Load(Element view)
    {
        if (!Ws.Model.Contains(view)) return;
        if (view.Kind == ElementKind.TableView)
        {
            _mode = Mode.Table;
            _view = view;
            SetDefinition(TableDefinition.FromElement(view));
        }
        else if (view.Kind is ElementKind.QueryView or ElementKind.ValidationRule)
        {
            _mode = Mode.Query;
            _queryView = view;
            LoadQueryText(view.Get(ViewFields.Query));
        }
        Ws.SetStatus($"Opened {Metamodel.DisplayName(view.Kind).ToLowerInvariant()} '{view.DisplayName}'");
    }

    private void NewView()
    {
        if (_mode == Mode.Table)
        {
            _view = null;
            SetDefinition(new TableDefinition { Query = "kind:Block", Columns = TableDefinition.DefaultColumns(ElementKind.Block) });
        }
        else
        {
            _queryView = null;
            LoadQueryText("kind:Block where not has:satisfy");
        }
    }

    private async Task SaveTable()
    {
        if (_view != null) { Save(() => SavedViews.SaveTable(Ws.Model, _def, _view.Name, _view)); return; }
        await SaveTableAs();
    }

    private async Task SaveTableAs()
    {
        var suggestion = RowKind is { } k ? $"{Metamodel.DisplayName(k)} table" : "Table";
        var name = await Prompt("Save table view", "Name", _view != null ? _view.Name + " copy" : suggestion);
        if (string.IsNullOrWhiteSpace(name)) return;
        var saved = Save(() => SavedViews.SaveTable(Ws.Model, _def, name, owner: SavedOwner(_view)));
        if (saved != null) _view = saved;
    }

    private async Task SaveQuery()
    {
        if (_queryError != null) return;
        if (_queryView?.Kind == ElementKind.QueryView) { Save(() => SavedViews.SaveQuery(Ws.Model, _queryText, _queryView.Name, _queryView)); return; }
        var name = await Prompt("Save query", "Name", _queryView != null ? _queryView.Name + " query" : "Query");
        if (string.IsNullOrWhiteSpace(name)) return;
        var saved = Save(() => SavedViews.SaveQuery(Ws.Model, _queryText, name, owner: SavedOwner(_queryView)));
        if (saved != null) _queryView = saved;
    }

    private async Task SaveRule()
    {
        if (_queryError != null) return;
        var existing = _queryView?.Kind == ElementKind.ValidationRule ? _queryView : null;
        var parameters = new DialogParameters<RuleDialog>
        {
            { x => x.Name, existing?.Name ?? (_queryView != null ? _queryView.Name : "") },
            { x => x.Severity, existing?.Get(ViewFields.Severity) is { Length: > 0 } s ? s : "warning" },
            { x => x.Message, existing?.Get(ViewFields.Message) is { Length: > 0 } msg ? msg : "{name} matches this rule" },
            { x => x.Query, _queryText },
            { x => x.Existing, existing != null },
        };
        var dialog = await Dialogs.ShowAsync<RuleDialog>("Validation rule", parameters);
        if (await dialog.Result is not { Canceled: false, Data: RuleDialog.Result r }) return;
        var saved = Save(() => SavedViews.SaveRule(Ws.Model, _queryText, r.Name, r.Severity, r.Message, existing, SavedOwner(_queryView)));
        if (saved != null)
        {
            _queryView = saved;
            var hits = _parsed.Run(Ws.Model).Count;
            Ws.SetStatus($"Validation rule '{saved.Name}' saved — {hits} current {(hits == 1 ? "match" : "matches")} reported in the Validation panel");
        }
    }

    /// <summary>New views go next to the view they were derived from, else into the "Views" package.</summary>
    private static Element? SavedOwner(Element? from) => from?.Owner;

    private Element? Save(Func<Element> save)
    {
        try
        {
            var e = save();
            Ws.SetStatus($"Saved {Metamodel.DisplayName(e.Kind).ToLowerInvariant()} '{e.Name}' in {e.Owner?.DisplayName}");
            return e;
        }
        catch (Exception ex) when (ex is InvalidOperationException or QueryParseException)
        {
            Ws.SetStatus(ex.Message);
            return null;
        }
    }

    private async Task<string?> Prompt(string title, string label, string value)
    {
        var parameters = new DialogParameters<PromptDialog> { { x => x.Title, title }, { x => x.Label, label }, { x => x.Value, value } };
        var dialog = await Dialogs.ShowAsync<PromptDialog>(title, parameters);
        return await dialog.Result is { Canceled: false, Data: string s } ? s.Trim() : null;
    }

    // ================================================================ table definition

    private void SetDefinition(TableDefinition def)
    {
        _def = def;
        if (ModelQuery.TryParse(def.Query, out var q, out _)) _rowQuery = q;
        else _rowQuery = new ModelQuery();
        _rowWhere = _rowQuery.Where?.ToString() ?? "";
    }

    private ElementKind? RowKind => _rowQuery.Kinds.Count == 1 ? _rowQuery.Kinds[0] : null;
    private string RowKindValue => _rowQuery.Kinds.Count > 0 ? _rowQuery.Kinds[0].ToString() : "";

    private static IEnumerable<ElementKind> RowKinds =>
        Metamodel.ElementKinds.Where(k => k != ElementKind.Model).OrderBy(Metamodel.DisplayName)
            .Concat(Metamodel.RelationshipKinds.OrderBy(Metamodel.DisplayName));

    private void SetRowKind(string? value)
    {
        var before = RowKind;
        SetKinds(_rowQuery, value);
        // Fresh tables get columns that suit the kind; customised column sets are kept.
        var defaults = TableDefinition.DefaultColumns(before).Select(c => c.Key);
        if (_def.Columns.Select(c => c.Key).SequenceEqual(defaults)) _def.Columns = TableDefinition.DefaultColumns(RowKind);
        SyncRowQuery();
    }

    private void SetRowScope(string? value)
    {
        _rowQuery.Scope = NullIfEmpty(value);
        SyncRowQuery();
    }

    private void SetRowWhere(string? text)
    {
        _rowWhere = text ?? "";
        SyncRowQuery();
    }

    /// <summary>Recomposes the row query text; an invalid where clause is kept verbatim so the error shows above the table.</summary>
    private void SyncRowQuery()
    {
        var head = new ModelQuery { Scope = _rowQuery.Scope, Stereotype = _rowQuery.Stereotype };
        head.Kinds.AddRange(_rowQuery.Kinds);
        var where = _rowWhere.Trim();
        if (where.StartsWith("where ", StringComparison.OrdinalIgnoreCase)) where = where[6..].Trim();
        _def.Query = (head + (where.Length > 0 ? " where " + where : "")).Trim();
    }

    private static void SetKinds(ModelQuery q, string? value)
    {
        q.Kinds.Clear();
        if (!string.IsNullOrEmpty(value) && Enum.TryParse<ElementKind>(value, out var k)) q.Kinds.Add(k);
    }

    private IEnumerable<(string Group, string Key)> ColumnChoices()
    {
        IReadOnlyList<Element> rows;
        try { rows = ModelQuery.Parse(_def.Query).Run(Ws.Model); }
        catch (QueryParseException) { rows = []; }
        return ModelTables.SuggestColumns(Ws.Model, rows, _rowQuery.Kinds);
    }

    private void AddColumn(string key)
    {
        if (_def.Columns.Any(c => c.Key == key)) return;
        _def.Columns.Add(new TableViewColumn(key));
    }

    private async Task AddCustomColumn()
    {
        var key = await Prompt("Add column", "Key (e.g. mass, tag:priority, field:reqId, rel:out:Trace, allocatedTo)", "");
        if (!string.IsNullOrWhiteSpace(key)) AddColumn(ModelValues.Canonical(key));
        StateHasChanged();
    }

    private void RemoveColumn(int index)
    {
        if (_def.SortKey == _def.Columns[index].Key) _def.SortKey = null;
        _def.Columns.RemoveAt(index);
    }

    private void MoveColumn(int index)
    {
        if (index <= 0) return;
        (_def.Columns[index - 1], _def.Columns[index]) = (_def.Columns[index], _def.Columns[index - 1]);
    }

    private void Sort(TableViewColumn col)
    {
        if (_def.SortKey == col.Key)
        {
            if (_def.SortDescending) { _def.SortKey = null; _def.SortDescending = false; }   // asc → desc → unsorted
            else _def.SortDescending = true;
        }
        else { _def.SortKey = col.Key; _def.SortDescending = false; }
    }

    private static void SetFilter(TableViewColumn col, string? value) => col.Filter = string.IsNullOrWhiteSpace(value) ? null : value;

    // ================================================================ row operations

    private void Commit(Element e, string key, string? value)
    {
        try
        {
            if (ModelValues.Get(Ws.Model, e, key) == (value ?? "").Trim()) return;
            ModelValues.Set(Ws.Model, e, key, value);
            Ws.SetStatus($"{e.DisplayName}: {ModelValues.Label(key)} set");
        }
        catch (InvalidOperationException ex) { Ws.SetStatus(ex.Message); }
    }

    private IEnumerable<Element> OwnerCandidates()
    {
        if (RowKind is not { } kind) return [];
        var scope = _rowQuery.ResolveScope(Ws.Model);
        var pool = scope != null ? scope.Descendants().Prepend(scope) : Ws.Model.AllElements;
        var list = pool.Where(o => o is not Relationship && o.Kind != ElementKind.Diagram && Metamodel.CanOwn(o.Kind, kind))
                       .OrderBy(o => o.Kind is ElementKind.Model or ElementKind.Package ? 0 : 1).ThenBy(o => o.QualifiedName, StringComparer.OrdinalIgnoreCase)
                       .Take(40).ToList();
        if (Ws.Selected is { } sel && Metamodel.CanOwn(sel.Kind, kind) && !list.Contains(sel)) list.Insert(0, sel);
        return list;
    }

    private void AddRow(Element owner)
    {
        if (RowKind is not { } kind) return;
        try
        {
            if (Metamodel.IsRelationship(kind)) { Ws.SetStatus("Relationships are created on diagrams or in the traceability matrix"); return; }
            var e = Ws.Model.Create(owner, kind);
            Ws.Select(e);
            Ws.SetStatus($"Added {Metamodel.DisplayName(kind)} '{e.DisplayName}' to {owner.DisplayName}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { Ws.SetStatus(ex.Message); }
    }

    private void DeleteRow(Element e)
    {
        try
        {
            var name = e.DisplayName;
            Ws.Model.Delete(e);
            if (ReferenceEquals(Ws.Selected, e)) Ws.Select(null);
            Ws.SetStatus($"Deleted {name} — undo with ⌘Z");
        }
        catch (InvalidOperationException ex) { Ws.SetStatus(ex.Message); }
    }

    private void Go(Element e)
    {
        if (Shell != null) Shell.Navigate(e);
        else Ws.Select(e);
    }

    private async Task ExportCsv()
    {
        IReadOnlyList<ModelTableRow> rows;
        try { rows = ModelTables.Build(Ws.Model, _def); }
        catch (QueryParseException ex) { Ws.SetStatus(ex.Message); return; }
        var name = (_view?.Name ?? (RowKind is { } k ? Metamodel.DisplayName(k) + " table" : "table")) + ".csv";
        var csv = ModelTables.ToCsv(_def, rows);
        await JS.InvokeVoidAsync("meridian.download", name, "text/csv", Convert.ToBase64String(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray()));
        Ws.Log($"Exported {rows.Count} rows to {name}");
        Ws.SetStatus($"Exported {rows.Count} rows to {name}");
    }

    // ================================================================ scopes

    private IEnumerable<Element> Scopes() =>
        Ws.Model.AllElements.Where(e => e.Kind is ElementKind.Package or ElementKind.Profile)
            .OrderBy(e => e.QualifiedName, StringComparer.OrdinalIgnoreCase);

    private static string ScopeValue(Element e) => e.QualifiedName;

    private static string ScopeLabel(Element e)
    {
        var depth = e.AncestorsAndSelf().Count() - 2;
        return new string(' ', Math.Max(0, depth) * 3) + e.DisplayName;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ================================================================ query builder

    private void SetTextMode(bool text)
    {
        if (!text)
        {
            // Back to the builder only when the text fits a flat list of conditions.
            if (_queryError != null) { Ws.SetStatus("Fix the query before switching to the builder"); return; }
            var rows = QueryRow.Flatten(_parsed.Where);
            if (rows == null) { Ws.SetStatus("This query uses nesting the builder can't show — keep editing it as text"); return; }
            _q = Copy(_parsed);
            _rows = rows;
        }
        _textMode = text;
    }

    private void LoadQueryText(string text)
    {
        SetQueryText(text);
        var rows = _queryError == null ? QueryRow.Flatten(_parsed.Where) : null;
        if (rows != null && _parsed.Kinds.Count <= 1)
        {
            _q = Copy(_parsed);
            _rows = rows;
            _textMode = false;
        }
        else _textMode = true;
    }

    private void SetQueryText(string? text)
    {
        _queryText = text ?? "";
        if (ModelQuery.TryParse(_queryText, out var q, out var error)) { _parsed = q; _queryError = null; }
        else _queryError = error;
    }

    private void RebuildQuery()
    {
        var q = Copy(_q);
        q.Where = QueryRow.Combine(_rows);
        _parsed = q;
        _queryText = q.ToString();
        _queryError = null;
    }

    private void AddCondition(bool relation)
    {
        _rows.Add(relation
            ? new QueryRow { IsRelation = true, RelKind = ElementKind.Satisfy }
            : new QueryRow { Field = "name", Op = QueryOp.Contains });
        RebuildQuery();
    }

    private void OpenAsTable()
    {
        if (_queryError != null) return;
        _view = null;
        SetDefinition(new TableDefinition { Query = _queryText, Columns = TableDefinition.DefaultColumns(_parsed.Kinds.Count == 1 ? _parsed.Kinds[0] : null) });
        _mode = Mode.Table;
    }

    private IEnumerable<string> FieldSuggestions()
    {
        var rows = new ModelQuery { Scope = _q.Scope };
        rows.Kinds.AddRange(_q.Kinds);
        return ModelTables.SuggestColumns(Ws.Model, rows.Run(Ws.Model), _q.Kinds).Select(c => c.Key);
    }

    private static ModelQuery Copy(ModelQuery q)
    {
        var c = new ModelQuery { Scope = q.Scope, Stereotype = q.Stereotype, Where = q.Where };
        c.Kinds.AddRange(q.Kinds);
        return c;
    }

    private static string OpLabel(QueryOp op) => op switch
    {
        QueryOp.Eq => "=", QueryOp.Ne => "≠", QueryOp.Lt => "<", QueryOp.Gt => ">", QueryOp.Le => "≤", QueryOp.Ge => "≥",
        QueryOp.Contains => "contains", QueryOp.Empty => "is empty", _ => "is not empty",
    };
}
