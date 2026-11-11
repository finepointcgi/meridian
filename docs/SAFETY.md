# Safety & reliability analysis (FMEA and fault trees)

Meridian keeps safety analysis **in the model**: failure modes are owned by (or allocated to) the blocks, parts and
functions that fail, requirements mitigate them, test cases verify them, and fault tree events represent them. Change
an item and its failure modes, fault tree events and mitigations show up in impact analysis.

Code: `src/Meridian.Core/Safety/` (`SafetyFields.cs`, `Fmea.cs`, `FaultTree.cs`, `SafetyAnalysis.cs`, `SafetyNotation.cs`),
rendering in `src/Meridian.Serialization/SvgRenderer.FaultTree.cs`, report blocks in `Reports/DocTemplate.cs` /
`DocResolver.cs`; UI: the **Safety** ribbon tab and dock tab (`SafetyPanel.razor`), `SafetyInspectorSection.razor`,
`Workbench.Safety.cs`. Sample: `ParkingBrakeSample.cs` (project template *Electric parking brake (safety)*). Tests:
`tests/Meridian.Tests/SafetyTests.cs`.

## Metamodel

| Kind | Notes |
|---|---|
| `FailureMode` | Owned by a Block, Class, Component, Property (part), Activity, Action or Operation — or by a package and *allocated* to one. Keyword «failureMode». |
| `FaultTreeEvent` | Package member. `subtype`: `top`, `intermediate`, `basic` (default), `undeveloped`, `house`. |
| `FaultTreeGate` | Package member, unnamed by default. `subtype`: `and`, `or` (default), `vote` (k of n, field `fta.k`). |
| `FaultTreeLink` | Relationship from an output (event or gate, drawn above) to one of its inputs (below). |
| `Mitigate` | Relationship «mitigate» from a requirement (or a design element, e.g. a safety mechanism) to a failure mode. |
| `Verify` | May now also target a failure mode (a test case that checks the failure is detected / handled). |

Diagram type `FaultTree` ("fta") is owned by a model or package. Its palette has the five event kinds, the three gates
and comments; its only connector is the fault tree link. Failure modes and «mitigate» are also on the requirement
diagram palette. All kinds are appended to `ElementKind` / `DiagramType`, so existing ordinals are unchanged.

### Fields

| Key | On | Meaning |
|---|---|---|
| `fmea.function` | failure mode | Function of the item that fails |
| `fmea.effect`, `fmea.cause` | failure mode | Failure effect and cause |
| `fmea.severity`, `fmea.occurrence`, `fmea.detection` | failure mode | Ratings 1 … 10 |
| `fmea.controls`, `fmea.actions`, `fmea.actionOwner`, `fmea.actionStatus` | failure mode | Current controls, recommended actions, owner, status (open / in progress / done) |
| `fmea.rpnThreshold` | model root | RPN at which a failure mode needs a mitigating requirement (default 100) |
| `fta.ref` | event | Id of the failure mode, block, part or function the event represents |
| `fta.probability` | basic event, failure mode | Probability 0 … 1 |
| `fta.lambda`, `fta.time` | basic event, failure mode | Failure rate per hour and exposure time; P = 1 − e^(−λt) when no probability is set |
| `fta.houseState` | house event | `true` (occurs, P = 1) or `false` |
| `fta.k` | vote gate | Inputs that must occur |

A basic event's probability is its own (`fta.probability`, else λ·t), else that of the failure mode it represents.

## FMEA

- **RPN** = S × O × D (1 … 1000), shown once all three ratings are set.
- **Action priority** (H / M / L) follows the AIAG-VDA FMEA Handbook (2019) design-FMEA table (severity, then
  occurrence, then detection bands 7–10 / 5–6 / 2–4 / 1). A test checks the table is monotonic in every rating.
- **Item**: the owner, or for a failure mode in a package the first element it is allocated to.
- **Mitigated by**: sources of incoming «mitigate» links (requirements first). **Verified by**: test cases verifying
  the failure mode directly or verifying one of its mitigating requirements.

