using Meridian.Core.DataModeling;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Serialization.Database;
using Meridian.Web.Components.Workbench.Database;
using Meridian.Web.Integrations.Databases;
using Meridian.Web.Services;
using Microsoft.JSInterop;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>
/// Database modelling commands: import (DDL, .dacpac, Mermaid erDiagram, live connections), refresh with a review,
/// subject areas, DDL and migration scripts, the data dictionary, and table editing on diagrams. The dialogs live in
/// Components/Workbench/Database; the model logic in Meridian.Core.DataModeling.
/// </summary>
public partial class Workbench
{
    /// <summary>Show the Database ribbon group: the project has data models, or a database diagram is open.</summary>
    public bool ShowDatabaseTools => Ws.Active?.Diagram.DiagramType == DiagramType.Database || DbModel.HasDataModel(Ws.Model);

    private static DialogOptions WideDialog => new() { MaxWidth = MaxWidth.Large, FullWidth = true, CloseOnEscapeKey = true, BackdropClick = false };
    private static DialogOptions MediumDialog => new() { MaxWidth = MaxWidth.Medium, FullWidth = true, CloseOnEscapeKey = true };

    /// <summary>The database, schema or package new data-model elements go into, from the selection or the active diagram.</summary>
    private Element DataContext()
    {
        var start = Ws.Selected is { } s && s is not Relationship ? s : Ws.Active?.Diagram.Owner ?? Ws.Model.Root;
        if (start is Diagram d) start = d.Owner ?? Ws.Model.Root;
        return start.AncestorsAndSelf().FirstOrDefault(a => a.Kind is ElementKind.Package or ElementKind.Model && !DbModel.IsGroup(a)) ?? Ws.Model.Root;
    }

    /// <summary>The database in context (selection, active diagram), or the only one in the model.</summary>
    private Element? DatabaseInContext()
    {
        var start = Ws.Selected is { } s && s is not Relationship ? s : Ws.Active?.Diagram.Owner;
        if (start != null && DbModel.DatabaseOf(start) is { } db) return db;
        var all = DbModel.Databases(Ws.Model).ToList();
        return all.Count == 1 ? all[0] : null;
    }

    /// <summary>Tables picked on the active diagram, else the selected table.</summary>
    public IReadOnlyList<Element> SelectedTables()
    {
        var onDiagram = Ws.Active?.SelectedNodes.Select(n => n.Element).Where(e => e.Kind == ElementKind.Table).Distinct().ToList() ?? [];
        if (onDiagram.Count > 0) return onDiagram;
        return Ws.Selected is { Kind: ElementKind.Table } t ? [t] : [];
    }

    // ------------------------------------------------------------------ import & refresh

    /// <summary>Import ▸ Database schema (DDL / .dacpac)…: preview, then a new database (or a refresh review of an existing one).</summary>
    public async Task ImportDatabaseSchema()
    {
        if (Ws.ReadOnly) { Ws.SetStatus("View-only: you can't import into this project"); return; }
        var target = DataContext();
        var dialog = await Dialogs.ShowAsync<DatabaseImportDialog>("Import database schema",
            new DialogParameters<DatabaseImportDialog> { { x => x.Target, target } }, WideDialog);
        if (await dialog.Result is not { Canceled: false, Data: DatabaseImportDialog.Answer a }) return;
        if (a.Refresh is { } existing) { await ReviewRefresh(existing, a.Catalog, a.Source); return; }
        ImportCatalog(a.Catalog, target, a.Source, a.Name);
    }

    /// <summary>Creates a database from a catalog (one undo step), opens its diagrams and logs warnings.</summary>
    public DbImportResult? ImportCatalog(DbCatalog catalog, Element target, string source, string? name = null)
    {
        try
        {
            var r = DbImport.Import(Ws.Model, target, catalog, new DbImportOptions { DatabaseName = name, Source = source });
            foreach (var d in r.Diagrams.Take(Workspace.AutoOpenLimit).Reverse()) Ws.Open(d, recordHistory: false);
            Ws.Select(r.Database, syncCanvas: false);
            Ws.Log($"Imported database {r.Database.Name} from {source}: {r.Summary}");
            foreach (var w in r.Warnings.Take(200)) Ws.Log("  warning: " + w);
            if (r.Warnings.Count > 0) OpenDock(3);
            Snackbar.Add($"Imported {r.Database.Name}: {r.Summary}", r.Warnings.Count > 0 ? Severity.Warning : Severity.Success);
            return r;
        }
        catch (InvalidOperationException ex)
        {
            Snackbar.Add($"Could not import the schema: {FriendlyError.Describe(ex)}. Nothing was changed.", Severity.Error);
            return null;
        }
    }

