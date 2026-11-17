# Cameo import fidelity

How the MagicDraw / Cameo importer measures what it loses, and how to check it against real projects.

## The accounting

`MagicDrawImportReport` (`src/Meridian.Serialization/MagicDraw/MagicDrawFidelity.cs`) gives every model element the
importer reaches one of three outcomes:

| Outcome | Meaning |
| --- | --- |
| **Imported** | The element became a Meridian element (its XMI id is in the builder's id map). |
| **Folded** | Its content went into a field of another element: documentation comments, the constraint on a constraint block, interaction operands (guards on the combined fragment), the collaboration that wraps an interaction and its attributes (shown through lifelines), `base_` properties of stereotypes, item flows that become a connector's item flow. The builder calls `Fold(id)` at each of those places. |
| **Not imported** | Everything else. This is the measure of loss. |

The following are not counted as model elements: value specifications (`Literal*`, `OpaqueExpression`, `InstanceValue`…),
events, occurrence and execution specifications, imports, profile applications, and features that the parent element
reads into its own fields (multiplicities, default values, guards, entry/exit/do behaviors, parameters, triggers…; see
`ParentReadTags`).

The coverage figures are:

- **Elements**: (imported + folded) / in file.
- **Diagrams**: imported / found.
- **Stereotype applications**: applications whose base element was imported or folded / all user applications.
  Applications from MagicDraw's own profiles (`MagicDraw_Profile`, `DSL_Customization`, …) are not counted.
- **Tag values**: copied / copied + structured values that could not be copied.

`IsLossless` is true only when nothing in any of these groups was lost. The web UI opens the **Import check** dialog
(`ImportFidelityDialog.razor`) only after an import that was not lossless.

When the importer adds support for a construct, it should either create an element (and so land in the id map) or call
`Fold(id)`. Otherwise the construct keeps showing up as *not imported*. That is deliberate: a construct the importer
silently swallows cannot hide.

## Checking real projects

The unit tests use hand-written fixtures. Real Cameo projects cannot be committed to the repository, so the corpus check
reads them from a folder you supply:

```bash
MERIDIAN_CAMEO_CORPUS=~/cameo-corpus dotnet test tests/Meridian.Tests --filter Corpus_Of_Real_Projects
```

For every `.mdzip` / `.mdxml` under the folder, the test imports the file, round-trips it through the `.meridian`
format, and writes `fidelity-report.md` into the folder. The report has a summary table with one row per file, then the
per-file details: losses by UML type, skip reasons, lost stereotypes and tag values, and warnings.

The test fails when a file cannot be read, when the round trip changes the element count, or when a file keeps less
than `MERIDIAN_CAMEO_MIN_COVERAGE` of its model elements (default `0.9`). Without `MERIDIAN_CAMEO_CORPUS`, the test does
nothing.

Good sources for a corpus are the sample projects that ship with Cameo / MagicDraw (they may only be used under your
Cameo license), openly licensed models published by OpenMBEE and others, and your own organization's projects. Keep the
corpus and its reports out of the repository.
