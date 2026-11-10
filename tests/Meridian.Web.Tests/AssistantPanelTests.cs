using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Meridian.Assistant.Engine;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

public class AssistantPanelTests : TestContext
{
    private const string TestKey = "sk-ant-test-0000000000000000-not-real";
    private readonly FakeApi _api = new();
    private readonly string _data = Path.Combine(Path.GetTempPath(), "meridian-ai-" + Guid.NewGuid().ToString("N"));

    private Workspace Setup(bool localMode, string? configuredKey = null, string? enabled = null)
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Meridian:AI:ApiKey"] = configuredKey,
            ["Meridian:AI:Enabled"] = enabled,
        }).Build();
        Services.AddSingleton<IConfiguration>(config);
        Services.AddSingleton(new MeridianHostInfo(localMode, _data, null));
        Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        Services.AddMeridianAssistant();
        Services.AddSingleton<IAssistantClientFactory>(new AssistantClientFactory(new HttpClient(_api), maxRetries: 0));   // replaces the real one
        Services.GetRequiredService<AssistantSettings>().ReadEnvironment = _ => null;
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(SampleModels.Vehicle());
        RenderComponent<MudBlazor.MudPopoverProvider>();
        return ws;
    }

    [Fact]
    public void Off_Without_A_Key_And_Explains_How_To_Enable()
    {
        Setup(localMode: false);
        var cut = RenderComponent<AssistantPanel>();
        Assert.Contains("AI assistant is off", cut.Markup);
        Assert.Contains("Meridian:AI:ApiKey", cut.Markup);
        Assert.Empty(cut.FindAll("textarea"));
        Assert.Empty(cut.FindAll("input.ai-key"));   // servers never take a key from the browser
    }

    [Fact]
    public void Admin_Can_Turn_It_Off()
    {
        Setup(localMode: false, configuredKey: TestKey, enabled: "false");
        var cut = RenderComponent<AssistantPanel>();
        Assert.Contains("turned off by the administrator", cut.Markup);
    }

    [Fact]
    public void Local_Mode_Saves_The_Key_Encrypted_And_Never_Renders_It()
    {
        Setup(localMode: true);
        var cut = RenderComponent<AssistantPanel>();
        Assert.Contains("Add your Anthropic API key", cut.Markup);
        cut.Find("input.ai-key").Input(TestKey);
        cut.Find("button.ai-save-key").Click();
        Assert.Contains("Review this model", cut.Markup);   // the assistant is on now
        Assert.DoesNotContain(TestKey, cut.Markup);
        var stored = File.ReadAllText(Path.Combine(_data, "assistant.json"));
        Assert.DoesNotContain(TestKey, stored);   // Data Protection, not plain text
        Assert.Equal(TestKey, Services.GetRequiredService<AssistantSettings>().ApiKey());
    }

    [Fact]
    public void Streams_An_Answer_With_Clickable_Mentions_After_The_Privacy_Notice()
    {
        var ws = Setup(localMode: false, configuredKey: TestKey);
        var engine = ws.Model.OfKind(ElementKind.Block).Single(b => b.Name == "Engine");
        _api.Stream(Sse.Text($"The [Engine](el:{engine.Id}) satisfies **REQ-005**.", "end_turn"));
        var cut = RenderComponent<AssistantPanel>();

        // One-time notice: the model is sent to Anthropic. Sending is blocked until acknowledged.
        Assert.Contains("Your model is sent to Anthropic", cut.Markup);
        Assert.True(cut.Find(".ai-input textarea").HasAttribute("disabled"));
        cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Contains("I understand")).Click()).GetAwaiter().GetResult();
        cut.WaitForAssertion(() => Assert.DoesNotContain("Your model is sent to Anthropic", cut.Markup));

        cut.InvokeAsync(() => cut.Find(".ai-input textarea").Input("What satisfies engine power?")).GetAwaiter().GetResult();
        cut.InvokeAsync(() => cut.Find("button.ai-send").Click()).GetAwaiter().GetResult();
        cut.WaitForAssertion(() => Assert.Contains("ai-chip", cut.Markup), TimeSpan.FromSeconds(5));
        Assert.Contains("What satisfies engine power?", cut.Find(".ai-msg.user").TextContent);
        Assert.Equal("REQ-005", cut.Find(".ai-msg.assistant strong").TextContent);
        Assert.Single(_api.Bodies);
        Assert.DoesNotContain(TestKey, cut.Markup);

        cut.InvokeAsync(() => cut.Find("button.ai-chip").Click()).GetAwaiter().GetResult();
        Assert.Same(engine, ws.Selected);

        // New chat clears the transcript.
        cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Contains("New chat")).Click()).GetAwaiter().GetResult();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".ai-msg")));
    }

    [Fact]
    public void Proposed_Changes_Are_Reviewed_Then_Applied_As_One_Undo_Step()
    {
        var ws = Setup(localMode: false, configuredKey: TestKey);
        var structure = ws.Model.Root.OwnedElements.Single(e => e.Name == "Structure");
        _api.Stream(Sse.ToolUse("toolu_1", "create_element", $"{{\"kind\":\"Block\",\"owner_id\":\"{structure.Id}\",\"name\":\"Gearbox\"}}"));
        _api.Stream(Sse.ToolUse("toolu_2", "create_element", $"{{\"kind\":\"Block\",\"owner_id\":\"{structure.Id}\",\"name\":\"Clutch\"}}"));
        _api.Stream(Sse.Text("Proposed a gearbox and a clutch.", "end_turn"));
        Services.GetRequiredService<AssistantService>().Acknowledge();
        var cut = RenderComponent<AssistantPanel>();

        cut.Find(".ai-input textarea").Input("Add a gearbox and a clutch");
        cut.Find("button.ai-send").Click();
        cut.WaitForAssertion(() => Assert.Contains("Proposed changes #1", cut.Markup), TimeSpan.FromSeconds(5));
        Assert.Contains("+2 added", cut.Markup);
        Assert.DoesNotContain(ws.Model.OfKind(ElementKind.Block), b => b.Name == "Gearbox");

        // Reject the clutch, apply the rest.
        cut.InvokeAsync(() => cut.FindAll(".ai-change")[1].QuerySelectorAll("button").Single(b => b.TextContent == "Reject").Click()).GetAwaiter().GetResult();
        cut.WaitForAssertion(() => Assert.Contains("Apply 1", cut.Find("button.ai-apply").TextContent));
        cut.InvokeAsync(() => cut.Find("button.ai-apply").Click()).GetAwaiter().GetResult();
        cut.WaitForAssertion(() => Assert.Contains(ws.Model.OfKind(ElementKind.Block), b => b.Name == "Gearbox"));
        Assert.DoesNotContain(ws.Model.OfKind(ElementKind.Block), b => b.Name == "Clutch");
        Assert.Contains("Applied 1 change", cut.Markup);
        Assert.Empty(cut.FindAll("button.ai-apply"));
        Assert.StartsWith("Assistant:", ws.Model.History.UndoDescription);
        ws.Model.History.Undo();
        Assert.DoesNotContain(ws.Model.OfKind(ElementKind.Block), b => b.Name == "Gearbox");
    }

    [Fact]
    public void Quick_Action_Form_Sends_A_High_Effort_Request()
    {
        var ws = Setup(localMode: false, configuredKey: TestKey);
        _api.Stream(Sse.Text("No requirements found.", "end_turn"));
        Services.GetRequiredService<AssistantService>().Acknowledge();
        var launcher = new AssistantLauncher();
        launcher.Run(QuickAction.GenerateRequirements);
        var cut = RenderComponent<AssistantPanel>(p => p.Add(x => x.Launcher, launcher));
        Assert.Contains("Generate requirements from text", cut.Find(".ai-form-title").TextContent);
        cut.Find(".ai-form textarea").Input("The pump shall deliver 10 l/min.");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Propose")).Click();
        cut.WaitForAssertion(() => Assert.Contains("No requirements found.", cut.Markup), TimeSpan.FromSeconds(5));
        var body = JsonDocument.Parse(_api.Bodies.Single()).RootElement;
        Assert.Equal("high", body.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Contains("10 l/min", body.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("text").GetString());
        Assert.Contains("Generate requirements in Requirements", cut.Find(".ai-msg.user").TextContent);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_data, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Fake Messages API (no real calls): queued SSE streams, recorded request bodies.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        private readonly Queue<string> _streams = new();
        public List<string> Bodies { get; } = new();
        public void Stream(string sse) => _streams.Enqueue(sse);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_streams.Dequeue(), Encoding.UTF8, "text/event-stream") };
        }
    }

    private static class Sse
    {
        private static string E(string name, object data) => $"event: {name}\ndata: {JsonSerializer.Serialize(data)}\n\n";

        private static string Start() => E("message_start", new
        {
            type = "message_start",
            message = new
            {
                id = "msg_1", type = "message", role = "assistant", model = "claude-opus-5-5", content = Array.Empty<object>(),
                stop_reason = (string?)null, usage = new { input_tokens = 10, output_tokens = 1, cache_creation_input_tokens = 0, cache_read_input_tokens = 0 },
            },
        });

        private static string End(string stop) =>
            E("message_delta", new { type = "message_delta", delta = new { stop_reason = stop }, usage = new { output_tokens = 5 } }) + E("message_stop", new { type = "message_stop" });

        public static string Text(string text, string stop) =>
            Start()
            + E("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } })
            + E("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } })
            + E("content_block_stop", new { type = "content_block_stop", index = 0 })
            + End(stop);

        public static string ToolUse(string id, string name, string json) =>
            Start()
            + E("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "tool_use", id, name, input = new { } } })
            + E("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = json } })
            + E("content_block_stop", new { type = "content_block_stop", index = 0 })
            + End("tool_use");
    }
}