    /// <summary>Compares a database with a fresh catalog of its source, shows the review, applies the accepted changes as one undo step and highlights them.</summary>
    public async Task<DbRefreshResult?> ReviewRefresh(Element database, DbCatalog incoming, string source)
    {
        var plan = DbRefresh.Plan(Ws.Model, database, incoming);
        if (plan.IsEmpty)
        {
            Snackbar.Add($"{database.Name} is up to date with {source}", Severity.Success);
            return null;
        }
        // Highlight what would change (removed and changed elements) while the review is open.
        SetDatabaseHighlights(plan.Changes.Select(c => (plan.ElementOf(c), c.Kind)).Where(x => x.Item1 != null).Select(x => (x.Item1!.Id, x.Kind)));
        var dialog = await Dialogs.ShowAsync<DatabaseReviewDialog>($"Review changes from {source}",
            new DialogParameters<DatabaseReviewDialog> { { x => x.Plan, plan }, { x => x.Source, source } }, WideDialog);
        var result = await dialog.Result;
        if (result is not { Canceled: false, Data: IReadOnlyList<DbChange> chosen } || chosen.Count == 0) { Ws.SetCodeChanges(null); return null; }
        if (Ws.ReadOnly || !Ws.CanEdit(database)) { Ws.SetCodeChanges(null); Snackbar.Add("You can't change this database", Severity.Warning); return null; }
        var before = Meridian.Serialization.Reports.ReportEngine.Snapshot(Ws.Model);
        try
        {
            var r = DbRefresh.Apply(Ws.Model, plan, chosen);
            var kinds = new Dictionary<string, ChangeHighlight>(StringComparer.Ordinal);
            foreach (var id in r.AddedIds) kinds[id] = ChangeHighlight.Added;
            foreach (var id in r.ChangedIds) kinds.TryAdd(id, ChangeHighlight.Changed);
            foreach (var c in chosen.Where(c => c.Kind == DbChangeKind.Removed)) if (plan.ElementOf(c) is { } gone) kinds.TryAdd(gone.Id, ChangeHighlight.Removed);
            Ws.SetCodeChanges(new CodeChangeOverlay(kinds, before, "database changes"));
            Ws.SetHighlightCodeChanges(true);
            Ws.Log($"Refreshed {database.Name} from {source}: {r.Applied} change{(r.Applied == 1 ? "" : "s")} applied (one undo step)");
            foreach (var w in r.Warnings) Ws.Log("  warning: " + w);
            Snackbar.Add($"Applied {r.Applied} change{(r.Applied == 1 ? "" : "s")} to {database.Name}", Severity.Success);
            return r;
        }
        catch (InvalidOperationException ex)
        {
            Ws.SetCodeChanges(null);
            Snackbar.Add($"Could not refresh from the database: {FriendlyError.Describe(ex)}. Nothing was changed.", Severity.Error);
            return null;
        }
    }

    private void SetDatabaseHighlights(IEnumerable<(string Id, DbChangeKind Kind)> items)
    {
        var kinds = new Dictionary<string, ChangeHighlight>(StringComparer.Ordinal);
        foreach (var (id, k) in items) kinds[id] = k == DbChangeKind.Removed ? ChangeHighlight.Removed : kinds.ContainsKey(id) ? kinds[id] : ChangeHighlight.Changed;
        Ws.SetCodeChanges(new CodeChangeOverlay(kinds, null, "database changes"));
        Ws.SetHighlightCodeChanges(true);
    }

