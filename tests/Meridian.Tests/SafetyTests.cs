using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Safety;
using Meridian.Core.Samples;
using Meridian.Core.Validation;
using Meridian.Serialization;
using Meridian.Serialization.Reports;

namespace Meridian.Tests;

/// <summary>FMEA (RPN, action priority, worksheet, CSV), fault tree analysis (cut sets, probability), validation, impact and exchange.</summary>
public class SafetyTests
{
    private const double Eps = 1e-12;

    // ------------------------------------------------------------------ FMEA

    [Theory]
    [InlineData(1, 1, 1, 1)]
    [InlineData(9, 3, 4, 108)]
    [InlineData(10, 10, 10, 1000)]
    public void Rpn_Is_Severity_Times_Occurrence_Times_Detection(int s, int o, int d, int rpn) =>
        Assert.Equal(rpn, Fmea.Rpn(s, o, d));

    [Fact]
    public void Rpn_Needs_All_Three_Ratings_In_Range()
    {
        var m = new ModelRepository("M");
        var b = m.Create(m.Root, ElementKind.Block, "Pump");
        var fm = m.Create(b, ElementKind.FailureMode, "Leak");
        Assert.Null(Fmea.Rpn(fm));
        m.Set(fm, SafetyFields.Severity, "8");
        m.Set(fm, SafetyFields.Occurrence, "3");
        Assert.Null(Fmea.Rpn(fm));
        m.Set(fm, SafetyFields.Detection, "11");   // out of range
        Assert.Null(Fmea.Rpn(fm));
        Assert.True(SafetyFields.InvalidRating(fm, SafetyFields.Detection));
        m.Set(fm, SafetyFields.Detection, " 5 ");
        Assert.Equal(120, Fmea.Rpn(fm));
    }

    [Theory]   // AIAG-VDA design FMEA action priority table, one probe per band
    [InlineData(10, 8, 1, ActionPriority.High)]
    [InlineData(9, 6, 1, ActionPriority.High)]
    [InlineData(9, 4, 1, ActionPriority.Medium)]
    [InlineData(9, 4, 2, ActionPriority.High)]
    [InlineData(9, 2, 7, ActionPriority.High)]
    [InlineData(9, 2, 5, ActionPriority.Medium)]
    [InlineData(9, 2, 4, ActionPriority.Low)]
    [InlineData(10, 1, 10, ActionPriority.Low)]
    [InlineData(8, 8, 1, ActionPriority.High)]
    [InlineData(7, 6, 1, ActionPriority.Medium)]
    [InlineData(7, 4, 7, ActionPriority.High)]
    [InlineData(7, 4, 2, ActionPriority.Medium)]
    [InlineData(8, 3, 5, ActionPriority.Medium)]
    [InlineData(8, 3, 4, ActionPriority.Low)]
    [InlineData(6, 8, 5, ActionPriority.High)]
    [InlineData(6, 8, 4, ActionPriority.Medium)]
    [InlineData(5, 6, 2, ActionPriority.Medium)]
    [InlineData(5, 6, 1, ActionPriority.Low)]
    [InlineData(4, 4, 7, ActionPriority.Medium)]
    [InlineData(4, 4, 6, ActionPriority.Low)]
    [InlineData(4, 3, 10, ActionPriority.Low)]
    [InlineData(3, 9, 6, ActionPriority.Medium)]
    [InlineData(3, 9, 4, ActionPriority.Low)]
    [InlineData(2, 7, 10, ActionPriority.Low)]
    [InlineData(1, 10, 10, ActionPriority.Low)]
    public void Action_Priority_Follows_The_Aiag_Vda_Table(int s, int o, int d, ActionPriority expected) =>
        Assert.Equal(expected, Fmea.Priority(s, o, d));

