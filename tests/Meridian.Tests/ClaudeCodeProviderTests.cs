using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meridian.Assistant.Engine;
using Meridian.Assistant.Providers;
using Meridian.Assistant.Tools;
using Meridian.Core.Model;
using Meridian.Core.Samples;

namespace Meridian.Tests;

/// <summary>
/// The Claude Code provider against a fake <c>claude</c> script that records its arguments and emits recorded
/// stream-json (the real CLI is never run), and the loopback MCP server that exposes Meridian's tools to it.
/// </summary>
public sealed class ClaudeCodeProviderTests : IDisposable
{
    private readonly ModelRepository _m = SampleModels.Vehicle();
    private readonly string _dir = Directory.CreateTempSubdirectory("meridian-fake-claude-").FullName;
    private string Context => Prompts.Context(_m, "Vehicle", _m.OfKind(ElementKind.Block).First(), _m.Diagrams.First(), readOnly: false);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- recorded stream-json

    private static string Ev(object o) => JsonSerializer.Serialize(o) + "\n";

    private static string Init() => Ev(new { type = "system", subtype = "init", session_id = "s1", tools = new[] { "mcp__meridian__search_elements" }, mcp_servers = new[] { new { name = "meridian", status = "connected" } } });

    private static string TextBlock(int index, params string[] chunks)
    {
        var sb = new StringBuilder(Ev(new { type = "stream_event", @event = new { type = "content_block_start", index, content_block = new { type = "text", text = "" } } }));
        foreach (var c in chunks) sb.Append(Ev(new { type = "stream_event", @event = new { type = "content_block_delta", index, delta = new { type = "text_delta", text = c } } }));
        sb.Append(Ev(new { type = "stream_event", @event = new { type = "content_block_stop", index } }));
        return sb.ToString();
    }

    private static string Result(string text, bool isError = false, string subtype = "success") =>
        Ev(new { type = "result", subtype, is_error = isError, result = text, usage = new { input_tokens = 50, output_tokens = 20, cache_read_input_tokens = 10, cache_creation_input_tokens = 0 } });

    /// <summary>A fake <c>claude</c>: records argv, cwd, stdin and the MCP config, then runs <paramref name="body"/>.</summary>
    private string FakeClaude(string body)
    {
        var path = Path.Combine(_dir, "claude");
        File.WriteAllText(path, $$"""
            #!/bin/sh
            dir="{{_dir}}"
            printf '%s\n' "$@" > "$dir/args.txt"
            pwd > "$dir/cwd.txt"
            ls -A > "$dir/cwd-files.txt"
            cat > "$dir/stdin.txt"
            cfg=""; prev=""
            for a in "$@"; do if [ "$prev" = "--mcp-config" ]; then cfg="$a"; fi; prev="$a"; done
            echo "$cfg" > "$dir/cfg-path.txt"
            cp "$cfg" "$dir/cfg.json" 2>/dev/null
            {{body}}
            """.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private string Recorded(string name, string content)
    {
        File.WriteAllText(Path.Combine(_dir, name), content);
        return $"cat \"$dir/{name}\"";
    }

    private static AssistantEngine Engine(string path, string? model = null) => new(new ClaudeCodeProvider(new ClaudeCodeOptions { ExecutablePath = path, Model = model, MaxTurns = 7 }));

    // ---------------------------------------------------------------- tests

    [SkippableFact]
    public async Task Streams_Text_With_Only_Meridian_Tools_In_An_Empty_Folder()
    {
        Skip.If(OperatingSystem.IsWindows(), "The fake claude is a shell script.");
        var script = FakeClaude(Recorded("out.jsonl", Init() + TextBlock(0, "The vehicle has ", "**4** blocks.") + Result("The vehicle has **4** blocks.")));
        var conv = new Conversation();
        await Engine(script, "claude-sonnet-5-5").RunTurnAsync(conv, _m, new TurnRequest("How many blocks?", Context), () => { });

        Assert.Empty(conv.Items.OfType<NoticeItem>());
        Assert.Equal("The vehicle has **4** blocks.", Assert.Single(conv.Items.OfType<AssistantTextItem>()).Text);
        Assert.Equal(2, conv.History.Count);
        Assert.Equal(50, conv.InputTokens);

        var args = File.ReadAllLines(Path.Combine(_dir, "args.txt"));
        string After(string flag) => args[Array.IndexOf(args, flag) + 1];
        Assert.Equal("-p", args[0]);
        Assert.Equal("stream-json", After("--output-format"));
        Assert.Equal("stream-json", After("--input-format"));
        Assert.Contains("--verbose", args);
        Assert.Equal("", After("--tools"));                          // no built-in tools
        Assert.Equal("dontAsk", After("--permission-mode"));         // never prompts
        Assert.Contains("--strict-mcp-config", args);
        var allowed = After("--allowedTools").Split(',');
        Assert.Equal(Toolbox.All.Count, allowed.Length);
        Assert.All(allowed, a => Assert.StartsWith("mcp__meridian__", a));
        var disallowed = After("--disallowedTools").Split(',');
        Assert.Contains("Bash", disallowed);
        Assert.Contains("Write", disallowed);
        Assert.Contains("Edit", disallowed);
        Assert.Contains("Read", disallowed);
        Assert.Contains("WebFetch", disallowed);
        Assert.Contains("WebSearch", disallowed);
        Assert.Equal("7", After("--max-turns"));
        Assert.Equal("claude-sonnet-5-5", After("--model"));
        Assert.StartsWith(Prompts.System.Split('\n')[0], After("--append-system-prompt"));

        // An empty temporary working folder; the MCP config (with its token) is elsewhere and deleted afterwards.
        var cwd = File.ReadAllText(Path.Combine(_dir, "cwd.txt")).Trim();
        Assert.Contains("meridian-claude-", cwd);
        Assert.Equal("", File.ReadAllText(Path.Combine(_dir, "cwd-files.txt")).Trim());
        Assert.False(Directory.Exists(cwd));
        var cfgPath = File.ReadAllText(Path.Combine(_dir, "cfg-path.txt")).Trim();
        Assert.False(File.Exists(cfgPath));
        var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "cfg.json")))!["mcpServers"]!["meridian"]!;
        Assert.Equal("http", cfg["type"]!.GetValue<string>());
        Assert.StartsWith("http://127.0.0.1:", cfg["url"]!.GetValue<string>());
        Assert.StartsWith("Bearer ", cfg["headers"]!["Authorization"]!.GetValue<string>());

