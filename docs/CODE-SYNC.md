# Code sync

A push to a connected GitHub or GitLab repository updates a **review branch** of the model from the code. Diagrams that
show changed elements are refreshed, the differences are highlighted, and an owner or editor accepts all or some of the
changes into `main`.

## Reviewing code changes

### Where the changes go

Every connected repository has its own review branch, named `code-sync/<repository>` (for example
`code-sync/acme/sat-fsw`). The first push that changes the model creates it from `main`'s current head; later pushes
**accumulate** on the same branch until they are reviewed. If `main` moves on in the meantime nothing needs rebasing: the
accept is a normal three-way merge.

Each push that is applied:

- reads the pushed source files and maps them onto the branch's model (a push that only touches files no reader handles, or
  that maps to no model change, is recorded as *No changes* and creates no branch);
- adds newly created elements to the existing diagrams they belong on — a new block to the BDD of its package, a new part to
  the IBD of its block, a new child to a package or block drawn as a container — placed in free space below what is already
  there. Shapes people positioned never move (a container may grow to hold a new child);
- creates diagrams for new namespaces and blocks with the auto-diagrammer (a structure diagram per new package, an IBD per new
  block with parts). A project turns this off by setting the model root's `codeSync.autoDiagrams` field to `false`;
- commits the result on the branch as `Code sync: <short sha> <first line of the commit message>` (authored by the system);
- records a **sync run**: commit, link, author, message, status, summary, counts and the list of changed elements.

Anyone with the branch open sees the update at once. A push that only carries the changed files (not a full snapshot)
never deletes elements.

### The review

When a code-sync branch has changes waiting, the title bar shows **Code changes (N)** next to the branch picker. It opens
**What changed**, a tab next to the diagrams (also on ⌘K “What changed”, and in the *Code* ribbon group). The commit-status
link a git host shows opens the project on the review branch with this tab open
(`/?project=<id>&branch=<id>&panel=code-changes`).

What changed lists, per review branch:

- the pushes waiting for review — short sha (linked to the commit), message, author, time and summary. Ticking a push selects
  all of its changes;
- the changes, grouped into **Added**, **Changed** and **Removed**, each with its kind, name and owner path. A changed element
  expands to show its field changes (before → after). The list is the *net* difference between the branch and what `main`
  last took from it, so a change a later push undid disappears;
- **Show on diagram** opens a diagram that shows the element (switching to the review branch for an element that only exists
  there) and selects it.

Filter by name, path or kind, tick changes (or **Select all**), then:

- **Accept selected** merges them into `main`. Changes you did not select keep `main`'s version and stay on the branch for
  later. If someone changed the same thing on `main`, the usual merge dialog asks which side to keep.
- **Reject selected** puts those elements on the review branch back to `main`'s version. A later push that still contains
  them brings them back (the code is the source of truth).

A push whose changes have all been decided is closed (*Accepted*, or *Rejected* when none were accepted). Only project
owners and editors can accept or reject; viewers can read the review.

### Highlights on diagrams

On a review branch, diagrams highlight what the code changed (toggle: the legend on the canvas, the *Code ▸ Highlight*
ribbon button, or ⌘K “Highlight code changes”; it is on by default when you open the branch):

| Change  | Drawn as |
|---------|----------|
| Added   | green outline |
| Changed | amber outline |
| Removed | a faded, dashed “ghost” at the place `main` has it, until accepted or rejected |

On `main` the same toggle outlines the elements a pending review would change or remove. The colours follow the theme's
status colours, so they read in light and dark mode.

### For integrators

- `ICodeSyncRunner.RunAsync(CodeSyncRequest)` (the git-host integration calls it after fetching files) returns a
  `CodeSyncOutcome`: `AwaitingReview` with the review link, `NoChanges`, or `Failed` with the reason. Runs of one project are
  serialized; review decisions take the same lock.
- `CodeSyncReviewService.Accepted` is raised once per run when its last pending change is decided and at least one was
  accepted, with `(outcome, connectionId, commitSha)` — use it to update the commit status. `Rejected` is raised when all of a
  run's changes were rejected.
- Runs are stored in the `code_sync_runs` table (SQLite and PostgreSQL), one row per push, with the change list as JSON.
- The model root's `codeSync.rootPackage` field names the package code is mapped into (default `Code`).
