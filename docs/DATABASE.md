# Database modelling

Meridian models relational databases next to the system model: databases and schemas, tables, views and
materialized views, columns, keys, indexes, constraints, sequences, enum and domain types, routines (signatures) and
triggers; subject areas that group tables across schemas; crow's-foot database diagrams; import from DDL scripts,
SQL Server `.dacpac` files, Mermaid `erDiagram`s, Enterprise Architect / XMI data models and live databases; refresh
with a review; DDL and migration scripts; validation; and a data dictionary.

**Meridian never executes DDL against a database.** It reads catalogs (in read-only transactions) and writes scripts
for you to review and run with your own tools.

Code: `src/Meridian.Core/DataModeling` (metamodel helpers, catalog, diff, refresh, validation, notation),
`src/Meridian.Serialization/Database` (DDL parser and writer, migration writer, `.dacpac`, data dictionary),
`src/Meridian.Serialization/Mermaid/MermaidDatabase.cs`, `src/Meridian.Serialization/SvgRenderer.Database.cs`,
`src/Meridian.Data/Databases/LiveSchemaReader.cs` (live catalogs), `src/Meridian.Web/Integrations/Databases`
(connections), `src/Meridian.Web/Components/Workbench/Database` and `Workbench.Database.cs` (UI).

## Metamodel

Everything is stored in the field bags of existing kinds, plus five kinds appended to `ElementKind` (existing ordinals
and project files are unchanged). Field keys are in `DbFields`.

| Concept | Stored as |
| --- | --- |
| Database / catalog | `Package` with `db.role = database`; `db.dialect` (postgresql, sqlserver, mysql, sqlite, oracle), `db.naming` (snake_case, PascalCase, camelCase), `db.source` |
| Schema | `Package` with `db.role = schema`, inside its database |
| Subject area (group) | `Package` with `db.role = subjectArea` and a `color`; tables list the ids of their areas in `db.groups` (a table can be in several) |
| Table / view / materialized view | `Table` with subtype `""` / `view` / `materializedView`; `db.definition` (the view's SELECT), `db.pkName` |
| Column | `Column`: `sqlType` (the full type, e.g. `numeric(10,2)`; `DbTypes.Parse` splits base type, length, precision and scale), `pk`, `nullable`, `unique`, `defaultValue`, `db.identity` (always / byDefault / autoIncrement), `db.generated`, `db.collation`; the comment is the documentation |
| Primary key | the `pk` flag of its columns, in column order; name in the table's `db.pkName` |
| Foreign key | `ForeignKey` relationship from the referencing to the referenced table: `fkColumns`, `refColumns`, `db.onDelete`, `db.onUpdate` |
| Unique / check constraint | `DbConstraint` owned by the table, subtype `unique` / `check`, `db.columns`, `db.expression` |
| Index | `DbIndex` owned by the table: `db.columns` (expressions allowed), `unique`, `db.method`, `db.expression` (partial index WHERE) |
| Sequence | `Sequence` in a schema: `sqlType`, `db.start`, `db.increment`, `db.min`, `db.max`, `db.cycle` |
| Enum type | `Enumeration` with literals, in a schema |
| Domain | `DataType` with `db.baseType`, `db.expression` (CHECK) |
| Stored procedure / function | `Routine`, subtype `procedure` / `function`: `signature`, `db.returns`, `db.language` (signature only, no body) |
| Trigger | `Trigger` owned by its table: `db.timing`, `db.events`, `db.level`, `db.definition` (the action) |
| Logical entity | a `Class` with the «entity» stereotype; a table **realizes** it (`Realization` table → class) or traces to it |
| Match key | `db.qualifiedName` = `database.schema.table[.column]` as the source named it; `db.comment` = the source's last comment |

Traceability works as for any element: «satisfy», «trace» and «refine» from tables or columns to requirements,
«allocate» from blocks and data items to tables (inspector *Traceability*, or connectors on a database diagram).

Renaming a column renames it in the foreign keys, indexes and constraints that list it, in the same undo step
(`DbRename`, hooked into `ModelRepository.Set`).

## Diagrams

The database diagram type (`DiagramType.Database`, "Database Diagram (ER)") uses crow's-foot notation:

* A table lists its columns with a gold key for primary-key columns, a blue key for foreign-key columns, `UQ` for
  unique ones, `*` after mandatory (NOT NULL) columns and the type on the right. Views have a dashed outline and a
  «view» / «materialized view» keyword.
* **Columns: all / keys only / none** (ribbon *Groups* menu, ⌘K) is a style of each table shape (`db.showColumns`);
  shapes resize to fit.
* Foreign keys end in a crow's foot at the referencing table and a bar (mandatory) or a circle (nullable referencing
  columns) at the referenced one; a unique referencing column makes it one-to-one. They run from the row of the
  first referencing column to the row of the referenced column when the tables sit side by side; otherwise the
  diagram's routing applies.
