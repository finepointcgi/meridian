using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Meridian.Assistant.Engine;
using Meridian.Assistant.Tools;

namespace Meridian.Assistant.Providers;

/// <summary>Settings for the Claude Code provider (desktop / local mode only).</summary>
public sealed record ClaudeCodeOptions
{
    /// <summary>The <c>claude</c> executable (see <see cref="ClaudeCodeLocator"/>).</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>A model for <c>--model</c>; null lets Claude Code use its own default.</summary>
    public string? Model { get; init; }

    /// <summary><c>--max-turns</c>: model ↔ tool round trips in one turn.</summary>
    public int MaxTurns { get; init; } = 24;

    /// <summary>How long Claude Code may stay silent (no output, no tool call) before the turn is stopped.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>An installed Claude Code CLI.</summary>
public sealed record ClaudeCodeInstall(string Path, string Version);

/// <summary>Finds the <c>claude</c> CLI and checks its version and login without spending tokens.</summary>
public static class ClaudeCodeLocator
{
    /// <summary>Candidate locations: the override, PATH, then the usual install folders.</summary>
    public static IEnumerable<string> Candidates(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) { yield return overridePath.Trim(); yield break; }
        var names = OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd" } : ["claude"];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in names) yield return Path.Combine(dir.Trim('"'), n);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            yield return Path.Combine(appData, "npm", "claude.cmd");
            yield return Path.Combine(home, ".local", "bin", "claude.exe");
            yield return Path.Combine(home, ".claude", "local", "claude.exe");
        }
        else
        {
            yield return Path.Combine(home, ".claude", "local", "claude");
            yield return Path.Combine(home, ".local", "bin", "claude");
            yield return "/opt/homebrew/bin/claude";
            yield return "/usr/local/bin/claude";
            yield return Path.Combine(home, ".npm-global", "bin", "claude");
        }
    }

    /// <summary>The first candidate that exists and answers <c>claude --version</c>; null when Claude Code is not installed.</summary>
    public static async Task<ClaudeCodeInstall?> DetectAsync(string? overridePath = null, CancellationToken ct = default)
    {
        foreach (var path in Candidates(overridePath).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            if (!File.Exists(path)) continue;
            var (code, output) = await RunAsync(path, ["--version"], TimeSpan.FromSeconds(10), ct);
            if (code == 0 && output.Trim() is { Length: > 0 } v) return new ClaudeCodeInstall(path, v.Split('\n')[0].Trim());
        }
        return null;
    }

    /// <summary><c>claude auth status --json</c>: true/false when it says, null when it can't tell (the first run reports it then).</summary>
    public static async Task<bool?> IsLoggedInAsync(string path, CancellationToken ct = default)
    {
        var (code, output) = await RunAsync(path, ["auth", "status", "--json"], TimeSpan.FromSeconds(10), ct);
        try
        {
            var start = output.IndexOf('{');
            if (start >= 0)
            {
                using var doc = JsonDocument.Parse(output[start..]);
                foreach (var key in (string[])["loggedIn", "logged_in", "authenticated"])
                    if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
            }
        }
        catch (JsonException) { }
        if (output.Contains("not logged in", StringComparison.OrdinalIgnoreCase) || (code != 0 && output.Contains("login", StringComparison.OrdinalIgnoreCase))) return false;
        return null;
    }

    /// <summary>A start descriptor for the CLI (a Windows <c>.cmd</c> shim goes through <c>cmd.exe</c>).</summary>
    internal static ProcessStartInfo StartInfo(string path, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                                         StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        if (OperatingSystem.IsWindows() && path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(path);
        }
        else psi.FileName = path;
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static async Task<(int Code, string Output)> RunAsync(string path, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(StartInfo(path, args));
            if (p == null) return (-1, "");
            p.StandardInput.Close();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = p.StandardError.ReadToEndAsync(cts.Token);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } return (-1, ""); }
            return (p.ExitCode, (await stdout) + (await stderr));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or OperationCanceledException) { return (-1, ""); }
    }
}

/// <summary>
/// Claude through the user's own Claude Code install and login (desktop / local mode). Each turn runs
/// <c>claude -p</c> headless with stream-json in and out, in an empty temporary folder, with every built-in tool turned off
/// (<c>--tools ""</c> plus <c>--disallowedTools</c>) and only Meridian's tools allowed, served over a loopback MCP endpoint
/// (<see cref="LoopbackMcpServer"/>, random port and bearer token, config in a temp file deleted afterwards). Claude Code
/// drives the tool loop; each tool call comes back through <see cref="ChatRequest.ExecuteTool"/>, so write tools still only
/// add proposals to the turn's change-set card. Text deltas stream into the panel; Stop kills the process tree.
/// </summary>
public sealed class ClaudeCodeProvider(ClaudeCodeOptions options) : IChatProvider
{
    /// <summary>Claude Code's own tools that must never run inside Meridian.</summary>
    public static readonly string[] DisallowedTools =
        ["Bash", "BashOutput", "KillShell", "Edit", "MultiEdit", "Write", "Read", "NotebookEdit", "Glob", "Grep", "LS", "WebFetch", "WebSearch", "Task", "TodoWrite", "Agent", "PowerShell"];

