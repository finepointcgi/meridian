using System.Collections;
using System.Globalization;

namespace Meridian.Scripting;

/// <summary>
/// The globals a C# script sees. Every public member is directly usable in a script:
/// <c>Model</c>, <c>Selection</c>, <c>Log(...)</c>, <c>Status(...)</c>, <c>Download(...)</c>, <c>CancellationToken</c>.
/// </summary>
public sealed class ScriptGlobals
{
    private readonly IModelApi _model;

    public ScriptGlobals(IModelApi model, CancellationToken cancellationToken)
    {
        _model = model;
        CancellationToken = cancellationToken;
    }

    /// <summary>The model API.</summary>
    public IModelApi Model => _model;

    /// <summary>The element selected in the workbench when the script started reading it, or null.</summary>
    public IElement? Selection => _model.Selection;

    /// <summary>Cancelled when the user presses Cancel or the time limit expires.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Writes a value to the script output (collections are written one item per line).</summary>
    public void Log(object? value)
    {
        switch (value)
        {
            case null: _model.Log("null"); break;
            case string s: _model.Log(s); break;
            case IDictionary d:
                foreach (DictionaryEntry kv in d) _model.Log($"{kv.Key}: {Format(kv.Value)}");
                break;
            case IEnumerable items:
                foreach (var i in items) _model.Log(Format(i));
                break;
            default: _model.Log(Format(value)); break;
        }
    }

    /// <summary>Formats and writes a line, e.g. <c>Log("{0} blocks", n)</c>.</summary>
    public void Log(string format, params object?[] args) => _model.Log(string.Format(CultureInfo.InvariantCulture, format, args));

    /// <summary>Alias of <see cref="Log(object?)"/>.</summary>
    public void Print(object? value) => Log(value);

    /// <summary>Shows a status bar message.</summary>
    public void Status(string message) => _model.Status(message);

    /// <summary>Offers a text file (CSV, JSON, …) to the user as a download.</summary>
    public void Download(string fileName, string content, string mimeType = "text/plain") => _model.Download(fileName, content, mimeType);

    /// <summary>Throws when the run was cancelled. Injected automatically at the top of every loop body.</summary>
    public void ThrowIfCancelled() => CancellationToken.ThrowIfCancellationRequested();

    private static string Format(object? v) => v switch
    {
        null => "null",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };
}
