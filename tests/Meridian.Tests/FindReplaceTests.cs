using System.Diagnostics;
using Meridian.Core.Analysis;
using Meridian.Core.Libraries;
using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Core.Samples;
using Meridian.Serialization;
using Xunit.Abstractions;

namespace Meridian.Tests;

public class FindReplaceTests(ITestOutputHelper output)
{
    private static FindResult Find(ModelRepository m, string text, Func<FindQuery, FindQuery>? with = null, IReadOnlyList<ExternalText>? external = null)
    {
        var q = new FindQuery(text);
        if (with != null) q = with(q);
        return FindReplace.Run(m, q, external);
    }

    private static IEnumerable<(Element? Element, FieldHit Field)> Fields(FindResult r) => r.Hits.SelectMany(h => h.Fields.Select(f => (h.Element, f)));

    private static Element Named(ModelRepository m, ElementKind k, string name) => m.OfKind(k).Single(e => e.Name == name);

    // ------------------------------------------------------------------ matching options

    [Fact]
    public void Plain_Text_Ignores_Case_Unless_Match_Case()
    {
        var m = SampleModels.Vehicle();
        var any = Find(m, "engine");
        Assert.Contains(any.Hits, h => h.Element?.Name == "Engine");
        Assert.Contains(any.Hits, h => h.Element?.Name == "engine");
        var exact = Find(m, "engine", q => q with { MatchCase = true });
        Assert.DoesNotContain(exact.Hits, h => h.Element?.Name == "Engine" && h.Fields.Any(f => f.Key == FieldKeys.Name));
        Assert.Contains(exact.Hits, h => h.Element?.Name == "engine");
        Assert.True(exact.Matches < any.Matches);
    }

    [Fact]
    public void Whole_Word_Skips_Partial_Words()
    {
        var m = new ModelRepository("M");
        var a = m.Create(m.Root, ElementKind.Block, "Engine");
        var b = m.Create(m.Root, ElementKind.Block, "Engines");
        var c = m.Create(m.Root, ElementKind.Block, "Main_Engine");
        var d = m.Create(m.Root, ElementKind.Block, "Main Engine");
        var r = Find(m, "Engine", q => q with { WholeWord = true });
        Assert.Equal([a, d], r.Hits.Select(h => h.Element).ToArray());
        Assert.Equal(4, Find(m, "Engine").Hits.Count);
        // Regex with whole word wraps the pattern in word boundaries ("_" is a word character).
        Assert.Equal([a, b, d], Find(m, "Eng\\w*", q => q with { Regex = true, WholeWord = true }).Hits.Select(h => h.Element).ToArray());
        _ = c;
    }

    [Fact]
    public void Regex_Groups_Feed_The_Replacement()
    {
        var m = new ModelRepository("M");
        var r1 = m.Create(m.Root, ElementKind.Requirement, "Speed");
        m.Set(r1, FieldKeys.Text, "The car shall reach 100 km/h in 8 s.");
        var result = Find(m, @"(\d+) km/h", q => q with { Regex = true, Replace = "$1 kph", In = SearchIn.Requirements });
        var hit = Assert.Single(Fields(result)).Field;
        Assert.Equal("100 kph", hit.Matches.Single().Replacement);
        var outcome = FindReplace.ReplaceAll(m, result);
        Assert.Equal(1, outcome.Applied);
        Assert.Equal("The car shall reach 100 kph in 8 s.", r1.Get(FieldKeys.Text));
    }

    [Fact]
    public void Invalid_Or_Empty_Patterns_Report_An_Error()
    {
        var m = SampleModels.Vehicle();
        Assert.NotNull(Find(m, "(unclosed", q => q with { Regex = true }).Error);
        Assert.NotNull(Find(m, "x*", q => q with { Regex = true }).Error);   // matches empty text
        Assert.NotNull(Find(m, "").Error);
        Assert.Null(Find(m, "(unclosed").Error);   // plain text: parentheses are literal
    }