The **FMEA** worksheet (Safety ▸ FMEA, or the *Safety* dock tab) lists every failure mode, highest RPN first; click a
column header (Item, Failure mode, S, O, D, RPN, AP) to sort. Name, effect, cause, ratings, actions and status are
edited inline; rows flag *needs mitigation* (high risk without a mitigating requirement) and *not verified*. Filter by
text, show high-risk rows only, set the model's RPN threshold, add failure modes and export **CSV**
(`Fmea.ToCsv`: RFC 4180 quoting and spreadsheet formula neutralization). Blocks on diagrams show a *failure modes*
compartment with each RPN; failure mode shapes show effect, cause and S / O / D / RPN / AP.

## Fault tree analysis

An event's inputs are combined by the gate linked under it (several inputs without a gate are OR-ed); an event with no
inputs is a leaf. House events are constants. `FaultTree.Analyze(model, top)` returns:

- **Minimal cut sets** by top-down expansion (MOCUS) with absorption: OR unions its inputs' sets, AND takes the product,
  a k-of-n vote gate takes every k-combination. Cut sets are capped at 20 000 (reported as truncated).
- **Top-event probability**, exact for independent basic events: Shannon decomposition on events that occur more than
  once in the tree (shared subtrees count), with independent AND / OR / k-of-n formulas in between. Skipped (with the
  min-cut upper bound shown instead) above 18 repeated events.
- The **rare-event approximation** (Σ P(cut set)) and the **min-cut upper bound** 1 − Π(1 − P(cut set)).
- **Importance** per basic event: Fussell-Vesely (share of the cut-set probability) and Birnbaum
  (P(top | event) − P(top | no event)).

The *Fault tree analysis* tab shows these for the chosen top event (the active fault tree diagram's top event by
default). Fault tree diagrams draw the classic symbols — description boxes, circles (basic), diamonds (undeveloped),
houses, AND / OR / k/n gates — with each leaf's probability and the computed probability of every intermediate and
top event (cached until the model changes).

## Validation

| Rule | Severity | Check |
|---|---|---|
| SAF-001 | Error | Severity / occurrence / detection not a whole number 1 … 10 |
| SAF-002 | Info | Failure mode not fully rated |
| SAF-003 | Warning | High risk (RPN ≥ threshold or AP H) without a mitigating requirement |
| SAF-004 | Info | High-risk failure mode mitigated but nothing verifies it |
| SAF-005 | Warning | Failure mode not owned by or allocated to a block, part or function |
| SAF-006 | Info | Severity 9–10 without a recommended action |
| SAF-007 | Warning | Failure mode probability not in 0 … 1 |
| FTA-001 | Error | Gate without inputs |
| FTA-002 | Error | Vote gate k not a whole number between 1 and its input count |
| FTA-003 | Warning | Gate without an output event |
| FTA-004 | Warning | Basic / undeveloped event with inputs |
| FTA-005 | Warning | Basic event without a (valid) probability |
| FTA-006 | Warning | Top / intermediate event without inputs |
| FTA-007 | Warning | Event represents an element that no longer exists |
| FTA-008 | Error | Fault tree cycle |

## Traceability and impact

`Traceability.Impact` follows relationships backwards plus `SafetyTrace.ImpactedBy`: the failure modes an item owns and
the fault tree events representing it. So changing a block reaches its failure modes (owned or allocated), the events
representing them, every gate and event above those up to the top event, and the requirements mitigating them. The
inspector's *Traceability* section lists *mitigatedBy / verifiedBy / allocatedTo* on failure modes and *mitigates* on
requirements, with the usual "+ link element" picker.

## Exchange and reports

- `.meridian` project files carry everything as ordinary fields. XMI marks the new kinds with `meridian:kind` and
  carries the `fmea.*` / `fta.*` fields as Meridian extension fields, so both round-trip (tested).
- Report templates have two blocks: **FMEA worksheet** (`fmea`: optional current-element scope, minimum RPN, high
  risk only) and **Fault tree analysis** (`faultTree`: a top event by name / id, the current loop element, or every
  top event; summary table plus the first N minimal cut sets).

## Commands

Ribbon **Safety**: Failure mode, FMEA, Fault tree (new diagram with a top event and an OR gate; with a failure mode or
block selected the top event represents it), Analyze, CSV. ⌘K: *FMEA worksheet*, *New failure mode on selection*,
*New fault tree*, *Analyze fault tree*, *Analyze fault tree: ‹top event›*, *Export FMEA as CSV*. Model tree context
menu: *New failure mode*, *New fault tree for this*, *Analyze fault tree*, *Show in FMEA worksheet*.
