using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meridian.Assistant.Tools;

namespace Meridian.Assistant.Providers;

/// <summary>
/// A minimal MCP server (streamable HTTP transport, JSON responses only) that exposes Meridian's assistant tools to a
/// local Claude Code process for one turn. It listens on 127.0.0.1 only, on a random port, and every request must carry a
/// random per-session bearer token and a loopback Host header (no DNS rebinding) and no browser Origin. Tool calls are
/// handed to <c>call</c>, which runs them on the conversation's context: write tools only record proposals.
/// </summary>
public sealed class LoopbackMcpServer : IAsyncDisposable
{
    public const string ServerName = "meridian";
    private const int MaxBody = 4 * 1024 * 1024;

    private readonly IReadOnlyList<ChatTool> _tools;
    private readonly Func<ToolCallPart, CancellationToken, Task<ToolResult>> _call;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = new();
    private Task? _accept;
    private int _ids;

    public LoopbackMcpServer(IReadOnlyList<ChatTool> tools, Func<ToolCallPart, CancellationToken, Task<ToolResult>> call)
    {
        _tools = tools;
        _call = call;
        Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The bearer token clients must send.</summary>
    public string Token { get; }
    public int Port { get; private set; }
    public Uri Url => new($"http://127.0.0.1:{Port}/mcp");

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptLoop();
    }

