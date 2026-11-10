# Plugins

A plugin is a .NET 10 class library that references `Meridian.Scripting` and contains a public class implementing
`IMeridianPlugin`. A plugin can add:

- **commands**, shown in the command palette (⌘K) and in **Extend ▸ Plugins** on the ribbon;
- **validation rules**, run by Validate and by the model-health indicator;
- **exporters**, which take a model and return the bytes of a file;
- **importers**, which take a file and edit the model.

Plugins use the same model API as scripts: `IModelApi`, `IElement`, `IRelationship` and `IDiagram`, documented in
[SCRIPTING.md](SCRIPTING.md).

> **Security.** A plugin is ordinary .NET code that runs in the Meridian process with full permissions, so install only
> plugins you trust. On a server, plugins load only from the folder an administrator configures.

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

How each contribution runs:

- **Commands** run on the UI thread as one undo step named `Plugin: <title>`. If a command throws, the error goes to the
  Output dock and the command's edits are rolled back. For viewers the API is read-only.
- **Validation rules** get a read-only API. Rules are added to every validator in the process. If a rule throws, it shows up
  as one warning instead of breaking validation.
- **Exporters** get a read-only API. The bytes they return are downloaded as `<model name><extension>`.
- **Importers** get the file's bytes and name. Their edits are one undo step.

## Sample

`samples/HelloPlugin` contains a working plugin with two commands, a rule (`HELLO-001`), a Markdown outline exporter
and a text importer. It is part of the solution, so `dotnet build` builds it. `PluginTests` loads it from a plugins
folder.

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

## Installing

Copy the plugin's build output into the plugins folder, using either layout:

```
plugins/HelloPlugin/HelloPlugin.dll   (+ its private dependencies, deps.json …)   ← preferred
plugins/HelloPlugin.dll
```

| Mode | Plugins folder |
| --- | --- |
| Desktop / local | `<data path>/plugins` (override with `Meridian:Plugins:Path`) |
| Server | `Meridian:Plugins:Path` only. If it is not set, no plugins load. |

Set `Meridian:Plugins:Enabled=false` to turn off plugin loading. Plugins load once, when the app starts; restart
Meridian after you add or update one.

## Loading and isolation

Each plugin assembly is loaded into its own `AssemblyLoadContext`. Its private dependencies come from its own folder,
resolved through `deps.json` when one is present. `Meridian.*`, `System.*` and `Microsoft.*` assemblies always resolve
to the host's copies, so the plugin and the host share the API types. If an assembly fails to load, a plugin's
constructor throws, or `Initialize` throws, the error is written to the Output dock and the server log, and the app keeps
running. Anything a plugin registered before its `Initialize` threw is discarded.

**Extend ▸ Plugins ▸ Loaded plugins…** lists each plugin with its version, status, any error, and what it contributes.

## Compatibility

API 1.0. Later versions only add members. Use `host.ApiVersion` to check which version you are running against.
