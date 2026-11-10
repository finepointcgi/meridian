# Scripting

Meridian has a C# script console for querying and bulk-editing models. Open it from the **Script** dock tab,
from the **Extend ▸ Script** ribbon button, or with ⌘K → "Script console".

## Security: read this first

**A script is arbitrary .NET code. It runs inside the Meridian process (the desktop app, or the web server) with that
process's permissions. The console is not a sandbox.**

| Mode | Console | Who can run scripts |
| --- | --- | --- |
| Desktop / local mode | On by default. Turn off with `Meridian:Scripting:Enabled=false`. | The local user |
| Server mode | **Off** unless `Meridian:Scripting:Enabled=true` | Only **server administrators** (add `Meridian:Scripting:AllowProjectOwners=true` to also allow the owner of the open project; any user can create and own a project) |

Before a script runs, Meridian applies a policy as a guard rail. It is not a security boundary:

- Only these assemblies are referenced: `System.Private.CoreLib`, `System.Runtime`, `System.Linq`, `System.Collections`,
  `System.Text.RegularExpressions` and `Meridian.Scripting`.
- Default imports: `System`, `System.Linq`, `System.Text`, `System.Collections.Generic`, `System.Globalization`,
  `System.Text.RegularExpressions`, `Meridian.Scripting`.
- `#r`, `#load`, `goto`, `unsafe` and `dynamic` are rejected.
- Code that uses a type or member from `System.IO`, `System.Diagnostics`, `System.Net`, `System.Reflection`,
  `System.Threading`, `System.Runtime.InteropServices`/`Loader`/`CompilerServices`, `System.Security`, `Microsoft.Win32`,
  or Meridian's internal assemblies fails to compile. So do `Environment`, `AppDomain`, `Activator`, `GC`, `Console` and `Type`.
  A few harmless types are still allowed: `StringWriter`/`StringReader`, `MemoryStream`, `Stopwatch`, `Task` and `CancellationToken`.
- `ThrowIfCancelled();` is inserted at the top of every loop body, so loops stop when you press Cancel or the time limit
  (30 s by default; `Meridian:Scripting:TimeoutSeconds`) runs out. Every API call also checks for cancellation.

Determined code can still get around these checks. For example, it can exhaust memory, overflow the stack with
deep recursion (which crashes the process), or run a long operation with no loop and no API call, which cannot be
interrupted. If a cancelled script does not stop within 2 seconds, its thread is abandoned. It can no longer touch the model,
but it keeps using CPU until it finishes. An administrator can turn the deny list off with
`Meridian:Scripting:Unrestricted=true`. Do that only on a single-user desktop.

Treat running a script like running a program: only run scripts you trust.

## The console

- **Editor**: shows line numbers, and lines with errors are highlighted. **Ctrl/⌘+Enter** runs the script.
- **Output**: shows `Log(...)` lines, the return value (`⇒ …`), and compile and runtime errors with line numbers.
  Everything is also copied to the Output dock.
- **Undo**: one run is one undo step named `Script: <name>`. If a script fails, is cancelled or times out, its edits are
  rolled back.
- **Saved scripts** are stored in the model, so they are saved in `.meridian` files, versions and collaboration.
  Each one is a Comment element with the stereotype «script» inside a top-level `Scripts` package. The comment's name is the
  script name and its body is the code. Viewers can run read-only scripts but cannot save.
- **Examples**: count elements by kind; add a Verify test case for every unverified requirement; rename blocks to
  PascalCase; export a custom requirements CSV.

## Globals

| Member | Description |
| --- | --- |
| `Model` | The `IModelApi` (below) |
| `Selection` | The selected element (`IElement?`) |
| `Log(object)` / `Log(format, args)` / `Print(object)` | Writes to the output. Collections are written one item per line. |
| `Status(string)` | Shows a status bar message |
| `Download(fileName, content, mime)` | Offers a text file to the user as a download |
| `CancellationToken`, `ThrowIfCancelled()` | Cancellation |

A script can `return` a value, which is shown in the output.

## API reference (`Meridian.Scripting`, version 1.0)

The same API is given to plugins. Kinds are strings (case-insensitive), so the API stays stable when the
metamodel grows. `Model.Kinds`, `Model.RelationshipKinds` and `Model.DiagramTypes` list the valid names.

### `IModelApi`

| Member | Description |
| --- | --- |
| `ApiVersion`, `ModelName`, `Root`, `IsReadOnly` | Basics |
| `All` | Every element except the root |
| `OfKind(kind)` | For example `OfKind("Requirement")` |
| `Query(kind, name, stereotype, owner, recursive)` | Filter by any combination of criteria (`null` means any) |
| `Search(text)` | Matches the name, requirement id or text |
| `Get(id)`, `FindByName(name, kind)` | Look up one element |
| `Relationships(kind)`, `Diagrams` | Collections |
| `Create(owner, kind, name)` | Creates an element. Ownership rules are enforced. |
| `Connect(kind, source, target)` | Creates a relationship, for example `Connect("Satisfy", block, req)` |
| `CreateDiagram(owner, type, name)` | For example `CreateDiagram(pkg, "BlockDefinition")` |
| `Set(element, field, value)`, `Move(element, owner)`, `Delete(element)` | Edits |
| `Batch(description)` | `using (Model.Batch("x")) { … }` groups the edits inside it into one undo step |
| `Selection`, `ActiveDiagram`, `Select(e)`, `OpenDiagram(d)` | Workbench |
| `Log(msg)`, `Status(msg)`, `Download(...)`, `DownloadBytes(...)` | Output |

### `IElement`

`Id`, `Kind`, `Name` (settable), `Documentation` (settable), `QualifiedName`, `Owner`, `Children`, `Descendants`,
`IsRelationship`, `IsDiagram`, `Fields`, `Get(field)`, `Set(field, value)`, `Stereotypes`, `HasStereotype`,
`AddStereotype`, `RemoveStereotype`, `TaggedValues`, `GetTag(tag)`, `SetTag(tag, value)`, `Relationships`,
`Outgoing(kind)`, `Incoming(kind)`, `Delete()`.

`IRelationship` adds `Source` and `Target`. `IDiagram` adds `DiagramType`, `Elements`, `Shows(e)` and `Show(e, x, y)`.

The `Fields` class has well-known field names: `Name`, `Documentation`, `Type`, `Multiplicity`, `RequirementId` (`reqId`),
`Text`, `Body`, `Stereotypes`, `Subtype`, `Visibility`, `DefaultValue`. Tagged values are stored as `tag:<name>`.

## Examples

```csharp
// Requirements without a satisfying design element
foreach (var r in Model.OfKind("Requirement").Where(r => r.Incoming("Satisfy").Count == 0))
    Log($"{r.Get(Fields.RequirementId)} {r.Name}");
```

```csharp
// Tag every part of the selected block
foreach (var p in Selection!.Children.Where(c => c.Kind == "Property"))
    p.SetTag("reviewed", "yes");
```

## Running scripts from code

`Meridian.Scripting.ScriptEngine` can be used without the UI, for example in tests or tools:

```csharp
var api = new ModelApi(repository, new HeadlessBridge());
var result = await new ScriptEngine().RunAsync(code, api, "my script");
```
