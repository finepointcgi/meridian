# Spreadsheet import and sync (Excel / CSV)

Bring any kind of element in from a spreadsheet — blocks, parts lists, value properties, ports and interfaces, signals,
requirements, test cases, use cases, activities and actions, states, database tables and columns, or any stereotyped
element — with a **column mapping**, and keep the sheet and the model in sync: export with the same mapping, edit in
Excel, import again and review what changed. It is Meridian's counterpart of Cameo's *Excel/CSV Import* (and Excel/CSV
Sync) and Enterprise Architect's *CSV import / export specifications*.

## Where to find it

| Entry point | Opens |
| --- | --- |
| Project menu → **Import ▸ Spreadsheet (Excel / CSV)…** | the import dialog, empty |
| Drop an `.xlsx`, `.csv` or `.tsv` file on the **model explorer** | the dialog with that file loaded |
| Ribbon **Requirements → Exchange → Import / re-import Excel / CSV**, and **Re-import** in the requirements table | the dialog with the **Requirements** preset |
| ⌘K **Import spreadsheet**, **Export to spreadsheet with a mapping**, **Import parts list (BOM)** | the dialog (the last one with the parts-list preset) |

To draw an org chart from a list of people instead, use **Import ▸ Org chart from spreadsheet…** (see
[Org charts](org-chart.md)).

The dialog has three steps: **Source**, **Mapping** and **Review**. Nothing changes in the model until you press
**Apply**, and applying is **one undo step** (⌘Z puts everything back).

## 1. Source

- **Excel workbooks** (`.xlsx`, `.xlsm`): every sheet can be chosen. Formulas are **not evaluated** — Meridian reads the
  value Excel last calculated and saved, and says how many formula cells there were. Links to other workbooks are
  ignored (with a warning). Dates come in as `yyyy-mm-dd`, booleans as `TRUE` / `FALSE`.
- **CSV / TSV / TXT**: the delimiter (comma, semicolon, tab or pipe) and the encoding (UTF-8, UTF-16 with or without a
  byte-order mark, Windows-1252) are detected; both can be overridden in the dialog. Quoted cells may contain the
  delimiter, quotes and line breaks.
- **Pasted cells**: copy a range in Excel and paste it into the box (tab separated).

The **header row** is detected (title rows above a table are skipped) and can be changed. A preview shows the first 50
rows, with the header row highlighted.

## 2. Mapping

### Target kind

Choose the kind every row creates — Block, Part property, Value property, Port, Interface block, Signal (a class with
«signal»), Value type, Actor, Use case, Activity, Action, State machine, State, Requirement, Test case, Constraint
block, Table, Column, Package, or any other kind — or **By column**, where a column holds the kind of each row
(`Block`, `Part property` or `part`, `Value property` or `value`, `Signal`, `Interface block`, `Requirement` …).

**Stereotype** applies a stereotype to every created element, and only elements carrying it are matched (for example
*Block* with «hardware»).

### Columns

Each column maps to one of:

