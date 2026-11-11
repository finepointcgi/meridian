# Simulation

Meridian executes behaviors and solves parametric models without leaving the workbench. Open the *Simulation* dock tab
with **Simulate → Simulate**; it has four tabs: **Step**, **Timed (discrete event)**, **Monte Carlo** and
**FMI import**. Parametric solving has its own tab (**Simulate → Parametrics**).

## Expressions

Action bodies, guards, transition effects and state `entry` / `do` / `exit` behaviors are small expressions:

| Where | Example |
| --- | --- |
| action body, effect, entry / do / exit (assignments separated by `;`) | `attempts = attempts + 1; started = attempts >= 2` |
| guard | `throttle > 0` |
| functions | `accel = max(min(accel, 2), -3.5)` |

Variables start from the owning block's value properties (their default values); assigning a new name creates a
variable. `else` is the default branch of a decision.

## Step simulation

The **Step** tab runs one activity or state machine at a time.

1. Choose the **Behavior** and press **Start**.
2. **Step** advances one node (activity) or takes one completion transition (state machine). **Run to end** runs an
   activity until it finishes; **Run until waiting** takes a state machine's completion transitions until it needs an
   event. Named transitions are events: each enabled one has a button. **Restart** and **Stop** are next to them.
3. Active nodes and states glow on the diagram, several at once in orthogonal regions. For a state machine the
   **Active configuration** line lists the innermost active states by path (`Operating › Heating`,
   `Operating › Monitoring`) and any join that is waiting. **Variables** shows the current values, and you can edit
   them to enable guards. The log below records every entry, exit, transition, choice and join.

### State machines

States can be nested to any depth. A composite state is entered through its initial state (default entry) and exited
from the inside out, running `exit` behaviors innermost first. When an event enables transitions at several levels,
the innermost one wins; in orthogonal regions every region takes its own transition for the same event.

**Orthogonal regions.** Meridian has no separate region element: a composite state's substates fall into regions by
how they are connected. Substates linked by transitions are in one region; each initial state starts its own region;
the targets of one fork, and the sources of one join, are always in different regions. A composite with two initial
states therefore has two regions that are active together. It completes (and its event-less transitions can fire)
when every region has reached a final state.

| Pseudostate | Behaviour |
| --- | --- |
| choice ◇ | Guards are evaluated when the choice is reached, after the exits and effects before it, so an effect can decide the branch. `[else]` is taken when no other guard holds. If no guard holds and there is no `[else]`, the run stops with an error naming the choice and its guards. |
| junction ● | A static branch: the whole path is chosen with the variables as they were before the transition fired. If no branch is enabled, the transition is not enabled (the event is ignored). |
| fork ▬ | Enters every target, in different regions; the regions it does not target get their default entry. |
| join ▬ | Waits until a transition has arrived from each incoming transition. Each arriving region leaves its state and waits, and the join is highlighted until it fires. Leaving the composite discards the arrivals. |
| shallow history Ⓗ | Re-enters the substate that was active when the composite was last left. That substate gets its default entry. |
| deep history Ⓗ* | Restores the whole nested configuration that was active. |
| (no history yet) | The history's own outgoing transition if it has one, else the composite's default entry. A region that had finished is not restored. |
| entry point ○ | Enters the composite on its border and continues along the point's transitions inside it, instead of the default entry. |
| exit point ⊗ | Exits everything inside the composite and the composite itself, then continues along the point's outgoing transition. |
| terminate ✕ | Ends the run at once, without exit behaviors. |

A final state inside a composite only finishes its region; the machine ends at a final state of its own top level.
A transition chain that loops through pseudostates without reaching a state is reported as an error rather than
followed forever.

### Activities: central buffers and data stores

- A **central buffer** node collects tokens and passes each one to **one** outgoing flow, taking the flows in turn.
  Tokens that no flow accepts (all guards false) stay in the buffer and are offered again on later steps.
- A **data store** node keeps its data, so a new token replaces the stored one. Every outgoing flow gets a copy, and
  the store keeps the data after the activity ends.

Buffers holding tokens and stores holding data are highlighted, and the log notes what is left when the activity
ends.

