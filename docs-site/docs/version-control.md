# Branches, merge and baselines

Meridian keeps a project's history the way Teamwork Cloud or a version control system does: you commit versions,
branch to work in isolation, merge back with a three-way merge, and tag important versions as baselines. This works
in the desktop app's local workspace and on a server.

## Saving and committing

Edits are saved continuously; the status bar shows the save state. A **version** is a named snapshot in the project
history:

- **Commit** in the title bar, **⌘S** / **Ctrl+S**, or **Commit version…** in the project menu asks for a message
  and records the current model as the next version on the line you are working on.
- **Version history…** (project menu or the clock icon) lists versions. Select one to compare it with the current
  model: added, removed and changed elements with their field differences. **Restore** replaces the model with that
  version for everyone in the project (the current state is committed as a version first, so you can restore it again), **Download** saves it as a `.meridian` file, **Branch** starts a branch from
  it and **Baseline…** tags it.

> [!NOTE]
> **TODO (screenshot):** the version history dialog comparing a version with the current model.

## Element history

**Who changed this, and when?** Every element has its own history, like Cameo's element history in Teamwork Cloud or
Enterprise Architect's element audit:

- Open it from the element's context menu (**History…**, in the model explorer and on diagrams) or from the
  **History** section at the bottom of the inspector.
- The timeline lists every version that changed the element, newest first: version number, message, author and date,
  what happened (**Added**, **Changed**, **Moved**, **Removed**, **On diagram** / **Off diagram**) and which fields
  changed. Merge commits carry the changes they brought in and say which branch they came from (*Merged from
  “feature”*); code-sync and system commits are labelled. An element that was already in the project's first version
  ends with a **First version** entry.
- **Uncommitted (current session)** at the top shows what changed since the last commit of the line you are on, and
  who made the latest edit.
- Click an entry for a **field-by-field diff**: before and after for every changed field, text with an inline word
  diff, and references (types, relationship ends, owners) by name. **Compare with current** compares that version
  with the model as it is now.
- **Restore** next to a field sets that one field back to its value in that version, as a normal edit: it reaches
  everyone at once and **Undo** takes it back. (A reference to an element that has since been deleted can't be
  restored.)
- **Show model at vN** opens the whole model at that version, read-only, with the element selected; **Go to latest**
  returns to the line you were on.
- **Relationships** adds the relationships that start or end at the element (for example a «satisfy» that was added
  and later removed). **All branches** shows changes made on every branch instead of only the history of the line
  you are on (a branch's history is its own commits plus the history of the line it was made from).

History follows [package permissions](permissions.md): there is no history for elements hidden from you, entries from
when an element or relationship was somewhere you can't see are left out, and every diff is read from versions with
the packages hidden from you removed.

### How it works

When a version is committed, Meridian compares it with the previous version of the same line in the background and
stores one small row per changed element (what changed and which fields), so opening an element's history reads a
handful of index rows instead of old versions. Field values are read from the two versions only when you open a diff.
Projects that already had versions are indexed the first time someone opens a history: the panel shows *Indexing
history… 34 of 100 versions* while it catches up, oldest first, and an interrupted run resumes where it stopped. On a
200,000-element project, opening a history takes well under a tenth of a second once indexed; see `docs/PERFORMANCE.md`.

## Branches

The branch picker next to the project name shows the line you are on (`main` or a branch).

- **Switch branch** re-opens the project on another line. Live collaboration happens per line: people on `main` and
  people on a branch do not see each other's edits until they are merged.
- **Manage branches…** lists branches with who created them and when, creates a **New branch** (from the current head,
  or from a committed version in the history) and deletes branches.
- Branch names are 1–60 characters: letters, digits, spaces and `- _ . /`. `main` is reserved for the project's main
  line.

## Merging

From **Manage branches…** choose **Merge into** (merge the branch into `main`) or **Merge from**; from the branch
picker, **Update from main…** brings the latest `main` into the branch you are on.

Meridian runs a **three-way merge** between the two heads and their merge base (the last state the branch shares
with the line it came from). Changes that do not touch each other combine automatically. The merge dialog lists the
rest as conflicts:

| Conflict | When |
| --- | --- |
| Field | the same field was changed to different values on both sides |
| Delete / modify | one side deleted an element (or its package) that the other side changed, extended or referenced |
| Move | both sides moved the element to different owners |
| Move / edit | one side moved the element while the other edited it in its old place |
| Diagram shape / path | a shape or path was changed differently on both sides, or removed on one side and changed on the other |

For each conflict choose **ours** (the target), **theirs** (the source) or, for field conflicts, a **custom value**
(empty clears the field). The dialog shows the base value and previews the incoming changes. Enter a merge commit
message and confirm: the result is committed as a new version on the target, becomes its head for everyone editing it,
and the merge base moves forward so the next merge only sees newer changes.

> [!NOTE]
> **TODO (screenshot):** the merge dialog with one field conflict and the incoming changes preview.

## Baselines

A **baseline** is an immutable, named tag on a committed version, for example `PDR 1.0` before a design review.

- **Baselines…** (project menu or branch picker) lists baselines with their version, branch, author and date.
- **New baseline** tags a version (name 1–80 characters, optional description).
- Pick two baselines, or a baseline and the current model (*Compare with the current model*), to see what changed
  between them; **Export CSV** writes the comparison.
- A review can compare against a baseline, and **Request review** can commit a new baseline for that purpose (see
  [Reviews](reviews.md)).

## Element locks (server)

On a team server you can take a pessimistic lock in the model explorer's context menu: **Lock for editing** locks one
element, **Lock with contents** locks a package and everything in it. Others can still see the element but their edits
to it are rejected until you **Unlock** or close your tab (the rejected edit is undone in their tab, with a message saying
who holds the lock). The tree shows a lock icon with the holder's name. Project owners can **Break lock**. Locks are an
optional "check-out": live co-editing is safe without them (see below), so most teams only lock what they want to keep
others out of for a while.

## Live collaboration (server)

Everyone who opens the same project on the same line edits one shared model in real time. The title bar shows who is
there; click someone's avatar to jump to what they have selected. Viewers (the
*viewer* project role) see changes live but cannot edit.

Your own edits apply instantly, and everyone always ends up with the same model, even when two people change the same
thing at the same moment:

- **The last edit wins, per field.** If you and a colleague rename the same block at once, both screens settle on the
  name that reached the server last. Different fields of the same element (say, its name and its documentation) never
  overwrite each other, and neither do shapes and other elements.
- **Deleting beats editing.** If someone deletes an element while you are changing it, the delete wins and your change is
  dropped; the status bar tells you ("Someone else deleted “Engine” — your change to it was dropped."). Relationships,
  shapes and paths of a deleted element go with it on every screen.
- **No impossible moves.** If two people move two packages into each other at the same moment, the second move would
  put a package inside itself; it is refused and undone on that person's screen with a message.
- **Undo is yours.** Undo and redo only take back your own edits, never a colleague's. If part of what you undo was
  deleted by someone else meanwhile, that part is skipped and the status bar says so.
- **Refused edits are undone in place.** An edit the server refuses (a package that is read-only for you, someone's
  lock) is taken back on your screen with the reason; the rest of your work and your undo history stay as they were.

## Project roles (server)

**Share & members…** in the project menu adds people with a role: **owner** (everything, including members, scripts
and integrations), **editor** (edit the model) or **viewer** (read only). A team can also be given a role on a project;
a member's effective role is the higher of their direct and team roles. See [Administration](administration.md).
