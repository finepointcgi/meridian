using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Core.Validation;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>The aerospace, automotive and medical domain samples: consistent, valid, exportable and neatly drawn.</summary>
public class DomainSampleTests
{
    /// <summary>Template id, and the requirement ids each sample deliberately leaves unsatisfied and unverified.</summary>
    public static TheoryData<string, string[]> Samples => new()
    {
        { "cubesat", ["SAT-004"] },
        { "acc", ["ACC-007"] },
        { "infusion-pump", ["PUMP-005"] },
    };

    private static ModelRepository Build(string id) => Templates.CreateProject(id, Templates.FindProject(id)!.Name);

    private static string Signature(ModelRepository m) =>
        string.Join("\n", m.AllElements.Where(e => e.Kind != ElementKind.Diagram)
            .Select(e => $"{e.Kind}|{e.QualifiedName}|{string.Join(";", e.Fields.Where(f => f.Key != FieldKeys.Type).OrderBy(f => f.Key).Select(f => f.Key + "=" + f.Value))}" +
                         (e is Relationship r ? $"|{r.Source.QualifiedName}->{r.Target.QualifiedName}" : "") +
                         (Metamodel.IsTypedFeature(e.Kind) ? $"|type={m.TypeDisplayName(e)}" : ""))
            .OrderBy(s => s, StringComparer.Ordinal));

    private static bool IsLeafRequirement(Element r) => r.Kind == ElementKind.Requirement && !r.OwnedElements.Any(c => c.Kind == ElementKind.Requirement);

    [Theory]
    [MemberData(nameof(Samples))]
    public void Sample_Is_In_The_Gallery_And_Rich(string id, string[] gaps)
    {
        _ = gaps;
        var t = Templates.FindProject(id);
        Assert.NotNull(t);
        var m = Build(id);
        Assert.Equal(t!.Name, m.Root.Name);
        Assert.False(m.History.CanUndo);
        Assert.True(m.OfKind(ElementKind.Requirement).Count() >= 10, "requirements");
        Assert.True(m.OfKind(ElementKind.TestCase).Count() >= 8, "test cases");
        Assert.True(m.Diagrams.Count() >= 7, "diagrams");
        foreach (var type in new[] { DiagramType.Requirement, DiagramType.BlockDefinition, DiagramType.StateMachine })
            Assert.Contains(m.Diagrams, d => d.DiagramType == type);
        Assert.Contains(m.OfKind(ElementKind.TableView), _ => true);
        Assert.All(m.Diagrams, d => Assert.NotEmpty(d.Nodes));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Sample_Validates_With_No_Errors_And_Only_Its_Deliberate_Gaps(string id, string[] gaps)
    {
        var m = Build(id);
        var issues = new ModelValidator().Validate(m);
        var errors = issues.Where(i => i.Severity == Severity.Error).Select(i => $"{i.RuleId}: {i.Message}").ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));

        // Every leaf requirement is satisfied and verified, except the documented gaps.
        var unsatisfied = issues.Where(i => i.RuleId == "REQ-003").Select(i => i.Element.Get(FieldKeys.RequirementId)).Order().ToArray();
        var unverified = issues.Where(i => i.RuleId == "REQ-004").Select(i => i.Element.Get(FieldKeys.RequirementId)).Order().ToArray();
        Assert.Equal(gaps, unsatisfied);
        Assert.Equal(gaps, unverified);
        foreach (var gap in gaps)
        {
            var r = m.OfKind(ElementKind.Requirement).Single(x => x.Get(FieldKeys.RequirementId) == gap);
            Assert.Equal("proposed", r.Get(ReqFields.Status));
            Assert.NotEmpty(r.Get(ReqFields.Rationale));
        }

