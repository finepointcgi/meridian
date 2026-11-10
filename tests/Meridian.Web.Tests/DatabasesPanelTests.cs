using Bunit;
using Meridian.Core.Model;
using Meridian.Data;
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

/// <summary>Integrations ▸ Databases: who sees the Add connection button, and the read-only advice.</summary>
public sealed class DatabasesPanelTests : TestContext, IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-dbpanel-{Guid.NewGuid():N}.db");
    private SqliteStore _db = null!;

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private IRenderedComponent<DatabasesPanel> Render(bool local, bool admin)
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        var host = new MeridianHostInfo(local, Path.GetTempPath(), null);
        Services.AddSingleton(host);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(new IntegrationService(_db, new EphemeralDataProtectionProvider(),
            new FakeHttpClientFactory(new FakeHandler((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)))));
        Services.AddSingleton(sp => new DatabaseConnectionService(sp.GetRequiredService<IntegrationService>(), _db, host, sp.GetRequiredService<IConfiguration>()));
        JSInterop.Mode = JSRuntimeMode.Loose;
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(new ModelRepository("M"));
        ws.IsServerAdmin = admin;
        RenderComponent<MudPopoverProvider>();
        return RenderComponent<DatabasesPanel>();
    }

    [Fact]
    public void Team_Server_Members_Cannot_Add_Connections()
    {
        var cut = Render(local: false, admin: false);
        Assert.Empty(cut.FindAll("[data-testid=db-connection-add]"));
        Assert.Contains("Only server administrators", cut.Markup);
        Assert.Contains("read-only account", cut.Markup);
    }

    [Fact]
    public void Administrators_Can_Add_Connections_But_Not_Sqlite_Files_On_A_Team_Server()
    {
        var cut = Render(local: false, admin: true);
        cut.Find("[data-testid=db-connection-add]").Click();
        Assert.NotEmpty(cut.FindAll("[data-testid=db-connection-string]"));
        Assert.DoesNotContain("SQLite (file)", cut.Markup);
    }

    [Fact]
    public void The_Desktop_App_Lets_Anyone_Add_Connections()
    {
        var cut = Render(local: true, admin: false);
        Assert.NotEmpty(cut.FindAll("[data-testid=db-connection-add]"));
        Assert.Contains("SQLite file", cut.Markup);
    }
}
