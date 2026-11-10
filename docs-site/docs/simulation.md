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
2. **Step** advances one node or transition; **Run to end** runs until the behavior finishes. **Restart** and **Stop**
   are next to them.
3. Active nodes glow on the diagram, and **Variables** shows the current values.

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

> [!IMPORTANT]
> FMI import brings in the **interface** only. Co-simulation (running the FMU) is not implemented.

## Try it on the samples

| Sample | Try |
| --- | --- |
| Vehicle | Step the *Start Vehicle* activity; solve the *Vehicle* parametrics (acceleration time and required power). |
| CubeSat mission | Solve *CubeSat* (power margin, total mass); Monte Carlo on `powerMargin` against SAT-011 (`>= 10`); timed run of *Acquire Image* together with *Operating Modes*, sending `commissioningComplete`, `targetInView` and `stationPass`. |
| Adaptive cruise control | Timed run of *ACC Control Loop* (20 ms cycle) and *ACC Modes* with `accSwitchOn`, `setPressed`, `sensorFault`. |
| Infusion pump | Step *Pump States* and *Program Infusion*. |