        // The other warnings are only the documented ones (built-in rules and the samples' own query rules).
        var otherWarnings = issues.Where(i => i.Severity == Severity.Warning && i.RuleId != "REQ-003").Select(i => $"{i.RuleId}: {i.Message}").ToList();
        if (id == "infusion-pump")
            Assert.Equal(["Hazard without risk control: Hazard H-07 Alarm Not Heard has no risk control (residual risk TBD)"], otherWarnings);
        else
            Assert.True(otherWarnings.Count == 0, string.Join("\n", otherWarnings));
        Assert.Empty(SuspectLinks.All(m));
        Assert.All(m.OfKind(ElementKind.Requirement).Where(IsLeafRequirement), r => Assert.NotEmpty(r.Get(FieldKeys.Text)));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Sample_Round_Trips_Through_The_Project_File(string id, string[] gaps)
    {
        _ = gaps;
        var m = Build(id);
        var json = ProjectFile.Serialize(m);
        var back = ProjectFile.Deserialize(json);
        Assert.Equal(Signature(m), Signature(back));
        Assert.Equal(m.Diagrams.Sum(d => d.Nodes.Count), back.Diagrams.Sum(d => d.Nodes.Count));
        Assert.Equal(m.Diagrams.Sum(d => d.Edges.Count), back.Diagrams.Sum(d => d.Edges.Count));
        Assert.Equal(json, ProjectFile.Serialize(back));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Sample_Round_Trips_Through_Xmi(string id, string[] gaps)
    {
        _ = gaps;
        var m = Build(id);
        var (back, report) = XmiExchange.Import(XmiExchange.ToXml(m));
        Assert.Empty(report.Warnings);
        Assert.Equal(Signature(m), Signature(back));
        Assert.Equal(m.Diagrams.Count(), back.Diagrams.Count());
        Assert.DoesNotContain(new ModelValidator().Validate(back), i => i.Severity == Severity.Error);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_Diagram_Renders_And_Is_Laid_Out_Without_Overlaps(string id, string[] gaps)
    {
        _ = gaps;
        var m = Build(id);
        foreach (var d in m.Diagrams)
        {
            Assert.StartsWith("<svg", SvgRenderer.Render(m, d));
            Assert.StartsWith("<svg", SvgRenderer.Render(m, d, theme: SvgTheme.Dark));

            // Top-level shapes never overlap (swimlanes, fragments and shapes nested in a container are exempt).
            var shapes = d.Nodes.Where(n => n.ParentId == null && n.Element.Kind is not (ElementKind.Partition or ElementKind.CombinedFragment))
                .Select(n => (n, b: Notation.EffectiveBounds(m, n, d.DiagramType))).ToList();
            for (var i = 0; i < shapes.Count; i++)
                for (var j = i + 1; j < shapes.Count; j++)
                    Assert.False(shapes[i].b.Intersects(shapes[j].b),
                        $"{d.Name}: '{shapes[i].n.Element.DisplayName}' overlaps '{shapes[j].n.Element.DisplayName}'");

            // Nested shapes stay inside their container (ports and parameters sit on its border).
            foreach (var child in d.Nodes.Where(n => n.ParentId != null))
            {
                var p = d.ParentOf(child)!.Bounds;
                var c = child.Bounds.Center;
                Assert.True(c.X >= p.X - 0.5 && c.X <= p.Right + 0.5 && c.Y >= p.Y - 0.5 && c.Y <= p.Bottom + 0.5, $"{d.Name}: {child.Element.DisplayName} outside its container");
            }
            // Every relationship drawn connects shapes on this diagram.
            Assert.All(d.Edges, e => Assert.True(d.Nodes.Contains(e.Source) && d.Nodes.Contains(e.Target)));
        }
    }

    [Fact]
    public void CubeSat_Budgets_Close_With_Margin()
    {
        var m = CubeSatSample.Create();
        var sat = m.OfKind(ElementKind.Block).Single(b => b.Name == "CubeSat");
        var r = Parametrics.Solve(m, sat);
        Assert.True(r.Complete, string.Join("; ", r.Unsolved.Concat(r.Problems)));
        double V(string n) => r.Values.Single(v => v.Property?.Name == n).Value!.Value;
        Assert.Equal(4.4, V("totalPower"), 6);
        Assert.InRange(V("powerMargin"), 10, 30);       // SAT-011: at least 10 %
        Assert.InRange(V("totalMass"), 3, 4);           // SAT-050: at most 4 kg

        var mc = MonteCarlo.Run(m, sat, 200);
        Assert.Contains("powerMargin", mc.Outputs);

        var study = m.OfKind(ElementKind.TradeStudy).Single();
        var result = TradeStudies.Evaluate(m, TradeStudyDefinition.Parse(study));
        Assert.Empty(result.Problems);
        Assert.Equal(3, result.Rows.Count);
    }

    [Fact]
    public void Acc_Functions_Are_Allocated_To_Ecus_And_Safety_Requirements_Carry_An_Asil()
    {
        var m = AdaptiveCruiseSample.Create();
        foreach (var a in m.OfKind(ElementKind.Action))
        {
            var target = Assert.Single(m.Outgoing(a, ElementKind.Allocate)).Target;
            Assert.Contains("ECU", target.Stereotypes);
        }
        var table = m.OfKind(ElementKind.TableView).Single(t => t.Name == "Safety requirements by ASIL");
        var rows = ModelTables.Build(m, TableDefinition.FromElement(table));
        Assert.Equal(10, rows.Count);
        Assert.All(rows, r => Assert.Contains(r.Cells[3], new[] { "A", "B", "C", "D" }));
        Assert.Equal("B", rows[0].Cells[3]);   // sorted by ASIL, highest first
        // Every safety goal is refined by at least one functional safety requirement.
        foreach (var sg in m.OfKind(ElementKind.Requirement).Where(r => r.Stereotypes.Contains("SafetyGoal")))
            Assert.NotEmpty(m.Incoming(sg, ElementKind.DeriveReqt));
    }

    [Fact]
    public void Infusion_Pump_Hazard_Table_Lists_Every_Hazard_With_Its_Controls()
    {
        var m = InfusionPumpSample.Create();
        var table = m.OfKind(ElementKind.TableView).Single(t => t.Name == "Hazard analysis");
        var def = TableDefinition.FromElement(table);
        var rows = ModelTables.Build(m, def);
        Assert.Equal(["H-01", "H-02", "H-03", "H-04", "H-05", "H-06", "H-07"], rows.Select(r => r.Cells[0]));
        var controls = def.Columns.FindIndex(c => c.Key == "tracedFrom");
        Assert.Contains("RC-004", rows[3].Cells[controls]);
        Assert.Contains("RC-007", rows[3].Cells[controls]);
        Assert.Equal("", rows[6].Cells[controls]);   // the deliberate gap
        Assert.StartsWith("ID,Hazard,Hazardous situation,Harm,Severity", ModelTables.ToCsv(def, rows));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Behaviours_Simulate_Without_Expression_Errors(string id, string[] gaps)
    {
        _ = gaps;
        var m = Build(id);
        foreach (var b in m.AllElements.Where(e => e.Kind is ElementKind.Activity or ElementKind.StateMachine))
        {
            var sim = new DiscreteEventSimulation(m, [b]);
            sim.Start();
            sim.RunUntil(10 * 3600);
            Assert.DoesNotContain(sim.Trace, t => t.Kind == TraceKind.Warning);
            if (b.Kind == ElementKind.Activity) Assert.True(sim.Finished, b.Name);
        }
    }
}
