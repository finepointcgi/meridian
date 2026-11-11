# Database modelling

Meridian models relational databases next to your system model: databases and schemas, tables and views, columns,
keys, indexes and constraints, sequences and enum types, and **subject areas** that group tables across schemas.
You can import an existing schema (from a script, a SQL Server `.dacpac` or a live database), keep it in step with the
database, design changes on crow's-foot diagrams, and generate DDL and migration scripts.

> Meridian never runs DDL against a database. It reads catalogs (read-only) and writes scripts that you review and run
> with your own tools.

## Import a schema

Project menu ▸ **Import ▸ Database schema (DDL / .dacpac)…** (or ⌘K "Import database schema"). Choose a file or paste
a script; Meridian detects the dialect (or pick it) and shows what it found before anything changes.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="../images/database-import-dark.png">
  <img src="../images/database-import-light.png" alt="The Import database schema dialog with a pasted PostgreSQL script and its preview: 2 schemas, 4 tables, 1 view, 3 foreign keys">
</picture>

| Source | Notes |
| --- | --- |
| PostgreSQL DDL (`pg_dump --schema-only`) | schemas, tables, views, materialized views, enum and domain types, sequences, indexes (partial, expression, GIN…), functions, triggers, `COMMENT ON` |
| SQL Server (T-SQL) | `GO` batches, `[brackets]`, IDENTITY and computed columns, default constraints, `MS_Description` comments |
| MySQL / MariaDB | backticks, `AUTO_INCREMENT`, `KEY` / `UNIQUE KEY`, inline `COMMENT`s, `DELIMITER` blocks |
| SQLite | inline keys, `AUTOINCREMENT`, typeless columns, `WITHOUT ROWID` |
| Oracle | `VARCHAR2` / `NUMBER`, identity columns, `/`-terminated PL/SQL, `ALTER SESSION SET CURRENT_SCHEMA` |
| SQL Server `.dacpac` | everything in its `model.xml` (extract one with `sqlpackage /Action:Extract`) |
| Mermaid `erDiagram` | entities, attributes with `PK` / `FK` / `UK`, relationships become foreign keys |

Statements Meridian does not model (grants, policies, extensions…) are skipped and listed as warnings. The import
creates a database with its schemas and **a diagram per schema**, as one undo step. **Import into this project…**
with a `.sql`, `.ddl` or `.dacpac` file imports directly, and the desktop app opens such files as new projects.

### From a live database

Project menu ▸ **Connect to a database…** opens Integrations ▸ **Databases**. Add a connection (PostgreSQL or
MySQL / MariaDB; SQLite files in the desktop app), test it, then **Import**. The connection string is stored encrypted
and never shown again. **Use a read-only account**: Meridian only reads the catalog, in read-only transactions.

On a team server only server administrators can add connections, and hosts on private networks are refused unless
an administrator allows them (`Meridian:Integrations:AllowPrivateNetworks`). SQL Server and Oracle have no live
connection — import their DDL (or a `.dacpac`) instead.

## Database diagrams

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="../images/database-diagram-dark.png">
  <img src="../images/database-diagram-light.png" alt="A database diagram with the orders, order_line and big_orders view inside the Ordering subject area frame, and the inspector">
</picture>

Tables list their columns with a **gold key** on primary-key columns, a **blue key** on foreign-key columns, `UQ` on
unique ones and `*` after mandatory columns; the type is on the right. Views have a dashed outline. Foreign keys use
crow's-foot ends — many at the referencing table, one (bar) or zero-or-one (circle, when the column is nullable) at
the referenced table — and run from column to column when the tables sit side by side.

* Ribbon **Database ▸ Subject areas ▸ Columns**: show all columns, **keys only**, or none.
* **New table** adds a table to the selected schema and the open diagram. The shapes panel also has views,
  materialized views and logical **entities** (a table *realizes* the entity it implements).
