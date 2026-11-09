using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Core.Validation;
using Meridian.Serialization;

namespace Meridian.Tests;

public class ModelQueryTests
{
    private static (ModelRepository M, Element Structure, Element Reqs) Fixture()
    {
        var m = new ModelRepository("Q");
        var reqs = m.Create(m.Root, ElementKind.Package, "Requirements");
        var structure = m.Create(m.Root, ElementKind.Package, "Structure");
        var sub = m.Create(structure, ElementKind.Package, "Propulsion");
        var r1 = m.Create(reqs, ElementKind.Requirement, "Speed");
        var r2 = m.Create(reqs, ElementKind.Requirement, "Range");
        var heavy = m.Create(structure, ElementKind.Block, "Chassis");
        var light = m.Create(structure, ElementKind.Block, "Mirror");
        var motor = m.Create(sub, ElementKind.Block, "Motor");
        var none = m.Create(structure, ElementKind.Block, "Bracket");
        Mass(m, heavy, "120");
        Mass(m, light, "2.5");
        Mass(m, motor, "60");
        m.Set(motor, FieldKeys.Stereotypes, "critical,electrical");
        m.Set(heavy, FieldKeys.TagPrefix + "supplier", "Acme Corp");
        m.Set(light, FieldKeys.Documentation, "Side mirror assembly");
        m.Connect(ElementKind.Satisfy, heavy, r1);
        m.Connect(ElementKind.Satisfy, motor, r2);
        var act = m.Create(structure, ElementKind.Activity, "Drive");
        var a = m.Create(act, ElementKind.Action, "Accelerate");
        m.Connect(ElementKind.Allocate, a, motor);
        _ = none;
        return (m, structure, reqs);
    }

    private static void Mass(ModelRepository m, Element block, string value)
    {
        var p = m.Create(block, ElementKind.Property, "mass");
        m.Set(p, FieldKeys.Type, "Real");
        m.Set(p, FieldKeys.DefaultValue, value);
    }

    private static string[] Names(ModelRepository m, string query) =>
        ModelQuery.Parse(query).Run(m).Select(e => e.Name).OrderBy(n => n).ToArray();

    [Fact]
    public void Kind_Filter_And_Numeric_Value_Property_Comparison()
    {
        var (m, _, _) = Fixture();
        Assert.Equal(["Bracket", "Chassis", "Mirror", "Motor"], Names(m, "kind:Block"));
        Assert.Equal(["Chassis", "Motor"], Names(m, "kind:Block where mass > 50"));
        Assert.Equal(["Mirror"], Names(m, "kind:Block where mass < 10"));   // numeric, not textual ("120" < "2.5" as text)
        Assert.Equal(["Motor"], Names(m, "kind:Block where mass >= 60 and mass <= 60"));
        Assert.Equal(["Motor"], Names(m, "kind:Block where mass = 60.0"));
        Assert.Equal(["Bracket"], Names(m, "kind:Block where mass is empty"));
        Assert.Equal(["Chassis", "Mirror", "Motor"], Names(m, "kind:Block where prop:mass is not empty"));
    }

    [Fact]
    public void Relationship_Existence_With_Direction_And_Negation()
    {
        var (m, _, _) = Fixture();
        Assert.Equal(["Chassis", "Motor"], Names(m, "kind:Block where has:satisfy"));
        Assert.Equal(["Bracket", "Mirror"], Names(m, "kind:Block where not has:satisfy"));
        Assert.Equal(["Bracket", "Mirror"], Names(m, "kind:Block where no:satisfy"));
        Assert.Equal(["Range", "Speed"], Names(m, "kind:Requirement where has:in:satisfy"));
        Assert.Empty(Names(m, "kind:Requirement where has:out:satisfy"));
        Assert.Equal(["Motor"], Names(m, "kind:Block where has:in:allocate"));
        Assert.Empty(Names(m, "kind:Action where no:out:allocate"));
        Assert.Equal(["Chassis"], Names(m, "kind:Block where mass > 50 and not has:in:allocate"));
    }

    [Fact]
    public void And_Binds_Tighter_Than_Or_And_Parentheses_Override()
    {
        var (m, _, _) = Fixture();
        Assert.Equal(["Chassis", "Mirror"], Names(m, "kind:Block where name = Mirror or mass > 100 and has:satisfy"));
        Assert.Equal(["Chassis"], Names(m, "kind:Block where (name = Mirror or mass > 100) and has:satisfy"));
        Assert.Equal(["Bracket", "Motor"], Names(m, "kind:Block where not (name = Mirror or name = Chassis)"));
        Assert.Equal(["Chassis", "Mirror"], Names(m, "kind:Block where name = Mirror || name = Chassis"));
    }