    /// <summary>Refresh from database…: the connection linked to the database (or a choice of connections, or a new script).</summary>
    public async Task RefreshFromDatabase(Element? database = null)
    {
        database ??= DatabaseInContext();
        if (database == null || Ws.Session == null) { Snackbar.Add("Select a database (imported from a script or a connection) first", Severity.Info); return; }
        var service = Services.GetRequiredService<DatabaseConnectionService>();
        var connections = await service.ListAsync(Ws.Session.ProjectId);
        var linked = connections.FirstOrDefault(c => DatabaseConnectionConfig.Parse(c.ConfigJson).DatabaseElementId == database.Id);
        if (linked == null)
        {
            // No live connection: re-import a script for this database.
            var dialog = await Dialogs.ShowAsync<DatabaseImportDialog>($"Refresh {database.Name}",
                new DialogParameters<DatabaseImportDialog> { { x => x.Target, database.Owner ?? Ws.Model.Root }, { x => x.RefreshOf, database } }, WideDialog);
            if (await dialog.Result is { Canceled: false, Data: DatabaseImportDialog.Answer a }) await ReviewRefresh(database, a.Catalog, a.Source);
            return;
        }
        await RefreshFromConnection(linked, database);
    }

    /// <summary>Reads a saved connection and imports it as a new database, or reviews the changes against the database it fills.</summary>
    public async Task RefreshFromConnection(IntegrationConnection connection, Element? database = null)
    {
        if (Ws.Session is not { } session) return;
        var service = Services.GetRequiredService<DatabaseConnectionService>();
        Ws.SetStatus($"Reading {connection.Name}…");
        DbCatalog catalog;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try { catalog = await service.ReadAsync(connection, cts.Token); }
        catch (Meridian.Data.Databases.DatabaseImportException ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
            Ws.SetStatus(ex.Message);
            return;
        }
        var cfg = DatabaseConnectionConfig.Parse(connection.ConfigJson);
        database ??= cfg.DatabaseElementId is { } id ? Ws.Model.Find(id) : null;
        if (database != null && DbModel.IsDatabase(database))
        {
            await ReviewRefresh(database, catalog, connection.Name);
            return;
        }
        if (Ws.ReadOnly) return;
        if (ImportCatalog(catalog, DataContext(), $"connection:{connection.Name}", connection.Name) is { } r)
            await service.LinkAsync(session.ProjectId, connection, r.Database.Id);
    }