    public ClaudeCodeOptions Options => options;
    public ChatProviderInfo Info => new(ChatProviderKind.ClaudeCode, options.Model is { Length: > 0 } m ? m : "Claude Code default");
    public bool RunsToolLoop => true;

    internal const string Guidance = """

        <meridian_host>
        You are running inside Meridian through Claude Code. The tools named in this prompt are Meridian's MCP tools: search_elements is
        mcp__meridian__search_elements, create_element is mcp__meridian__create_element, and so on. You have no file system, shell or web
        access here and must not try other tools. Write tools only propose changes for the user to review in Meridian.
        </meridian_host>
        """;

    public static string McpToolName(string tool) => $"mcp__{LoopbackMcpServer.ServerName}__{tool}";

    /// <summary>The command-line arguments for one turn.</summary>
    /// <param name="systemPromptFile">When set, the prompt is passed as a file (a Windows <c>.cmd</c> shim can't carry newlines).</param>
    internal IReadOnlyList<string> Arguments(ChatRequest request, string mcpConfigPath, string systemPrompt, string? systemPromptFile = null)
    {
        var args = new List<string>
        {
            "-p", "--output-format", "stream-json", "--input-format", "stream-json", "--verbose", "--include-partial-messages",
            "--no-session-persistence",
            "--strict-mcp-config", "--mcp-config", mcpConfigPath,
            "--tools", "",                                                     // no built-in tools at all
            "--allowedTools", string.Join(",", request.Tools.Select(t => McpToolName(t.Name))),
            "--disallowedTools", string.Join(",", DisallowedTools),
            "--permission-mode", "dontAsk",                                    // never prompt; anything not allowed is denied
            systemPromptFile == null ? "--append-system-prompt" : "--append-system-prompt-file", systemPromptFile ?? systemPrompt,
            "--max-turns", options.MaxTurns.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (options.Model is { Length: > 0 } model) { args.Add("--model"); args.Add(model); }
        if (request.Effort == AssistantEffort.High) { args.Add("--effort"); args.Add("high"); }
        return args;
    }

    /// <summary>
    /// The single stream-json user message: earlier turns as a compact transcript (each run starts a fresh session),
    /// then the current context and question.
    /// </summary>
    internal static string InputMessage(IReadOnlyList<ChatMessage> history)
    {
        var sb = new StringBuilder();
        var last = history.Count - 1;
        while (last >= 0 && (history[last].Role != ChatRole.User || history[last].ToolResults.Any())) last--;
        if (last > 0)
        {
            sb.AppendLine("<conversation_so_far>");
            for (var i = 0; i < last; i++)
            {
                var m = history[i];
                foreach (var part in m.Parts)
                    switch (part)
                    {
                        case TextPart t when m.Role == ChatRole.User && t.Text.StartsWith("<context>", StringComparison.Ordinal): break;   // old context is stale
                        case TextPart t: sb.Append(m.Role == ChatRole.User ? "User: " : "Assistant: ").AppendLine(t.Text); break;
                        case ToolCallPart c: sb.Append("Assistant called ").Append(c.Name).Append(' ').AppendLine(ToolContext.Truncate(c.RawArguments, 300)); break;
                        case ToolResultPart r: sb.Append("Result of ").Append(r.Name).Append(r.IsError ? " (error): " : ": ").AppendLine(ToolContext.Truncate(r.Content, 600)); break;
                    }
            }
            sb.AppendLine("</conversation_so_far>");
            sb.AppendLine();
        }
        if (last >= 0) sb.Append(string.Join("\n\n", history[last].Parts.OfType<TextPart>().Select(p => p.Text)));
        return new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = sb.ToString() }),
            },
        }.ToJsonString();
    }

    private abstract record Item;
    private sealed record Line(string Text) : Item;
    private sealed record Call(ToolCallPart Use, TaskCompletionSource<ToolResult> Done) : Item;
    private sealed record Exited : Item;

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, IChatStream stream, CancellationToken ct)
    {
        if (!File.Exists(options.ExecutablePath)) throw new ChatProviderException(NotInstalled);
        var items = Channel.CreateUnbounded<Item>();
        await using var mcp = new LoopbackMcpServer(request.Tools, (use, token) =>
        {
            var done = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => done.TrySetCanceled());
            if (!items.Writer.TryWrite(new Call(use, done))) done.TrySetResult(ToolResult.Error("The turn has ended."));
            return done.Task;
        });
        mcp.Start();

        var work = Directory.CreateTempSubdirectory("meridian-claude-");          // empty working folder for the CLI
        var configDir = Directory.CreateTempSubdirectory("meridian-mcp-");        // the token never sits in the working folder
        var configPath = Path.Combine(configDir.FullName, "mcp.json");
        await File.WriteAllTextAsync(configPath, mcp.McpConfigJson(), ct);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Process? process = null;
        var stderr = new StringBuilder();
        try
        {
            var system = request.System + Guidance;
            string? systemFile = null;
            if (OperatingSystem.IsWindows() && options.ExecutablePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                systemFile = Path.Combine(configDir.FullName, "system.txt");
                await File.WriteAllTextAsync(systemFile, system, ct);
            }
            var psi = ClaudeCodeLocator.StartInfo(options.ExecutablePath, Arguments(request, configPath, system, systemFile));
            psi.WorkingDirectory = work.FullName;
            stream.Status("Starting Claude Code…");
            try { process = Process.Start(psi) ?? throw new ChatProviderException(NotInstalled); }
            catch (System.ComponentModel.Win32Exception e) { throw new ChatProviderException(NotInstalled, e); }

            await process.StandardInput.WriteLineAsync(InputMessage(request.Messages).AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            process.StandardInput.Close();

            var p = process;
            var reader = Task.Run(async () =>
            {
                try
                {
                    while (await p.StandardOutput.ReadLineAsync() is { } line) items.Writer.TryWrite(new Line(line));
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
                try { await p.WaitForExitAsync(); } catch (InvalidOperationException) { }
                items.Writer.TryWrite(new Exited());
            }, CancellationToken.None);
            var errors = Task.Run(async () =>
            {
                try
                {
                    while (await p.StandardError.ReadLineAsync() is { } line)
                        lock (stderr) { if (stderr.Length < 8000) stderr.AppendLine(line); }
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
            }, CancellationToken.None);

            var state = new RunState();
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(options.IdleTimeout);
            try
            {
                await foreach (var item in items.Reader.ReadAllAsync(idle.Token))
                {
                    idle.CancelAfter(options.IdleTimeout);
                    if (item is Exited) break;
                    if (item is Call call)
                    {
                        // Runs on the caller's (circuit's) context: the engine validates the input and records proposals.
                        stream.EndText();
                        call.Done.TrySetResult(request.ExecuteTool?.Invoke(call.Use) ?? ToolResult.Error("Tools are not available."));
                        continue;
                    }
                    if (item is Line l) Handle(l.Text, state, stream);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ChatProviderException($"Claude Code stopped responding (nothing for {(int)options.IdleTimeout.TotalMinutes} minutes).");
            }
            items.Writer.TryComplete();
            await Task.WhenAny(errors, Task.Delay(500, CancellationToken.None));
            stream.EndText();
            stream.Status(null);

            string err;
            lock (stderr) err = stderr.ToString();
            if (state.Result is not { } result)
            {
                var code = process.HasExited ? process.ExitCode : -1;
                throw new ChatProviderException(LoginProblem(err) ? NotLoggedIn
                    : $"Claude Code ended without an answer (exit code {code})." + (err.Trim() is { Length: > 0 } e ? " " + Short(e) : ""));
            }
            if (result.IsError)
            {
                if (LoginProblem(result.Text) || LoginProblem(err)) throw new ChatProviderException(NotLoggedIn);
                if (result.Subtype == "error_max_turns")
                    return Finish(state, ChatStopReason.EndTurn, $"Claude Code stopped after {options.MaxTurns} turns. Ask again to continue.");
                throw new ChatProviderException("Claude Code failed: " + Short(result.Text.Length > 0 ? result.Text : result.Subtype ?? err));
            }
            return Finish(state, state.Refused ? ChatStopReason.Refusal : state.MaxTokens ? ChatStopReason.MaxTokens : ChatStopReason.EndTurn, null);
        }
        finally
        {
            items.Writer.TryComplete();
            while (items.Reader.TryRead(out var left))
                if (left is Call c) c.Done.TrySetResult(ToolResult.Error("The turn has ended."));
            if (process != null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                process.Dispose();
            }
            TryDelete(configDir);
            TryDelete(work);
        }
    }

    private static ChatResponse Finish(RunState s, ChatStopReason stop, string? warning) => new()
    {
        Stop = stop,
        Message = s.Answer.Length > 0 ? new ChatMessage(ChatRole.Assistant, [new TextPart(s.Answer.ToString().Trim())]) : null,
        Usage = s.Usage,
        Warning = warning,
    };

    private sealed class RunState
    {
        public readonly StringBuilder Answer = new();
        public bool SawPartial, InText, Refused, MaxTokens;
        public ChatUsage Usage = ChatUsage.None;
        public (bool IsError, string? Subtype, string Text)? Result;
    }

    /// <summary>Reads one stream-json event.</summary>
    private static void Handle(string line, RunState s, IChatStream stream)
    {
        JsonNode? ev;
        try { ev = JsonNode.Parse(line); }
        catch (JsonException) { return; }
        if (ev is not JsonObject o) return;
        switch (Str(o["type"]))
        {
            case "system" when Str(o["subtype"]) == "init":
                stream.Status("Claude Code is thinking…");
                break;
            case "stream_event" when o["event"] is JsonObject e:
                s.SawPartial = true;
                switch (Str(e["type"]))
                {
                    case "content_block_start":
                        var block = e["content_block"] as JsonObject;
                        var kind = Str(block?["type"]);
                        if (kind == "tool_use") stream.Status("Calling " + Str(block?["name"])?.Replace($"mcp__{LoopbackMcpServer.ServerName}__", "") + "…");
                        else if (kind is "thinking" or "redacted_thinking") stream.Status("Thinking…");
                        else if (kind == "text" && s.Answer.Length > 0) s.Answer.Append("\n\n");
                        break;
                    case "content_block_delta" when e["delta"] is JsonObject d && Str(d["type"]) == "text_delta":
                        var text = Str(d["text"]) ?? "";
                        s.InText = true;
                        s.Answer.Append(text);
                        stream.Text(text);
                        break;
                    case "content_block_stop":
                        if (s.InText) stream.EndText();
                        s.InText = false;
                        break;
                    case "message_delta" when e["delta"] is JsonObject md:
                        var stop = Str(md["stop_reason"]);
                        if (stop == "refusal") s.Refused = true;
                        if (stop == "max_tokens") s.MaxTokens = true;
                        if (stop is "end_turn" or "tool_use") s.MaxTokens = false;
                        break;
                }
                break;
            case "assistant" when !s.SawPartial && (o["message"]?["content"] is JsonArray content):
                // A CLI without partial messages: show each complete text block.
                foreach (var b in content.OfType<JsonObject>().Where(b => Str(b["type"]) == "text"))
                {
                    var t = Str(b["text"]) ?? "";
                    if (s.Answer.Length > 0) s.Answer.Append("\n\n");
                    s.Answer.Append(t);
                    stream.Text(t);
                    stream.EndText();
                }
                break;
            case "result":
                var usage = o["usage"] as JsonObject;
                s.Usage = new ChatUsage(Long(usage?["input_tokens"]) + Long(usage?["cache_creation_input_tokens"]), Long(usage?["output_tokens"]), Long(usage?["cache_read_input_tokens"]));
                var isError = o["is_error"] is JsonValue v && v.TryGetValue<bool>(out var b2) && b2;
                var subtype = Str(o["subtype"]);
                s.Result = (isError || (subtype != null && subtype != "success"), subtype, Str(o["result"]) ?? "");
                break;
        }
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static long Long(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;

    private static bool LoginProblem(string text) =>
        text.Contains("/login", StringComparison.OrdinalIgnoreCase) || text.Contains("not logged in", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("invalid api key", StringComparison.OrdinalIgnoreCase) || text.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("oauth token", StringComparison.OrdinalIgnoreCase);

    public const string NotInstalled =
        "Claude Code isn't installed (the `claude` command was not found). Install it from https://claude.com/claude-code " +
        "(for example `npm install -g @anthropic-ai/claude-code`), sign in by running `claude` once in a terminal, then try again — " +
        "or set its path in Settings ▸ AI assistant.";

    public const string NotLoggedIn =
        "Claude Code isn't signed in. Open a terminal, run `claude` (or `claude auth login`) and sign in, then try again.";

    private static string Short(string s) => s.Length <= 400 ? s.Trim() : s[..400].Trim() + "…";

    private static void TryDelete(DirectoryInfo dir)
    {
        try { dir.Delete(recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public string? DescribeError(Exception e) => e switch
    {
        ChatProviderException => e.Message,
        System.ComponentModel.Win32Exception => NotInstalled,
        IOException => "Lost the connection to Claude Code: " + Short(e.Message),
        _ => null,
    };
}