    [Fact]
    public void Text_Operators_Stereotypes_Tags_Owner_And_Scope()
    {
        var (m, _, _) = Fixture();
        Assert.Equal(["Mirror"], Names(m, "kind:Block where documentation contains MIRROR"));
        Assert.Equal(["Mirror"], Names(m, "kind:Block where doc ~ assembly"));
        Assert.Equal(["Bracket", "Chassis", "Motor"], Names(m, "kind:Block where documentation not contains mirror"));
        Assert.Equal(["Motor"], Names(m, "kind:Block stereotype:critical"));
        Assert.Equal(["Motor"], Names(m, "kind:Block where stereotype = electrical"));
        Assert.Equal(["Chassis"], Names(m, "kind:Block where tag:supplier = \"acme corp\""));
        Assert.Equal(["Chassis"], Names(m, "kind:Block where supplier contains Acme"));
        Assert.Equal(["Motor"], Names(m, "kind:Block where owner = Propulsion"));
        Assert.Equal(["Motor"], Names(m, "kind:Block in:Q::Structure::Propulsion"));
        Assert.Equal(["Motor"], Names(m, "kind:Block in:Propulsion"));
        Assert.Equal(["Bracket", "Chassis", "Mirror", "Motor"], Names(m, "kind:Block in:Structure"));
        Assert.Equal(["Range", "Speed"], Names(m, "in:Requirements"));
        Assert.Empty(Names(m, "kind:Block in:NoSuchPackage"));
        Assert.Equal(["Chassis", "Mirror", "Motor"], Names(m, "kind:Block where name != Bracket"));
        Assert.Equal(["Range"], Names(m, "kind:Requirement where name < S"));
    }

    [Fact]
    public void Kind_Lists_Kind_Field_And_Display_Names()
    {
        var (m, _, _) = Fixture();
        Assert.Equal(["Accelerate", "Drive"], Names(m, "kind:Activity,Action"));
        Assert.Equal(["Accelerate", "Drive"], Names(m, "kind:activities, actions"));
        Assert.Equal(["Range", "Speed"], Names(m, "kind = Requirement"));
        Assert.Equal(2, ModelQuery.Parse("kind:\"Test Case\",UseCase").Kinds.Count);
        // Without a kind filter, relationships, diagrams and saved views are excluded.
        Assert.DoesNotContain(ModelQuery.Parse("").Run(m), e => e.IsRelationship);
    }

    [Theory]
    [InlineData("kind:Block where mass > 50 and not has:satisfy")]
    [InlineData("kind:Block,Requirement stereotype:critical in:\"Q::Structure\" where (name = A or name = \"B c\") and tag:x is not empty")]
    [InlineData("where not (a = 1 and b != 2) or c contains \"and\"")]
    [InlineData("kind:Action where has:out:allocate or doc is empty")]
    [InlineData("kind:Requirement")]
    public void Text_Form_Round_Trips(string text)
    {
        var q = ModelQuery.Parse(text);
        var printed = q.ToString();
        var again = ModelQuery.Parse(printed);
        Assert.Equal(printed, again.ToString());
        Assert.Equal(q.Kinds, again.Kinds);
        Assert.Equal(q.Scope, again.Scope);
        Assert.Equal(q.Stereotype, again.Stereotype);
    }

    [Theory]
    [InlineData("kind:Blokk", "Unknown element kind")]
    [InlineData("kind:Block where mass >", "Expected a value")]
    [InlineData("kind:Block where (mass > 1", "Expected ')'")]
    [InlineData("kind:Block where has:frobnicate", "Unknown relationship")]
    [InlineData("kind:Block where name \"x\"", "Expected an operator")]
    [InlineData("kind:Block where name = \"x", "Unterminated string")]
    [InlineData("kind:Block where a = 1 b = 2", "Unexpected input")]
    public void Parse_Errors_Are_Reported(string text, string message)
    {
        Assert.False(ModelQuery.TryParse(text, out _, out var error));
        Assert.Contains(message, error);
    }

