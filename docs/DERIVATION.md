# Create diagrams from diagrams (derivation)

Meridian can derive the next MBSE view from the one you have, the way a systems engineer works down the chain:

**Requirements → Use case → Activity → Sequence → BDD / IBD (→ State machine)**

Each step is rule-based and deterministic. It works without any AI, plans what it would create, shows that plan for
review, and applies what you keep as **one undo step**. Every derived element keeps a record of its source, so a derived
diagram knows when its source changed and can be **updated from source** without losing your own work.

Engine: `src/Meridian.Core/Derivation/` (`DiagramDerivation.Plan` / `Apply`, `DerivationChain`, `UseCaseSteps`).
UI: `DeriveDialog`, `DerivedSourceBadge` and `DerivationChainSection` in `src/Meridian.Web/Components/Workbench/`.

## Where to find it

- **Right-click** a use case, activity, interaction, block, requirement or requirements package, either in the model
  explorer or on a diagram. On a diagram, right-clicking a swimlane, an action or the empty canvas offers the
  derivations of the diagram's activity or interaction. The menu has a **Create from…** section that lists only the
  derivations that apply.
- **Ribbon ▸ Diagram ▸ Create from…**: the derivations of the selection, or of the active diagram.
- **⌘K**: "Create activity from use case (Drive Vehicle)", "Create sequence from activity (…)" and so on, for the
  selection or the active diagram, plus "Update “…” from source" when the active diagram is out of date.
- **Inspector ▸ Derivation chain**: for example *Requirements → Use case → **Activity** → Sequence → IBD*. Each link that
  exists opens its diagram. The first missing link has a **Create next…** button.

## The chain

