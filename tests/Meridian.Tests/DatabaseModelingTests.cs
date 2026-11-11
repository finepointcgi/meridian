using System.IO.Compression;
using System.Text;
using Meridian.Core.DataModeling;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Core.Validation;
using Meridian.Serialization;
using Meridian.Serialization.Database;
using Meridian.Serialization.Mermaid;

namespace Meridian.Tests;

public class DatabaseParsingTests
{
    [Fact]
    public void Postgres_Script_Is_Read_With_Schemas_Keys_Indexes_Views_Types_And_Comments()
    {
        var c = DdlParser.Parse(DatabaseScripts.Postgres, "postgresql", "shop");
        Assert.Equal(["public", "sales", "inventory"], c.Schemas.Select(s => s.Name).ToArray());

        var customer = c.FindTable("public", "customer")!;
        Assert.Equal(["id"], customer.PrimaryKey);
        Assert.Equal("always", customer.FindColumn("id")!.Identity);
        Assert.Equal("public.email", customer.FindColumn("email")!.Type);
        Assert.Equal("character varying(200)", customer.FindColumn("Display Name")!.Type);
        Assert.Equal("numeric(10,2)", customer.FindColumn("credit")!.Type);
        Assert.False(customer.FindColumn("credit")!.Nullable);
        Assert.Equal("0", customer.FindColumn("credit")!.Default);
        Assert.Equal("timestamp with time zone", customer.FindColumn("created_at")!.Type);
        Assert.Equal("People who buy things", customer.Comment);
        Assert.Equal("Store credit in EUR", customer.FindColumn("credit")!.Comment);
        Assert.Contains(customer.Constraints, k => k.Name == "customer_email_uq" && k.Type == DbFields.UniqueConstraint && k.Columns.SequenceEqual(["email"]));
        var lowerEmail = customer.Indexes.Single(i => i.Name == "customer_lower_email");
        Assert.True(lowerEmail.Unique);
        Assert.StartsWith("lower(", lowerEmail.Columns.Single());

        var order = c.FindTable("sales", "order")!;
        Assert.Equal("serial", order.FindColumn("id")!.Type);
        Assert.Equal("sales.order_status", order.FindColumn("status")!.Type);
        Assert.Equal("0", order.FindColumn("total")!.Generated);
        Assert.Equal("C", order.FindColumn("note")!.Collation);
        var fk = order.ForeignKeys.Single();
        Assert.Equal(("order_customer_fk", "customer", "public", "RESTRICT", "CASCADE"), (fk.Name, fk.RefTable, fk.RefSchema, fk.OnDelete, fk.OnUpdate));
        var partial = order.Indexes.Single(i => i.Name == "order_open_idx");
        Assert.Contains("status <> 'cancelled'", partial.Where);
        Assert.Equal("order_touch", order.Triggers.Single().Name);
        Assert.Equal("BEFORE", order.Triggers.Single().Timing);
        Assert.Equal("INSERT, UPDATE", order.Triggers.Single().Events);

        var lines = c.FindTable("sales", "order_line")!;
        Assert.Equal(["order_id", "line_no"], lines.PrimaryKey);
        Assert.Equal(2, lines.ForeignKeys.Count);
        Assert.Equal("CASCADE", lines.ForeignKeys.Single(f => f.RefTable == "order").OnDelete);
        Assert.Equal("inventory", lines.ForeignKeys.Single(f => f.RefTable == "product").RefSchema);

        var product = c.FindTable("inventory", "product")!;
        Assert.Equal("product_pkey", product.PrimaryKeyName);
        Assert.Equal("text[]", product.FindColumn("tags")!.Type);
        Assert.Contains(product.Constraints, k => k.Type == DbFields.CheckConstraint && k.Expression == "price >= 0");
        Assert.Equal("gin", product.Indexes.Single().Method);

        var view = c.FindTable("sales", "open_orders")!;
        Assert.Equal(DbFields.View, view.Kind);
        Assert.Equal(["id", "customer_id", "customer_email"], view.Columns.Select(x => x.Name).ToArray());
        Assert.StartsWith("SELECT o.id", view.Definition);
        Assert.Equal(DbFields.MaterializedView, c.FindTable("sales", "daily_totals")!.Kind);

        var sales = c.FindSchema("sales")!;
        Assert.Equal("Orders and invoicing", sales.Comment);
        Assert.Equal(["new", "paid", "shipped", "cancelled"], sales.Types.Single().Values);
        Assert.Equal("1000", sales.Sequences.Single().Start);
        Assert.Equal("touch", sales.Routines.Single().Name);
        Assert.Equal("trigger", sales.Routines.Single().Returns);
        var domain = c.FindSchema("public")!.Types.Single();
        Assert.Equal(("email", "domain", "varchar(320)"), (domain.Name, domain.Kind, domain.BaseType));

        Assert.Contains(c.Warnings, w => w.Contains("EXTENSION"));
        Assert.Contains(c.Warnings, w => w.Contains("POLICY"));
        Assert.DoesNotContain(c.Warnings, w => w.Contains("GRANT") || w.Contains("SET"));
    }

    [Fact]
    public void SqlServer_Script_With_Go_Batches_Brackets_And_Extended_Properties()
    {
        var c = DdlParser.Parse(DatabaseScripts.SqlServer);
        Assert.Equal("sqlserver", c.Dialect);
        Assert.Equal("Shop", c.Name);
        var customers = c.FindTable("dbo", "Customers")!;
        Assert.Equal(["Id"], customers.PrimaryKey);
        Assert.Equal("PK_Customers", customers.PrimaryKeyName);
        Assert.Equal("always", customers.FindColumn("Id")!.Identity);
        Assert.Equal("nvarchar(max)", customers.FindColumn("Name")!.Type);
        Assert.Equal("((1))", customers.FindColumn("IsActive")!.Default);
        Assert.Equal("Registered customers", customers.Comment);
        Assert.Equal("Login e-mail", customers.FindColumn("Email")!.Comment);
        Assert.Contains(customers.Constraints, k => k.Name == "UQ_Customers_Email");

        var orders = c.FindTable("sales", "Orders")!;
        Assert.Equal("decimal(18,2)", orders.FindColumn("Amount")!.Type);
        Assert.Equal("[Amount] * (1.2)", orders.FindColumn("Total")!.Generated);
        Assert.Equal("(sysutcdatetime())", orders.FindColumn("PlacedAt")!.Default);
        var fk = orders.ForeignKeys.Single();
        Assert.Equal(("FK_Orders_Customers", "Customers", "dbo", "CASCADE"), (fk.Name, fk.RefTable, fk.RefSchema, fk.OnDelete));
        Assert.Contains(orders.Constraints, k => k.Name == "CK_Orders_Amount" && k.Type == DbFields.CheckConstraint);
        Assert.Contains(orders.Indexes, i => i.Name == "IX_Orders_CustomerId" && i.Columns.SequenceEqual(["CustomerId"]) && i.Where != null);
        Assert.Equal("trg_Orders_Audit", orders.Triggers.Single().Name);
        Assert.Equal("INSERT, UPDATE", orders.Triggers.Single().Events);
        // Two CREATE TABLEs in one batch without semicolons.
        Assert.NotNull(c.FindTable("sales", "OrderNotes"));
        Assert.Single(c.FindTable("sales", "OrderNotes")!.ForeignKeys);
        var view = c.FindTable("sales", "BigOrders")!;
        Assert.Equal(["Id", "Amount", "CustomerEmail"], view.Columns.Select(x => x.Name).ToArray());
        var proc = c.FindSchema("sales")!.Routines.Single();
        Assert.Equal((DbFields.Procedure, "PlaceOrder"), (proc.Kind, proc.Name));
        Assert.Contains("@CustomerId int", proc.Signature);
        Assert.Empty(c.Warnings);
    }

