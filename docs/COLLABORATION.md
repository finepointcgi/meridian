# Live collaboration: how every replica converges

Every browser tab on a project keeps a full replica of the model and edits it directly, so edits feel instant. One
`ProjectSession` per project line (main or a branch) holds the authoritative model. This page explains how the replicas
stay equal to it without locks, and what users see when two people change the same thing at once.

## The design: server-authoritative sequencing with client rebase

- **One order.** The session applies every user action (a *batch*: one rename, or a delete with its cascade, an import,
  a script run) under its lock, all or nothing, and gives each operation the next sequence number
  (`ProjectSession.SubmitBatch`). It relays each operation, with its number, to every other tab, and sends the author an
  **acknowledgement** (`ISessionClient.Acked`). Operations, acknowledgements and whole-model replacements reach a tab
  through one FIFO inbox, in the session's order.
- **Optimistic local edits.** A tab applies its own action at once, then submits it (`Workspace.Collaboration.cs`). Until
  the acknowledgement arrives the batch stays *pending* in a `ReplicaSync` (`src/Meridian.Core/Collaboration`), together
  with a `RawJournal`: the exact inverse of every primitive mutation the batch made.
- **Rebase.** Because the inbox is FIFO, a remote operation that arrives while a batch is pending was ordered *before*
  that batch. The tab rewinds its pending batches (journal inverses, newest first), applies the remote operations, and
  re-applies its batches exactly as the session did (`ModelRepository.TryApplyRemote` semantics). Consecutive remote
  operations are applied in one rebase. Afterwards the replica is the session's model plus the tab's not-yet-confirmed
  batches, so once all acknowledgements are in it equals the session's model. A replacement (merge, version restore,
  permission change, filtered resync) is installed and the pending batches are re-applied on top of it, since the session
  applied them after the snapshot it sent.
- **Identity is kept.** Re-applied operations reuse the objects the batch created the first time (elements, shapes,
  paths), when those still hold exactly what the operation describes, so undo history, selection and open diagrams keep
  pointing at live objects.
- **Refusals roll back in place.** A batch the session refuses (role, package permissions, someone else's element lock,
  a move that would make an ownership cycle) is not applied or relayed. The result comes back synchronously; the tab takes
  the batch back with its journal, removes its step from the undo history (`CommandStack.DiscardLastAction`) and shows the
  reason. No resynchronisation, no lost history, other pending batches untouched.

For this to work, a local primitive and the remote application of the operation it emits must give the same model. So
every primitive in `ModelRepository` skips (and emits nothing) exactly when the server would skip: it acts only on the
model's own objects (an undo step holding a copy of something that was deleted and re-created by someone else is
skipped), and cascades are the same on both sides — removing a shape removes its paths; detaching an element removes the
views of its subtree and the relationships from elsewhere that touch it. Building an attached subtree from a snapshot
(`Materialize`) leaves out, deterministically, ids already in the model (ids stay unique), relationships whose end is gone,
and shapes or paths of missing elements.

### Why not per-field last-writer-wins?

Per-field LWW with server sequence numbers handles fields, but not the structural operations: inserts and moves carry
list positions (children, operands, diagram shapes), deletes cascade to relationships and views, and creates carry whole
subtrees. Each would need its own transformation or tombstone rule, and the combination is where divergence hides.
Rebasing re-uses the one thing that is already deterministic — the session's apply of each operation in order — for every
kind of operation, and it makes the answer to "what happens when…" always "what the server's order says". It costs
nothing when there is no conflict (the fast path applies remote operations directly) and microseconds when there is
(see below).

## Conflict rules (what users see)

| Situation | Result |
|---|---|
| Two people set the same field | The later one in the session's order wins, on every screen. Different fields never interfere. |
| Delete vs. edit of the same element | The delete wins. An edit ordered after it is dropped; the editor sees "Someone else deleted “X” — your change to it was dropped." An edit ordered before it is deleted along with the element. |
| Move vs. move of the same element | The later move wins. |
| Moves that would create a cycle (A into B, B into A) | The later batch is refused and undone in its tab: "“B” can't be moved into “A”: someone just moved “A” inside it. Your move was undone." |
| Move into / create inside a package deleted meanwhile | Dropped (the element stays where it was / is not created). |
| Relationship to an end deleted meanwhile | Dropped. A relationship created before the end was deleted goes with the end. |
| Shapes moved by several people | The last bounds win. A shape for an element deleted meanwhile is dropped; a path to a shape removed meanwhile is dropped. |
| Inserts at the same position (children, operands, diagram shapes) | Both are kept, in the session's order; every replica has the same order. |
| Two people undo the same delete | One copy comes back (ids stay unique). |
| Undo after others changed things | Undo takes back only your own action. Parts whose target someone deleted are skipped: "Someone else deleted part of what this changed — that part was skipped." An undo the session refuses is rolled back and stays undoable. |

## How it fits the rest

- **Element locks** are an optional check-out: an edit inside someone else's lock is refused and rolled back in place with
  the existing message.
- **Package permissions**: the session's check of every operation is unchanged; a refusal rolls back in place.
  Participants with hidden packages still receive only what they may see; when an operation changes what they can see,
  their tab gets a fresh filtered model, and their pending batches are re-applied on it.
- **Session snapshot cache**: unchanged (keyed by sequence). A joining tab gets the snapshot with its sequence number and
  skips relayed operations up to it.
- **Code sync** (`ApplySystemOps`) operations are sequenced and relayed like anyone's; tabs rebase under them.
- **Shared SysML v2 text** has its own operational transformation (`TextOperation`) and is untouched.
- **Libraries**: library elements stay read-only on every replica and in the session's check.
- **Branches and merges**: each line has its own session and order; a merge replaces the model (`Replace`) and every tab
  re-applies its pending batches on the result.

## Tests

- `tests/Meridian.Tests/ConvergenceTests.cs`: an in-memory session with scripted interleavings for every rule above, and a
  property test over seeded random interleavings of three to five replicas (2,000 seeds by default;
  `MERIDIAN_CONVERGENCE_SEEDS`, `MERIDIAN_CONVERGENCE_FIRST`, `MERIDIAN_CONVERGENCE_DUMP` for a trace of a failing seed)
  asserting every replica serializes like the server and the model has no dangling references.
- `tests/Meridian.Web.Tests/CollaborationConvergenceTests.cs`: the same through real `ProjectSession`s and `Workspace`s
  (locks, permissions, hidden packages, code sync, merges, late joiners) and a property test over real tabs
  (`MERIDIAN_CONVERGENCE_WEB_SEEDS`, default 500).
- `tests/Meridian.Web.Tests/ConcurrencyLoadTests.cs`: 25 tabs edit shared elements without locks for 30 s.
- `tests/Meridian.E2E/CollaborationTests.cs`: two browsers rename the same element at the same moment.

## Remaining edge cases

- An undo that re-attaches a deleted subtree uses the undoing tab's copy of it. If someone else deleted an element inside
  that subtree meanwhile (and the undoing tab hadn't seen it yet), the element comes back too — on every replica alike.
- Relationships whose end is another relationship are not cascaded when that relationship is deleted (as before).
- The cascade of a delete can remove a relationship in a package the deleting user may not edit, when that relationship
  was created concurrently (the session checks the operations the user sent, not their cascade).
