using Meridian.Core.Analysis;
using Meridian.Core.Instances;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Core.Validation;
using Meridian.Serialization;
using Meridian.Serialization.Reports;

namespace Meridian.Tests;

public class InstanceModelTests
{
    internal static Element Block(ModelRepository m, string name) => m.OfKind(ElementKind.Block).Single(b => b.Name == name);
    internal static Element Prop(Element owner, string name) => owner.OwnedElements.Single(p => p.Kind == ElementKind.Property && p.Name == name);

    [Fact]
    public void Create_instance_of_builds_the_whole_tree_in_one_undo_step()
    {
        var m = SampleModels.Vehicle();
        var vehicle = Block(m, "Vehicle");
        var before = m.Count;
        var v1 = InstanceModel.CreateInstanceTree(m, vehicle);

        Assert.Equal("vehicle1", v1.Name);
        Assert.Equal("Structure", v1.Owner!.Name);   // next to the block
        Assert.Same(vehicle, Assert.Single(InstanceModel.Classifiers(m, v1)));
        Assert.Equal("vehicle1 : Vehicle", InstanceModel.Label(m, v1));

        // Value properties with defaults get slots (in the property's unit); parts get nested instances, slots and links.
        var mass = InstanceModel.SlotFor(v1, Prop(vehicle, "mass"))!;
        Assert.Equal("1450 kg", InstanceModel.DisplayValue(m, mass));
        Assert.Equal(1450, InstanceModel.NumericValue(m, mass));
        Assert.Null(InstanceModel.SlotFor(v1, Prop(vehicle, "accelTime")));   // no default: no slot
        var nested = InstanceModel.NestedInstances(v1).Select(i => i.Name).ToList();
        Assert.Equal(["engine", "tank", "brakes"], nested);
        var engine = InstanceModel.NestedInstances(v1).First();
        Assert.Equal("engine", InstanceModel.DisplayValue(m, InstanceModel.SlotFor(v1, Prop(vehicle, "engine"))!));
        Assert.Equal("160 kW", InstanceModel.DisplayValue(m, InstanceModel.SlotFor(engine, Prop(Block(m, "Engine"), "maxPower"))!));
        var links = m.Relationships.Where(r => r.Kind == ElementKind.Link).ToList();
        Assert.Equal(3, links.Count);
        Assert.All(links, l => Assert.Equal(ElementKind.Composition, InstanceModel.AssociationOf(m, l)!.Kind));
        Assert.Equal("vehicle1.engine", InstanceTable.PathOf(engine));

        m.History.Undo();
        Assert.Equal(before, m.Count);
        Assert.Empty(m.OfKind(ElementKind.InstanceSpecification));

        // A second instance gets the next free name.
        m.History.Redo();
        Assert.Equal("vehicle2", InstanceModel.CreateInstanceTree(m, vehicle).Name);
    }

    [Fact]
    public void Slot_values_are_unit_aware_and_typed()
    {
        var m = SampleModels.Vehicle();
        var vehicle = Block(m, "Vehicle");
        var v1 = InstanceModel.CreateInstanceTree(m, vehicle);
        var mass = Prop(vehicle, "mass");

        InstanceModel.SetValue(m, v1, mass, "1.6 t");
        Assert.Equal("1600 kg", InstanceTable.Cell(m, v1, mass));
        InstanceModel.SetValue(m, v1, mass, "1700");   // no unit: the property's
        Assert.Equal("1700 kg", InstanceTable.Cell(m, v1, mass));
        var bad = Assert.Throws<InvalidOperationException>(() => InstanceModel.SetValue(m, v1, mass, "3 m"));
        Assert.Contains("m does not convert to kg", bad.Message);
        Assert.Throws<InvalidOperationException>(() => InstanceModel.SetValue(m, v1, mass, "heavy"));
        InstanceModel.SetValue(m, v1, mass, "= 1500 + 200");
        Assert.Equal(SlotValueKind.Expression, InstanceModel.KindOf(InstanceModel.SlotFor(v1, mass)!));
        Assert.Equal("= 1500 + 200", InstanceTable.Cell(m, v1, mass));

        // Enumeration literals by name.
        var structure = vehicle.Owner!;
        var colour = m.Create(structure, ElementKind.Enumeration, "Colour");
        m.Create(colour, ElementKind.EnumerationLiteral, "Red");
        var blue = m.Create(colour, ElementKind.EnumerationLiteral, "Blue");
        var paint = m.Create(vehicle, ElementKind.Property, "paint");
        m.Set(paint, FieldKeys.Type, colour.Id);
        InstanceModel.SetValue(m, v1, paint, "blue");
        var ps = InstanceModel.SlotFor(v1, paint)!;
        Assert.Equal((SlotValueKind.Enum, blue.Id), (InstanceModel.KindOf(ps), ps.Get(InstanceFields.Value)));
        Assert.Throws<InvalidOperationException>(() => InstanceModel.SetValue(m, v1, paint, "Green"));

        // Parts: instances by name; empty text removes the slot.
        var engineProp = Prop(vehicle, "engine");
        var e2 = InstanceModel.CreateInstanceTree(m, Block(m, "Engine"), structure, "spareEngine");
        InstanceModel.SetValue(m, v1, engineProp, "spareEngine");
        Assert.Equal([e2], InstanceModel.ReferencedInstances(m, InstanceModel.SlotFor(v1, engineProp)!));
        Assert.Throws<InvalidOperationException>(() => InstanceModel.SetValue(m, v1, engineProp, "nope"));
        InstanceModel.SetValue(m, v1, engineProp, "");
        Assert.Null(InstanceModel.SlotFor(v1, engineProp));
    }

