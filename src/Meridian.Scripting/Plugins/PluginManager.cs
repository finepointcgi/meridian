using System.Reflection;
using System.Runtime.Loader;
using Meridian.Core.Model;
using Meridian.Core.Validation;

namespace Meridian.Scripting;

public sealed record PluginCommand(string Plugin, string Title, Action<IModelApi> Run);
public sealed record PluginExporter(string Plugin, string Name, string Extension, Func<IModelApi, byte[]> Export);
public sealed record PluginImporter(string Plugin, string Name, string Extensions, Action<IModelApi, byte[], string> Import);
public sealed record PluginValidationRule(string Plugin, string Id, string Description, Func<IModelApi, IEnumerable<PluginIssue>> Check);

/// <summary>A plugin that was found, with its load status.</summary>
public sealed record PluginInfo(string Name, string Version, string AssemblyPath, bool Loaded, string? Error,
    int Commands, int Rules, int Exporters, int Importers);

/// <summary>
/// Discovers and loads plugins from a folder, each assembly in its own <see cref="AssemblyLoadContext"/>.
/// Load errors are collected in <see cref="Messages"/> (shown in the Output dock) and never thrown.
/// </summary>
/// <remarks>
/// Layout: <c>plugins/MyPlugin.dll</c>, or <c>plugins/MyPlugin/MyPlugin.dll</c> with its private dependencies beside it.
/// Meridian.*, System.* and Microsoft.* assemblies always resolve to the host's copies, so plugin and host share API types.
/// </remarks>
public sealed class PluginManager
{
    private readonly object _gate = new();
    private readonly List<PluginInfo> _plugins = new();
    private readonly List<PluginCommand> _commands = new();
    private readonly List<PluginExporter> _exporters = new();
    private readonly List<PluginImporter> _importers = new();
    private readonly List<PluginValidationRule> _rules = new();
    private readonly List<string> _messages = new();

    /// <param name="localMode">Reported to plugins via <see cref="IPluginHost.LocalMode"/>.</param>
    /// <param name="registerValidationGlobally">Add plugin rules to every <see cref="ModelValidator"/> (the app). Tests pass false.</param>
    public PluginManager(bool localMode = true, bool registerValidationGlobally = true)
    {
        LocalMode = localMode;
        RegisterValidationGlobally = registerValidationGlobally;
    }

    public bool LocalMode { get; }
    public bool RegisterValidationGlobally { get; }
    public string? Folder { get; private set; }

    public IReadOnlyList<PluginInfo> Plugins { get { lock (_gate) return _plugins.ToList(); } }
    public IReadOnlyList<PluginCommand> Commands { get { lock (_gate) return _commands.ToList(); } }
    public IReadOnlyList<PluginExporter> Exporters { get { lock (_gate) return _exporters.ToList(); } }
    public IReadOnlyList<PluginImporter> Importers { get { lock (_gate) return _importers.ToList(); } }
    public IReadOnlyList<PluginValidationRule> ValidationRules { get { lock (_gate) return _rules.ToList(); } }

    /// <summary>Load messages and plugin log lines, oldest first.</summary>
    public IReadOnlyList<string> Messages { get { lock (_gate) return _messages.ToList(); } }

    /// <summary>Raised for every new message (plugin logs at run time).</summary>
    public event Action<string>? MessageLogged;

    internal void Log(string message)
    {
        lock (_gate) _messages.Add(message);
        MessageLogged?.Invoke(message);
    }

