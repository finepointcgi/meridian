using Meridian.Scripting;
using Microsoft.JSInterop;

namespace Meridian.Web.Services;

/// <summary>One line of script console output.</summary>
public sealed record ConsoleLine(string Kind, string Text, int Line = 0);

/// <summary>
/// Per-circuit script console state (editor text, output, the running script). Lives as long as the browser tab, so the
/// draft and output survive closing the dock, and a running script keeps going while other panels are shown.
/// </summary>
public sealed class ScriptConsole(Workspace ws, ScriptEngine engine, ScriptingSettings settings, IJSRuntime js)
{
    private CancellationTokenSource? _cts;
    private DateTime _lastRender;

    public string Name { get; set; } = "Untitled";
    public string Code { get; set; } = ExampleScripts.All[0].Code;
    public int TimeoutSeconds { get; set; } = (int)Math.Round(settings.Timeout.TotalSeconds);
    public List<ConsoleLine> Output { get; } = new();
    public HashSet<int> ErrorLines { get; } = new();
    public bool Running => _cts != null;
    public ScriptingSettings Settings => settings;

    /// <summary>Raised (on the circuit) when output or state changes.</summary>
    public event Action? Changed;

    public string? DenialReason => settings.DenialReason(ws);

    public void Load(SavedScript script)
    {
        Name = script.Name;
        Code = script.Code;
        ErrorLines.Clear();
        Changed?.Invoke();
    }

    public void Cancel() => _cts?.Cancel();

    public void Clear()
    {
        Output.Clear();
        ErrorLines.Clear();
        Changed?.Invoke();
    }

    public async Task<ScriptRunResult?> RunAsync()
    {
        if (Running) return null;
        if (DenialReason is { } reason)
        {
            Output.Add(new ConsoleLine("error", reason));
            Changed?.Invoke();
            return null;
        }

        Output.Clear();
        ErrorLines.Clear();
        _cts = new CancellationTokenSource();
        Output.Add(new ConsoleLine("info", $"Running {Name}…"));
        Changed?.Invoke();
        ws.Log($"Script: running {Name}");

        var bridge = new WorkspaceBridge(ws, (name, mime, bytes) => js.InvokeVoidAsync("meridian.download", name, mime, Convert.ToBase64String(bytes)).AsTask())
        {
            OnLog = text =>
            {
                Output.Add(new ConsoleLine("log", text));
                if (Output.Count > 5000) Output.RemoveRange(0, 1000);
                // Throttle re-rendering for chatty scripts.
                if ((DateTime.UtcNow - _lastRender).TotalMilliseconds > 150) { _lastRender = DateTime.UtcNow; Changed?.Invoke(); }
            },
        };
        var api = new ModelApi(ws.Model, bridge, readOnly: ws.ReadOnly);
        ScriptRunResult result;
        try
        {
            result = await engine.RunAsync(Code, api, Name, _cts.Token, TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 3600)));
        }
        catch (Exception e)
        {
            Output.Add(new ConsoleLine("error", $"The script could not be run: {e.Message}"));
            Changed?.Invoke();
            return null;
        }
        finally
        {
            if (_cts != null) { _cts.Dispose(); _cts = null; }
        }

        foreach (var d in result.Diagnostics)
        {
            Output.Add(new ConsoleLine(d.Severity == "warning" ? "warn" : "error",
                (d.Line > 0 ? $"Line {d.Line}" + (d.Column > 0 ? $", col {d.Column}" : "") + ": " : "") + $"{d.Code}: {d.Message}", d.Line));
            if (d.Line > 0 && d.Severity == "error") ErrorLines.Add(d.Line);
        }
        if (result.Success && result.ReturnValue is { } v) Output.Add(new ConsoleLine("result", $"⇒ {v}"));
        var summary = result.Outcome switch
        {
            ScriptOutcome.Succeeded => $"Done in {result.Elapsed.TotalMilliseconds:0} ms · {result.Edits} edits{(result.Edits > 0 ? $" (undo with ⌘Z as \"Script: {Name}\")" : "")}",
            ScriptOutcome.CompileFailed => "Not run: fix the errors above",
            _ => $"{result.Outcome} after {result.Elapsed.TotalMilliseconds:0} ms" + (result.RolledBack ? $" · {result.Edits} edits rolled back" : ""),
        };
        Output.Add(new ConsoleLine(result.Success ? "info" : "error", summary));
        ws.Log($"Script: {Name}: {summary}");
        ws.SetStatus($"Script {Name}: {result.Outcome}");
        Changed?.Invoke();
        return result;
    }
}
