using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Admin;
using Meridian.Web.Integrations;
using Meridian.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Meridian.Web.Tests;

/// <summary>The MCP server at /api/mcp on a real Kestrel host: protocol, token-only access, per-caller redaction and the audit log.</summary>
public sealed class McpEndpointTests : IAsyncLifetime
{
    private const string SecretPackage = "Classified", SecretBlock = "Stealth Coating";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-mcp-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;
    private WebApplication _app = default!;
    private Uri _base = default!;
    private Guid _project;
    private UserAccount _owner = default!, _viewer = default!;
    private string _ownerToken = "", _viewerToken = "";

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        _owner = await _db.CreateUserAsync("olga", "Olga", "correct horse");
        _viewer = await _db.CreateUserAsync("victor", "Victor", "battery staple");
        var model = SampleModels.Vehicle();
        var secret = model.Create(model.Root, ElementKind.Package, SecretPackage);
        model.Create(secret, ElementKind.Block, SecretBlock);
        var engine = model.Create(model.Root, ElementKind.Block, "Linked Engine");
        model.Set(engine, FieldKeys.TagPrefix + "keel.part", "P-000123");   // the cross-app id convention (docs-site/docs/mcp.md)
        _project = await _db.CreateProjectAsync("Vehicle", ProjectFile.Serialize(model), _owner.Id);
        await _db.SetMemberAsync(_project, _viewer.Id, ProjectRole.Viewer);
        await _db.SetPackagePermissionAsync(_project, secret.Id, PrincipalKind.User, _viewer.Id, PackageAccess.None, _owner.Id);
        _ownerToken = await Token(_owner);
        _viewerToken = await Token(_viewer);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IMeridianStore>(_db);
        builder.Services.AddSingleton(new CollaborationHub(_db, NullLogger<CollaborationHub>.Instance));
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddSingleton(new AdminSettings(new SsoOptions(), new SecurityOptions(), new BackupOptions()));
        builder.Services.AddSingleton<IAuditLog>(new AuditLog(_db, NullLogger<AuditLog>.Instance));
        _app = builder.Build();
        McpEndpoints.Map(_app);
        await _app.StartAsync();
        _base = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/");
    }

    private async Task<string> Token(UserAccount user)
    {
        var token = ApiTokens.Generate();
        await _db.CreateApiTokenAsync(user.Id, "mcp", ApiTokens.Hash(token), ApiTokens.PrefixOf(token));
        return token;
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    private HttpClient Client(string? token)
    {
        var http = new HttpClient { BaseAddress = _base };
        if (token != null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static Task<HttpResponseMessage> Post(HttpClient http, string json) =>
        http.PostAsync("api/mcp", new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<JsonObject> Rpc(HttpClient http, string method, JsonObject? parameters = null)
    {
        var req = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 7, ["method"] = method };
        if (parameters != null) req["params"] = parameters;
        using var r = await Post(http, req.ToJsonString());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject();
    }

    private static JsonObject Call(string name, JsonObject arguments) => new() { ["name"] = name, ["arguments"] = arguments };

    private static string Text(JsonObject reply) => reply["result"]!["content"]![0]!["text"]!.ToString();

    [Fact]
    public async Task Tools_Read_The_Callers_Filtered_Model_And_Are_Audited()
    {
        using var viewer = Client(_viewerToken);
        var tools = (await Rpc(viewer, "tools/list"))["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.ToString()).ToList();
        Assert.Equal(["list_projects", "search_elements", "get_element", "list_diagrams", "get_traceability", "validate_model", "get_sysml_text", "plan_diagrams",
            "create_element", "update_fields", "create_relationship", "delete_element", "create_diagram_with_elements", "create_diagrams", "apply_sysml_text"], tools);

        var projects = Text(await Rpc(viewer, "tools/call", Call("list_projects", new JsonObject())));
        Assert.Contains(_project.ToString(), projects);

        var reqs = await Rpc(viewer, "tools/call", Call("search_elements", new JsonObject { ["project"] = "Vehicle", ["query"] = "kind:Requirement" }));
        Assert.False(reqs["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("\"total\":", Text(reqs));

        // The hidden package never reaches the viewer; the owner sees it.
        var hidden = Text(await Rpc(viewer, "tools/call", Call("search_elements", new JsonObject { ["project"] = _project.ToString(), ["query"] = "", ["text"] = "Stealth" })));
        Assert.DoesNotContain(SecretBlock, hidden);
        Assert.DoesNotContain(SecretBlock, Text(await Rpc(viewer, "tools/call", Call("get_sysml_text", new JsonObject { ["project"] = "Vehicle" }))));
        using var owner = Client(_ownerToken);
        Assert.Contains(SecretBlock, Text(await Rpc(owner, "tools/call", Call("search_elements", new JsonObject { ["project"] = "Vehicle", ["query"] = "", ["text"] = "Stealth" }))));

        // Bad input and unknown projects are tool errors; unknown tools (write tools included) are protocol errors.
        Assert.True((await Rpc(viewer, "tools/call", Call("search_elements", new JsonObject { ["project"] = "Nope", ["query"] = "" })))["result"]!["isError"]!.GetValue<bool>());
        Assert.True((await Rpc(viewer, "tools/call", Call("get_element", new JsonObject { ["project"] = "Vehicle", ["id"] = "_missing" })))["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal(-32602, (await Rpc(viewer, "tools/call", Call("make_coffee", new JsonObject())))["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32601, (await Rpc(viewer, "resources/list"))["error"]!["code"]!.GetValue<int>());

        // Elements linked to other apps are found by their tagged values.
        var linked = Text(await Rpc(viewer, "tools/call", Call("search_elements", new JsonObject { ["project"] = "Vehicle", ["query"] = "kind:Block where tag:keel.part = P-000123" })));
        Assert.Contains("Linked Engine", linked);
        Assert.Contains("\"total\":1", linked);

        var audit = await _db.QueryAuditAsync(new AuditQuery(Action: AuditActions.McpTool, Take: 100));
        Assert.Contains(audit.Items, e => e.Target == "search_elements" && e.Actor == "victor" && e.ProjectId == _project);
        Assert.Contains(audit.Items, e => e.Target == "list_projects");
    }

    [Fact]
    public async Task Editors_Change_The_Live_Model_And_Viewers_Cannot()
    {
        using var owner = Client(_ownerToken);
        var root = JsonNode.Parse(Text(await Rpc(owner, "tools/call", Call("search_elements", new JsonObject { ["project"] = "Vehicle", ["query"] = "kind:Package", ["limit"] = 200 }))))!;
        var package = root["results"]!.AsArray().First(r => r!["name"]!.ToString() != SecretPackage)!["id"]!.ToString();

        var created = await Rpc(owner, "tools/call", Call("create_element", new JsonObject
        {
            ["project"] = "Vehicle", ["owner_id"] = package, ["kind"] = "Requirement", ["name"] = "Range from an assistant",
            ["fields"] = new JsonArray(new JsonObject { ["key"] = "text", ["value"] = "The vehicle shall travel 500 km on one charge." }),
        }));
        Assert.False(created["result"]!["isError"]!.GetValue<bool>(), Text(created));
        var result = JsonNode.Parse(Text(created))!;
        Assert.StartsWith("Applied", result["applied"]!.ToString());
        var id = result["created"]!.AsObject().Single().Value!.ToString();

        // It's in the live model: a new read sees it, and it can be changed and deleted by id.
        var read = Text(await Rpc(owner, "tools/call", Call("get_element", new JsonObject { ["project"] = "Vehicle", ["id"] = id })));
        Assert.Contains("500 km", read);
        var updated = await Rpc(owner, "tools/call", Call("update_fields", new JsonObject { ["project"] = "Vehicle", ["element_id"] = id, ["fields"] = new JsonArray(new JsonObject { ["key"] = "name", ["value"] = "Range (500 km)" }) }));
        Assert.False(updated["result"]!["isError"]!.GetValue<bool>(), Text(updated));
        Assert.Contains("Range (500 km)", Text(await Rpc(owner, "tools/call", Call("get_element", new JsonObject { ["project"] = "Vehicle", ["id"] = id }))));

        // A viewer can read it but not change it.
        using var viewer = Client(_viewerToken);
        var refused = await Rpc(viewer, "tools/call", Call("delete_element", new JsonObject { ["project"] = "Vehicle", ["element_id"] = id }));
        Assert.True(refused["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("editor", Text(refused));

        var deleted = await Rpc(owner, "tools/call", Call("delete_element", new JsonObject { ["project"] = "Vehicle", ["element_id"] = id }));
        Assert.False(deleted["result"]!["isError"]!.GetValue<bool>(), Text(deleted));
        Assert.True((await Rpc(owner, "tools/call", Call("get_element", new JsonObject { ["project"] = "Vehicle", ["id"] = id })))["result"]!["isError"]!.GetValue<bool>());

        var audit = await _db.QueryAuditAsync(new AuditQuery(Action: AuditActions.McpTool, Take: 100));
        Assert.Contains(audit.Items, e => e.Target == "create_element" && e.Actor == "olga");
    }

    [Fact]
    public async Task Only_Tokens_Without_A_Browser_Origin_May_Connect()
    {
        const string ping = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";
        using (var none = await Post(Client(null), ping)) Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);

        // A username and password are not enough: AI clients get tokens, which can be revoked on their own.
        using var basic = Client(null);
        basic.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("olga:correct horse")));
        using (var pw = await Post(basic, ping)) Assert.Equal(HttpStatusCode.Unauthorized, pw.StatusCode);

        using var viewer = Client(_viewerToken);
        using (var req = new HttpRequestMessage(HttpMethod.Post, "api/mcp") { Content = new StringContent(ping, Encoding.UTF8, "application/json") })
        {
            req.Headers.Add("Origin", "https://evil.example");
            using var page = await viewer.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        }

        using (var get = await viewer.GetAsync("api/mcp")) Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        using (var note = await Post(viewer, """{"jsonrpc":"2.0","method":"notifications/initialized"}""")) Assert.Equal(HttpStatusCode.Accepted, note.StatusCode);
        using (var bad = await Post(viewer, "{nope")) Assert.Contains("-32700", await bad.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_Official_Mcp_Client_Connects_Lists_And_Calls_Tools()
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_base, "api/mcp"),
            Name = "meridian",
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + _viewerToken },
        });
        await using var client = await McpClient.CreateAsync(transport);
        Assert.Equal("meridian", client.ServerInfo.Name);
        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "get_traceability");
        Assert.All(tools.Where(t => t.Name is "search_elements" or "get_element" or "get_traceability"), t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
        Assert.False(tools.Single(t => t.Name == "create_element").ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(tools.Single(t => t.Name == "delete_element").ProtocolTool.Annotations?.DestructiveHint);
        var result = await client.CallToolAsync("list_diagrams", new Dictionary<string, object?> { ["project"] = "Vehicle" });
        Assert.NotEqual(true, result.IsError);
        Assert.IsType<TextContentBlock>(result.Content[0]);
    }
}
