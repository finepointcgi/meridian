# Database modelling

Meridian models relational databases next to the system model: databases and schemas, tables, views and
materialized views, columns, keys, indexes, constraints, sequences, enum and domain types, routines and triggers with
their bodies; subject areas that group tables across schemas; crow's-foot database diagrams; import from DDL scripts,
SQL Server `.dacpac` files, Mermaid `erDiagram`s, Enterprise Architect / XMI data models and live databases; refresh
with a review; DDL and migration scripts; validation; and a data dictionary.

**Meridian never executes DDL against a database.** It reads catalogs (in read-only transactions) and writes scripts
for you to review and run with your own tools.

Code: `src/Meridian.Core/DataModeling` (metamodel helpers, catalog, diff, refresh, validation, notation),
`src/Meridian.Serialization/Database` (DDL parser and writer, migration writer, `.dacpac`, data dictionary),
`src/Meridian.Serialization/Mermaid/MermaidDatabase.cs`, `src/Meridian.Serialization/SvgRenderer.Database.cs`,
`src/Meridian.Data/Databases` (live catalogs: `LiveSchemaReader.cs`, `SqlServerSchema.cs`, `OracleSchema.cs`),
`src/Meridian.Serialization/Database/DbModuleSql.cs` (routine and trigger statements per dialect), `src/Meridian.Web/Integrations/Databases`
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
| Column | `Column`: `sqlType` (the full type, e.g. `numeric(10,2)`; `DbTypes.Parse` splits base type, length, precision and scale), `pk`, `nullable`, `unique`, `defaultValue`, `db.defaultName` (SQL Server: the DEFAULT constraint's name), `db.identity` (always / byDefault / autoIncrement), `db.generated`, `db.collation`; the comment is the documentation |
| Primary key | the `pk` flag of its columns, in column order; name in the table's `db.pkName` |
| Foreign key | `ForeignKey` relationship from the referencing to the referenced table: `fkColumns`, `refColumns`, `db.onDelete`, `db.onUpdate` |
| Unique / check constraint | `DbConstraint` owned by the table, subtype `unique` / `check`, `db.columns`, `db.expression` |
| Index | `DbIndex` owned by the table: `db.columns` (expressions allowed), `unique`, `db.method`, `db.expression` (partial index WHERE) |
| Sequence | `Sequence` in a schema: `sqlType`, `db.start`, `db.increment`, `db.min`, `db.max`, `db.cycle` |
| Enum type | `Enumeration` with literals, in a schema |
| Domain | `DataType` with `db.baseType`, `db.expression` (CHECK) |
| Stored procedure / function | `Routine`, subtype `procedure` / `function`: `signature`, `db.returns`, `db.language`, `db.options` (characteristics such as `STABLE SECURITY DEFINER`, `DETERMINISTIC`, `WITH SCHEMABINDING`), `db.definition` (the body, verbatim — see below) |
| Trigger | `Trigger` owned by its table: `db.timing`, `db.events`, `db.level`, `db.definition` (the action, verbatim) |
| Logical entity | a `Class` with the «entity» stereotype; a table **realizes** it (`Realization` table → class) or traces to it |
| Match key | `db.qualifiedName` = `database.schema.table[.column]` as the source named it; `db.comment` = the source's last comment |

Traceability works as for any element: «satisfy», «trace» and «refine» from tables or columns to requirements,
«allocate» from blocks and data items to tables (inspector *Traceability*, or connectors on a database diagram).

Renaming a column renames it in the foreign keys, indexes and constraints that list it, in the same undo step
(`DbRename`, hooked into `ModelRepository.Set`).

### Routine and trigger bodies

Bodies are kept as the source wrote them (comments and layout inside included), not translated between dialects:

| Dialect | Routine body (`db.definition`) | Trigger action (`db.definition`) |
| --- | --- | --- |
| PostgreSQL | the text inside `$$ … $$` / `$tag$ … $tag$` (or `'…'`); a SQL-standard `BEGIN ATOMIC … END` / `RETURN …` body as written | `[WHEN (…)] EXECUTE FUNCTION f(…)` |
| SQL Server | everything after `AS` up to the batch's `GO` | everything after `AS` |
| MySQL / MariaDB | from the first statement after the characteristics (usually `BEGIN … END`), `DELIMITER` blocks or a single statement | the statement after `FOR EACH ROW` |
| Oracle | the declarations and block after `IS` / `AS` up to the `/` line (`… END name;`) | `[WHEN (…)] [DECLARE …] BEGIN … END;` |
| SQLite | (no routines) | `[WHEN …] BEGIN … END` |

The inspector shows the body in a monospace editor (Meridian has no SQL highlighter; the field stays editable).
Sources that know only a signature (a `.dacpac` without the body, an encrypted SQL Server module, a MySQL account without
the right to see `ROUTINE_DEFINITION`) leave an existing body alone on refresh.

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
constraints in any order, identity / auto-increment / generated columns, collations and MySQL keys, routine and
trigger bodies (`GO` batches, `DELIMITER`, `/` blocks, dollar quoting; T-SQL default constraint names). Data statements
(INSERT, SET, GRANT…) are skipped silently; any other statement is skipped with a warning naming its line.
Unqualified names go to the dialect's default schema (public, dbo, main; for MySQL and Oracle the current database /
schema). An import creates the database, its schemas and a diagram per schema in **one undo step**.