    [Fact]
    public void Duplicate_remaps_references_to_the_copied_parts_and_delete_drops_references()
    {
        var m = SampleModels.Vehicle();
        var vehicle = Block(m, "Vehicle");
        var v1 = InstanceModel.CreateInstanceTree(m, vehicle);
        var copy = InstanceModel.Duplicate(m, v1);
        Assert.Equal("vehicle2", copy.Name);
        var copiedEngine = InstanceModel.NestedInstances(copy).First();
        Assert.Same(copiedEngine, Assert.Single(InstanceModel.ReferencedInstances(m, InstanceModel.SlotFor(copy, Prop(vehicle, "engine"))!)));
        Assert.Equal(6, m.Relationships.Count(r => r.Kind == ElementKind.Link));

        // A reference from another instance to the copy's engine goes when the copy is deleted.
        var other = InstanceModel.CreateInstanceTree(m, vehicle, vehicle.Owner, "other");
        InstanceModel.SetSlot(m, other, Prop(vehicle, "engine"), SlotValueKind.Instance, copiedEngine.Id);
        InstanceModel.Delete(m, copy);
        Assert.False(m.Contains(copy));
        Assert.Null(InstanceModel.SlotFor(other, Prop(vehicle, "engine")));
        m.History.Undo();
        Assert.True(m.Contains(copy));
        Assert.NotNull(InstanceModel.SlotFor(other, Prop(vehicle, "engine")));
    }

    [Fact]
    public void Links_are_typed_by_the_one_matching_association_otherwise_left_to_the_user()
    {
        var m = SampleModels.Vehicle();
        var vehicle = Block(m, "Vehicle");
        var a = InstanceModel.CreateInstanceTree(m, vehicle, vehicle.Owner, "a");
        var tank = InstanceModel.CreateInstanceTree(m, Block(m, "FuelTank"), vehicle.Owner, "t");
        Assert.Equal(ElementKind.Link, ConnectorRules.Default(DiagramType.Object, ElementKind.InstanceSpecification, ElementKind.InstanceSpecification)!.Kind);

        var link = m.Connect(ElementKind.Link, a, tank);
        var options = InstanceModel.TypeLink(m, link);
        Assert.Equal(ElementKind.Composition, Assert.Single(options).Kind);
        Assert.Same(options[0], InstanceModel.AssociationOf(m, link));

        // A second association between Vehicle and FuelTank: the link isn't typed, both are offered.
        m.Connect(ElementKind.Association, vehicle, Block(m, "FuelTank"));
        var link2 = m.Connect(ElementKind.Link, a, tank);
        Assert.Equal(2, InstanceModel.TypeLink(m, link2).Count);
        Assert.Null(InstanceModel.AssociationOf(m, link2));
        Assert.Equal("Links connect two instance specifications", Metamodel.CheckEnds(ElementKind.Link, ElementKind.Block, ElementKind.InstanceSpecification));
    }

