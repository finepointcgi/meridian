# Import and export

## Where to find it

| Menu | What it does |
| --- | --- |
| Project menu → **Open a file as a new project…** (.meridian, .xmi, .sysml, EA .qea, Cameo .mdzip / .mdxml, Mermaid .mmd / .md) | creates a new project from a file |
| Project menu → **Import into this project…** (BPMN, ArchiMate, SQL, C#, Mermaid, Cameo re-import) | adds the content of one or more files to the open project |
| Project menu → **Paste Mermaid…** | imports Mermaid text, for example from an AI assistant, with a preview ([Mermaid](#mermaid)) |
| Project menu → **Export ▸** **Download .meridian file**, **Export XMI 2.5.1**, **Export SysML v2 text**, **Export diagram as SVG / PNG / Mermaid**, **Copy diagram as Mermaid** | exports |
| Right-click a diagram tab → **Export ▸** | SVG, PNG, image or Mermaid of that diagram |
| Ribbon **Requirements → Exchange** | ReqIF, CSV and Excel import and export, re-import with preview, renumbering |
| Ribbon **Generate** | report, SysML v2, XMI, C# code, **Formats** for Word / PDF, BPMN, ArchiMate, ReqIF and SQL DDL, and **Plugins** (plugin exporters and importers) |

The desktop app can also open `.meridian`, `.sysml`, `.xmi`, `.qea`, `.qeax` and Mermaid (`.mmd`, `.mermaid`, `.md`) files from the start screen, from
the operating system (double-click) or by passing the path on the command line.

## Meridian project files (.meridian)

A `.meridian` file is the complete model as JSON: every element and relationship with all its fields, every diagram
with its layout, styles, layers and pictures, and the saved views, rules and scripts that live in the model. It
round-trips exactly. Discussion comments and reviews are stored with the project in the workspace database, not in
the file. Use `.meridian` files for hand-offs, e-mail and moving models between the desktop app and a server.

## XMI 2.5.1

**Export XMI** writes UML 2.5.1 / SysML 1.6 XMI with the SysML profile applications and **UML Diagram Interchange**
(shapes with bounds, paths with waypoints), so diagrams survive the trip. Fields with no standard UML or SysML home
(port kind, item flows, state entry / exit, BPMN and ArchiMate subtypes, requirement attributes, test verdicts,
custom stereotypes and tagged values, saved views …) are kept in a Meridian extension, so a Meridian → XMI → Meridian
round trip loses nothing that matters.

**Import** reads XMI from Cameo Systems Modeler / MagicDraw, Enterprise Architect and Papyrus, including their
different XMI namespace versions and SysML profile prefixes (`SysML1.4:Block`, `sysml:Requirement`, …). Requirement ids
and text, satisfy / verify / derive / refine / trace, blocks, parts, ports, value types, multiplicities and primitive
types are mapped. Elements that cannot be placed or are not supported are skipped and listed in the import report.

## SysML v2 text (.sysml)

Export the whole model, import a `.sysml` file as a new project, or edit a package as text with live synchronisation.
See [SysML v2 text](sysml-v2.md).

## Enterprise Architect (.qea / .qeax)

Meridian imports EA 16+ repositories stored as SQLite (`.qea`, `.qeax`; `.eapx` files that are SQLite also work): packages, elements, attributes, operations,
connectors (including SysML stereotypes such as satisfy and verify), tagged values and diagram layouts. Older `.eap`
(Access) files must be converted to `.qea` in EA first, or exported from EA as XMI.

## Requirements

### CSV

**Exchange → Import CSV…** imports into the selected package; **Export CSV** writes every requirement. Columns:

| Column | Notes |
| --- | --- |
| `Id` | matches existing requirements (updated) or creates new ones |
| `Name`, `Text`, `Documentation` | |
| `Parent Id` | nests a requirement under another |
| `Satisfied By`, `Verified By` | names of the linked elements (export) |

Headers are matched by name, quoted fields and embedded newlines are supported, and the import is one undo step.

### Excel round trip with preview

**Export Excel (.xlsx)** writes a *Requirements* sheet with `Id`, `Name`, `Text`, `Priority`, `Status`, `Owner`,
`Verification Method`, `Risk`, `Rationale`, `Documentation`, one `Attr: <name>` column per custom attribute, plus
`Parent Id`, `Satisfied By` and `Verified By`.

Edit the sheet (or have a stakeholder edit it), then **Re-import Excel / CSV with preview…**. Meridian matches rows to
requirements by id and shows a reviewable diff:

- **Added** and **Changed** rows (with every field change, old → new) are accepted by default;
- requirements **not in the sheet** are listed too, but deleting them must be ticked explicitly;
- **Accept all** / **Accept none** toggle everything.

The accepted changes are applied as one undo step. Common alternative headers are recognised (`Req Id`,
`Requirement Id`, `Title`, `Description`, `Requirement Text`, `Parent`).

> [!NOTE]
> **TODO (screenshot):** the re-import preview with one added, one changed and one removed requirement.

### ReqIF 1.2

**Import ReqIF (.reqif / .reqifz)…** imports into the selected package; **Export ReqIF** writes every requirement.
Export writes one SPEC-OBJECT per requirement (ForeignID, Name, Text), the containment hierarchy as a SPECIFICATION
and derive / trace links as SPEC-RELATIONs. Import accepts STRING and XHTML attributes and nests by hierarchy. Use it
with IBM DOORS, Polarion, Jama, Codebeamer and other ALM tools.

## BPMN 2.0

Import `.bpmn` files (processes, pools, lanes, flow nodes, sequence and message flows, and their layout) as a BPMN
diagram; export the active BPMN diagram as BPMN 2.0 XML with BPMN DI.

## ArchiMate

Import and export the Open Group **ArchiMate Model Exchange File Format** (3.x): elements, relationships and views.

## SQL

Import PostgreSQL, SQL Server, MySQL / MariaDB, SQLite or Oracle DDL scripts and SQL Server `.dacpac` files as a
database with its schemas, tables, keys, indexes, views and types (**Import ▸ Database schema (DDL / .dacpac)…** or
**Import into this project…**), connect to a live database, and generate DDL and migration scripts for all five
dialects. See [Database modelling](database.md).

## C#

- **Import into this project** accepts `.cs` files or a `.zip` of sources: namespaces become packages; classes,
  interfaces, records, structs and enums become classifiers with their properties, fields and methods, inheritance and
  interface realization.
- **Generate → C#** downloads a zip of C# source skeletons for the model's structural classifiers.

## Mermaid

Meridian imports [Mermaid](https://mermaid.js.org/) text: `.mmd` / `.mermaid` files, several diagrams in one file, and
the ```` ```mermaid ```` blocks of Markdown files.

- **Project menu → Paste Mermaid…** (or ⌘K "Paste Mermaid") shows a live preview of what each diagram will become,
  lets you choose **create new** or **match by name** (reuse existing blocks and requirements), and imports everything
  as one undo step. Text produced by any AI assistant can be pasted as is, Markdown fences included.
- **Import into this project…** and **Open a file as a new project…** accept `.mmd`, `.mermaid` and `.md` files, and
  dropping a `.mmd` file on the canvas imports it.
- Flowcharts become activity diagrams (decisions, initial / final nodes, guards, subgraphs as swimlanes), class diagrams
  block definition diagrams (blocks, interface blocks, enumerations, properties, operations, generalization,
  composition with part properties, aggregation, association, dependency, realization), state diagrams state machines,
  sequence diagrams interactions with messages and combined fragments, requirement diagrams requirements with
  satisfy / verify / trace / derive / refine / copy, and ER and C4 diagrams blocks. Gantt charts, pie charts, mind maps
  and other non-SysML types are skipped with a warning.
- **Export diagram as Mermaid (.mmd)** and **Copy diagram as Mermaid** write activity, state machine, sequence, block
  definition and requirement diagrams back out.

See [Mermaid](mermaid.md) for the whole workflow and what each Mermaid diagram becomes.

## Images

SVG and PNG export of diagrams is described in [Reports and documents](document-generation.md#diagram-images).

## Scripted and plugin formats

A [script](scripting.md) can write any text format with `Download(...)`. A [plugin](plugins.md) can register its own
importers and exporters; they appear under **Generate ▸ Plugins**.
