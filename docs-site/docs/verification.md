# Verifying requirements

Meridian checks requirements against the model's analyses, like Cameo's simulation-based verification. You give a
requirement a **criterion**, press **Verify**, and every requirement gets **pass**, **fail** or **inconclusive**, with
the computed value and the margin. Results are saved in the model and turn **stale** when anything they depend on
changes.

## 1. Give a requirement a criterion

Select a requirement and type its criterion in the Inspector (*Verification criterion*), in the Requirements table
(*Criterion* column) or in the **Verification** tab:

```
mass <= 1500 kg
accelTime < 8 s
capacity * 10 km/L >= 600 km
mc: p95(accelTime) < 8 s
timed(Start Vehicle): endTime <= 10 s
```

- Numbers carry units (`kg`, `km/h`, `kW`, `°C`, `%`…). Both sides are converted, so `150 kW` and `160000 W` compare.
- A prefix picks the analysis: none (or `parametric`) solves the block's constraints; `mc:` uses Monte Carlo samples;
  `timed:` runs the discrete-event simulation; `cosim:` runs an FMI co-simulation.
- `(Target)` names the block or behaviour to analyse. Without it, Meridian analyses the element that **satisfies**
  the requirement.

### Several criteria

A requirement can have more than one criterion, for example `accelTime < 8 s` and `mass <= 1500 kg`, each with its own
analysis. Add one with **Add criterion** in the Inspector, or with **+** on the requirement's row in the Verification
tab. Each criterion gets its own result, and the requirement shows the combination:

- **fail** if any criterion fails;
- **inconclusive** if none fails but at least one is inconclusive;
- **pass** if all pass.

A criterion you add or edit shows *not run* until you verify again. The others keep their results.

## 2. Verify

**Simulate ▸ Verify** (or ⌘K *Verify requirements*) runs the analyses in the background and opens the
**Verification** tab next to the diagrams (the bottom dock's *Verification* entry and ⌘K *Verification results* open it too): a summary (pass, fail, inconclusive, stale, not run; click a number to filter) and a row per
requirement with its criterion, result, value, bound and margin. Click a row for the details. A requirement with
several criteria shows *N criteria* with a dot per result. Click it to open a row per criterion, where you can edit,
remove or add criteria and see each one's result, value, bound and margin. On a phone, the account menu ▸
**Verification results** lists the same results read-only.

On diagrams, requirements are outlined green (pass), red (fail) or amber (inconclusive), dashed when stale. With
several criteria the colour is the combined result, and the tooltip lists each criterion. The legend chip on the
diagram, or ⌘K *Hide verification results on diagrams*, turns this off.

## Verification runs

For repeatable checks, press **New run** in the Verification tab. A run is a test case that says what to analyse:
the subject (a block or behaviour, or an [instance](instances.md) whose slot values replace its block's default values),
the analysis, a **variant** to verify (see [Variants and trade studies](variants-trade-studies.md)),
input overrides such as `mass = 1.7 t`, Monte Carlo samples and simulation stop time. Select a run and press
**Verify** to execute just that run. Its overall verdict shows in the VCRM and the traceability views like any test
case.

## Where results show

- **Requirements table**: *Criterion* and *Result* columns. Several criteria show as *N criteria*; edit them in the
  Inspector or the Verification tab.
- **Inspector**: each criterion with a syntax check and its last result, and the combined result.
- **Diagrams**: coloured outlines, as above.
- **Reports**: the **VCRM** template (*Generate ▸ Report*) has *Analysis result*, *Criteria* (each criterion with its
  result), *Computed* and *Margin* columns.

## Try it

The **Vehicle sample** has three criteria: acceleration passes (7.2 s), range fails (550 km against 600 km) and
engine power passes. Open it, press **Simulate ▸ Verify**, then change the range criterion and watch it turn stale.
