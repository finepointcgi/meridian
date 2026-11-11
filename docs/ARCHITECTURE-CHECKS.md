# Architecture checks (model-to-code conformance)

Code sync ([CODE-SYNC.md](CODE-SYNC.md)) brings the code into the model. Architecture checks go the other way: they
check the code against the architecture drawn in the model and report where the code breaks it. A pull request or
commit that violates the architecture gets a separate commit status, `meridian/architecture`.

Example: the model connects `ThermalControl` and `PowerBus` only through ports typed by the interface block `IPowerBus`.
Code in which `ThermalControl` calls `PowerBus` directly is reported:

> ThermalControl uses PowerBus, but the model has no relationship from ThermalControl to PowerBus that permits it.
> *Fix:* Depend on IPowerBus instead: the model connects ThermalControl and PowerBus only through it.

## When it runs

- **After every code-sync run** (webhook or *Sync now*), for runs that did not fail. The pushed code is read again,
  stored per repository connection, and checked against **main's** model (the approved architecture).
- **On demand**: *Check architecture* in the ribbon's *Code* group, or ⌘K “Check architecture”. It checks the last
  synced code of every connected repository against the model you have open (any branch), so you can fix the model
  and check again without a push. On main, for owners and editors, it also stores the result and posts the
  `meridian/architecture` status again for the last synced commit.

## The rules

| Rule id | Severity | What it reports |
|---|---|---|
| `dependency` | error | A code dependency between two types whose model elements have no relationship path that permits it. |
| `layering` | error | A type in one «layer» package depends on a type in another layer that the first layer does not allow. |
| `interface` | error | A block that realizes an interface block does not implement the matching code interface, or misses one of its operations. |
| `missing-in-code` | warning | A block marked «implemented» has no matching type in the code. |
| `missing-in-model` | warning | A type in the synced code has no element in the model. |

Every finding has a severity, the code location (`file:line` of the type, linked to the file at the synced commit),
the model element (and the target element for pairs), a message and a fix hint.

**The lines that break the rule.** A `dependency` or `layering` finding also lists where the source type uses the
target (`ArchitectureViolation.Locations`, from the reader's `CodeType.DependencyLocations`, see
[CODE-SYNC.md](CODE-SYNC.md#where-dependencies-are-used)): the first 5 uses in file and line order
(`ConformanceOptions.MaxLocationsPerViolation`), each with its kind (`new`, `call`, `field`, `param`, `return`,
`inherit`, `import`, `use`), and `MoreLocations`, the number of further uses (including those the reader only counted).
`FirstLocation` is the first of them, or the type's location when the code has none recorded (older stored code). Other
rules have no use locations.

### How code maps to the model

Each code type maps to:

1. elements whose `code.symbol` is the type's symbol (set by code sync, or by hand on a hand-made block to link it), and
2. hand-made elements (no `code.symbol`) with the same name. Names match loosely: `Thermal Control`, `thermal_control`
   and `ThermalControl` are the same; generic parameters are ignored. When several match, the ones whose owning
   packages match the namespace best win.

Elements synced from another repository connection (`code.repo`) are ignored. A finding names the element the
architecture constrains when there is one.

### What counts as architecture

Only **hand-made** relationships, parts and ports count. Everything code sync created carries `code.symbol` and
mirrors the code, so it would permit any dependency the code has.

### (a) Dependencies

A code dependency is any of: base class, implemented interface, field / property type, or usage in a body, parameter,
local, `new` or static call. It is permitted when, between the two types' elements (or packages that own them), the
model has one of these **permitted paths**:

| Path | Meaning |
|---|---|
| `association`, `composition`, `aggregation` | the relationship, in either direction; or a hand-made part / reference of the source typed by the target |
| `connector` | a connector between parts typed by the two blocks — **not** through ports typed by an interface block |
| `dependency` | a dependency from the source to the target (not «use», «allow», «allowed») |
| `usage` | a dependency stereotyped «use» or «usage» |
| `realization`, `generalization` | from the source to the target |
| `shared-interface` | the target is an interface (block) the source shares: a port typed by it, or a connector through it |

A connector through ports typed by an interface block permits each side to depend on **the interface**, not on each
other. That is the thermal example.

By default the rule only applies when **both** blocks appear in the architecture (they, or a package owning them, have
a hand-made relationship, part or port). Blocks the model does not constrain are free to use each other. Set the model
root's field `architecture.strict` to `true` to check every dependency between mapped types.

Configure the paths with the model root's field `architecture.permittedPaths`, a comma separated list of the names
above (default: all). For example `association, composition, connector, shared-interface` makes plain dependencies
and generalizations stop counting.

### (b) Layering

Stereotype packages «layer». A code type is in a layer when its element is inside the package, or when its namespace
is listed in the layer's tagged value `namespaces` (comma separated prefixes, e.g. `Sat.Infrastructure`). Declare what
a layer may depend on in either (or both) of two ways:

- the tagged value **`allowed`** on the layer package: other layers by name or qualified name, comma separated;
  `none` allows no other layer, `*` allows all;
- a **dependency stereotyped «allowed»** from the layer package to a layer it may use.

A layer with neither is unrestricted. Example: «layer» `Domain` with `allowed = Shared` reports any dependency of a
Domain type on an `Infrastructure` type. Nested layers may use each other.

### (c) Interface conformance

A block “typed by” an interface block is one with a hand-made **realization** (or generalization) to it, or a port
stereotyped **«provided»** typed by it. A plain port typed by an interface block is a required interface and is not
checked. For each such block with a code type:

- if the interface block maps to a code interface, the block's code type must implement it (directly, through a base
  class, or through another interface);
- every operation of the interface block in the model, and every method of the code interface, must exist on the
  block's code type (or its scanned base types; implementing the code interface covers that interface's methods).
  Names compare case-insensitively.

