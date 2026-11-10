using Bunit;
using Meridian.Core.DataModeling;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Data.Databases;
using Meridian.Serialization.Database;
using Meridian.Web.Components.Workbench.Database;
using Meridian.Web.Integrations;
using Meridian.Web.Integrations.Databases;
using Meridian.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>The database modelling dialogs and panels: import, refresh review, subject areas, DDL, connections.</summary>
public class DatabaseWebTests : TestContext
{
    private const string Ddl = """
        CREATE SCHEMA sales;
        CREATE TABLE public.customer (id serial PRIMARY KEY, email varchar(320) NOT NULL UNIQUE);
        CREATE TABLE sales.orders (id bigint PRIMARY KEY, customer_id integer NOT NULL REFERENCES public.customer(id), total numeric(10,2));
        CREATE VIEW sales.big_orders AS SELECT id, total FROM sales.orders WHERE total > 100;
        CREATE EXTENSION postgis;
        """;

    private readonly Workspace _ws;

    public DatabaseWebTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ws = Services.GetRequiredService<Workspace>();
        _ws.UseLocalModel(new ModelRepository("Shop"));
        RenderComponent<MudPopoverProvider>();
    }

    private DbImportResult ImportShop() =>
        DbImport.Import(_ws.Model, _ws.Model.Root, DdlParser.Parse(Ddl, "postgresql", "shop"), new DbImportOptions { Source = "ddl:shop.sql" });

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Reference)> Show<T>(DialogParameters<T> parameters) where T : Microsoft.AspNetCore.Components.ComponentBase
    {
        var provider = RenderComponent<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        IDialogReference? reference = null;
        await provider.InvokeAsync(async () => reference = await dialogs.ShowAsync<T>("test", parameters));
        return (provider, reference!);
    }

    [Fact]
    public async Task Import_Dialog_Previews_A_Pasted_Script_And_Answers_With_The_Catalog()
    {
        var (provider, reference) = await Show(new DialogParameters<DatabaseImportDialog> { { x => x.Target, _ws.Model.Root } });
        Assert.True(provider.Find("[data-testid=db-import-ok]").HasAttribute("disabled"));
        provider.Find("[data-testid=db-import-text]").Input(Ddl);
        var preview = provider.Find("[data-testid=db-import-preview]").TextContent;
        Assert.Contains("2 schemas", preview);
        Assert.Contains("2 tables", preview);
        Assert.Contains("1 view ", preview);
        Assert.Contains("1 foreign key ", preview);
        Assert.Contains("customer", preview);
        Assert.Contains("1 warning", provider.Find(".db-warnings").TextContent);
        provider.Find("[data-testid=db-import-ok]").Click();
        var answer = Assert.IsType<DatabaseImportDialog.Answer>((await reference.Result)!.Data);
        Assert.Null(answer.Refresh);
        Assert.Equal(3, answer.Catalog.AllTables.Count());
        Assert.Equal("pasted script", answer.Source);
    }

    [Fact]
    public async Task Import_Dialog_Offers_A_Review_When_The_Database_Exists()
    {
        var r = ImportShop();
        var (provider, reference) = await Show(new DialogParameters<DatabaseImportDialog> { { x => x.Target, _ws.Model.Root }, { x => x.RefreshOf, r.Database } });
        provider.Find("[data-testid=db-import-text]").Input(Ddl.Replace("total numeric(10,2)", "total numeric(12,2), note text"));
        Assert.Contains("Review changes", provider.Find("[data-testid=db-import-ok]").TextContent);
        provider.Find("[data-testid=db-import-ok]").Click();
        var answer = Assert.IsType<DatabaseImportDialog.Answer>((await reference.Result)!.Data);
        Assert.Same(r.Database, answer.Refresh);
        var plan = DbRefresh.Plan(_ws.Model, r.Database, answer.Catalog);
        Assert.Equal(2, plan.Changes.Count);   // total changed, note added
    }

    [Fact]
    public async Task Review_Dialog_Lists_Changes_And_Applies_The_Selection()
    {
        var r = ImportShop();
        var incoming = DdlParser.Parse(Ddl.Replace("total numeric(10,2)", "total numeric(12,2), note text").Replace("CREATE VIEW sales.big_orders AS SELECT id, total FROM sales.orders WHERE total > 100;", ""), "postgresql", "shop");
        var plan = DbRefresh.Plan(_ws.Model, r.Database, incoming);
        var (provider, reference) = await Show(new DialogParameters<DatabaseReviewDialog> { { x => x.Plan, plan }, { x => x.Source, "shop.sql" } });
        var rows = provider.FindAll(".db-review-row");
        Assert.Equal(plan.Changes.Count, rows.Count);
        Assert.Contains(rows, x => x.TextContent.Contains("note") && x.TextContent.Contains("Added"));
        Assert.Contains(rows, x => x.TextContent.Contains("big_orders") && x.TextContent.Contains("Removed"));
        Assert.Contains($"Apply {plan.Changes.Count} changes", provider.Find("[data-testid=db-review-apply]").TextContent);
        // Keep the view: untick its removal.
        provider.FindAll(".db-review-row").Single(x => x.TextContent.Contains("big_orders")).QuerySelector("input")!.Change(false);
        provider.Find("[data-testid=db-review-apply]").Click();
        var chosen = Assert.IsAssignableFrom<IReadOnlyList<DbChange>>((await reference.Result)!.Data);
        Assert.Equal(plan.Changes.Count - 1, chosen.Count);
        DbRefresh.Apply(_ws.Model, plan, chosen);
        Assert.Contains(_ws.Model.OfKind(ElementKind.Table), t => t.Name == "big_orders");
        Assert.NotNull(DbModel.Column(_ws.Model.OfKind(ElementKind.Table).Single(t => t.Name == "orders"), "note"));
    }

    [Fact]
    public async Task Groups_Dialog_Creates_Colours_Fills_And_Deletes_Subject_Areas()
    {
        var r = ImportShop();
        var (provider, _) = await Show(new DialogParameters<DatabaseGroupsDialog> { { x => x.Home, r.Database } });
        Assert.Contains("No subject areas yet", provider.Markup);
        provider.Find("[data-testid=db-groups-new]").Input("Ordering");
        provider.Find("[data-testid=db-groups-create]").Click();
        var group = Assert.Single(DbModel.Groups(_ws.Model));
        Assert.Equal("Ordering", group.Name);
        Assert.Same(r.Database, group.Owner);
        provider.FindAll("[data-testid=db-group-colors] button")[2].Click();
        Assert.Equal(DbFields.GroupColors[2], group.Get(FieldKeys.Color));
        var orders = _ws.Model.OfKind(ElementKind.Table).Single(t => t.Name == "orders");
        await provider.InvokeAsync(() => DbModel.AddToGroup(_ws.Model, [orders], group));
        provider.WaitForAssertion(() => Assert.Contains("orders", provider.Find("[data-testid=db-group-members]").TextContent));
        Assert.Contains("1", provider.Find("[data-group=Ordering]").TextContent);
        provider.FindAll("button").Single(b => b.TextContent.Contains("Delete area")).Click();
        Assert.Empty(DbModel.Groups(_ws.Model));
        Assert.True(_ws.Model.Contains(orders));
        Assert.Empty(DbModel.GroupIds(orders));
    }

    [Fact]
    public async Task Add_To_Group_Dialog_Creates_A_New_Area_With_The_Tables()
    {
        ImportShop();
        var tables = _ws.Model.OfKind(ElementKind.Table).Where(t => !DbModel.IsView(t)).ToList();
        var (provider, reference) = await Show(new DialogParameters<AddToGroupDialog> { { x => x.Tables, tables }, { x => x.Home, _ws.Model.Root } });
        provider.Find("[data-testid=db-new-group-name]").Input("Core");
        provider.Find("[data-testid=db-new-group-add]").Click();
        var group = Assert.IsAssignableFrom<Element>((await reference.Result)!.Data);
        Assert.Equal(tables.Count, DbModel.Members(_ws.Model, group).Count);
        Assert.Equal("Add to Core", _ws.Model.History.UndoDescription);
    }

    [Fact]
    public async Task Generate_Ddl_Dialog_Previews_The_Script_In_The_Database_Dialect()
    {
        var r = ImportShop();
        var (provider, _) = await Show(new DialogParameters<GenerateDdlDialog> { { x => x.Scope, r.Database } });
        var sql = provider.Find("[data-testid=ddl-preview]").TextContent;
        Assert.Contains("CREATE TABLE public.customer", sql);
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS sales;", sql);
        Assert.Contains("REFERENCES public.customer", sql);
        Assert.Contains("never executes DDL", sql);

        _ws.Model.Set(r.Database, DbFields.Dialect, "sqlserver");
        var (provider2, _) = await Show(new DialogParameters<GenerateDdlDialog> { { x => x.Scope, r.Database } });
        var tsql = provider2.FindAll("[data-testid=ddl-preview]").Last().TextContent;
        Assert.Contains("[public].customer", tsql);
        Assert.Contains("IDENTITY(1,1)", tsql);

        var orders = _ws.Model.OfKind(ElementKind.Table).Single(t => t.Name == "orders");
        var (provider3, _) = await Show(new DialogParameters<GenerateDdlDialog> { { x => x.Scope, null }, { x => x.Selection, new[] { orders } } });
        var selection = provider3.FindAll("[data-testid=ddl-preview]").Last().TextContent;
        Assert.Contains("orders", selection);
        Assert.DoesNotContain("CREATE TABLE [public].customer", selection);
    }

    [Fact]
    public void Inspector_Section_Adds_Columns_And_Toggles_The_Primary_Key()
    {
        var r = ImportShop();
        var orders = _ws.Model.OfKind(ElementKind.Table).Single(t => t.Name == "orders");
        _ws.Select(orders);
        var cut = RenderComponent<DatabaseInspectorSection>(p => p.Add(x => x.E, orders));
        Assert.Equal(3, cut.FindAll(".db-col-row").Count);
        cut.Find("[data-testid=db-add-column-name]").Input("placed_at");
        cut.Find("[data-testid=db-add-column]").Click();
        Assert.NotNull(DbModel.Column(orders, "placed_at"));
        var total = DbModel.Column(orders, "total")!;
        cut.FindAll(".db-col-row").Single(x => x.GetAttribute("data-column") == "total").QuerySelector("[data-testid=db-pk-toggle]")!.Click();
        Assert.True(DbModel.IsPk(total));
        Assert.False(DbModel.IsNullable(total));
        Assert.Contains("Subject areas", cut.Markup);
        _ = r;
    }
}