    [Fact]
    public void Instance_shapes_show_underlined_name_and_type_with_slots()
    {
        var m = SampleModels.Vehicle();
        var vehicle = Block(m, "Vehicle");
        var v1 = InstanceModel.CreateInstanceTree(m, vehicle);
        var content = Notation.Content(m, v1, DiagramType.Object);
        Assert.Equal("vehicle1 : Vehicle", content.Name);
        Assert.Contains("mass = 1450 kg", content.Compartments.SelectMany(c => c.Lines));
        Assert.Contains(ElementKind.InstanceSpecification, Metamodel.PaletteNodes(DiagramType.Object));
        Assert.Contains(ElementKind.InstanceSpecification, Metamodel.PaletteNodes(DiagramType.BlockDefinition));

        var d = m.CreateDiagram(vehicle.Owner!, DiagramType.Object, "Vehicle instances");
        m.AddNode(d, v1, new Bounds(0, 0, 220, 140));
        m.AddNode(d, InstanceModel.NestedInstances(v1).First(), new Bounds(300, 0, 200, 100));
        Assert.Single(d.Edges);   // the link shows with both ends
        var svg = SvgRenderer.Render(m, d);
        Assert.Contains("text-decoration=\"underline\">vehicle1 : Vehicle</text>", svg);
        Assert.Contains("Vehicle–Engine", svg);   // the link's association
    }
}

public class InstanceValidationTests
{
    private static IReadOnlyList<ValidationIssue> Issues(ModelRepository m, string rule) =>
        new ModelValidator().Validate(m).Where(i => i.RuleId == rule).ToList();

    [Fact]
    public void Flags_stale_slots_type_mismatches_and_missing_required_slots()
    {
        var m = SampleModels.Vehicle();
        var vehicle = InstanceModelTests.Block(m, "Vehicle");
        var v1 = InstanceModel.CreateInstanceTree(m, vehicle);
        Assert.Empty(Issues(m, InstanceRules.StaleSlot));
        Assert.Empty(Issues(m, InstanceRules.TypeMismatch));

        // Missing required slot: a new required value property without a default (computed ones, bound to parametrics, are exempt).
        var colour = m.Create(vehicle, ElementKind.Property, "colour");
        m.Set(colour, FieldKeys.Type, "String");
        var missing = Assert.Single(Issues(m, InstanceRules.MissingSlot), i => i.Element == v1);
        Assert.Contains("'colour'", missing.Message);
        InstanceModel.SetValue(m, v1, colour, "Blue");
        Assert.DoesNotContain(Issues(m, InstanceRules.MissingSlot), i => i.Element == v1);

        // Type mismatch: a literal where a part is expected, a non-number for a value with a unit.
        var massSlot = InstanceModel.SlotFor(v1, InstanceModelTests.Prop(vehicle, "mass"))!;
        m.Set(massSlot, InstanceFields.Value, "heavy");
        var engineSlot = InstanceModel.SlotFor(v1, InstanceModelTests.Prop(vehicle, "engine"))!;
        m.Set(engineSlot, InstanceFields.ValueKind, null);
        m.Set(engineSlot, InstanceFields.Value, "V8");
        var mismatches = Issues(m, InstanceRules.TypeMismatch);
        Assert.Contains(mismatches, i => i.Element == massSlot && i.Message.Contains("expects a number"));
        Assert.Contains(mismatches, i => i.Element == engineSlot && i.Message.Contains("expects instances of Engine"));

        // Stale slot after the instance's type changes: offered for remapping (same name first) or deletion.
        var tankBlock = InstanceModelTests.Block(m, "FuelTank");
        var tankInstance = InstanceModel.NestedInstances(v1).Single(i => i.Name == "tank");
        var capacitySlot = Assert.Single(InstanceModel.Slots(tankInstance));
        var bigTank = m.Create(vehicle.Owner!, ElementKind.Block, "BigTank");
        var bigCapacity = m.Create(bigTank, ElementKind.Property, "capacity");
        m.Set(bigCapacity, ExtFieldKeys.Unit, "L");
        InstanceModel.SetClassifiers(m, tankInstance, [bigTank]);
        Assert.Contains(Issues(m, InstanceRules.StaleSlot), i => i.Element == capacitySlot);
        Assert.True(InstanceModel.IsStale(m, capacitySlot));
        Assert.Same(bigCapacity, InstanceModel.RemapCandidates(m, capacitySlot)[0]);
        InstanceModel.Remap(m, capacitySlot, bigCapacity);
        Assert.DoesNotContain(Issues(m, InstanceRules.StaleSlot), i => i.Element == capacitySlot);
        Assert.Equal("55 L", InstanceModel.DisplayValue(m, capacitySlot));
        _ = tankBlock;
    }
}

