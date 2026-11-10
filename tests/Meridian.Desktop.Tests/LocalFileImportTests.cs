using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web;
using Meridian.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Desktop.Tests;

/// <summary>The desktop host's "open this file" endpoint on a real in-process local-mode server.</summary>
public sealed class LocalFileImportTests : IAsyncLifetime
{
    private const string Token = "TEST-LAUNCH-TOKEN";
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("meridian-import-test-");
    private WebApplication _app = default!;
    private HttpClient _http = default!;

    public async Task InitializeAsync()
    {
        _app = await MeridianServer.CreateAsync([], b =>
        {
            b.Configuration["Meridian:Mode"] = "local";
            b.Configuration["Meridian:DataPath"] = Path.Combine(_dir.FullName, "data");
            b.Configuration["Meridian:LaunchToken"] = Token;
            b.WebHost.UseUrls("http://127.0.0.1:0");
        }, new WebApplicationOptions { ApplicationName = "Meridian.Web", EnvironmentName = "Production", ContentRootPath = AppContext.BaseDirectory });
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { _dir.Delete(true); } catch (IOException) { }
    }

    private Task<HttpResponseMessage> Post(string? path, string? token = Token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, LocalFileImport.Route) { Content = JsonContent.Create(new LocalFileImport.Request(path)) };
        if (token != null) req.Headers.Add(LocalFileImport.TokenHeader, token);
        return _http.SendAsync(req);
    }

    [Fact]
    public async Task Imports_a_meridian_file_as_a_project()
    {
        var file = Path.Combine(_dir.FullName, "Vehicle Copy.meridian");
        await File.WriteAllTextAsync(file, ProjectFile.Serialize(SampleModels.Vehicle()));
        using var res = await Post(file);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<LocalFileImport.Response>(JsonSerializerOptions.Web);
        Assert.Equal("Vehicle Copy", body!.Name);
        var db = _app.Services.GetRequiredService<IMeridianStore>();
        Assert.NotNull(await db.LoadProjectAsync(body.ProjectId));
    }

    [Fact]
    public async Task Imports_sysml_text()
    {
        var file = Path.Combine(_dir.FullName, "Drone.sysml");
        await File.WriteAllTextAsync(file, "package Drone { part def Frame; part def Motor; }");
        using var res = await Post(file);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Imports_mermaid_and_markdown_files()
    {
        var mmd = Path.Combine(_dir.FullName, "Flow.mmd");
        await File.WriteAllTextAsync(mmd, "flowchart LR\n  A[Start] --> B{Ok?}\n  B -->|yes| C[Done]\n");
        using (var res = await Post(mmd))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<LocalFileImport.Response>(JsonSerializerOptions.Web);
            var model = ProjectFile.Deserialize((await _app.Services.GetRequiredService<IMeridianStore>().LoadProjectAsync(body!.ProjectId))!.Value.ModelJson);
            Assert.Single(model.Diagrams);
        }
        var md = Path.Combine(_dir.FullName, "Notes.md");
        await File.WriteAllTextAsync(md, "# Notes\n\n```mermaid\nsequenceDiagram\n  A->>B: hi\n```\n");
        using (var res = await Post(md)) Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var prose = Path.Combine(_dir.FullName, "Prose.md");
        await File.WriteAllTextAsync(prose, "# Just text\n");
        using (var res = await Post(prose)) Assert.Equal((HttpStatusCode)422, res.StatusCode);
    }

    [Fact]
    public async Task Requires_the_launch_token()
    {
        var file = Path.Combine(_dir.FullName, "a.meridian");
        await File.WriteAllTextAsync(file, ProjectFile.Serialize(SampleModels.Vehicle()));
        using (var none = await Post(file, token: null)) Assert.Equal(HttpStatusCode.Forbidden, none.StatusCode);
        using (var wrong = await Post(file, token: "nope")) Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        using (var get = await _http.GetAsync(LocalFileImport.Route)) Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
    }

    [Fact]
    public async Task Refuses_other_files()
    {
        var secret = Path.Combine(_dir.FullName, "secret.txt");
        await File.WriteAllTextAsync(secret, "do not read");
        using (var txt = await Post(secret)) Assert.Equal(HttpStatusCode.BadRequest, txt.StatusCode);
        using (var relative = await Post("model.meridian")) Assert.Equal(HttpStatusCode.BadRequest, relative.StatusCode);
        using (var missing = await Post(Path.Combine(_dir.FullName, "missing.meridian"))) Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var empty = await Post(null)) Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        var broken = Path.Combine(_dir.FullName, "broken.meridian");
        await File.WriteAllTextAsync(broken, "{ not json");
        using (var bad = await Post(broken)) Assert.Equal((HttpStatusCode)422, bad.StatusCode);
    }
}