| From | Create | How it maps | Link kept |
| --- | --- | --- | --- |
| Requirements (a requirements package or a requirement) | Use case diagram in a new "*Package* use cases" package | Each functional requirement's "shall …" phrase names a use case ("The system shall allow the operator to schedule imaging passes" → **Schedule Imaging Passes**). Requirements with the same phrase share a use case. Actors come from a `stakeholder` attribute or tag, from "allow / enable / let the *X* to …", or from existing actors that the text names, otherwise a placeholder **User**. Performance and "shall not" requirements are listed but not ticked. | «refine» use case → requirement; actor–use case associations |
| Use case | Activity (owned by the use case, which makes it the use case's behaviour) and its activity diagram | The use case's description is parsed into steps: numbered or bulleted lines, or sentences if there is no list. A step that starts with an actor's name ("Driver presses start", "Driver: presses start") goes in that actor's swimlane, every other step in the **System** swimlane (or the subject block's swimlane when the use case is drawn inside a block on a use case diagram). "If *X*, *Y*" becomes a decision with *Y* on the [*X*] branch and [else] continuing. "Alternate: *X* - *Y*" (also Alternative, Exception, Otherwise) branches off to *Y* and then ends. With no steps, you get a skeleton: start → *use case name* → end. | behaviour ownership; derivation record |
| Activity | Interaction and sequence diagram | Each swimlane becomes a lifeline, typed by the block or actor it represents. If it represents nothing, a block of the same name is reused or created. Each hand-over from an action to an action in another swimlane (directly or through decisions, forks and merges) becomes a message from the source swimlane to the target swimlane, named after the target action (`driveVehicle`). If an actor swimlane starts the activity, the first message comes from that actor. Decisions become **alt** fragments, forks become **par** fragments and back edges become **loop** fragments, each around the messages it governs. | «allocate» action → the swimlane's block; «trace» interaction → activity |
| Sequence | BDD and IBD | Lifelines become blocks: their type when they have one, otherwise an existing block of the same name or a new block. A context block, "*interaction* context", gets one part per lifeline and composition to each block. A synchronous message becomes an operation on the receiving block. An asynchronous message becomes a «reception» operation, because Meridian has no Signal metaclass. Replies create nothing, and operations that already exist are reused. Parts that exchange messages get a port on each block, named after the peer, and a connector. The BDD shows the context and the blocks. The IBD of the context block shows the parts, ports and connectors. | «trace» message → operation |
| Sequence or activity, for a chosen block | State machine owned by the block (its classifier behaviour) and its diagram | The events the block receives, in order, become transitions **Idle → After *event1* → After *event2* → …**, with a completion transition back to Idle. From an activity, the events are the hand-overs into the block's swimlane. This is flagged as approximate. | derivation record (`derive.target` = the block) |
| Blocks / requirements | Proposals only, with no diagram | «satisfy» from each block whose name (or "fuel tank" for `FuelTank`) appears in a requirement's text, and a test case "Verify *requirement*" with «verify» for each requirement that nothing verifies yet. The test cases go in a **Verification** package. Each proposal has its own checkbox. | «satisfy», «verify» |

## Review before anything changes

The dialog lists every proposed item with a checkbox and a status (**new**, **change**, **remove**), and shows warnings
such as "no description, so the activity is a skeleton" or "fragments are placed around the messages…". If you untick
an item, everything that depends on it goes too: unticking an action also drops the flows to it, and an actor that no
ticked use case needs is not created. **Create** (or **Accept** when updating) applies what is ticked as one undo step.
The status bar says what was made, for example *Created “Drive Vehicle” (8 added) (⌘Z to undo)*. New diagrams open as
tabs, and the last one becomes active.

## Derivation records

Every derived element and diagram stores these fields. They are ordinary fields, so they save, replicate, diff and undo
like all the others. The constants are in `DeriveFields`.

| Field | Meaning |
| --- | --- |
| `derive.source` | id of the source element (use case, activity, interaction, requirements package…) |
| `derive.kind` | which derivation: `usecases-from-requirements`, `activity-from-usecase`, `sequence-from-activity`, `structure-from-sequence`, `statemachine-from-sequence`, `statemachine-from-activity`, `satisfy-verify` |
| `derive.fingerprint` | hash of the source content the derivation read, as of the last derivation or update |
| `derive.key` | the item's stable key within its derivation (`step:3`, `decision:4`, `lifeline:<id>`, `msg:<a>><b>`, `diagram`) |
| `derive.target` | the block a state machine was derived for |
| `derive.hash` | hash of the item's own fields as the derivation last wrote them; if it differs, you edited the item |

The trace relationships («refine», «allocate», «trace», «satisfy», «verify») are real model relationships. They appear
in traceability, matrices and suspect-link checks like any others.

## Updating from source

When a source changes, for example a use case's steps are reworded, its derived diagrams show an amber **Source
changed** badge in the diagram header and a dot on the tab. **Update from source…** recomputes the plan against the
current source and compares it, key by key, with what the previous run made:

- **added**: items the new plan has and the previous run did not. Only these get new shapes. Each is placed where it was
  planned if that spot is free, otherwise below what is already there. Swimlanes may grow to hold new actions.
- **changed**: earlier items whose planned fields (name, guard, type…) now differ.
- **removed**: earlier items the plan no longer has.

The dialog shows the derived diagram as it would look after the update, using the same highlights as code sync's "What
changed": green outline for added, amber for changed, and a dashed ghost for removed. The list has a checkbox per item.
**Accept** applies the ticked items as one undo step. **Dismiss** keeps the diagram as it is and marks the source change
as seen, which clears the badge.

What an update never does:

- It never touches elements, relationships or shapes you added yourself. They have no `derive.key`.
- It never moves shapes that are already on the diagram, so your layout stays as you left it.
- It never removes or overwrites a derived item you edited since it was derived (`derive.hash` differs), or one that
  carries things you added, without asking. Such items are listed but not ticked.

Numbered steps keep their number as their key. Rewording step 2 is therefore a **change**, deleting step 4 is a
**remove**, and a new step 5 is an **add**. Unnumbered steps are keyed by their position.

Deriving again from a source that already has a derivation offers the update instead of a duplicate. The menu entry then
reads "Update activity from use case…".

## Fill in with AI (optional)

When the AI assistant is on (Claude, a local model or Claude Code; see [AI.md](AI.md)), the dialog shows a **Fill in with
AI** switch for the two steps where rules run out:

- use case → activity: writes the steps and alternate flows from a free-text description, in the same mini-language as above;
- activity → sequence: names the messages (`requestStart` rather than `checksTheBrakePedal`).

The answer comes back through a strict `submit_derivation` tool (`DerivationWriter`). It is validated: steps are
trimmed and capped, and only message keys that were asked about are accepted, as identifiers. The result is shown in the
same preview, with items marked "written by AI", before anything is created. It is never required, and tests use fakes
only (no network).

## Known approximations

- Partition membership is read from the activity's diagrams: actions nested in a swimlane, or whose centre lies inside
  one. If that fails, an «allocate» to the element a swimlane represents is used. Actions in no swimlane are skipped
  with a warning.
- Fragments cover the messages that leave a decision or fork (alt / par) or that lie between the ends of a back edge
  (loop). Operand boundaries are not computed, so check them.
- State machines are a straight chain of "After *event*" states. Merge states that are really the same, and add guards.
- A lifeline without a type gets a proposed block, but the lifeline itself is left untyped, so the source is not changed.
- New messages added by an update go below the existing ones, in the order the activity runs.
