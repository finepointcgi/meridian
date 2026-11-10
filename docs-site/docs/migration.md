# Coming from Cameo / EA

This page maps the concepts, windows and menus of **Cameo Systems Modeler / MagicDraw** (with Teamwork Cloud) and
**Sparx Enterprise Architect** to their Meridian equivalents, and explains how to bring an existing model across.

## Bringing your model across

| From | How | Notes |
| --- | --- | --- |
| Cameo / MagicDraw | export the model as **UML 2.5 / SysML XMI**, then **Import file as project** | Blocks, parts, ports, value types, requirements (id and text), satisfy / verify / derive / refine / trace, multiplicities and primitive types map. Diagrams come across when the export includes UML Diagram Interchange. Check the import report for skipped elements. |
| Enterprise Architect 16+ | open the `.qea` / `.qeax` file directly with **Import file as project** (or the desktop start screen) | Packages, elements, attributes, operations, connectors with SysML stereotypes, tagged values and diagram layouts are read straight from the repository. |
| Older EA (`.eap`, Access) | convert to `.qea` in EA, or export XMI 2.5 from EA | |
| DOORS, Polarion, Jama, Codebeamer | **ReqIF** or CSV / Excel | see [Import and export](import-export.md#requirements) |

After importing, run **Validate** (**F7**) and look at the *Traceability table* to see what arrived. A good habit is
to **Commit** the imported state immediately and tag it as a **baseline** (for example `Imported from Cameo`), so you
can always compare against it.

## Cameo Systems Modeler / MagicDraw

| Cameo | Meridian |
| --- | --- |
| Containment tree | **Model explorer** (left). Drag to move, drag onto diagrams to show, right-click for New / Rename / Delete. |
| Specification window | **Inspector** (right): properties, specification, stereotypes, tagged values, traceability, appearance, comments |
| Diagram palette | **Shapes panel** (searchable), plus **hover arrows** and double-click quick add |
| Symbol properties | **Format bar** (fill, line, text colour, line width, format painter) and *Appearance on this diagram* in the inspector |
| Display related elements / Display paths | canvas menu **Display related elements**; relationships to shown elements are added automatically |
| Layout → Quick diagram layout | **Arrange** |
| Requirement table | **Requirements → Table** |
| Dependency matrix | **Traceability → Matrix** (rows, columns and relationship kind; click a cell to create a link) |
| Requirements traceability / coverage analysis | **Traceability table** (gaps, suspect links, multi-level, orphans, per-package coverage) and **Coverage** |
| Relation map / impact analysis | **Traceability → Impact** (transitive dependents, as a list) |
| Suspect links | **Suspect links**, cleared in the traceability table |
| Generic table | **Model views → Table** (saved as a *Table View* element) |
| Find / smart packages / derived queries | **Model views → Query** (saved as a *Query* element); the **command palette** (⌘K) finds anything by name |
| Validation suites and constraints | **Validate** (built-in rules) plus **validation rules** written as model queries, and plugin rules |
| Profiles, stereotypes, customizations | **Profiles** with stereotypes, metaclasses, colours and tag definitions; *Add UAF profile* |
| Parametric evaluation (Cameo Simulation Toolkit) | **Parametrics** panel (solves each equation for any one unknown) |
| Simulation Toolkit: activities and state machines | **Simulation → Step** and **Timed (discrete event)**; animation on the diagram, signals, time and change events |
| Monte Carlo / variability analysis | **Monte Carlo** tab with distributions on value properties |
| FMU co-simulation | **FMI import** of the FMU interface; co-simulation is not implemented |
| Product line / variability | **Variants**: feature models, presence conditions, configurations, variant export |
| Trade-off analysis | **Trade studies** with weights, utility curves and sensitivity |
| Report Wizard / document templates | **Generate → Formats → Word / PDF document from template**, and the HTML **Report** |
| Macros (Groovy, JavaScript, Jython) | **Script console** (C#) |
| Plugins (Java Open API) | **.NET plugins** (`IMeridianPlugin`) |
| SysML v2 textual notation | **SysML v2 text** editor, live-synced with the model |
| Teamwork Cloud: commit, update | edits are **saved live**; **Commit** records a version; collaborators see changes in real time |
| Teamwork Cloud: branches, merge | **Branches & merge…**: branches per project, three-way merge with conflict resolution |
| Teamwork Cloud: element locking | optional **Lock for editing** / **Lock with contents** (server) |
| Tags / baselines | **Baselines…** |
| Collaborator reviews and comments | **Comments** with @mentions and **Reviews** with votes and checklists |
| OSLC / DataHub | **OSLC RM provider**, issue tracker links, webhooks |

## Enterprise Architect

| Enterprise Architect | Meridian |
| --- | --- |
| Project Browser | **Model explorer** |
| Properties window, docked properties, tagged values window | **Inspector** |
| Toolbox | **Shapes panel** |
| Quick Linker | **Hover arrows** (drag to connect, click to add a connected shape) |
| Diagram layout tools | **Arrange**, align and distribute in the selection toolbar |
| Relationship Matrix | **Traceability matrix** |
| Traceability window | **Traceability table** and **Impact** |
| Model Search, Model Views | **Query** and **Table** in *Model views* |
| Model Validation | **Validate** and query-based **validation rules** |
| Requirements (internal and external), Specification Manager | requirement elements with the **requirements table**; spreadsheet round trip with preview |
| MDG Technologies, UML profiles | **profiles and stereotypes**, and **plugins** for behaviour |
| Document generation (RTF / DOCX templates) | **Word / PDF from template** with section presets |
| Baselines and Compare | **Baselines…** with compare and CSV export, and **Version history** compare |
| Version control (SVN, Git, TFS packages) | built-in **commits, branches and merge** for the whole project |
| Security and user locks | **project roles** (owner, editor, viewer) and optional **element locks** |
| Discussions, reviews | **Comments** and **Reviews** |
| Scripting (JavaScript, JScript, VBScript) | **Script console** (C#) |
| Simulation (SysML simulation, executable state machines) | **Simulation** tabs |
| BPMN, ArchiMate, database modeling | **BPMN**, **ArchiMate** and **ER diagrams** with BPMN XML, ArchiMate exchange and SQL DDL |
| Code engineering | C# reverse engineering and C# skeleton generation |
| Pro Cloud Server, OSLC | **team server** with the OSLC RM provider and a read-only SysML v2 API |

## Things that work differently

- **Delete vs remove.** **Backspace** removes a shape from the diagram only; **Delete** removes the element from the
  model. EA and Cameo ask each time; Meridian makes both one keystroke and undoable.
- **No save button for edits.** Edits persist immediately. **Commit** creates a named version in history, like a
  Teamwork Cloud commit.
- **Requirements ids** are assigned automatically and can be renumbered with a prefix.
- **Queries are the extension point for rules.** Instead of OCL constraints, write a query such as
  `kind:Block where not has:satisfy` and save it as a rule with a severity and message.
- **Everything saved is in the model.** Tables, queries, rules and scripts are model elements, so they are versioned
  and merged with the design.

## Not available (yet)

Meridian does not have instance specifications and object diagrams, timing, communication, interaction overview or
deployment diagrams, project usages (mounted modules shared between projects), OCL, or FMU co-simulation. Components
can be drawn on class diagrams.