* Draw a **Foreign Key** from a **column row** to another table: that column references the other table's primary
  key (drop it on a column row to pick the referenced column). Without a column, Meridian adds `<table>_id` with the
  key's type.
* **Auto-diagram** (or the database's context menu ▸ *Diagram each schema and subject area*) lays out one diagram per
  schema and per subject area.

## Edit tables

Select a table: the inspector's **Columns** list edits names and types in place, the key icon makes a column part of
the primary key in one click, `*` / `?` toggles NOT NULL, and **+ Column** adds one (type a name and press Enter).
Add indexes, unique or check constraints and triggers below it. Columns have identity, generated expression and
collation fields; foreign keys their *on delete* / *on update* actions; the database its dialect and naming
convention.

Renaming a column updates the foreign keys, indexes and constraints that use it. **Validate** (F7) checks tables
without a primary key, foreign keys whose types don't match, duplicate names, reserved words of the database's
dialect, the naming convention (snake_case, PascalCase or camelCase) and tables in no subject area.

Tables and columns take part in traceability like any element: link them to requirements with «satisfy» or «trace»,
and allocate blocks or data items to them.

## Subject areas

A subject area groups tables by topic, independently of schemas — a table can be in several. Select tables and use
ribbon **Database ▸ Add to subject area**, or **Subject areas ▸ Subject areas…** to create, rename, colour and fill them.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="../images/database-groups-dark.png">
  <img src="../images/database-groups-light.png" alt="The Subject areas dialog with the Customers and Ordering areas, colours and the tables of Ordering">
</picture>

Areas appear as coloured frames on database diagrams. **Subject areas ▸ Show on this diagram** filters a diagram to one
area, the model explorer has a chip per area, and DDL and the data dictionary can be generated for an area. Areas
are packages, so **package permissions** set on an area apply to its tables.

## Refresh from the database

When the database changes, **Refresh…** (ribbon, or the database's context menu) reads it again through its
connection — or asks for the new script — and shows what is different: added, changed and removed tables, columns,
keys, indexes and more. Untick anything you want to keep as it is.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="../images/database-review-dark.png">
  <img src="../images/database-review-light.png" alt="The refresh review: a changed column type, an added column, a removed and an added index, each with a check box">
</picture>

Applying is one undo step, and the diagrams highlight what changed (as for code changes). Elements are matched by
the names they have in the database, so tables renamed in Meridian still match. Documentation you wrote, subject
areas and diagram layout are never overwritten.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="../images/database-refreshed-dark.png">
  <img src="../images/database-refreshed-light.png" alt="The sales diagram after the refresh with the changed orders table highlighted and the legend Added 2, Changed 2, Removed 1">
</picture>

## Generate DDL and migration scripts

**Generate DDL…** writes the CREATE script for a database, schema, subject area or the selected tables, for
PostgreSQL, SQL Server, MySQL, SQLite or Oracle: schemas and types first, tables in dependency order, indexes, then
foreign keys, views and comments. Copy it or download a `.sql` file.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="../images/database-ddl-dark.png">
  <img src="../images/database-ddl-light.png" alt="The Generate DDL dialog with the PostgreSQL script of the shop database">
</picture>

**Migration script…** writes the ALTER statements that turn what a database has now into the model: choose an empty
database, a committed version, a baseline, another branch, a live connection or a pasted script as the starting
point. Steps that can lose data or fail on existing rows — drops, type changes, `SET NOT NULL` — are marked
`-- WARNING:` and listed above the script.

## Data dictionary

⌘K **Data dictionary (CSV)** or **(Excel)** exports every column with its table, type, keys, references, default,
comment and subject areas. For a formatted document, **Generate ▸ Report** offers the **Data Dictionary** template
(PDF, Word or HTML) with the tables, their columns and the database diagrams.

See also: [Import and export](import-export.md), [Integrations](integrations.md), and `docs/DATABASE.md` in the
repository for the full reference.