public class InstanceExchangeTests
{
    private static (ModelRepository M, Element Instance) Sample()
    {
        var m = SampleModels.Vehicle();
        var vehicle = InstanceModelTests.Block(m, "Vehicle");
        var colour = m.Create(vehicle.Owner!, ElementKind.Enumeration, "Colour");
        m.Create(colour, ElementKind.EnumerationLiteral, "Red");
        m.Create(colour, ElementKind.EnumerationLiteral, "Blue");
        var paint = m.Create(vehicle, ElementKind.Property, "paint");
        m.Set(paint, FieldKeys.Type, colour.Id);
        var label = m.Create(vehicle, ElementKind.Property, "label");
        m.Set(label, FieldKeys.Type, "String");
        var v1 = InstanceModel.CreateInstanceTree(m, vehicle);
        InstanceModel.SetValue(m, v1, InstanceModelTests.Prop(vehicle, "mass"), "1.6 t");
        InstanceModel.SetValue(m, v1, paint, "Blue");
        InstanceModel.SetValue(m, v1, label, "Fleet car, \"blue\"");
        InstanceModel.SetValue(m, v1, InstanceModelTests.Prop(vehicle, "topSpeed"), "= 40 + 10");
        return (m, v1);
    }

    private static void AssertSameInstances(ModelRepository a, ModelRepository b, bool typedLinks = true)
    {
        var x = a.OfKind(ElementKind.InstanceSpecification).OrderBy(i => i.QualifiedName).ToList();
        var y = b.OfKind(ElementKind.InstanceSpecification).OrderBy(i => i.QualifiedName).ToList();
        Assert.Equal(x.Select(i => i.QualifiedName), y.Select(i => i.QualifiedName));
        for (var i = 0; i < x.Count; i++)
        {
            Assert.Equal(InstanceModel.Label(a, x[i]), InstanceModel.Label(b, y[i]));
            Assert.Equal(InstanceModel.Slots(x[i]).Select(s => InstanceModel.SlotLine(a, s)).Order(), InstanceModel.Slots(y[i]).Select(s => InstanceModel.SlotLine(b, s)).Order());
        }
        Assert.Equal(a.Relationships.Count(r => r.Kind == ElementKind.Link), b.Relationships.Count(r => r.Kind == ElementKind.Link));
        if (typedLinks) Assert.All(b.Relationships.Where(r => r.Kind == ElementKind.Link), l => Assert.NotNull(InstanceModel.AssociationOf(b, l)));
    }

    [Fact]
    public void Xmi_round_trip_keeps_instances_slots_nesting_and_links()
    {
        var (m, _) = Sample();
        var xml = XmiExchange.ToXml(m);
        var text = xml.ToString();
        Assert.Contains("uml:InstanceSpecification", text);
        Assert.Contains("uml:Slot", text);
        Assert.Contains("uml:LiteralReal", text);
        Assert.Contains("uml:InstanceValue", text);
        var (back, report) = XmiExchange.Import(xml);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("Instance", StringComparison.OrdinalIgnoreCase) || w.Contains("Link", StringComparison.Ordinal));
        AssertSameInstances(m, back);
        var engine = back.OfKind(ElementKind.InstanceSpecification).Single(i => i.Name == "engine");
        Assert.Equal("vehicle1", engine.Owner!.Name);   // re-nested

