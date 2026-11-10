using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Meridian.Assistant.Providers;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Meridian.Web.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>The local-model provider in the panel and in Settings ▸ AI assistant (server: admin config only; desktop: per device).</summary>
public class LocalAssistantSettingsTests : TestContext
{
    private const string LocalKey = "local-test-key-not-real";
    private readonly FakeServer _server = new();
    private readonly string _data = Path.Combine(Path.GetTempPath(), "meridian-local-ai-" + Guid.NewGuid().ToString("N"));

    private Workspace Setup(bool localMode, Dictionary<string, string?>? config = null, bool admin = false)
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config ?? new()).Build());
        Services.AddSingleton(new MeridianHostInfo(localMode, _data, null));
        Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        Services.AddMeridianAssistant();
        Services.AddSingleton<ILocalModelHttpFactory>(new LocalModelHttpFactory(_server));   // the fake server; never a real one
        Services.AddSingleton<Meridian.Data.IMeridianStore>(new Meridian.Data.SqliteStore(Path.Combine(_data, "prefs.db")));
        Services.AddScoped<ThemeService>();
        Services.GetRequiredService<AssistantSettings>().ReadEnvironment = _ => null;
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(SampleModels.Vehicle());
        ws.IsServerAdmin = admin;
        RenderComponent<MudPopoverProvider>();
        return ws;
    }

    private static Dictionary<string, string?> ServerConfig() => new()
    {
        ["Meridian:AI:Provider"] = "Local",
        ["Meridian:AI:Local:BaseUrl"] = "http://ollama.internal:11434",
        ["Meridian:AI:Local:Model"] = "qwen2.5:14b",
    };

    [Fact]
    public void Server_Local_Provider_Shows_Its_Own_Notice_And_Streams_Answers()
    {
        var ws = Setup(localMode: false, ServerConfig());
        var engine = ws.Model.OfKind(ElementKind.Block).Single(b => b.Name == "Engine");
        _server.Chat($"The [Engine](el:{engine.Id}) satisfies **REQ-005**.");
        var cut = RenderComponent<AssistantPanel>();

        Assert.Contains("Your model stays on the configured model server", cut.Markup);
        Assert.DoesNotContain("Your model is sent to Anthropic", cut.Markup);
        Assert.DoesNotContain("Anthropic's API", cut.Markup);
        Assert.Contains("Local model · qwen2.5:14b", cut.Find(".ai-provider").TextContent);
        Assert.DoesNotContain("ollama.internal", cut.Markup);   // the server's address is for administrators

        cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Contains("I understand")).Click()).GetAwaiter().GetResult();
        cut.InvokeAsync(() => cut.Find(".ai-input textarea").Input("What satisfies engine power?")).GetAwaiter().GetResult();
        cut.InvokeAsync(() => cut.Find("button.ai-send").Click()).GetAwaiter().GetResult();
        cut.WaitForAssertion(() => Assert.Contains("ai-chip", cut.Markup), TimeSpan.FromSeconds(5));
        Assert.Equal("REQ-005", cut.Find(".ai-msg.assistant strong").TextContent);
        var request = Assert.Single(_server.Requests);
        Assert.Equal("http://ollama.internal:11434/v1/chat/completions", request.Url);
        Assert.Equal("qwen2.5:14b", JsonDocument.Parse(request.Body).RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void Acknowledging_Anthropic_Does_Not_Cover_The_Local_Notice()
    {
        Setup(localMode: true);
        var settings = Services.GetRequiredService<AssistantSettings>();
        settings.Acknowledge(null);
        Assert.True(settings.HasAcknowledged(null));
        settings.SetProvider(AssistantProvider.Local);
        Assert.False(settings.HasAcknowledged(null));
    }

    [Fact]
    public void Server_Users_Cannot_Point_The_Server_Elsewhere()
    {
        Setup(localMode: false, ServerConfig());
        var settings = Services.GetRequiredService<AssistantSettings>();
        Assert.Throws<InvalidOperationException>(() => settings.SaveLocal("http://169.254.169.254", "x", 8192, 1024, null, ToolCallingMode.Auto));
        Assert.Throws<InvalidOperationException>(() => settings.SetProvider(AssistantProvider.Anthropic));
        Assert.Throws<InvalidOperationException>(() => settings.DraftLocalOptions("http://10.0.0.1", "x"));
        Assert.Throws<InvalidOperationException>(() => settings.SaveLocalApiKey("k"));
        Assert.Equal("http://ollama.internal:11434/v1", settings.LocalOptions()!.BaseUrl.AbsoluteUri);

        // Non-admins see the provider and model only, read-only.
        var d = SettingsDialogTests.Open(this, SettingsSection.Assistant);
        Assert.Contains("Local model server (qwen2.5:14b)", d.Markup);
        Assert.Empty(d.FindAll("input.ai-local-url"));
        Assert.Empty(d.FindAll(".seg[data-name='ai-provider']"));
        Assert.Empty(d.FindAll("button.ai-local-test"));
        Assert.DoesNotContain("ollama.internal", d.Markup);
        Assert.Contains(SettingsDialog.LocalPrivacyText, d.Markup);
        Assert.DoesNotContain(SettingsDialog.PrivacyText, d.Markup);
    }

    [Fact]
    public void Server_Admins_Can_Test_The_Configured_Server()
    {
        Setup(localMode: false, ServerConfig(), admin: true);
        _server.Get("/v1/models", new { data = new[] { new { id = "qwen2.5:14b" } } });
        _server.Get("/api/tags", new { models = new[] { new { name = "qwen2.5:14b" } } });
        _server.Get("/api/show", new { capabilities = new[] { "completion", "tools" } });
        var d = SettingsDialogTests.Open(this, SettingsSection.Assistant);
        Assert.Contains("http://ollama.internal:11434/v1", d.Markup);
        Assert.Empty(d.FindAll("input.ai-local-url"));   // still configuration only
        d.Find("button.ai-local-test").Click();
        d.WaitForAssertion(() => Assert.Contains("model 'qwen2.5:14b' is available", d.Find(".ai-local-message").TextContent), TimeSpan.FromSeconds(5));
        Assert.All(_server.Requests, r => Assert.StartsWith("http://ollama.internal:11434/", r.Url));
    }

    [Fact]
    public void Desktop_Configures_A_Local_Server_Per_Device()
    {
        var ws = Setup(localMode: true);
        _server.Get("/v1/models", new { data = new[] { new { id = "llama3.1:8b" }, new { id = "qwen2.5:14b" } } });
        _server.Get("/api/tags", new { models = new[] { new { name = "llama3.1:8b" }, new { name = "qwen2.5:14b" } } });
        var d = SettingsDialogTests.Open(this, SettingsSection.Assistant);
        d.FindAll(".seg[data-name='ai-provider'] button").Single(b => b.TextContent.Trim() == "Local / OpenAI-compatible").Click();
        d.WaitForAssertion(() => Assert.NotEmpty(d.FindAll("input.ai-local-url")));
        Assert.Empty(d.FindAll("input.ai-key"));   // the Anthropic key row is for the Claude provider

        d.Find("button.ai-preset-ollama").Click();
        d.Find("button.ai-local-refresh").Click();
        d.WaitForAssertion(() => Assert.Equal(2, d.FindAll("#ai-local-models option").Count), TimeSpan.FromSeconds(5));
        d.Find("input.ai-local-model").Input("qwen2.5:14b");
        d.FindAll(".seg[data-name='ai-local-tools'] button").Single(b => b.TextContent.Trim() == "No (text protocol)").Click();
        d.Find("input.ai-local-key").Input(LocalKey);
        d.Find("button.ai-local-save").Click();
        d.WaitForAssertion(() => Assert.Contains("Saved.", d.Find(".ai-local-message").TextContent));

        var settings = Services.GetRequiredService<AssistantSettings>();
        var o = settings.LocalOptions()!;
        Assert.Equal(AssistantProvider.Local, settings.Provider);
        Assert.Equal("http://localhost:11434/v1", o.BaseUrl.AbsoluteUri);
        Assert.Equal("qwen2.5:14b", o.Model);
        Assert.Equal(ToolCallingMode.Text, o.ToolCalling);
        Assert.Equal(LocalKey, o.ApiKey);
        Assert.DoesNotContain(LocalKey, d.Markup);
        var stored = File.ReadAllText(Path.Combine(_data, "assistant.json"));
        Assert.DoesNotContain(LocalKey, stored);   // Data Protection, not plain text
        Assert.Contains("localhost:11434", stored);
        Assert.Null(Services.GetRequiredService<AssistantService>().UnavailableReason);
        Assert.Contains(SettingsDialog.LocalPrivacyText, d.Markup);
        Assert.NotNull(ws);
    }

    [Fact]
    public void Desktop_Local_Provider_Without_A_Model_Explains_How_To_Set_It_Up()
    {
        Setup(localMode: true);
        Services.GetRequiredService<AssistantSettings>().SetProvider(AssistantProvider.Local);
        var cut = RenderComponent<AssistantPanel>();
        Assert.Contains("Set the local model server's URL", cut.Markup);
        Assert.Contains("Set up the local model in Settings", cut.Find("button.ai-open-settings").TextContent);
    }

    [Fact]
    public void Claude_Code_Is_Never_Used_On_A_Server()
    {
        Setup(localMode: false, new() { ["Meridian:AI:Provider"] = "ClaudeCode" });
        var settings = Services.GetRequiredService<AssistantSettings>();
        Assert.Equal(AssistantProvider.ClaudeCode, settings.Provider);
        Assert.Contains("only be used by the desktop app", settings.UnavailableReason);
        Assert.Null(settings.ClaudeCodeOptions());
        Assert.Null(Services.GetRequiredService<AssistantService>().CreateProvider());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_data, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A fake OpenAI-compatible server: queued chat answers and GET routes, recorded requests.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        private readonly Queue<string> _chats = new();
        private readonly Dictionary<string, Queue<object>> _gets = new();
        public List<(string Url, string Body)> Requests { get; } = new();

        public void Chat(string text) => _chats.Enqueue(
            "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = text }, finish_reason = (string?)null } } }) + "\n\n" +
            "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } } }) + "\n\ndata: [DONE]\n\n");

        public void Get(string suffix, object body)
        {
            if (!_gets.TryGetValue(suffix, out var q)) _gets[suffix] = q = new();
            q.Enqueue(body);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct)));
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/chat/completions", StringComparison.Ordinal) && _chats.Count > 0)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_chats.Dequeue(), Encoding.UTF8, "text/event-stream") };
            var route = _gets.Keys.FirstOrDefault(k => path.EndsWith(k, StringComparison.Ordinal) && _gets[k].Count > 0);
            if (route != null)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(_gets[route].Dequeue()), Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
