# Per-package permissions

Project roles (owner / editor / viewer) say what someone may do in a whole project. Package permissions refine that per
package, in the style of Teamwork Cloud: a contractor can edit only their subsystem, a supplier's library is read-only,
and an export-controlled package is hidden from people who must not see it.

## Levels

| Level | The package's contents |
| --- | --- |
| **none** | Hidden. The package itself is still listed (its name belongs to its parent), with a red lock badge and no children. |
| **read** | Visible, not changeable. |
| **edit** | Changeable. |
| **manage** | Changeable, and the user may change the package's permissions. |

## Resolution

- A rule names a package (by element id), a **user or a team**, and a level. Rules are stored per project in the
  `package_permissions` table (both stores; see below).
- **Inherited down the containment tree; the nearest explicit rule wins.** A rule on `Structure::Power` beats one on
  `Structure` for everything inside `Power`.
- At one package a rule for the **user** beats rules for their **teams**; among several team rules the **most
  permissive** wins.
- Without a rule, the project role decides: editors **edit**, viewers **read**. A rule never lifts a viewer above read.
- **Project owners always manage everything**; rules don't apply to them.
- Everything below a hidden package stays hidden, whatever rules sit further down (a nested grant can't reveal it).
- A **relationship** with a hidden end is hidden; a relationship can only be created or changed when its owner **and
  both ends** are editable.
- A **diagram** has the level of the package that owns it. A diagram in a visible package that shows hidden elements
  keeps those shapes as nameless, hatched **"Restricted" placeholders** (same place and size; no name, kind, style or id
  of the hidden element). Connectors to placeholders are not shown.

The resolver is `Meridian.Core.Permissions.AccessView`; unit tests are in `tests/Meridian.Tests/PackagePermissionTests.cs`.

## Managing permissions

- **Package context menu ▸ Permissions…** (server mode): the package's rules, the inherited ones above it, and — for
  owners and managers of the package — a form to grant a user or a team (project members, and teams with a role on the
  project) a level, change it or remove it.
- **Share ▸ Package permissions**: every rule of the project, with the same controls for the packages you manage.
- Changes are audited (`project.permission-set`, `project.permission-remove`) and take effect at once for everyone
  connected (`CollaborationHub.ReloadPermissionsAsync`). Every change is authorised again on the server from the store.
- Team membership and project-role changes take effect when the user next opens the project.

In the workbench: protected packages carry a lock badge in the tree (red: hidden from you, amber: read-only for you,
grey: has rules you manage); a read-only diagram shows a "Read-only — “Package” is protected" banner, a framed canvas and
no shapes panel, and refuses edits; the Properties panel says when the selection is read-only for you.

## Enforcement (writes)

Every model edit reaches the authoritative model through `ProjectSession` (`CollaborationHub.cs`), and every operation
is checked there by `PermissionCheck.Check` (in `ProjectSession.Locks.cs` → `AllowEdit`, next to the role and
element-lock checks) before it is applied — the UI's disabled buttons are only a convenience. The operations of one user
action (a delete with its cascade, an import, a script batch) are sent together and accepted **all or nothing**
(`ProjectSession.SubmitAll`). A refused action is not applied; the user's tab is resynchronised and told why, e.g.
“Structure” is read-only for you (package permissions) — your change was not applied.

Covered: setting fields, creating and deleting elements (with everything they contain), **moves into or out of a
protected package**, **relationships whose source or target is protected**, and any change to a **diagram owned by a
protected package** (shapes, connectors, layout, style). Showing a hidden element on a diagram is refused too.

The same check applies to every write path:

| Path | How |
| --- | --- |
| Manual edits, SysML v2 text apply, derivations, tables | Workspace operations → session check |
| Scripts (`ModelApi`) | Operate on the workspace model → session check |
| AI assistant apply | Pre-checked with a clear message (`AssistantService.Apply`), then the session check |
| Mermaid import | Pre-checked for the target package, then the session check |
| XMI / file import | Creates a new project the user owns |
| Merges (`VersionControl.CompleteMergeAsync`) | `PermissionCheck.CheckReplace`: every element the merge adds, removes, changes or moves must be editable by the author, before anything is committed; users with hidden packages can't merge |
| Version restore (`ProjectSession.Reset`) | `CheckReplace` against the live model |
| Code-sync accept / reject (`CodeSyncReviewService`) | Accept merges through `CompleteMergeAsync`; reject is checked with `CheckReplace`; users with hidden packages can't review code changes |
| OSLC creation, scheduled requirements-tool sync (`ServerModelEdit`) | Edit the user's filtered model; operations submitted together through the session check; a refusal is an error (OSLC: 409) |
| Code-sync runs and replays (`ProjectSession.ApplySystemOps`, `CodeSyncRunner.WriteAsync`) | System writes on the review branch (no user); relayed filtered. Given a user's view, `ApplySystemOps` checks every operation first, all or nothing |

## Redaction (reads)

A user with hidden packages gets a **filtered copy** of the model (`ModelRedactor`): hidden elements and relationships
are removed, references from visible elements to hidden ones (a part's type, …) are cleared, and diagram shapes become
placeholders. The server never sends them hidden data:

- **Their workspace model** (Blazor Server keeps it on the server, but everything rendered — tree, diagrams, search,
  tables, matrices, reports, exports, validation, the assistant's context and tools, scripts' reads — is built from it)
  is installed from `ProjectSession.SnapshotFor(participant)`.
- **Generated reports** are written from a snapshot of that model and downloaded from `GET /api/reports/{id}/file` by
  their owner only, while still a project member. The job records which packages' contents the owner could see; if a
  rule later hides one of them, the download is refused (403, "generate it again").
- **Live collaboration**: others' operations are relayed only when they touch what the user can see; operations inside
  hidden packages are dropped; an operation that changes what they can see (a move across the boundary, a new view of a
  hidden element) is replaced by a fresh filtered model (`PermissionCheck.Probe`/`Decide`). Merges, restores and code
  syncs replace the model per participant, filtered. Rule changes refresh affected users at once.
- **Shared SysML v2 text** (exported from the whole model) is not offered to them; their text panel works on their own
  filtered model.
- **REST API** (`/api/projects/{id}/commits/{commit}/…`, head and stored versions) and **OSLC** responses are filtered
  per caller.
- **Version history, baselines and reviews** filter stored versions before comparing or downloading them.
- **Notifications**: comments on elements a user can't see don't notify them.
- **Code-change reviews** (they show main's whole model) and **merging** are unavailable to them; the architecture
  check runs on their filtered model but doesn't store or post its result.

## Storage

```
package_permissions (
  project_id      → projects(id) on delete cascade,
  element_id      the package's element id,
  principal_kind  'user' | 'team',
  principal_id    user or team id,
  level           'none' | 'read' | 'edit' | 'manage',
  updated_by, updated_at,
  primary key (project_id, element_id, principal_kind, principal_id))
```

PostgreSQL migrates it inside the schema transaction under the advisory lock; SQLite's `create table if not exists`
is atomic (columns added later must use the duplicate-column guard of `SqliteStore.Admin.cs`). Deleting a project or a
team deletes its rules. Contract tests: `tests/Meridian.Tests/StoreContract/StoreContract.Permissions.cs` (SQLite and a
throwaway Testcontainers PostgreSQL).

## Limits

- Permissions protect model **content**. Scripts run arbitrary .NET code in the server process (administrators, or
  owners when allowed), so a script can read anything; its model edits still go through the session check.
- Admin backups contain whole projects (administrators are trusted); restoring a project from a backup doesn't restore
  its package permissions.
- Webhooks, issue-tracker links and pull-request previews / commit statuses go to external systems configured by
  owners, from the whole model.
- Review comment bodies on hidden elements stay visible to the review's participants (they are discussion, not model
  data); element comments are only shown on visible elements.
- Desktop (local) mode is single-user: permissions don't apply there.