/// <summary>Database connections: who may add them, the outbound guard, encryption of the connection string.</summary>
public sealed class DatabaseConnectionServiceTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-dbconn-{Guid.NewGuid():N}.db");
    private SqliteStore _db = null!;
    private Guid _project;

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        var owner = await _db.CreateUserAsync("owner", "Owner", "pw-0wner!");
        _project = await _db.CreateProjectAsync("Shop", Meridian.Serialization.ProjectFile.Serialize(new ModelRepository("Shop")), owner.Id);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private DatabaseConnectionService Service(bool local, bool allowPrivate = false)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [OutboundGuard.AllowPrivateNetworksKey] = allowPrivate ? "true" : "false" }).Build();
        var integrations = new IntegrationService(_db, new EphemeralDataProtectionProvider(), new FakeHttpClientFactory(new FakeHandler((_, _) => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound))));
        return new DatabaseConnectionService(integrations, _db, new MeridianHostInfo(local, Path.GetTempPath(), null), config);
    }

    [Fact]
    public async Task Only_Administrators_Add_Connections_On_A_Team_Server_And_The_String_Is_Encrypted()
    {
        var svc = Service(local: false);
        var cfg = new DatabaseConnectionConfig { Provider = LiveProviders.PostgreSql };
        const string cs = "Host=db.example.test;Database=shop;Username=reader;Password=Sup3rSecret";
        var denied = await Assert.ThrowsAsync<DatabaseImportException>(() => svc.SaveAsync(_project, null, "prod", cfg, cs, isServerAdmin: false));
        Assert.Contains("administrators", denied.Message);
        var saved = await svc.SaveAsync(_project, null, "prod", cfg, cs, isServerAdmin: true);
        Assert.Equal(IntegrationKinds.Database, saved.Kind);
        Assert.NotNull(saved.ProtectedSecret);
        Assert.DoesNotContain("Sup3rSecret", saved.ProtectedSecret);
        Assert.DoesNotContain("Sup3rSecret", saved.ConfigJson);
        Assert.Equal("db.example.test:5432/shop", DatabaseConnectionConfig.Parse(saved.ConfigJson).Display);
        Assert.Single(await svc.ListAsync(_project));
        await Assert.ThrowsAsync<DatabaseImportException>(() => svc.DeleteAsync(_project, saved.Id, isServerAdmin: false));
    }

    [Fact]
    public async Task Team_Servers_Refuse_Loopback_Private_Hosts_Sockets_And_Sqlite_Files()
    {
        var svc = Service(local: false);
        var pg = new DatabaseConnectionConfig { Provider = LiveProviders.PostgreSql, TimeoutSeconds = 5 };
        var loop = await Assert.ThrowsAsync<DatabaseImportException>(() => svc.TestAsync(_project, null, pg, "Host=127.0.0.1;Database=x;Username=u;Password=p", true, CancellationToken.None));
        Assert.Contains("not allowed", loop.Message);
        await Assert.ThrowsAsync<DatabaseImportException>(() => svc.TestAsync(_project, null, pg, "Host=10.1.2.3;Database=x;Username=u;Password=p", true, CancellationToken.None));
        await Assert.ThrowsAsync<DatabaseImportException>(() => svc.TestAsync(_project, null, pg, "Host=169.254.169.254;Database=x;Username=u;Password=p", true, CancellationToken.None));
        var socket = await Assert.ThrowsAsync<DatabaseImportException>(() => svc.SaveAsync(_project, null, "s", pg, "Host=/var/run/postgresql;Database=x", true));
        Assert.Contains("socket", socket.Message);
        var sqlite = await Assert.ThrowsAsync<DatabaseImportException>(() => svc.SaveAsync(_project, null, "f", new DatabaseConnectionConfig { Provider = LiveProviders.Sqlite }, "Data Source=/etc/passwd", true));
        Assert.Contains("desktop", sqlite.Message);
        // An administrator who allows private networks still never reaches link-local metadata addresses.
        var open = Service(local: false, allowPrivate: true);
        await Assert.ThrowsAsync<DatabaseImportException>(() => open.TestAsync(_project, null, pg, "Host=169.254.169.254;Database=x;Username=u;Password=p", true, CancellationToken.None));
    }

    [Fact]
    public async Task Desktop_Mode_Reads_A_Local_Sqlite_File_Through_A_Saved_Connection()
    {
        var file = Path.Combine(Path.GetTempPath(), $"meridian-dbconn-src-{Guid.NewGuid():N}.db");
        try
        {
            await using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False"))
            {
                await c.OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "CREATE TABLE a (id INTEGER PRIMARY KEY, name TEXT NOT NULL); CREATE TABLE b (id INTEGER PRIMARY KEY, a_id INTEGER REFERENCES a(id));";
                await cmd.ExecuteNonQueryAsync();
            }
            var svc = Service(local: true);
            Assert.True(svc.CanManage(isServerAdmin: false));
            var saved = await svc.SaveAsync(_project, null, "", new DatabaseConnectionConfig { Provider = LiveProviders.Sqlite }, $"Data Source={file}", false);
            var catalog = await svc.ReadAsync(saved, CancellationToken.None);
            Assert.Equal(["a", "b"], catalog.AllTables.Select(t => t.Name).OrderBy(x => x).ToArray());
            Assert.Contains("Connected", await svc.TestAsync(_project, saved.Id, new DatabaseConnectionConfig { Provider = LiveProviders.Sqlite }, null, false, CancellationToken.None));
            await svc.LinkAsync(_project, saved, "_db1");
            Assert.Equal("_db1", DatabaseConnectionConfig.Parse((await svc.FindAsync(_project, saved.Id))!.ConfigJson).DatabaseElementId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(file); } catch (IOException) { }
        }
    }
}