* Subject areas are drawn as tinted, labelled frames around their tables; tables also carry their areas' colours on
  the top edge. **Show only one area** (`db.groupFilter` on the diagram) hides the other tables and their lines.
* Palette: Table, View, Materialized View, Entity (logical), Requirement, Block, Comment; Foreign Key, Realization,
  Trace, Satisfy, Allocate. Drawing a foreign key **from a column row to another table** uses that column (dropping on
  a column row picks the referenced column); otherwise it references the primary key and creates
  `<table>_<key>` with the key's type when the table has no such column.
* **Auto-diagram** plans a database diagram per schema (or package holding tables) and per subject area; imports
  create one per schema.

## Import

| Source | Where | Reads |
| --- | --- | --- |
| DDL scripts | Project ▸ **Database schema (DDL / .dacpac)…** (preview, dialect detection), **Import into this project…**, the desktop app's **Open** | PostgreSQL, SQL Server (GO batches, `[brackets]`, extended-property comments), MySQL / MariaDB (backticks, `DELIMITER`, inline `COMMENT`s), SQLite, Oracle (`/` blocks, `ALTER SESSION SET CURRENT_SCHEMA`) |
| SQL Server `.dacpac` | same | `model.xml`: schemas, tables, columns, keys, defaults, checks, indexes, views, sequences, routines, DML triggers, `MS_Description` |
| Mermaid `erDiagram` | same (`.mmd`, `.md`) | entities, attributes with `PK` / `FK` / `UK` and comments, relationships as foreign keys from the "many" side |
| Enterprise Architect `.qea` | Open a file as a new project | «table» classes, columns with length / precision / NOT NULL, «PK» / «FK» / «unique» / «index» operations, «FK» connectors |
| XMI | Open a file as a new project | Meridian's own data-model fields round-trip through the XMI extension |
| Live database | **Connect to a database…** (Integrations ▸ Databases) | see below |

The DDL parser (`DdlParser`) is tolerant: it understands `CREATE SCHEMA/DATABASE/TABLE/VIEW/MATERIALIZED VIEW/INDEX/
SEQUENCE/TYPE/DOMAIN/FUNCTION/PROCEDURE/TRIGGER`, `ALTER TABLE` (add / alter / drop / rename column, add / drop
constraint, T-SQL default constraints), `COMMENT ON`, quoted identifiers, dollar-quoted bodies, column and table
constraints in any order, identity / auto-increment / generated columns, collations and MySQL keys. Data statements
(INSERT, SET, GRANT…) are skipped silently; any other statement is skipped with a warning naming its line.
Unqualified names go to the dialect's default schema (public, dbo, main; for MySQL and Oracle the current database /
schema). An import creates the database, its schemas and a diagram per schema in **one undo step**.

### Live connections

Integrations ▸ **Databases** holds connections: provider, connection string, schemas to read and a timeout.

* **PostgreSQL** (Npgsql) reads `pg_catalog`: schemas, tables, views (with definitions), materialized views, columns
  (serial and identity columns, generated columns, collations), primary / unique / check / foreign keys with their
  actions, indexes (method, expressions, partial), triggers, sequences (not those owned by serial or identity
  columns), enum and domain types, functions and procedures (excluding extension members), and comments.
