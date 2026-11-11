using System.IO.Compression;
using System.Text;
using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;
using Meridian.Serialization.Spreadsheets;

namespace Meridian.Tests;

/// <summary>Spreadsheet (Excel / CSV) import, sync and export for any element kind.</summary>
public class SpreadsheetImportTests
{
    private static SheetTable Table(params string[][] rows) => new() { Name = "T", Rows = rows.Select(r => r.ToList()).ToList() };

    private static SheetTable Csv(string text) => SheetReader.ReadText(text, ',').Sheets[0];

    private static SheetApplyResult Import(ModelRepository m, SheetTable t, SheetMapping map, Element? scope = null)
    {
        var plan = SheetImport.Plan(m, t, map, scope);
        Assert.Empty(plan.Errors);
        return SheetImport.Apply(m, plan);
    }

    private static Element Named(ModelRepository m, string name, ElementKind? kind = null) =>
        m.AllElements.Single(e => e.Name == name && (kind == null || e.Kind == kind));

    // ------------------------------------------------------------------ reading: CSV dialects

    [Fact]
    public void Csv_Detects_Semicolons_Tabs_And_Pipes()
    {
        Assert.Equal(';', SheetReader.DetectDelimiter("Name;Type;Value\nmass;Real;1,5\nspeed;Real;2,0\n"));
        Assert.Equal('\t', SheetReader.DetectDelimiter("Name\tType\nA\tB, C\n"));
        Assert.Equal('|', SheetReader.DetectDelimiter("a|b|c\n1|2|3\n"));
        Assert.Equal(',', SheetReader.DetectDelimiter("\"a;b\",c\n\"x;y\",z\n"));   // delimiters inside quotes do not count
    }

    [Fact]
    public void Csv_Detects_Encodings_And_Allows_Overrides()
    {
        var latin = Encoding.Latin1.GetBytes("Name,Doc\nMoteur,Câble électrique\n");
        var book = SheetReader.ReadDelimited(latin);
        Assert.Equal("windows-1252", book.EncodingName);
        Assert.Equal("Câble électrique", book.Sheets[0].Rows[1][1]);

        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("A\tB\nü\tß\n")).ToArray();
        book = SheetReader.Read("x.tsv", new MemoryStream(utf16));
        Assert.Equal('\t', book.Delimiter);
        Assert.Equal("ß", book.Sheets[0].Rows[1][1]);

