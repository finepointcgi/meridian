using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;

namespace Meridian.Tests;

public class UnitsTests
{
    [Theory]
    [InlineData(100, "km/h", "m/s", 27.7777778)]
    [InlineData(1, "kW", "W", 1000)]
    [InlineData(1500, "kg", "t", 1.5)]
    [InlineData(2, "min", "s", 120)]
    [InlineData(1, "mi", "km", 1.609344)]
    [InlineData(100, "degC", "K", 373.15)]
    [InlineData(212, "°F", "°C", 100)]
    [InlineData(1, "kWh", "MJ", 3.6)]
    [InlineData(9.81, "m/s^2", "g0", 1.000342)]
    [InlineData(1, "N·m", "J", 1)]
    [InlineData(55, "L", "m^3", 0.055)]
    [InlineData(10, "km/L", "km/gal", 37.85411784)]
    [InlineData(50, "%", "1", 0.5)]
    [InlineData(1, "W/(m*K)", "W/(m·K)", 1)]
    [InlineData(3, "m²", "m^2", 3)]
    public void Converts_Between_Compatible_Units(double v, string from, string to, double expected)
    {
        var r = Units.Convert(v, from, to);
        Assert.NotNull(r);
        Assert.Equal(expected, r!.Value, 4);
    }

    [Fact]
    public void Rejects_Unknown_And_Incompatible_Units()
    {
        Assert.Null(Units.Parse("furlongs"));
        Assert.Null(Units.Convert(1, "kg", "m"));
        Assert.Null(Units.Convert(1, "kW", "kWh"));
        Assert.True(Units.Compatible(Units.Parse("mph")!, Units.Parse("km/h")!));
        Assert.Equal(Units.Parse("N")!.Dimension, Units.Parse("kg*m/s^2")!.Dimension);
        Assert.Equal("kg·m/s^2", Units.Parse("N")!.Dimension.ToString());
    }

    [Fact]
    public void Prefixes_Are_Case_Sensitive()
    {
        Assert.Equal(1e-3, Units.Parse("mW")!.Scale, 9);
        Assert.Equal(1e6, Units.Parse("MW")!.Scale, 9);
        Assert.Equal(60, Units.Parse("min")!.Scale);      // minutes, not milli-inches
    }
}

public class CriterionTests
{
    /// <summary>A measure source backed by a dictionary.</summary>
    private sealed class Fake(params (string Name, Measure M)[] measures) : IMeasureSource
    {
        public Measure? Find(string name, out string? problem)
        {
            problem = null;
            foreach (var (n, m) in measures) if (n == name) return m;
            problem = $"No value called '{name}'";
            return null;
        }
    }

    [Theory]
    [InlineData("mass <= 1500 kg", null, null, "<=")]
    [InlineData("parametric(Vehicle): accelTime < 8 s", AnalysisKind.Parametric, "Vehicle", "<")]
    [InlineData("mc: p95(accelTime) < 8 [s]", AnalysisKind.MonteCarlo, null, "<")]
    [InlineData("timed(Engine States): final(rpm) >= 800", AnalysisKind.Timed, "Engine States", ">=")]
    [InlineData("fmi: max(plant.temp) ≤ 90 degC", AnalysisKind.CoSim, null, "<=")]
    [InlineData("mc: prob(range >= 600 km) >= 95 %", AnalysisKind.MonteCarlo, null, ">=")]
    [InlineData("(100 km/h)^2 / (2 * decel) = 40 m", null, null, "==")]
    public void Parses_Criteria(string text, AnalysisKind? kind, string? target, string op)
    {
        var c = VerificationCriterion.Parse(text);
        Assert.Equal(kind, c.Analysis);
        Assert.Equal(target, c.Target);
        Assert.Equal(op, c.Comparator);
    }

