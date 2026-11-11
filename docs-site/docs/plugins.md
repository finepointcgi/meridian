# Plugins

A plugin is a .NET 10 class library that references `Meridian.Scripting` and contains a public class implementing
<xref:Meridian.Scripting.IMeridianPlugin>. A plugin can add:

- **commands**, shown in the command palette (**⌘K**) and under **Generate ▸ Plugins** on the ribbon;
- **validation rules**, run by Validate and by the model-health indicator;
- **exporters**, which turn the model into the bytes of a file;
- **importers**, which read a file and edit the model.

Plugins use the same model API as [scripts](scripting.md).

> [!WARNING]
> A plugin is ordinary .NET code that runs in the Meridian process with full permissions. Install only plugins you
> trust. On a server, plugins load only from the folder an administrator configures.

## The interface

```csharp
public interface IMeridianPlugin
{
    string Name { get; }
    string Version { get; }
    void Initialize(IPluginHost host);
}

public interface IPluginHost
{
    string ApiVersion { get; }     // "1.0"
    bool LocalMode { get; }
    void RegisterCommand(string title, Action<IModelApi> run);
    void RegisterValidationRule(string id, string description, Func<IModelApi, IEnumerable<PluginIssue>> check);
    void RegisterExporter(string name, string extension, Func<IModelApi, byte[]> export);
    void RegisterImporter(string name, string extensions, Action<IModelApi, byte[], string> import);
    void Log(string message);
}

public sealed record PluginIssue(IssueSeverity Severity, string Message, IElement Element);
```

See <xref:Meridian.Scripting.IPluginHost> and <xref:Meridian.Scripting.PluginIssue> in the API reference.

How each contribution runs:

| Contribution | Behaviour |
| --- | --- |
| Command | runs on the UI thread as one undo step named `Plugin: <title>`; if it throws, the error goes to the Output tab and its edits are rolled back; viewers get a read-only API |
| Validation rule | gets a read-only API; added to every validator in the process; if it throws, it shows up as one warning instead of breaking validation |
| Exporter | gets a read-only API; the bytes are downloaded as `<model name><extension>` |
| Importer | gets the file's bytes and name; its edits are one undo step |

## A minimal plugin

```csharp
using Meridian.Scripting;

public sealed class NamingPlugin : IMeridianPlugin
{
    public string Name => "Naming conventions";
    public string Version => "1.0.0";

    public void Initialize(IPluginHost host)
    {
        host.RegisterValidationRule("NAME-001", "Blocks use PascalCase", api =>
            api.OfKind("Block")
               .Where(b => b.Name.Length > 0 && !char.IsUpper(b.Name[0]))
               .Select(b => new PluginIssue(IssueSeverity.Warning, $"Block '{b.Name}' should start with a capital letter", b)));

        host.RegisterCommand("Capitalise block names", api =>
        {
            foreach (var b in api.OfKind("Block").Where(b => b.Name.Length > 0 && char.IsLower(b.Name[0])))
                b.Name = char.ToUpperInvariant(b.Name[0]) + b.Name[1..];
        });

        host.RegisterExporter("Block list", ".txt", api =>
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n", api.OfKind("Block").Select(b => b.QualifiedName))));
    }
}
```

Project file essentials:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <EnableDynamicLoading>true</EnableDynamicLoading>
</PropertyGroup>
<ItemGroup>
  <!-- Provided by the host at run time: compile against it, don't ship it. -->
  <ProjectReference Include="path/to/Meridian.Scripting.csproj">
    <Private>false</Private>
    <ExcludeAssets>runtime</ExcludeAssets>
  </ProjectReference>
</ItemGroup>
```

The repository's `samples/HelloPlugin` is a complete plugin with two commands, a rule (`HELLO-001`), a Markdown outline
exporter and a text importer.

## Installing

Copy the plugin's build output into the plugins folder, either as `plugins/MyPlugin/MyPlugin.dll` (with its private
dependencies and `deps.json`; preferred) or as `plugins/MyPlugin.dll`.

| Mode | Plugins folder |
| --- | --- |
| Desktop / local | `<data path>/plugins` (override with `Meridian:Plugins:Path`) |
| Server | `Meridian:Plugins:Path` only; when it is not set, no plugins load |

Set `Meridian:Plugins:Enabled=false` to turn plugin loading off. Plugins load once, when Meridian starts; restart it
after adding or updating one.

**Generate ▸ Plugins ▸ Loaded plugins…** lists each plugin with its version, status, any error and what it contributes.

## Loading and isolation

Each plugin assembly is loaded into its own `AssemblyLoadContext`; its private dependencies come from its own folder
(through `deps.json` when present). `Meridian.*`, `System.*` and `Microsoft.*` always resolve to the host's copies, so
plugin and host share the API types. If a plugin fails to load or its `Initialize` throws, the error is written to the
Output tab and the server log, anything it registered is discarded, and Meridian keeps running.

## Compatibility

The API is version 1.0. Later versions only add members; check `host.ApiVersion` at run time.