    [Fact]
    public void Catastrophic_Regex_Times_Out_With_A_Message()
    {
        var m = new ModelRepository("M");
        var b = m.Create(m.Root, ElementKind.Block, "B");
        m.Set(b, FieldKeys.Documentation, new string('a', 40) + "!");
        var sw = Stopwatch.StartNew();
        var r = Find(m, "(a+)+$x", q => q with { Regex = true, RegexTimeout = TimeSpan.FromMilliseconds(50) });
        sw.Stop();
        Assert.Equal(FindReplace.Matcher.TimeoutMessage, r.Error);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact]
    public void Snippets_Show_Context_On_One_Line()
    {
        var text = "first line\nThe Engine shall start in under two seconds at minus twenty degrees.";
        var m = new TextMatch(text.IndexOf("Engine", StringComparison.Ordinal), 6, "Motor");
        var (before, hit, after) = FindReplace.Snippet(text, m, 10);
        Assert.Equal("Engine", hit);
        Assert.StartsWith("…", before);
        Assert.DoesNotContain("\n", before);
        Assert.EndsWith("…", after);
        Assert.Equal("first line\nThe Motor shall", FindReplace.Apply(text, [m])[..26]);
    }

    // ------------------------------------------------------------------ scopes and kinds

    [Fact]
    public void Scopes_Package_Diagram_Selection_And_Kind_Filter()
    {
        var m = SampleModels.Vehicle();
        var all = Find(m, "Engine");
        var structure = m.OfKind(ElementKind.Package).Single(p => p.Name == "Structure");
        var pkg = Find(m, "Engine", q => q with { Scope = FindScope.Package, Roots = [structure] });
        Assert.NotEmpty(pkg.Hits);
        Assert.All(pkg.Hits, h => Assert.True(ReferenceEquals(h.Element, structure) || structure.IsAncestorOf(h.Element!)));
        Assert.True(pkg.Hits.Count < all.Hits.Count);

        var bdd = m.Diagrams.First(d => d.DiagramType == DiagramType.BlockDefinition);
        var dia = Find(m, "Engine", q => q with { Scope = FindScope.Diagram, Diagram = bdd });
        Assert.All(dia.Hits, h => Assert.True(ReferenceEquals(h.Element, bdd) || bdd.Shows(h.Element!) || bdd.Edges.Any(x => ReferenceEquals(x.Relationship, h.Element))));
        Assert.Contains(dia.Hits, h => h.Element?.Name == "Engine");

        var engine = Named(m, ElementKind.Block, "Engine");
        var sel = Find(m, "Engine", q => q with { Scope = FindScope.Selection, Roots = [engine] });
        Assert.Equal(engine, Assert.Single(sel.Hits).Element);

        var reqs = Find(m, "Engine", q => q with { Kinds = new HashSet<ElementKind> { ElementKind.Requirement } });
        Assert.NotEmpty(reqs.Hits);
        Assert.All(reqs.Hits, h => Assert.Equal(ElementKind.Requirement, h.Element!.Kind));
    }

    [Fact]
    public void Hits_Come_In_Model_Order_And_Stop_At_The_Cap()
    {
        var m = new ModelRepository("M");
        for (var i = 0; i < 50; i++) m.Create(m.Root, ElementKind.Block, $"Pump {i:00}");
        var r = Find(m, "Pump", q => q with { MaxMatches = 20 });
        Assert.True(r.Truncated);
        Assert.Equal(20, r.Matches);
        Assert.Equal("Pump 00", r.Hits[0].Element!.Name);
        Assert.Equal("Pump 19", r.Hits[^1].Element!.Name);
        Assert.False(Find(m, "Pump").Truncated);
    }