        // Without the Meridian extension (as another tool would write it), the standard slots still carry the values.
        foreach (var ext in xml.Descendants(XmiExchange.Meridian + "field").ToList()) ext.Remove();
        var (plain, _) = XmiExchange.Import(xml);
        var v1 = plain.OfKind(ElementKind.InstanceSpecification).Single(i => i.Name == "vehicle1");
        var lines = InstanceModel.Slots(v1).Select(s => InstanceModel.SlotLine(plain, s)).ToList();
        Assert.Contains("mass = 1600", lines);
        Assert.Contains("paint = Blue", lines);
        Assert.Contains("engine = engine", lines);
        Assert.Contains("topSpeed = = 40 + 10", lines);
    }

    [Fact]
    public void SysML_v2_text_round_trips_instances_as_individual_usages()
    {
        var (m, _) = Sample();
        var text = SysMLv2Text.Export(m);
        Assert.Contains("individual part vehicle1 : Vehicle {", text);
        Assert.Contains(":>> mass = 1600 [kg];", text);
        Assert.Contains(":>> paint = Colour::Blue;", text);
        Assert.Contains(":>> engine = engine;", text);
        Assert.Contains("#link dependency from", text);

        var doc = SysMLv2Parser.Parse(text);
        Assert.False(doc.HasErrors, string.Join("\n", doc.Diagnostics));
        var target = new ModelRepository("Copy");
        var r = SysMLv2Sync.Apply(target, target.Root, doc);
        Assert.True(r.Applied, string.Join("\n", r.Diagnostics));
        AssertSameInstances(m, target, typedLinks: false);   // associations are written as comments in SysML v2 text, so a fresh model has none to type links by

        // Re-applying the same text to the model changes nothing.
        var again = SysMLv2Sync.Apply(m, m.Root, SysMLv2Parser.Parse(text));
        Assert.True(again.Changes.Count == 0, string.Join("\n", again.Changes.Select(c => c.Description)));
    }

    [Fact]
    public void Project_file_round_trip_keeps_instances()
    {
        var (m, _) = Sample();
        AssertSameInstances(m, ProjectFile.Deserialize(ProjectFile.Serialize(m)));
    }

    [Fact]
    public void Enterprise_architect_objects_with_run_state_import_as_instances()
    {
        var dir = Directory.CreateTempSubdirectory("meridian-ea-inst-");
        var path = Path.Combine(dir.FullName, "model.qea");
        try
        {
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                c.Open();
                void Exec(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
                Exec("create table t_package (Package_ID int, Name text, Parent_ID int, Notes text)");
                Exec("create table t_object (Object_ID int, Object_Type text, Name text, Alias text, Note text, Package_ID int, Stereotype text, ParentID int, Classifier int, Abstract text, NType int, RunState text)");
                Exec("create table t_attribute (ID int, Object_ID int, Name text, Type text, Classifier text, LowerBound text, UpperBound text, \"Default\" text, Notes text, Scope text, Pos int)");
                Exec("create table t_connector (Connector_ID int, Connector_Type text, SubType text, Stereotype text, Name text, Start_Object_ID int, End_Object_ID int, DestCard text, PDATA2 text)");
                Exec("insert into t_package values (1,'Model',0,''),(2,'Fleet',1,'')");
                Exec("""
                    insert into t_object values
                    (10,'Class','Drone','','',2,'block',0,0,'0',0,''),
                    (11,'Class','Battery','','',2,'block',0,0,'0',0,''),
                    (20,'Object','drone7','','',2,'',0,10,'0',0,'@VAR;Variable=mass;Value=1.4;Op==;@ENDVAR;@VAR;Variable=callsign;Value=Hawk;Op==;@ENDVAR;'),
                    (21,'Object','pack3','','',2,'',0,11,'0',0,'')
                    """);
                Exec("insert into t_attribute values (1,10,'mass','double','','1','1','1.2','','Public',0),(2,10,'callsign','string','','1','1','','','Public',1)");
                Exec("insert into t_connector values (100,'Aggregation','Strong','','',11,10,'1',''),(101,'Association','','','',20,21,'','')");
            }
            var (m, _) = EaImporter.Import(path);
            var drone7 = m.OfKind(ElementKind.InstanceSpecification).Single(i => i.Name == "drone7");
            Assert.Equal("drone7 : Drone", InstanceModel.Label(m, drone7));
            Assert.Equal(["callsign = Hawk", "mass = 1.4"], InstanceModel.Slots(drone7).Select(s => InstanceModel.SlotLine(m, s)).Order());
            var link = Assert.Single(m.Relationships, r => r.Kind == ElementKind.Link);
            Assert.Equal(ElementKind.Composition, InstanceModel.AssociationOf(m, link)!.Kind);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            dir.Delete(true);
        }
    }

    [Fact]
    public void Instances_report_block_lists_the_instance_table()
    {
        var (m, _) = Sample();
        var t = new DocTemplate { Name = "Instances", TitlePage = false, TableOfContents = false, RevisionHistory = false, ListOfFigures = false, ListOfTables = false };
        t.Sections.Add(new DocSection { Title = "Fleet", Blocks = [new InstancesBlock { Classifier = "Vehicle" }] });
        var doc = DocResolver.Resolve(m, t, new ResolveOptions { RenderDiagrams = false });
        Assert.Empty(doc.Warnings);
        var table = Assert.Single(doc.Nodes.OfType<TableNode>());
        Assert.Equal("Instances of Vehicle", table.Caption!.Text);
        Assert.Contains("mass [kg]", table.Headers);
        Assert.Equal("vehicle1", table.Rows[0][0]);
        Assert.Contains("1600 kg", table.Rows[0]);
        var json = t.ToJson();
        Assert.Contains("\"type\": \"instances\"", json);
        Assert.IsType<InstancesBlock>(DocTemplate.FromJson(json).Sections[0].Blocks[0]);
    }
}

