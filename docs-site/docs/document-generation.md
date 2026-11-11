# Reports and document generation

Meridian generates documents straight from the model, so a specification or a review package is always in step with
the design.

## HTML report

**Formats → Quick HTML model report** (on the ribbon) opens a single, self-contained HTML page in a new browser tab: every diagram
as an image, the requirements with their *satisfied by* and *verified by* links, the model elements with their
documentation, and the current validation results. Save or print it from the browser. (The desktop app saves it instead.)

## Documents from templates (PDF, Word, HTML)

**Report** on the ribbon (or **Formats → Word / PDF / HTML document from template…**) opens *Generate document*.

| Option | Choices |
| --- | --- |
| **Template** | the built-in *System Requirements Specification*, *Interface Control Document*, *System Design Description*, *Verification Cross-Reference Matrix / Test Report*, *Model Overview* and *Data Dictionary*, plus any template saved in the model (⋮ → *Edit a copy…* opens the template editor) |
| **Title**, **Subtitle**, **Document number**, **Revision**, **Classification marking**, **Author**, **Page size** | the title page, headers and footers |
| **Format** | PDF, Word (`.docx`) or HTML (one self-contained file) |
| **Report on** | *Whole model*, the *Selection* (the selected element and what it contains), or *A package…* |
| **Package depth** | with the whole model or a selection: leave out elements nested more than this many packages deep (0 = everything) |
| **Output** | *One document*; *One document, a part per top-level package* (each package is a numbered part with the template's sections inside, all listed in one table of contents); or *A zip with one document per top-level package* |

The *System Design Description* has a **Deployment view** section (after *Behavior*): the deployment diagrams, a table of
artifacts with the node and execution environment they are deployed to, and the communication paths ([deployment diagrams](deployment-diagrams.md)).

A scope limits what the template's queries, diagram lists, tables, statistics and validation results see; links to
elements outside it (a requirement's *satisfied by*, for example) are still shown. With *A package…*, "top-level
package" means that package's own sub-packages.

### Big models

**Generate** first sizes the document (a quick pass that numbers headings, figures and tables without writing
anything), then:

- **A huge document is not started by accident.** Above about 2,000 estimated pages the dialog says so, for example
  *This System Design Description would be ~8,000 pages (128,000 table rows, 1,221 diagrams). Generate it per top-level
  package, or limit it to a package or a package depth?*, and offers **Split per top-level package** (a zip),
  **Limit to a package or depth**, or **Generate anyway**.
- **Big documents run in the background** (above about 150 pages, or 20,000 model elements visited). The dialog shows
  the progress (sections and elements processed) and can be closed: the status bar keeps a progress entry with a cancel
  button, and when the document is ready a notification and the status bar offer **Download**. Finished documents can
  be downloaded for two hours.
- **Small documents** download as soon as they are written, as before.

Documents are written to the server's disk as they are produced and downloaded straight from there (the browser shows
a normal download; the desktop app asks where to save and copies the file in chunks), so even a whole-model report of
a 200,000-element model — 80 MB of HTML, 180 MB of Word or a 7,000-page PDF — doesn't strain the browser or the
server's memory. A downloaded report contains only what you could see when it was generated: if package permissions
later hide part of it from you, the download is refused and you generate it again.

Trace matrices (*Requirement × design*, *Requirement × test case*) can **hide empty rows and columns** (on by default in
the built-in SDD and VCRM; a checkbox in the template editor). In a big model most cells of a full matrix are empty:
16,000 requirements against 6,000 blocks is five million rows, where the links fit in a few thousand.

> [!NOTE]
> **TODO (screenshot):** the *Generate document* dialog with the size warning, and a background report in the status bar.

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