        var utf8 = Encoding.UTF8.GetBytes("a;b\n1;2\n");
        book = SheetReader.ReadDelimited(utf8, delimiter: ',', encoding: "iso-8859-1");   // overrides win
        Assert.Equal(',', book.Delimiter);
        Assert.Equal("a;b", book.Sheets[0].Rows[0][0]);
    }

    [Fact]
    public void Csv_Parses_Quotes_Newlines_And_Restores_Formula_Guards()
    {
        var rows = SheetReader.ParseDelimited("Name,Doc\r\n\"A, B\",\"line 1\nline \"\"2\"\"\"\r\nX,'=1+1\n", ',');
        Assert.Equal(3, rows.Count);
        Assert.Equal("A, B", rows[1][0]);
        Assert.Equal("line 1\nline \"2\"", rows[1][1]);
        Assert.Equal("=1+1", rows[2][1]);
    }

    [Fact]
    public void Header_Row_Detection_Skips_Title_Rows()
    {
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "Vehicle parts list", "", "" }, new[] { "", "", "" },
            new[] { "Part Number", "Name", "Qty" }, new[] { "P-1", "Engine", "1" },
        };
        Assert.Equal(2, SheetReader.DetectHeaderRow(rows));
        Assert.Equal(0, SheetReader.DetectHeaderRow([new[] { "Name", "Type" }, new[] { "a", "b" }]));
    }

    [Fact]
    public void Pasted_Cells_Read_As_Tab_Separated()
    {
        var book = SheetReader.ReadText("Name\tDocumentation\nPump\tMoves fluid\n");
        Assert.Equal('\t', book.Delimiter);
        Assert.Equal("Moves fluid", book.Sheets[0].Rows[1][1]);
    }

    // ------------------------------------------------------------------ reading: xlsx

    [Fact]
    public void Xlsx_Reads_Every_Sheet()
    {
        var bytes = Xlsx.Write(new Xlsx.Sheet("Blocks", ["Name"], [["Engine"]]), new Xlsx.Sheet("Parts", ["Name", "Type"], [["engine", "Engine"]]));
        var book = SheetReader.Read("w.xlsx", new MemoryStream(bytes));
        Assert.Equal(["Blocks", "Parts"], book.Sheets.Select(s => s.Name));
        Assert.Equal("Engine", book.Sheets[1].Rows[1][1]);
        Assert.Empty(book.Warnings);
    }

    /// <summary>A hand-written workbook: shared strings, a formula with its cached value, a date, an indent style and an external link.</summary>
    internal static byte[] HandMadeXlsx(string sheetXml, bool externalLink = false, string? sharedStrings = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Put(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }
            Put("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Put("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Put("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                + (externalLink ? "<Relationship Id=\"rId9\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/externalLink\" Target=\"externalLinks/externalLink1.xml\"/>" : "") + "</Relationships>");
            Put("xl/styles.xml", "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><cellXfs count=\"3\"><xf numFmtId=\"0\"/><xf numFmtId=\"14\"/><xf numFmtId=\"0\" applyAlignment=\"1\"><alignment indent=\"2\"/></xf></cellXfs></styleSheet>");
            Put("xl/sharedStrings.xml", sharedStrings ?? "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>Name</t></si><si><r><t>Mass</t></r><r><t xml:space=\"preserve\"> (kg)</t></r><rPh><t>ignored</t></rPh></si><si><t>Engine</t></si></sst>");
            Put("xl/worksheets/sheet1.xml", sheetXml);
            if (externalLink) Put("xl/externalLinks/externalLink1.xml", "<externalLink xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"/>");
        }
        return ms.ToArray();
    }

    private const string SheetXml = "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>"
        + "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"inlineStr\"><is><t>Due</t></is></c></row>"
        + "<row r=\"2\"><c r=\"A2\" t=\"s\" s=\"2\"><v>2</v></c><c r=\"B2\"><f>1000+450</f><v>1450</v></c><c r=\"C2\" s=\"1\"><v>45658</v></c></row>"
        + "<row r=\"4\"><c r=\"C4\" t=\"b\"><v>1</v></c></row>"
        + "</sheetData></worksheet>";

    [Fact]
    public void Xlsx_Uses_Cached_Formula_Values_Dates_Indents_And_Ignores_External_Links()
    {
        var book = SheetReader.Read("hand.xlsx", new MemoryStream(HandMadeXlsx(SheetXml, externalLink: true)));
        var t = book.Sheets[0];
        Assert.Equal(["Name", "Mass (kg)", "Due"], t.Rows[0]);
        Assert.Equal("1450", t.Rows[1][1]);          // cached value, never evaluated
        Assert.Equal("2025-01-01", t.Rows[1][2]);    // date format
        Assert.Equal(2, t.IndentOf(1, 0));           // alignment indent
        Assert.Empty(t.Rows[2]);                     // gap rows keep positions
        Assert.Equal(["", "", "TRUE"], t.Rows[3]);
        Assert.Equal(1, t.FormulaCells);
        Assert.Contains(book.Warnings, w => w.Contains("formula"));
        Assert.Contains(book.Warnings, w => w.Contains("other workbooks"));
    }

    [Fact]
    public void Xlsx_With_A_Dtd_Is_Refused()
    {
        var evil = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c t=\"inlineStr\"><is><t>&e;</t></is></c></row></sheetData></worksheet>";
        Assert.ThrowsAny<System.Xml.XmlException>(() => SheetReader.Read("evil.xlsx", new MemoryStream(HandMadeXlsx(evil))));
    }

    [Fact]
    public void Xlsx_Parts_Are_Read_Through_Zip_Limits()
    {
        // A worksheet that inflates beyond the per-part limit (a zip bomb in miniature: it compresses to almost nothing).
        var huge = "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" + new string(' ', (int)SheetLimits.MaxPartBytes + 1024) + "</sheetData></worksheet>";
        var bytes = HandMadeXlsx(huge);
        Assert.True(bytes.Length < 1024 * 1024);
        var ex = Assert.Throws<FormatException>(() => SheetReader.Read("bomb.xlsx", new MemoryStream(bytes)));
        Assert.IsType<InvalidDataException>(ex.InnerException);
        Assert.Contains("when unpacked", ex.Message);
    }

    [Fact]
    public void Row_And_Cell_Limits()
    {
        var sb = new StringBuilder("Name\n");
        for (var i = 0; i <= SheetLimits.MaxRows + 1; i++) sb.Append('r').Append(i).Append('\n');
        var ex = Assert.Throws<FormatException>(() => SheetReader.ReadText(sb.ToString(), ','));
        Assert.Contains("100,000", ex.Message);

        var book = SheetReader.ReadText("Name,Doc\nA," + new string('x', SheetLimits.MaxCellChars + 10) + "\n", ',');
        Assert.Equal(SheetLimits.MaxCellChars, book.Sheets[0].Rows[1][1].Length);
        Assert.Contains(book.Warnings, w => w.Contains("cut"));

        var big = new MemoryStream(new byte[SheetLimits.MaxFileBytes + 1]);
        Assert.Throws<FormatException>(() => SheetReader.Read("big.csv", big));
    }

    // ------------------------------------------------------------------ mapping per kind

    [Fact]
    public void Blocks_Go_Into_Package_Paths_Created_On_Demand()
    {
        var m = new ModelRepository("M");
        var map = new SheetMapping { Kind = "Block", Columns = [new("Name", SheetRole.Name), new("Package", SheetRole.Owner), new("Doc", SheetRole.Documentation), new("Stereo", SheetRole.Stereotypes)] };
        var r = Import(m, Csv("Name,Package,Doc,Stereo\nEngine,Structure::Powertrain,Makes torque,hardware\nWheel,Structure/Chassis,,\nRadio,,,\n"), map);
        Assert.Equal(3, r.Added);
        var engine = Named(m, "Engine");
        Assert.Equal("Structure::Powertrain::Engine", engine.QualifiedName);
        Assert.Equal("Makes torque", engine.Documentation);
        Assert.Equal(["hardware"], engine.Stereotypes);
        Assert.Equal("Structure::Chassis::Wheel", Named(m, "Wheel").QualifiedName);
        Assert.Single(m.OfKind(ElementKind.Package), p => p.Name == "Structure");   // shared, not duplicated
        Assert.Same(m.Root, Named(m, "Radio").Owner);
    }

    [Fact]
    public void Part_Value_Properties_And_Ports_Resolve_Types_And_Owners()
    {
        var m = SampleModels.Vehicle();
        var parts = new SheetMapping { Kind = SheetKinds.PartProperty, KeyMatch = SheetKeyMatch.QualifiedName, Columns = [new("Block", SheetRole.Owner), new("Name", SheetRole.Name), new("Type", SheetRole.Type), new("Mult", SheetRole.Multiplicity)] };
        Import(m, Csv("Block,Name,Type,Mult\nVehicle,spare,BrakeSystem,0..1\nEngine,injectors,Structure::FuelTank,4\n"), parts);
        var spare = Named(m, "spare");
        Assert.Equal("Vehicle", spare.Owner!.Name);
        Assert.Equal(Named(m, "BrakeSystem").Id, spare.TypeRef);
        Assert.Equal("0..1", spare.Multiplicity);
        Assert.Equal("4", Named(m, "injectors").Multiplicity);

        var values = SheetPresets.ValueProperties();
        Import(m, Csv("Block,Name,Type,Value,Unit\nVehicle,wheelbase,Real,2.7 m,\nEngine,rpmMax,Integer,6500,rpm\n"), values);
        var wb = Named(m, "wheelbase");
        Assert.Equal(("2.7", "m", "Real"), (wb.Get(FieldKeys.DefaultValue), wb.Get(ExtFieldKeys.Unit), wb.TypeRef));
        Assert.Equal("rpm", Named(m, "rpmMax").Get(ExtFieldKeys.Unit));

        var ports = new SheetMapping { Kind = "Port", KeyMatch = SheetKeyMatch.QualifiedName, Columns = [new("Owner", SheetRole.Owner), new("Name", SheetRole.Name), new("Type", SheetRole.Type), new("Direction", SheetRole.Field, FieldKeys.Direction)] };
        Import(m, Csv("Owner,Name,Type,Direction\nBrakeSystem,fluidIn,FuelInterface,in\n"), ports);
        var port = Named(m, "fluidIn");
        Assert.Equal(ElementKind.Port, port.Kind);
        Assert.Equal(Named(m, "FuelInterface").Id, port.TypeRef);
        Assert.Equal("in", port.Get(FieldKeys.Direction));
    }

    [Fact]
    public void Behaviour_Requirement_Signal_And_Database_Kinds()
    {
        var m = new ModelRepository("M");
        var byKind = new SheetMapping
        {
            Kind = "", KeyMatch = SheetKeyMatch.QualifiedName,
            Columns = [new("Path", SheetRole.Key), new("Kind", SheetRole.Kind), new("Type", SheetRole.Type), new("Doc", SheetRole.Documentation)],
        };
        var csv = "Path,Kind,Type,Doc\n"
                  + "Interfaces::Brake,Signal,,brake pressed\n"
                  + "Interfaces::BusIf,Interface block,,\n"
                  + "Ops::Driver,Actor,,\n"
                  + "Ops::Drive,Use case,,\n"
                  + "Ops::Start,Activity,,\n"
                  + "Ops::Start::Crank,Action,,\n"
                  + "Ops::Modes,State machine,,\n"
                  + "Ops::Modes::Idle,State,,\n"
                  + "Ops::Limits,Constraint block,,\n"
                  + "Ops::T1,Test case,,\n"
                  + "Ops::R1,Requirement,,\n"
                  + "Db::Orders,Table,,\n"
                  + "Db::Orders::id,Column,bigint,\n";
        var r = Import(m, Csv(csv), byKind);
        Assert.Equal(13, r.Added);
        var brake = Named(m, "Brake");
        Assert.Equal((ElementKind.Class, "signal"), (brake.Kind, brake.Stereotypes.Single()));
        Assert.Equal(ElementKind.Action, Named(m, "Crank").Kind);
        Assert.Equal("Start", Named(m, "Crank").Owner!.Name);
        Assert.Equal(ElementKind.State, Named(m, "Idle").Kind);
        Assert.Equal("bigint", Named(m, "id").Get(FieldKeys.SqlType));
        Assert.Equal(ElementKind.Table, Named(m, "id").Owner!.Kind);
    }

    [Fact]
    public void Stereotype_Applied_Kind_And_Tagged_Values()
    {
        var m = new ModelRepository("M");
        var map = new SheetMapping { Kind = "Block", Stereotype = "hardware", Columns = [new("Name", SheetRole.Name), new("Vendor", SheetRole.Tag, "vendor"), new("Safety", SheetRole.Attribute, "Safety")] };
        Import(m, Csv("Name,Vendor,Safety\nECU,Bosch,ASIL B\n"), map);
        var ecu = Named(m, "ECU");
        Assert.Equal(["hardware"], ecu.Stereotypes);
        Assert.Equal("Bosch", ecu.Get("tag:vendor"));
        Assert.Equal("ASIL B", ecu.Get("attr:Safety"));
        // A plain block of the same name is not "a hardware block": it is not matched or offered for deletion.
        m.Create(m.Root, ElementKind.Block, "Plain");
        var plan = SheetImport.Plan(m, Csv("Name,Vendor,Safety\nECU,Bosch,ASIL B\n"), map);
        Assert.Empty(plan.Rows);
        Assert.Equal(1, plan.Unchanged);
    }

    // ------------------------------------------------------------------ hierarchy

    [Fact]
    public void Hierarchy_From_Indentation_Level_And_Parent_Column()
    {
        var m = new ModelRepository("M");
        var reqs = new SheetMapping { Kind = "Requirement", KeyMatch = SheetKeyMatch.ExternalId, KeyField = FieldKeys.RequirementId, IndentHierarchy = true, Columns = [new("Id", SheetRole.Key), new("Name", SheetRole.Name)] };
        Import(m, Csv("Id,Name\nR-1,Top\nR-2,  Child\nR-3,    Grandchild\nR-4,Other\n"), reqs);
        Assert.Equal("Top", Named(m, "Child").Owner!.Name);
        Assert.Equal("Child", Named(m, "Grandchild").Owner!.Name);
        Assert.Same(m.Root, Named(m, "Other").Owner);

        var pkgs = new SheetMapping { Kind = "Package", KeyMatch = SheetKeyMatch.Name, Columns = [new("Level", SheetRole.Level), new("Name", SheetRole.Name)] };
        Import(m, Csv("Level,Name\n1,System\n2,Sub A\n3,Leaf\n2,Sub B\n"), pkgs);
        Assert.Equal("Sub A", Named(m, "Leaf").Owner!.Name);
        Assert.Equal("System", Named(m, "Sub B").Owner!.Name);

        var parent = new SheetMapping { Kind = "Requirement", KeyMatch = SheetKeyMatch.ExternalId, KeyField = FieldKeys.RequirementId, Columns = [new("Id", SheetRole.Key), new("Name", SheetRole.Name), new("Parent", SheetRole.Parent)] };
        Import(m, Csv("Id,Name,Parent\nQ-2,Kid,Q-1\nQ-1,Mum,\nQ-3,Adopted,R-1\n"), parent);
        Assert.Equal("Mum", Named(m, "Kid").Owner!.Name);
        Assert.Equal("Top", Named(m, "Adopted").Owner!.Name);   // existing parent by requirement id
    }

    [Fact]
    public void Circular_Hierarchy_Is_An_Error()
    {
        var m = new ModelRepository("M");
        var map = new SheetMapping { Kind = "Requirement", KeyMatch = SheetKeyMatch.ExternalId, KeyField = FieldKeys.RequirementId, Columns = [new("Id", SheetRole.Key), new("Name", SheetRole.Name), new("Parent", SheetRole.Parent)] };
        var plan = SheetImport.Plan(m, Csv("Id,Name,Parent\nA,a,B\nB,b,A\nC,c,\n"), map);
        Assert.Equal(2, plan.Errors.Count(e => e.Message.Contains("Circular")));
        Assert.Single(plan.Rows);   // only C
        Assert.Equal("C", plan.Rows[0].Key);
    }

    // ------------------------------------------------------------------ relationships

    [Fact]
    public void Relationships_By_Requirement_Id_Name_And_Qualified_Name()
    {
        var m = SampleModels.Vehicle();
        var map = new SheetMapping
        {
            Kind = "Block", KeyMatch = SheetKeyMatch.Name,
            Columns =
            [
                new("Name", SheetRole.Name),
                new("Satisfies", SheetRole.Relationship) { Relationship = "Satisfy" },
                new("Traces to", SheetRole.Relationship) { Relationship = "Trace", Match = SheetTargetMatch.QualifiedName },
                new("Allocated from", SheetRole.Relationship) { Relationship = "Allocate", Incoming = true },
            ],
        };
        var plan = SheetImport.Plan(m, Csv("Name,Satisfies,Traces to,Allocated from\nGearbox,REQ-002; REQ-003,Structure::Engine,Start Vehicle\n"), map);
        Assert.Empty(plan.Errors);
        SheetImport.Apply(m, plan);
        var gearbox = Named(m, "Gearbox");
        Assert.Equal(["Acceleration", "Range"], m.Outgoing(gearbox, ElementKind.Satisfy).Select(r => r.Target.Name).Order());
        Assert.Equal("Engine", m.Outgoing(gearbox, ElementKind.Trace).Single().Target.Name);
        Assert.Equal("Start Vehicle", m.Incoming(gearbox, ElementKind.Allocate).Single().Source.Name);

        // Re-import without REQ-003 removes that satisfy link.
        plan = SheetImport.Plan(m, Csv("Name,Satisfies,Traces to,Allocated from\nGearbox,REQ-002,Structure::Engine,Start Vehicle\n"), map);
        var change = Assert.Single(plan.Rows, r => r.Kind == SheetChangeKind.Changed);
        Assert.Equal("Satisfies", Assert.Single(change.Fields).Label);
        Assert.All(plan.Rows.Where(r => r.Kind == SheetChangeKind.Removed), r => Assert.False(r.Accept));   // the other blocks are only offered
        SheetImport.Apply(m, plan);
        Assert.Single(m.Outgoing(gearbox, ElementKind.Satisfy));
    }

    [Fact]
    public void Relationship_Targets_Can_Be_New_Rows()
    {
        var m = new ModelRepository("M");
        var map = new SheetMapping
        {
            Kind = "", KeyMatch = SheetKeyMatch.ExternalId,
            Columns = [new("Id", SheetRole.Key), new("Kind", SheetRole.Kind), new("Name", SheetRole.Name), new("Satisfies", SheetRole.Relationship) { Relationship = "Satisfy" }],
        };
        Import(m, Csv("Id,Kind,Name,Satisfies\nB1,Block,Pump,R1\nR1,Requirement,Flow,\n"), map);
        Assert.Equal("Flow", m.Outgoing(Named(m, "Pump"), ElementKind.Satisfy).Single().Target.Name);
    }

    // ------------------------------------------------------------------ errors

    [Fact]
    public void Row_Errors_Skip_The_Row_With_Clear_Messages_And_Export_As_Csv()
    {
        var m = SampleModels.Vehicle();
        var map = new SheetMapping
        {
            Kind = SheetKinds.PartProperty, KeyMatch = SheetKeyMatch.QualifiedName,
            Columns = [new("Block", SheetRole.Owner), new("Name", SheetRole.Name), new("Type", SheetRole.Type), new("Mult", SheetRole.Multiplicity),
                       new("Satisfies", SheetRole.Relationship) { Relationship = "Satisfy" }],
        };
        var csv = "Block,Name,Type,Mult,Satisfies\n"
                  + "Vehicle,a,Gizmo,1,\n"           // unknown type
                  + "Vehicle,b,Engine,2..1,\n"       // bad multiplicity
                  + "Vehicle,c,Engine,1,REQ-999\n"   // missing target
                  + "Nowhere,d,Engine,1,\n"          // owner not found (properties cannot live in packages)
                  + "Vehicle,e,Engine,1,\n";         // fine
        var plan = SheetImport.Plan(m, Csv(csv), map);
        Assert.Contains(plan.Errors, e => e.Row == 2 && e.Message.Contains("Unknown type 'Gizmo'"));
        Assert.Contains(plan.Errors, e => e.Row == 3 && e.Message.Contains("Bad multiplicity"));
        Assert.Contains(plan.Errors, e => e.Row == 4 && e.Message.Contains("REQ-999") && e.Message.Contains("not found"));
        Assert.Contains(plan.Errors, e => e.Row == 5 && e.Message.Contains("Owner 'Nowhere' not found"));
        Assert.Equal("e", Assert.Single(plan.Rows, r => r.Kind == SheetChangeKind.Added).Name);
        Assert.Equal(["Gizmo"], plan.MissingTypes);
        var csvOut = plan.ErrorsCsv();
        Assert.StartsWith("Row,Key,Column,Message", csvOut);
        Assert.Contains("Gizmo", csvOut);

        // Creating missing types fixes the first row.
        map.CreateMissingTypes = true;
        plan = SheetImport.Plan(m, Csv(csv), map);
        Assert.DoesNotContain(plan.Errors, e => e.Row == 2);
        var result = SheetImport.Apply(m, plan);
        Assert.Equal(1, result.TypesCreated);
        Assert.Equal(ElementKind.Block, Named(m, "Gizmo").Kind);
        Assert.Equal(Named(m, "Gizmo").Id, m.AllElements.Single(e => e.Name == "a" && e.Kind == ElementKind.Property).TypeRef);
    }

    [Fact]
    public void Unknown_Kind_And_Bad_Level_Are_Errors()
    {
        var m = new ModelRepository("M");
        var map = new SheetMapping { Kind = "", Columns = [new("Kind", SheetRole.Kind), new("Name", SheetRole.Name), new("Level", SheetRole.Level)] };
        var plan = SheetImport.Plan(m, Csv("Kind,Name,Level\nWidget,x,1\nBlock,y,two\nBlock,z,1\n"), map);
        Assert.Contains(plan.Errors, e => e.Message.Contains("Unknown element kind 'Widget'"));
        Assert.Contains(plan.Errors, e => e.Message.Contains("not a whole number"));
        Assert.Single(plan.Rows);
    }

    // ------------------------------------------------------------------ sync

    [Fact]
    public void Reimport_Updates_Instead_Of_Duplicating_And_Offers_Removals_Unticked()
    {
        var m = new ModelRepository("M");
        var map = new SheetMapping { Kind = "Block", KeyMatch = SheetKeyMatch.ExternalId, Columns = [new("PN", SheetRole.Key), new("Name", SheetRole.Name), new("Doc", SheetRole.Documentation)] };
        Import(m, Csv("PN,Name,Doc\nP1,Engine,v1\nP2,Wheel,\nP3,Seat,\n"), map);
        Assert.Equal("P1", Named(m, "Engine").Get(SheetMapping.DefaultKeyField));   // source key stored

        var plan = SheetImport.Plan(m, Csv("PN,Name,Doc\nP1,Motor,v2\nP2,Wheel,\nP4,Mirror,\n"), map);
        Assert.Equal(1, plan.Count(SheetChangeKind.Added));
        Assert.Equal(1, plan.Count(SheetChangeKind.Changed));
        Assert.Equal(1, plan.Unchanged);
        var removal = Assert.Single(plan.Rows, r => r.Kind == SheetChangeKind.Removed);
        Assert.Equal("Seat", removal.Name);
        Assert.False(removal.Accept);
        var changed = plan.Rows.Single(r => r.Kind == SheetChangeKind.Changed);
        Assert.Equal(["Name", "Doc"], changed.Fields.Select(f => f.Label));

        var before = m.OfKind(ElementKind.Block).Count();
        var revision = m.History.CanUndo;
        var r = SheetImport.Apply(m, plan);
        Assert.Equal((1, 1, 0), (r.Added, r.Changed, r.Removed));
        Assert.Equal(before + 1, m.OfKind(ElementKind.Block).Count());
        Assert.Equal("v2", Named(m, "Motor").Documentation);
        Assert.Contains(m.OfKind(ElementKind.Block), b => b.Name == "Seat");

        // Ticking the removal deletes it.
        plan = SheetImport.Plan(m, Csv("PN,Name,Doc\nP1,Motor,v2\nP2,Wheel,\nP4,Mirror,\n"), map);
        plan.Rows.Single(x => x.Kind == SheetChangeKind.Removed).Accept = true;
        Assert.Equal(1, SheetImport.Apply(m, plan).Removed);
        Assert.DoesNotContain(m.OfKind(ElementKind.Block), b => b.Name == "Seat");
        Assert.True(revision);
    }

    [Fact]
    public void Apply_Is_One_Undo_Step()
    {
        var m = SampleModels.Vehicle();
        var count = m.Count;
        var map = SheetPresets.ValueProperties();
        var plan = SheetImport.Plan(m, Csv("Block,Name,Type,Value,Unit\nVehicle,length,Real,4.5,m\nVehicle,mass,Real,1500,kg\nNew Pkg::Thing,x,Real,1,\n"), map);
        Assert.Contains(plan.Errors, e => e.Message.Contains("New Pkg::Thing"));   // properties cannot be owned by new packages
        SheetImport.Apply(m, plan);
        Assert.NotEqual(count, m.Count);
        Assert.Equal("1500", m.AllElements.Single(e => e.QualifiedName == "Structure::Vehicle::mass").Get(FieldKeys.DefaultValue));
        m.History.Undo();
        Assert.Equal(count, m.Count);
        Assert.Equal("1450", m.AllElements.Single(e => e.QualifiedName == "Structure::Vehicle::mass").Get(FieldKeys.DefaultValue));
    }

    [Fact]
    public void Unticked_Rows_Are_Not_Applied()
    {
        var m = new ModelRepository("M");
        var map = new SheetMapping { Kind = "Block", KeyMatch = SheetKeyMatch.Name, Columns = [new("Name", SheetRole.Name)] };
        var plan = SheetImport.Plan(m, Csv("Name\nA\nB\n"), map);
        plan.Rows[1].Accept = false;
        Assert.Equal(1, SheetImport.Apply(m, plan).Added);
        Assert.Single(m.OfKind(ElementKind.Block));
    }

    // ------------------------------------------------------------------ bills of materials

    [Fact]
    public void Parts_List_Builds_Part_Properties_And_Updates_Quantities()
    {
        var m = SampleModels.Vehicle();
        var structure = Named(m, "Structure", ElementKind.Package);
        var bom = SheetPresets.PartsList();
        var csv = "Level,Part Number,Name,Description,Qty,Mass,Material,Supplier\n"
                  + "1,V-100,Vehicle,,1,,,\n"
                  + "2,C-200,Chassis,Frame,1,320 kg,Steel,Acme\n"
                  + "3,W-300,Wheel,,4,18 kg,Aluminium,\n"
                  + "2,E-400,Engine,,1,,,\n"
                  + "3,W-300,Wheel,,1,18 kg,Aluminium,\n";   // the same wheel also used by the engine (a pulley, say)
        var plan = SheetImport.Plan(m, Csv(csv), bom, structure);
        Assert.Empty(plan.Errors);
        // Vehicle and Engine exist (matched by unique name, no part numbers yet); Chassis and Wheel are new.
        Assert.Equal(2, plan.Count(SheetChangeKind.Added));
        SheetImport.Apply(m, plan);
        var vehicle = Named(m, "Vehicle", ElementKind.Block);
        var chassis = Named(m, "Chassis", ElementKind.Block);
        var wheel = Named(m, "Wheel", ElementKind.Block);
        Assert.Equal("W-300", wheel.Get("tag:Part Number"));
        Assert.Equal("Steel", chassis.Get("tag:Material"));
        Assert.Equal("320", chassis.OwnedElements.Single(p => p.Name == "mass").Get(FieldKeys.DefaultValue));
        var wheelInChassis = chassis.OwnedElements.Single(p => p.Kind == ElementKind.Property && p.TypeRef == wheel.Id);
        Assert.Equal("4", wheelInChassis.Multiplicity);
        Assert.Single(m.Outgoing(chassis, ElementKind.Composition), c => c.Target == wheel);
        Assert.Contains(vehicle.OwnedElements, p => p.TypeRef == chassis.Id);
        Assert.Contains(Named(m, "Engine", ElementKind.Block).OwnedElements, p => p.TypeRef == wheel.Id);

        // Re-import: five wheels, and the engine no longer uses one.
        var edited = csv.Replace("3,W-300,Wheel,,4,", "3,W-300,Wheel,,5,").Replace("3,W-300,Wheel,,1,18 kg,Aluminium,\n", "");
        plan = SheetImport.Plan(m, Csv(edited), bom, structure);
        Assert.Empty(plan.Errors);
        var change = Assert.Single(plan.Rows, r => r.Kind == SheetChangeKind.Changed);
        Assert.Equal("Wheel", change.Name);
        Assert.Equal("Part of", Assert.Single(change.Fields).Label);
        SheetImport.Apply(m, plan);
        Assert.Equal("5", wheelInChassis.Multiplicity);
        Assert.DoesNotContain(Named(m, "Engine", ElementKind.Block).OwnedElements, p => p.TypeRef == wheel.Id);
    }

    [Fact]
    public void Part_Of_Column_Adds_A_Part_Property()
    {
        var m = SampleModels.Vehicle();
        var map = new SheetMapping { Kind = "Block", KeyMatch = SheetKeyMatch.Name, Columns = [new("Name", SheetRole.Name), new("Part of", SheetRole.PartOf), new("Qty", SheetRole.Multiplicity)] };
        Import(m, Csv("Name,Part of,Qty\nSeat,Vehicle,5\n"), map, Named(m, "Structure", ElementKind.Package));
        var seat = Named(m, "Seat");
        var usage = Named(m, "Vehicle", ElementKind.Block).OwnedElements.Single(p => p.TypeRef == seat.Id);
        Assert.Equal(("seat", "5"), (usage.Name, usage.Multiplicity));
    }

    // ------------------------------------------------------------------ presets

    [Fact]
    public void Presets_Round_Trip_As_Json_And_Are_Stored_Per_Project()
    {
        Assert.Equal(["Requirements", "Parts list (BOM)", "Interfaces / signals", "Value properties"], SheetPresets.BuiltIn().Select(p => p.Name));
        var bom = SheetPresets.PartsList();
        var back = SheetMapping.FromJson(bom.ToJson());
        Assert.Equal(bom.ToJson(), back.ToJson());
        Assert.Contains("\"role\": \"Level\"", bom.ToJson());

        var m = new ModelRepository("M");
        bom.Name = "My BOM";
        SheetPresets.Save(m, bom);
        SheetPresets.Save(m, SheetPresets.Requirements());
        Assert.Equal(["My BOM", "Requirements"], SheetPresets.Project(m).Select(p => p.Name));
        SheetPresets.Delete(m, "Requirements");
        Assert.Equal("My BOM", Assert.Single(SheetPresets.Project(m)).Name);
        m.History.Undo();
        Assert.Equal(2, SheetPresets.Project(m).Count);
        Assert.Throws<FormatException>(() => SheetMapping.FromJson("{ not json"));
    }

    [Fact]
    public void Auto_Mapping_Uses_The_Preset_Then_Header_Names()
    {
        var mapped = SheetPresets.Requirements().AutoMap(["Id", "Name", "Description", "Attr: Safety", "Tag: Owner team", "Satisfied By", "Whatever"]);
        Assert.Equal([SheetRole.Key, SheetRole.Name, SheetRole.Documentation, SheetRole.Attribute, SheetRole.Tag, SheetRole.Relationship, SheetRole.Ignore], mapped.Columns.Select(c => c.Role));
        Assert.Equal("Safety", mapped.Columns[3].Name);
        Assert.True(mapped.Columns[5].Incoming);
        Assert.Equal(SheetKinds.ById("Signal"), SheetKinds.Parse("«signal»"));
        Assert.Equal(SheetKinds.PartProperty, SheetKinds.Parse("part")!.Id);
        Assert.Equal(SheetKinds.ValueProperty, SheetKinds.Parse("Value property")!.Id);
    }

    // ------------------------------------------------------------------ export and round trip

    [Theory]
    [InlineData("Requirements")]
    [InlineData("Parts list (BOM)")]
    [InlineData("Interfaces / signals")]
    [InlineData("Value properties")]
    public void Export_Then_Import_Reports_No_Changes(string preset)
    {
        var m = SampleModels.Vehicle();
        var map = SheetPresets.BuiltIn().Single(p => p.Name == preset);
        if (preset == "Parts list (BOM)")
        {
            // Give the blocks part numbers first (the sample has none).
            var n = 100;
            foreach (var b in m.OfKind(ElementKind.Block).ToList()) m.Set(b, "tag:Part Number", "P-" + n++);
        }
        var sheet = SheetExport.ToSheet(m, map);
        Assert.NotEmpty(sheet.Rows);
        var book = SheetReader.Read("rt.xlsx", new MemoryStream(Xlsx.Write(sheet)));
        var plan = SheetImport.Plan(m, book.Sheets[0], map);
        Assert.Empty(plan.Errors);
        Assert.Empty(plan.Rows.Where(r => r.Kind != SheetChangeKind.Removed).Select(r => $"{r.Name}: {string.Join(", ", r.Fields.Select(f => $"{f.Label} {f.Old} → {f.New}"))}"));
        Assert.Empty(plan.Rows);

        // Same through CSV.
        var csv = SheetExport.ToCsv(sheet);
        plan = SheetImport.Plan(m, SheetReader.Read("rt.csv", new MemoryStream(Encoding.UTF8.GetBytes(csv))).Sheets[0], map);
        Assert.Empty(plan.Rows);
    }

    [Fact]
    public void Round_Trip_Picks_Up_Edits_Made_In_Excel()
    {
        var m = SampleModels.Vehicle();
        var map = SheetPresets.ValueProperties();
        var sheet = SheetExport.ToSheet(m, map);
        var rows = sheet.Rows.Select(r => r.ToList()).ToList();
        var mass = rows.FindIndex(r => r[0] == "Structure::Vehicle" && r[1] == "mass");
        Assert.Equal(("1450", "kg"), (rows[mass][3], rows[mass][4]));
        rows[mass][3] = "1.39";
        rows[mass][4] = "t";
        var edited = Xlsx.Write(new Xlsx.Sheet("V", sheet.Header, rows.Select(r => (IReadOnlyList<string>)r).ToList()));
        var plan = SheetImport.Plan(m, SheetReader.Read("e.xlsx", new MemoryStream(edited)).Sheets[0], map);
        var change = Assert.Single(plan.Rows);
        Assert.Equal(["Value", "Unit"], change.Fields.Select(f => f.Label));
        SheetImport.Apply(m, plan);
        var p = m.AllElements.Single(e => e.QualifiedName == "Structure::Vehicle::mass");
        Assert.Equal(("1.39", "t"), (p.Get(FieldKeys.DefaultValue), p.Get(ExtFieldKeys.Unit)));
    }

    [Fact]
    public void Export_Of_Selected_Elements_Or_A_Query()
    {
        var m = SampleModels.Vehicle();
        var map = new SheetMapping { Kind = "Block", KeyMatch = SheetKeyMatch.ElementId, Columns = [new("Id", SheetRole.Key), new("Name", SheetRole.Name), new("Owner", SheetRole.Owner), new("Satisfies", SheetRole.Relationship) { Relationship = "Satisfy" }] };
        var query = ModelQuery.Parse("kind:Block where name contains \"e\"").Run(m);
        var sheet = SheetExport.ToSheet(m, map, query);
        Assert.Equal(query.Count, sheet.Rows.Count);
        var engine = sheet.Rows.Single(r => r[1] == "Engine");
        Assert.Equal(Named(m, "Engine", ElementKind.Block).Id, engine[0]);
        Assert.Equal("Structure", engine[2]);
        Assert.Equal("REQ-005", engine[3]);
        // Formula-like text is neutralised in CSV.
        m.Set(Named(m, "Engine", ElementKind.Block), FieldKeys.Name, "=HYPERLINK(\"x\")");
        Assert.Contains("'=HYPERLINK", SheetExport.ToCsv(m, map, query));
    }

    [Fact]
    public void Large_Sheets_Plan_And_Apply_Without_Quadratic_Lookups()
    {
        var m = new ModelRepository("M");
        var sb = new StringBuilder("Id,Name,Parent,Traces to\n");
        const int n = 20_000;
        for (var i = 0; i < n; i++) sb.Append("B").Append(i).Append(",Block ").Append(i).Append(',').Append(i % 100 == 0 ? "" : "B" + (i / 100 * 100)).Append(',').Append(i > 0 ? "B0" : "").Append('\n');
        var map = new SheetMapping
        {
            Kind = "Block", KeyMatch = SheetKeyMatch.ExternalId,
            Columns = [new("Id", SheetRole.Key), new("Name", SheetRole.Name), new("Parent", SheetRole.Parent), new("Traces to", SheetRole.Relationship) { Relationship = "Trace" }],
        };
        var table = Csv(sb.ToString());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var plan = SheetImport.Plan(m, table, map);
        Assert.Empty(plan.Errors);
        Assert.Equal(n, plan.Count(SheetChangeKind.Added));
        SheetImport.Apply(m, plan);
        Assert.Equal(n, m.OfKind(ElementKind.Block).Count());
        var again = SheetImport.Plan(m, table, map);
        Assert.Empty(again.Rows);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Requirements_Preset_Matches_The_Existing_Requirements_Sheet()
    {
        var m = SampleModels.Vehicle();
        var sheet = RequirementsSheet.ToSheet(m);
        var table = new SheetTable { Name = "R", Rows = new[] { sheet.Header.ToList() }.Concat(sheet.Rows.Select(r => r.ToList())).ToList() };
        var map = SheetPresets.Requirements().AutoMap(sheet.Header);
        var plan = SheetImport.Plan(m, table, map);
        Assert.Empty(plan.Errors);
        Assert.Empty(plan.Rows);
        table.Rows[2][2] = "Changed text";
        plan = SheetImport.Plan(m, table, map);
        Assert.Equal("Text", Assert.Single(Assert.Single(plan.Rows).Fields).Label);
    }
}