    /// <summary>Connect to a database…: Integrations ▸ Databases.</summary>
    public Task ConnectToDatabase() =>
        Dialogs.ShowAsync<IntegrationsDialog>("Integrations",
            new DialogParameters<IntegrationsDialog> { { x => x.InitialTab, IntegrationsDialog.DatabasesTab } },
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });

    // ------------------------------------------------------------------ editing

    /// <summary>New table in the schema in context; shown on the active database diagram.</summary>
    public void NewTable(string? subtype = null)
    {
        if (Ws.ReadOnly) return;
        var owner = DataContext();
        if (DbModel.IsDatabase(owner) && owner.OwnedElements.FirstOrDefault(DbModel.IsSchema) is { } firstSchema) owner = firstSchema;
        try
        {
            Element t;
            using (Ws.Model.History.Transaction("New table"))
            {
                t = DbModel.CreateTable(Ws.Model, owner, subtype: subtype);
                if (Ws.Active is { Diagram.DiagramType: DiagramType.Database } c)
                {
                    c.Show(t);
                    if (c.Diagram.Get(DbFields.ShowColumns) is { Length: > 0 } mode && c.Diagram.NodeFor(t) is { } n) Ws.Model.SetNodeStyle(c.Diagram, n, DbFields.ShowColumns, mode);
                    if (c.Diagram.Get(DbFields.GroupFilter) is { Length: > 0 } g && Ws.Model.Find(g) is { } area) DbModel.AddToGroup(Ws.Model, [t], area);
                }
            }
            Ws.Select(t);
        }
        catch (InvalidOperationException ex) { Ws.SetStatus(ex.Message); }
    }

    public void AddColumn(Element table)
    {
        if (Ws.ReadOnly || table.Kind != ElementKind.Table) return;
        var c = DbModel.AddColumn(Ws.Model, table);
        Ws.Select(c);
    }

    /// <summary>Add to subject area…: the selected tables join a chosen (or new) area.</summary>
    public async Task AddSelectionToGroup()
    {
        var tables = SelectedTables();
        if (tables.Count == 0) { Snackbar.Add("Select tables on a database diagram (or in the model explorer) first", Severity.Info); return; }
        var dialog = await Dialogs.ShowAsync<AddToGroupDialog>("Add to subject area",
            new DialogParameters<AddToGroupDialog> { { x => x.Tables, tables }, { x => x.Home, DatabaseInContext() ?? DataContext() } }, MediumDialog);
        if (await dialog.Result is { Canceled: false, Data: Element group })
            Ws.SetStatus($"Added {tables.Count} table{(tables.Count == 1 ? "" : "s")} to {group.Name}");
    }

    public Task ManageGroups() =>
        Dialogs.ShowAsync<DatabaseGroupsDialog>("Subject areas", new DialogParameters<DatabaseGroupsDialog> { { x => x.Home, DatabaseInContext() ?? DataContext() } }, MediumDialog);

    public void SetShowColumns(string mode)
    {
        if (Ws.Active is not { Diagram.DiagramType: DiagramType.Database } c || Ws.IsReadOnly(c.Diagram)) return;
        DbModel.SetShowColumns(Ws.Model, c.Diagram, mode);
    }

    public void FilterActiveDiagram(Element? group)
    {
        if (Ws.Active is not { Diagram.DiagramType: DiagramType.Database } c) return;
        DbModel.FilterDiagram(Ws.Model, c.Diagram, group);
        Ws.SetStatus(group == null ? "Showing every table" : $"Showing the tables of {group.Name}");
    }

    // ------------------------------------------------------------------ generating

    public Task GenerateDdl(Element? scope = null)
    {
        scope ??= SelectedTables().Count > 1 ? null : DatabaseInContext() ?? DataContext();
        return Dialogs.ShowAsync<GenerateDdlDialog>("Generate DDL", new DialogParameters<GenerateDdlDialog>
        {
            { x => x.Scope, scope }, { x => x.Selection, SelectedTables() },
        }, WideDialog);
    }

    public Task MigrationScript(Element? database = null)
    {
        database ??= DatabaseInContext();
        return Dialogs.ShowAsync<MigrationScriptDialog>("Migration script", new DialogParameters<MigrationScriptDialog> { { x => x.Database, database } }, WideDialog);
    }

    public async Task ExportDataDictionary(bool xlsx, Element? scope = null)
    {
        scope ??= DatabaseInContext() ?? Ws.Model.Root;
        var name = $"{BaseName}-data-dictionary";
        if (xlsx) await Download(name + ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", DataDictionary.Xlsx(Ws.Model, scope));
        else await DownloadText(name + ".csv", "text/csv", DataDictionary.Csv(Ws.Model, scope));
    }

    public async Task DataDictionaryReport() =>
        await Dialogs.ShowAsync<ReportDialog>("Generate document",
            new DialogParameters<ReportDialog> { { x => x.Preselect, Meridian.Serialization.Reports.ReportTemplates.DataDictionary().Name } },
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, BackdropClick = false });

    /// <summary>Diagram per schema and per subject area of a database, laid out automatically (one undo step).</summary>
    private void DiagramDatabase(Element database)
    {
        if (Ws.ReadOnly) return;
        var plan = AutoDiagrammer.Plan(Ws.Model, new AutoDiagramOptions { Scope = database, DerivePackageDependencies = false, MaxNodes = 200 });
        plan.Diagrams.RemoveAll(p => p.Type != DiagramType.Database);
        if (plan.IsEmpty) { Ws.SetStatus("Every table is on a database diagram already"); return; }
        ApplyAutoDiagram(plan, openTabs: true);
    }

    // ------------------------------------------------------------------ menus and ⌘K

    private IEnumerable<CommandPalette.Command> DatabasePaletteCommands()
    {
        CommandPalette.Command C(string title, string icon, Func<Task> a) => new(title, "Database", icon, a);
        yield return C("Import database schema (DDL / .dacpac / Mermaid)…", Icons.Material.Outlined.Storage, ImportDatabaseSchema);
        yield return C("Connect to a database…", Icons.Material.Outlined.Cable, ConnectToDatabase);
        if (!ShowDatabaseTools) yield break;
        yield return C("New table", Icons.Material.Outlined.TableChart, () => { NewTable(); return Task.CompletedTask; });
        yield return C("Add selection to subject area…", Icons.Material.Outlined.Workspaces, AddSelectionToGroup);
        yield return C("Subject areas…", Icons.Material.Outlined.Workspaces, ManageGroups);
        yield return C("Generate DDL…", Icons.Material.Outlined.Code, () => GenerateDdl());
        yield return C("Migration script…", Icons.Material.Outlined.CompareArrows, () => MigrationScript());
        yield return C("Refresh from database…", Icons.Material.Outlined.Sync, () => RefreshFromDatabase());
        yield return C("Data dictionary (CSV)", Icons.Material.Outlined.ListAlt, () => ExportDataDictionary(false));
        yield return C("Data dictionary (Excel)", Icons.Material.Outlined.GridOn, () => ExportDataDictionary(true));
        yield return C("Data dictionary document (PDF / Word / HTML)…", Icons.Material.Outlined.Description, DataDictionaryReport);
        if (Ws.Active is { Diagram.DiagramType: DiagramType.Database })
        {
            yield return C("Show all columns", Icons.Material.Outlined.ViewList, () => { SetShowColumns("all"); return Task.CompletedTask; });
            yield return C("Show key columns only", Icons.Material.Outlined.Key, () => { SetShowColumns("keys"); return Task.CompletedTask; });
            yield return C("Hide columns", Icons.Material.Outlined.ViewAgenda, () => { SetShowColumns("none"); return Task.CompletedTask; });
        }
    }

    private IEnumerable<MenuEntry> DatabaseTreeEntries(Element e)
    {
        var write = !Ws.ReadOnly;
        if (DbModel.IsDatabase(e))
        {
            yield return MenuEntry.Title("Database");
            if (write) yield return MenuEntry.Do("New schema", () => { var s = DbModel.CreateSchema(Ws.Model, e, "schema"); Ws.Select(s); });
            yield return MenuEntry.Async("Generate DDL…", () => GenerateDdl(e));
            yield return MenuEntry.Async("Migration script…", () => MigrationScript(e));
            if (write) yield return MenuEntry.Async("Refresh from database…", () => RefreshFromDatabase(e));
            if (write) yield return MenuEntry.Do("Diagram each schema and subject area", () => DiagramDatabase(e));
            yield return MenuEntry.Async("Data dictionary (Excel)", () => ExportDataDictionary(true, e));
        }
        else if (DbModel.IsSchema(e))
        {
            yield return MenuEntry.Title("Schema");
            if (write) yield return MenuEntry.Do("New table", () => { Ws.Select(e); NewTable(); });
            yield return MenuEntry.Async("Generate DDL for this schema…", () => GenerateDdl(e));
        }
        else if (DbModel.IsGroup(e))
        {
            yield return MenuEntry.Title("Subject area");
            if (write) yield return MenuEntry.Do("Diagram this subject area", () => Ws.Open(DbDiagrams.BuildForGroup(Ws.Model, e)));
            yield return MenuEntry.Async("Generate DDL for this subject area…", () => GenerateDdl(e));
            if (Ws.Active is { Diagram.DiagramType: DiagramType.Database }) yield return MenuEntry.Do("Show only its tables on the diagram", () => FilterActiveDiagram(e));
            if (write) yield return MenuEntry.Async("Subject areas…", ManageGroups);
        }
        else if (e.Kind == ElementKind.Table && write)
        {
            yield return MenuEntry.Title("Table");
            yield return MenuEntry.Do("Add column", () => AddColumn(e));
            yield return MenuEntry.Async("Add to subject area…", () => { Ws.Select(e); return AddSelectionToGroup(); });
        }
    }

    private IEnumerable<MenuEntry> DatabaseCanvasEntries(DiagramController c, DiagramNode n)
    {
        if (n.Element.Kind != ElementKind.Table || Ws.ReadOnly) yield break;
        yield return MenuEntry.Title("Table");
        yield return MenuEntry.Do("Add column", () => AddColumn(n.Element));
        yield return MenuEntry.Async("Add to subject area…", AddSelectionToGroup);
        yield return MenuEntry.Async("Generate DDL…", () => GenerateDdl(n.Element));
        yield return MenuEntry.Sep();
    }

    private IEnumerable<MenuEntry> DatabaseMultiEntries(DiagramController c)
    {
        if (Ws.ReadOnly || !c.SelectedNodes.Any(n => n.Element.Kind == ElementKind.Table)) yield break;
        yield return MenuEntry.Async("Add to subject area…", AddSelectionToGroup);
        yield return MenuEntry.Async("Generate DDL for the selection…", () => GenerateDdl());
        yield return MenuEntry.Sep();
    }
}
