using System.Net;
using System.Text;
using System.Text.Json;
using Meridian.Assistant.Engine;
using Meridian.Assistant.Providers;

namespace Meridian.Tests;

/// <summary>
/// A fake OpenAI-compatible server (Ollama / LM Studio style): routes by path to queued responses and records every
/// request. No test ever reaches a real local model server.
/// </summary>
public sealed class FakeOpenAiHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<Func<HttpRequestMessage, HttpResponseMessage>>> _routes = new();
    public List<(string Method, string Url, string Body, Dictionary<string, string> Headers)> Requests { get; } = new();

    /// <summary>Requests to /chat/completions, as parsed JSON bodies.</summary>
    public List<JsonElement> ChatBodies => Requests.Where(r => r.Url.EndsWith("/chat/completions", StringComparison.Ordinal))
                                                   .Select(r => JsonDocument.Parse(r.Body).RootElement.Clone()).ToList();

    public FakeOpenAiHandler On(string pathSuffix, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        if (!_routes.TryGetValue(pathSuffix, out var q)) _routes[pathSuffix] = q = new();
        q.Enqueue(respond);
        return this;
    }

    public FakeOpenAiHandler Chat(string sse) => On("/chat/completions", _ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
    });

    public FakeOpenAiHandler ChatError(HttpStatusCode status, string message) => On("/chat/completions", _ => Json(status, new { error = new { message, type = "api_error" } }));

    public FakeOpenAiHandler Get(string pathSuffix, object body, HttpStatusCode status = HttpStatusCode.OK) => On(pathSuffix, _ => Json(status, body));

    public static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        var url = request.RequestUri!.ToString();
        Requests.Add((request.Method.Method, url, body, headers));
        var route = _routes.Keys.Where(k => request.RequestUri!.AbsolutePath.EndsWith(k, StringComparison.Ordinal)).OrderByDescending(k => k.Length).FirstOrDefault();
        if (route == null || _routes[route].Count == 0) return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("404 page not found") };
        return _routes[route].Dequeue()(request);
    }

    public OpenAiCompatibleProvider Provider(LocalModelOptions? options = null) =>
        new(new LocalModelHttpFactory(this).Create((options ?? Options()).BaseUrl), options ?? Options(), new LocalToolSupportCache());

    public AssistantEngine Engine(LocalModelOptions? options = null) => new(Provider(options));

    public static LocalModelOptions Options(string model = "qwen2.5:14b", ToolCallingMode tools = ToolCallingMode.Auto) =>
        new() { BaseUrl = new Uri(LocalModelOptions.OllamaUrl), Model = model, ToolCalling = tools, RevealEndpoint = true };
}

/// <summary>Builds OpenAI chat-completion SSE streams.</summary>
public sealed class OpenAiSse
{
    private readonly StringBuilder _sb = new();

    private OpenAiSse Chunk(object delta, string? finish = null)
    {
        var chunk = new { id = "chatcmpl-1", @object = "chat.completion.chunk", model = "qwen2.5:14b", choices = new[] { new { index = 0, delta, finish_reason = finish } } };
        _sb.Append("data: ").Append(JsonSerializer.Serialize(chunk)).Append("\n\n");
        return this;
    }

    public OpenAiSse Text(params string[] chunks)
    {
        foreach (var c in chunks) Chunk(new { role = "assistant", content = c });
        return this;
    }

    /// <summary>A tool call streamed as the OpenAI API does: id and name first, then argument fragments.</summary>
    public OpenAiSse ToolCall(int index, string id, string name, string argumentsJson)
    {
        Chunk(new { tool_calls = new[] { new { index, id, type = "function", function = new { name, arguments = "" } } } });
        var half = argumentsJson.Length / 2;
        Chunk(new { tool_calls = new[] { new { index, function = new { arguments = argumentsJson[..half] } } } });
        Chunk(new { tool_calls = new[] { new { index, function = new { arguments = argumentsJson[half..] } } } });
        return this;
    }

    public string End(string finish = "stop", int promptTokens = 120, int completionTokens = 30)
    {
        Chunk(new { }, finish);
        _sb.Append("data: ").Append(JsonSerializer.Serialize(new { id = "chatcmpl-1", choices = Array.Empty<object>(), usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens } })).Append("\n\n");
        _sb.Append("data: [DONE]\n\n");
        return _sb.ToString();
    }
}
