# Reports and document generation

Meridian generates documents straight from the model, so a specification or a review package is always in step with
the design.

## HTML report

**Generate → Report** opens a single, self-contained HTML page in a new browser tab: every diagram as an image, the
requirements with their *satisfied by* and *verified by* links, the model elements with their documentation, and the
current validation results. Save or print it from the browser.

## Word and PDF documents

**Generate → Formats → Word / PDF document from template…** opens *Generate document*.

| Option | Choices |
| --- | --- |
| **Template** | *System design description*, *Requirements specification*, *Verification & validation report*, *Full model report* (each preset ticks a set of sections) |
| **Document title**, **Author** | shown on the title page |
| **Sections** | title page, summary, requirements, traceability, diagrams, elements, validation |
| **Diagram types** | when *Diagrams* is ticked, limit the diagrams to some types (empty = all) |
| **Format** | Word (`.docx`) or PDF |

**Generate** downloads the file. Diagrams are embedded as pictures, requirements as tables with their links, and the
traceability section summarises coverage and gaps.

| Preset | Sections |
| --- | --- |
| System design description | title page, summary, diagrams, elements |
| Requirements specification | title page, summary, requirements, traceability |
| Verification & validation report | title page, requirements, traceability, validation |
| Full model report | all sections |

> [!NOTE]
> **TODO (screenshot):** the *Generate document* dialog with the *Requirements specification* preset.

## Diagram images

- **Export diagram as SVG** (project menu, or the canvas menu) downloads a vector image of the active diagram with its
  SysML frame header (for example `bdd [Package] Structure [Vehicle Structure]`).
- **Export diagram as PNG…** lets you choose the scale and a transparent or solid background; **Copy as image** puts the picture on
  the clipboard for pasting into slides and documents.
- Hidden layers are left out of exported images.

## Tables and spreadsheets

- Any generic table exports its visible rows as CSV ([Tables and queries](tables-queries.md)).
- The traceability table exports to CSV or Excel ([Requirements and traceability](requirements.md#traceability-table)).
- Requirements export to Excel and CSV ([Import and export](import-export.md#requirements)).
- Baseline comparisons export to CSV ([Branches, merge and baselines](version-control.md#baselines)).
- Scripts can produce any text file with `Download(...)` ([Scripting](scripting.md)), and plugins can add exporters
  ([Plugins](plugins.md)).