        // The question goes in as one stream-json user message with the workbench context.
        var input = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "stdin.txt")))!;
        Assert.Equal("user", input["type"]!.GetValue<string>());
        var text = input["message"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("<context>", text);
        Assert.Contains("How many blocks?", text);
    }

    [SkippableFact]
    public async Task Tool_Calls_Go_Through_Mcp_And_Become_A_Change_Set_Card()
    {
        Skip.If(OperatingSystem.IsWindows(), "The fake claude is a shell script.");
        Skip.If(!File.Exists("/usr/bin/curl"), "curl is needed by the fake claude.");
        var structure = _m.Root.OwnedElements.Single(e => e.Name == "Structure");
        File.WriteAllText(Path.Combine(_dir, "call.json"), JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 3, method = "tools/call",
            @params = new { name = "create_element", arguments = new { kind = "Block", owner_id = structure.Id, name = "Gearbox" } },
        }));
        var before = Recorded("before.jsonl", Init() + TextBlock(0, "Proposing a gearbox."));
        var after = Recorded("after.jsonl", TextBlock(2, "Proposed **Gearbox**; review it in Meridian.") + Result("Proposed **Gearbox**; review it in Meridian."));
        var script = FakeClaude($$"""
            {{before}}
            url=$(sed -n 's/.*"url":"\([^"]*\)".*/\1/p' "$cfg")
            auth=$(sed -n 's/.*"Authorization":"\([^"]*\)".*/\1/p' "$cfg")
            /usr/bin/curl -s -X POST -H "Authorization: $auth" -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" --data @"$dir/call.json" "$url" > "$dir/call-result.json"
            /usr/bin/curl -s -o /dev/null -w '%{http_code}' -X POST -H "Content-Type: application/json" --data @"$dir/call.json" "$url" > "$dir/no-token.txt"
            {{after}}
            """);
        var conv = new Conversation();
        var count = _m.AllElements.Count();
        await Engine(script).RunTurnAsync(conv, _m, new TurnRequest("Add a gearbox", Context, AssistantEffort.High), () => { });

        Assert.Empty(conv.Items.OfType<NoticeItem>());
        Assert.Equal(count, _m.AllElements.Count());   // proposed, never applied
        var card = Assert.Single(conv.Items.OfType<ChangeSetItem>());
        Assert.Equal("Gearbox", card.Changes.Items.Single().Name);
        Assert.Single(conv.Items.OfType<ToolActivityItem>(), t => t.Tool == "create_element" && !t.IsError);
        var result = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "call-result.json")))!;
        Assert.Equal(3, result["id"]!.GetValue<int>());
        Assert.False(result["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("new:1", result["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("401", File.ReadAllText(Path.Combine(_dir, "no-token.txt")).Trim());
        Assert.Contains("high", File.ReadAllLines(Path.Combine(_dir, "args.txt")));   // --effort high for quick actions
        Assert.Equal(["Proposing a gearbox.", "Proposed **Gearbox**; review it in Meridian."], conv.Items.OfType<AssistantTextItem>().Select(t => t.Text));
    }

    [SkippableFact]
    public async Task Not_Logged_In_Missing_Cli_And_Cancel_Are_Reported()
    {
        Skip.If(OperatingSystem.IsWindows(), "The fake claude is a shell script.");
        var script = FakeClaude(Recorded("auth.jsonl", Init() + Result("Invalid API key · Please run /login", isError: true)));
        var conv = new Conversation();
        await Engine(script).RunTurnAsync(conv, _m, new TurnRequest("Hello", Context), () => { });
        Assert.Equal(ClaudeCodeProvider.NotLoggedIn, Assert.Single(conv.Items.OfType<NoticeItem>()).Text);
        Assert.Empty(conv.History);

        conv = new Conversation();
        await Engine(Path.Combine(_dir, "no-such-claude")).RunTurnAsync(conv, _m, new TurnRequest("Hello", Context), () => { });
        Assert.Equal(ClaudeCodeProvider.NotInstalled, Assert.Single(conv.Items.OfType<NoticeItem>()).Text);

        script = FakeClaude(Recorded("slow.jsonl", Init()) + "\nsleep 30\n");
        conv = new Conversation();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        var watch = Stopwatch.StartNew();
        await Engine(script).RunTurnAsync(conv, _m, new TurnRequest("Hello", Context), () => { }, cts.Token);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal("Stopped.", Assert.Single(conv.Items.OfType<NoticeItem>()).Text);
        Assert.False(conv.Busy);
    }

    [Fact]
    public async Task Mcp_Server_Lists_And_Calls_Tools_And_Rejects_Strangers()
    {
        var calls = new List<ToolCallPart>();
        await using var server = new LoopbackMcpServer(ChatTool.Toolbox, (use, _) =>
        {
            calls.Add(use);
            return Task.FromResult(ToolResult.Ok(new { ok = true, tool = use.Name }));
        });
        server.Start();
        using var http = new HttpClient();

        async Task<(HttpStatusCode Status, JsonNode? Body)> Post(object payload, string? token, string? host = null, bool origin = false)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, server.Url) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
            if (token != null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            if (host != null) req.Headers.Host = host;
            if (origin) req.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
            using var res = await http.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            return (res.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
        }

        var init = await Post(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } } }, server.Token);
        Assert.Equal(HttpStatusCode.OK, init.Status);
        Assert.Equal("meridian", init.Body!["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Accepted, (await Post(new { jsonrpc = "2.0", method = "notifications/initialized" }, server.Token)).Status);

        var list = await Post(new { jsonrpc = "2.0", id = 2, method = "tools/list" }, server.Token);
        var tools = list.Body!["result"]!["tools"]!.AsArray();
        Assert.Equal(Toolbox.All.Count, tools.Count);
        Assert.Equal("object", tools[0]!["inputSchema"]!["type"]!.GetValue<string>());

        var call = await Post(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "search_elements", arguments = new { query = "kind:Block" } } }, server.Token);
        Assert.Contains("search_elements", call.Body!["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("kind:Block", Assert.Single(calls).Input!.Value.GetProperty("query").GetString());

        var unknown = await Post(new { jsonrpc = "2.0", id = 4, method = "tools/call", @params = new { name = "Bash", arguments = new { command = "rm -rf /" } } }, server.Token);
        Assert.True(unknown.Body!["result"]!["isError"]!.GetValue<bool>());
        Assert.Single(calls);   // never reached the tools

        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(new { jsonrpc = "2.0", id = 5, method = "tools/list" }, null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(new { jsonrpc = "2.0", id = 5, method = "tools/list" }, "wrong-token")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(new { jsonrpc = "2.0", id = 6, method = "tools/list" }, server.Token, host: "evil.example")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(new { jsonrpc = "2.0", id = 7, method = "tools/list" }, server.Token, origin: true)).Status);
        Assert.Equal(-32601, (await Post(new { jsonrpc = "2.0", id = 8, method = "resources/list" }, server.Token)).Body!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void Input_Message_Carries_Earlier_Turns_As_A_Transcript()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextPart("<context>old</context>"), new TextPart("What is the engine?")]),
            new(ChatRole.Assistant, [new TextPart("A block.")]),
            new(ChatRole.User, [new TextPart("<context>new</context>"), new TextPart("And the gearbox?")]),
        };
        var text = JsonNode.Parse(ClaudeCodeProvider.InputMessage(history))!["message"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("<conversation_so_far>", text);
        Assert.Contains("User: What is the engine?", text);
        Assert.Contains("Assistant: A block.", text);
        Assert.DoesNotContain("<context>old</context>", text);
        Assert.EndsWith("<context>new</context>\n\nAnd the gearbox?", text);
    }

    [Fact]
    public void Locator_Uses_The_Override_Only_And_Knows_The_Usual_Places()
    {
        Assert.Equal(["/custom/claude"], ClaudeCodeLocator.Candidates("/custom/claude"));
        var all = ClaudeCodeLocator.Candidates().ToList();
        if (OperatingSystem.IsWindows()) Assert.Contains(all, p => p.EndsWith(Path.Combine("npm", "claude.cmd"), StringComparison.OrdinalIgnoreCase));
        else
        {
            Assert.Contains(all, p => p.EndsWith(Path.Combine(".claude", "local", "claude"), StringComparison.Ordinal));
            Assert.Contains("/opt/homebrew/bin/claude", all);
        }
    }
}
