using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Meridian.Scripting;

/// <summary>A compile error, policy violation or runtime error, with a 1-based line in the user's script (0 = unknown).</summary>
public sealed record ScriptDiagnostic(int Line, int Column, string Severity, string Code, string Message)
{
    public override string ToString() => Line > 0 ? $"({Line},{Column}) {Severity} {Code}: {Message}" : $"{Severity} {Code}: {Message}";
}

public enum ScriptOutcome { Succeeded, CompileFailed, Failed, Cancelled, TimedOut, Refused }

public sealed record ScriptRunResult(
    ScriptOutcome Outcome,
    object? ReturnValue,
    IReadOnlyList<ScriptDiagnostic> Diagnostics,
    TimeSpan Elapsed,
    int Edits,
    bool RolledBack,
    bool Abandoned = false)
{
    public bool Success => Outcome == ScriptOutcome.Succeeded;
}

/// <summary>Engine settings. Defaults are the safe ones.</summary>
public sealed record ScriptEngineOptions
{
    /// <summary>Wall-clock limit per run.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Skip the namespace/type deny list (System.IO, Process, reflection…). Off by default.</summary>
    public bool Unrestricted { get; init; }

    /// <summary>How long to wait for a cancelled script to stop before abandoning its thread.</summary>
    public TimeSpan CancelGrace { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Compiles and runs C# scripts (Roslyn scripting) against an <see cref="IModelApi"/>.
/// <para><b>Not a sandbox.</b> A script runs inside the host process with its permissions. See docs/SCRIPTING.md.</para>
/// </summary>
public sealed class ScriptEngine
{
    public const string ScriptFileName = "script.csx";

    public static readonly IReadOnlyList<string> DefaultImports =
    [
        "System", "System.Linq", "System.Text", "System.Collections.Generic", "System.Globalization",
        "System.Text.RegularExpressions", "Meridian.Scripting",
    ];

    public ScriptEngine(ScriptEngineOptions? options = null)
    {
        Settings = options ?? new ScriptEngineOptions();
        Options = BuildOptions();
    }

    public ScriptEngineOptions Settings { get; }
    public ScriptOptions Options { get; }

    private static ScriptOptions BuildOptions()
    {
        var refs = new[]
        {
            typeof(object).Assembly,                       // System.Private.CoreLib
            Assembly.Load("System.Runtime"),
            typeof(Enumerable).Assembly,                   // System.Linq
            typeof(HashSet<>).Assembly,                    // System.Collections
            typeof(Regex).Assembly,                        // System.Text.RegularExpressions
            typeof(IModelApi).Assembly,                    // Meridian.Scripting (the API)
        }.Distinct();
        return ScriptOptions.Default
            .WithReferences(refs)
            .WithImports(DefaultImports)
            .WithMetadataResolver(ScriptMetadataResolver.Default.WithSearchPaths(Array.Empty<string>()).WithBaseDirectory(null))
            .WithSourceResolver(new SourceFileResolver(Array.Empty<string>(), null))
            .WithAllowUnsafe(false)
            .WithCheckOverflow(false)
            .WithOptimizationLevel(OptimizationLevel.Debug)
            .WithEmitDebugInformation(true)
            .WithFilePath(ScriptFileName)
            .WithFileEncoding(Encoding.UTF8)
            .WithLanguageVersion(LanguageVersion.Latest);
    }

    /// <summary>Compiles (and policy-checks) without running. Returns errors and warnings.</summary>
    public IReadOnlyList<ScriptDiagnostic> Check(string code) => Prepare(code).Diagnostics;

    private (Script<object>? Script, IReadOnlyList<ScriptDiagnostic> Diagnostics) Prepare(string code)
    {
        var parse = CSharpParseOptions.Default.WithKind(SourceCodeKind.Script).WithLanguageVersion(LanguageVersion.Latest);
        var tree = CSharpSyntaxTree.ParseText(code, parse, ScriptFileName, Encoding.UTF8);
        var syntaxErrors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
            .Concat(ScriptPolicy.CheckSyntax(tree)).ToList();
        if (syntaxErrors.Count > 0) return (null, syntaxErrors.Select(Map).ToList());

        var rewritten = new CancellationInjector().Visit(tree.GetRoot())!.ToFullString();
        var script = CSharpScript.Create<object>(rewritten, Options, typeof(ScriptGlobals));
        var diagnostics = script.Compile().Where(d => d.Severity >= DiagnosticSeverity.Warning).ToList();
        if (diagnostics.All(d => d.Severity != DiagnosticSeverity.Error) && !Settings.Unrestricted)
            diagnostics.AddRange(ScriptPolicy.CheckSemantics(script.GetCompilation()));
        var mapped = diagnostics.Select(Map).OrderBy(d => d.Line).ToList();
        return (mapped.Any(d => d.Severity == "error") ? null : script, mapped);
    }

    private static ScriptDiagnostic Map(Diagnostic d)
    {
        var span = d.Location.IsInSource ? d.Location.GetLineSpan().StartLinePosition : default;
        return new ScriptDiagnostic(d.Location.IsInSource ? span.Line + 1 : 0, d.Location.IsInSource ? span.Character + 1 : 0,
            d.Severity.ToString().ToLowerInvariant(), d.Id, d.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Runs a script as one undo step named "Script: {name}". On failure, cancellation or timeout the script's edits are rolled back.
    /// The script body runs on a worker thread; the API marshals model access to the host thread via the <see cref="IWorkbenchBridge"/>.
    /// </summary>
    public async Task<ScriptRunResult> RunAsync(string code, ModelApi api, string name = "script", CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        var (script, diagnostics) = Prepare(code);
        if (script is null) return new ScriptRunResult(ScriptOutcome.CompileFailed, null, diagnostics, watch.Elapsed, 0, false);

        // Cancelled while compiling (a cold compile can take longer than a short cancel delay): nothing ran, nothing to undo.
        if (cancellationToken.IsCancellationRequested)
            return new ScriptRunResult(ScriptOutcome.Cancelled, null, diagnostics, watch.Elapsed, 0, false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var limit = timeout ?? Settings.Timeout;
        cts.CancelAfter(limit);
        api.CancellationToken = cts.Token;
        var editsBefore = api.EditCount;
        var globals = new ScriptGlobals(api, cts.Token);

        var batch = api.IsReadOnly ? null : api.Batch($"Script: {name}");
        Task<ScriptState<object>> run;
        try
        {
            // A dedicated thread: the host thread stays free to service the API calls and the Cancel button.
            run = Task.Factory.StartNew(() => script.RunAsync(globals, catchException: null, cancellationToken: cts.Token).GetAwaiter().GetResult(),
                cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch
        {
            batch?.Dispose();
            throw;
        }

        ScriptOutcome outcome;
        object? value = null;
        var all = diagnostics.ToList();
        var abandoned = false;
        try
        {
            var stop = new TaskCompletionSource();
            await using (cts.Token.Register(() => stop.TrySetResult()))
            {
                var first = await Task.WhenAny(run, stop.Task).ConfigureAwait(true);
                if (first != run)
                {
                    // Give the script a moment to observe cancellation at its next API call or loop iteration.
                    var grace = await Task.WhenAny(run, Task.Delay(Settings.CancelGrace)).ConfigureAwait(true);
                    abandoned = grace != run;
                }
            }

            if (!abandoned && run.IsCompletedSuccessfully)
            {
                value = run.Result.ReturnValue;
                outcome = ScriptOutcome.Succeeded;
            }
            else if (cts.IsCancellationRequested)
            {
                outcome = cancellationToken.IsCancellationRequested ? ScriptOutcome.Cancelled : ScriptOutcome.TimedOut;
                all.Add(new ScriptDiagnostic(0, 0, "error", outcome == ScriptOutcome.TimedOut ? "MS020" : "MS021",
                    (outcome == ScriptOutcome.TimedOut ? $"Time limit of {limit.TotalSeconds:0.#} s exceeded" : "Cancelled by the user")
                    + (abandoned ? "; the script did not stop and its thread was abandoned (it can no longer touch the model)" : "")));
            }
            else
            {
                outcome = ScriptOutcome.Failed;
                all.Add(RuntimeError(run.Exception?.InnerException ?? run.Exception));
            }
        }
        finally
        {
            if (!abandoned) { try { await run.ConfigureAwait(true); } catch { /* reported above */ } }
            batch?.Dispose();
        }

        // Discard partial edits on any failure, so a broken script never leaves a half-applied change behind.
        var edits = api.EditCount - editsBefore;
        var rolledBack = false;
        if (outcome != ScriptOutcome.Succeeded && edits > 0)
            rolledBack = api.InvokeUnchecked(() =>
            {
                var h = api.Repository.History;
                if (h.UndoDescription != $"Script: {name}" || !h.CanUndo) return false;
                h.Undo();
                return true;
            });

        return new ScriptRunResult(outcome, value, all, watch.Elapsed, edits, rolledBack, abandoned);
    }

    private static ScriptDiagnostic RuntimeError(Exception? e)
    {
        while (e is AggregateException { InnerException: { } inner }) e = inner;
        if (e is null) return new ScriptDiagnostic(0, 0, "error", "MS030", "Unknown error");
        var line = 0;
        var m = Regex.Match(e.StackTrace ?? "", Regex.Escape(ScriptFileName) + @":line (\d+)");
        if (m.Success) line = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return new ScriptDiagnostic(line, 0, "error", e.GetType().Name, e.Message);
    }
}
