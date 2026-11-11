# Import and export

## Where to find it

| Menu | What it does |
| --- | --- |
| Project menu → **Open a file as a new project…** (.meridian, .xmi, .sysml, EA .qea, Cameo .mdzip / .mdxml, Mermaid .mmd / .md) | creates a new project from a file |
| Project menu → **Import into this project…** (BPMN, ArchiMate, SQL, C#, Mermaid, Cameo re-import) | adds the content of one or more files to the open project |
| Project menu → **Paste Mermaid…** | imports Mermaid text, for example from an AI assistant, with a preview ([Mermaid](#mermaid)) |
| Project menu → **Export ▸** **Download .meridian file**, **Export XMI 2.5.1**, **Export SysML v2 text**, **Export diagram as SVG / PNG / Mermaid**, **Copy diagram as Mermaid** | exports |
| Right-click a diagram tab → **Export ▸** | SVG, PNG, image or Mermaid of that diagram |
| Project menu → **Spreadsheet (Excel / CSV)…**, or drop a sheet on the model explorer | any element kind from Excel or CSV with a column mapping, re-import with a review, export with the same mapping ([Spreadsheet import](spreadsheet-import.md)) |
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
types are mapped. Combined fragments keep one **operand** per `uml:InteractionOperand`, guarded by its
`InteractionConstraint`; pseudostates keep their `kind` (choice, junction, fork, join, shallow / deep history, entry / exit
point, terminate); `DataStoreNode` and `CentralBufferNode` become datastore and central buffer nodes; an association keeps the
multiplicities of both member ends. Operand separators come from Meridian's own diagram data or, in XMI from other tools,
from the operands' own shapes. Elements that cannot be placed or are not supported are skipped and listed in the import report;
what was approximated (an unknown pseudostate kind read as a choice, operand separators spread evenly because the file has
none) is listed with the warnings, each starting with "Approximated".

## Cameo Systems Modeler / MagicDraw (.mdzip / .mdxml)

**Open a file as a new project** reads `.mdzip` projects (model parts named `…uml_model.model` / `…uml_model.shared_model` or,
in later versions, `…uml_umodel.model` / `…uml_umodel.shared_umodel`) and `.mdxml` exports, including diagram layouts.

| Cameo | Meridian |
| --- | --- |
| `uml:CombinedFragment` with `uml:InteractionOperand`s | fragment with one **operand** each, in order; the guard is the operand's `InteractionConstraint` specification |
| operand shapes inside the fragment's shape on a sequence diagram | the **separators**: each later operand starts where Cameo drew its shape; operands without a readable shape share the rest of the fragment evenly (listed as *Operand separators guessed*) |
| `uml:Pseudostate` `kind` = initial (or no kind) | initial state |
| `kind` = choice, junction, fork, join, shallowHistory, deepHistory, entryPoint, exitPoint, terminate | **pseudostate** of that kind; entry / exit points (`connectionPoint`) stay on their state's border |
| any other `kind` | choice pseudostate, listed as *Unknown pseudostate kinds* |
| `uml:DataStoreNode`, `uml:CentralBufferNode` | object node with the «datastore» / «centralBuffer» kind |
| association `memberEnd`s | the target end's multiplicity, and the other end's as the **source multiplicity** (when the file states one) |

**Used projects.** Meridian reads the project metadata (`com.nomagic.ci.metamodel.project`, or `project.xmi` where present):
the project's own id, the packages it shares, and each **project usage** with its `usedProjectURI`, project id and mount
points (the ids of the shared packages mounted from it). `local:/Standard%20Parts.mdzip` and `file:` paths are shown by file
name; `twcloud:/…` (Teamwork Cloud) usages by the `projectName` the URI carries, else as "Teamwork Cloud project" and the
first characters of the project id. Used-project proxy entries and `href`s into other projects are recognised too. The
standard profiles and libraries (SysML, UML Standard Profile, MD Customization for SysML, QUDV, ISO 80000 …) are listed apart:
Meridian has them built in. The import report says for each used project whether it was **found** (referenced only: types
are kept by name), **copied** (its shared packages were in the file and imported with it) or **linked** (an existing Meridian
project is used instead — see [Used projects](libraries.md#coming-from-cameo)).

> [!NOTE]
> Cameo does not publish the format of its project metadata. The element and attribute names Meridian reads
> (`projectUsages`, `usedProjectURI`, `mountPoints`, `sharePoints`, `properties key="LOCAL_PROJECT_ID"`) follow Cameo's
> project model as seen in exported projects; anything not found there falls back to the references in the model itself,
> so a project is never refused because of its metadata.

## SysML v2 text (.sysml)

Export the whole model, import a `.sysml` file as a new project, or edit a package as text with live synchronisation.
See [SysML v2 text](sysml-v2.md).

## Enterprise Architect (.qea / .qeax)

Meridian imports EA 16+ repositories stored as SQLite (`.qea`, `.qeax`; `.eapx` files that are SQLite also work): packages, elements, attributes, operations,
connectors (including SysML stereotypes such as satisfy and verify), tagged values and diagram layouts. Older `.eap`
(Access) and `.feap` (Firebird) files must be converted to `.qea` in EA first, or exported from EA as XMI.

State machine nodes, fragments and object nodes are read from `t_object` (`Object_Type`, and `NType`, which EA's automation
interface calls *Subtype*):

| EA | Meridian | Source |
| --- | --- | --- |
| `StateNode` NType 3 / 4 | initial state / final state | confirmed |
| `StateNode` NType 5 / 15 | shallow / deep history | confirmed |
| `StateNode` NType 11 / 12 | choice / terminate | confirmed |
| `StateNode` NType 13 / 14, `EntryPoint`, `ExitPoint` | entry / exit point | confirmed |
| `StateNode` NType 100 / 101 / 102 | activity initial / activity final / flow final (as activity final) | confirmed |
| `StateNode` NType 10 | junction — reported as approximated | not confirmed |
| `StateNode`, any other NType | choice — reported as approximated | — |
| `Synchronization` in a state machine | fork or join pseudostate: by its «Fork» / «Join» stereotype, else a fork when it has more than one outgoing transition (reported) | confirmed / inferred |
| `Synchronization` in an activity | fork node | confirmed |
| `CentralBufferNode`; `DataStore` / `DataStoreNode`; `Object` «datastore» | central buffer node; datastore node | confirmed |
| `Interaction`, `Sequence` (lifeline), `Sequence` connectors (messages) | interaction, lifeline, message (in sequence-number order) | confirmed |
| `InteractionFragment` NType 0 / 1 / 3 / 4 | alt / opt / par / loop fragment | confirmed |
| `InteractionFragment` NType 2, 5–11 | break, critical, neg, assert, strict, seq, ignore, consider — reported as approximated | EA toolbox order, not confirmed |

A fragment's **operands** are EA's *partitions*: the `t_xref` row named `Partitions` whose client is the fragment, with one
`@PAR;Name=<guard>;Size=<height>;…;@ENDPAR;` entry per operand, top first. Each becomes an operand guarded by its name (brackets
around a guard are removed), and the heights place the separators. When the heights do not fit the fragment's shape the
separators are spread evenly and the import report says so. Association ends keep `DestCard` as the target multiplicity and
`SourceCard` as the source multiplicity (swapped for compositions and aggregations, which EA draws from the part).

"Confirmed" codes match Sparx's EA object model as used by the open-source EA tools of the OpenMBEE MTIP project and Geert
Bellekens' EA toolpack; the others are listed so you can check them after an import.

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

Edit the sheet (or have a stakeholder edit it), then **Import / re-import Excel / CSV…**: the
[spreadsheet import](spreadsheet-import.md) dialog opens with the **Requirements** preset (change the mapping there if
your sheet has other columns). Meridian matches rows to requirements by id and shows a reviewable diff:

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
