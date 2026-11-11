# Safety and reliability (FMEA and fault trees)

The **Safety** ribbon tab holds failure mode and effects analysis (FMEA) and fault tree analysis (FTA). Both live in the
model, next to the design they analyse: failure modes belong to blocks, parts and functions, requirements mitigate
them, tests verify them, and fault tree events point at them. To try it, start a project from the
**Electric parking brake (safety)** template.

## Failure modes and the FMEA worksheet

1. Select a block, part, activity or action and click **Safety → Failure mode** (or right-click it in the model tree →
   **New failure mode**). A failure mode can also sit in a package and be *allocated* to the item.
2. In **Properties**, fill in the function, effect and cause, and rate **severity (S)**, **occurrence (O)** and
   **detection (D)** from 1 to 10. Meridian shows the **RPN** (S × O × D) and the AIAG-VDA **action priority**
   (H / M / L).
3. Link the requirement that mitigates it: in **Properties → Traceability**, pick *mitigatedBy* and choose the
   requirement, or draw a **Mitigate** line from the requirement to the failure mode on a requirement diagram. Test cases
   that verify the failure mode, or its mitigating requirements, appear as *Verified by*.

**Safety → FMEA** opens the worksheet in the bottom dock. Every failure mode is a row, highest RPN first. Click a column
header to sort by item, name, S, O, D, RPN or AP. Edit names, effects, causes, ratings, actions and status in place.
Rows that are high risk but have no mitigating requirement say **needs mitigation**. You can filter, show only high-risk
rows, change the model's **RPN threshold** (default 100), and export the worksheet as **CSV**.

Blocks on diagrams list their failure modes with each RPN.

## Fault trees

**Safety → Fault tree** creates a fault tree diagram with a top event and an OR gate under it. If a failure mode or a
block is selected, the top event represents it. Build the tree from the palette:

| Symbol | Meaning |
|---|---|
| Box | Top or intermediate event |
| Box over a circle | Basic event: give it a probability, or a failure rate λ and exposure time, or let it represent a failure mode that has one |
| Box over a diamond | Undeveloped event |
| Box over a house | House event: switched on (1) or off (0) |
| Flat-bottomed gate | AND: all inputs |
| Curved gate | OR: any input |
| Curved gate with k/n | Vote: at least k of the n inputs (set k in Properties) |

Connect each event or gate to its inputs below it. A basic event's **Represents** property links it to a failure mode,
block, part or function.

**Safety → Analyze** opens the *Fault tree analysis* tab for the active fault tree. It shows:

- the **top-event probability**, computed exactly (events that appear in several places are handled correctly), and
  the rare-event approximation and min-cut upper bound;
- the **minimal cut sets** (the smallest combinations of basic events that cause the top event), smallest first, with
  their probabilities and share;
- the **importance** of each basic event (Fussell-Vesely and Birnbaum).

Probabilities are also shown on the diagram: under each basic event and inside every intermediate and top event.

## Checks, impact analysis and reports

**Validate** (F7) adds safety rules: ratings out of range, high-risk failure modes without a mitigating requirement,
failure modes not on any item, gates without inputs, bad k values, basic events without probabilities and cycles.

**Impact analysis** of a block now lists its failure modes, the fault tree events that represent them, the events above
those up to the top event, and the requirements that mitigate them.

In **Reports and documents**, add an **FMEA worksheet** block (optionally high-risk only or above a minimum RPN) or a
**Fault tree analysis** block (summary plus minimal cut sets) to any template.

Everything round-trips through the Meridian file format and XMI.

Reference: [docs/SAFETY.md](https://github.com/finepointcgi/meridian/blob/main/docs/SAFETY.md).
