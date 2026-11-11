# Requirements and traceability

Requirements in Meridian are model elements like blocks: they live in packages, appear on diagrams, and connect to
the rest of the model through relationships. Everything on this page works in both the desktop app and on a server.

## Writing requirements

Create a requirement with the ribbon (**Create → Requirement**), the shapes panel of a requirement diagram, the
requirements table (**New requirement**), or by importing a spreadsheet or ReqIF file.

Each requirement has:

| Field | Notes |
| --- | --- |
| **Id** | Assigned automatically as `REQ-001`, `REQ-002`, … You can type any id; ids must be unique (validation rule REQ-002). |
| **Name** and **Text** | The text is the "shall" statement. Changing the name or text of a requirement makes its trace links *suspect* (see below). |
| **Priority** | low, medium, high, critical |
| **Status** | draft, proposed, approved, implemented, verified, rejected |
| **Owner**, **Rationale** | free text |
| **Verification method** | test, analysis, inspection, demonstration |
| **Risk** | low, medium, high |
| **Custom attributes** | any name / value pair, for example *Safety level = ASIL B*. They become columns in exports and tables. |

Edit them in the inspector's **Requirement attributes** section or directly in the requirements table.

**Nesting**: drag a requirement onto another in the model explorer to make it a child. Parent requirements group their
children; only *leaf* requirements are expected to be satisfied and verified.

**Renumbering**: **Requirements → Exchange → Renumber requirements…** renumbers every requirement in document order
with a prefix you choose (for example `SYS-`). New ids then continue with that prefix. It is one undo step.

## Linking requirements

| Relationship | From → to | Meaning |
| --- | --- | --- |
| **deriveReqt** | derived requirement → source requirement | the derived requirement follows from the source |
| **satisfy** | design element → requirement | the block, part, state machine, constraint property … meets the requirement |
| **verify** | test case → requirement | the test case checks the requirement |
| **refine** | any → requirement (or the reverse) | a use case or activity details a requirement |
| **trace** | any → any | a general trace, for example a risk control to a hazard |
| **copy** | requirement → requirement | a reused requirement |
| **allocate** | function → structure | an action allocated to the block that performs it |

You can draw them on requirement diagrams, drag hover arrows between shapes, create them in the traceability matrix
or the traceability table, or add them in the inspector's **Traceability** section (*+ link element…*).

## Test cases and verdicts

A **test case** (ribbon **Requirements → Test case**) verifies one or more requirements. Its **Verdict** field
(pass, fail, inconclusive, error) records the last result and is shown in the traceability table's multi-level view.
A test case can own an activity that describes its procedure.

## The requirements table

**Requirements → Table** opens the requirements table in the dock: every requirement with id, name, text, priority,
status, verification method and its *Satisfied by* / *Verified by* links. Edit cells in place, add requirements,
**Re-import** a spreadsheet and **Renumber**.

> [!NOTE]
> **TODO (screenshot):** the requirements table with the inspector's requirement attributes alongside.

## Traceability matrix

**Traceability → Matrix** shows a dependency matrix. Choose what the **Rows** and **Columns** list (requirements,
blocks, classes, parts / ports, use cases, actors, test cases, activities / actions or states) and the
**Relationship** kind; the *Satisfy*, *Verify*, *Derivation* and *Allocation* buttons set up the common matrices in one
click. Click an empty cell to create the relationship and a filled cell to select it.

## Traceability table

**Traceability → Table** (the *Traceability table* dock tab) lists one row per requirement with its derived-from,
derived, refined-by, satisfied-by, verified-by and allocated-to links, its coverage and whether it has suspect links.

- **Filters**: *Not satisfied*, *Not verified*, *Has suspect links*, a package, and a text search. The counter shows
  how many requirements match. Id sorting is natural (`REQ-2` before `REQ-10`).
- **Edit in place**: each link cell has a picker (*Link…*) that offers only valid, not-yet-linked elements, and a
  remove button per link.
- **Per-package coverage** shows how much of each package is satisfied and verified.
- **Multi-level** shows the chain *need → requirement → derived → design → test* with the test verdict, flagging gaps.
- **Orphan design** lists blocks that no requirement traces to.
- **Export** the filtered table as CSV or the whole table (with the multi-level sheet) as an Excel workbook.

A leaf requirement whose status is not *rejected* is a **gap** when nothing satisfies it or nothing verifies it.

## Suspect links

When the name or text of a requirement changes, every satisfy, verify, refine, derive, trace and allocate link
attached to it becomes **suspect**: the design or test on the other end may no longer match. Suspect links are
flagged in the traceability table and by validation (rule TRC-001). After reviewing the other end, click the
suspect marker to clear it, or use **Clear N suspect** in the traceability table to clear every suspect link at once. Undoing the text change also clears the
suspect state, because the baseline is recorded in the same undo step.

## Impact analysis and coverage

To see a requirement's satisfy / verify / derive / refine graph as a picture, open its [relation map](relation-map.md).

- **Traceability → Impact** shows everything that depends on the selected element, transitively: everything that
  points at it through any relationship, plus what contains it.
- **Traceability → Coverage** writes the satisfied and verified ratios and the list of unsatisfied and unverified
  requirements to the Output tab.

## Validation

**Traceability → Validate** (or **F7**) runs the validation suite and opens the *Validation* tab; the status bar shows
the model health all the time. Click an issue to select the element.

| Rule | Severity | Checks |
| --- | --- | --- |
| MER-001 | error | named elements have a name |
| MER-002 | error | sibling names are unique per kind |
| MER-003 | error | relationship ends are valid for the relationship kind |
| MER-004 | error | no cyclic generalization |
| MER-005 | error | typed features reference an existing type |
| MER-006 | error | multiplicities are well formed (`1`, `0..1`, `1..*`, `*`) |
| REQ-001 | error / warning | requirements have an id (error) and text (warning) |
| REQ-002 | error | requirement ids are unique |
| REQ-003 | warning | leaf requirements are satisfied (directly, or through a satisfied derived requirement) |
| REQ-004 | info | leaf requirements are verified |
| BEH-001 | warning | state machines and activities have exactly one initial node |
| BEH-002 | warning | every state has an incoming transition |
| TRC-001 | warning | suspect trace links |

You can add your own rules as model queries; see [Tables and queries](tables-queries.md#validation-rules). Plugins
can add rules too.

## Exchanging requirements

Requirements round-trip with spreadsheets (CSV and Excel), ReqIF 1.2 (DOORS, Polarion, Jama, Codebeamer …) and the
live sync with DOORS Next and Polarion, and the OSLC provider on a server. See [Import and export](import-export.md#requirements) and [Integrations](integrations.md).