## Timed simulation (discrete event)

The **Timed** tab runs several activities and state machines **together on one simulation clock**.

- Give actions a **Duration**: `5`, `2.5s`, `200ms`, `1 min`, `2h`, or an expression over variables (seconds).
- Make an action **send a signal** when it completes, or **accept an event** (wait for a signal).
- Name state machine transitions with a signal (`stationPass`), a time event (`after(5s)`, `after(30 min)`) or a change
  event (`when(temp < 50)`).

Choose the **Behaviors (run together)**, press **Start**, then **Next event**, **Play** (at a chosen **Speed**) or
**Run to completion**. While it runs, **Send signal** injects a signal by name (the buttons list the signals the
model uses). The **timeline** shows actions, waits and states as bars and signals as arrows; below it are the
variables and the event trace with timestamps.

State machines follow the same rules as in the Step tab (regions, pseudostates, history). Each orthogonal region gets
its own timeline row, so a fork shows parallel bars. A join waits until its slowest incoming region arrives: with
`after(2s)` in one region and `after(5s)` in the other, the join fires at 5 s.

> [!NOTE]
> **TODO (screenshot):** the timeline of the CubeSat *Acquire Image* activity running with the *Operating Modes*
> state machine.

## Parametrics

**Simulate → Parametrics** solves a block's parametric model. Pick the **Block** and press **Solve**:

- value properties with a default value are inputs;
- every other value property bound to a constraint parameter is solved for, one equation at a time (each equation may
  have one unknown; Meridian finds roots numerically, so `time = (100 / 3.6) * mass / force` can be solved for any of
  its variables);
- equations whose variables are all known are checked, and a violated one is reported.

The result table shows each value, whether it was computed and from which equation. **Store results as tagged
values** writes the computed values onto the value properties (tag `computed`) so tables, trade studies and reports
can use them.

## Monte Carlo

The **Monte Carlo** tab propagates uncertainty through the parametric model. Give input value properties a
**Distribution** in the inspector:

| Distribution | Example |
| --- | --- |
| normal(mean, sd) | `normal(1.0, 0.15)` |
| uniform(min, max) | `uniform(0.58, 0.66)` |
| triangular(min, mode, max) | `triangular(0.85, 0.9, 1.0)` |
| lognormal(mu, sigma) | `lognormal(0, 0.25)` |

Pick the **Block**, the number of **Samples** and a **Seed** (runs are reproducible), then **Run**. Choose an
**Output** to see its histogram and statistics (mean, standard deviation, min / max, P5 / P50 / P95). Link a
**Requirement** and a **Meets if** threshold (for example `>= 10`) to get the probability that the requirement is met.

## FMI import

The **FMI import** tab reads an FMI 2.0 or 3.0 **.fmu** (or its `modelDescription.xml`) and shows its variables with
causality, type, start value and unit. **Import** creates a block with value properties for parameters and locals and
in / out ports for inputs and outputs, so the FMU can be typed as a part and connected in internal block diagrams.

Imported FMUs can then be run together with ordinary blocks in the **Co-simulation** tab: each FMU runs in its own
`Meridian.FmuHost` process. The FMU's default step size and end time are filled in. Connected ports with compatible
units (for example `km` and `m`) are converted. String, binary and clock variables are not exchanged. See `docs/FMI.md`
in the repository for the details and the security notes.

## Try it on the samples

| Sample | Try |
| --- | --- |
| Vehicle | Step the *Start Vehicle* activity; solve the *Vehicle* parametrics (acceleration time and required power). |
| CubeSat mission | Solve *CubeSat* (power margin, total mass); Monte Carlo on `powerMargin` against SAT-011 (`>= 10`); timed run of *Acquire Image* together with *Operating Modes*, sending `commissioningComplete`, `targetInView` and `stationPass`. |
| Adaptive cruise control | Timed run of *ACC Control Loop* (20 ms cycle) and *ACC Modes* with `accSwitchOn`, `setPressed`, `sensorFault`. |
| Infusion pump | Step *Pump States* and *Program Infusion*. |