    [Theory]
    [InlineData("mass", "Missing comparison")]
    [InlineData("mass <= 1500 furlongs", "Unexpected")]
    [InlineData("mass <= (1500 kg", "Missing ')'")]
    [InlineData("", "empty")]
    [InlineData("mass <= 1500 kg extra", "Unexpected")]
    public void Reports_Syntax_Errors(string text, string fragment)
    {
        var err = VerificationCriterion.Check(text);
        Assert.NotNull(err);
        Assert.Contains(fragment, err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluates_With_Unit_Conversion_And_Margin()
    {
        var src = new Fake(("mass", new ScalarMeasure(1.45, "t")));
        var r = VerificationCriterion.Parse("mass <= 1500 kg").Evaluate(src);
        Assert.Equal(Verdict.Pass, r.Verdict);
        Assert.Equal("1450 kg", r.Value);
        Assert.Equal("1500 kg", r.Bound);
        Assert.StartsWith("50 kg", r.Margin);
        Assert.Equal(50.0 / 1500, r.RelativeMargin!.Value, 6);

        var fail = VerificationCriterion.Parse("mass < 1.4 t").Evaluate(src);
        Assert.Equal(Verdict.Fail, fail.Verdict);
        Assert.StartsWith("-0.05 t", fail.Margin);
    }

    [Fact]
    public void Unitless_Values_Take_The_Bounds_Unit()
    {
        var r = VerificationCriterion.Parse("maxPower >= 150 kW").Evaluate(new Fake(("maxPower", new ScalarMeasure(160, null))));
        Assert.Equal(Verdict.Pass, r.Verdict);
        Assert.Equal("160 kW", r.Value);
        Assert.Contains(r.Notes, n => n.Contains("no unit"));
    }

    [Fact]
    public void Mismatched_Dimensions_Are_Inconclusive()
    {
        var r = VerificationCriterion.Parse("mass <= 8 s").Evaluate(new Fake(("mass", new ScalarMeasure(1, "kg"))));
        Assert.Equal(Verdict.Inconclusive, r.Verdict);
        Assert.Contains("Units do not match", r.Message);
    }

    [Fact]
    public void Missing_Values_Are_Inconclusive()
    {
        var r = VerificationCriterion.Parse("speed > 1 m/s").Evaluate(new Fake());
        Assert.Equal(Verdict.Inconclusive, r.Verdict);
        Assert.Contains("speed", r.Message);
    }

    [Fact]
    public void Arithmetic_Combines_Units()
    {
        var src = new Fake(("capacity", new ScalarMeasure(55, "L")));
        var r = VerificationCriterion.Parse("capacity * 10 km/L >= 600 km").Evaluate(src);
        Assert.Equal(Verdict.Fail, r.Verdict);
        Assert.Equal("550 km", r.Value);
        Assert.StartsWith("-50 km", r.Margin);
    }

    [Fact]
    public void Statistics_Over_Samples()
    {
        var xs = Enumerable.Range(1, 100).Select(i => (double)i).ToList();   // 1..100 s
        var src = new Fake(("t", new SampleMeasure(xs, "s")));
        Assert.Equal(Verdict.Pass, VerificationCriterion.Parse("p95(t) < 96 s").Evaluate(src).Verdict);
        Assert.Equal(Verdict.Fail, VerificationCriterion.Parse("p95(t) < 95 s").Evaluate(src).Verdict);
        Assert.Equal(Verdict.Pass, VerificationCriterion.Parse("mean(t) == 50.5 s").Evaluate(src).Verdict);
        Assert.Equal(Verdict.Pass, VerificationCriterion.Parse("max(t) <= 100 s").Evaluate(src).Verdict);
        Assert.Equal(Verdict.Pass, VerificationCriterion.Parse("prob(t <= 90 s) >= 90 %").Evaluate(src).Verdict);
        Assert.Equal(Verdict.Fail, VerificationCriterion.Parse("prob(t <= 1.5 min) > 90 %").Evaluate(src).Verdict);
        // A plain name over samples is their mean, with a note.
        var plain = VerificationCriterion.Parse("t > 50 s").Evaluate(src);
        Assert.Equal(Verdict.Pass, plain.Verdict);
        Assert.Contains(plain.Notes, n => n.Contains("mean"));
    }

    [Fact]
    public void Statistics_Over_Time_Series()
    {
        var src = new Fake(("temp", new SeriesMeasure([0, 1, 2, 3], [20, 60, 95, 80], "degC")));
        Assert.Equal(Verdict.Fail, VerificationCriterion.Parse("max(temp) <= 90 degC").Evaluate(src).Verdict);
        Assert.Equal(Verdict.Pass, VerificationCriterion.Parse("final(temp) <= 90 °C").Evaluate(src).Verdict);
        Assert.Equal(Verdict.Pass, VerificationCriterion.Parse("temp < 363.15 K").Evaluate(src).Verdict);       // final value, °C → K
        Assert.Equal(Verdict.Pass, VerificationCriterion.Parse("at(temp, 1 s) == 60 degC").Evaluate(src).Verdict);
        Assert.Equal(Verdict.Pass, VerificationCriterion.Parse("prob(temp < 90 degC) >= 0.75").Evaluate(src).Verdict);
    }
}

public class VerificationRunTests
{
    private static ModelRepository Clone(ModelRepository m) => ProjectFile.Deserialize(ProjectFile.Serialize(m));
    private static Element Req(ModelRepository m, string id) => m.OfKind(ElementKind.Requirement).Single(r => r.Get(FieldKeys.RequirementId) == id);
    private static readonly VerificationOptions Opts = new() { Clone = Clone, Now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc) };

    [Fact]
    public void Vehicle_Sample_Verifies_Parametrically()
    {
        var m = SampleModels.Vehicle();
        var reports = Verification.RunAll(m, Opts);
        var report = Assert.Single(reports);
        Assert.Null(report.Run);
        Assert.Equal(3, report.Results.Count);
        var accel = report.Results.Single(r => r.Requirement == Req(m, "REQ-002"));
        Assert.Equal(Verdict.Pass, accel.Verdict);
        Assert.Equal("7.192 s", accel.Value);
        Assert.Contains("Vehicle", accel.Analysis);
        Assert.Equal(Verdict.Fail, report.Results.Single(r => r.Requirement == Req(m, "REQ-003")).Verdict);
        Assert.Equal(Verdict.Pass, report.Results.Single(r => r.Requirement == Req(m, "REQ-005")).Verdict);

        // Stored on the requirement, one undo step.
        var stored = Verification.LastResult(m, Req(m, "REQ-002"))!;
        Assert.Equal("pass", stored.Status);
        Assert.Equal("7.192 s", stored.Value);
        Assert.Equal("2026-11-10T12:00:00Z", stored.At);
        Assert.Equal(Verification.FullModel, stored.Variant);
        Assert.StartsWith("#", stored.ModelVersion);
        Assert.False(stored.Stale);
        m.History.Undo();
        Assert.Null(Verification.LastResult(m, Req(m, "REQ-002")));
    }

    [Fact]
    public void Results_Survive_Save_And_Load()
    {
        var m = SampleModels.Vehicle();
        Verification.RunAll(m, Opts);
        var copy = Clone(m);
        var r = Verification.LastResult(copy, Req(copy, "REQ-003"))!;
        Assert.Equal("fail", r.Status);
        Assert.False(r.Stale);
    }

    [Fact]
    public void Results_Go_Stale_When_Inputs_Change_But_Not_For_Unrelated_Edits()
    {
        var m = SampleModels.Vehicle();
        Verification.RunAll(m, Opts);
        var accel = Req(m, "REQ-002");
        var range = Req(m, "REQ-003");
        // Unrelated: documentation, another requirement's text, a use case.
        m.Set(m.Root, FieldKeys.Documentation, "changed");
        m.Set(m.OfKind(ElementKind.UseCase).First(), FieldKeys.Name, "Drive");
        Assert.False(Verification.IsStale(m, accel));

        var mass = m.OfKind(ElementKind.Property).Single(p => p.Name == "mass" && p.Owner!.Name == "Vehicle");
        m.Set(mass, FieldKeys.DefaultValue, "1600");
        Assert.True(Verification.IsStale(m, accel));
        Assert.False(Verification.IsStale(m, range));          // range depends on FuelTank only
        Assert.Equal("pass (stale)", Verification.LastResult(m, accel)!.Label);

        m.Set(range, VerificationKeys.Criterion, "capacity * 12 km/L >= 600 km");
        Assert.True(Verification.IsStale(m, range));
        Assert.Equal(2, Verification.Summarize(m).Stale);

        Verification.RunAll(m, Opts);
        Assert.False(Verification.IsStale(m, accel));
        Assert.Equal("pass", Verification.LastResult(m, range)!.Status);   // 660 km
    }

    [Fact]
    public void A_Run_Applies_Inputs_On_A_Copy_And_Stores_A_Summary()
    {
        var m = SampleModels.Vehicle();
        var vehicle = m.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle");
        var run = Verification.CreateRun(m, m.Root, "Heavy vehicle", [Req(m, "REQ-002")], vehicle);
        m.Set(run, VerificationKeys.Inputs, "mass = 1.7 t");
        Assert.True(Verification.IsRun(run));
        Assert.Single(Verification.RequirementsOf(m, run));

        var report = Verification.Run(m, run, Opts);
        var r = Assert.Single(report.Results);
        Assert.Equal(Verdict.Fail, r.Verdict);                  // 27.78 * 1700 / 5600 = 8.43 s
        Assert.Equal("8.433 s", r.Value);
        Assert.Equal("1450", m.OfKind(ElementKind.Property).Single(p => p.Name == "mass" && p.Owner == vehicle).Get(FieldKeys.DefaultValue));
        Assert.Equal("fail", run.Get(ReqFields.Verdict));
        Assert.StartsWith("0 pass, 1 fail", run.Get(VerificationKeys.Summary));
        Assert.Equal("fail", Assert.Single(Verification.RunResults(run)).Status);
        Assert.Equal(run.Id, Req(m, "REQ-002").Get(VerificationKeys.Run));

        // Editing the run's inputs makes its results stale.
        m.Set(run, VerificationKeys.Inputs, "mass = 1.2 t");
        Assert.True(Verification.IsStale(m, Req(m, "REQ-002")));
    }

    [Fact]
    public void Inputs_Need_A_Copy()
    {
        var m = SampleModels.Vehicle();
        var run = Verification.CreateRun(m, m.Root, "R", [Req(m, "REQ-002")]);
        m.Set(run, VerificationKeys.Inputs, "mass = 1600 kg");
        var report = Verification.Execute(m, run, options: new VerificationOptions());
        Assert.Equal(Verdict.Inconclusive, Assert.Single(report.Results).Verdict);
    }

    [Fact]
    public void Requirements_Without_Subject_Or_With_Bad_Criteria_Are_Inconclusive()
    {
        var m = SampleModels.Vehicle();
        var orphan = m.Create(m.Root, ElementKind.Requirement, "Orphan");
        m.Set(orphan, VerificationKeys.Criterion, "mass < 1 kg");
        var bad = m.Create(m.Root, ElementKind.Requirement, "Bad");
        m.Set(bad, VerificationKeys.Criterion, "mass <");
        var report = Verification.Execute(m, null, [orphan, bad], Opts);
        Assert.All(report.Results, r => Assert.Equal(Verdict.Inconclusive, r.Verdict));
        Assert.Contains("No subject", report.Results[0].Message);
        Assert.StartsWith("Criterion:", report.Results[1].Message);
    }

    [Fact]
    public void Criterion_Can_Name_Its_Target_And_Navigate_Parts()
    {
        var m = SampleModels.Vehicle();
        var req = m.Create(m.Root, ElementKind.Requirement, "Power reserve");
        m.Set(req, VerificationKeys.Criterion, "parametric(Vehicle): engine.maxPower >= requiredPower");
        var r = Assert.Single(Verification.Execute(m, null, [req], Opts).Results);
        Assert.Equal(Verdict.Fail, r.Verdict);                  // 160 kW < 280 kW
        Assert.Equal("160 kW", r.Value);
        Assert.Equal("280 kW", r.Bound);
    }

    // ------------------------------------------------------------------ other analyses (fakes where heavy)

    private sealed class FakeAnalyzer(Dictionary<string, Measure> values) : IVerificationAnalyzer
    {
        public List<(AnalysisKind Kind, string Target)> Calls { get; } = new();

        public AnalysisOutput Analyze(ModelRepository model, AnalysisKind kind, Element target, AnalysisSettings settings, CancellationToken ct)
        {
            Calls.Add((kind, target.Name));
            var o = new AnalysisOutput($"fake {kind}", n => values.TryGetValue(n, out var v) ? (v, null) : (null, $"no {n}"));
            o.Dependencies.Add(target.Id);
            return o;
        }
    }

    [Fact]
    public void Each_Analysis_Kind_Is_Dispatched_Once_Per_Target()
    {
        var m = SampleModels.Vehicle();
        var vehicle = m.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle");
        var reqs = new List<Element>();
        foreach (var (name, crit) in new[]
                 {
                     ("a", "mc: p95(accelTime) < 8 s"), ("b", "mc: mean(accelTime) < 8 s"),
                     ("c", "timed(Start Vehicle): final(rpm) >= 800"), ("d", "cosim: max(plant.temp) <= 90 degC"),
                 })
        {
            var r = m.Create(m.Root, ElementKind.Requirement, name);
            m.Set(r, VerificationKeys.Criterion, crit);
            m.Connect(ElementKind.Satisfy, vehicle, r);
            reqs.Add(r);
        }
        var fake = new FakeAnalyzer(new()
        {
            ["accelTime"] = new SampleMeasure([7.0, 7.5, 7.9, 8.2], "s"),
            ["rpm"] = new SeriesMeasure([0, 1], [0, 800], null),
            ["plant.temp"] = new SeriesMeasure([0, 1], [20, 85], "degC"),
        });
        var report = Verification.Execute(m, null, reqs, new VerificationOptions { Analyzer = fake });
        Assert.Equal([Verdict.Fail, Verdict.Pass, Verdict.Pass, Verdict.Pass], report.Results.Select(r => r.Verdict));
        Assert.Equal(3, fake.Calls.Count);                       // the two Monte Carlo criteria share one run
        Assert.Contains((AnalysisKind.Timed, "Start Vehicle"), fake.Calls);
        Assert.Contains((AnalysisKind.CoSim, "Vehicle"), fake.Calls);
    }

    [Fact]
    public void Monte_Carlo_Statistics_On_The_Real_Analysis()
    {
        var m = SampleModels.Vehicle();
        var mass = m.OfKind(ElementKind.Property).Single(p => p.Name == "mass" && p.Owner!.Name == "Vehicle");
        m.Set(mass, ExtFieldKeys.Distribution, "normal(1450, 100)");
        var req = Req(m, "REQ-002");
        m.Set(req, VerificationKeys.Criterion, "mc: p95(accelTime) < 8 s");
        var r = Assert.Single(Verification.Execute(m, null, [req], Opts).Results);
        Assert.Equal(Verdict.Pass, r.Verdict);                  // p95 ≈ (1450 + 1.645 · 100) · 27.78 / 5600 ≈ 7.6 s
        Assert.Contains("Monte Carlo", r.Analysis);
        m.Set(req, VerificationKeys.Criterion, "mc: prob(accelTime < 7.2 s) > 90 %");
        Assert.Equal(Verdict.Fail, Assert.Single(Verification.Execute(m, null, [req], Opts).Results).Verdict);
    }

    [Fact]
    public void Timed_Simulation_End_Values()
    {
        var m = SampleModels.Vehicle();
        var act = m.OfKind(ElementKind.Activity).Single(a => a.Name == "Start Vehicle");
        foreach (var a in act.OwnedElements.Where(a => a.Kind == ElementKind.Action)) m.Set(a, ExtFieldKeys.Duration, "2 s");
        var req = m.Create(m.Root, ElementKind.Requirement, "Start time");
        m.Set(req, VerificationKeys.Criterion, "timed(Start Vehicle): endTime <= 10 s");
        var req2 = m.Create(m.Root, ElementKind.Requirement, "Idle");
        m.Set(req2, VerificationKeys.Criterion, "timed(Start Vehicle): final(rpm) == 800");
        var results = Verification.Execute(m, null, [req, req2], Opts).Results;
        Assert.Equal(Verdict.Pass, results[0].Verdict);
        Assert.Equal("8 s", results[0].Value);                  // insert key, crank twice, idle: 4 × 2 s
        Assert.Equal(Verdict.Pass, results[1].Verdict);
    }

    [Fact]
    public void Co_Simulation_Of_Algebraic_Parts()
    {
        var m = new ModelRepository("Cosim");
        var sys = m.Create(m.Root, ElementKind.Block, "Rig");
        var src = m.Create(m.Root, ElementKind.Block, "Source");
        var o = m.Create(src, ElementKind.Port, "y"); m.Set(o, FieldKeys.Direction, "out"); m.Set(o, FieldKeys.DefaultValue, "time * 2"); m.Set(o, FieldKeys.Type, "Real"); m.Set(o, ExtFieldKeys.Unit, "m");
        var sink = m.Create(m.Root, ElementKind.Block, "Sink");
        var i = m.Create(sink, ElementKind.Port, "u"); m.Set(i, FieldKeys.Direction, "in"); m.Set(i, FieldKeys.Type, "Real"); m.Set(i, ExtFieldKeys.Unit, "m");
        var pa = m.Create(sys, ElementKind.Property, "src"); m.Set(pa, FieldKeys.Type, src.Id);
        var pb = m.Create(sys, ElementKind.Property, "sink"); m.Set(pb, FieldKeys.Type, sink.Id);
        var c = m.Connect(ElementKind.Connector, o, i, sys); m.Set(c, FieldKeys.SourcePart, pa.Id); m.Set(c, FieldKeys.TargetPart, pb.Id);
        var req = m.Create(m.Root, ElementKind.Requirement, "Reach");
        m.Set(req, VerificationKeys.Criterion, "cosim(Rig): max(src.y) >= 1900 cm");
        var run = Verification.CreateRun(m, m.Root, "Rig run", [req], sys);
        m.Set(run, VerificationKeys.StopTime, "10 s");
        var r = Assert.Single(Verification.Execute(m, run, options: Opts).Results);
        Assert.True(r.Verdict == Verdict.Pass, r.Message);
        Assert.Equal("2000 cm", r.Value);
    }

    // ------------------------------------------------------------------ variants

    [Fact]
    public void Verifying_A_Variant_Uses_The_Resolved_Model()
    {
        var m = ProductLineSample.Create();
        var vehicle = m.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle");
        var battery = m.OfKind(ElementKind.Block).Single(b => b.Name == "Battery");
        var cap = m.Create(battery, ElementKind.Property, "capacity"); m.Set(cap, FieldKeys.DefaultValue, "60"); m.Set(cap, ExtFieldKeys.Unit, "kWh");
        var req = m.Create(m.Root, ElementKind.Requirement, "Battery size");
        m.Set(req, VerificationKeys.Criterion, "parametric(Battery): capacity >= 50 kWh");
        var eco = m.OfKind(ElementKind.Configuration).Single(c => c.Name == "Eco");
        var touring = m.OfKind(ElementKind.Configuration).Single(c => c.Name == "Touring");

        var ok = Verification.Execute(m, null, [req], new VerificationOptions { Clone = Clone, Configuration = eco });
        Assert.Equal("Eco", ok.Variant);
        Assert.Equal(Verdict.Pass, Assert.Single(ok.Results).Verdict);

        var none = Verification.Execute(m, null, [req], new VerificationOptions { Clone = Clone, Configuration = touring });
        var r = Assert.Single(none.Results);
        Assert.Equal(Verdict.Inconclusive, r.Verdict);
        Assert.Contains("Battery", r.Message);                   // not in the Touring variant

        // A conditional requirement outside the variant is reported as such.
        m.Set(req, ExtFieldKeys.PresenceCondition, "Electric");
        var skipped = Assert.Single(Verification.Execute(m, null, [req], new VerificationOptions { Clone = Clone, Configuration = touring }).Results);
        Assert.Contains("Not part of variant", skipped.Message);

        // The configuration's selection is part of the fingerprint.
        Verification.Run(m, null, new VerificationOptions { Clone = Clone, Configuration = eco }, [req]);
        Assert.False(Verification.IsStale(m, req));
        m.Set(eco, ExtFieldKeys.SelectedFeatures, Variability.Serialize(Variability.Selected(m, touring)));
        Assert.True(Verification.IsStale(m, req));
        _ = vehicle;
    }
}

public class FeatureSolverTests
{
    private static Element F(ModelRepository m, string name) => m.OfKind(ElementKind.Feature).Single(f => f.Name == name);