    [Fact]
    public void Builder_Rows_Combine_And_Flatten()
    {
        var rows = new List<QueryRow>
        {
            new() { Field = "mass", Op = QueryOp.Gt, Value = "50" },
            new() { IsRelation = true, Negate = true, RelKind = ElementKind.Satisfy },
            new() { Or = true, Field = "name", Op = QueryOp.Eq, Value = "Mirror" },
        };
        var expr = QueryRow.Combine(rows)!;
        Assert.Equal("mass > 50 and not has:satisfy or name = Mirror", expr.ToString());
        var (m, _, _) = Fixture();
        Assert.Equal(["Mirror"], ModelQuery.Parse("kind:Block where " + expr).Run(m).Select(e => e.Name).ToArray());
        var back = QueryRow.Flatten(expr)!;
        Assert.Equal(3, back.Count);
        Assert.True(back[1].IsRelation && back[1].Negate);
        Assert.True(back[2].Or);
        Assert.Null(QueryRow.Flatten(ModelQuery.ParseCondition("not (a = 1 or b = 2)")));
    }

    [Fact]
    public void Model_Values_Resolve_Aliases_Relationships_And_Edit_Undoably()
    {
        var (m, _, _) = Fixture();
        var chassis = m.AllElements.Single(e => e.Name == "Chassis");
        var motor = m.AllElements.Single(e => e.Name == "Motor");
        Assert.EndsWith(" Speed", ModelValues.Get(m, chassis, "satisfies"));   // requirements show "<id> <name>"
        Assert.Equal("Accelerate", ModelValues.Get(m, motor, "allocatedFrom"));
        Assert.Equal("Satisfies", ModelValues.Label("satisfies"));
        Assert.Equal("Allocated to", ModelValues.Label("rel:out:Allocate"));
        Assert.False(ModelValues.CanEdit(chassis, "satisfies"));
        Assert.True(ModelValues.CanEdit(chassis, "mass"));

        ModelValues.Set(m, chassis, "mass", "130");
        Assert.Equal("130", ModelValues.Get(m, chassis, "prop:mass"));
        ModelValues.Set(m, chassis, "tag:supplier", "Other");
        ModelValues.Set(m, chassis, "name", "Frame");
        Assert.Equal("Frame", chassis.Name);
        m.History.Undo(); m.History.Undo(); m.History.Undo();
        Assert.Equal("Chassis", chassis.Name);
        Assert.Equal("120", ModelValues.Get(m, chassis, "mass"));
        Assert.Equal("Acme Corp", ModelValues.Get(m, chassis, "supplier"));
        Assert.Throws<InvalidOperationException>(() => ModelValues.Set(m, chassis, "owner", "x"));
    }

    [Fact]
    public void Tables_Filter_Sort_And_Export_Csv()
    {
        var (m, _, _) = Fixture();
        var def = new TableDefinition { Query = "kind:Block", Columns = [new("name"), new("mass"), new("satisfies")], SortKey = "mass" };
        var rows = ModelTables.Build(m, def);
        Assert.Equal(["Mirror", "Motor", "Chassis", "Bracket"], rows.Select(r => r.Element.Name));   // numeric sort, empty last
        def.SortDescending = true;
        Assert.Equal("Chassis", ModelTables.Build(m, def)[0].Element.Name);
        def.Columns[1].Filter = ">50";
        Assert.Equal(["Chassis", "Motor"], ModelTables.Build(m, def).Select(r => r.Element.Name).OrderBy(n => n));
        def.Columns[1].Filter = null;
        def.Columns[2].Filter = "!speed";
        Assert.DoesNotContain(ModelTables.Build(m, def), r => r.Element.Name == "Chassis");

        m.Set(m.AllElements.Single(e => e.Name == "Mirror"), FieldKeys.Name, "Mirror, \"left\"");
        def.Columns[2].Filter = null;
        var csv = ModelTables.ToCsv(def, ModelTables.Build(m, def));
        Assert.StartsWith("Name,Mass,Satisfies", csv);
        Assert.Contains("\"Mirror, \"\"left\"\"\",2.5,", csv);
    }

