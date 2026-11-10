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
to it are rejected until you **Unlock** or close your tab. The tree shows a lock icon with the holder's name. Project
owners can **Break lock**. Locks are optional; most teams rely on live co-editing and merging instead.

## Live collaboration (server)

Everyone who opens the same project on the same line edits one shared model in real time. The title bar shows who is
there; click someone's avatar to jump to what they have selected. Viewers (the
*viewer* project role) see changes live but cannot edit.

## Project roles (server)

**Share & members…** in the project menu adds people with a role: **owner** (everything, including members, scripts
and integrations), **editor** (edit the model) or **viewer** (read only). A team can also be given a role on a project;
a member's effective role is the higher of their direct and team roles. See [Administration](administration.md).