    /// <summary>The candidate plugin assemblies in a folder.</summary>
    public static IEnumerable<string> Discover(string folder)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (var dll in Directory.EnumerateFiles(folder, "*.dll").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            yield return dll;
        foreach (var dir in Directory.EnumerateDirectories(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var main = Path.Combine(dir, Path.GetFileName(dir) + ".dll");
            if (File.Exists(main)) yield return main;
        }
    }

    /// <summary>Loads every plugin in <paramref name="folder"/>. Safe to call when the folder does not exist.</summary>
    public void LoadFrom(string folder)
    {
        Folder = folder;
        if (!Directory.Exists(folder))
        {
            Log($"Plugins: no plugins folder at {folder}");
            return;
        }
        var found = 0;
        foreach (var path in Discover(folder))
        {
            found++;
            LoadAssembly(path);
        }
        if (found == 0) Log($"Plugins: none found in {folder}");
    }

    /// <summary>Loads the plugins in one assembly file.</summary>
    public void LoadAssembly(string path)
    {
        Assembly assembly;
        try
        {
            var context = new PluginLoadContext(Path.GetFullPath(path));
            assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
        }
        catch (Exception e)
        {
            Fail(Path.GetFileNameWithoutExtension(path), path, $"could not load assembly: {e.Message}");
            return;
        }

        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types.OfType<Type>().ToArray();
            Log($"Plugins: {Path.GetFileName(path)}: some types could not be loaded: {e.LoaderExceptions.FirstOrDefault()?.Message}");
        }
        catch (Exception e)
        {
            Fail(Path.GetFileNameWithoutExtension(path), path, $"could not read types: {e.Message}");
            return;
        }

        var pluginTypes = types.Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } && typeof(IMeridianPlugin).IsAssignableFrom(t)).ToList();
        if (pluginTypes.Count == 0)
        {
            // Dependencies sitting in the top-level folder are fine; just say so.
            Log($"Plugins: {Path.GetFileName(path)} contains no IMeridianPlugin implementation (skipped)");
            return;
        }
        foreach (var type in pluginTypes) Initialize(type, path);
    }

    /// <summary>Registers an already-instantiated plugin (built-in plugins and tests).</summary>
    public PluginInfo Add(IMeridianPlugin plugin, string source = "(built-in)") => Initialize(plugin, source);

    private void Initialize(Type type, string path)
    {
        IMeridianPlugin plugin;
        try { plugin = (IMeridianPlugin)Activator.CreateInstance(type)!; }
        catch (Exception e)
        {
            Fail(type.FullName ?? type.Name, path, $"could not be created: {(e as TargetInvocationException)?.InnerException?.Message ?? e.Message}");
            return;
        }
        Initialize(plugin, path);
    }

    private PluginInfo Initialize(IMeridianPlugin plugin, string path)
    {
        string name, version;
        try { name = plugin.Name; version = plugin.Version; }
        catch (Exception e) { return Fail(plugin.GetType().Name, path, $"Name/Version threw: {e.Message}"); }

        var host = new Host(this, name);
        try
        {
            plugin.Initialize(host);
        }
        catch (Exception e)
        {
            host.Rollback();
            return Fail(name, path, $"Initialize failed: {e.Message}");
        }
        var info = host.Commit(version, path);
        Log($"Plugins: loaded {name} {version} ({info.Commands} commands, {info.Rules} rules, {info.Exporters} exporters, {info.Importers} importers)");
        return info;
    }

    private PluginInfo Fail(string name, string path, string error)
    {
        var info = new PluginInfo(name, "?", path, false, error, 0, 0, 0, 0);
        lock (_gate) _plugins.Add(info);
        Log($"Plugins: ERROR {name}: {error}");
        return info;
    }

    /// <summary>Removes this manager's validation rules from the global registry.</summary>
    public void UnregisterValidationRules()
    {
        foreach (var r in ValidationRules) ExternalValidationRules.Unregister(r.Id);
    }

    /// <summary>Validation rules as core <see cref="IValidationRule"/>s (used for global registration and tests).</summary>
    public IReadOnlyList<IValidationRule> CoreRules => ValidationRules.Select(r => (IValidationRule)new RuleAdapter(r)).ToList();

    // Collects a plugin's registrations and publishes them only if Initialize succeeds.
    private sealed class Host(PluginManager manager, string pluginName) : IPluginHost
    {
        private readonly List<PluginCommand> _commands = new();
        private readonly List<PluginExporter> _exporters = new();
        private readonly List<PluginImporter> _importers = new();
        private readonly List<PluginValidationRule> _rules = new();
        private bool _committed;

        public string ApiVersion => ModelApi.Version;
        public bool LocalMode => manager.LocalMode;

        public void RegisterCommand(string title, Action<IModelApi> run)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentNullException.ThrowIfNull(run);
            var c = new PluginCommand(pluginName, title, run);
            if (_committed) lock (manager._gate) manager._commands.Add(c); else _commands.Add(c);
        }

        public void RegisterValidationRule(string id, string description, Func<IModelApi, IEnumerable<PluginIssue>> check)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentNullException.ThrowIfNull(check);
            var r = new PluginValidationRule(pluginName, id, description ?? id, check);
            if (_committed) manager.PublishRule(r); else _rules.Add(r);
        }

        public void RegisterExporter(string name, string extension, Func<IModelApi, byte[]> export)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(export);
            var x = new PluginExporter(pluginName, name, extension.StartsWith('.') ? extension : "." + extension, export);
            if (_committed) lock (manager._gate) manager._exporters.Add(x); else _exporters.Add(x);
        }

        public void RegisterImporter(string name, string extensions, Action<IModelApi, byte[], string> import)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(import);
            var x = new PluginImporter(pluginName, name, extensions ?? "", import);
            if (_committed) lock (manager._gate) manager._importers.Add(x); else _importers.Add(x);
        }

        public void Log(string message) => manager.Log($"[{pluginName}] {message}");

        public void Rollback()
        {
            _commands.Clear(); _exporters.Clear(); _importers.Clear(); _rules.Clear();
        }

        public PluginInfo Commit(string version, string path)
        {
            var info = new PluginInfo(pluginName, version, path, true, null, _commands.Count, _rules.Count, _exporters.Count, _importers.Count);
            lock (manager._gate)
            {
                manager._commands.AddRange(_commands);
                manager._exporters.AddRange(_exporters);
                manager._importers.AddRange(_importers);
                manager._plugins.Add(info);
            }
            foreach (var r in _rules) manager.PublishRule(r);
            _committed = true;
            return info;
        }
    }

    private void PublishRule(PluginValidationRule rule)
    {
        lock (_gate) _rules.Add(rule);
        if (RegisterValidationGlobally) ExternalValidationRules.Register(new RuleAdapter(rule));
    }

    private sealed class RuleAdapter(PluginValidationRule rule) : IValidationRule
    {
        public string Id => rule.Id;
        public string Description => $"{rule.Description} ({rule.Plugin})";

        public IEnumerable<ValidationIssue> Check(ModelRepository model)
        {
            var api = new ModelApi(model, null, readOnly: true);
            foreach (var issue in rule.Check(api))
                if (issue.Element is ElementWrapper w && model.Contains(w.Element))
                    yield return new ValidationIssue(issue.Severity switch
                    {
                        IssueSeverity.Error => Severity.Error,
                        IssueSeverity.Warning => Severity.Warning,
                        _ => Severity.Info,
                    }, rule.Id, issue.Message, w.Element);
        }
    }
}

/// <summary>Isolates a plugin's private dependencies while sharing Meridian, System and Microsoft assemblies with the host.</summary>
internal sealed class PluginLoadContext(string mainAssemblyPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: false)
{
    private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

    protected override Assembly? Load(AssemblyName name)
    {
        var n = name.Name ?? "";
        if (n.StartsWith("Meridian.", StringComparison.Ordinal) || n.StartsWith("System.", StringComparison.Ordinal)
            || n.StartsWith("Microsoft.", StringComparison.Ordinal) || n is "System" or "mscorlib" or "netstandard")
            return null;   // the host's copy (Default context)
        var path = _resolver.ResolveAssemblyToPath(name);
        if (path is null)
        {
            var local = Path.Combine(Path.GetDirectoryName(mainAssemblyPath)!, n + ".dll");
            if (File.Exists(local)) path = local;
        }
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
