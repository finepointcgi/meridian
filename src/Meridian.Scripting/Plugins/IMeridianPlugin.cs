namespace Meridian.Scripting;

/// <summary>
/// Entry point of a Meridian plugin. Put a public, parameterless-constructible class implementing this interface in an
/// assembly in the plugins folder; Meridian creates it once at start-up and calls <see cref="Initialize"/>.
/// </summary>
public interface IMeridianPlugin
{
    /// <summary>Display name, e.g. "Hello Plugin".</summary>
    string Name { get; }

    /// <summary>Plugin version, e.g. "1.0.0".</summary>
    string Version { get; }

    /// <summary>Registers the plugin's commands, rules, exporters and importers. Called once.</summary>
    void Initialize(IPluginHost host);
}

/// <summary>Severity of a plugin validation issue.</summary>
public enum IssueSeverity { Info, Warning, Error }

/// <summary>A problem reported by a plugin validation rule.</summary>
public sealed record PluginIssue(IssueSeverity Severity, string Message, IElement Element);

/// <summary>What a plugin can contribute. Passed to <see cref="IMeridianPlugin.Initialize"/>.</summary>
public interface IPluginHost
{
    /// <summary>Version of the plugin API (same as <see cref="IModelApi.ApiVersion"/>).</summary>
    string ApiVersion { get; }

    /// <summary>True in the desktop app / local mode, false on a team server.</summary>
    bool LocalMode { get; }

    /// <summary>
    /// Adds a command to the command palette (⌘K) and the Plugins ribbon menu. <paramref name="run"/> executes on the UI thread
    /// as one undo step; exceptions are caught and written to the Output dock.
    /// </summary>
    void RegisterCommand(string title, Action<IModelApi> run);

    /// <summary>
    /// Adds a validation rule to every validation run (Validate, model health). The API passed to <paramref name="check"/> is read-only.
    /// </summary>
    void RegisterValidationRule(string id, string description, Func<IModelApi, IEnumerable<PluginIssue>> check);

    /// <summary>Adds an exporter to the Plugins menu. <paramref name="export"/> receives a read-only API and returns the file content.</summary>
    void RegisterExporter(string name, string extension, Func<IModelApi, byte[]> export);

    /// <summary>
    /// Adds an importer to the Plugins menu. <paramref name="extensions"/> is an accept list such as ".csv,.txt".
    /// <paramref name="import"/> receives the API (edits are one undo step), the file content and the file name.
    /// </summary>
    void RegisterImporter(string name, string extensions, Action<IModelApi, byte[], string> import);

    /// <summary>Writes a line to the Output dock (prefixed with the plugin name).</summary>
    void Log(string message);
}