* **MySQL / MariaDB** (MySqlConnector, MIT) reads `information_schema`.
* **SQLite** (Microsoft.Data.Sqlite) parses the DDL stored in `sqlite_master`; desktop / local mode only.
* **SQL Server and Oracle** have no live connection: their drivers are not shipped (Microsoft.Data.SqlClient brings
  Azure.Identity, MSAL and native SNI libraries; Oracle's driver is not MIT/Apache licensed). Import their DDL, or a
  `.dacpac` for SQL Server (`sqlpackage /Action:Extract`).

Security:

* The connection string is the integration's secret: encrypted with ASP.NET Data Protection, decrypted only to
  connect, never shown again or logged; error messages have passwords masked.
* **Use a read-only account.** Meridian only runs catalog queries in a read-only transaction (PostgreSQL
  `SET TRANSACTION READ ONLY`, MySQL `SET SESSION TRANSACTION READ ONLY`, SQLite `Mode=ReadOnly`), but a read-only role
  is the guarantee. PostgreSQL: a role with `CONNECT` on the database and `USAGE` on the schemas (catalog views are
  readable by default). MySQL: `SELECT` and `SHOW VIEW` on the schemas.
* On a **team server** only server administrators add, change or delete connections (project members use them to
  refresh). The host is resolved once and checked like webhook URLs: link-local and metadata addresses are always
  refused; loopback and private networks unless `Meridian:Integrations:AllowPrivateNetworks=true`. The checked address
  is pinned for the connection (with `SSL Mode=VerifyFull` the host name is kept so the certificate can be verified).
  SQLite files and Unix sockets are refused (they would be paths on the server).
* The **desktop app's local mode** connects without these restrictions.
* Timeouts: connect and command timeouts follow the connection's timeout (5–600 s); the whole read is cancelled after
  it, and the UI's own limit is 10 minutes.

## Refresh / re-import

**Refresh…** (ribbon, database context menu, inspector) reads the database's connection again — or asks for a
new script when the database came from one — and compares it with the model by the names the source had
(`db.qualifiedName`), so tables and columns renamed in Meridian still match. The review lists added, changed and
removed schemas, tables, views, columns, primary keys, foreign keys, indexes, constraints, triggers, sequences,
types and routines; each can be ticked off. The diagrams highlight what will change while the review is open (the
code-sync highlight), and what changed after it is applied, with removed tables as ghosts. Applying is **one undo
step**.

What a refresh never overwrites: documentation written in Meridian (a comment from the source only replaces
documentation that is empty or still equal to the previous import's comment), subject areas, and diagram layout.
Removing a table or column deletes it from the model (and its shapes); untick it to keep it.

## Managing schemas

* **New table** (ribbon), **Add column** (inspector, canvas menu, tree menu), one-click **primary key** toggle and
  NOT NULL toggle per column in the inspector's *Columns* list, indexes, constraints and triggers from the inspector.
* **Subject areas**: ribbon **Add to group** for the selected tables (or a new area), **Groups ▸ Subject areas…** to
  create, rename, recolour, fill and delete them, filter the active diagram, diagram an area; the model explorer has a
  chip per area and a *Data* chip. Areas are packages: package permissions set on an area apply to its tables
  (nearest rule wins, the most restrictive of several areas; an area never hides a table — "no access" makes its
  tables read-only).
* **Validation** (F7): DB-001 table without a primary key, DB-002 foreign key type or column-count mismatch, DB-003
  duplicate names (columns, case-only table names, index / constraint names), DB-004 reserved words of the
  database's dialect, DB-005 naming convention (`db.naming` on the database or the model root), DB-006 table in no
  subject area (when areas exist), DB-007 foreign keys naming missing columns, DB-008 indexes / constraints naming
  missing columns.

## Generating

* **Generate DDL…**: for a database, schema, subject area, the selected tables or the whole model, in PostgreSQL,
  SQL Server, MySQL, SQLite or Oracle. Schemas, types and sequences first, tables in dependency order, indexes, then
  foreign keys (inline for SQLite), views and comments (COMMENT ON, `sp_addextendedproperty`, MySQL `COMMENT`).
  Types are converted per dialect (serial → IDENTITY / AUTO_INCREMENT, boolean → bit, text → nvarchar(max) …); enum
  types become `ENUM(…)` in MySQL and a CHECK elsewhere. Routines and triggers are listed as comments.
* **Migration script…**: ALTER statements from a baseline to the model. The baseline is an empty database, a
  committed version, a baseline, another branch, a live database connection or a pasted DDL script. Foreign keys and
  changed indexes / constraints are dropped first and added last; new tables are created in dependency order;
  columns are added, altered (type, NULL / NOT NULL, default) and dropped. Steps that can lose data or fail on existing
  rows (drops, type changes, SET NOT NULL, NOT NULL columns without a default) carry a `-- WARNING:` comment and are
  listed above the script. SQLite limitations are spelled out as comments.
* **Data dictionary**: ⌘K *Data dictionary (CSV / Excel)*, the database context menu, and the **Data Dictionary**
  report template (PDF / Word / HTML via Generate ▸ Report) with every table's columns, types, keys, defaults,
  comments, subject areas and the database diagrams. `ModelValues` offers `subjectAreas` and `keys` as table columns
  for custom tables and templates.

## Tests

* Core: `tests/Meridian.Tests/DatabaseModelingTests.cs` (parsing per dialect with realistic multi-schema scripts,
  round trips parse → model → DDL → parse for all five dialects, migration scripts, validation, subject areas and
  permissions, refresh plans and selective apply, data dictionary, rendering and routing, EA and XMI, `.dacpac`,
  Mermaid) and `LiveDatabaseImportTests.cs` (SQLite file; PostgreSQL in a throwaway Testcontainers container on a
  random port).
* Web: `tests/Meridian.Web.Tests/DatabaseWebTests.cs` (import, review, groups, add-to-group and DDL dialogs, the
  inspector section, connection permissions, the outbound guard and encryption).
* E2E: `tests/Meridian.E2E/DatabaseTests.cs`; screenshots: `DatabaseDocsScreenshotTests`
  (`MERIDIAN_DOCS_SCREENSHOTS=1`).

## Limits

* Routine and trigger bodies are not modelled (signatures and timing only) and are not generated.
* Composite and range types, partitions, table inheritance, exclusion constraints, policies and grants are skipped
  with a warning.
* The MySQL live reader is not exercised against a real server in the test suite (the SQLite and PostgreSQL readers
  are).
* View column types from DDL are unknown until a live refresh fills them in.
* Migration scripts do not reorder columns or rename (a rename shows as drop + add), and cannot drop an unnamed
  foreign key or a SQL Server default constraint by name.
