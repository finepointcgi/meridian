# Used projects (shared model libraries)

A project can **use** a package of another project — a parts catalogue, a requirements standard, shared interface
definitions — without copying it. This is Cameo's *used projects* (modules) and Enterprise Architect's shared packages.
The used package shows up read-only in the using project, pinned to a version, a branch head or a baseline; parts can be
typed by its blocks, requirements can be satisfied and traced to its requirements, and its elements can be shown on the
project's own diagrams. When the library moves on, the project reviews the change and moves its pin.

## Concepts

| Term | Meaning |
| --- | --- |
| **Usage** | One row of `project_usages`: project *P* uses package *X* of project *S*, pinned, shown below a mount point. |
| **Pin** | `version` (a committed version number of *S*), `branch` (the current head of a branch of *S*, live when someone edits it; `main` is the project itself) or `baseline` (a baseline id of *S*). |
| **Mount point** | The package of *P* the library is shown in; the model root by default. A mount point that is deleted falls back to the root. |
| **Library element** | An element of a used package as loaded into *P*: original id, read-only, not saved with *P*. |
| **Missing reference** | A reference from *P* to a library element that no longer exists (removed by an update, or in a library the reader can't see). Kept with its id, drawn as a dashed "missing reference" shape, and reported by validation rule **LIB-001**. |

## Design: a library overlay in `ModelRepository`

Library elements live **in the same `ModelRepository` as the project**, mounted below their mount point, with their
original ids. Everything that resolves references — `Find`, `OfKind`, the relationship incidence index, `ResolveType`,
diagram shapes and paths, the validator, traceability — works on them unchanged. What makes them library elements is an
**overlay map** (`Dictionary<Element, LibraryMount>`, `ModelRepository.Libraries.cs`) registered when a library is mounted:

- **Read-only.** Every public mutator (`Set`, `Add`/`Create`, `Move`, `Delete`, `CreateDiagram`, the diagram API) calls
  `EnsureEditable`, which throws `ReadOnlyElementException` ("… comes from the used project “Parts” (v3) and is read-only
  here. Open it in the source project to change it."). Replication operations that touch library elements are refused by
  the server's single write check (`PermissionCheck.Check` → `LibraryGuard`, for owners too) and ignored by replicas.
  Deleting a package that shows a library is refused until the usage is removed or moved.
- **What stays allowed.** Relationships *to* library elements (satisfy, trace, allocate, generalization …) are owned by the
  project — `Connect` picks a project owner even when the library element is the source; features can be typed by library
  classifiers; library elements can be dropped onto the project's diagrams.
- **Never saved.** `ProjectFile.Serialize` skips library roots wherever they are mounted. References to library elements
  are plain ids in the file (relationship ends, `type`, diagram shapes), so they survive save and load. Loading a project
  without its libraries turns unresolved ends and shapes into `MissingReference` placeholders that keep the ids, so saving
  again writes exactly the same document.
- **In memory the libraries travel along.** `ProjectFile.Serialize(model, includeLibraries: true)` (and `ProjectFile.Copy`,
  used for report snapshots, previews and redaction) adds a `"libraries"` section — each library's info and root package —
  which the reader mounts again. The live session sends each tab its document with the libraries *that participant* may
  read (`ProjectFile.WithLibraries`, which appends the section without re-serializing the project).
- **Changing a pin rebuilds the model** (deserialize the project with the new libraries) rather than mutating it in place:
  libraries are loader-owned, so no undo history, events or replication op exists for them.

Why an overlay in the same repository instead of a separate read-only repository consulted on lookup: every existing
feature (≈ 40 analyses, renderers, exporters and panels) resolves references with `Find` and walks `OwnedElements`; a
separate store would have needed a fallback in each of them. Mounting the library keeps them all correct, and the overlay
makes the read-only rule a single choke point.

### Performance

The overlay map only exists once a library is mounted; for models without libraries every check is a null test and the
project file is byte-for-byte unchanged. Measured with the quick benchmark runner on the 200k-element Cameo-scale model
(`dotnet run -c Release --project benchmarks/Meridian.Benchmarks -- --quick --elements 200000`):

| Scenario | ms |
| --- | --- |
| Load .meridian (deserialize) | 517 |
| Load + library of 8.5k elements (mounted) | 454 |
| Save .meridian (serialize) | 91 |
| Save, library mounted (same bytes as without) | 85 |
| Validate (full suite) | 85 |
| Validate, library mounted | 104 |

Library content at an immutable pin (a version or a baseline) is cached by the server (bounded, 64 packages).

## Loading

When a session opens (`CollaborationHub.JoinAsync`), `LibraryLoader.ResolveAsync` reads the project's usages and loads
each used package at its pin:

1. **Pins** resolve to a stored version, a branch head (the live session's model when the branch is open) or a baseline's
   version. A branch pin's resolved value carries a content hash of the package, so a changed head is detected.
2. **The package** is extracted from the source model (`LibraryContent.Extract`) with its owned diagrams. Relationships of
   the library whose other end is outside the shared package are left out (read-only, nothing to keep); library diagram
   shapes of such elements are dropped.
3. **Transitive libraries.** The source project's own usages are followed breadth first (`LibraryResolver`):
   - a library that uses the project being loaded, or one already on its own path, is a **cycle** — skipped with a warning;
   - the same package of the same project reached twice at the **same pin** (a diamond) is loaded once;
   - the same package at **two different pins** is a warning; **the first one reached wins** (direct usages come first,
     then in the order they were added);
   - a nested library mounts where its own project mounted it when that package was loaded (inside the outer library),
     otherwise where the library that brought it in is mounted.
4. **Ids already in the model** (a copy imported earlier, or a duplicate) are not mounted again: the project's own element
   wins, and references resolve to it.

Warnings are shown in the **Used projects** dialog.

## Permissions

- **Adding or changing a usage** needs an editor or owner role on the using project, at least **read** on the source project,
  and the package must be visible to the user there (not hidden by a package rule). A usage that would close a cycle is
  refused.
- **Who sees what.** Each participant gets only the libraries they can read: a role on the source project, with the source
  project's package rules applied (`ModelRedactor`, exactly as in the source project — hidden packages stay hidden, placeholders
  for hidden shapes). A library whose mount point is hidden from the participant in the using project is left out too.
  Participants without access keep the references as missing references (validation LIB-001) — nothing is dropped.
- Library elements are read-only for everyone in the using project; the inspector offers **Open in source project**.
- **Open in source project** needs a role on the source project and opens it as that user sees it there (`PackagePermissions.RedactJson`
  with their view; hidden packages stay hidden). A version or baseline pin opens as a *viewer* (the session refuses edits too),
  whatever the user's role.
- **Suspect marks** are written by the system on the project's own elements only (never on library content, which stays
  read-only and unsaved). Their text names library elements as the *accepting* user's review showed them, so a package hidden
  from that user is never named. Accepting needs an editor or owner role on the using project, as before.
- **Upstream notices** go only to owners and editors of the using project who have a role on the source project and can see
  the used package there; the text names projects and versions, never elements.

## Using a library

- **Model explorer.** The library's root package shows below its mount point with a badge naming the pin (`v3`,
  `main (head)`, `baseline PDR`); its content is in italics. The context menu offers *Show on diagram*, *Open in source
  project* and *Used projects…* only.
