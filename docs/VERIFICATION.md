# Simulation-driven requirement verification

Meridian can check requirements against the model's analyses, in the style of Cameo's simulation-based
verification: a requirement carries a machine-checkable **criterion**, a **verification run** says what to analyse
and how, and running it records **pass / fail / inconclusive** per requirement, with the computed value, the
margin, a timestamp, the model version and the variant. Results are fields of the model, so they save, version,
merge and replicate like everything else, and they turn **stale** when anything they depend on changes.

Code: `src/Meridian.Core/Analysis/Units.cs`, `VerificationCriterion.cs`, `VerificationAnalyses.cs`, `Verification.cs`;
UI: `VerificationPanel.razor`, `Services/VerificationView.cs`.

## Criteria

Set the criterion in the Inspector (requirement → *Verification criterion*), in the Requirements table
(*Criterion* column) or in the Verification tab.

```
[analysis [(target)] :] expression comparator expression
```

| Part | Meaning |
|---|---|
| `analysis` | `parametric` (default; also `param`, `constraint`), `montecarlo` (`mc`), `timed` (`sim`), `cosim` (`fmi`) |
| `(target)` | the block, behavior or co-simulation system to analyse, by name. Optional. |
| expressions | numbers with units (`1500 kg`, `100 km/h`, `9.81 [m/s^2]`, `95 %`), value names (`mass`, `engine.maxPower`, `'top speed'`), `+ - * / ^`, parentheses, `abs sqrt min max` |
| statistics | over Monte Carlo samples or simulation time series: `mean sd min max median final initial p5 p95 pNN count`, `prob(x < 8 s)` (fraction meeting a bound), `at(x, 5 s)` |
| comparators | `< <= > >= == != ≤ ≥ ≠` (a lone `=` means equality) |

Examples:

```
mass <= 1500 kg
accelTime < 8 s
capacity * 10 km/L >= 600 km
parametric(Vehicle): engine.maxPower >= requiredPower
mc: p95(accelTime) < 8 s
mc: prob(range >= 600 km) >= 95 %
timed(Start Vehicle): endTime <= 10 s
timed: final(rpm) == 800
cosim(Rig): max(plant.temp) <= 90 degC
```

### Several criteria per requirement

A requirement can carry a list of criteria. Each one is parsed, analysed and evaluated on its own (with its own
analysis kind and target), and the requirement gets the **aggregate**:

| Criteria results                         | Requirement |
|------------------------------------------|-------------|
| any fails                                | fail        |
| none fails, at least one is inconclusive | inconclusive |
| all pass                                 | pass        |
| no criteria                              | inconclusive ("No verification criterion") |

The aggregate's value and bound list every criterion's (`7.192 s; 1450 kg`), its margin is the tightest one (the
smallest relative margin, negative for a failing criterion), and its message says how many criteria pass and why the
deciding one failed.

Add, edit and remove criteria in the Inspector (*Add criterion*, ✕) or in the Verification tab (**+** on a row, then
one sub-row per criterion). The Requirements table shows *N criteria* for a list and edits a single criterion in place.
In code: `Verification.Criteria`, `AddCriterion`, `SetCriterionAt` (a blank text removes it), `RemoveCriterion`,
`SetCriteria`; each is one undo step.

### Which element is analysed

1. the `(target)` written in the criterion, else
2. the run's **subject**, else
3. the element that **satisfies** (or refines) the requirement — a block, or the block owning a satisfying value or
   constraint property; for timed simulation, a satisfying activity / state machine or the behaviors a block owns.

Dotted names navigate part properties (`engine.maxPower` follows the part `engine` to its block) or start at a block by
name (`Engine.maxPower`).

### What each analysis provides

| Analysis | Values | A plain name means |
|---|---|---|
| parametric | every value property of the block after solving its constraints (`Parametrics.Solve`) | the value |
| montecarlo | samples of every value, from properties with a *Distribution* (`MonteCarlo.Run`; run settings: samples, seed) | the mean (a note says so) |
| timed | the discrete-event simulation's variables as time series, plus `time` / `endTime` (s) and `events` | the final value |
| cosim | every recorded output `part.port` of the FMI co-simulation (stop time / step from the run). FMU parts need the FMI runtime the co-simulation tab uses; algebraic parts always run | the final value |

## Units

`Units.cs` knows SI base and derived units with prefixes (`mW` ≠ `MW`), common non-SI units (`min h d km/h mph kn L gal
bar psi atm hp lb lbf ft in mi nmi °C °F rpm % ppm Wh kWh eV cal`) and products, quotients and powers of them
(`m/s^2`, `kg·m²`, `W/(m*K)`, `km/L`). Both sides of a comparison are converted to SI; different dimensions make the
result **inconclusive** ("Units do not match"). Values come with the unit of their value property (*Unit* field, or the
unit of its value type). A value **without a unit is read in the unit of the other side** of the comparison (the result
notes this), so `maxPower >= 150 kW` works with a unitless `maxPower = 160`. Temperatures are affine: `°C`/`°F`
convert absolutely, margins are differences.

## Verification runs

A verification run is a **test case stereotyped «VerificationRun»** (create it with *New run* in the Verification
tab; it gets «verify» links to the selected requirement, or to every requirement with a criterion). Its fields:

