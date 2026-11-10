# Variant management (product lines)

A Meridian model can be a **150% model**: one model with every option, a **feature model** saying which combinations
are valid, **presence conditions** on elements, and **configurations** choosing features. A configuration resolves the
150% model into one variant.

Code: `src/Meridian.Core/Analysis/Variability.cs`, `FeatureExpression.cs`, `FeatureSolver.cs`;
UI: the **Variants** dock tab (`VariantsPanel.razor`: Configurations, Feature model, Compare, Trade studies),
`Services/VariantView.cs`, `DiagramLegends.razor`.

## Feature model

- **Features** form a tree by containment (a feature owning features) and/or *Feature Decomposition* links (drawn on a
  Feature Model diagram).
- Each child's **kind** (`featureKind`) relates it to its parent: `mandatory`, `optional`, `alternative` (exactly one of
  the alternative siblings when the parent is selected) or `or` (at least one). Root features are always selected.
- Cross-tree constraints: **Requires** (selecting the source requires the target) and **Excludes** (not both).

The **Feature model** tab edits all of this: rename inline, change kinds, add children (also placed on feature
diagrams that show the parent) and root features, delete, and add / remove requires / excludes constraints. It shows
the number of valid products, **dead** features (in no product) and **core** features (in every product).

## Constraint solving

`FeatureSolver` turns the feature model into clauses (root, parent, mandatory, alternative = one-of + pairwise
exclusion, or-group, requires, excludes) and offers:

- **unit propagation** of decisions, reporting features *implied* (forced on) or *excluded* (forced off) with the
  constraint responsible, and conflicts;
- one-step **lookahead** (failed-literal probing): a feature whose selection would contradict the decisions is
  excluded even when propagation alone does not see it;
- `Complete(selection, turnedOn / turnedOff)`: the latest decision wins, selected features that contradict it are
  dropped (keeping as many as possible, in tree order) and everything forced on is added. Deselecting a core feature or
  selecting a dead one is refused;
- DPLL **product counting / enumeration** (`Variability.EnumerateProducts` now uses it, with no feature-count limit) and
  dead / core feature detection.

The solver's semantics equal `Variability.Validate`'s (tests check every enumerated product validates).

## Configurations

The **Configurations** tab lists the features with checkboxes. With *Propagate constraints* on, ticking a feature
selects its ancestors, mandatory children and required features and deselects alternatives and excluded features;
unticking drops what depended on it and selects the remaining alternative when one is forced. Each row shows what the
solver concludes: *required* (implied) or *blocked* (excluded), with the reason as a tooltip. Validation problems and
the resolved variant (included / excluded elements) are listed live.

## Presence conditions

Any element or relationship may carry a presence condition (Inspector), a feature expression:
`GPS and not Basic`, `(A || B) && !C`, `Premium implies Sunroof`, `"Lane Keeping" xor Cruise`. Operators by increasing
precedence: implies (`=>`, `->`), or (`||`, `|`), xor (`^`), and (`&&`, `&`), not (`!`, `~`). An element is in the
variant when its condition holds and its owner is in the variant; a relationship also needs both ends. Unknown feature
names count as unselected (and are reported); a malformed condition keeps the element (and is reported).

## Show variant on diagrams

Pick *Show on diagrams → Grey out / Hide* in the Configurations tab, or ⌘K *Show variant on diagrams: …*. Every diagram
then greys (or hides) the nodes and connectors the configuration leaves out; on feature model diagrams the features it
does not select. A chip on the canvas switches configuration and mode, and *Off* returns to the whole model. This is
a per-user view setting; it does not change the model. Ad-hoc requirement verification uses the variant shown.

## Comparing variants

The **Compare** tab picks two configurations and lists the features and the (top-most) model elements only in one or
the other, plus what they share (`VariantComparison.Compare`).

## Generating a variant

From the Configurations tab:

- **New project from variant** — a new project containing the resolved model;
- **Copy *package* as variant** — a copy of a package next to it with the excluded elements removed;
- **Export variant (.meridian)** and **Inclusion CSV**;
- **Resolve branch to variant** — deletes what the configuration excludes from the model you are editing (one undo
  step) and clears presence conditions. To keep a variant as a **branch**, create a branch (title bar → branch menu),
  then resolve it there; it can later be compared and merged like any branch.

## Verification of variants

A verification run may name a configuration; its criteria are then evaluated on the resolved variant (on a copy), and
results record the variant name. Requirements outside the variant are inconclusive ("Not part of variant"). See
[VERIFICATION.md](VERIFICATION.md).

## Changes in this release

- SAT-style propagation (excludes, forced alternatives, lookahead) replaces the parent / mandatory / requires-only
  propagation in the configuration editor; blocked and required states are shown.
- Product counting no longer stops at 22 features.
- The configuration's diagram highlight no longer shares the simulation highlight (which made a running simulation and
  the variant overwrite each other); it is now the Grey / Hide variant view on every diagram.
- New: feature model editor, comparison view, resolve-in-place, variant structure diagram in the product line example.
