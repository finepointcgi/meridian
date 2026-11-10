using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Meridian.Assistant.Engine;
using Meridian.Assistant.Providers;
using Meridian.Assistant.Tools;
using Meridian.Core.Model;
using Meridian.Core.Samples;

namespace Meridian.Tests;

/// <summary>The OpenAI-compatible (local model) provider against a fake server: streaming, tools, text fallback, discovery, errors.</summary>
public class LocalModelProviderTests
{
    private readonly ModelRepository _m = SampleModels.Vehicle();
    private string Context => Prompts.Context(_m, "Vehicle", _m.OfKind(ElementKind.Block).First(), _m.Diagrams.First(), readOnly: false);

    [Fact]
    public async Task Native_Tool_Loop_Streams_Text_And_Sends_Tool_Results_As_Tool_Messages()
    {
        var engine = _m.OfKind(ElementKind.Block).Single(b => b.Name == "Engine");
        var fake = new FakeOpenAiHandler()
            .Chat(new OpenAiSse().Text("Let me ", "look.").ToolCall(0, "call_abc", "search_elements", "{\"query\": \"kind:Block where name = Engine\"}").End("tool_calls"))
            .Chat(new OpenAiSse().Text("The ", $"[Engine](el:{engine.Id})", " satisfies REQ-005.").End());
        var conv = new Conversation();
        var updates = 0;
        await fake.Engine(FakeOpenAiHandler.Options() with { ApiKey = "local-test-key" }).RunTurnAsync(conv, _m, new TurnRequest("What satisfies engine power?", Context), () => updates++);

        Assert.Empty(conv.Items.OfType<NoticeItem>());
        var texts = conv.Items.OfType<AssistantTextItem>().ToList();
        Assert.Equal("Let me look.", texts[0].Text);
        Assert.Contains($"(el:{engine.Id})", texts[1].Text);
        Assert.Single(conv.Items.OfType<ToolActivityItem>(), t => t.Tool == "search_elements" && !t.IsError);
        Assert.Equal(4, conv.History.Count);
        Assert.Equal(240, conv.InputTokens);
        Assert.True(updates > 3);

        var bodies = fake.ChatBodies;
        Assert.Equal(2, bodies.Count);
        var first = bodies[0];
        Assert.Equal("qwen2.5:14b", first.GetProperty("model").GetString());
        Assert.True(first.GetProperty("stream").GetBoolean());
        Assert.Equal(Toolbox.All.Count, first.GetProperty("tools").GetArrayLength());
        Assert.Equal("function", first.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Equal("object", first.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
        var system = first.GetProperty("messages")[0];
        Assert.Equal("system", system.GetProperty("role").GetString());
        Assert.StartsWith(Prompts.System, system.GetProperty("content").GetString());   // Claude's prompt, unchanged…
        Assert.Contains("<format_guidance>", system.GetProperty("content").GetString());   // …plus local-only guidance
        Assert.Contains("<context>", first.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal("Bearer local-test-key", fake.Requests[0].Headers["authorization"]);
        Assert.StartsWith("http://localhost:11434/v1/chat/completions", fake.Requests[0].Url);

        var messages = bodies[1].GetProperty("messages");
        var assistant = messages[2];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        var call = assistant.GetProperty("tool_calls")[0];
        Assert.Equal("call_abc", call.GetProperty("id").GetString());
        Assert.Equal("kind:Block where name = Engine", JsonDocument.Parse(call.GetProperty("function").GetProperty("arguments").GetString()!).RootElement.GetProperty("query").GetString());
        var tool = messages[3];
        Assert.Equal("tool", tool.GetProperty("role").GetString());
        Assert.Equal("call_abc", tool.GetProperty("tool_call_id").GetString());
        Assert.Contains(engine.Id, tool.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Native_Write_Tools_Produce_A_Change_Set_And_Invalid_Input_Is_An_Error_Result()
    {
        var structure = _m.Root.OwnedElements.Single(e => e.Name == "Structure");
        var fake = new FakeOpenAiHandler()
            .Chat(new OpenAiSse()
                .ToolCall(0, "call_1", "create_element", $"{{\"kind\":\"Block\",\"owner_id\":\"{structure.Id}\",\"name\":\"Gearbox\"}}")
                .ToolCall(1, "call_2", "create_element", "{\"kind\":\"Gizmo\",\"name\":\"X\"}")
                .End("tool_calls"))
            .Chat(new OpenAiSse().Text("Proposed a **Gearbox** block.").End());
        var conv = new Conversation();
        var count = _m.AllElements.Count();
        await fake.Engine().RunTurnAsync(conv, _m, new TurnRequest("Add a gearbox", Context, AssistantEffort.High), () => { });

        Assert.Equal(count, _m.AllElements.Count());   // proposals only
        var card = Assert.Single(conv.Items.OfType<ChangeSetItem>());
        Assert.Equal("Gearbox", card.Changes.Items.Single().Name);
        var results = fake.ChatBodies[1].GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "tool").ToList();
        Assert.Equal(2, results.Count);
        Assert.StartsWith("ERROR: Invalid input", results[1].GetProperty("content").GetString());   // validated against the schema before running
    }

    [Fact]
    public async Task Multi_Turn_Conversation_Resends_The_History()
    {
        var fake = new FakeOpenAiHandler()
            .Chat(new OpenAiSse().Text("Hello!").End())
            .Chat(new OpenAiSse().Text("Still here.").End());
        var conv = new Conversation();
        var engine = fake.Engine();
        await engine.RunTurnAsync(conv, _m, new TurnRequest("Hi", Context), () => { });
        await engine.RunTurnAsync(conv, _m, new TurnRequest("Are you there?", Context), () => { });
        var messages = fake.ChatBodies[1].GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());   // system, user, assistant, user
        Assert.Equal("Hello!", messages[2].GetProperty("content").GetString());
        Assert.Contains("Are you there?", messages[3].GetProperty("content").GetString());
        Assert.Equal(4, conv.History.Count);
    }

    [Fact]
    public async Task Model_Without_Tool_Support_Falls_Back_To_The_Text_Protocol()
    {
        var engine = _m.OfKind(ElementKind.Block).Single(b => b.Name == "Engine");
        var fake = new FakeOpenAiHandler()
            .ChatError(HttpStatusCode.BadRequest, "registry.ollama.ai/library/gemma2:9b does not support tools")
            .Chat(new OpenAiSse().Text("I'll search.\n```json\n{\"tool\": \"search_elements\", \"arguments\": {\"query\": \"kind:Block where name = Engine\",},}\n```").End())
            .Chat(new OpenAiSse().Text($"Found [Engine](el:{engine.Id}).").End());
        var conv = new Conversation();
        var options = FakeOpenAiHandler.Options("gemma2:9b");
        var cache = new LocalToolSupportCache();
        var provider = new OpenAiCompatibleProvider(new LocalModelHttpFactory(fake).Create(options.BaseUrl), options, cache);
        await new AssistantEngine(provider).RunTurnAsync(conv, _m, new TurnRequest("Find the engine", Context), () => { });

        Assert.Empty(conv.Items.OfType<NoticeItem>());
        Assert.Single(conv.Items.OfType<ToolActivityItem>(), t => t.Tool == "search_elements" && !t.IsError);
        var texts = conv.Items.OfType<AssistantTextItem>().Select(t => t.Text).ToList();
        Assert.Equal("I'll search.", texts[0].Trim());                    // the JSON block is never shown
        Assert.DoesNotContain(texts, t => t.Contains("\"tool\"", StringComparison.Ordinal));
        Assert.Equal(false, cache.Get(options.BaseUrl, "gemma2:9b"));     // remembered for the next turns

        var bodies = fake.ChatBodies;
        Assert.True(bodies[0].TryGetProperty("tools", out _));
        Assert.False(bodies[1].TryGetProperty("tools", out _));
        Assert.Contains("<tool_protocol>", bodies[1].GetProperty("messages")[0].GetProperty("content").GetString());
        var third = bodies[2].GetProperty("messages");
        Assert.Contains("```json", third[2].GetProperty("content").GetString());   // our call, rendered back as text
        Assert.Equal("user", third[3].GetProperty("role").GetString());
        Assert.StartsWith("Tool results:", third[3].GetProperty("content").GetString());
        Assert.Contains(engine.Id, third[3].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Text_Protocol_Retries_Once_When_The_Block_Is_Not_Valid_Json()
    {
        var structure = _m.Root.OwnedElements.Single(e => e.Name == "Structure");
        var fake = new FakeOpenAiHandler()
            .Chat(new OpenAiSse().Text("```json\n{\"tool\": \"create_element\", \"arguments\": {\"kind\": \"Block\" \"name\": \n```").End())
            .Chat(new OpenAiSse().Text($"```json\n{{\"tool\": \"create_element\", \"arguments\": {{\"kind\": \"Block\", \"owner_id\": \"{structure.Id}\", \"name\": \"Pump\"}}}}\n```").End())
            .Chat(new OpenAiSse().Text("Proposed a pump.").End());
        var conv = new Conversation();
        await fake.Engine(FakeOpenAiHandler.Options(tools: ToolCallingMode.Text)).RunTurnAsync(conv, _m, new TurnRequest("Add a pump", Context), () => { });

        Assert.Empty(conv.Items.OfType<NoticeItem>());
        Assert.Equal("Pump", Assert.Single(conv.Items.OfType<ChangeSetItem>()).Changes.Items.Single().Name);
        var retry = fake.ChatBodies[1].GetProperty("messages");
        Assert.Contains("could not be used", retry.EnumerateArray().Last().GetProperty("content").GetString());
        Assert.Contains("not valid JSON", retry.EnumerateArray().Last().GetProperty("content").GetString());
        Assert.False(fake.ChatBodies[0].TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Text_Protocol_Schema_Mismatch_Is_Retried_Then_Reported_Without_Running_The_Tool()
    {
        var bad = "```json\n{\"tool\": \"create_element\", \"arguments\": {\"kind\": \"Gizmo\", \"name\": \"X\", \"colour\": \"red\"}}\n```";
        var fake = new FakeOpenAiHandler()
            .Chat(new OpenAiSse().Text(bad).End())
            .Chat(new OpenAiSse().Text(bad).End());
        var conv = new Conversation();
        await fake.Engine(FakeOpenAiHandler.Options(tools: ToolCallingMode.Text)).RunTurnAsync(conv, _m, new TurnRequest("Add a gizmo", Context), () => { });

        Assert.Equal(2, fake.ChatBodies.Count);   // one retry only
        Assert.Contains("colour is not a known property", fake.ChatBodies[1].GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString());
        var warning = Assert.Single(conv.Items.OfType<NoticeItem>());
        Assert.Equal(NoticeKind.Warning, warning.Kind);
        Assert.Contains("still invalid after one retry", warning.Text);
        Assert.Empty(conv.Items.OfType<ToolActivityItem>());
        Assert.Empty(conv.Items.OfType<ChangeSetItem>());
    }

    [Fact]
    public void Text_Protocol_Parses_Leniently_And_Validates_Every_Call()
    {
        var tools = ChatTool.Toolbox;
        // Fenced, trailing commas and a comment.
        var r = TextToolProtocol.Parse("Sure.\n```json\n{\"tool\": \"list_diagrams\", \"arguments\": {}, // none\n}\n```", tools);
        Assert.Equal("list_diagrams", Assert.Single(r.Calls).Name);
        Assert.Equal("Sure.", r.Text);
        Assert.Empty(r.Errors);
        // <tool_call> tags, OpenAI-style {"name", "arguments": "<json string>"} and an array.
        r = TextToolProtocol.Parse("<tool_call>[{\"name\": \"search_elements\", \"arguments\": \"{\\\"query\\\": \\\"kind:Block\\\"}\"}, {\"function\": {\"name\": \"list_diagrams\", \"parameters\": {}}}]</tool_call>", tools);
        Assert.Equal(["search_elements", "list_diagrams"], r.Calls.Select(c => c.Name));
        Assert.Equal("kind:Block", r.Calls[0].Input!.Value.GetProperty("query").GetString());
        // Bare JSON with prose after it.
        r = TextToolProtocol.Parse("{\"tool\": \"list_diagrams\", \"arguments\": {}} I will wait.", tools);
        Assert.Single(r.Calls);
        // Unknown tool and schema errors are reported, not run.
        r = TextToolProtocol.Parse("```json\n{\"tool\": \"format_disk\", \"arguments\": {}}\n```", tools);
        Assert.Empty(r.Calls);
        Assert.Contains("Unknown tool 'format_disk'", Assert.Single(r.Errors));
        r = TextToolProtocol.Parse("```json\n{\"tool\": \"search_elements\", \"arguments\": {\"query\": 5}}\n```", tools);
        Assert.Contains("input.query must be a string", Assert.Single(r.Errors));
        // JSON that is not a tool call stays in the answer.
        r = TextToolProtocol.Parse("Example:\n```json\n{\"mass\": 12}\n```", tools);
        Assert.Empty(r.Calls);
        Assert.Empty(r.Errors);
        Assert.Contains("\"mass\"", r.Text);
        // Streaming: text before a block may be shown, the block is held back.
        Assert.Equal(5, TextToolProtocol.VisibleLength("Sure.```js"));
        Assert.Equal(5, TextToolProtocol.VisibleLength("Sure.``"));
        Assert.Equal(0, TextToolProtocol.VisibleLength("  {\"tool\""));
    }

    [Fact]
    public async Task Model_Discovery_Reads_V1_Models_And_Ollama_Tags()
    {
        var fake = new FakeOpenAiHandler()
            .Get("/v1/models", new { @object = "list", data = new[] { new { id = "qwen2.5:14b" }, new { id = "llama3.1:8b" } } })
            .Get("/api/tags", new { models = new[] { new { name = "qwen2.5:14b", size = 9_000_000_000L }, new { name = "mistral-small:24b", size = 14_000_000_000L } } });
        var list = await fake.Provider().ListModelsAsync();
        Assert.Null(list.Error);
        Assert.True(list.IsOllama);
        Assert.Equal(["llama3.1:8b", "mistral-small:24b", "qwen2.5:14b"], list.Models.Select(m => m.Id));
        Assert.Equal(9_000_000_000L, list.Models.Single(m => m.Id == "qwen2.5:14b").SizeBytes);
        Assert.Contains(fake.Requests, r => r.Url == "http://localhost:11434/api/tags");

        // LM Studio: only /v1/models (no /api/tags).
        var lm = new FakeOpenAiHandler().Get("/v1/models", new { data = new[] { new { id = "qwen2.5-7b-instruct" } } });
        var lmOptions = FakeOpenAiHandler.Options("qwen2.5-7b-instruct") with { BaseUrl = new Uri(LocalModelOptions.LmStudioUrl) };
        var lmList = await lm.Provider(lmOptions).ListModelsAsync();
        Assert.Null(lmList.Error);
        Assert.False(lmList.IsOllama);
        Assert.Equal("qwen2.5-7b-instruct", Assert.Single(lmList.Models).Id);
    }

    [Fact]
    public async Task Test_Connection_Reports_A_Missing_Model_And_Detects_Tool_Support()
    {
        var missing = new FakeOpenAiHandler()
            .Get("/v1/models", new { data = new[] { new { id = "llama3.1:8b" } } })
            .Get("/api/tags", new { models = new[] { new { name = "llama3.1:8b" } } });
        var test = await missing.Provider().TestConnectionAsync();
        Assert.False(test.Ok);
        Assert.Contains("run `ollama pull qwen2.5:14b`", test.Message);

        var ok = new FakeOpenAiHandler()
            .Get("/v1/models", new { data = new[] { new { id = "qwen2.5:14b" } } })
            .Get("/api/tags", new { models = new[] { new { name = "qwen2.5:14b" } } })
            .Get("/api/show", new { capabilities = new[] { "completion", "tools" } });
        test = await ok.Provider().TestConnectionAsync();
        Assert.True(test.Ok, test.Message);
        Assert.True(test.SupportsTools);
        Assert.Contains("supports tool calling", test.Message);
    }

    [Fact]
    public async Task Unreachable_Server_And_Missing_Model_Give_Clear_Errors()
    {
        var refused = new ThrowingHandler(new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused)));
        var options = FakeOpenAiHandler.Options();
        var conv = new Conversation();
        var provider = new OpenAiCompatibleProvider(new LocalModelHttpFactory(refused).Create(options.BaseUrl), options, new LocalToolSupportCache());
        await new AssistantEngine(provider).RunTurnAsync(conv, _m, new TurnRequest("Hello", Context), () => { });
        var notice = Assert.Single(conv.Items.OfType<NoticeItem>());
        Assert.Equal(NoticeKind.Error, notice.Kind);
        Assert.Contains("Could not connect to the local model server at http://localhost:11434", notice.Text);
        Assert.Contains("ollama serve", notice.Text);
        Assert.Empty(conv.History);
        Assert.False(conv.Busy);
        Assert.Contains("Could not connect", (await provider.ListModelsAsync()).Error);

        var fake = new FakeOpenAiHandler().ChatError(HttpStatusCode.NotFound, "model \"qwen2.5:14b\" not found, try pulling it first");
        conv = new Conversation();
        await fake.Engine().RunTurnAsync(conv, _m, new TurnRequest("Hello", Context), () => { });
        Assert.Equal("Model 'qwen2.5:14b' isn't installed — run `ollama pull qwen2.5:14b`.", Assert.Single(conv.Items.OfType<NoticeItem>()).Text);

        fake = new FakeOpenAiHandler().ChatError(HttpStatusCode.Unauthorized, "bad key");
        conv = new Conversation();
        await fake.Engine().RunTurnAsync(conv, _m, new TurnRequest("Hello", Context), () => { });
        Assert.Contains("Check the API key", Assert.Single(conv.Items.OfType<NoticeItem>()).Text);
    }

    [Fact]
    public async Task Slow_Models_Time_Out_And_Stop_Cancels()
    {
        var slow = new DelayHandler(TimeSpan.FromSeconds(30));
        var options = FakeOpenAiHandler.Options() with { FirstTokenTimeout = TimeSpan.FromMilliseconds(200) };
        var conv = new Conversation();
        var provider = new OpenAiCompatibleProvider(new LocalModelHttpFactory(slow).Create(options.BaseUrl), options, new LocalToolSupportCache());
        await new AssistantEngine(provider).RunTurnAsync(conv, _m, new TurnRequest("Hello", Context), () => { });
        Assert.Contains("did not answer in time", Assert.Single(conv.Items.OfType<NoticeItem>()).Text);

        conv = new Conversation();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var generous = new OpenAiCompatibleProvider(new LocalModelHttpFactory(slow).Create(options.BaseUrl), FakeOpenAiHandler.Options(), new LocalToolSupportCache());
        await new AssistantEngine(generous).RunTurnAsync(conv, _m, new TurnRequest("Hello", Context), () => { }, cts.Token);
        Assert.Equal("Stopped.", Assert.Single(conv.Items.OfType<NoticeItem>()).Text);
        Assert.Empty(conv.History);
    }

    [Fact]
    public async Task Requests_Only_Ever_Go_To_The_Configured_Host()
    {
        var fake = new FakeOpenAiHandler();
        using var http = new LocalModelHttpFactory(fake).Create(new Uri("http://localhost:11434/v1"));
        await Assert.ThrowsAsync<ChatProviderException>(() => http.GetAsync("http://169.254.169.254/latest/meta-data/"));
        await Assert.ThrowsAsync<ChatProviderException>(() => http.GetAsync("http://localhost:8080/admin"));
        Assert.Empty(fake.Requests);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("http://localhost:11434/v1/nothing")).StatusCode);
    }

    [Fact]
    public void Base_Urls_Are_Normalised_And_Unsafe_Ones_Rejected()
    {
        Assert.Equal("http://localhost:11434/v1", LocalModelOptions.NormalizeBaseUrl("localhost:11434")!.AbsoluteUri);
        Assert.Equal("http://localhost:1234/v1", LocalModelOptions.NormalizeBaseUrl("http://localhost:1234/v1/")!.AbsoluteUri);
        Assert.Equal("https://gpu.example.com/openai/v1", LocalModelOptions.NormalizeBaseUrl("https://gpu.example.com/openai/v1/chat/completions")!.AbsoluteUri);
        Assert.Null(LocalModelOptions.NormalizeBaseUrl("file:///etc/passwd"));
        Assert.Null(LocalModelOptions.NormalizeBaseUrl("http://user:pw@localhost:11434/v1"));
        Assert.Null(LocalModelOptions.NormalizeBaseUrl("http://localhost:11434/v1?x=1"));
        Assert.Null(LocalModelOptions.NormalizeBaseUrl(" "));
    }

    [Fact]
    public async Task Long_Conversations_Are_Shortened_To_The_Context_Window_Or_Refused()
    {
        var fake = new FakeOpenAiHandler().Chat(new OpenAiSse().Text("ok").End());
        var conv = new Conversation();
        conv.History.Add(new ChatMessage(ChatRole.User, [new TextPart("<context>…</context>"), new TextPart("first")]));
        conv.History.Add(new ChatMessage(ChatRole.Assistant, [new ToolCallPart("c1", "get_sysml_text", JsonDocument.Parse("{}").RootElement, "{}")]));
        conv.History.Add(new ChatMessage(ChatRole.User, [new ToolResultPart("c1", "get_sysml_text", new string('x', 200_000), false)]));
        conv.History.Add(new ChatMessage(ChatRole.Assistant, [new TextPart("done")]));
        await fake.Engine(FakeOpenAiHandler.Options() with { ContextWindow = 16384, MaxTokens = 2048 }).RunTurnAsync(conv, _m, new TurnRequest("Next", Context), () => { });
        Assert.Empty(conv.Items.OfType<NoticeItem>());
        var tool = fake.ChatBodies[0].GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Contains("shortened to fit", tool.GetProperty("content").GetString());

        conv = new Conversation();
        await fake.Engine(FakeOpenAiHandler.Options() with { ContextWindow = 2048, MaxTokens = 1024 }).RunTurnAsync(conv, _m, new TurnRequest(new string('y', 20_000), Context), () => { });
        Assert.Contains("no longer fits", Assert.Single(conv.Items.OfType<NoticeItem>()).Text);
    }

    [Fact]
    public async Task Narrative_Writer_Works_With_A_Local_Model()
    {
        var args = JsonSerializer.Serialize(new { sections = new[] { new { title = "Introduction", overview = "The vehicle {model}.", rationale = "Because." } } });
        var fake = new FakeOpenAiHandler().Chat(new OpenAiSse().ToolCall(0, "call_n", NarrativeWriter.ToolName, args).End("tool_calls"));
        var result = await new NarrativeWriter(fake.Provider()).DraftAsync(_m, "SDD", ["Introduction", "Design"]);
        Assert.True(result.Ok, result.Error);
        Assert.Equal("The vehicle (model).", Assert.Single(result.Sections).Overview);
        Assert.Contains("submit_narrative", fake.Requests[0].Body);
    }

    private sealed class ThrowingHandler(Exception e) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromException<HttpResponseMessage>(e);
    }

    private sealed class DelayHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