| Field | Meaning |
|---|---|
| `verify.analysis` | default analysis for criteria without a prefix |
| `verify.subject` | element id of the block / behavior / system to analyse |
| `verify.requirements` | extra requirement ids (in addition to its «verify» links) |
| `verify.configuration` | a Configuration: verify that variant (resolved on a copy of the model) |
| `verify.inputs` | input overrides, `mass = 1.7 t; engine.maxPower = 180 kW` (converted to each property's unit) |
| `verify.samples`, `verify.seed` | Monte Carlo settings (default 500, 12345) |
| `verify.stopTime`, `verify.step` | timed simulation time limit (default 3600 s), co-simulation stop time / step (10 s / 0.01 s) |

The model itself is never changed by the analysis: a variant or inputs are applied to a copy.

## Running

- Ribbon *Simulate → Verify*, ⌘K *Verify requirements*, or the *Verify requirements* button in the **Verification** tab
  (a view tab next to the diagrams since 0.7.1; the dock's *Verification* entry opens it). With a run selected, that run is executed; otherwise every run is executed and then every requirement with a
  criterion that no run covers is checked ad hoc (on the variant shown on diagrams, if any).
- The analyses run on a snapshot in the background; the results are then recorded in **one undo step per run**.
- The tab shows the pass / fail / inconclusive / stale / not-run summary (click a number to filter), and a row per
  requirement with its editable criterion, result, value, bound, margin, variant and time. Click a row for the
  drill-down (message and notes, each criterion's message, analysis, model version, run). A requirement with several
  criteria shows *N criteria* and a dot per criterion result; clicking it expands one sub-row per criterion (edit,
  remove, its own result, value, bound, margin and analysis) plus a row to add one.

## How results are stored

The criteria are `verify.criterion`, **one per line**. A requirement saved with a single criterion (every file written
before criteria lists) is simply a list of one, so old files load and verify unchanged and nothing needs converting.

On each requirement (by the last run that checked it; with several criteria these are the aggregate):

| Field | Example |
|---|---|
| `verify.status` | `pass` / `fail` / `inconclusive` |
| `verify.value`, `verify.bound` | `7.192 s`, `8 s` |
| `verify.margin` | `0.808 s (10.1 %)` — positive means inside the bound |
| `verify.message` | `7.192 s < 8 s`, or why it is inconclusive, plus notes |
| `verify.at` | `2026-11-10T12:00:00Z` |
| `verify.modelVersion` | `main #3fa2c1d0` (branch and a content hash of the model, results excluded) |
| `verify.variant` | configuration name, or `150% model` |
| `verify.run`, `verify.analysisUsed` | run id (empty for ad hoc), `Parametric (Vehicle)` |
| `verify.fingerprint`, `verify.deps` | staleness: a hash of the criteria and of the elements the analysis read |
| `verify.criteria` | only with several criteria: JSON, one entry per criterion with its text, status, value, bound, margin, message, analysis, fingerprint and dependencies |

A requirement with one criterion writes no `verify.criteria`: the fields above are that criterion's result, as before.
Reading a requirement (`Verification.LastResult(...).Checks`, `Verification.StoredChecks`) gives one entry per current
criterion, matched to the recorded results **by criterion text**. A result recorded for a single criterion, also by
an older version, belongs to the criterion whose fingerprint it matches (else to the first one, then stale), so adding
a second criterion to an old requirement keeps the first one's result. A criterion added or edited since the last run
has no result (*not run*).

On the run: `verify.lastRun`, `verify.summary`, `verify.results` (JSON history of that run: per requirement its
aggregate, plus a `Criteria` list with each criterion's own result when it has several) and the standard test-case
`verdict` (fail if any failed, else inconclusive if any was, else pass), so the run shows in the VCRM and traceability
views like any test case.

### Staleness

A result is **stale** when its fingerprint no longer matches: the criterion changed, or anything in the elements the
analysis read (the analysed blocks with their contents, the constraint blocks they use, relationships touching them,
the run's settings, the configuration and the feature model) changed. Each criterion has its own fingerprint, so a
change that only one criterion's analysis read makes only that criterion stale. The requirement's aggregate is stale
when any criterion's is, and when a criterion is added, edited or removed. Documentation edits, layout, review bookkeeping
and verification results themselves do not count. Stale results show dashed on diagrams, with a *stale* badge in the
tables, and as `pass (stale)` in reports.

## Where results show

- **Requirements table**: *Criterion* and *Result* columns (*N criteria* for a list).
- **Inspector**: each criterion with syntax check, add / remove, its own last result, and the aggregate.
- **Diagrams**: requirements are outlined green / red / amber by their last (aggregate) result (dashed when stale),
  with each criterion's result in the tooltip and a legend chip to hide them (⌘K *Hide verification results on
  diagrams*).
- **VCRM report** (*Generate → Report → VCRM*): *Analysis result*, *Criteria*, *Computed* and *Margin* columns. Any
  table or query can use the computed keys `verificationResult` (aggregate with staleness) and `verificationCriteria`
  (`accelTime < 8 s: pass; mass <= 1400 kg: fail`) and the raw fields (`field:verify.value`, …).
- XMI export / import carries the criteria (line breaks escaped in the attribute), run settings, results and
  `verify.criteria`.

## The Vehicle sample

Its value properties carry units and three requirements carry criteria: REQ-002 `accelTime < 8 s` (pass, 7.192 s),
REQ-003 `capacity * 10 km/L >= 600 km` (fail, 550 km) and REQ-005 `maxPower >= 150 kW` (pass, 160 kW).

## Limits

- Inputs and variants need a model copy; the web app always has one (core callers pass `VerificationOptions.Clone`).
- Timed simulation variables have the unit of the context block's property of the same name, otherwise none.
- A criterion is a single line; criteria are combined only by the fixed aggregate rule (no "any of").
- Results record the last run per requirement and per criterion; per-run history lives in each run's `verify.results`
  and in the model's version history.
