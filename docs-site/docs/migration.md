# Coming from Cameo / EA

This page maps the concepts, windows and menus of **Cameo Systems Modeler / MagicDraw** (with Teamwork Cloud) and
**Sparx Enterprise Architect** to their Meridian equivalents, and explains how to bring an existing model across.

## Bringing your model across

| From | How | Notes |
| --- | --- | --- |
| Cameo / MagicDraw | open the `.mdzip` (or `.mdxml`) file directly with **Open a file as a new project** (or double-click it with the desktop app installed). XMI exports work too. | Packages, blocks, value types, interface blocks, ports, part/reference/value properties, requirements (id and text) with satisfy / verify / refine / deriveReqt / trace / allocate, activities (including datastore and central buffer nodes), state machines with every pseudostate kind, interactions with combined fragment operands and their separators, use cases, constraint blocks, stereotypes and tagged values map, associations keep both end multiplicities, and diagrams keep their layout. Used projects are read from the project metadata (local and Teamwork Cloud) and listed as found, copied or linked; the standard profiles are recognised. Re-importing updates the project by element id. The import report lists anything approximated or skipped ([details](import-export.md#cameo-systems-modeler--magicdraw-mdzip--mdxml)). |
| Enterprise Architect 16+ | open the `.qea` / `.qeax` file directly with **Open a file as a new project** (or the desktop start screen) | Packages, elements, attributes, operations, connectors with SysML stereotypes, tagged values and diagram layouts are read straight from the repository. State machine pseudostates (history, choice, junction, fork / join, entry / exit, terminate), sequence diagrams with lifelines, messages and combined fragments whose partitions become operands, datastore and central buffer nodes, and both association end cardinalities are mapped; codes that Sparx does not document are reported ([details](import-export.md#enterprise-architect-qea--qeax)). Data models («table» classes) keep column lengths, NOT NULL, primary keys, unique constraints, indexes and «FK» connectors as foreign keys. |
| Database schemas | **Import ▸ Database schema (DDL / .dacpac)…** or **Connect to a database…** | PostgreSQL, SQL Server, MySQL / MariaDB, SQLite and Oracle DDL; SQL Server `.dacpac`; live PostgreSQL, MySQL and SQLite ([details](database.md)) |
| Older EA (`.eap`, Access) | convert to `.qea` in EA, or export XMI 2.5 from EA | |
| DOORS Next, Polarion | **Integrations ▸ DOORS & Polarion**: a live two-way requirements sync, or ReqIF | see [Integrations](integrations.md#doors-next-and-polarion) |
| Jama, Codebeamer, classic DOORS | **ReqIF** or CSV / Excel | see [Import and export](import-export.md#requirements) |

A new, empty project offers **Open a Cameo file** in the middle of the screen as well.

**What the import report tells you.** Besides counts and skipped constructs, the report (in the **Output** panel) has an
*Approximated* section: pseudostates whose kind Meridian does not know (kept as choices), fragments whose operand separators
were not in the file (spread evenly), and for Cameo the **used projects** — each one found (referenced only), copied (its
shared packages were in the file) or linked to a Meridian project — plus the standard profiles the project used.

### Checking what came across

Every Cameo import counts what the file holds and compares it with what Meridian kept. If anything was left out, an
**Import check** dialog opens with:

- the share of **model elements**, **diagrams** and **stereotype applications** that came across. An element *folded into
  another* still counts as kept: a documentation comment becomes the element's documentation, and a constraint on a
  constraint block becomes its equation;
- the elements **not imported**, by UML type (for example `InteractionUse` or `Reception`), and the
  reasons the importer recorded;
- stereotypes lost because the element they were applied to was not imported, and tagged values with structured values
  that could not be copied.

Some Cameo constructs have no exact Meridian counterpart and arrive approximated (the report warns about each):

- **Structured activity nodes** (structured, loop, conditional and sequence nodes, expansion regions) become an action
  with a stereotype such as «loopNode», which keeps its pins and flows. The actions and flows inside are moved up into the
  owning activity. Loop parts and conditional clauses are kept as tagged values that name those actions.
- **Opaque and function behaviors** become activities stereotyped «opaqueBehavior» / «functionBehavior» that keep their
  body text and language, so call behavior actions still point at them.
- **Tagged values that reference elements** show the referenced element's name. A reference to an element in a used
  project that was not loaded keeps the Cameo id.
- **Orthogonal regions** of a state are merged into one region.

**Download report** saves the same information as a Markdown file, which is useful to attach to a migration ticket.
The full list, with every warning, is also in the **Output** panel. When nothing was lost, the dialog does not open.

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
| Create diagram from model elements, "Display related elements" in a new diagram | **Auto-diagram** (whole model, a package, or the views you tick) and the assistant's **Diagram my model** |
| Refining a use case into an activity, an activity into a sequence (by hand) | **Create from…**: requirements → use cases → activity → sequence → BDD / IBD → state machine, with **Update from source** ([details](derivation.md)) |
| Multiple selection, "Specification of selected elements", Symbol properties for many symbols | **Multi-select** with the bulk Inspector (colours, line style, stereotypes, shared fields) |
| Create relations to several elements, Smart manipulator | **Link to…** / **Trace to requirement…** for a selection; lines pick their type automatically ([details](multi-select.md)) |
| Requirement table | **Requirements → Table** |
| Dependency matrix | **Traceability → Matrix** (rows, columns and relationship kind; click a cell to create a link) |
| Requirements traceability / coverage analysis | **Traceability table** (gaps, suspect links, multi-level, orphans, per-package coverage) and **Coverage** |
| Relation map (relation map diagram / element) | **Relation map** tab (right-click ▸ Relation map, ⌘K "Relation map of …", Traceability → Relation map): radial or tree, depth 1–6, relationship presets (traceability, structure, allocation), element kinds, direction, containment, expand / collapse per node, "+N more" beyond 500 elements, create a diagram from the selection, SVG / PNG, a report block; saved as a *Relation Map* element ([details](relation-map.md)) |
| Impact analysis | **Traceability → Impact** (transitive dependents, as a list) |
| Suspect links | **Suspect links**, cleared in the traceability table |
| Generic table | **Model views → Generic table** (saved as a *Table View* element) |
| Instance specifications, slots, instance table, "Create instance" | **Instances**: right-click a block ▸ **Create instance of…** builds the whole part tree; the **instance table** edits slots with units and exports CSV / XLSX; **object diagrams**; links typed by associations; a run's subject can be an instance ([details](instances.md)) |
| Excel/CSV Import (and Excel/CSV Sync): map columns to elements, properties and tagged values | **Import ▸ Spreadsheet (Excel / CSV)…**: any element kind, column mapping with presets, hierarchy, relationships, a reviewed re-import and export with the same mapping ([details](spreadsheet-import.md)) |
| Find / smart packages / derived queries | **Model views → Query** (saved as a *Query* element); the **command palette** (⌘K) finds anything by name |
| Validation suites and constraints | **Validate** (built-in rules) plus **validation rules** written as model queries, and plugin rules |
| Profiles, stereotypes, customizations | **Profiles** with stereotypes, metaclasses, colours and tag definitions; *Add UAF profile* |
| Parametric evaluation (Cameo Simulation Toolkit) | **Parametrics** panel (solves each equation for any one unknown) |
| Simulation Toolkit: activities and state machines | **Simulation → Step** and **Timed (discrete event)**; animation on the diagram, signals, time and change events |
| Monte Carlo / variability analysis | **Monte Carlo** tab with distributions on value properties |
| FMU co-simulation | **FMI import** and **co-simulation** along IBD connectors (Simulation ▸ Co-simulation; an administrator turns running FMUs on) |
| Simulation-based requirement verification (Cameo Simulation Toolkit requirements verification) | **Simulate ▸ Verify**: criteria such as `mass <= 1500 kg`, checked with parametrics, Monte Carlo, timed simulation or co-simulation; pass / fail on diagrams ([details](verification.md)) |
| Product line / variability | **Variants**: feature models, presence conditions, configurations, variant export |
| Trade-off analysis | **Trade studies** with weights, utility curves and sensitivity |
| Report Wizard / document templates | **Generate → Formats → Word / PDF document from template**, and the HTML **Report** |
| File ▸ Print, Print Preview, Page Setup (fit to N pages, scale, print diagrams of a package) | **Print / PDF…** (⌘P): paper, margins, fit to one page / N × M pages / fixed scale, overlap and registration marks, header and footer fields, preview with page thumbnails; vector PDF, PNG pages or the browser's print dialog; **Print diagrams…** for a package with a contents page ([details](printing.md)) |
| Macros (Groovy, JavaScript, Jython) | **Script console** (C#) |
| Plugins (Java Open API) | **.NET plugins** (`IMeridianPlugin`) |
| SysML v2 textual notation | **SysML v2 text** editor, live-synced with the model |
| Teamwork Cloud: commit, update | edits are **saved live**; **Commit** records a version; collaborators see changes in real time |
| Teamwork Cloud: branches, merge | **Branches & merge…**: branches per project, three-way merge with conflict resolution |
| Teamwork Cloud: element locking | optional **Lock for editing** / **Lock with contents** (server) |
| Teamwork Cloud: element history (who changed an element, compare its revisions) | right-click ▸ **History…** or the inspector's **History** section: every commit that changed the element, with field diffs, restore of one field and the model at that version ([details](version-control.md#element-history)) |
| Teamwork Cloud: permissions on packages | right-click a package ▸ **Permissions…**: none, read, edit or manage per person or team ([details](permissions.md)) |
| Containment tree context menu: Create Element ▸, Create Diagram ▸ | the same: right-click ▸ **New element ▸** and **New diagram ▸** |
| Help ▸ Keyboard shortcuts | **Help (?) ▸ Keyboard & mouse** ([details](keyboard-shortcuts.md)) |
| Code engineering sets (round trip with code) | **Code sync**: GitHub / GitLab pushes update a review branch you accept into `main` ([details](code-sync.md)) |
| Environment options (look and feel, diagram defaults) | **Settings** (⌘,): theme, accent, density, grid, connector style, per user or per device ([details](settings.md)) |
| Tags / baselines | **Baselines…** |
| Used projects / modules (shared packages, "Use project", "Update used project") | **Used projects…**: a package of another project, read-only and pinned to a version, branch head or baseline, with update review and suspect marking; a Cameo import offers to use projects already in Meridian instead of copying them ([details](libraries.md)) |
| Collaborator reviews and comments | **Comments** with @mentions and **Reviews** with votes and checklists |
| OSLC / DataHub | **OSLC RM provider**, issue tracker links, webhooks |
| Cameo AI assistant / third-party AI plugins | **AI assistant** on Claude, a local model or Claude Code; every change reviewed before it is applied ([details](ai-assistant.md)) |
| Import from diagram-as-code tools | **Mermaid** import and export ([details](mermaid.md)) |
| Deployment diagrams: nodes, devices, execution environments, artifacts, «deploy», «manifest», communication paths (`uml:Node`, `uml:Device`, `uml:ExecutionEnvironment`, `uml:Artifact`, `uml:Deployment`, `uml:Manifestation`, `uml:CommunicationPath`) | **Deployment diagrams** with 3D node boxes, nesting, automatic «deploy» / «manifest» lines, **Deploy to…**, a deployment table and matrix; imported from Cameo with their diagrams ([details](deployment-diagrams.md)) |
| Data modeling with SQL / database profiles (Cameo DataHub, SQL profile), entity-relationship diagrams | **Database diagrams** (crow's foot) with tables, views, keys, subject areas and logical entities; DDL, `.dacpac`, Mermaid `erDiagram` and live import with refresh; DDL and migration scripts; data dictionary ([details](database.md)) |

## Enterprise Architect

| Enterprise Architect | Meridian |
| --- | --- |
| Project Browser | **Model explorer** |
| Properties window, docked properties, tagged values window | **Inspector** |
| Toolbox | **Shapes panel** |
| Quick Linker | **Hover arrows** (drag to connect, click to add a connected shape) |
| Diagram layout tools | **Arrange**, align and distribute in the selection toolbar; **Auto-diagram** to draw new views |
| Multiple selection and bulk property edit | **Multi-select** with the bulk Inspector and **Link to…** ([details](multi-select.md)) |
| Relationship Matrix | **Traceability matrix** |
| Traceability window | **Traceability table** and **Impact** |
| Model Search, Model Views | **Query** and **Generic table** in *Model views* |
| Objects (instances) with run state, object diagrams | **Instances** with slots, the **instance table** and **object diagrams**; `.qea` Objects import with their run state as slots ([details](instances.md)) |
| Security: package permissions | **Package permissions** (none / read / edit / manage per person or team) ([details](permissions.md)) |
| Simulation-based requirement checks | **Simulate ▸ Verify** with requirement criteria ([details](verification.md)) |
| Architecture and code conformance checks | **Code ▸ Architecture**: synced code against the model's allowed dependencies ([details](code-sync.md)) |
| Model Validation | **Validate** and query-based **validation rules** |
| Requirements (internal and external), Specification Manager | requirement elements with the **requirements table**; spreadsheet round trip with preview |
| CSV Import / Export specifications (Publish ▸ CSV), preserve hierarchy | **Import ▸ Spreadsheet (Excel / CSV)…**: mapping presets saved per project and exportable as JSON, any element kind, `.xlsx` as well as CSV, parent / level / indent hierarchy, reviewed sync ([details](spreadsheet-import.md)) |
| Preferences, visual styles | **Settings** (⌘,) with light / dark / system themes and accent colours ([details](settings.md)) |
| MDG Technologies, UML profiles | **profiles and stereotypes**, and **plugins** for behaviour |
| Document generation (RTF / DOCX templates) | **Word / PDF from template** with section presets |
| Page Setup, Print Preview, print a diagram across pages | **Print / PDF…** (⌘P) with paper, scaling, overlap, header and footer fields and a page preview; **Print diagrams…** for a package ([details](printing.md)) |
| Baselines and Compare | **Baselines…** with compare and CSV export, and **Version history** compare |
| Element audit / auditing (who changed an element and when, before and after) | **History…** on any element: author, date and changed fields per commit, before / after diffs, restore one field ([details](version-control.md#element-history)) |
| Shared packages, package imports across repositories, Reusable Asset Service | **Used projects…**: reuse a package of another project read-only, pinned, with update review ([details](libraries.md)) |
| Version control (SVN, Git, TFS packages) | built-in **commits, branches and merge** for the whole project |
| Security and user locks | **project roles** (owner, editor, viewer) and optional **element locks** |
| Discussions, reviews | **Comments** and **Reviews** |
| Scripting (JavaScript, JScript, VBScript) | **Script console** (C#) |
| Simulation (SysML simulation, executable state machines) | **Simulation** tabs |
| BPMN, ArchiMate | **BPMN** and **ArchiMate** diagrams with BPMN XML and ArchiMate exchange |
| Deployment diagrams: Node, Device, ExecutionEnvironment, Artifact, Deployment and Manifest connectors, communication paths | **Deployment diagrams**, imported from the `.qea` with nesting and «deploy» / «manifest» links; **Deploy to…** for a selection ([details](deployment-diagrams.md)) |
| Database Engineering: data models, «table» / «column», PK / FK operations | **Database modelling**: databases, schemas, tables, views, keys, indexes, sequences, enum types; EA data models import with lengths, NOT NULL, PK / FK / unique / index operations and «FK» connectors ([details](database.md)) |
| Database Builder: connect to a live database, import / synchronize schema | **Connect to a database…** (PostgreSQL, MySQL / MariaDB, SQLite) and **Refresh…** with a change review; SQL Server and Oracle through DDL or `.dacpac` |
| Generate DDL, DDL compare / transformation | **Generate DDL…** for five dialects and **Migration script…** (ALTER statements from a version, branch, baseline or live database, with warnings) |
| Package-based diagram filtering / data model subject areas | **Subject areas**: colour-framed groups of tables across schemas, diagram filters, permissions |
| Code engineering | C# reverse engineering and C# skeleton generation; **Code sync** from GitHub / GitLab with a review branch ([details](code-sync.md)) |
| Model transformations (MDA style) | **Create from…** derivations along the MBSE chain ([details](derivation.md)) |
| AI-assisted modeling | **AI assistant** (Claude, a local model or Claude Code) ([details](ai-assistant.md)) |
| Import Mermaid / PlantUML text | **Mermaid** import and export ([details](mermaid.md)); PlantUML is not supported |
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

## Finding things by their Cameo / EA name

Press **⌘K** and type the name you know: *dependency matrix*, *relationship matrix*, *requirement table*, *report
wizard*, *use project*, *quick diagram layout*, *containment tree*, *model search*, *macros*. The palette offers the
Meridian equivalent. **Help (?) ▸ Coming from Cameo or EA** is a short guided tour of the same mapping.

## Not available (yet)

Meridian does not have a glossary or OCL constraints yet. Queries replace most OCL validation rules.
