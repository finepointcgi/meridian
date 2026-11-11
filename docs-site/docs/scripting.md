# Scripting

The script console runs C# against the open model: query it, bulk-edit it, check it or export it. Open it from the
**Script** dock tab, **Generate ▸ Script** on the ribbon, or **⌘K** → "Script console".

> [!WARNING]
> **A script is arbitrary .NET code** running inside the Meridian process (the desktop app or the web server) with
> that process's permissions. The policy below is a guard rail, not a sandbox. Only run scripts you trust.

## Who can run scripts

| Mode | Console | Who can run scripts |
| --- | --- | --- |
| Desktop / local workspace | on by default; turn off with `Meridian:Scripting:Enabled=false` | the local user |
| Server | **off** unless `Meridian:Scripting:Enabled=true` | only the **owner** of the open project |

## The console

- **Editor** with line numbers; lines with errors are highlighted. **⌘Enter** / **Ctrl+Enter** runs the script.
- **Output** shows `Log(...)` lines, the return value (`⇒ …`) and compile or runtime errors with line numbers. It is
  also copied to the *Output* dock tab.
- **One run is one undo step** named `Script: <name>`. If a script fails, is cancelled or times out, its edits are
  rolled back.
- **Saved scripts** are stored in the model (a Comment with the «script» stereotype in a top-level `Scripts` package),
  so they are versioned and shared. Viewers can run read-only scripts but cannot save.
- **Examples** in the console: count elements by kind, add a verify test case for every unverified requirement,
  rename blocks to PascalCase, export a custom requirements CSV.

## Globals

| Member | Description |
| --- | --- |
| `Model` | the <xref:Meridian.Scripting.IModelApi> |
| `Selection` | the selected element (`IElement?`) |
| `Log(object)`, `Log(format, args)`, `Print(object)` | write to the output; collections are written one item per line |
| `Status(string)` | show a status bar message |
| `Download(fileName, content, mime)` | offer a text file to the user |
| `CancellationToken`, `ThrowIfCancelled()` | cancellation |

A script can `return` a value, which is shown in the output.

## The model API

The API is version 1.0 and is shared with [plugins](plugins.md). Kinds are strings (case-insensitive), so the API stays
stable as the metamodel grows; `Model.Kinds`, `Model.RelationshipKinds` and `Model.DiagramTypes` list the valid names.
The full reference is in the [API reference](../api/index.md).

| `IModelApi` member | Description |
| --- | --- |
| `All`, `OfKind(kind)`, `Query(kind, name, stereotype, owner, recursive)`, `Search(text)` | find elements |
| `Get(id)`, `FindByName(name, kind)` | look up one element |
| `Relationships(kind)`, `Diagrams` | collections |
| `Create(owner, kind, name)` | create an element (ownership rules are enforced) |
| `Connect(kind, source, target)` | create a relationship, e.g. `Connect("Satisfy", block, req)` |
| `CreateDiagram(owner, type, name)` | e.g. `CreateDiagram(pkg, "BlockDefinition")` |
| `Set(element, field, value)`, `Move(element, owner)`, `Delete(element)` | edit |
| `Batch(description)` | `using (Model.Batch("x")) { … }` groups edits into one undo step |
| `Selection`, `ActiveDiagram`, `Select(e)`, `OpenDiagram(d)` | workbench |

<xref:Meridian.Scripting.IElement> has `Id`, `Kind`, `Name`, `Documentation`, `QualifiedName`, `Owner`, `Children`,
`Descendants`, `Fields`, `Get` / `Set`, stereotypes, tagged values (`GetTag`, `SetTag`), `Relationships`,
`Outgoing(kind)`, `Incoming(kind)` and `Delete()`. <xref:Meridian.Scripting.IRelationship> adds `Source` and `Target`;
<xref:Meridian.Scripting.IDiagram> adds `DiagramType`, `Elements`, `Shows(e)` and `Show(e, x, y)`.
<xref:Meridian.Scripting.Fields> names the common field keys (`Fields.RequirementId`, `Fields.Text`, …).

## The script policy

Before a script runs, Meridian applies a policy:

- Only `System.Private.CoreLib`, `System.Runtime`, `System.Linq`, `System.Collections`,
  `System.Text.RegularExpressions` and `Meridian.Scripting` are referenced.
- Default imports: `System`, `System.Linq`, `System.Text`, `System.Collections.Generic`, `System.Globalization`,
  `System.Text.RegularExpressions`, `Meridian.Scripting`.
- `#r`, `#load`, `goto`, `unsafe` and `dynamic` are rejected.
- Using file system, process, network, reflection, threading, interop, security or registry APIs fails to compile, as
  do `Environment`, `AppDomain`, `Activator`, `GC`, `Console` and `Type`.
- Loops check for cancellation, so **Cancel** and the time limit (30 s by default; `Meridian:Scripting:TimeoutSeconds`)
  stop them. A script that does not stop within 2 seconds is abandoned.

An administrator can turn the deny list off with `Meridian:Scripting:Unrestricted=true`. Do that only on a
single-user desktop.

## Examples

```csharp
// Requirements without a satisfying design element
foreach (var r in Model.OfKind("Requirement").Where(r => r.Incoming("Satisfy").Count == 0))
    Log($"{r.Get(Fields.RequirementId)} {r.Name}");
```

```csharp
// A verify test case for every unverified leaf requirement, in one undo step
var tests = Model.FindByName("Verification", "Package") ?? Model.Create(Model.Root, "Package", "Verification");
using (Model.Batch("Add test cases"))
    foreach (var r in Model.OfKind("Requirement").Where(r => r.Incoming("Verify").Count == 0
                                                         && !r.Children.Any(c => c.Kind == "Requirement")))
        Model.Connect("Verify", Model.Create(tests, "TestCase", "Test " + r.Name), r);
```

```csharp
// Hazard log as CSV (Infusion pump sample)
var sb = new StringBuilder("Id,Hazard,Severity,Controls\n");
foreach (var h in Model.OfKind("Block").Where(b => b.HasStereotype("Hazard")))
    sb.AppendLine($"{h.GetTag("hazardId")},{h.Name},{h.GetTag("severity")},"
                + string.Join(" ", h.Incoming("Trace").Select(t => t.Source.Get(Fields.RequirementId))));
Download("hazards.csv", sb.ToString(), "text/csv");
```

## Running scripts outside the UI

`Meridian.Scripting.ScriptEngine` runs scripts headless, for example in tests or batch tools:

```csharp
var api = new ModelApi(repository, new HeadlessBridge());
var result = await new ScriptEngine().RunAsync(code, api, "my script");
```
