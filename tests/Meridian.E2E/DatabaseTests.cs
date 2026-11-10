using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>Database modelling end to end: import a DDL script, subject areas and filtering, DDL generation.</summary>
public sealed class DatabaseTests(ServerFixture f) : E2ETest(f)
{
    public const string Script = """
        CREATE SCHEMA sales;
        CREATE TABLE public.customer (
            id serial PRIMARY KEY,
            email varchar(320) NOT NULL UNIQUE,
            name text
        );
        CREATE TABLE public.address (
            id serial PRIMARY KEY,
            customer_id integer NOT NULL REFERENCES public.customer(id) ON DELETE CASCADE,
            city text NOT NULL
        );
        CREATE TABLE sales.orders (
            id bigserial PRIMARY KEY,
            customer_id integer NOT NULL REFERENCES public.customer(id),
            placed_at timestamptz NOT NULL DEFAULT now(),
            total numeric(12,2)
        );
        CREATE TABLE sales.order_line (
            order_id bigint NOT NULL REFERENCES sales.orders(id) ON DELETE CASCADE,
            line_no smallint NOT NULL,
            sku text NOT NULL,
            quantity integer NOT NULL DEFAULT 1,
            PRIMARY KEY (order_id, line_no)
        );
        CREATE INDEX orders_customer_idx ON sales.orders (customer_id);
        CREATE VIEW sales.big_orders AS SELECT id, total FROM sales.orders WHERE total > 1000;
        COMMENT ON TABLE public.customer IS 'People who buy things';
        """;