    [Fact]
    public void MySql_Script_With_Backticks_Keys_Comments_And_Delimiters()
    {
        var c = DdlParser.Parse(DatabaseScripts.MySql, null, "shop");
        Assert.Equal("mysql", c.Dialect);
        var customer = c.FindTable("shop", "customer")!;
        Assert.Equal("int unsigned", customer.FindColumn("id")!.Type);
        Assert.Equal("autoIncrement", customer.FindColumn("id")!.Identity);
        Assert.Equal("utf8mb4_bin", customer.FindColumn("email")!.Collation);
        Assert.Equal("login", customer.FindColumn("email")!.Comment);
        Assert.Equal("enum('active','blocked')", customer.FindColumn("status")!.Type);
        Assert.Equal("CURRENT_TIMESTAMP", customer.FindColumn("created_at")!.Default);
        Assert.Equal("People who buy", customer.Comment);
        Assert.Contains(customer.Indexes, i => i.Name == "uq_customer_email" && i.Unique);
        Assert.Contains(customer.Indexes, i => i.Name == "ix_customer_created" && !i.Unique);

        var orders = c.FindTable("shop", "orders")!;
        Assert.Equal("CASCADE", orders.ForeignKeys.Single().OnDelete);
        Assert.Contains(orders.Indexes, i => i.Name == "ix_orders_total");
        Assert.Equal("orders_bi", orders.Triggers.Single().Name);
        var visits = c.FindTable("analytics", "visits")!;
        Assert.Equal(["id"], visits.PrimaryKey);
        Assert.Equal(("shop", "customer"), (visits.ForeignKeys.Single().RefSchema, visits.ForeignKeys.Single().RefTable));
        Assert.Contains(visits.Indexes, i => i.Method == "fulltext");
        Assert.Equal("close_order", c.FindSchema("shop")!.Routines.Single().Name);
        Assert.Equal(["id", "total"], c.FindTable("shop", "big_orders")!.Columns.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void Sqlite_Script_With_Inline_Keys_Typeless_Columns_And_Triggers()
    {
        var c = DdlParser.Parse(DatabaseScripts.Sqlite);
        Assert.Equal("sqlite", c.Dialect);
        var author = c.FindTable("main", "author")!;
        Assert.Equal("autoIncrement", author.FindColumn("id")!.Identity);
        Assert.True(author.FindColumn("name")!.Unique);
        var book = c.FindTable("main", "book")!;
        Assert.Equal("CASCADE", book.ForeignKeys.Single().OnDelete);
        Assert.Contains(book.Constraints, k => k.Type == DbFields.UniqueConstraint && k.Columns.SequenceEqual(["author_id", "title"]));
        Assert.Contains(book.Indexes, i => i.Name == "book_isbn" && i.Unique && i.Where == "isbn IS NOT NULL");
        Assert.Equal("book_ai", book.Triggers.Single().Name);
        var tag = c.FindTable("main", "tag")!;
        Assert.Equal(["name"], tag.PrimaryKey);
        Assert.Equal("", tag.FindColumn("colour")!.Type);
        Assert.Equal(["title", "price"], c.FindTable("main", "cheap_books")!.Columns.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void Oracle_Script_With_Session_Schema_Plsql_Blocks_And_Comments()
    {
        var c = DdlParser.Parse(DatabaseScripts.Oracle);
        Assert.Equal("oracle", c.Dialect);
        var emp = c.FindTable("HR", "employees")!;
        Assert.Equal("byDefault", emp.FindColumn("employee_id")!.Identity);
        Assert.Equal("VARCHAR2(25)", emp.FindColumn("email")!.Type);
        Assert.Equal("NUMBER(8,2)", emp.FindColumn("salary")!.Type);
        Assert.Equal("SYSDATE", emp.FindColumn("hire_date")!.Default);
        Assert.Equal("Monthly salary", emp.FindColumn("salary")!.Comment);
        Assert.Equal("emp_dept_fk", emp.ForeignKeys.Single().Name);
        Assert.Empty(emp.ForeignKeys.Single().RefColumns);   // references the primary key
        Assert.Equal("dept_mgr_fk", c.FindTable("HR", "departments")!.ForeignKeys.Single().Name);
        Assert.Equal("dept_id_pk", c.FindTable("HR", "departments")!.PrimaryKeyName);
        Assert.Equal("207", c.FindSchema("HR")!.Sequences.Single().Start);
        Assert.Equal("add_job_history", c.FindSchema("HR")!.Routines.Single().Name);
        Assert.Equal(DbFields.View, c.FindTable("HR", "emp_details")!.Kind);
    }

    [Theory]
    [InlineData("CREATE TABLE [x] (id int IDENTITY(1,1))\nGO", "sqlserver")]
    [InlineData("CREATE TABLE `x` (id int AUTO_INCREMENT);", "mysql")]
    [InlineData("CREATE TABLE x (id INTEGER PRIMARY KEY AUTOINCREMENT);", "sqlite")]
    [InlineData("CREATE TABLE x (id NUMBER(10), n VARCHAR2(20));", "oracle")]
    [InlineData("CREATE TABLE x (id serial primary key);", "postgresql")]
    public void Dialect_Is_Detected(string sql, string dialect) => Assert.Equal(dialect, DdlParser.Detect(sql));

    [Fact]
    public void Unknown_Statements_Are_Skipped_With_Warnings_And_Do_Not_Stop_The_Import()
    {
        var c = DdlParser.Parse("CREATE FOOBAR thing;\nCREATE TABLE a (id int primary key);\nALTER TABLE missing ADD COLUMN x int;\nWHATEVER;\nCREATE TABLE b (a_id int references a);", "postgresql");
        Assert.Equal(["a", "b"], c.AllTables.Select(t => t.Name).ToArray());
        Assert.Equal(3, c.Warnings.Count);
        Assert.All(c.Warnings, w => Assert.StartsWith("line ", w));
    }

    [Fact]
    public void Mermaid_ErDiagram_Becomes_Tables_With_Foreign_Keys()
    {
        const string mmd = """
            erDiagram
                CUSTOMER ||--o{ ORDER : places
                ORDER ||--|{ LINE_ITEM : contains
                CUSTOMER {
                    int id PK
                    string email UK "login"
                }
                ORDER {
                    int id PK
                    int customer_id FK
                    date placed
                }
                LINE_ITEM {
                    int qty
                }
            """;
        var c = MermaidDatabase.ToCatalog(mmd, "shop");
        var order = c.FindTable(null, "ORDER")!;
        Assert.Equal(["id"], order.PrimaryKey);
        var fk = order.ForeignKeys.Single();
        Assert.Equal(("CUSTOMER", "customer_id", "id"), (fk.RefTable, fk.Columns.Single(), fk.RefColumns.Single()));
        Assert.True(c.FindTable(null, "CUSTOMER")!.FindColumn("email")!.Unique);
        Assert.Equal("login", c.FindTable(null, "CUSTOMER")!.FindColumn("email")!.Comment);
        var line = c.FindTable(null, "LINE_ITEM")!;
        Assert.Equal("order_id", line.ForeignKeys.Single().Columns.Single());
        Assert.NotNull(line.FindColumn("order_id"));
    }

    [Fact]
    public void Dacpac_Model_Xml_Is_Read()
    {
        const string modelXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <DataSchemaModel FileFormatVersion="1.2" SchemaVersion="2.9" xmlns="http://schemas.microsoft.com/sqlserver/dac/Serialization/2012/02">
              <Model>
                <Element Type="SqlSchema" Name="[sales]" />
                <Element Type="SqlTable" Name="[dbo].[Customers]">
                  <Relationship Name="Columns">
                    <Entry><Element Type="SqlSimpleColumn" Name="[dbo].[Customers].[Id]"><Property Name="IsNullable" Value="False" /><Property Name="IsIdentity" Value="True" />
                      <Relationship Name="TypeSpecifier"><Entry><Element Type="SqlTypeSpecifier"><Relationship Name="Type"><Entry><References ExternalSource="BuiltIns" Name="[int]" /></Entry></Relationship></Element></Entry></Relationship></Element></Entry>
                    <Entry><Element Type="SqlSimpleColumn" Name="[dbo].[Customers].[Email]"><Property Name="IsNullable" Value="False" />
                      <Relationship Name="TypeSpecifier"><Entry><Element Type="SqlTypeSpecifier"><Property Name="Length" Value="320" /><Relationship Name="Type"><Entry><References ExternalSource="BuiltIns" Name="[nvarchar]" /></Entry></Relationship></Element></Entry></Relationship></Element></Entry>
                  </Relationship>
                </Element>
                <Element Type="SqlTable" Name="[sales].[Orders]">
                  <Relationship Name="Columns">
                    <Entry><Element Type="SqlSimpleColumn" Name="[sales].[Orders].[Id]"><Property Name="IsNullable" Value="False" />
                      <Relationship Name="TypeSpecifier"><Entry><Element Type="SqlTypeSpecifier"><Relationship Name="Type"><Entry><References ExternalSource="BuiltIns" Name="[int]" /></Entry></Relationship></Element></Entry></Relationship></Element></Entry>
                    <Entry><Element Type="SqlSimpleColumn" Name="[sales].[Orders].[CustomerId]">
                      <Relationship Name="TypeSpecifier"><Entry><Element Type="SqlTypeSpecifier"><Relationship Name="Type"><Entry><References ExternalSource="BuiltIns" Name="[int]" /></Entry></Relationship></Element></Entry></Relationship></Element></Entry>
                    <Entry><Element Type="SqlSimpleColumn" Name="[sales].[Orders].[Amount]">
                      <Relationship Name="TypeSpecifier"><Entry><Element Type="SqlTypeSpecifier"><Property Name="Precision" Value="18" /><Property Name="Scale" Value="2" /><Relationship Name="Type"><Entry><References ExternalSource="BuiltIns" Name="[decimal]" /></Entry></Relationship></Element></Entry></Relationship></Element></Entry>
                  </Relationship>
                </Element>
                <Element Type="SqlPrimaryKeyConstraint" Name="[dbo].[PK_Customers]">
                  <Relationship Name="ColumnSpecifications"><Entry><Element Type="SqlIndexedColumnSpecification"><Relationship Name="Column"><Entry><References Name="[dbo].[Customers].[Id]" /></Entry></Relationship></Element></Entry></Relationship>
                  <Relationship Name="DefiningTable"><Entry><References Name="[dbo].[Customers]" /></Entry></Relationship>
                </Element>
                <Element Type="SqlForeignKeyConstraint" Name="[sales].[FK_Orders_Customers]">
                  <Property Name="DeleteAction" Value="1" />
                  <Relationship Name="Columns"><Entry><References Name="[sales].[Orders].[CustomerId]" /></Entry></Relationship>
                  <Relationship Name="DefiningTable"><Entry><References Name="[sales].[Orders]" /></Entry></Relationship>
                  <Relationship Name="ForeignColumns"><Entry><References Name="[dbo].[Customers].[Id]" /></Entry></Relationship>
                  <Relationship Name="ForeignTable"><Entry><References Name="[dbo].[Customers]" /></Entry></Relationship>
                </Element>
                <Element Type="SqlIndex" Name="[sales].[Orders].[IX_Orders_Customer]">
                  <Property Name="IsUnique" Value="False" />
                  <Relationship Name="ColumnSpecifications"><Entry><Element Type="SqlIndexedColumnSpecification"><Relationship Name="Column"><Entry><References Name="[sales].[Orders].[CustomerId]" /></Entry></Relationship></Element></Entry></Relationship>
                  <Relationship Name="IndexedObject"><Entry><References Name="[sales].[Orders]" /></Entry></Relationship>
                </Element>
                <Element Type="SqlExtendedProperty" Name="[SqlColumn].[dbo].[Customers].[Email].[MS_Description]">
                  <Property Name="Value"><Value><![CDATA[N'Login e-mail']]></Value></Property>
                </Element>
              </Model>
            </DataSchemaModel>
            """;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("model.xml").Open(), new UTF8Encoding(false));
            w.Write(modelXml.Trim());
        }
        ms.Position = 0;
        var c = DatabaseFiles.Read("Shop.dacpac", ms);
        Assert.Equal("sqlserver", c.Dialect);
        var customers = c.FindTable("dbo", "Customers")!;
        Assert.Equal(["Id"], customers.PrimaryKey);
        Assert.Equal("always", customers.FindColumn("Id")!.Identity);
        Assert.Equal("nvarchar(320)", customers.FindColumn("Email")!.Type);
        Assert.Equal("Login e-mail", customers.FindColumn("Email")!.Comment);
        var orders = c.FindTable("sales", "Orders")!;
        Assert.Equal("decimal(18,2)", orders.FindColumn("Amount")!.Type);
        Assert.Equal(("FK_Orders_Customers", "Customers", "CASCADE"), (orders.ForeignKeys.Single().Name, orders.ForeignKeys.Single().RefTable, orders.ForeignKeys.Single().OnDelete));
        Assert.Equal("IX_Orders_Customer", orders.Indexes.Single().Name);
    }

    [Fact]
    public void Enterprise_Architect_Data_Models_Keep_Lengths_Not_Null_Keys_And_Foreign_Keys()
    {
        var dir = Directory.CreateTempSubdirectory("meridian-ea-db-");
        var path = Path.Combine(dir.FullName, "model.qea");
        try
        {
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                void Exec(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
                Exec("create table t_package (Package_ID int, Name text, Parent_ID int, Notes text)");
                Exec("create table t_object (Object_ID int, Object_Type text, Name text, Alias text, Note text, Package_ID int, Stereotype text, ParentID int, Classifier int, Abstract text, NType int)");
                Exec("create table t_attribute (ID int, Object_ID int, Name text, Type text, Classifier text, LowerBound text, UpperBound text, \"Default\" text, Notes text, Scope text, Pos int, Length int, Precision int, Scale int, AllowDuplicates int)");
                Exec("create table t_operation (OperationID int, Object_ID int, Name text, Type text, Notes text, Pos int, Stereotype text)");
                Exec("create table t_operationparams (OperationID int, Name text, Type text, Kind text, Pos int)");
                Exec("create table t_connector (Connector_ID int, Connector_Type text, SubType text, Stereotype text, Name text, Start_Object_ID int, End_Object_ID int, DestCard text, PDATA2 text)");
                Exec("insert into t_package values (1,'Model',0,''),(2,'Data',1,'')");
                Exec("insert into t_object values (10,'Class','customer','','',2,'table',0,0,'0',0),(11,'Class','orders','','',2,'table',0,0,'0',0)");
                Exec("""
                    insert into t_attribute values
                    (1,10,'id','INTEGER','','1','1','','','Public',0,0,0,0,1),
                    (2,10,'email','VARCHAR','','1','1','','','Public',1,320,0,0,1),
                    (3,11,'id','INTEGER','','1','1','','','Public',0,0,0,0,1),
                    (4,11,'customer_id','INTEGER','','1','1','','','Public',1,0,0,0,0),
                    (5,11,'total','DECIMAL','','1','1','','','Public',2,0,10,2,0)
                    """);
                Exec("insert into t_operation values (1,10,'PK_customer','','',0,'PK'),(2,11,'PK_orders','','',0,'PK'),(3,11,'FK_orders_customer','','',1,'FK'),(4,10,'UQ_email','','',1,'unique')");
                Exec("insert into t_operationparams values (1,'id','INTEGER','in',0),(2,'id','INTEGER','in',0),(3,'customer_id','INTEGER','in',0),(4,'email','VARCHAR','in',0)");
                Exec("insert into t_connector values (100,'Association','','FK','FK_orders_customer',11,10,'','')");
            }
            var (m, _) = EaImporter.Import(path);
            var customer = m.OfKind(ElementKind.Table).Single(t => t.Name == "customer");
            var orders = m.OfKind(ElementKind.Table).Single(t => t.Name == "orders");
            Assert.Equal("VARCHAR(320)", DbModel.Column(customer, "email")!.Get(FieldKeys.SqlType));
            Assert.Equal("DECIMAL(10,2)", DbModel.Column(orders, "total")!.Get(FieldKeys.SqlType));
            Assert.False(DbModel.IsNullable(DbModel.Column(customer, "email")!));
            Assert.True(DbModel.IsNullable(DbModel.Column(orders, "customer_id")!));
            Assert.True(DbModel.IsPk(DbModel.Column(customer, "id")!));
            Assert.Equal("PK_orders", orders.Get(DbFields.PkName));
            Assert.Contains(DbModel.Constraints(customer), k => k.Name == "UQ_email" && k.Get(DbFields.Columns) == "email");
            var fk = m.Outgoing(orders, ElementKind.ForeignKey).Single();
            Assert.Equal((customer, "customer_id", "id"), (fk.Target, fk.Get(FieldKeys.FkColumns), fk.Get(FieldKeys.RefColumns)));
            Assert.Contains("REFERENCES \"customer\"", SqlDdl.Generate(m, SqlDialect.PostgreSql));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { dir.Delete(true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Dacpac_Without_Model_Is_Rejected()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) zip.CreateEntry("Origin.xml");
        ms.Position = 0;
        Assert.Throws<FormatException>(() => DacpacReader.Read(ms));
    }
}

public class DatabaseModelTests
{
    internal static (ModelRepository M, DbImportResult R) Import(string sql, string? dialect = null, string name = "shop")
    {
        var m = new ModelRepository("Model");
        var r = DbImport.Import(m, m.Root, DdlParser.Parse(sql, dialect, name), new DbImportOptions { Source = "ddl:test.sql" });
        return (m, r);
    }

    [Fact]
    public void Import_Builds_Database_Schemas_Tables_Keys_And_A_Diagram_Per_Schema_As_One_Undo_Step()
    {
        var (m, r) = Import(DatabaseScripts.Postgres, "postgresql");
        Assert.True(DbModel.IsDatabase(r.Database));
        Assert.Equal("postgresql", r.Database.Get(DbFields.Dialect));
        Assert.Equal(["public", "sales", "inventory"], r.Database.OwnedElements.Where(DbModel.IsSchema).Select(s => s.Name).ToArray());
        var order = m.OfKind(ElementKind.Table).Single(t => t.Name == "order");
        Assert.Equal("shop.sales.order", order.Get(DbFields.QualifiedName));
        Assert.Equal("shop.sales.order.customer_id", DbModel.Column(order, "customer_id")!.Get(DbFields.QualifiedName));
        var fk = m.Outgoing(order, ElementKind.ForeignKey).Single();
        Assert.Equal("customer", fk.Target.Name);
        Assert.Equal("RESTRICT", fk.Get(DbFields.OnDelete));
        Assert.Equal("People who buy things", m.OfKind(ElementKind.Table).Single(t => t.Name == "customer").Documentation);
        Assert.Contains(m.OfKind(ElementKind.Enumeration), e => e.Name == "order_status" && e.OwnedElements.Count == 4);
        Assert.Contains(m.OfKind(ElementKind.Sequence), e => e.Name == "invoice_no");
        Assert.Contains(m.OfKind(ElementKind.Routine), e => e.Name == "touch");
        Assert.Contains(order.OwnedElements, e => e.Kind == ElementKind.Trigger && e.Name == "order_touch");
        Assert.Equal(3, r.Diagrams.Count);
        Assert.All(r.Diagrams, d => Assert.Equal(DiagramType.Database, d.DiagramType));
        var salesDiagram = r.Diagrams.Single(d => d.Owner!.Name == "sales");
        Assert.Equal(salesDiagram.Owner!.OwnedElements.Count(e => e.Kind == ElementKind.Table), salesDiagram.Nodes.Count);
        Assert.Contains(salesDiagram.Edges, e => e.Relationship.Kind == ElementKind.ForeignKey);
        Assert.Equal("Import database shop", m.History.UndoDescription);
        m.History.Undo();
        Assert.Empty(m.OfKind(ElementKind.Table));
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    [InlineData("sqlite")]
    [InlineData("oracle")]
    public void Round_Trip_Parse_Model_Generate_Parse_Is_Equal(string dialect)
    {
        var script = dialect switch
        {
            "sqlserver" => DatabaseScripts.SqlServer,
            "mysql" => DatabaseScripts.MySql,
            "sqlite" => DatabaseScripts.Sqlite,
            "oracle" => DatabaseScripts.Oracle,
            _ => DatabaseScripts.Postgres,
        };
        var (m, r) = Import(script, dialect);
        var fromModel = DbExtract.FromModel(m, r.Database).Catalog;
        var ddl = DdlWriter.Generate(fromModel, dialect);
        var reparsed = DdlParser.Parse(ddl, dialect, fromModel.Name);
        // Everything survives, routine and trigger bodies included.
        var diff = DbDiff.Compare(fromModel, reparsed).ToList();
        if (dialect != "sqlite") Assert.Contains(fromModel.Schemas.SelectMany(s => s.Routines), x => x.Body is { Length: > 0 });
        if (dialect != "oracle") Assert.Contains(fromModel.AllTables.SelectMany(t => t.Triggers), x => x.Action is { Length: > 0 });
        Assert.True(diff.Count == 0, string.Join("\n", diff.Select(d => d.Describe())) + "\n\n" + ddl);
        Assert.DoesNotContain(reparsed.Warnings, w => !w.Contains("not part of the data model"));
    }

    [Fact]
    public void Generated_Ddl_Orders_Tables_By_Dependency_With_Foreign_Keys_Last()
    {
        var (m, r) = Import(DatabaseScripts.Postgres, "postgresql");
        var ddl = SqlDdl.Generate(m, r.Database, SqlDialect.PostgreSql);
        var customer = ddl.IndexOf("CREATE TABLE public.customer", StringComparison.Ordinal);
        var order = ddl.IndexOf("CREATE TABLE sales.\"order\"", StringComparison.Ordinal);
        var lines = ddl.IndexOf("CREATE TABLE sales.order_line", StringComparison.Ordinal);
        var firstFk = ddl.IndexOf("FOREIGN KEY", StringComparison.Ordinal);
        Assert.True(customer >= 0 && order > customer && lines > order, ddl);
        Assert.True(firstFk > lines && firstFk > ddl.LastIndexOf("CREATE TABLE", StringComparison.Ordinal));
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS sales;", ddl);
        Assert.Contains("CREATE TYPE sales.order_status AS ENUM ('new', 'paid', 'shipped', 'cancelled');", ddl);
        Assert.Contains("COMMENT ON COLUMN public.customer.credit IS 'Store credit in EUR';", ddl);
        Assert.Contains("never executes DDL", ddl);

        var tsql = SqlDdl.Generate(m, r.Database, SqlDialect.SqlServer);
        Assert.Contains("IDENTITY(1,1)", tsql);
        Assert.Contains("\nGO\n", tsql);
        Assert.Contains("sp_addextendedproperty", tsql);
        Assert.Contains("[order]", tsql);
        var mysql = SqlDdl.Generate(m, r.Database, SqlDialect.MySql);
        Assert.Contains("ENUM('new', 'paid', 'shipped', 'cancelled')", mysql);
        Assert.Contains("COMMENT 'Store credit in EUR'", mysql);
        var sqlite = SqlDdl.Generate(m, r.Database, SqlDialect.Sqlite);
        Assert.DoesNotContain("ALTER TABLE", sqlite);
        Assert.Contains("REFERENCES", sqlite);
    }

    [Fact]
    public void Ddl_For_A_Subject_Area_Contains_Only_Its_Tables()
    {
        var (m, r) = Import(DatabaseScripts.Postgres, "postgresql");
        var g = DbModel.CreateGroup(m, r.Database, "Ordering");
        DbModel.AddToGroup(m, m.OfKind(ElementKind.Table).Where(t => t.Name is "order" or "order_line"), g);
        var ddl = SqlDdl.Generate(m, g, SqlDialect.PostgreSql);
        Assert.Contains("CREATE TABLE sales.\"order\"", ddl);
        Assert.DoesNotContain("CREATE TABLE public.customer", ddl);
        Assert.Contains("REFERENCES public.customer", ddl);   // the key to the table outside the area stays
    }

    [Fact]
    public void Migration_Script_Adds_Alters_And_Drops_With_Warnings()
    {
        var (m, r) = Import(DatabaseScripts.Postgres, "postgresql");
        var baseline = DbExtract.FromModel(m, r.Database).Catalog;
        var customer = m.OfKind(ElementKind.Table).Single(t => t.Name == "customer");
        var order = m.OfKind(ElementKind.Table).Single(t => t.Name == "order");
        DbModel.AddColumn(m, customer, "phone", "varchar(30)");
        m.Set(DbModel.Column(customer, "credit")!, FieldKeys.SqlType, "numeric(14,2)");
        m.Delete(DbModel.Column(order, "note")!);
        m.Set(DbModel.Column(customer, "Display Name")!, FieldKeys.Nullable, null);   // becomes NOT NULL
        var invoice = DbModel.CreateTable(m, DbModel.SchemaOf(order)!, "invoice");
        var orderId = DbModel.AddColumn(m, invoice, "order_id", "integer", nullable: false);
        var fk = m.Connect(ElementKind.ForeignKey, invoice, order, invoice.Owner);
        DbModel.CompleteForeignKey(m, fk, orderId);
        var ix = m.Create(customer, ElementKind.DbIndex, "customer_phone_idx");
        m.Set(ix, DbFields.Columns, "phone");

        var after = DbExtract.FromModel(m, r.Database).Catalog;
        var script = MigrationWriter.Generate(baseline, after, "postgresql");
        Assert.Contains("ALTER TABLE public.customer ADD COLUMN phone varchar(30);", script.Sql);
        Assert.Contains("ALTER TABLE public.customer ALTER COLUMN credit TYPE numeric(14,2)", script.Sql);
        Assert.Contains("ALTER TABLE sales.\"order\" DROP COLUMN note;", script.Sql);
        Assert.Contains("SET NOT NULL", script.Sql);
        Assert.Contains("CREATE TABLE sales.invoice", script.Sql);
        Assert.Contains("CREATE INDEX customer_phone_idx ON public.customer (phone);", script.Sql);
        var fkLine = script.Sql.IndexOf("REFERENCES sales.\"order\"", StringComparison.Ordinal);
        Assert.True(fkLine > script.Sql.IndexOf("CREATE TABLE sales.invoice", StringComparison.Ordinal));
        Assert.Contains(script.Warnings, w => w.Contains("deletes its data"));
        Assert.Contains(script.Warnings, w => w.Contains("can fail or truncate"));
        Assert.Contains(script.Warnings, w => w.Contains("NOT NULL"));
        Assert.Contains("-- WARNING:", script.Sql);

        var tsql = MigrationWriter.Generate(baseline, after, "sqlserver").Sql;
        Assert.Contains("ALTER TABLE [public].customer ADD phone nvarchar(30);", tsql);
        Assert.Contains("ALTER COLUMN credit numeric(14,2) NOT NULL", tsql);
        var none = MigrationWriter.Generate(after, after, "mysql");
        Assert.Equal(0, none.Steps);
        Assert.Contains("No differences", none.Sql);
    }

    [Fact]
    public void Validation_Reports_Missing_Keys_Type_Mismatches_Duplicates_Reserved_Words_Naming_And_Orphans()
    {
        var m = new ModelRepository("M");
        var db = DbModel.CreateDatabase(m, m.Root, "app", "postgresql");
        m.Set(db, DbFields.Naming, "snake_case");
        var s = DbModel.CreateSchema(m, db, "public");
        var user = DbModel.CreateTable(m, s, "user");               // reserved word in PostgreSQL
        var log = m.Create(s, ElementKind.Table, "AuditLog");         // no PK, not snake_case
        DbModel.AddColumn(m, log, "message", "text");
        DbModel.AddColumn(m, log, "Message", "text");                 // duplicate (case-insensitive)
        var post = DbModel.CreateTable(m, s, "post");
        var userId = DbModel.AddColumn(m, post, "user_id", "varchar(10)");
        var fk = m.Connect(ElementKind.ForeignKey, post, user, s);
        m.Set(fk, FieldKeys.FkColumns, "user_id");
        m.Set(fk, FieldKeys.RefColumns, "id");
        var dangling = m.Connect(ElementKind.ForeignKey, log, user, s);
        m.Set(dangling, FieldKeys.FkColumns, "nope");
        DbModel.AddToGroup(m, [user], DbModel.CreateGroup(m, db, "Accounts"));

        var issues = new ModelValidator().Validate(m).Where(i => i.RuleId.StartsWith("DB-")).ToList();
        Assert.Contains(issues, i => i.RuleId == "DB-001" && i.Element == log);
        Assert.Contains(issues, i => i.RuleId == "DB-002" && i.Element == fk && i.Message.Contains("varchar(10)"));
        Assert.Contains(issues, i => i.RuleId == "DB-003" && i.Message.Contains("'message'", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, i => i.RuleId == "DB-004" && i.Element == user);
        Assert.Contains(issues, i => i.RuleId == "DB-005" && i.Element == log);
        Assert.Contains(issues, i => i.RuleId == "DB-006" && i.Element == post);
        Assert.DoesNotContain(issues, i => i.RuleId == "DB-006" && i.Element == user);
        Assert.Contains(issues, i => i.RuleId == "DB-007" && i.Element == dangling);
        Assert.False(DbValidationRules.Follows("OrderLine", "snake_case"));
        Assert.True(DbValidationRules.Follows("OrderLine", "PascalCase"));
        Assert.True(DbValidationRules.Follows("orderLine", "camelCase"));
        _ = userId;
    }

    [Fact]
    public void Renaming_A_Column_Updates_Foreign_Keys_And_Indexes_In_The_Same_Undo_Step()
    {
        var (m, _) = Import(DatabaseScripts.Postgres, "postgresql");
        var customer = m.OfKind(ElementKind.Table).Single(t => t.Name == "customer");
        var order = m.OfKind(ElementKind.Table).Single(t => t.Name == "order");
        var id = DbModel.Column(customer, "id")!;
        var cid = DbModel.Column(order, "customer_id")!;
        var ix = DbModel.Indexes(order).Single(i => i.Name == "order_open_idx");
        m.Rename(id, "customer_key");
        m.Rename(cid, "buyer_id");
        var fk = m.Outgoing(order, ElementKind.ForeignKey).Single();
        Assert.Equal("customer_key", fk.Get(FieldKeys.RefColumns));
        Assert.Equal("buyer_id", fk.Get(FieldKeys.FkColumns));
        Assert.Equal("buyer_id", ix.Get(DbFields.Columns));
        m.History.Undo();
        Assert.Equal("customer_id", fk.Get(FieldKeys.FkColumns));
        Assert.Equal("customer_id", ix.Get(DbFields.Columns));
        Assert.Equal("customer_key", fk.Get(FieldKeys.RefColumns));
    }

    [Fact]
    public void Project_File_And_Xmi_Round_Trips_Keep_The_Data_Model()
    {
        var (m, r) = Import(DatabaseScripts.Postgres, "postgresql");
        var g = DbModel.CreateGroup(m, r.Database, "Ordering");
        DbModel.AddToGroup(m, m.OfKind(ElementKind.Table).Where(t => t.Name == "order"), g);
        var before = DbExtract.FromModel(m, r.Database).Catalog;

        var reloaded = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        var db2 = DbModel.Databases(reloaded).Single();
        Assert.Empty(DbDiff.Compare(before, DbExtract.FromModel(reloaded, db2).Catalog));
        Assert.Single(DbModel.Members(reloaded, DbModel.Groups(reloaded).Single()));

        var (viaXmi, _) = XmiExchange.Import(XmiExchange.ToXml(m));
        var db3 = DbModel.Databases(viaXmi).Single();
        var diff = DbDiff.Compare(before, DbExtract.FromModel(viaXmi, db3).Catalog);
        Assert.True(diff.Count == 0, string.Join("\n", diff.Select(d => d.Describe())));
        Assert.Contains(viaXmi.OfKind(ElementKind.DbIndex), i => i.Name == "order_open_idx");
        Assert.Contains(viaXmi.OfKind(ElementKind.Sequence), s => s.Name == "invoice_no");
    }

    [Fact]
    public void Drawing_A_Foreign_Key_Creates_The_Referencing_Column_When_Needed()
    {
        var m = new ModelRepository("M");
        var db = DbModel.CreateDatabase(m, m.Root, "app");
        var s = DbModel.CreateSchema(m, db, "public");
        var customer = DbModel.CreateTable(m, s, "Customer", pkType: "bigserial");
        var order = DbModel.CreateTable(m, s, "order");
        var fk = m.Connect(ElementKind.ForeignKey, order, customer, s);
        DbModel.CompleteForeignKey(m, fk);
        var col = DbModel.Column(order, "customer_id")!;
        Assert.Equal("bigint", col.Get(FieldKeys.SqlType));
        Assert.Equal(("customer_id", "id"), (fk.Get(FieldKeys.FkColumns), fk.Get(FieldKeys.RefColumns)));
        Assert.Equal("fk_order_customer", fk.Name);

        // From a chosen column to a chosen column: nothing is created.
        var note = DbModel.AddColumn(m, order, "owner", "bigint");
        var fk2 = m.Connect(ElementKind.ForeignKey, order, customer, s);
        var before = DbModel.Columns(order).Count();
        DbModel.CompleteForeignKey(m, fk2, note, DbModel.Column(customer, "id"));
        Assert.Equal(before, DbModel.Columns(order).Count());
        Assert.Equal("owner", fk2.Get(FieldKeys.FkColumns));

        DbModel.SetPrimaryKey(m, note, true);
        Assert.True(DbModel.IsPk(note));
        Assert.False(DbModel.IsNullable(note));
        Assert.Equal(ArrowHead.CrowsFoot, DbNotation.ForeignKeyStyle(fk).SourceHead);
        m.Set(col, FieldKeys.Nullable, "true");
        Assert.Equal(ArrowHead.HollowDot, DbNotation.ForeignKeyStyle(fk).TargetHead);
        m.Set(col, FieldKeys.Unique, "true");
        Assert.Equal(ArrowHead.Bar, DbNotation.ForeignKeyStyle(fk).SourceHead);
    }
}

public class DatabaseGroupsAndRefreshTests
{
    [Fact]
    public void Subject_Areas_Hold_Tables_From_Several_Schemas_And_A_Table_Can_Be_In_Several()
    {
        var (m, r) = DatabaseModelTests.Import(DatabaseScripts.Postgres, "postgresql");
        var order = m.OfKind(ElementKind.Table).Single(t => t.Name == "order");
        var customer = m.OfKind(ElementKind.Table).Single(t => t.Name == "customer");
        var ordering = DbModel.CreateGroup(m, r.Database, "Ordering");
        var crm = DbModel.CreateGroup(m, r.Database, "CRM", "#e0a85a");
        Assert.Equal(2, DbModel.AddToGroup(m, [order, customer], ordering));
        Assert.Equal(0, DbModel.AddToGroup(m, [order], ordering));
        DbModel.AddToGroup(m, [customer], crm);
        Assert.Equal(["customer", "order"], DbModel.Members(m, ordering).Select(t => t.Name).ToArray());
        Assert.Equal(["CRM", "Ordering"], DbModel.GroupsOf(m, customer).Select(g => g.Name).OrderBy(x => x).ToArray());
        Assert.Equal("#e0a85a", DbModel.ColorOf(crm));
        Assert.NotEqual(DbModel.ColorOf(ordering), DbModel.ColorOf(crm));
        Assert.Equal("CRM, Ordering", string.Join(", ", Meridian.Core.Analysis.ModelValues.Get(m, customer, "subjectAreas").Split(", ").OrderBy(x => x)));

        // Filtering a diagram hides tables outside the area, and the paths to them.
        var d = r.Diagrams.Single(x => x.Owner!.Name == "sales");
        DbModel.FilterDiagram(m, d, crm);
        Assert.DoesNotContain(DiagramExtras.VisibleView(d).Nodes, n => n.Element == order);
        DbModel.FilterDiagram(m, d, ordering);
        Assert.Contains(DiagramExtras.VisibleView(d).Nodes, n => n.Element == order);
        Assert.DoesNotContain(DiagramExtras.VisibleView(d).Nodes, n => n.Element.Name == "order_line");

        // A diagram per area, framed in its colour.
        var gd = DbDiagrams.BuildForGroup(m, ordering);
        Assert.Equal(2, gd.Nodes.Count);
        var frames = DbModel.GroupFrames(m, gd);
        Assert.Contains(frames, f => f.Group == ordering && f.Nodes.Count == 2);
        var svg = SvgRenderer.Render(m, gd);
        Assert.Contains("db-area", svg);
        Assert.Contains("Ordering", svg);

        DbModel.RemoveFromGroup(m, [order], ordering);
        Assert.Equal(["customer"], DbModel.Members(m, ordering).Select(t => t.Name).ToArray());
        DbModel.DeleteGroup(m, crm);
        Assert.DoesNotContain(crm.Id, DbModel.GroupIds(customer));
        Assert.True(m.Contains(customer));
    }

    [Fact]
    public void Auto_Diagram_Plans_A_Database_Diagram_Per_Schema_And_Per_Subject_Area()
    {
        var (m, r) = DatabaseModelTests.Import(DatabaseScripts.Postgres, "postgresql");
        foreach (var d in r.Diagrams.ToList()) m.Delete(d);
        var g = DbModel.CreateGroup(m, r.Database, "Ordering");
        DbModel.AddToGroup(m, m.OfKind(ElementKind.Table).Where(t => t.Name.StartsWith("order")), g);
        var plan = AutoDiagrammer.Plan(m, new AutoDiagramOptions { Scope = r.Database });
        var dbPlans = plan.Diagrams.Where(p => p.Type == DiagramType.Database).ToList();
        Assert.Contains(dbPlans, p => p.Owner.Name == "sales");
        Assert.Contains(dbPlans, p => p.Owner.Name == "public");
        Assert.Contains(dbPlans, p => p.Owner == g);
        var result = AutoDiagrammer.Apply(m, plan);
        Assert.Contains(result.All, d => d.DiagramType == DiagramType.Database && d.Owner == g);
    }

    [Fact]
    public void Package_Permissions_On_A_Subject_Area_Apply_To_Its_Tables()
    {
        var (m, r) = DatabaseModelTests.Import(DatabaseScripts.Postgres, "postgresql");
        var order = m.OfKind(ElementKind.Table).Single(t => t.Name == "order");
        var customer = m.OfKind(ElementKind.Table).Single(t => t.Name == "customer");
        var g = DbModel.CreateGroup(m, r.Database, "Finance");
        DbModel.AddToGroup(m, [order], g);
        var user = Guid.NewGuid();
        var view = AccessView.For([new PermissionRule(g.Id, PrincipalKind.User, user, PackageAccess.Read)], user, [], PackageAccess.Edit, PackageAccess.Manage);
        Assert.False(view.CanEdit(order));
        Assert.False(view.CanEdit(DbModel.Column(order, "id")!));
        Assert.True(view.CanEdit(customer));
        Assert.NotNull(PermissionCheck.Check(m, view, new SetFieldOp(order.Id, FieldKeys.Documentation, "x")));
        // A rule on the table's schema further up loses to the area (nearest wins); several areas: the most restrictive.
        var open = DbModel.CreateGroup(m, r.Database, "Open");
        DbModel.AddToGroup(m, [order], open);
        var view2 = AccessView.For([new PermissionRule(open.Id, PrincipalKind.User, user, PackageAccess.Edit), new PermissionRule(g.Id, PrincipalKind.User, user, PackageAccess.Read),
                                    new PermissionRule(DbModel.SchemaOf(order)!.Id, PrincipalKind.User, user, PackageAccess.Manage)], user, [], PackageAccess.Read, PackageAccess.Manage);
        Assert.Equal(PackageAccess.Read, view2.Level(order));
        Assert.False(view2.IsHidden(order));
    }

    [Fact]
    public void Refresh_Shows_Added_Changed_And_Removed_Objects_And_Applies_A_Selection_As_One_Undo_Step()
    {
        var (m, r) = DatabaseModelTests.Import(DatabaseScripts.Postgres, "postgresql");
        var customer = m.OfKind(ElementKind.Table).Single(t => t.Name == "customer");
        var product = m.OfKind(ElementKind.Table).Single(t => t.Name == "product");
        // Work done in Meridian that a refresh must keep.
        m.Set(product, FieldKeys.Documentation, "Written in Meridian");
        var g = DbModel.CreateGroup(m, r.Database, "Catalog");
        DbModel.AddToGroup(m, [product], g);
        m.Rename(customer, "client");   // renamed in the model, still matched by its database name
        var diagram = r.Diagrams.Single(d => d.Owner!.Name == "inventory");
        var node = diagram.NodeFor(product)!;
        m.SetBounds(diagram, new Dictionary<DiagramNode, Bounds> { [node] = node.Bounds with { X = 777 } });

        var changed = DatabaseScripts.Postgres
            .Replace("credit numeric(10,2) DEFAULT 0 NOT NULL,", "credit numeric(12,2) DEFAULT 0 NOT NULL,\n    phone text,")
            .Replace("tags text[],", "")
            .Replace("COMMENT ON TABLE public.customer IS 'People who buy things';", "COMMENT ON TABLE public.customer IS 'Buyers';")
            .Replace("CREATE INDEX product_tags_gin ON inventory.product USING gin (tags);", "")
            + "\nCREATE TABLE inventory.warehouse (id int primary key, name text not null);\nCOMMENT ON TABLE inventory.product IS 'From the database';";
        var plan = DbRefresh.Plan(m, r.Database, DdlParser.Parse(changed, "postgresql", "shop"));
        string Kinds(DbObjectType t, DbChangeKind k) => string.Join(",", plan.Changes.Where(c => c.Type == t && c.Kind == k).Select(c => c.Name).OrderBy(x => x));
        Assert.Equal("phone", Kinds(DbObjectType.Column, DbChangeKind.Added));
        Assert.Equal("credit", Kinds(DbObjectType.Column, DbChangeKind.Changed));
        Assert.Equal("tags", Kinds(DbObjectType.Column, DbChangeKind.Removed));
        Assert.Equal("product_tags_gin", Kinds(DbObjectType.Index, DbChangeKind.Removed));
        Assert.Equal("warehouse", Kinds(DbObjectType.Table, DbChangeKind.Added));
        Assert.Contains(plan.Changes, c => c.Type == DbObjectType.Table && c.Kind == DbChangeKind.Changed && c.Name == "customer" && c.Fields.Any(f => f.Key == "comment"));
        Assert.Equal(DbModel.Column(customer, "credit"), plan.ElementOf(plan.Changes.Single(c => c.Name == "credit")));
        Assert.DoesNotContain(plan.Changes, c => c.Name == "client");

        var undoBefore = m.History.UndoDescription;
        var chosen = plan.Changes.Where(c => c.Name is "phone" or "credit" or "warehouse" or "tags" or "customer" or "product").ToList();
        var result = DbRefresh.Apply(m, plan, chosen);
        Assert.Equal(chosen.Count, result.Applied);
        Assert.NotNull(DbModel.Column(customer, "phone"));
        Assert.Equal("numeric(12,2)", DbModel.Column(customer, "credit")!.Get(FieldKeys.SqlType));
        Assert.Null(DbModel.Column(product, "tags"));
        Assert.Contains(m.OfKind(ElementKind.Table), t => t.Name == "warehouse");
        Assert.NotNull(DbModel.Indexes(product).SingleOrDefault(i => i.Name == "product_tags_gin"));   // not selected: kept
        Assert.Equal("client", customer.Name);                       // the model's name stays
        Assert.Equal("Buyers", customer.Documentation);              // imported comment replaced
        Assert.Equal("Written in Meridian", product.Documentation);  // user documentation kept
        Assert.True(DbModel.InGroup(product, g));                    // groups kept
        Assert.Equal(777, diagram.NodeFor(product)!.Bounds.X);       // layout kept
        Assert.Contains(result.AddedIds, id => m.Find(id)?.Name == "warehouse");
        Assert.Contains(customer.Id, result.ChangedIds);

        m.History.Undo();
        Assert.Equal(undoBefore, m.History.UndoDescription);
        Assert.Null(DbModel.Column(customer, "phone"));
        Assert.NotNull(DbModel.Column(product, "tags"));

        // Nothing changed: an empty plan.
        var same = DbRefresh.Plan(m, r.Database, DdlParser.Parse(DatabaseScripts.Postgres, "postgresql", "shop"));
        Assert.True(same.IsEmpty, string.Join("\n", same.Changes.Select(c => c.Describe())));
    }

    [Fact]
    public void Data_Dictionary_Lists_Columns_With_Keys_References_And_Subject_Areas()
    {
        var (m, r) = DatabaseModelTests.Import(DatabaseScripts.Postgres, "postgresql");
        var order = m.OfKind(ElementKind.Table).Single(t => t.Name == "order");
        DbModel.AddToGroup(m, [order], DbModel.CreateGroup(m, r.Database, "Ordering"));
        var rows = DataDictionary.Rows(m, r.Database);
        var cid = rows.Single(x => x[2] == "order" && x[7] == "customer_id");
        Assert.Equal("FK", cid[11]);
        Assert.Equal("customer.id", cid[12]);
        Assert.Equal("Ordering", rows.First(x => x[2] == "order")[5]);
        var csv = DataDictionary.Csv(m, r.Database);
        Assert.StartsWith("Database,Schema,Table", csv);
        Assert.Contains("Store credit in EUR", csv);
        var xlsx = DataDictionary.Xlsx(m, r.Database);
        using var ms = new MemoryStream(xlsx);
        var sheet = Xlsx.Read(ms);
        Assert.Equal("Database", sheet[0][0]);
    }

    [Theory]
    [InlineData(Meridian.Serialization.Reports.ReportFormat.Html)]
    [InlineData(Meridian.Serialization.Reports.ReportFormat.Docx)]
    [InlineData(Meridian.Serialization.Reports.ReportFormat.Pdf)]
    public void Data_Dictionary_Report_Lists_Tables_Columns_Keys_And_Subject_Areas(Meridian.Serialization.Reports.ReportFormat format)
    {
        var (m, r) = DatabaseModelTests.Import(DatabaseScripts.Postgres, "postgresql");
        var order = m.OfKind(ElementKind.Table).Single(t => t.Name == "order");
        DbModel.AddToGroup(m, [order], DbModel.CreateGroup(m, r.Database, "Ordering"));
        var t = Meridian.Serialization.Reports.ReportTemplates.DataDictionary();
        var doc = Meridian.Serialization.Reports.DocResolver.Resolve(m, t);
        Assert.True(doc.Warnings.Count == 0, string.Join("; ", doc.Warnings));
        var html = Meridian.Serialization.Reports.ReportEngine.Preview(m, t, loopLimit: 50, diagrams: false);
        Assert.Contains("customer_id", html);
        Assert.Contains("Store credit in EUR", html);
        Assert.Contains("Ordering", html);
        Assert.Contains("PK", html);
        Assert.Contains("numeric(10,2)", html);
        Assert.True(Meridian.Serialization.Reports.ReportEngine.Generate(m, t, format).Length > 1000);
    }

    [Fact]
    public void Database_Diagram_Renders_Key_Markers_Views_And_Column_To_Column_Foreign_Keys()
    {
        var (m, r) = DatabaseModelTests.Import(DatabaseScripts.Postgres, "postgresql");
        var d = r.Diagrams.Single(x => x.Owner!.Name == "sales");
        var svg = SvgRenderer.Render(m, d);
        Assert.Contains("data-column=", svg);
        Assert.Contains("open_orders", svg);
        Assert.Contains("stroke-dasharray=\"6,4\"", svg);   // the view's dashed outline

        var order = d.Nodes.Single(n => n.Element.Name == "order");
        var lines = d.Nodes.Single(n => n.Element.Name == "order_line");
        var edge = d.Edges.Single(e => e.Source == lines && e.Target == order);
        Bounds B(DiagramNode n) => Notation.EffectiveBounds(m, n, d.DiagramType);
        var route = DbNotation.ColumnRoute(d, edge, B, null);
        Assert.NotNull(route);
        var rowIndex = DbNotation.Rows(lines.Element, DbModel.ForeignKeyColumns(m, lines.Element)).ToList().FindIndex(x => x.Column.Name == "order_id");
        Assert.Equal(DbNotation.RowCenter(B(lines), false, rowIndex), route![0].Y, 3);
        Assert.Equal(DbNotation.RowCenter(B(order), false, 0), route[^1].Y, 3);

        // Keys only / none shrink the shapes.
        var full = B(order).Height;
        DbModel.SetShowColumns(m, d, "keys");
        Assert.True(B(order).Height < full);
        DbModel.SetShowColumns(m, d, "none");
        Assert.Equal(DbNotation.HeaderHeight(false), Notation.PreferredHeight(Notation.Content(m, order, d.DiagramType), NodeShape.Rectangle, Notation.LineHeight));
        m.History.Undo();
        Assert.Equal("keys", DbModel.ShowColumnsOf(order));
    }
}
