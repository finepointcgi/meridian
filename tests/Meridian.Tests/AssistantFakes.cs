using System.Net;
using System.Text;
using System.Text.Json;
using Meridian.Assistant.Engine;

namespace Meridian.Tests;

/// <summary>
/// A fake Anthropic API: answers each POST /v1/messages with the next queued server-sent-event stream and records
/// the requests. No test ever reaches the real API.
/// </summary>
public sealed class FakeAnthropicHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();
    public List<(string Url, string Body, Dictionary<string, string> Headers)> Requests { get; } = new();

    public FakeAnthropicHandler Stream(string sse)
    {
        _responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });
        return this;
    }

    public FakeAnthropicHandler Error(HttpStatusCode status, string type, string message)
    {
        _responses.Enqueue(() => new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { type = "error", error = new { type, message } }), Encoding.UTF8, "application/json"),
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        Requests.Add((request.RequestUri!.ToString(), body, headers));
        if (_responses.Count == 0) throw new InvalidOperationException("No fake response queued");
        return _responses.Dequeue()();
    }

    public AssistantEngine Engine(AssistantOptions? options = null) =>
        new(new AssistantClientFactory(new HttpClient(this), maxRetries: 0).Create("sk-ant-test-key-not-real-000000"), options);
}

/// <summary>Builds Messages API streaming responses.</summary>
public sealed class Sse
{
    private readonly StringBuilder _sb = new();
    private int _index;

    public Sse()
    {
        Event("message_start", new
        {
            type = "message_start",
            message = new
            {
                id = "msg_test", type = "message", role = "assistant", model = "claude-opus-5-5", content = Array.Empty<object>(),
                stop_reason = (string?)null, stop_sequence = (string?)null,
                usage = new { input_tokens = 100, output_tokens = 1, cache_creation_input_tokens = 0, cache_read_input_tokens = 40 },
            },
        });
    }

    private void Event(string name, object data) => _sb.Append("event: ").Append(name).Append('\n').Append("data: ").Append(JsonSerializer.Serialize(data)).Append("\n\n");

    public Sse Text(params string[] chunks)
    {
        var i = _index++;
        Event("content_block_start", new { type = "content_block_start", index = i, content_block = new { type = "text", text = "" } });
        foreach (var c in chunks) Event("content_block_delta", new { type = "content_block_delta", index = i, delta = new { type = "text_delta", text = c } });
        Event("content_block_stop", new { type = "content_block_stop", index = i });
        return this;
    }

    public Sse ToolUse(string id, string name, string inputJson)
    {
        var i = _index++;
        Event("content_block_start", new { type = "content_block_start", index = i, content_block = new { type = "tool_use", id, name, input = new { } } });
        // Split the JSON as the API does with input_json_delta fragments.
        var half = inputJson.Length / 2;
        Event("content_block_delta", new { type = "content_block_delta", index = i, delta = new { type = "input_json_delta", partial_json = inputJson[..half] } });
        Event("content_block_delta", new { type = "content_block_delta", index = i, delta = new { type = "input_json_delta", partial_json = inputJson[half..] } });
        Event("content_block_stop", new { type = "content_block_stop", index = i });
        return this;
    }

    public string End(string stopReason, object? stopDetails = null)
    {
        Event("message_delta", new
        {
            type = "message_delta",
            delta = new { stop_reason = stopReason, stop_sequence = (string?)null, stop_details = stopDetails },
            usage = new { output_tokens = 25 },
        });
        Event("message_stop", new { type = "message_stop" });
        return _sb.ToString();
    }
}