    [Fact]
    public void Search_Can_Be_Cancelled()
    {
        var m = new ModelRepository("M");
        for (var i = 0; i < 3000; i++) m.Create(m.Root, ElementKind.Block, $"Pump {i}");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => FindReplace.Run(m, new FindQuery("Pump"), null, cts.Token));
    }

    // ------------------------------------------------------------------ field coverage

    [Fact]
    public void Every_Field_Category_Is_Found_And_Can_Be_Switched_Off()
    {
        var m = new ModelRepository("M");
        var pkg = m.Create(m.Root, ElementKind.Package, "Gear pkg");
        var block = m.Create(pkg, ElementKind.Block, "Gearbox");
        m.Set(block, FieldKeys.Documentation, "Holds the gear train.");
        m.Set(block, FieldKeys.TagPrefix + "supplier", "GearCo");
        m.Set(block, FieldKeys.Stereotypes, "gearStereo");
        var p = m.Create(block, ElementKind.Property, "ratio");
        m.Set(p, FieldKeys.DefaultValue, "gear1 * 2");
        var cb = m.Create(pkg, ElementKind.ConstraintBlock, "Law");
        m.Set(cb, FieldKeys.Body, "torque = gear * force");
        var req = m.Create(pkg, ElementKind.Requirement, "Shift");
        m.Set(req, FieldKeys.Text, "The gear shall shift.");
        m.Set(req, FieldKeys.RequirementId, "GEAR-1");
        m.Set(req, ReqFields.Rationale, "gear wear");
        m.Set(req, ReqFields.CustomPrefix + "Safety", "gear critical");
        var note = m.Create(pkg, ElementKind.Comment);
        m.Set(note, FieldKeys.Body, "Check the gear ratio");
        var shape = m.Create(pkg, ElementKind.Shape, "Gear zone");
        var sm = m.Create(block, ElementKind.StateMachine, "Modes");
        var s1 = m.Create(sm, ElementKind.State, "Idle");
        var s2 = m.Create(sm, ElementKind.State, "Run");
        m.Set(s1, FieldKeys.Entry, "gear = 0");
        var t = m.Connect(ElementKind.Transition, s1, s2);
        m.Set(t, FieldKeys.Guard, "gear > 0");
        // Not searched: references and layout.
        m.Set(p, FieldKeys.Type, "gearType");

        var r = Find(m, "gear", q => q with { In = SearchIn.All });
        var found = Fields(r).Select(x => (x.Element, x.Field.Key, x.Field.Category)).ToList();
        Assert.Contains((pkg, FieldKeys.Name, SearchIn.Names), found);
        Assert.Contains((block, FieldKeys.Name, SearchIn.Names), found);
        Assert.Contains((block, FieldKeys.Documentation, SearchIn.Documentation), found);
        Assert.Contains((block, FieldKeys.TagPrefix + "supplier", SearchIn.TaggedValues), found);
        Assert.Contains((block, FieldKeys.Stereotypes, SearchIn.Stereotypes), found);
        Assert.Contains((p, FieldKeys.DefaultValue, SearchIn.Values), found);
        Assert.Contains((cb, FieldKeys.Body, SearchIn.Values), found);
        Assert.Contains((req, FieldKeys.Text, SearchIn.Requirements), found);
        Assert.Contains((req, FieldKeys.RequirementId, SearchIn.Requirements), found);
        Assert.Contains((req, ReqFields.Rationale, SearchIn.Requirements), found);
        Assert.Contains((req, ReqFields.CustomPrefix + "Safety", SearchIn.TaggedValues), found);
        Assert.Contains((note, FieldKeys.Body, SearchIn.DiagramText), found);
        Assert.Contains((shape, FieldKeys.Name, SearchIn.DiagramText), found);
        Assert.Contains((s1, FieldKeys.Entry, SearchIn.Values), found);
        Assert.Contains(((Element)t, FieldKeys.Guard, SearchIn.Values), found);
        Assert.DoesNotContain(found, f => f.Key == FieldKeys.Type);

        // Stereotype names are find-only.
        Assert.Equal("Find only", Fields(r).Single(x => x.Field.Key == FieldKeys.Stereotypes).Field.ReadOnlyReason);
        // Switching categories off.
        var namesOnly = Find(m, "gear", q => q with { In = SearchIn.Names });
        Assert.All(Fields(namesOnly), x => Assert.Equal(SearchIn.Names, x.Field.Category));
        Assert.Empty(Find(m, "gear", q => q with { In = SearchIn.None }).Hits);
    }

    [Fact]
    public void Comments_And_Sysml_Text_Come_From_Outside_And_Stay_Find_Only_Unless_Writable()
    {
        var m = SampleModels.Vehicle();
        var engine = Named(m, ElementKind.Block, "Engine");
        ExternalText[] ext =
        [
            new("comment:1", SearchIn.Comments, "Comment by Alice", "Is the Engine big enough?", engine.Id),
            new("comment:2", SearchIn.Comments, "Comment by Bob", "Engine looks fine", engine.Id, "Only the author can change a comment"),
            new("sysml:3", SearchIn.SysmlText, "Line 3", "part def Engine {", null, "Find only"),
        ];
        var r = Find(m, "Engine", q => q with { In = SearchIn.All, Replace = "Motor" }, ext);
        var engineHit = r.Hits.Single(h => ReferenceEquals(h.Element, engine));
        Assert.Contains(engineHit.Fields, f => f.Key == "comment:1" && !f.ReadOnly);
        Assert.Contains(engineHit.Fields, f => f.Key == "comment:2" && f.ReadOnly);
        var sysml = r.Hits.Single(h => h.Element == null);
        Assert.Equal("SysML v2 text", sysml.Title);
        // Off by default.
        Assert.DoesNotContain(Find(m, "Engine", null, ext).Hits.SelectMany(h => h.Fields), f => f.Key.StartsWith("comment:") || f.Key.StartsWith("sysml:"));

        // A replace leaves the model alone for external texts: the caller applies the writable ones.
        var plan = FindReplace.Plan(m, r.Hits.SelectMany(h => h.Fields.Select(f => (h, f, (IReadOnlyCollection<TextMatch>?)null))).Where(x => x.f.Key.Contains(':') && !x.f.Key.StartsWith("tag:")));
        Assert.Empty(plan.Changes);
        Assert.Equal("Is the Motor big enough?", plan.External.Single(c => c.Key == "comment:1").After);
        Assert.True(plan.External.Single(c => c.Key == "comment:2").Skipped);
        Assert.True(plan.External.Single(c => c.Key == "sysml:3").Skipped);
    }

    // ------------------------------------------------------------------ replace

    [Fact]
    public void Replace_All_Is_One_Undo_Step_And_One_Replicated_Batch()
    {
        var m = SampleModels.Vehicle();
        m.History.Clear();
        var engine = Named(m, ElementKind.Block, "Engine");
        var doc = engine.Documentation;
        var before = ProjectFile.Serialize(m);
        var ops = new List<ModelOp>();
        var actions = 0;
        m.OpEmitted += ops.Add;
        m.History.ActionCompleted += () => actions++;

        var r = Find(m, "Engine", q => q with { MatchCase = true, Replace = "Motor", In = SearchIn.Names | SearchIn.Documentation | SearchIn.Requirements });
        var outcome = FindReplace.ReplaceAll(m, r);
        Assert.True(outcome.Applied > 3, $"applied {outcome.Applied}");
        Assert.Empty(outcome.Skipped);
        Assert.Equal("Motor", engine.Name);
        Assert.Contains(m.OfKind(ElementKind.UseCase), u => u.Name == "Start Motor");
        Assert.DoesNotContain(m.AllElements, e => e.Name.Contains("Engine", StringComparison.Ordinal));
        Assert.Equal(1, actions);
        Assert.True(ops.Count >= outcome.Applied);
        Assert.StartsWith("Replace in", m.History.UndoDescription);

        m.History.Undo();
        Assert.Equal("Engine", engine.Name);
        Assert.Equal(doc, engine.Documentation);
        Assert.Equal(before, ProjectFile.Serialize(m));
        Assert.False(m.History.CanUndo);
        m.History.Redo();
        Assert.Equal("Motor", engine.Name);
    }

    [Fact]
    public void Replace_Selected_Matches_Only()
    {
        var m = new ModelRepository("M");
        var b = m.Create(m.Root, ElementKind.Block, "B");
        m.Set(b, FieldKeys.Documentation, "pump, pump, pump");
        var r = Find(m, "pump", q => q with { Replace = "valve" });
        var hit = r.Hits.Single();
        var f = hit.Fields.Single();
        var plan = FindReplace.Plan(m, [(hit, f, [f.Matches[1]])]);
        var change = plan.Changes.Single();
        Assert.Equal("pump, pump, pump", change.Before);
        Assert.Equal("pump, valve, pump", change.After);
        Assert.Equal(1, plan.Occurrences);
        FindReplace.ApplyPlan(m, plan);
        Assert.Equal("pump, valve, pump", b.Documentation);
    }

    [Fact]
    public void Renames_Respect_Unique_Sibling_Names_And_Requirement_Ids()
    {
        var m = new ModelRepository("M");
        var pkg = m.Create(m.Root, ElementKind.Package, "P");
        var a = m.Create(pkg, ElementKind.Block, "Pump A");
        m.Create(pkg, ElementKind.Block, "Valve A");
        var other = m.Create(m.Root, ElementKind.Package, "Q");
        var c = m.Create(other, ElementKind.Block, "Pump A");
        var prop = m.Create(pkg, ElementKind.Actor, "Pump A");   // another kind: no clash
        var r1 = m.Create(pkg, ElementKind.Requirement, "R1");
        m.Set(r1, FieldKeys.RequirementId, "PUMP-1");
        var r2 = m.Create(pkg, ElementKind.Requirement, "R2");
        m.Set(r2, FieldKeys.RequirementId, "VALVE-1");

        var result = Find(m, "Pump", q => q with { MatchCase = true, Replace = "Valve", In = SearchIn.Names });
        var plan = FindReplace.Plan(m, result.Hits.SelectMany(h => h.Fields.Select(f => (h, f, (IReadOnlyCollection<TextMatch>?)null))));
        var skipA = plan.Changes.Single(x => ReferenceEquals(x.Element, a));
        Assert.True(skipA.Skipped);
        Assert.Contains("already has a Block named “Valve A”", skipA.SkipReason);
        Assert.False(plan.Changes.Single(x => ReferenceEquals(x.Element, c)).Skipped);
        Assert.False(plan.Changes.Single(x => ReferenceEquals(x.Element, prop)).Skipped);
        var outcome = FindReplace.ApplyPlan(m, plan);
        Assert.Equal("Pump A", a.Name);
        Assert.Equal("Valve A", c.Name);
        Assert.Single(outcome.Skipped);

        var ids = Find(m, "PUMP", q => q with { MatchCase = true, Replace = "VALVE", In = SearchIn.Requirements });
        var idPlan = FindReplace.Plan(m, ids.Hits.SelectMany(h => h.Fields.Select(f => (h, f, (IReadOnlyCollection<TextMatch>?)null))));
        Assert.Contains("already used", idPlan.Changes.Single().SkipReason);

        // An emptied name is refused.
        var empty = Find(m, "Valve A", q => q with { Scope = FindScope.Selection, Roots = [c], In = SearchIn.Names });
        Assert.Contains("empty", FindReplace.Plan(m, empty.Hits.SelectMany(h => h.Fields.Select(f => (h, f, (IReadOnlyCollection<TextMatch>?)null)))).Changes.Single().SkipReason);
    }

    [Fact]
    public void Swapping_Names_Between_Siblings_Is_Not_A_Clash()
    {
        var m = new ModelRepository("M");
        var a = m.Create(m.Root, ElementKind.Block, "Left");
        var b = m.Create(m.Root, ElementKind.Block, "Right");
        var r = Find(m, "^(Left|Right)$", q => q with { Regex = true, MatchCase = true, Replace = "X", In = SearchIn.Names });
        var hits = r.Hits.ToList();
        var plan = FindReplace.Plan(m,
        [
            (hits[0], hits[0].Fields[0], [hits[0].Fields[0].Matches[0] with { Replacement = "Right" }]),
            (hits[1], hits[1].Fields[0], [hits[1].Fields[0].Matches[0] with { Replacement = "Left" }]),
        ]);
        Assert.DoesNotContain(plan.Changes, c => c.Skipped);
        FindReplace.ApplyPlan(m, plan);
        Assert.Equal(("Right", "Left"), (a.Name, b.Name));
    }

    [Fact]
    public void Renaming_A_Column_Through_Replace_Updates_Its_Foreign_Keys()
    {
        var m = new ModelRepository("M");
        var schema = m.Create(m.Root, ElementKind.Package, "public");
        var customer = m.Create(schema, ElementKind.Table, "customer");
        m.Create(customer, ElementKind.Column, "id");
        var order = m.Create(schema, ElementKind.Table, "order");
        var cid = m.Create(order, ElementKind.Column, "customer_id");
        var fk = m.Connect(ElementKind.ForeignKey, order, customer);
        m.Set(fk, FieldKeys.FkColumns, "customer_id");
        m.Set(fk, FieldKeys.RefColumns, "id");
        m.History.Clear();

        var r = Find(m, "customer_id", q => q with { Replace = "buyer_id", In = SearchIn.Names, Kinds = new HashSet<ElementKind> { ElementKind.Column } });
        var outcome = FindReplace.ReplaceAll(m, r);
        Assert.Equal(1, outcome.Applied);
        Assert.Equal("buyer_id", cid.Name);
        Assert.Equal("buyer_id", fk.Get(FieldKeys.FkColumns));
        m.History.Undo();
        Assert.Equal("customer_id", cid.Name);
        Assert.Equal("customer_id", fk.Get(FieldKeys.FkColumns));
        Assert.False(m.History.CanUndo);
    }

    [Fact]
    public void Table_Names_Clash_Ignoring_Case()
    {
        var m = new ModelRepository("M");
        var schema = m.Create(m.Root, ElementKind.Package, "public");
        m.Create(schema, ElementKind.Table, "Orders");
        var t = m.Create(schema, ElementKind.Table, "purchases");
        var r = Find(m, "purchases", q => q with { Replace = "orders", In = SearchIn.Names });
        var outcome = FindReplace.ReplaceAll(m, r);
        Assert.Equal(0, outcome.Applied);
        Assert.Equal("purchases", t.Name);
    }

    [Fact]
    public void A_Field_Changed_After_The_Preview_Is_Skipped_And_Reported()
    {
        var m = SampleModels.Vehicle();
        var engine = Named(m, ElementKind.Block, "Engine");
        var r = Find(m, "Engine", q => q with { MatchCase = true, Replace = "Motor", In = SearchIn.Names });
        var plan = FindReplace.Plan(m, r.Hits.SelectMany(h => h.Fields.Select(f => (h, f, (IReadOnlyCollection<TextMatch>?)null))));
        m.Rename(engine, "Engine X");   // a concurrent edit lands between the preview and the apply
        var outcome = FindReplace.ApplyPlan(m, plan);
        Assert.Equal("Engine X", engine.Name);
        var skipped = Assert.Single(outcome.Skipped);
        Assert.Same(engine, skipped.Element);
        Assert.Contains("since the preview", skipped.SkipReason);
        Assert.True(outcome.Applied > 0);
    }

    // ------------------------------------------------------------------ read-only and permissions

    [Fact]
    public void Library_Elements_Are_Found_Greyed_And_Never_Replaced()
    {
        var src = new ModelRepository("Parts Library");
        var pkg = src.Create(src.Root, ElementKind.Package, "Standard parts");
        var wheel = src.Create(pkg, ElementKind.Block, "Engine mount");
        src.Set(wheel, FieldKeys.Documentation, "Holds the Engine");
        var info = new LibraryInfo("u1", Guid.NewGuid(), "Parts Library", pkg.Id, LibraryPinKind.Version, "1", LibraryPins.Label(LibraryPinKind.Version, "1"), "", Resolved: "v1");
        var m = ProjectFile.Deserialize(ProjectFile.Serialize(SampleModels.Vehicle()), [new LibraryPayload(info, LibraryContent.Extract(src, pkg.Id)!)]);
        var mounted = m.OfKind(ElementKind.Block).Single(b => b.Name == "Engine mount");
        Assert.True(m.IsLibraryElement(mounted));

        var r = Find(m, "Engine", q => q with { MatchCase = true, Replace = "Motor" });
        var lib = r.Hits.Single(h => ReferenceEquals(h.Element, mounted));
        Assert.All(lib.Fields, f => Assert.Contains("Parts Library", f.ReadOnlyReason));
        var outcome = FindReplace.ReplaceAll(m, r);
        Assert.Equal("Engine mount", mounted.Name);
        Assert.Equal("Holds the Engine", mounted.Documentation);
        Assert.Contains(outcome.Skipped, s => ReferenceEquals(s.Element, mounted));
        Assert.Equal("Motor", m.OfKind(ElementKind.Block).Single(b => b.Name == "Motor").Name);
    }

    [Fact]
    public void Hidden_Packages_Are_Never_Searched_And_Read_Only_Ones_Never_Replaced()
    {
        var m = new ModelRepository("M");
        var open = m.Create(m.Root, ElementKind.Package, "Open");
        var secret = m.Create(m.Root, ElementKind.Package, "Secret");
        var locked = m.Create(m.Root, ElementKind.Package, "Locked");
        var a = m.Create(open, ElementKind.Block, "Pump");
        var s = m.Create(secret, ElementKind.Block, "Pump");
        var l = m.Create(locked, ElementKind.Block, "Pump");
        var bob = Guid.NewGuid();
        var view = AccessView.For(
            [new(secret.Id, PrincipalKind.User, bob, PackageAccess.None), new(locked.Id, PrincipalKind.User, bob, PackageAccess.Read)],
            bob, [], PackageAccess.Edit, PackageAccess.Manage);

        var r = Find(m, "Pump", q => q with { Replace = "Valve", Access = view });
        Assert.DoesNotContain(r.Hits, h => ReferenceEquals(h.Element, s));
        var lockedHit = r.Hits.Single(h => ReferenceEquals(h.Element, l));
        Assert.Contains("Locked", lockedHit.Fields.Single().ReadOnlyReason);
        Assert.Null(r.Hits.Single(h => ReferenceEquals(h.Element, a)).Fields.Single().ReadOnlyReason);

        var outcome = FindReplace.ReplaceAll(m, r);
        Assert.Equal(("Valve", "Pump", "Pump"), (a.Name, s.Name, l.Name));
        Assert.Single(outcome.Skipped);

        // View-only: everything is read-only.
        var viewOnly = Find(m, "Pump", q => q with { ViewOnly = true });
        Assert.All(viewOnly.Hits.SelectMany(h => h.Fields), f => Assert.Equal("View only", f.ReadOnlyReason));
    }

    [Fact]
    public void A_Rename_That_Would_Touch_A_Read_Only_Trace_Link_Is_Greyed()
    {
        // Renaming a requirement baselines its trace links (suspect links); a link to a read-only package can't take that,
        // and a collaboration server would refuse the whole batch for it.
        var m = new ModelRepository("M");
        var reqs = m.Create(m.Root, ElementKind.Package, "Reqs");
        var locked = m.Create(m.Root, ElementKind.Package, "Locked");
        var req = m.Create(reqs, ElementKind.Requirement, "Pump flow");
        var plain = m.Create(reqs, ElementKind.Requirement, "Pump noise");
        var pump = m.Create(locked, ElementKind.Block, "Unit");
        m.Connect(ElementKind.Satisfy, pump, req);
        var bob = Guid.NewGuid();
        var view = AccessView.For([new(locked.Id, PrincipalKind.User, bob, PackageAccess.Read)], bob, [], PackageAccess.Edit, PackageAccess.Manage);
        var r = Find(m, "Pump", q => q with { Replace = "Valve", Access = view, In = SearchIn.Names });
        Assert.Contains("satisfy link with “Unit”", r.Hits.Single(h => ReferenceEquals(h.Element, req)).Fields.Single().ReadOnlyReason);
        Assert.Null(r.Hits.Single(h => ReferenceEquals(h.Element, plain)).Fields.Single().ReadOnlyReason);
        FindReplace.ReplaceAll(m, r, e => FindReplace.ReadOnlyReason(m, e, new FindQuery("x") { Access = view }));
        Assert.Equal(("Pump flow", "Valve noise"), (req.Name, plain.Name));
    }

    [Fact]
    public void Apply_Rechecks_Permissions_At_Apply_Time()
    {
        var m = new ModelRepository("M");
        var a = m.Create(m.Root, ElementKind.Block, "Pump");
        var r = Find(m, "Pump", q => q with { Replace = "Valve" });
        var plan = FindReplace.Plan(m, r.Hits.SelectMany(h => h.Fields.Select(f => (h, f, (IReadOnlyCollection<TextMatch>?)null))));
        var outcome = FindReplace.ApplyPlan(m, plan, _ => "Read-only for you");
        Assert.Equal("Pump", a.Name);
        Assert.Equal("Read-only for you", outcome.Skipped.Single().SkipReason);
        Assert.False(m.History.CanUndo && m.History.UndoDescription?.StartsWith("Replace") == true);
    }

    // ------------------------------------------------------------------ performance

    [Fact]
    public void Plain_Search_Of_A_20k_Model_Is_Fast()
    {
        var m = LargeModelGenerator.Generate(LargeModelOptions.Cameo(20_000));
        var sw = Stopwatch.StartNew();
        var r = FindReplace.Run(m, new FindQuery("pump") { MaxMatches = int.MaxValue });
        sw.Stop();
        output.WriteLine($"{m.Count:N0} elements: {r.Matches:N0} matches in {r.Hits.Count:N0} elements, {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
    }

    [SkippableFact]
    public void Heavy_200k_Find_Replace_Budgets()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("MERIDIAN_HEAVY") is "1" or "true", "Set MERIDIAN_HEAVY=1 for the 200,000-element find.");
        var m = LargeModelGenerator.Generate(LargeModelOptions.Cameo(200_000));
        Assert.InRange(m.Count, 190_000, 215_000);
        FindReplace.Run(m, new FindQuery("warm"));   // JIT

        foreach (var (label, q) in new (string, FindQuery)[]
        {
            ("plain, rare", new FindQuery("no such text anywhere")),
            ("plain, common", new FindQuery("pump") { MaxMatches = int.MaxValue }),
            ("plain, case + word", new FindQuery("Pump") { MatchCase = true, WholeWord = true, MaxMatches = int.MaxValue }),
            ("plain, capped", new FindQuery("e")),
            ("regex", new FindQuery(@"\bmass\w*") { Regex = true, MaxMatches = int.MaxValue }),
            ("all fields", new FindQuery("pump") { In = SearchIn.All, MaxMatches = int.MaxValue }),
        })
        {
            var sw = Stopwatch.StartNew();
            var r = FindReplace.Run(m, q);
            sw.Stop();
            output.WriteLine($"{label}: {r.Matches:N0} matches in {r.Hits.Count:N0} elements{(r.Truncated ? " (capped)" : "")}, {sw.ElapsedMilliseconds} ms");
            Assert.Null(r.Error);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(label == "regex" ? 3 : 1.5), $"{label} took {sw.Elapsed}");
        }

        // Replacing a few thousand fields is one undo step.
        var hits = FindReplace.Run(m, new FindQuery("mass") { MatchCase = true, WholeWord = true, Replace = "weight", In = SearchIn.Names, MaxMatches = 5_000 });
        var watch = Stopwatch.StartNew();
        var outcome = FindReplace.ReplaceAll(m, hits);
        watch.Stop();
        output.WriteLine($"replace: {outcome.Applied:N0} fields applied, {outcome.Skipped.Count:N0} skipped, {watch.ElapsedMilliseconds} ms");
        Assert.True(outcome.Applied > 0);
        watch.Restart();
        m.History.Undo();
        output.WriteLine($"undo: {watch.ElapsedMilliseconds} ms");
        Assert.Empty(m.AllElements.Where(e => e.Name == "weight").Take(1));
    }
}