    [Fact]
    public void Action_Priority_Is_Monotonic_In_Every_Rating()
    {
        for (var s = 1; s <= 10; s++)
            for (var o = 1; o <= 10; o++)
                for (var d = 1; d <= 10; d++)
                {
                    var ap = Fmea.Priority(s, o, d)!.Value;
                    if (s < 10) Assert.True(Fmea.Priority(s + 1, o, d) >= ap, $"S {s}->{s + 1} at O{o} D{d}");
                    if (o < 10) Assert.True(Fmea.Priority(s, o + 1, d) >= ap, $"O {o}->{o + 1} at S{s} D{d}");
                    if (d < 10) Assert.True(Fmea.Priority(s, o, d + 1) >= ap, $"D {d}->{d + 1} at S{s} O{o}");
                }
    }

    [Fact]
    public void Worksheet_Rows_Link_Items_Mitigations_And_Tests_Sorted_By_Rpn()
    {
        var m = ParkingBrakeSample.Create();
        var rows = Fmea.Rows(m);
        Assert.Equal(4, rows.Count);
        Assert.Equal(rows.Select(r => r.Rpn).OrderByDescending(x => x), rows.Select(r => r.Rpn));
        var top = rows[0];
        Assert.Equal("Wheel speed signal stuck", top.FailureMode.Name);
        Assert.Equal(245, top.Rpn);
        Assert.Equal(ActionPriority.High, top.Priority);
        Assert.Equal("Wheel Speed Sensor", top.Item!.Name);
        Assert.Empty(top.Mitigations);

        var stall = rows.Single(r => r.FailureMode.Name == "Motor stall");
        Assert.Equal("EPB-011", stall.MitigatingRequirements.Single().Get(FieldKeys.RequirementId));
        Assert.Equal(["Stall injection test"], stall.VerifiedBy.Select(t => t.Name));   // direct verify and via the requirement: listed once

        var hang = rows.Single(r => r.FailureMode.Name == "Software hang");
        Assert.Equal(["Watchdog reset test"], hang.VerifiedBy.Select(t => t.Name));       // through the mitigating requirement
    }

    [Fact]
    public void Failure_Mode_In_A_Package_Belongs_To_What_It_Is_Allocated_To()
    {
        var m = new ModelRepository("M");
        var pkg = m.Create(m.Root, ElementKind.Package, "Safety");
        var fn = m.Create(m.Root, ElementKind.Activity, "Brake");
        var fm = m.Create(pkg, ElementKind.FailureMode, "No braking");
        Assert.Null(Fmea.ItemOf(m, fm));
        m.Connect(ElementKind.Allocate, fm, fn);
        Assert.Same(fn, Fmea.ItemOf(m, fm));
    }

    [Fact]
    public void Csv_Export_Quotes_And_Neutralizes_Formulas()
    {
        var m = new ModelRepository("M");
        var b = m.Create(m.Root, ElementKind.Block, "Valve");
        var fm = m.Create(b, ElementKind.FailureMode, "Sticks, closed");
        m.Set(fm, SafetyFields.Effect, "=HYPERLINK(\"x\")");
        m.Set(fm, SafetyFields.Severity, "7"); m.Set(fm, SafetyFields.Occurrence, "2"); m.Set(fm, SafetyFields.Detection, "3");
        var csv = Fmea.ToCsv(m);
        var lines = csv.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("Item,Function,Failure mode,Effect,Cause,S,O,D,RPN,AP", lines[0]);
        Assert.Contains("\"Sticks, closed\"", lines[1]);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", lines[1]);
        Assert.Contains(",7,2,3,42,L,", lines[1]);
    }

    // ------------------------------------------------------------------ fault trees

    private sealed class Tree(ModelRepository m)
    {
        public readonly ModelRepository M = m;
        public Element Event(string name, string kind = SafetyFields.Basic, double? p = null)
        {
            var e = M.Create(M.Root, ElementKind.FaultTreeEvent, name);
            M.Set(e, FieldKeys.Subtype, kind);
            if (p is { } v) M.Set(e, SafetyFields.Probability, v.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            return e;
        }
        public Element Gate(string kind, params Element[] inputs)
        {
            var g = M.Create(M.Root, ElementKind.FaultTreeGate);
            M.Set(g, FieldKeys.Subtype, kind);
            foreach (var i in inputs) M.Connect(ElementKind.FaultTreeLink, g, i);
            return g;
        }
        public Element Top(Element gate)
        {
            var t = Event("Top", SafetyFields.Top);
            M.Connect(ElementKind.FaultTreeLink, t, gate);
            return t;
        }
    }