| Maps to | Meaning |
| --- | --- |
| **Key / id** | the matching key (see [Matching](#3-matching-and-review)) |
| **Name**, **Documentation** | |
| **Kind** | the element kind of the row (*By column* mappings) |
| **Owner / package path** | `Structure::Powertrain` or `Structure/Powertrain`. Missing packages are created; a single name such as `Vehicle` also finds a block anywhere in the model (properties and ports need an existing owner) |
| **Parent (hierarchy)** | the parent row's key (or name / qualified name) |
| **Level (hierarchy)** | an outline level `1`, `2`, `3` … (or `1.2.3`), as in a bill of materials |
| **Type** | a primitive (`Real`, `Integer`, `Boolean`, `String`), a name or a qualified name. An unknown type is a row error, or is created when **Create missing types** is ticked (a block for part properties, a value type for value properties, an interface block for ports). For database columns the type is the SQL type |
| **Multiplicity** | `1`, `0..1`, `1..*`, `*`, `2..4` — for a block in a parts list, the quantity of its part property |
| **Value (with unit)** | `1450 kg` sets the default value `1450` and the unit `kg`. Without a name it is the row's own value (value properties, ports); with a name, it is that value property of the row's element (created when missing) |
| **Unit** | a unit symbol for the row's value, or for the named value property |
| **Stereotypes** | separated by commas or semicolons; the sheet then lists them all |
| **Tagged value** | `tag:<name>` (headers like `Tag: Supplier` are recognised) |
| **Custom attribute** | requirement attributes `attr:<name>` (headers like `Attr: Safety level`) |
| **Other field** | any field: `text`, `priority`, `status`, `direction`, `sqlType` … |
| **Relationship** | `REQ-001; REQ-002` — the kind (satisfy, verify, trace, allocate, refine, deriveReqt, dependency …), the direction (*row → listed*, or *listed → row* for *Satisfied by*), and how targets are found: requirement id, key, name, qualified name or element id (*auto* tries them in that order) |
| **Part of** | the listed blocks each get a part property typed by the row's block (plus a composition) |
| **(ignore)** | |

Headers are mapped by name when the dialog opens (`Description` → documentation, `Qty` → multiplicity, `Satisfied by` →
an incoming satisfy, `Part of`, `Level` …); change anything in the table.

### Hierarchy

Rows nest by **indentation** of the name column (Excel's indent, or leading spaces / tabs), by a **Level** column, or
by a **Parent** column. **Hierarchy means** either *containment* (the parent row owns the child: requirements, packages,
nested blocks, states) or *part of* (the parent block gets a part property typed by the child: bills of materials).
A row that is its own ancestor is an error.

### Presets

| Built-in preset | Kind | Columns |
| --- | --- | --- |
| **Requirements** | Requirement, keyed by requirement id | Id, Name, Text, Priority, Status, Owner, Verification Method, Risk, Rationale, Documentation, Parent Id, `Attr: …`, Satisfied By, Verified By — the same columns as **Export Excel** on the Requirements tab |
| **Parts list (BOM)** | Block, keyed by `tag:Part Number`, levels mean *part of* | Level, Part Number, Name, Description, Qty, Mass, Material, Supplier |
| **Interfaces / signals** | by column, keyed by qualified name | Qualified Name, Kind, Type, Direction, Multiplicity, Stereotypes, Documentation |
| **Value properties** | Value property, keyed by owner + name | Block, Name, Type, Value, Unit, Multiplicity, Documentation |

**Save to project** stores the current mapping as a preset in the project (it is part of the model: it is versioned,
merged and undoable). **Export JSON** downloads it, **Import JSON…** loads one, so a team can share mappings.

## 3. Matching and review

**Match rows by** decides when a row updates an existing element instead of creating one:

- **External id** — the key column (part number, requirement id, ticket number …) is **stored in a field** of the element
  (`extId` by default, `reqId` for requirements, `tag:Part Number` for the parts list). The next import updates rather
  than duplicates. On the first import into an existing model, a row whose key is not known yet still matches an element
  of the same kind and unique name that has no key, and the key is stored.
- **Meridian element id** — for sheets exported from Meridian with an *Id* column.
- **Qualified name** — `Structure::Vehicle::mass`; without a key column the owner path plus the name is used.
- **Name** — unique names within the target kind.

**Check changes** shows the review:

- **new** and **change** rows (with every field, old → new) are ticked;
- elements of the target kind that the sheet no longer lists are offered for **delete**, unticked;
- **Accept all** / **Accept none**, or tick rows one by one;
- **row errors** — an unknown kind or type, a bad multiplicity or level, a circular hierarchy, an owner or relationship
  target that is not found or is ambiguous, a relationship the kinds do not allow — are listed with the sheet row and
  column; those rows are skipped. **Export errors (CSV)** downloads the list to fix the sheet. Rows that depend on an
  errored row (its children, or rows linking to it) are skipped too.

New elements go into the package chosen under **New elements go into** (the selected package by default) unless an
owner, parent or qualified name says otherwise.

## Export and the round trip

**Export with this mapping** (on the Mapping step) writes `.xlsx` or `.csv` with exactly the mapped columns, for:

- every element of the target kind in the chosen package,
- the current **selection** (diagram multi-selection, or the selected element), or
- a **query** such as `kind:Block where name contains "Engine"` ([Tables and queries](tables-queries.md)).

Hierarchies are written the way the mapping reads them (levels, parent keys or indented names); a part used in two
assemblies appears under each. References are written so that they are found again (requirement id, key, a unique name,
otherwise the qualified name). So **export → edit in Excel → import** reports exactly what was edited — nothing for an
untouched sheet. CSV cells that a spreadsheet would run as a formula (starting with `=`, `+`, `-`, `@`) are written with a
leading apostrophe, which the import removes again.

## Limits and safety

- Up to **100,000 rows** per sheet, 1,000 columns, **32,767 characters** per cell (longer cells are cut, with a warning)
  and **50 MB** per file.
- Workbooks are read part by part with the same limits as other archives: a part that would unpack to more than 64 MB
  is refused ("zip bomb" protection); DTDs and external entities are refused; formulas are never evaluated and external
  links never followed.

## Coming from Cameo or Enterprise Architect

| Cameo / EA | Meridian |
| --- | --- |
| Cameo **File ▸ Import From ▸ Excel/CSV File** (Excel Import wizard), Excel/CSV Sync | **Import ▸ Spreadsheet (Excel / CSV)…** with a column mapping, presets and a reviewed sync |
| Cameo mapping of columns to element properties and tagged values, "create new element if not found" | the column table (**Maps to**), **Create missing types**, external-id matching |
| EA **Publish ▸ CSV Import/Export specifications** | mapping **presets**, saved per project and exportable as JSON |
| EA CSV export of a package or search results | **Export with this mapping** for a package, the selection or a query |
| EA *Preserve hierarchy* (`CSV_KEY` / `CSV_PARENT_KEY`) | **Parent** column (or **Level** / indentation) with the key column |