### Live connections

Integrations ▸ **Databases** holds connections: provider, connection string, schemas to read and a timeout.

* **PostgreSQL** (Npgsql) reads `pg_catalog`: schemas, tables, views (with definitions), materialized views, columns
  (serial and identity columns, generated columns, collations), primary / unique / check / foreign keys with their
  actions, indexes (method, expressions, partial), triggers (`pg_get_triggerdef`), sequences (not those owned by serial
  or identity columns), enum and domain types, functions and procedures with their bodies (`pg_get_functiondef`,
  excluding extension members), and comments.
* **SQL Server / Azure SQL** (Microsoft.Data.SqlClient) reads the `sys.*` catalog views of the connection's database:
  user schemas, tables, views (definition from `sys.sql_modules`), columns (types with sizes, identity, computed,
  defaults with their constraint names, collations that differ from the database's, alias types), primary / unique /
  check / foreign keys, indexes (clustered, filtered), DML triggers, sequences, alias types as domains, procedures and
  functions (module text read by the DDL parser; parameters from `sys.parameters` when the text is encrypted), and
  `MS_Description` comments.
* **MySQL / MariaDB** (MySqlConnector, MIT) reads `information_schema`, with routine bodies from
  `ROUTINES.ROUTINE_DEFINITION` (characteristics from its columns) and trigger bodies from `TRIGGERS.ACTION_STATEMENT`.
* **Oracle** (Oracle.ManagedDataAccess.Core) reads the `ALL_*` dictionary views for the connecting user's schema (or the
  schemas listed in the connection): tables (not materialized-view containers, nested tables or the recycle bin), views,
  materialized views, columns (identity, virtual columns, defaults), primary / unique / check / foreign keys (the NOT NULL
  checks Oracle names itself become the columns' NOT NULL), indexes (function-based ones with their expressions),
  sequences (not identity sequences), procedures, functions and triggers (source from `ALL_SOURCE`, read by the DDL
  parser) and comments. Packages and object types are listed as warnings.
* **SQLite** (Microsoft.Data.Sqlite) parses the DDL stored in `sqlite_master`; desktop / local mode only.

#### Drivers and licenses

| Driver | Version | License | Added to the published output |
| --- | --- | --- | --- |
| Microsoft.Data.SqlClient | 7.1.1 | MIT | see the size table below |
| Oracle.ManagedDataAccess.Core | 23.26.400 | Oracle Free Use Terms and Conditions (not OSI) | see the size table below |

* **SQL Server.** Microsoft.Data.SqlClient was rejected before because it pulled in Azure.Identity, MSAL and native
  libraries. Since 7.0 the Azure authentication code lives in a separate package (`Microsoft.Data.SqlClient.Extensions.Azure`)
  that Meridian does not reference, so Azure.Identity and MSAL are gone from the tree. What remains is MIT licensed:
  the driver, `Microsoft.Data.SqlClient.Extensions.Abstractions` / `Internal.Logging`, `Microsoft.IdentityModel.*`
  (already shipped with the server's OpenID Connect sign-in), `Microsoft.SqlServer.Server`,
  `System.Configuration.ConfigurationManager` and `System.Security.Cryptography.Pkcs` (pinned to 10.0.14: the version
  SqlClient asks for has security advisories). The one native piece, `Microsoft.Data.SqlClient.SNI.runtime` (Windows only,
  Microsoft license terms that require indemnification and end-user terms), is **excluded** in `Meridian.Web.csproj` and
  `Meridian.Desktop.csproj` (`ExcludeAssets="all"`), and SqlClient uses its managed networking on every platform
  (`Switch.Microsoft.Data.SqlClient.UseManagedNetworkingOnWindows`, set in the runtime config and before connecting).
  Consequence: Windows integrated authentication over named pipes and shared memory is not available; TCP with SQL
  logins, Entra ID tokens in the connection string are not affected (Entra ID interactive / managed-identity
  authentication would need the Azure extension package, which is not shipped).
* **Oracle.** `Oracle.ManagedDataAccess.Core` is distributed under the *Oracle Free Use Terms and Conditions*: a
  non-exclusive license to use the unmodified driver and to **redistribute it unmodified** in applications (also
  commercial ones that add substantial value), provided the license text ships with every distribution, no extra fee is
  charged for the driver itself, Oracle's notices are kept, export rules are followed and the driver is not reverse
  engineered. Meridian meets these: the driver is used as published, it is free of charge, and the license text is copied
  next to the binaries (`licenses/Oracle.ManagedDataAccess.LICENSE.txt` in the desktop, server and Docker outputs, from
  `Meridian.Data.csproj`). It is not an OSI license, so anyone repackaging Meridian under terms that forbid such
  conditions should drop the reference (Oracle import then falls back to DDL scripts). Its extra dependencies
  (`System.DirectoryServices.Protocols`, `System.Diagnostics.PerformanceCounter`) are MIT.

Sizes, measured with `dotnet publish -c Release` (server: framework-dependent like the Dockerfile; desktop:
self-contained like `build/package.sh`), branch against `main`:

| Output | Before | After | Added | of which Oracle | of which SqlClient |
| --- | --- | --- | --- | --- | --- |
| Server (framework-dependent, all runtimes) | 168.6 MB | 181.3 MB | +12.8 MB (31 files) | 6.4 MB | 5.9 MB (unix and win builds of the driver, 13 resource languages) |
| Desktop macOS arm64 (self-contained) | 190.0 MB | 199.6 MB | +9.5 MB (23 files) | 5.9 MB | 3.5 MB (1.9 MB of it resource languages) |
| Desktop Windows x64 (self-contained) | 189.6 MB | 199.2 MB | +9.6 MB (23 files) | 5.9 MB | 3.5 MB, no native SNI |

The shared rest (`System.Configuration.ConfigurationManager`, `ProtectedData`, `Pkcs`) is about 0.5 MB.

Security:

* The connection string is the integration's secret: encrypted with ASP.NET Data Protection, decrypted only to
  connect, never shown again or logged; error messages have passwords masked.
* **Use a read-only account.** Meridian only runs catalog queries in a read-only transaction (PostgreSQL
  `SET TRANSACTION READ ONLY`, MySQL `SET SESSION TRANSACTION READ ONLY`, Oracle `SET TRANSACTION READ ONLY`, SQLite
  `Mode=ReadOnly`), but a read-only role is the guarantee. SQL Server has no read-only transaction: Meridian runs only
  SELECTs on `sys.*` views, inside a transaction it rolls back, with `ApplicationIntent=ReadOnly` and a lock timeout.
  PostgreSQL: a role with `CONNECT` on the database and `USAGE` on the schemas (catalog views are readable by default).
  SQL Server: a user in the database with `VIEW DEFINITION` (module text) and nothing else (`db_datareader` is not
  needed). MySQL: `SELECT` and `SHOW VIEW` on the schemas (routine bodies need `SHOW_ROUTINE` or ownership). Oracle:
  `CREATE SESSION` plus the objects being visible in `ALL_*` (the schema's own user, or `SELECT ANY DICTIONARY` /
  grants on the objects for another schema).
* On a **team server** only server administrators add, change or delete connections (project members use them to
  refresh). The host is resolved once and checked like webhook URLs: link-local and metadata addresses are always
  refused; loopback and private networks unless `Meridian:Integrations:AllowPrivateNetworks=true`. The checked address
  is pinned for the connection (with PostgreSQL `SSL Mode=VerifyFull` and Oracle TCPS the host name is kept so the
  certificate can be verified; SQL Server connects to the address and checks the certificate against the name through
  `HostNameInCertificate`). SQLite files and Unix sockets are refused (they would be paths on the server), and so are
  hosts the guard can't check: SQL Server named pipes, shared memory and LocalDB, Oracle TNS aliases (resolved from
  `tnsnames.ora` or a directory), and several hosts (multi-host strings, a SQL Server `Failover Partner`, an Oracle
  descriptor with several addresses).
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
  types become `ENUM(…)` in MySQL and a CHECK elsewhere. Routines, then triggers, follow with their bodies
  (`DbModuleSql`): PostgreSQL `CREATE OR REPLACE FUNCTION … AS $body$ … $body$` (a tag the body doesn't contain),
  SQL Server `CREATE OR ALTER … AS …` in its own `GO` batch, MySQL inside `DELIMITER $$ … $$` when the body has
  semicolons, Oracle `CREATE OR REPLACE … IS …` ended by a `/` line, SQLite triggers. Bodies are only written for the
  dialect they came from; for another dialect (or without a body) they are listed as comments. Trigger forms a dialect
  lacks are commented with the reason (MySQL and SQLite fire on one event, SQL Server has no BEFORE triggers, PostgreSQL
  triggers must call a function).
* **Migration script…**: ALTER statements from a baseline to the model. The baseline is an empty database, a
  committed version, a baseline, another branch, a live database connection or a pasted DDL script. In order:
  1. **Renames** of tables, views and columns (below), so every later step uses the new names.
  2. Foreign keys, changed indexes / constraints / primary keys and removed triggers are dropped.
  3. New schemas, types, sequences and tables (dependency order); columns are added, altered (type, NULL / NOT NULL,
     default) and dropped; tables, views, sequences, types and routines that are gone are dropped.
  4. Primary keys, constraints and indexes are added, then foreign keys.
  5. Routines (new or changed) and triggers (new, changed, or on new tables) with their bodies: PostgreSQL
     `CREATE OR REPLACE` (a changed parameter list or return type drops the old function first) and `DROP TRIGGER IF
     EXISTS` + `CREATE TRIGGER`; SQL Server `CREATE OR ALTER`; MySQL `DROP … IF EXISTS` + `CREATE`; Oracle
     `CREATE OR REPLACE`; SQLite `DROP TRIGGER IF EXISTS` + `CREATE TRIGGER`.

  Steps that can lose data or fail on existing rows (drops, type changes, SET NOT NULL, NOT NULL columns without a
  default) carry a `-- WARNING:` comment and are listed above the script. SQLite limitations are spelled out as comments.

  **Renames.** Model versions keep element ids, so a table or column with the same id and another name is a rename
  (`DbDiff.Compare`, `DbTableDef.Id` / `DbColumnDef.Id`): PostgreSQL / Oracle / SQLite `ALTER TABLE … RENAME TO` /
  `RENAME COLUMN … TO` (views: `ALTER VIEW` / `ALTER MATERIALIZED VIEW`, Oracle `RENAME`; SQLite recreates a renamed
  view), SQL Server `EXEC sp_rename`, MySQL `RENAME TABLE` and `RENAME COLUMN` (MySQL 8.0+ / MariaDB 10.5+). Foreign
  keys, indexes and constraints that only follow a rename are not dropped and re-added. A live database or a script has
  no ids: `DbDiff.SuggestRenames` pairs a table that disappeared with one that appeared in the same schema (mostly the
  same columns, similar name) and, within a table, a column that disappeared with one of the **same type** that
  appeared (scored by name similarity, position and nullability; at least 60 %). The migration dialog lists them as
  **Possible renames** with check boxes, unticked: a ticked one is scripted as a rename instead of drop + add.

  **Generated constraint names.** A foreign key declared without a name (in the model or the before-script) is dropped
  by a script that finds its generated name in the database's catalog by table, referenced table and columns:
  PostgreSQL a `DO` block over `pg_constraint`, SQL Server `sys.foreign_keys` + `sp_executesql`, MySQL
  `information_schema.KEY_COLUMN_USAGE` + a prepared statement, Oracle a PL/SQL block over `ALL_CONSTRAINTS` (no-op when
  nothing matches). SQL Server defaults are dropped by name when it is known (`CONSTRAINT df DEFAULT …` in DDL, or
  `sys.default_constraints` of a live baseline, stored as `db.defaultName`), otherwise looked up the same way — before
  the default changes and before its column is dropped. A primary key without a known name: SQL Server looks it up in
  `sys.key_constraints`, Oracle and MySQL use `DROP PRIMARY KEY`, PostgreSQL its `<table>_pkey` default. With a live
  baseline the names come straight from its catalog.
* **Data dictionary**: ⌘K *Data dictionary (CSV / Excel)*, the database context menu, and the **Data Dictionary**
  report template (PDF / Word / HTML via Generate ▸ Report) with every table's columns, types, keys, defaults,
  comments, subject areas and the database diagrams. `ModelValues` offers `subjectAreas` and `keys` as table columns
  for custom tables and templates.

## Tests

* Core: `tests/Meridian.Tests/DatabaseModelingTests.cs` (parsing per dialect with realistic multi-schema scripts,
  round trips parse → model → DDL → parse for all five dialects, migration scripts, validation, subject areas and
  permissions, refresh plans and selective apply, data dictionary, rendering and routing, EA and XMI, `.dacpac`,
  Mermaid) and `LiveDatabaseImportTests.cs` (SQLite file; PostgreSQL in a throwaway Testcontainers container on a
  random port). `DatabaseBodiesAndRenamesTests.cs`: bodies per dialect (parsing, round trip model → DDL → parse,
  generated batch syntax, migrations that replace / drop routines and triggers), renames by id per dialect, rename
  suggestions, and the generated-name drop scripts per dialect. `LiveDatabaseServerTests.cs`: SQL Server 2022
  (`mcr.microsoft.com/mssql/server:2022-latest`), Oracle Database Free (`gvenzl/oracle-free:23-slim-faststart`),
  MySQL 8.4 and PostgreSQL in throwaway Testcontainers containers on random ports — catalogs read, bodies carried to a
  fresh database and read back, and the migration scripts (lookup drops, renames) **run** against the servers. Each
  fixture skips its tests when Docker or the image is unavailable; `MERIDIAN_REQUIRE_SQLSERVER=1` /
  `MERIDIAN_REQUIRE_ORACLE=1` / `MERIDIAN_REQUIRE_MYSQL=1` turn that into a failure, `MERIDIAN_SKIP_ORACLE=1` skips the
  (slower) Oracle container, `MERIDIAN_SQLSERVER_IMAGE` / `MERIDIAN_ORACLE_IMAGE` / `MERIDIAN_MYSQL_IMAGE` pick other
  images. On Apple silicon the SQL Server image runs under emulation.
* Web: `tests/Meridian.Web.Tests/DatabaseWebTests.cs` (import, review, groups, add-to-group and DDL dialogs, the
  inspector section, connection permissions, the outbound guard and encryption) and `DatabaseMigrationRenameTests.cs`
  (the migration dialog's rename check boxes, the body editor).
* E2E: `tests/Meridian.E2E/DatabaseTests.cs` (including a column renamed in the inspector → a RENAME in the migration
  script against a committed version, and a confirmed suggestion against the original script); screenshots:
  `DatabaseDocsScreenshotTests` (`MERIDIAN_DOCS_SCREENSHOTS=1`).

## Limits

* Bodies are not translated between dialects, and only the characteristics written between the signature and the body
  are kept (`db.options`); a trigger's `REFERENCING` clause is dropped with a warning. Oracle routines inside packages and
  object types are not modelled.
* Composite and range types, partitions, table inheritance, exclusion constraints, policies and grants are skipped
  with a warning.
* View column types from DDL are unknown until a live refresh fills them in.
* Migration scripts do not reorder columns. Renames are detected for tables, views and columns (not for indexes,
  constraints, sequences or routines, which show as drop + create), within one schema, and not for swapped names. A
  primary key that only changed its name is not scripted.
* SQL Server: Windows authentication over named pipes / shared memory and Entra ID interactive or managed-identity
  sign-in are not available (managed networking, no Azure extension package); a SQL login or an access token works.