### (d) Missing in code / missing in model

- A block, class, interface block … stereotyped **«implemented»** with no code type mapped to it. Not reported for a
  partial push (only the changed files).
- A code type with no model element. Accepting the code-sync review usually resolves these.

Both are warnings: they never fail a status.

## Suppressing findings

- An **«allow» dependency** between two elements (blocks or packages) suppresses findings from the first (or anything
  inside it) to the second. A tagged value `rules` (e.g. `dependency, layering`) limits it to those rules. An «allow»
  dependency from a package to itself suppresses everything inside it.
- The **project suppression list**: the model root's field `architecture.suppressions`, one entry per line (or `;`):

  ```
  # known debt, ticket SAT-142
  dependency: Sat.Thermal.* -> Sat.Power.PowerBus
  missing-in-model: Sat.Internal.*
  *: LegacyAdapter
  Architecture::ThermalControl -> *
  ```

  `rule:` is optional (`*` or missing means every rule). Patterns use `*` and match, case-insensitively, the code
  symbol, the element name or the element's qualified name. `source -> target` matches pairs; a single pattern matches
  the source side.
- Switch whole rules off with the model root's field `architecture.disabledRules` (e.g. `missing-in-model`).

Suppressed findings are counted in the tab (“conforms to the architecture (2 suppressed)”) but not reported.

## The commit status

A separate `meridian/architecture` status is posted for the synced commit (when the connection posts statuses), next
to code sync's `meridian/model`. Its link opens the project with the Architecture tab
(`/?project=<id>&panel=architecture`).

| Result | Default | “Fail pull requests on architecture violations” on |
|---|---|---|
| no violations | success “No architecture violations” (+ warnings count) | same |
| violations | success “2 violations, 1 warning (not enforced), first at src/Thermal/ThermalControl.cs:42” | **failure** “2 violations, first at src/Thermal/ThermalControl.cs:42” |

With violations the description cites the first location of the first error (in the sorted order: rule, file, line). The
hosts keep 140 characters, so a long path is shortened from the left to its last folders
(“…/control/ThermalControlLoop.cs:42”).

The setting is per repository, in the Architecture tab (owners and editors). Make the status a required check on the
branch to block merging.

## The Architecture tab

*Architecture* (a view tab next to the diagrams) shows, per connected repository: the last synced commit, the last
result on main, the *Fail pull requests* setting, and the violations of the last check **grouped by rule**. Each row
links the file at the synced commit (`file:line` of the type). Below the message, **Lines:** lists the lines that break
the rule (`line 42 (new)` in the type's file, `Heating.cs:23 (call)` in another one, the full path and kind on hover),
each linked to that line at the synced commit, then “+N more”. The links use each host's line anchors:

| Host | Link |
|---|---|
| GitHub (and GitHub Enterprise) | `…/blob/<sha>/<path>#L42` |
| GitLab (also self-managed) | `…/-/blob/<sha>/<path>#L42` |
| Bitbucket Cloud | `…/src/<sha>/<path>#lines-42` |
| Bitbucket Data Center | `…/projects/<KEY>/repos/<repo>/browse/<path>?at=<sha>#42` |

Without a commit link the lines are plain text. Each row also has **Show on diagram**, which opens a diagram showing the element
(both ends when one does) and selects it. Violations between two elements are drawn on diagrams as a **red dashed
edge** between the blocks (or between parts typed by them, on internal block diagrams). *Hide on diagrams* turns that
off.

## For integrators

- Core: `Meridian.Core.CodeSync.Conformance.ArchitectureConformance.Check(model, codeModel, options)` returns a
  `ConformanceResult` (violations and suppressed findings, sorted, deterministic). `ConformanceOptions.FromModel` reads
  the model-root fields above. `CodeModelJson.Pack/Unpack` store a `CodeModel` compactly (gzip + base64 JSON), use
  locations included. Additive contract: `CodeType.DependencyLocations` (`CodeDependencyUse(Symbol, File, Line, Kind)`,
  `CodeDependencyKind`), `CodeType.OmittedDependencyLocations`, `CodeDependencyUses.Cap`, and on
  `ArchitectureViolation` the `Locations`, `MoreLocations` and `FirstLocation`; all default to empty, so other readers
  and stored code keep working.
- Web: `ArchitectureCheckService.FileUrl(commitUrl, sha, path, line)` builds the per-host line links;
  `StatusFor(result, failPullRequests)` the status text with the first location.
- Web: `CodeSyncEvents.RunCompleted(request, outcome)` is raised by the runner after each run that did not fail;
  `ArchitectureCheckService` subscribes (through a hosted service), reads the files with the same reader, stores the
  code and the result (an integration row of kind `code-architecture` per connection) and posts the status through
  `IGitHost.PostCommitStatusAsync` inside `GitHostFactory.UseStatusContext("meridian/architecture")`. Hosts read
  `GitHostFactory.StatusContext` when they build the status.

## Limits

- Use locations are capped at 8 per (type, target) by the reader and 5 per violation in the report; the rest are only
  counted. C# records no `import` locations (its `using` directives name namespaces, not types).
- The hand-written readers (TypeScript, Java, Python) find uses from tokens, not with a type checker: any name that
  resolves to a scanned type counts, and the parts of a type written over several lines count at the line where the
  type starts.
- The code is read with the first registered `ICodeReader` that handles the pushed files (the "auto" reader: C#,
  TypeScript, Java and Python).
- Rule (c) compares operation names, not signatures.
- The run-completed check uses main's model, so architecture drawn only on a branch counts once merged (or with an
  on-demand check on that branch, which does not post a status).