    /// <summary>The shape of a table, by its exact name (column rows contain other tables' names).</summary>
    public static ILocator Table(IPage p, string name) =>
        p.Nodes().Filter(new() { Has = p.Locator("text", new() { HasTextRegex = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(name) + "$") }) });

    /// <summary>Project ▸ Database schema (DDL / .dacpac)…, paste, import; returns once the diagrams are open.</summary>
    public static async Task ImportScriptAsync(IPage p)
    {
        await p.ProjectMenuAsync("Database schema (DDL / .dacpac)…");
        var dlg = p.MudDialog();
        await dlg.Locator("[data-testid=db-import-text]").FillAsync(Script);
        await Expect(dlg.Locator("[data-testid=db-import-preview]")).ToContainTextAsync("4 tables");
        await Expect(dlg.Locator("[data-testid=db-import-preview]")).ToContainTextAsync("1 view");
        await dlg.Locator("[data-testid=db-import-name] input, input[data-testid=db-import-name]").First.FillAsync("shop");
        await dlg.Locator("[data-testid=db-import-ok]").ClickAsync();
        await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);
    }

    [E2EFact]
    public async Task Importing_a_ddl_script_opens_a_database_diagram_per_schema()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Blank project", "Db import " + Guid.NewGuid().ToString("N")[..6]);
        await Expect(p.Locator("[data-testid=ribbon-database]")).ToHaveCountAsync(0);   // no data model yet
        var before = await p.CountsAsync();

        await ImportScriptAsync(p);

        await Expect(p.Tabs().Filter(new() { HasTextString = "public – tables" })).ToHaveCountAsync(1);
        await Expect(p.Tabs().Filter(new() { HasTextString = "sales – tables" })).ToHaveCountAsync(1);
        await Expect(p.Locator(".tabstrip .tab.active")).ToContainTextAsync("public – tables");
        await Expect(p.Nodes()).ToHaveCountAsync(2);
        await Expect(Table(p, "customer")).ToBeVisibleAsync();
        await Expect(p.Locator(".diagram-host svg.main g.db-row")).Not.ToHaveCountAsync(0);   // column rows
        await Expect(p.Locator("[data-testid=ribbon-database]")).ToBeVisibleAsync();
        await p.Tabs().Filter(new() { HasTextString = "sales – tables" }).ClickAsync();
        await Expect(p.Nodes()).ToHaveCountAsync(3);   // orders, order_line and the view
        var after = await p.CountsAsync();
        Assert.Equal(before.Diagrams + 2, after.Diagrams);
        Assert.Equal(before.Relationships + 3, after.Relationships);   // three foreign keys

        // The whole import is one undo step.
        await p.Canvas().FocusAsync();
        await p.Keyboard.PressAsync("ControlOrMeta+z");
        await Ui.EventuallyAsync(async () => (await p.CountsAsync()).Diagrams == before.Diagrams, "undo removes the imported database");
        s.AssertNoErrors();
    }

    [E2EFact]
    public async Task Subject_areas_group_tables_filter_the_diagram_and_tree_and_ddl_is_generated()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Blank project", "Db groups " + Guid.NewGuid().ToString("N")[..6]);
        await ImportScriptAsync(p);
        await Expect(Table(p, "customer")).ToBeVisibleAsync();

        // Select both tables and add them to a new subject area.
        var (cx, cy, _) = await Table(p, "customer").CenterAsync();
        var (ax, ay, _) = await Table(p, "address").CenterAsync();
        await p.Mouse.ClickAsync(cx, cy);
        await p.Keyboard.DownAsync("Shift");
        await p.Mouse.ClickAsync(ax, ay);
        await p.Keyboard.UpAsync("Shift");
        await p.Locator("[data-testid=ribbon-add-to-group]").ClickAsync();
        var dlg = p.MudDialog();
        await dlg.Locator("[data-testid=db-new-group-name] input, input[data-testid=db-new-group-name]").First.FillAsync("CRM");
        await dlg.Locator("[data-testid=db-new-group-add]").ClickAsync();
        await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);
        await Expect(p.Locator(".diagram-host svg.main g.db-area")).ToHaveCountAsync(1);   // the area's frame
        await Expect(p.Locator(".diagram-host svg.main g.db-area")).ToContainTextAsync("CRM");

        // The tree filters by subject area.
        await p.Locator(".chips .chip[data-group=CRM]").ClickAsync();
        await Expect(p.Tree().GetByText("customer", new() { Exact = true })).ToHaveCountAsync(1);
        await Expect(p.Tree().GetByText("address", new() { Exact = true })).ToHaveCountAsync(1);
        await Expect(p.Tree().GetByText("orders", new() { Exact = true })).ToHaveCountAsync(0);
        await p.Locator(".chips .chip").Filter(new() { HasTextString = "All" }).First.ClickAsync();

        // On the sales diagram, filtering by the area hides the tables outside it.
        await p.Tabs().Filter(new() { HasTextString = "sales – tables" }).ClickAsync();
        await Expect(p.Nodes()).ToHaveCountAsync(3);
        await p.Locator("[data-testid=ribbon-groups]").ClickAsync();
        await p.Locator(".mud-popover-open .mud-menu-item[data-group=CRM]").ClickAsync();
        await Expect(p.Nodes()).ToHaveCountAsync(0);
        await p.Locator("[data-testid=ribbon-groups]").ClickAsync();
        await p.Locator(".mud-popover-open .mud-menu-item").Filter(new() { HasTextString = "Every table" }).ClickAsync();
        await Expect(p.Nodes()).ToHaveCountAsync(3);

        // Keys only.
        var full = await p.Locator(".diagram-host svg.main g.db-row").CountAsync();
        await p.Locator("[data-testid=ribbon-groups]").ClickAsync();
        await p.Locator(".mud-popover-open [data-testid=columns-keys]").ClickAsync();
        await Ui.EventuallyAsync(async () => await p.Locator(".diagram-host svg.main g.db-row").CountAsync() < full, "keys only hides the other columns");

        // Generate DDL for the database and download it.
        await p.Locator("[data-testid=ribbon-generate-ddl]").ClickAsync();
        dlg = p.MudDialog();
        var preview = dlg.Locator("[data-testid=ddl-preview]");
        await Expect(preview).ToContainTextAsync("CREATE TABLE public.customer");
        await Expect(preview).ToContainTextAsync("CREATE SCHEMA IF NOT EXISTS sales;");
        await Expect(preview).ToContainTextAsync("FOREIGN KEY");
        var download = await p.RunAndWaitForDownloadAsync(() => dlg.Locator("[data-testid=ddl-download]").ClickAsync());
        Assert.EndsWith(".sql", download.SuggestedFilename);
        await dlg.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
        await Expect(p.Locator(".mud-dialog")).ToHaveCountAsync(0);
        s.AssertNoErrors();
    }
}
