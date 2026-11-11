using Bunit;
using Meridian.Core.DataModeling;
using Meridian.Core.Model;
using Meridian.Data;
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

/// <summary>The migration dialog's rename confirmation, and the routine body editor in the inspector.</summary>
public sealed class DatabaseMigrationRenameTests : TestContext
{
    private const string Ddl = """
        CREATE TABLE public.customer (id serial PRIMARY KEY, email varchar(320) NOT NULL UNIQUE);
        CREATE TABLE public.orders (id bigint PRIMARY KEY, customer_id integer NOT NULL REFERENCES public.customer(id), total numeric(10,2), note text);
        CREATE FUNCTION public.order_count() RETURNS bigint LANGUAGE sql STABLE AS $$ SELECT count(*) FROM orders $$;
        """;

    private readonly Workspace _ws;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-migration-{Guid.NewGuid():N}.db");

    public DatabaseMigrationRenameTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        var db = new SqliteStore(_path);
        Services.AddSingleton<IMeridianStore>(db);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var integrations = new IntegrationService(db, new EphemeralDataProtectionProvider(), new FakeHttpClientFactory(new FakeHandler((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound))));
        Services.AddSingleton(new DatabaseConnectionService(integrations, db, new MeridianHostInfo(true, Path.GetTempPath(), null), config));
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ws = Services.GetRequiredService<Workspace>();
        _ws.UseLocalModel(new ModelRepository("Shop"));
        RenderComponent<MudPopoverProvider>();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }

    [Fact]
    public async Task Migration_Dialog_Offers_Renames_Against_A_Script_And_Scripts_The_Ticked_Ones()
    {
        var r = DbImport.Import(_ws.Model, _ws.Model.Root, DdlParser.Parse(Ddl, "postgresql", "shop"));
        // The model moves on: orders.total is renamed (the pasted script below is the database as it still is).
        _ws.Model.Set(_ws.Model.OfKind(ElementKind.Column).Single(c => c.Name == "total"), FieldKeys.Name, "grand_total");

        var provider = RenderComponent<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        await provider.InvokeAsync(() => dialogs.ShowAsync<MigrationScriptDialog>("test", new DialogParameters<MigrationScriptDialog> { { x => x.Database, r.Database } }));
        var from = provider.FindComponents<MudSelect<string>>().First(s => s.Instance.Label!.StartsWith("From", StringComparison.Ordinal));
        await provider.InvokeAsync(() => from.Instance.ValueChanged.InvokeAsync("script"));
        provider.Find("[data-testid=migration-script-input]").Input(Ddl);

        var suggestion = provider.Find("[data-testid=migration-rename]");
        Assert.Contains("column orders.total → grand_total", suggestion.TextContent);
        Assert.Contains("% match", suggestion.TextContent);
        var checkbox = suggestion.QuerySelector("input[type=checkbox]")!;
        Assert.False(checkbox.HasAttribute("checked"));   // nothing is assumed: the user confirms
        var before = provider.Find("[data-testid=migration-preview]").TextContent;
        Assert.Contains("DROP COLUMN total", before);
        Assert.Contains("ADD COLUMN grand_total", before);
        Assert.NotNull(provider.Find("[data-testid=migration-warnings]"));

        provider.Find("[data-testid=migration-rename] input[type=checkbox]").Change(true);
        var after = provider.Find("[data-testid=migration-preview]").TextContent;
        Assert.Contains("ALTER TABLE orders RENAME COLUMN total TO grand_total;", after);
        Assert.DoesNotContain("DROP COLUMN", after);
        Assert.Empty(provider.FindAll("[data-testid=migration-warnings]"));

        // Unticking brings the drop and add back.
        provider.Find("[data-testid=migration-rename] input[type=checkbox]").Change(false);
        Assert.Contains("DROP COLUMN total", provider.Find("[data-testid=migration-preview]").TextContent);
    }

    [Fact]
    public async Task No_Suggestions_From_An_Empty_Database_Or_When_Nothing_Was_Renamed()
    {
        var r = DbImport.Import(_ws.Model, _ws.Model.Root, DdlParser.Parse(Ddl, "postgresql", "shop"));
        var provider = RenderComponent<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        await provider.InvokeAsync(() => dialogs.ShowAsync<MigrationScriptDialog>("test", new DialogParameters<MigrationScriptDialog> { { x => x.Database, r.Database } }));
        Assert.Empty(provider.FindAll("[data-testid=migration-renames]"));
        Assert.Contains("CREATE TABLE", provider.Find("[data-testid=migration-preview]").TextContent);
        var from = provider.FindComponents<MudSelect<string>>().First(s => s.Instance.Label!.StartsWith("From", StringComparison.Ordinal));
        await provider.InvokeAsync(() => from.Instance.ValueChanged.InvokeAsync("script"));
        provider.Find("[data-testid=migration-script-input]").Input(Ddl);
        Assert.Empty(provider.FindAll("[data-testid=migration-renames]"));
        Assert.Contains("No differences", provider.Find("[data-testid=migration-preview]").TextContent);
    }

    [Fact]
    public void Inspector_Shows_A_Routine_Body_In_A_Monospace_Editor()
    {
        DbImport.Import(_ws.Model, _ws.Model.Root, DdlParser.Parse(Ddl, "postgresql", "shop"));
        var fn = _ws.Model.OfKind(ElementKind.Routine).Single();
        _ws.Select(fn);
        var cut = RenderComponent<DatabaseInspectorSection>(p => p.Add(x => x.E, fn));
        var body = cut.Find("[data-testid=db-body]");
        Assert.Equal("textarea", body.TagName.ToLowerInvariant());
        Assert.Equal("SELECT count(*) FROM orders", body.GetAttribute("value")!.Trim());
        Assert.Contains("font-family:var(--mono", body.GetAttribute("style"));
        Assert.Contains("Body (as written in the database)", cut.Markup);
        body.Change("SELECT count(*) FROM orders WHERE total > 0");
        Assert.Equal("SELECT count(*) FROM orders WHERE total > 0", fn.Get(DbFields.Definition));
        Assert.Equal("STABLE", fn.Get(DbFields.Options));
    }
}