    [Fact]
    public void Counts_The_Same_Products_As_The_Validator()
    {
        var m = ProductLineSample.Create();
        var solver = new FeatureSolver(m);
        Assert.Equal(36, solver.CountProducts());
        foreach (var p in solver.Products()) Assert.Empty(Variability.Validate(m, p));
        var (dead, core) = solver.DeadAndCore();
        Assert.Empty(dead);
        Assert.Equal(["Car", "Powertrain", "Infotainment"], core.Select(f => f.Name));
    }

    [Fact]
    public void Propagates_Requires_Excludes_And_Alternatives()
    {
        var m = ProductLineSample.Create();
        var solver = new FeatureSolver(m);
        var a = solver.Analyze([F(m, "GPS")]);
        Assert.True(a.Consistent);
        Assert.Equal(FeatureState.Implied, a[F(m, "Premium")].State);          // GPS requires Premium
        Assert.Contains("requires", a[F(m, "Premium")].Reason);
        Assert.Equal(FeatureState.Excluded, a[F(m, "Basic")].State);           // alternative of Premium
        Assert.Equal(FeatureState.Implied, a[F(m, "Car")].State);               // root

        var b = solver.Analyze([F(m, "Towing")]);
        Assert.Equal(FeatureState.Excluded, b[F(m, "Electric")].State);        // Towing excludes Electric
        Assert.Equal(FeatureState.Implied, b[F(m, "Combustion")].State);       // the only alternative left

        var c = solver.Analyze([F(m, "Towing"), F(m, "Electric")]);
        Assert.False(c.Consistent);
        Assert.Contains(c.Conflicts, x => x.Contains("excludes") || x.Contains("alternatives"));

        // Lookahead: Sunroof alone is fine; with Comfort deselected, Sunroof can't be selected.
        var d = solver.Analyze([], [F(m, "Comfort")]);
        Assert.Equal(FeatureState.Excluded, d[F(m, "Sunroof")].State);
    }