    private static string[][] Sets(FaultTreeResult r) =>
        r.CutSets.Select(c => c.Events.Select(e => e.Name).OrderBy(n => n).ToArray()).OrderBy(s => s.Length).ThenBy(s => string.Join(",", s)).ToArray();

    [Fact]
    public void And_Or_Tree_Has_Expected_Cut_Sets_And_Probability()
    {
        var t = new Tree(new ModelRepository("M"));
        var a = t.Event("A", p: 0.1); var b = t.Event("B", p: 0.2); var c = t.Event("C", p: 0.3);
        var top = t.Top(t.Gate(SafetyFields.Or, a, t.Gate(SafetyFields.And, b, c)));
        var r = FaultTree.Analyze(t.M, top);
        Assert.Equal([["A"], ["B", "C"]], Sets(r));
        Assert.Equal(1 - (1 - 0.1) * (1 - 0.06), r.Probability!.Value, Eps);
        Assert.Equal(0.1 + 0.06, r.RareEvent, Eps);
        Assert.Equal(1 - 0.9 * 0.94, r.MinCutUpperBound, Eps);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Repeated_Events_Are_Absorbed_And_Evaluated_Exactly()
    {
        // Top = (A and B) or (A and C): cut sets {A,B}, {A,C}; P = pA · (1 − (1−pB)(1−pC)), not the naive independent OR.
        var t = new Tree(new ModelRepository("M"));
        var a = t.Event("A", p: 0.5); var b = t.Event("B", p: 0.4); var c = t.Event("C", p: 0.2);
        var top = t.Top(t.Gate(SafetyFields.Or, t.Gate(SafetyFields.And, a, b), t.Gate(SafetyFields.And, a, c)));
        var r = FaultTree.Analyze(t.M, top);
        Assert.Equal([["A", "B"], ["A", "C"]], Sets(r));
        Assert.Equal(0.5 * (1 - 0.6 * 0.8), r.Probability!.Value, Eps);
        Assert.NotEqual(1 - (1 - 0.2) * (1 - 0.1), r.Probability!.Value, 6);

        // Absorption: A or (A and B) has the single cut set {A}.
        var t2 = new Tree(new ModelRepository("M"));
        var a2 = t2.Event("A", p: 0.3); var b2 = t2.Event("B", p: 0.9);
        var r2 = FaultTree.Analyze(t2.M, t2.Top(t2.Gate(SafetyFields.Or, a2, t2.Gate(SafetyFields.And, a2, b2))));
        Assert.Equal([["A"]], Sets(r2));
        Assert.Equal(0.3, r2.Probability!.Value, Eps);
    }

    [Fact]
    public void Vote_Gate_Takes_Every_K_Combination()
    {
        var t = new Tree(new ModelRepository("M"));
        var a = t.Event("A", p: 0.1); var b = t.Event("B", p: 0.2); var c = t.Event("C", p: 0.3);
        var vote = t.Gate(SafetyFields.Vote, a, b, c);
        t.M.Set(vote, SafetyFields.K, "2");
        var r = FaultTree.Analyze(t.M, t.Top(vote));
        Assert.Equal([["A", "B"], ["A", "C"], ["B", "C"]], Sets(r));
        // P(at least 2 of 3) = ab + ac + bc − 2abc
        Assert.Equal(0.02 + 0.03 + 0.06 - 2 * 0.006, r.Probability!.Value, Eps);
        Assert.Equal(FaultTree.AtLeast(2, [0.1, 0.2, 0.3]), r.Probability!.Value, Eps);
    }

    [Fact]
    public void House_Events_Switch_Branches_On_And_Off()
    {
        var t = new Tree(new ModelRepository("M"));
        var a = t.Event("A", p: 0.1);
        var house = t.Event("Maintenance mode", SafetyFields.House);
        var top = t.Top(t.Gate(SafetyFields.And, a, house));
        Assert.Empty(FaultTree.Analyze(t.M, top).CutSets);
        Assert.Equal(0, FaultTree.Analyze(t.M, top).Probability!.Value, Eps);
        t.M.Set(house, SafetyFields.HouseState, "true");
        var r = FaultTree.Analyze(t.M, top);
        Assert.Equal([["A"]], Sets(r));
        Assert.Equal(0.1, r.Probability!.Value, Eps);
    }

    [Fact]
    public void Basic_Event_Probability_From_Rate_And_Time_Or_Linked_Failure_Mode()
    {
        var m = new ModelRepository("M");
        var t = new Tree(m);
        var e = t.Event("E");
        Assert.Null(FaultTree.ProbabilityOf(m, e));
        m.Set(e, SafetyFields.FailureRate, "1e-5");
        m.Set(e, SafetyFields.ExposureTime, "1000");
        Assert.Equal(1 - Math.Exp(-0.01), FaultTree.ProbabilityOf(m, e)!.Value, Eps);

        var b = m.Create(m.Root, ElementKind.Block, "Pump");
        var fm = m.Create(b, ElementKind.FailureMode, "Seal leak");
        m.Set(fm, SafetyFields.Probability, "0.004");
        var f = t.Event("F");
        m.Set(f, SafetyFields.Ref, fm.Id);
        Assert.Equal(0.004, FaultTree.ProbabilityOf(m, f)!.Value, Eps);
        m.Set(f, SafetyFields.Probability, "0.5");   // the event's own value wins
        Assert.Equal(0.5, FaultTree.ProbabilityOf(m, f)!.Value, Eps);
    }

    [Fact]
    public void Importance_Measures_Rank_The_Single_Point_Failure_First()
    {
        var t = new Tree(new ModelRepository("M"));
        var a = t.Event("A", p: 0.02); var b = t.Event("B", p: 0.1); var c = t.Event("C", p: 0.1);
        var r = FaultTree.Analyze(t.M, t.Top(t.Gate(SafetyFields.Or, a, t.Gate(SafetyFields.And, b, c))));
        Assert.Equal("A", r.Importance[0].Event.Name);
        // Birnbaum of A: P(top|A) − P(top|¬A) = 1 − P(B and C) = 1 − 0.01
        Assert.Equal(1 - 0.01, r.Importance[0].Birnbaum!.Value, 1e-9);
        Assert.Equal(0.02 / 0.03, r.Importance[0].FussellVesely, 1e-9);
    }

    [Fact]
    public void Sample_Fault_Tree_Matches_A_Brute_Force_Evaluation()
    {
        var m = ParkingBrakeSample.Create();
        var top = FaultTree.TopEvents(m).Single();
        var r = FaultTree.Analyze(m, top);
        Assert.Empty(r.Warnings);
        // Single points of failure: motor stall, software hang, spurious release; undervoltage alone (it also blinds the sensors).
        Assert.Equal(4, r.CutSets.Count(c => c.Order == 1));
        Assert.Contains(r.CutSets, c => c.Order == 1 && c.Events.Single().Name == "Undervoltage");
        Assert.DoesNotContain(r.CutSets, c => c.Events.Any(e => e.Name == "Backup supply fails"));   // absorbed by {Undervoltage}
        Assert.Equal(3, r.CutSets.Count(c => c.Order == 2));                                        // 2 of 3 wheel speed sensors

        // Brute force over all 2^n states of the basic events.
        var leaves = r.BasicEvents;
        var p = leaves.Select(l => FaultTree.ProbabilityOf(m, l)!.Value).ToArray();
        double total = 0;
        for (var mask = 0; mask < 1 << leaves.Count; mask++)
        {
            double w = 1;
            for (var i = 0; i < leaves.Count; i++) w *= (mask >> i & 1) == 1 ? p[i] : 1 - p[i];
            var occurs = r.CutSets.Any(c => c.Events.All(e => (mask >> leaves.ToList().IndexOf(e) & 1) == 1));
            if (occurs) total += w;
        }
        Assert.Equal(total, r.Probability!.Value, 1e-15);
        Assert.True(r.RareEvent >= r.Probability!.Value - 1e-15);
        Assert.True(r.MinCutUpperBound >= r.Probability!.Value - 1e-15);
        Assert.Equal(r.Probability!.Value, FaultTree.CachedProbability(m, top)!.Value, 1e-15);
    }

    [Fact]
    public void Cycles_And_Empty_Gates_Are_Reported_Not_Fatal()
    {
        var t = new Tree(new ModelRepository("M"));
        var a = t.Event("A", SafetyFields.Intermediate);
        var g = t.Gate(SafetyFields.Or, a);
        t.M.Connect(ElementKind.FaultTreeLink, a, g);   // a -> g -> a
        var empty = t.Gate(SafetyFields.And);
        var r = FaultTree.Analyze(t.M, a);
        Assert.Contains(r.Warnings, w => w.Contains("cycle"));
        var issues = new ModelValidator().Validate(t.M);
        Assert.Contains(issues, i => i.RuleId == "FTA-008");
        Assert.Contains(issues, i => i.RuleId == "FTA-001" && ReferenceEquals(i.Element, empty));
    }

    // ------------------------------------------------------------------ validation, impact, metamodel

    [Fact]
    public void High_Risk_Failure_Mode_Without_Mitigating_Requirement_Is_Flagged()
    {
        var m = ParkingBrakeSample.Create();
        var issues = new ModelValidator().Validate(m);
        var saf3 = issues.Where(i => i.RuleId == "SAF-003").ToList();
        Assert.Equal(["Wheel speed signal stuck"], saf3.Select(i => i.Element.Name));
        Assert.DoesNotContain(issues, i => i.RuleId.StartsWith("FTA-", StringComparison.Ordinal));

        var stuck = saf3[0].Element;
        var req = m.Create(m.Root, ElementKind.Requirement, "Plausibility check");
        m.Connect(ElementKind.Mitigate, req, stuck);
        issues = new ModelValidator().Validate(m);
        Assert.DoesNotContain(issues, i => i.RuleId == "SAF-003");
        Assert.Contains(issues, i => i.RuleId == "SAF-004" && ReferenceEquals(i.Element, stuck));   // mitigation not yet verified
    }

    [Fact]
    public void Validation_Reports_Bad_Ratings_Bad_K_And_Missing_Probabilities()
    {
        var m = new ModelRepository("M");
        var pkg = m.Create(m.Root, ElementKind.Package, "P");
        var fm = m.Create(pkg, ElementKind.FailureMode, "Orphan");
        m.Set(fm, SafetyFields.Severity, "12");
        var t = new Tree(m);
        var a = t.Event("A");
        var vote = t.Gate(SafetyFields.Vote, a);
        m.Set(vote, SafetyFields.K, "3");
        t.Top(vote);
        var ids = new ModelValidator().Validate(m).Select(i => i.RuleId).ToHashSet();
        Assert.Contains("SAF-001", ids);   // severity 12
        Assert.Contains("SAF-005", ids);   // not on a block / part / function
        Assert.Contains("FTA-002", ids);   // k = 3 of 1
        Assert.Contains("FTA-005", ids);   // A has no probability
    }

    [Fact]
    public void Impact_Of_A_Block_Reaches_Its_Failure_Modes_Fault_Tree_And_Mitigations()
    {
        var m = ParkingBrakeSample.Create();
        var actuator = m.OfKind(ElementKind.Block).Single(b => b.Name == "Motor Actuator");
        var impacted = Traceability.Impact(m, actuator).Select(x => x.Element).ToList();
        Assert.Contains(impacted, e => e.Kind == ElementKind.FailureMode && e.Name == "Motor stall");
        Assert.Contains(impacted, e => e.Kind == ElementKind.FaultTreeEvent && e.Name == "Motor stall");
        Assert.Contains(impacted, e => e.Kind == ElementKind.FaultTreeEvent && e.Name == "Loss of parking brake hold");   // up the tree
        Assert.Contains(impacted, e => e.Kind == ElementKind.Requirement && e.Get(FieldKeys.RequirementId) == "EPB-011");
    }

    [Fact]
    public void Metamodel_Rules_For_Safety_Kinds()
    {
        Assert.True(Metamodel.CanOwn(ElementKind.Block, ElementKind.FailureMode));
        Assert.True(Metamodel.CanOwn(ElementKind.Property, ElementKind.FailureMode));
        Assert.True(Metamodel.CanOwn(ElementKind.Activity, ElementKind.FailureMode));
        Assert.True(Metamodel.CanOwn(ElementKind.Action, ElementKind.FailureMode));
        Assert.True(Metamodel.CanOwn(ElementKind.Package, ElementKind.FaultTreeEvent));
        Assert.False(Metamodel.CanOwn(ElementKind.Requirement, ElementKind.FailureMode));
        Assert.Null(Metamodel.CheckEnds(ElementKind.Mitigate, ElementKind.Requirement, ElementKind.FailureMode));
        Assert.NotNull(Metamodel.CheckEnds(ElementKind.Mitigate, ElementKind.FailureMode, ElementKind.Requirement));
        Assert.Null(Metamodel.CheckEnds(ElementKind.Verify, ElementKind.TestCase, ElementKind.FailureMode));
        Assert.Null(Metamodel.CheckEnds(ElementKind.FaultTreeLink, ElementKind.FaultTreeGate, ElementKind.FaultTreeEvent));
        Assert.NotNull(Metamodel.CheckEnds(ElementKind.FaultTreeLink, ElementKind.Block, ElementKind.FaultTreeEvent));
        Assert.Equal(ElementKind.Mitigate, ConnectorRules.Default(DiagramType.Requirement, ElementKind.Requirement, ElementKind.FailureMode)!.Kind);
        Assert.Equal(ElementKind.FaultTreeLink, ConnectorRules.Default(DiagramType.FaultTree, ElementKind.FaultTreeEvent, ElementKind.FaultTreeGate)!.Kind);
        Assert.Contains(Metamodel.PaletteItems(DiagramType.FaultTree), i => i.Kind == ElementKind.FaultTreeGate && i.Subtype == SafetyFields.Vote);
        Assert.True(Metamodel.IsValidDiagramContext(DiagramType.FaultTree, ElementKind.Package));
        Assert.False(Metamodel.IsValidDiagramContext(DiagramType.FaultTree, ElementKind.Block));
    }

    // ------------------------------------------------------------------ exchange, rendering, reports

    private static string Signature(ModelRepository m) =>
        string.Join("\n", m.AllElements.Where(e => e.Kind != ElementKind.Diagram)
            .Select(e => $"{e.Kind}|{e.QualifiedName}|{string.Join(";", e.Fields.Where(f => f.Key != FieldKeys.Type).OrderBy(f => f.Key).Select(f => f.Key + "=" + f.Value))}" +
                         (e is Relationship r ? $"|{r.Source.QualifiedName}->{r.Target.QualifiedName}" : ""))
            .OrderBy(s => s, StringComparer.Ordinal));

    [Fact]
    public void Safety_Model_Round_Trips_Through_Project_File_And_Xmi()
    {
        var m = ParkingBrakeSample.Create();
        var json = ProjectFile.Serialize(m);
        var back = ProjectFile.Deserialize(json);
        Assert.Equal(Signature(m), Signature(back));
        Assert.Equal(json, ProjectFile.Serialize(back));

        var (viaXmi, report) = XmiExchange.Import(XmiExchange.ToXml(m));
        Assert.Empty(report.Warnings);
        Assert.Equal(Signature(m), Signature(viaXmi));
        var fta = viaXmi.Diagrams.Single(d => d.DiagramType == DiagramType.FaultTree);
        Assert.Equal(m.Diagrams.Single(d => d.DiagramType == DiagramType.FaultTree).Edges.Count, fta.Edges.Count);

        // Analysis results survive the round trip.
        Assert.Equal(FaultTree.Analyze(m, FaultTree.TopEvents(m).Single()).Probability!.Value,
                     FaultTree.Analyze(viaXmi, FaultTree.TopEvents(viaXmi).Single()).Probability!.Value, 1e-15);
        Assert.Equal(Fmea.ToCsv(m).Split('\n').Length, Fmea.ToCsv(viaXmi).Split('\n').Length);
    }

    [Fact]
    public void Fault_Tree_Diagram_Renders_Gates_Events_And_Probabilities()
    {
        var m = ParkingBrakeSample.Create();
        var d = m.Diagrams.Single(x => x.DiagramType == DiagramType.FaultTree);
        var svg = SvgRenderer.Render(m, d);
        Assert.Contains(">AND<", svg);
        Assert.Contains(">OR<", svg);
        Assert.Contains(">2/3<", svg);
        Assert.Contains("P = 1.00E-4", svg);   // motor stall, from its failure mode
        Assert.Contains("Loss of parking", svg);   // wrapped over lines
        // Blocks list their failure modes.
        var bdd = m.Diagrams.Single(x => x.DiagramType == DiagramType.BlockDefinition);
        Assert.Contains("Motor stall (RPN 160)", SvgRenderer.Render(m, bdd));
    }

    [Fact]
    public void Fault_Tree_Layout_Centres_Outputs_Over_Inputs_And_Links_Run_Tree_Style()
    {
        var m = ParkingBrakeSample.Create();
        var d = m.Diagrams.Single(x => x.DiagramType == DiagramType.FaultTree);
        var layout = AutoLayout.Layered(d);   // fault trees use the tidy tree layout
        DiagramNode Node(string name) => d.Nodes.First(n => n.Element.Name == name);
        var top = layout[Node("Loss of parking brake hold")];
        var cmd = layout[Node("Controller commands release")];
        Assert.True(cmd.Y > top.Bottom);
        // Each output is centred over its inputs: the gate under "Controller commands release" sits under its centre.
        var gate = d.Nodes.Single(n => n.Element.Kind == ElementKind.FaultTreeGate && FaultTree.Outputs(m, n.Element).Any(o => o.Name == "Controller commands release"));
        Assert.InRange(Math.Abs(cmd.Center.X - layout[gate].Center.X), 0, 2);
        // No two shapes overlap.
        var boxes = layout.Values.ToList();
        for (var i = 0; i < boxes.Count; i++)
            for (var j = i + 1; j < boxes.Count; j++)
                Assert.False(boxes[i].Intersects(boxes[j]), $"{boxes[i]} overlaps {boxes[j]}");

        var link = d.Edges.First(e => e.Source.Element.Kind == ElementKind.FaultTreeGate && e.Target.Element.Name == "Motor stall");
        var route = Notation.RouteIn(d, link);
        Assert.Equal(link.Source.Bounds.Bottom, route[0].Y, 3);
        Assert.Equal(link.Target.Bounds.Y, route[^1].Y, 3);
        Assert.All(EdgeRouter.Segments(route), s => Assert.True(Math.Abs(s.A.X - s.B.X) < 0.01 || Math.Abs(s.A.Y - s.B.Y) < 0.01));
    }

    [Fact]
    public void Report_Blocks_Emit_The_Fmea_Worksheet_And_Fault_Tree_Tables()
    {
        var m = ParkingBrakeSample.Create();
        var t = new DocTemplate { Name = "Safety" };
        t.Sections.Add(new DocSection { Title = "FMEA", Blocks = [new FmeaBlock { HighRiskOnly = true }, new FaultTreeBlock()] });
        var doc = DocResolver.Resolve(m, t, new ResolveOptions { Date = new DateTime(2026, 1, 2) });
        var tables = doc.Nodes.OfType<TableNode>().ToList();
        var fmea = tables.First(x => x.Headers.Contains("RPN"));
        Assert.Equal(3, fmea.Rows.Count);   // RPN 245, 160, 108 (≥ 100)
        Assert.Equal("245", fmea.Rows[0][7]);
        Assert.Contains(tables, x => x.Rows.Any(r => r[0] == "Minimal cut sets"));
        Assert.Contains(tables, x => x.Headers.Contains("Basic events") && x.Rows.Count == 7);
        var html = DocHtml.Write(doc);
        Assert.Contains("Wheel speed signal stuck", html);
    }
}