    /// <summary>The <c>--mcp-config</c> document for Claude Code.</summary>
    public string McpConfigJson() => new JsonObject
    {
        ["mcpServers"] = new JsonObject
        {
            [ServerName] = new JsonObject
            {
                ["type"] = "http",
                ["url"] = Url.AbsoluteUri,
                ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + Token },
            },
        },
    }.ToJsonString();

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            lock (_connections) _connections.Add(Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var reader = new HttpReader(stream);
                while (!_stop.IsCancellationRequested)
                {
                    var req = await reader.ReadRequestAsync(_stop.Token);
                    if (req == null) return;
                    var (status, body, keepOpen) = await HandleAsync(req);
                    await WriteResponse(stream, status, body, keepOpen && req.KeepAlive);
                    if (!(keepOpen && req.KeepAlive)) return;
                }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException) { }
        }
    }

    internal sealed record HttpRequest(string Method, string Path, Dictionary<string, string> Headers, byte[] Body)
    {
        public bool KeepAlive => !Headers.TryGetValue("connection", out var c) || !c.Contains("close", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(int Status, string? Body, bool KeepOpen)> HandleAsync(HttpRequest req)
    {
        // Loopback Host only (DNS rebinding), no browser Origin, and the session's bearer token.
        var host = req.Headers.GetValueOrDefault("host") ?? "";
        var hostName = host.StartsWith('[') ? host[..(host.IndexOf(']') + 1)] : host.Split(':')[0];
        if (hostName is not ("127.0.0.1" or "localhost" or "[::1]")) return (403, Error(null, -32000, "Forbidden host."), false);
        if (req.Headers.ContainsKey("origin")) return (403, Error(null, -32000, "Browser requests are not allowed."), false);
        var auth = req.Headers.GetValueOrDefault("authorization") ?? "";
        var expected = Encoding.UTF8.GetBytes("Bearer " + Token);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(auth), expected)) return (401, Error(null, -32001, "Unauthorized."), false);
        if (req.Path.Split('?')[0] != "/mcp") return (404, Error(null, -32000, "Not found."), true);
        if (req.Method == "DELETE") return (200, null, true);
        if (req.Method != "POST") return (405, null, true);   // no server-initiated SSE stream

        JsonNode? root;
        try { root = JsonNode.Parse(req.Body); }
        catch (JsonException) { return (400, Error(null, -32700, "Parse error."), true); }
        if (root is JsonArray batch)
        {
            var responses = new JsonArray();
            foreach (var item in batch)
                if (item is JsonObject o && await DispatchAsync(o) is { } r) responses.Add(r);
            return responses.Count == 0 ? (202, null, true) : (200, responses.ToJsonString(), true);
        }
        if (root is not JsonObject msg) return (400, Error(null, -32600, "Invalid request."), true);
        var response = await DispatchAsync(msg);
        return response == null ? (202, null, true) : (200, response.ToJsonString(), true);
    }

    private async Task<JsonObject?> DispatchAsync(JsonObject msg)
    {
        var id = msg["id"]?.DeepClone();
        var method = msg["method"]?.GetValue<string>();
        if (id == null) return null;   // a notification (initialized, cancelled…) or a response: nothing to answer
        switch (method)
        {
            case "initialize":
                var version = (msg["params"] as JsonObject)?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18";
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = version,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "1.0" },
                    ["instructions"] = "Meridian's model tools. Write tools only propose changes; the user reviews and applies them in Meridian.",
                });
            case "ping":
                return Result(id, new JsonObject());
            case "tools/list":
                return Result(id, new JsonObject
                {
                    ["tools"] = new JsonArray(_tools.Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["inputSchema"] = t.Schema.DeepClone(),
                    }).ToArray()),
                });
            case "tools/call":
                var p = msg["params"] as JsonObject;
                var name = p?["name"]?.GetValue<string>() ?? "";
                var args = p?["arguments"];
                JsonElement? input = args is JsonObject ao ? JsonSerializer.SerializeToElement(ao) : args == null ? JsonDocument.Parse("{}").RootElement.Clone() : null;
                ToolResult result;
                if (_tools.All(t => t.Name != name)) result = ToolResult.Error($"Unknown tool '{name}'.");
                else
                {
                    try { result = await _call(new ToolCallPart("mcp" + Interlocked.Increment(ref _ids), name, input, args?.ToJsonString() ?? "{}"), _stop.Token); }
                    catch (OperationCanceledException) { result = ToolResult.Error("Cancelled."); }
                }
                return Result(id, new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Content }),
                    ["isError"] = result.IsError,
                });
            default:
                return ErrorObject(id, -32601, $"Method not found: {method}");
        }
    }

    private static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static string Error(JsonNode? id, int code, string message) => ErrorObject(id, code, message).ToJsonString();

    private static JsonObject ErrorObject(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static async Task WriteResponse(Stream stream, int status, string? body, bool keepAlive)
    {
        var bytes = body == null ? [] : Encoding.UTF8.GetBytes(body);
        var reason = status switch { 200 => "OK", 202 => "Accepted", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 413 => "Payload Too Large", _ => "Error" };
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Length: ").Append(bytes.Length).Append("\r\n")
            .Append(bytes.Length > 0 ? "Content-Type: application/json\r\n" : "")
            .Append(status == 405 ? "Allow: POST, DELETE\r\n" : "")
            .Append("Cache-Control: no-store\r\n")
            .Append(keepAlive ? "Connection: keep-alive\r\n" : "Connection: close\r\n")
            .Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
        if (bytes.Length > 0) await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    /// <summary>Reads HTTP/1.1 requests (Content-Length or chunked bodies) from one connection.</summary>
    private sealed class HttpReader(Stream stream)
    {
        private readonly byte[] _buf = new byte[16 * 1024];
        private int _start, _end;

        private async Task<int> FillAsync(CancellationToken ct)
        {
            if (_start > 0 && _start == _end) _start = _end = 0;
            if (_end == _buf.Length)
            {
                if (_start == 0) throw new InvalidDataException("Header line too long.");
                Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            var n = await stream.ReadAsync(_buf.AsMemory(_end), ct);
            _end += n;
            return n;
        }

        private async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                var i = Array.IndexOf(_buf, (byte)'\n', _start, _end - _start);
                if (i >= 0)
                {
                    var line = Encoding.ASCII.GetString(_buf, _start, i - _start).TrimEnd('\r');
                    _start = i + 1;
                    return line;
                }
                if (await FillAsync(ct) == 0) return null;
            }
        }

        private async Task ReadExactAsync(byte[] target, int offset, int count, CancellationToken ct)
        {
            while (count > 0)
            {
                if (_start == _end && await FillAsync(ct) == 0) throw new IOException("Connection closed.");
                var n = Math.Min(count, _end - _start);
                Buffer.BlockCopy(_buf, _start, target, offset, n);
                _start += n;
                offset += n;
                count -= n;
            }
        }

        public async Task<HttpRequest?> ReadRequestAsync(CancellationToken ct)
        {
            var first = await ReadLineAsync(ct);
            while (first is "") first = await ReadLineAsync(ct);
            if (first == null) return null;
            var parts = first.Split(' ');
            if (parts.Length < 3) throw new InvalidDataException("Bad request line.");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var n = 0; ; n++)
            {
                if (n > 100) throw new InvalidDataException("Too many headers.");
                var line = await ReadLineAsync(ct) ?? throw new IOException("Connection closed.");
                if (line.Length == 0) break;
                var colon = line.IndexOf(':');
                if (colon > 0) headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
            }
            byte[] body;
            if (headers.TryGetValue("transfer-encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                using var ms = new MemoryStream();
                while (true)
                {
                    var sizeLine = await ReadLineAsync(ct) ?? throw new IOException("Connection closed.");
                    var size = Convert.ToInt32(sizeLine.Split(';')[0].Trim(), 16);
                    if (size == 0) { while (await ReadLineAsync(ct) is { Length: > 0 }) { } break; }
                    if (ms.Length + size > MaxBody) throw new InvalidDataException("Body too large.");
                    var chunk = new byte[size];
                    await ReadExactAsync(chunk, 0, size, ct);
                    ms.Write(chunk);
                    await ReadLineAsync(ct);
                }
                body = ms.ToArray();
            }
            else
            {
                var length = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var l) ? l : 0;
                if (length < 0 || length > MaxBody) throw new InvalidDataException("Body too large.");
                body = new byte[length];
                await ReadExactAsync(body, 0, length, ct);
            }
            return new HttpRequest(parts[0].ToUpperInvariant(), parts[1], headers, body);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        if (_accept != null) await _accept;
        Task[] open;
        lock (_connections) open = _connections.ToArray();
        try { await Task.WhenAll(open).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { }
        _stop.Dispose();
    }
}