public class InstanceVerificationTests
{
    private static ModelRepository Clone(ModelRepository m) => ProjectFile.Deserialize(ProjectFile.Serialize(m));
    private static readonly VerificationOptions Opts = new() { Clone = Clone, Now = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc) };

    [Fact]
    public void An_instance_as_the_run_subject_overrides_default_values_with_its_slots()
    {
        var m = SampleModels.Vehicle();
        var vehicle = InstanceModelTests.Block(m, "Vehicle");
        var heavy = InstanceModel.CreateInstanceTree(m, vehicle, vehicle.Owner, "heavyVan");
        InstanceModel.SetValue(m, heavy, InstanceModelTests.Prop(vehicle, "mass"), "1.8 t");
        var accel = m.OfKind(ElementKind.Requirement).Single(r => r.Get(FieldKeys.RequirementId) == "REQ-002");
        var power = m.OfKind(ElementKind.Requirement).Single(r => r.Get(FieldKeys.RequirementId) == "REQ-005");
        // The nested engine instance overrides Engine::maxPower.
        var engine = InstanceModel.NestedInstances(heavy).Single(i => i.Name == "engine");
        InstanceModel.SetValue(m, engine, InstanceModelTests.Prop(InstanceModelTests.Block(m, "Engine"), "maxPower"), "140");

        m.Set(power, VerificationKeys.Criterion, "engine.maxPower >= 150 kW");   // evaluated on the subject (the vehicle)
        var run = Verification.CreateRun(m, m.Root, "Heavy van", [accel, power], subject: heavy);
        var report = Verification.Execute(m, run, options: Opts);
        Assert.Empty(report.Problems);
        var a = report.Results.Single(r => r.Requirement == accel);
        Assert.True(a.Verdict == Verdict.Fail, a.Message);   // 27.78 m/s * 1800 kg / 5600 N = 8.93 s
        Assert.Equal("8.929 s", a.Value);
        var p = report.Results.Single(r => r.Requirement == power);
        Assert.True(p.Verdict == Verdict.Fail, p.Message);   // 140 kW < 150 kW
        Assert.Equal("1450", InstanceModelTests.Prop(vehicle, "mass").Get(FieldKeys.DefaultValue));   // the model is untouched

        // Results depend on the instance: editing a slot makes them stale.
        Verification.Record(m, report);
        Assert.False(Verification.IsStale(m, accel));
        InstanceModel.SetValue(m, heavy, InstanceModelTests.Prop(vehicle, "mass"), "1.2 t");
        Assert.True(Verification.IsStale(m, accel));
    }

    [Fact]
    public void Parametrics_solve_an_instance_with_its_slot_values()
    {
        var m = SampleModels.Vehicle();
        var vehicle = InstanceModelTests.Block(m, "Vehicle");
        var v1 = InstanceModel.CreateInstanceTree(m, vehicle);
        InstanceModel.SetValue(m, v1, InstanceModelTests.Prop(vehicle, "mass"), "2800");
        var r = Parametrics.Solve(m, vehicle, InstanceAnalysis.ValueOverrides(m, v1, vehicle));
        var time = r.Values.Single(v => v.Property?.Name == "accelTime");
        Assert.Equal(13.889, time.Value!.Value, 3);
    }
}