- **Diagrams.** Library elements shown on the project's diagrams carry a small marker (top right, with a tooltip naming the
  library and pin). Library diagrams open read-only, with a banner naming the used project.
- **Open in source project** (inspector, tree menu, ⌘K, the phone's element sheet) respects the pin
  (`Workspace.OpenAtPinAsync`):
  - a **branch** pin joins that branch's live session (`main` is the main line), editable for the source project's editors;
  - a **version** or **baseline** pin opens a **historical view** (`Workspace.Pinned`): the tab joins the source project's
    main session *as a viewer* (so it is listed as present and the server refuses any edit), then installs the stored version
    (`LibraryLoader.LoadAtPinAsync`, the same loader the libraries use) redacted for the user, with the source project's own
    libraries as that user may read them. While it is shown, `Workspace.ReadOnly` is true, undo history is read-only, and
    remote operations, resets and replacements from the live session are ignored (they belong to the head). A banner
    (`PinnedViewBanner`) says **Viewing version 3 (pinned)** / **Viewing baseline PDR — version 5 (pinned)**
    (`LibraryPins.ViewLabel`) with **Go to latest**, which opens the main line normally.
- **Phones** (`Components/Mobile`): the model list shows a library's root package with its pin badge (`m-lib-badge`), its
  content in italics with "read-only" in the subtitle, and a "From … — read-only" hint inside it; the element sheet names the
  used project and offers *Open source*. The top bar shows a library icon with the number of updates
  (`MobileLibraryUpdates`); one update opens its review, several are listed in an action sheet first. The review
  (`LibraryUpdateDialog`) opens full screen on a phone (`lib-sheet` styles: wrapped header, diagrams and changes fitting the
  width, large buttons). Checked at 390×844 without horizontal scrolling (E2E `LibraryGapsTests`).
- **Typing and linking.** The inspector's type picker lists library blocks and value types. Drag a library requirement onto
  a diagram and draw satisfy / trace / allocate to it, or use the traceability tools; these relationships belong to the project.
- **Exports.** XMI, SysML v2 text and the HTML report ask whether to **include library content** (inline, as if it were part
  of the project) or export **references only** (`LibraryExport`: library elements become stand-ins named by their qualified
  name, and features typed by them are typed by that name). Document generation snapshots include library content.
  Downloading the `.meridian` file never includes it.

## Updates

- **Check for library updates** runs when a project opens, from ⌘K, and from the ribbon's **Libraries** button: a newer
  committed version for a version pin, a changed head for a branch pin, a newer baseline for a baseline pin. Updates show
  as a badge in the title bar and on the ribbon button.
- **Update used project…** opens a review: `ModelDiff` between the old and new pins (added / changed / removed, with field
  detail), the library's changed diagrams with the code-sync change highlighting (removed shapes as ghosts), the project
  elements that will become suspect and the references that will break.
- **Accept** moves the pin (`project_usages`), rebuilds every live session of the project with the new content, and marks
  the project's references to changed library elements as **suspect** through the suspect-link mechanism
  (`ReqFields.LibraryChange`): trace links appear in the suspect lists and traceability table, other elements (a part typed by
  a changed block) in validation **TRC-001** and in the inspector with *Mark reviewed*. A library element counts as changed
  when it or anything it owns changed. It is **one undo step**: ⌘Z moves the pin back and clears the marks it set.
- **Where the marks go.** Suspect marks belong to the project that accepts the update, at the moment it accepts it. The pin
  is the project's (one `project_usages` row for all its lines), so every line of that project moves, and so does every
  project that uses it, directly or through other libraries (they load its usages transitively, `DependentsAsync`).
  `LibraryService.ApplyUpdateAsync` runs **one marking function** (`LibraryUpdates.MarkSuspects` with the reviewed change
  set) through two paths:
  - lines with a live session are rebuilt (`ProjectSession.ReloadLibraries`), marked, and persisted at once;
  - lines nobody has open are changed through `CollaborationHub.ChangeClosedLineAsync`, which holds the session-open gate (no
    session can open that line meanwhile), loads the stored head with the project's current libraries, marks it, saves it as
    the head and commits it as a **system version** (no author) named *Library update: “Parts” v3 → v4 (suspect references
    marked)*. A line where nothing new was marked gets no commit.

  `LibraryUpdateResult.Lines` records the marks per project and line (and the system commit, if any); undo clears them the same
  way (live sessions rebuilt, closed lines changed by a system commit *Undo library update …*).
- **Tabs survive.** Installing a rebuilt model (accepting or undoing an update here, or receiving one from the session —
  `Replaced`, also used for merges and resyncs — and version restores) keeps every diagram tab whose diagram still exists, in
  order, with its zoom, scroll position, active layer and canvas selection (`DiagramController.TakeViewFrom`); the active tab
  stays active (or its left neighbour when its diagram was removed), view tabs of saved elements and the code-change and
  architecture reviews stay open, the selected element and back/forward history are kept (`Workspace.CaptureTabs` /
  `RestoreTabs`). Only when nothing is left do the default diagrams open.
- **Library changed upstream.** When a project commits a version of its main line (`VersionControl.CommitAsync` →
  `ProjectSession.VersionCommittedAsync`, and merges into main), `LibraryNotices.VersionCommittedAsync` looks at the projects
  that use one of its packages pinned to an older **version**. When this commit changed that package (compared with the
  previous version) and it differs from the pinned content, their owners and editors who may read it (see Permissions) get a
  notification of kind `library_update` — the usage id in the notification's element id — with **Review update**, which opens
  the project, checks for updates and opens that usage's review. At most one unread notice per usage and user; each user can
  turn them off (Settings ▸ Editor ▸ *Library changed upstream*, `UserPreferences.LibraryUpstreamNotices`). A failure is
  logged and never fails the commit. System commits (suspect marks) don't notify.
- **Removed library elements** that are still referenced become **missing references** (see above): kept, drawn as dashed
  placeholders and reported by LIB-001 until someone retargets or deletes the reference.

## Managing usages

**Project ▸ Used projects…** (also from **Share ▸ Used projects…** and ⌘K): the usages with their pin, mount point, who
added them and when, an update badge, and **which elements refer to each library** (click one to select it). Add a usage
(pick a project → pin kind → version / branch / baseline → package → mount point), change its pin or mount point, or remove
it — removing warns when elements still refer to it (the references are kept as missing references).

## Cameo import

`MagicDrawImporter` records the used projects (modules) an `.mdzip` refers to: the project metadata
(`com.nomagic.ci.metamodel.project` / `project.xmi`: `projectUsages` with `usedProjectURI`, `LOCAL_PROJECT_ID` properties and
`mountPoints` whose `mountedPoint` names the mounted shared package; the project's own `sharePoints`), the used-project proxies
(`proxy.local__…`, `proxy.twcloud__…`) and `href`s into other projects (`…mdzip#id`, `local:/…`, `twcloud:/…`). URIs map to
display names (`MagicDrawImporter.UsedProjectName`: file name for local / file URIs, the `projectName` query parameter or
"Teamwork Cloud project <id>" for Teamwork Cloud). Standard profiles (by module name, or by mounting the SysML / UML Standard
Profile root package ids) go to `MagicDrawImportReport.StandardProfiles`, not `UsedProjects`. Each used project's `Outcome` is
`Referenced`, `Copied` (a mounted shared package is in the file) or `Linked` (set by `UseLibraries` when its `used` argument is
given); `SuggestPackages` prefers the mounted shared packages. Types from them are kept
by name and relationships to them are kept aside, as before. When a used project already exists in Meridian — same Cameo
project id (`md:projectId` on the imported root) or the same name — **Open a file as a new project** offers to **use the
existing project instead of copying**: copies of its packages are left out of the new project, features typed by its
elements get their ids back, relationships to it are created, and a usage is added for each referenced top-level package
(pinned to its latest version, else `main`). Copying stays the default and the fallback.

## Storage

```sql
create table if not exists project_usages (
    id               text/uuid primary key,
    project_id       references projects(id) on delete cascade,
    used_project_id  not null,              -- no foreign key: a deleted used project leaves the usage, reported as unavailable
    root_package_id  text not null,
    pin_kind         text not null check (pin_kind in ('version', 'branch', 'baseline')),
    pin_value        text not null,
    mount_point_id   text,                  -- null: the model root
    added_by         references users(id) on delete set null,
    added_at         timestamp,
    unique (project_id, used_project_id, root_package_id));
create index if not exists project_usages_used on project_usages(used_project_id);
```

Both stores create it idempotently (`create … if not exists`; PostgreSQL inside the migration transaction under the schema
advisory lock; SQLite's `create … if not exists` is atomic, and columns added later must use the pragma check +
duplicate-column catch of `SqliteStore.Admin.cs`). The store contract tests (`StoreContract.Usages.cs`) run against SQLite
and a throwaway Testcontainers PostgreSQL.

## Limits

- A participant who can't read a library sees references to it as missing references; operations of others that create new
  references to library elements reach such a participant only on their next resynchronisation.
- Upstream notices are sent for version pins only: a branch pin follows its head live (it can't fall behind), and a baseline
  pin moves when a baseline is made, which doesn't notify.
- A historical (pinned) view mounts the source project's *current* libraries, not the ones it used at that version.
- A project that uses the accepting project through a **baseline or version** pin still moves to the new pin of the library
  (usages are followed as they are now, not as they were at that version), so it gets the marks too.
- The Cameo import recognises used projects from `href`s, project metadata and proxies; read-only flags and auto-load options
  of a usage are not interpreted. Cameo's project metadata format is not published: the names read are those seen in
  exported projects, and anything missing falls back to the references in the model.