    [Fact]
    public void Complete_Lets_The_Latest_Decision_Win()
    {
        var m = ProductLineSample.Create();
        var solver = new FeatureSolver(m);
        var touring = Variability.Selected(m, m.OfKind(ElementKind.Configuration).Single(c => c.Name == "Touring"));
        var switched = solver.Complete(touring, turnedOn: F(m, "Electric"));
        Assert.Contains(F(m, "Electric"), switched);
        Assert.DoesNotContain(F(m, "Combustion"), switched);
        Assert.DoesNotContain(F(m, "Towing"), switched);                      // excluded by Electric
        Assert.Contains(F(m, "GPS"), switched);
        Assert.Empty(Variability.Validate(m, switched));

        var noPremium = solver.Complete(touring, turnedOff: F(m, "Premium"));
        Assert.DoesNotContain(F(m, "GPS"), noPremium);                         // GPS requires Premium
        Assert.Contains(F(m, "Basic"), noPremium);                              // the other alternative is forced
        Assert.Empty(Variability.Validate(m, noPremium));

        // Deselecting a core feature is ignored.
        Assert.Equal(touring.Count, solver.Complete(touring, turnedOff: F(m, "Powertrain")).Count);
    }

    [Fact]
    public void Compares_Two_Configurations()
    {
        var m = ProductLineSample.Create();
        var eco = m.OfKind(ElementKind.Configuration).Single(c => c.Name == "Eco");
        var touring = m.OfKind(ElementKind.Configuration).Single(c => c.Name == "Touring");
        var cmp = VariantComparison.Compare(m, eco, touring);
        Assert.Equal(["Electric", "Basic"], cmp.FeaturesOnlyInA.Select(f => f.Name));
        Assert.Equal(["Combustion", "Premium", "GPS", "Towing"], cmp.FeaturesOnlyInB.Select(f => f.Name));
        Assert.Contains(cmp.ElementsOnlyInA, e => e.Name == "Battery");
        Assert.Contains(cmp.ElementsOnlyInB, e => e.Name == "FuelTank");
        Assert.Contains(cmp.ElementsOnlyInB, e => e.Name == "NavigationUnit");
        Assert.DoesNotContain(cmp.ElementsOnlyInA, e => e.Name == "Vehicle");
        Assert.False(cmp.Identical);
        Assert.True(VariantComparison.Compare(m, eco, eco).Identical);
    }

    [Theory]
    [InlineData("Electric", true)]
    [InlineData("GPS and not Basic", false)]
    [InlineData("Electric implies not Towing", true)]
    public void Presence_Conditions_Follow_The_Configuration(string condition, bool inEco)
    {
        var m = ProductLineSample.Create();
        var e = m.Create(m.Root, ElementKind.Block, "X");
        m.Set(e, ExtFieldKeys.PresenceCondition, condition);
        var eco = m.OfKind(ElementKind.Configuration).Single(c => c.Name == "Eco");
        Assert.Equal(inEco, Variability.Resolve(m, eco).IsIncluded(e));
    }
}