    [Fact]
    public void Saved_Views_Persist_In_Project_Files_And_Undo_As_One_Step()
    {
        var (m, _, _) = Fixture();
        var def = new TableDefinition { Query = "kind:Block where mass > 50", Columns = [new("name"), new("mass") { Filter = "1" }], SortKey = "name", SortDescending = true };
        var view = SavedViews.SaveTable(m, def, "Heavy blocks");
        Assert.Equal(ElementKind.TableView, view.Kind);
        Assert.Equal("Views", view.Owner!.Name);
        var query = SavedViews.SaveQuery(m, "kind:Requirement where no:in:verify", "Unverified");

        // Updating an existing view changes it in place.
        def.Columns.Add(new TableViewColumn("satisfies"));
        Assert.Same(view, SavedViews.SaveTable(m, def, "Heavy blocks", view));
        m.History.Undo();
        Assert.Equal(2, TableDefinition.FromElement(view).Columns.Count);

        var back = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        var loaded = back.Find(view.Id)!;
        Assert.Equal(ElementKind.TableView, loaded.Kind);
        var d2 = TableDefinition.FromElement(loaded);
        Assert.Equal(def.Query, d2.Query);
        Assert.Equal(["name", "mass"], d2.Columns.Select(c => c.Key));
        Assert.Equal("1", d2.Columns[1].Filter);
        Assert.True(d2.SortDescending);
        Assert.Equal("kind:Requirement where no:in:verify", back.Find(query.Id)!.Get(ViewFields.Query));

        Assert.Throws<QueryParseException>(() => SavedViews.SaveQuery(m, "kind:Nope", "Bad"));

        // Creating the view (package + element + fields) is a single undo step.
        var before = m.AllElements.Count();
        SavedViews.SaveQuery(m, "kind:Block", "Another");
        m.History.Undo();
        Assert.Equal(before, m.AllElements.Count());
    }

    [Fact]
    public void Saved_Views_Replicate_To_Other_Replicas()
    {
        var json = ProjectFile.Serialize(SampleModels.Vehicle());
        var a = ProjectFile.Deserialize(json);
        var b = ProjectFile.Deserialize(json);
        a.OpEmitted += b.ApplyRemote;
        var rule = SavedViews.SaveRule(a, "kind:Block where mass is empty", "Blocks need mass", "error", "{name} has no mass");
        var copy = b.Find(rule.Id)!;
        Assert.Equal(ElementKind.ValidationRule, copy.Kind);
        Assert.Equal("error", copy.Get(ViewFields.Severity));
        Assert.Contains(new ModelValidator().Validate(b), i => i.RuleId == "Blocks need mass" && i.Severity == Severity.Error && i.Message == "Engine has no mass");
    }

    [Fact]
    public void Custom_Validation_Rules_Report_Matches_And_Bad_Queries()
    {
        var (m, _, _) = Fixture();
        SavedViews.SaveRule(m, "kind:Block where not has:satisfy", "Unsatisfied", "warning", "Block {name} ({mass} kg) in {owner} satisfies nothing");
        SavedViews.SaveRule(m, "kind:Block where mass > 100", "Too heavy", "error", "");
        var issues = new ModelValidator().Validate(m);
        var unsat = issues.Where(i => i.RuleId == "Unsatisfied").ToList();
        Assert.Equal(2, unsat.Count);
        Assert.All(unsat, i => Assert.Equal(Severity.Warning, i.Severity));
        Assert.Contains(unsat, i => i.Message == "Block Mirror (2.5 kg) in Structure satisfies nothing");
        Assert.Contains(unsat, i => i.Message == "Block Bracket (∅ kg) in Structure satisfies nothing");
        Assert.Contains(issues, i => i.RuleId == "Too heavy" && i.Severity == Severity.Error && i.Element.Name == "Chassis");

        var bad = m.Create(m.Root, ElementKind.ValidationRule, "Broken");
        m.Set(bad, ViewFields.Query, "kind:Block where (");
        Assert.Contains(new ModelValidator().Validate(m), i => i.RuleId == QueryRules.InvalidRuleId && ReferenceEquals(i.Element, bad));
    }

    [Fact]
    public void Sample_Model_Ships_Views_And_Rules_Without_Errors()
    {
        var m = SampleModels.Vehicle();
        Assert.Contains(m.OfKind(ElementKind.TableView), v => v.Name == "Block overview");
        Assert.Contains(m.OfKind(ElementKind.QueryView), v => v.Name == "Unverified requirements");
        var issues = new ModelValidator().Validate(m);
        Assert.DoesNotContain(issues, i => i.Severity == Severity.Error);
        Assert.Contains(issues, i => i.RuleId == "Unallocated actions" && i.Severity == Severity.Info);
        Assert.DoesNotContain(issues, i => i.RuleId == "Engine power target");   // 160 kW meets the target
        foreach (var v in m.OfKind(ElementKind.TableView)) Assert.NotEmpty(ModelTables.Build(m, TableDefinition.FromElement(v)));
        Assert.False(m.History.CanUndo);
    }
}
