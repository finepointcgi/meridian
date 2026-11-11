using System.Xml.Linq;
using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;
using Meridian.Serialization.Reports;

namespace Meridian.Tests;

/// <summary>Several verification criteria per requirement: own results and staleness, the aggregate, migration, history, XMI, VCRM.</summary>
public class VerificationCriteriaTests
{
    private static ModelRepository Clone(ModelRepository m) => ProjectFile.Deserialize(ProjectFile.Serialize(m));
    private static Element Req(ModelRepository m, string id) => m.OfKind(ElementKind.Requirement).Single(r => r.Get(FieldKeys.RequirementId) == id);
    private static readonly VerificationOptions Opts = new() { Clone = Clone, Now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc) };

    /// <summary>The vehicle sample with REQ-002 checked by acceleration (pass, 7.192 s) and mass (fail, 1450 kg &gt; 1400 kg).</summary>
    private static (ModelRepository M, Element Accel) TwoCriteria(string second = "mass <= 1400 kg")
    {
        var m = SampleModels.Vehicle();
        var accel = Req(m, "REQ-002");
        Verification.AddCriterion(m, accel, second);
        return (m, accel);
    }

    [Fact]
    public void Criteria_Are_One_Per_Line_And_Edited_In_One_Undo_Step_Each()
    {
        var m = SampleModels.Vehicle();
        var r = Req(m, "REQ-002");
        Assert.Equal(["accelTime < 8 s"], Verification.Criteria(r));
        Verification.AddCriterion(m, r, "  mass <= 1500 kg ");
        Assert.Equal(["accelTime < 8 s", "mass <= 1500 kg"], Verification.Criteria(r));
        Assert.Equal("accelTime < 8 s\nmass <= 1500 kg", r.Get(VerificationKeys.Criterion));
        Verification.SetCriterionAt(m, r, 1, "mass <=\n1400 kg");                // a line break inside one criterion is a space
        Assert.Equal("mass <= 1400 kg", Verification.Criteria(r)[1]);
        Verification.SetCriterionAt(m, r, 0, " ");                             // blank removes
        Assert.Equal(["mass <= 1400 kg"], Verification.Criteria(r));
        m.History.Undo();
        Assert.Equal(2, Verification.Criteria(r).Count);
        Verification.RemoveCriterion(m, r, 1);
        Assert.Equal(["accelTime < 8 s"], Verification.Criteria(r));
        Verification.RemoveCriterion(m, r, 0);
        Assert.False(Verification.HasCriterion(r));
        Assert.Equal("", r.Get(VerificationKeys.Criterion));
    }

    [Fact]
    public void The_Aggregate_Fails_If_Any_Fails_And_Is_Inconclusive_If_None_Fail_But_One_Is()
    {
        Assert.Equal(Verdict.Pass, Verification.Aggregate([Verdict.Pass, Verdict.Pass]));
        Assert.Equal(Verdict.Inconclusive, Verification.Aggregate([Verdict.Pass, Verdict.Inconclusive]));
        Assert.Equal(Verdict.Fail, Verification.Aggregate([Verdict.Inconclusive, Verdict.Fail, Verdict.Pass]));
        Assert.Equal(Verdict.Inconclusive, Verification.Aggregate([]));

        var m = SampleModels.Vehicle();
        var power = Req(m, "REQ-005");
        Verification.AddCriterion(m, power, "noSuchValue > 1");
        var report = Verification.Execute(m, null, [power], Opts);
        var r = Assert.Single(report.Results);
        Assert.Equal(Verdict.Inconclusive, r.Verdict);
        Assert.Equal([Verdict.Pass, Verdict.Inconclusive], r.Checks.Select(c => c.Verdict));
        Assert.Equal(1, report.Inconclusive);
    }

    [Fact]
    public void Each_Criterion_Has_Its_Own_Result_And_The_Requirement_The_Aggregate()
    {
        var (m, accel) = TwoCriteria();
        var report = Assert.Single(Verification.RunAll(m, Opts));
        var r = report.Results.Single(x => x.Requirement == accel);
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Equal(2, r.Checks.Count);
        Assert.Equal((Verdict.Pass, "7.192 s"), (r.Checks[0].Verdict, r.Checks[0].Value));
        Assert.Equal((Verdict.Fail, "1450 kg"), (r.Checks[1].Verdict, r.Checks[1].Value));
        Assert.Contains("1 of 2 criteria pass", r.Message);
        Assert.Contains("criterion 2 (mass <= 1400 kg)", r.Message);
        Assert.Equal("7.192 s; 1450 kg", r.Value);
        Assert.StartsWith("-50 kg", r.Margin);                                    // the tightest margin
        Assert.Equal(2, report.Fail);                                            // REQ-002 and REQ-003

        // Recorded: the aggregate in the usual fields, each criterion in verify.criteria.
        var stored = Verification.LastResult(m, accel)!;
        Assert.Equal("fail", stored.Status);
        Assert.False(stored.Stale);
        Assert.Equal(["pass", "fail"], stored.Checks.Select(c => c.Status));
        Assert.All(stored.Checks, c => Assert.False(c.Stale));
        Assert.NotEmpty(accel.Get(VerificationKeys.CriteriaResults));
        Assert.Equal(1, Verification.Summarize(m).Pass);                         // REQ-005 only
        Assert.Equal(2, Verification.Summarize(m).Fail);

        // Survives save / load.
        var copy = Clone(m);
        var back = Verification.LastResult(copy, Req(copy, "REQ-002"))!;
        Assert.Equal(["pass", "fail"], back.Checks.Select(c => c.Status));
        Assert.False(back.Stale);

        // Undo removes all of it in one step.
        m.History.Undo();
        Assert.Null(Verification.LastResult(m, accel));
        Assert.Equal("", accel.Get(VerificationKeys.CriteriaResults));
    }

    [Fact]
    public void Each_Criterion_Goes_Stale_On_Its_Own()
    {
        var (m, accel) = TwoCriteria("parametric(FuelTank): capacity >= 50 L");
        Verification.RunAll(m, Opts);
        Assert.Equal("pass", Verification.LastResult(m, accel)!.Status);

        // A change only the first criterion reads.
        var mass = m.OfKind(ElementKind.Property).Single(p => p.Name == "mass" && p.Owner!.Name == "Vehicle");
        m.Set(mass, FieldKeys.DefaultValue, "1600");
        var stored = Verification.LastResult(m, accel)!;
        Assert.True(stored.Stale);
        Assert.Equal([true, false], stored.Checks.Select(c => c.Stale));

        // An edited criterion, like a new one, has no result until the next run; the others keep theirs.
        m.Set(mass, FieldKeys.DefaultValue, "1450");
        Assert.False(Verification.LastResult(m, accel)!.Stale);
        Verification.SetCriterionAt(m, accel, 1, "parametric(FuelTank): capacity >= 40 L");
        stored = Verification.LastResult(m, accel)!;
        Assert.True(stored.Stale);
        Assert.Equal([false, true], stored.Checks.Select(c => c.NotRun));
        Assert.False(stored.Checks[0].Stale);
        Verification.AddCriterion(m, accel, "mass <= 1500 kg");
        stored = Verification.LastResult(m, accel)!;
        Assert.True(stored.Checks[2].NotRun);
        Assert.Equal("not run", stored.Checks[2].Label);

        // Removing a criterion keeps the others' results (matched by their text).
        Verification.RemoveCriterion(m, accel, 1);
        stored = Verification.LastResult(m, accel)!;
        Assert.Equal(["accelTime < 8 s", "mass <= 1500 kg"], stored.Checks.Select(c => c.Criterion));
        Assert.Equal(("pass", false), (stored.Checks[0].Status, stored.Checks[0].Stale));
        Assert.True(stored.Checks[1].NotRun);
        Assert.True(stored.Stale);                                               // the requirement's aggregate is out of date

        Verification.RunAll(m, Opts);
        stored = Verification.LastResult(m, accel)!;
        Assert.False(stored.Stale);
        Assert.Equal(["pass", "pass"], stored.Checks.Select(c => c.Status));
    }

    [Fact]
    public void A_Result_From_Before_Criteria_Lists_Still_Reads_And_Stays_Fresh()
    {
        // What 0.7 and earlier stored: one criterion and its result in the flat fields, no verify.criteria.
        var m = SampleModels.Vehicle();
        var accel = Req(m, "REQ-002");
        m.Set(accel, VerificationKeys.Criterion, "  accelTime < 8 s ");           // old files kept the text as typed
        Verification.RunAll(m, Opts);
        Assert.Equal("", accel.Get(VerificationKeys.CriteriaResults));            // a single criterion writes no list
        var old = Clone(m);
        var a = Req(old, "REQ-002");
        var stored = Verification.LastResult(old, a)!;
        Assert.False(stored.Stale);
        var check = Assert.Single(stored.Checks);
        Assert.Equal(("accelTime < 8 s", "pass", "7.192 s", false), (check.Criterion, check.Status, check.Value, check.Stale));

        // Adding a second criterion: the old result stays with the criterion it was computed for.
        Verification.AddCriterion(old, a, "mass <= 1400 kg");
        Verification.SetCriteria(old, a, ["mass <= 1400 kg", "accelTime < 8 s"]);   // even when it moves
        stored = Verification.LastResult(old, a)!;
        Assert.True(stored.Stale);
        Assert.True(stored.Checks[0].NotRun);
        Assert.Equal(("pass", false), (stored.Checks[1].Status, stored.Checks[1].Stale));

        // The next run records both.
        Verification.RunAll(old, Opts);
        Assert.Equal(["fail", "pass"], Verification.LastResult(old, a)!.Checks.Select(c => c.Status));
        Assert.Equal("fail", a.Get(VerificationKeys.Status));
    }

    [Fact]
    public void A_Run_Keeps_Each_Criterions_Result_In_Its_History()
    {
        var (m, accel) = TwoCriteria();
        var vehicle = m.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle");
        var run = Verification.CreateRun(m, m.Root, "Mass and acceleration", [accel], vehicle);
        Verification.Run(m, run, Opts);
        var entry = Assert.Single(Verification.RunResults(run));
        Assert.Equal("fail", entry.Status);
        Assert.NotNull(entry.Criteria);
        Assert.Equal(["accelTime < 8 s", "mass <= 1400 kg"], entry.Criteria!.Select(c => c.Criterion));
        Assert.Equal(["pass", "fail"], entry.Criteria!.Select(c => c.Status));
        Assert.Equal("fail", run.Get(ReqFields.Verdict));
        Assert.Equal(run.Id, accel.Get(VerificationKeys.Run));

        // A requirement with one criterion keeps the old, flat history entry.
        var single = Verification.CreateRun(m, m.Root, "Power", [Req(m, "REQ-005")], vehicle);
        Verification.Run(m, single, Opts);
        Assert.Null(Assert.Single(Verification.RunResults(single)).Criteria);
        Assert.DoesNotContain("\"Criteria\"", single.Get(VerificationKeys.Results));
    }

    [Fact]
    public void Criteria_And_Their_Results_Round_Trip_Through_Xmi()
    {
        var (m, accel) = TwoCriteria();
        Verification.RunAll(m, Opts);
        var xml = XmiExchange.ToXml(m).ToString();
        var (back, _) = XmiExchange.Import(XDocument.Parse(xml));
        var b = back.OfKind(ElementKind.Requirement).Single(r => r.Get(FieldKeys.RequirementId) == "REQ-002");
        Assert.Equal(["accelTime < 8 s", "mass <= 1400 kg"], Verification.Criteria(b));
        var stored = Verification.LastResult(back, b)!;
        Assert.Equal("fail", stored.Status);
        Assert.Equal(["pass", "fail"], stored.Checks.Select(c => c.Status));
        Assert.Equal(accel.Get(VerificationKeys.CriteriaResults), b.Get(VerificationKeys.CriteriaResults));
    }

    [Fact]
    public void The_Vcrm_Lists_Each_Criterion_With_Its_Result()
    {
        var (m, accel) = TwoCriteria();
        Verification.RunAll(m, Opts);
        Assert.Equal("accelTime < 8 s: pass; mass <= 1400 kg: fail", ModelValues.Get(m, accel, ModelValues.VerificationCriteriaKey));
        Assert.Equal("fail", ModelValues.Get(m, accel, ModelValues.VerificationResultKey));
        Assert.Equal("maxPower >= 150 kW: pass", ModelValues.Get(m, Req(m, "REQ-005"), "verificationCriteria"));
        Assert.False(ModelValues.CanEdit(accel, ModelValues.VerificationCriteriaKey));
        var table = ReportTemplates.Vcrm().Sections.SelectMany(s => s.Blocks).OfType<TableBlock>().First();
        Assert.Contains(table.Columns, c => c.Key == "verificationCriteria");
    }
}
