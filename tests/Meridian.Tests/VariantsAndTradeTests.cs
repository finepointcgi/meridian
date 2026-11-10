using System.IO.Compression;
using System.Text;
using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;

namespace Meridian.Tests;

public class VariabilityTests
{
    private static Element Feature(ModelRepository m, string name) => m.OfKind(ElementKind.Feature).Single(f => f.Name == name);
    private static Element Config(ModelRepository m, string name) => m.OfKind(ElementKind.Configuration).Single(f => f.Name == name);
    private static HashSet<Element> Sel(ModelRepository m, params string[] names) =>
        new(names.Select(n => Feature(m, n)), ReferenceEqualityComparer.Instance);

    [Theory]
    [InlineData("GPS and not Basic", true)]
    [InlineData("GPS && !Premium", false)]
    [InlineData("Basic or (Premium and GPS)", true)]
    [InlineData("Premium xor GPS", false)]
    [InlineData("Basic implies GPS", true)]
    [InlineData("\"Lane Keeping\" | Basic", true)]
    [InlineData("", true)]
    [InlineData("true and not false", true)]
    public void Feature_Expressions_Evaluate(string expr, bool expected)
    {
        var selected = new HashSet<string>(["GPS", "Premium", "Lane Keeping"]);
        Assert.Equal(expected, FeatureExpression.Evaluate(expr, selected.Contains));
    }

    [Fact]
    public void Feature_Expression_Reports_Syntax_Errors()
    {
        Assert.NotNull(FeatureExpression.Check("GPS and"));
        Assert.NotNull(FeatureExpression.Check("(GPS"));
        Assert.Null(FeatureExpression.Check("GPS -> Premium"));
        Assert.Equal(["GPS", "Basic"], FeatureExpression.Names("GPS and not Basic"));
    }

    [Fact]
    public void Feature_Tree_Combines_Containment_And_Decomposition()
    {
        var m = ProductLineSample.Create();
        var car = Feature(m, "Car");
        Assert.Single(Variability.Roots(m));
        Assert.Same(car, Variability.Roots(m)[0]);
        Assert.Same(Feature(m, "Powertrain"), Variability.Parent(m, Feature(m, "Electric")));   // containment (+ generated edge)
        Assert.Same(car, Variability.Parent(m, Feature(m, "GPS")));                            // decomposition only
        Assert.Equal(5, Variability.Children(m, car).Count);
        Assert.Equal(12, Variability.Tree(m).Count());
    }

    [Fact]
    public void Sample_Configurations_Are_Valid()
    {
        var m = ProductLineSample.Create();
        Assert.Empty(Variability.Validate(m, Config(m, "Eco")));
        Assert.Empty(Variability.Validate(m, Config(m, "Touring")));
    }

    [Fact]
    public void Validation_Catches_Group_Mandatory_And_Cross_Tree_Violations()
    {
        var m = ProductLineSample.Create();
        // Both alternatives, missing mandatory Infotainment.
        var p = Variability.Validate(m, Sel(m, "Car", "Powertrain", "Electric", "Combustion"));
        Assert.Contains(p, x => x.Message.Contains("Exactly one of the alternatives of 'Powertrain'"));
        Assert.Contains(p, x => x.Message.Contains("Mandatory feature 'Infotainment'"));
        // Requires: GPS needs Premium.
        p = Variability.Validate(m, Sel(m, "Car", "Powertrain", "Electric", "Infotainment", "Basic", "GPS"));
        Assert.Contains(p, x => x.Message == "'GPS' requires 'Premium'");
        // Excludes: Towing with Electric.
        p = Variability.Validate(m, Sel(m, "Car", "Powertrain", "Electric", "Infotainment", "Basic", "Towing"));
        Assert.Contains(p, x => x.Message == "'Towing' excludes 'Electric'");
        // Or group needs at least one member; child without parent; root missing.
        p = Variability.Validate(m, Sel(m, "Car", "Powertrain", "Combustion", "Infotainment", "Basic", "Comfort"));
        Assert.Contains(p, x => x.Message.Contains("or-group"));
        p = Variability.Validate(m, Sel(m, "Powertrain", "Sunroof"));
        Assert.Contains(p, x => x.Message.Contains("Root feature 'Car' must be selected"));
        Assert.Contains(p, x => x.Message.Contains("'Sunroof' is selected but its parent 'Comfort' is not"));
    }

    [Fact]
    public void Propagation_Adds_Ancestors_Mandatory_Children_And_Required_Features()
    {
        var m = ProductLineSample.Create();
        var s = Variability.Propagate(m, [Feature(m, "GPS"), Feature(m, "Electric")]);
        Assert.Contains(Feature(m, "Car"), s);
        Assert.Contains(Feature(m, "Powertrain"), s);
        Assert.Contains(Feature(m, "Infotainment"), s);   // mandatory child of Car
        Assert.Contains(Feature(m, "Premium"), s);        // GPS requires Premium
        Assert.Empty(Variability.Validate(m, s));
    }

    [Fact]
    public void Enumerates_All_Valid_Products()
    {
        var m = ProductLineSample.Create();
        // (Basic | Premium | Premium+GPS) × (Electric | Combustion | Combustion+Towing) × (no comfort | 3 comfort combos)
        Assert.Equal(36, Variability.EnumerateProducts(m).Count);
    }

    [Fact]
    public void Resolution_Applies_Presence_Conditions_Ownership_And_Relationship_Ends()
    {
        var m = ProductLineSample.Create();
        Element Block(string n) => m.OfKind(ElementKind.Block).Single(b => b.Name == n);
        var vehicle = Block("Vehicle");

        var eco = Variability.Resolve(m, Config(m, "Eco"));
        Assert.Empty(eco.Problems);
        Assert.True(eco.IsIncluded(Block("Battery")));
        Assert.False(eco.IsIncluded(Block("FuelTank")));
        Assert.False(eco.IsIncluded(Block("NavigationUnit")));
        Assert.True(eco.IsIncluded(vehicle.OwnedElements.Single(p => p.Name == "battery")));
        Assert.DoesNotContain(m.Relationships, r => r.Kind == ElementKind.Composition && ReferenceEquals(r.Target, Block("FuelTank")) && eco.IsIncluded(r));

        var touring = Variability.Resolve(m, Config(m, "Touring"));
        Assert.False(touring.IsIncluded(Block("Battery")));
        Assert.False(touring.IsIncluded(vehicle.OwnedElements.Single(p => p.Name == "battery")));
        Assert.True(touring.IsIncluded(Block("NavigationUnit")));
        Assert.True(touring.IsIncluded(Block("TowHitch")));
        var navLink = m.Relationships.Single(r => ReferenceEquals(r.Target, Block("NavigationUnit")));
        Assert.True(touring.IsIncluded(navLink));
        Assert.Contains("NavigationUnit", Variability.InclusionCsv(m, touring));
    }

    [Fact]
    public void Resolution_Reports_Unknown_Features()
    {
        var m = ProductLineSample.Create();
        var tank = m.OfKind(ElementKind.Block).Single(b => b.Name == "FuelTank");
        m.Set(tank, ExtFieldKeys.PresenceCondition, "Diesel");
        var res = Variability.Resolve(m, Config(m, "Touring"));
        Assert.False(res.IsIncluded(tank));
        Assert.Contains(res.Problems, p => p.Message.Contains("unknown feature 'Diesel'"));
    }

    [Fact]
    public void Resolved_Variant_Can_Become_A_New_Project()
    {
        var m = ProductLineSample.Create();
        var res = Variability.Resolve(m, Config(m, "Eco"));
        var copy = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        var removed = Variability.ApplyTo(copy, res);
        Assert.True(removed > 0);
        Assert.DoesNotContain(copy.OfKind(ElementKind.Block), b => b.Name is "FuelTank" or "NavigationUnit" or "TowHitch");
        Assert.Contains(copy.OfKind(ElementKind.Block), b => b.Name == "Battery");
        Assert.DoesNotContain(copy.AllElements, e => e.Get(ExtFieldKeys.PresenceCondition).Length > 0);
        Assert.DoesNotContain(copy.Relationships, r => r.Target.Name == "FuelTank");
        // The original is untouched.
        Assert.Contains(m.OfKind(ElementKind.Block), b => b.Name == "FuelTank");
    }

    [Fact]
    public void Resolved_Variant_Can_Be_A_Package_Copy_And_Undone()
    {
        var m = ProductLineSample.Create();
        var structure = m.OfKind(ElementKind.Package).Single(p => p.Name == "Structure");
        var res = Variability.Resolve(m, Config(m, "Touring"));
        var before = m.AllElements.Count();
        var copy = Variability.CopyPackageAsVariant(m, structure, res, "Touring");
        Assert.Equal("Structure [Touring]", copy.Name);
        var names = copy.OwnedElements.Select(e => e.Name).ToList();
        Assert.Contains("FuelTank", names);
        Assert.Contains("NavigationUnit", names);
        Assert.DoesNotContain("Battery", names);
        var vehicleCopy = copy.OwnedElements.Single(e => e.Name == "Vehicle");
        Assert.DoesNotContain(vehicleCopy.OwnedElements, p => p.Name == "battery");
        Assert.Contains(m.Relationships, r => ReferenceEquals(r.Source, vehicleCopy) && r.Target.Name == "NavigationUnit");
        m.History.Undo();
        Assert.Equal(before, m.AllElements.Count());
    }

    [Fact]
    public void Feature_Kinds_Round_Trip_Through_Project_File_And_Xmi()
    {
        var m = ProductLineSample.Create();
        var json = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        Assert.Equal(12, json.OfKind(ElementKind.Feature).Count());
        Assert.Equal(2, json.OfKind(ElementKind.Configuration).Count());
        Assert.Single(json.Diagrams, d => d.DiagramType == DiagramType.FeatureModel);

        var (xm, _) = XmiExchange.Import(XmiExchange.ToXml(m));
        Assert.Equal(12, xm.OfKind(ElementKind.Feature).Count());
        Assert.Single(xm.Relationships, r => r.Kind == ElementKind.Requires);
        Assert.Single(xm.Relationships, r => r.Kind == ElementKind.Excludes);
        Assert.Equal("alternative", xm.OfKind(ElementKind.Feature).Single(f => f.Name == "Electric").Get(ExtFieldKeys.FeatureKind));
        Assert.Equal("GPS and not Basic", xm.OfKind(ElementKind.Block).Single(f => f.Name == "NavigationUnit").Get(ExtFieldKeys.PresenceCondition));
        var eco = xm.OfKind(ElementKind.Configuration).Single(c => c.Name == "Eco");
        Assert.Empty(Variability.Validate(xm, eco));
    }

    [Fact]
    public void Feature_Model_Notation_And_Svg()
    {
        var m = ProductLineSample.Create();
        var d = m.Diagrams.Single(x => x.DiagramType == DiagramType.FeatureModel);
        Assert.Equal(12, d.Nodes.Count);
        Assert.True(d.Edges.Count >= 12);   // 10 decomposition edges + requires + excludes
        Assert.Equal(ArrowHead.HollowDot, Notation.StyleOf(m.Relationships.First(r => r.Kind == ElementKind.FeatureDecomposition && r.Target.Name == "GPS")).TargetHead);
        Assert.Equal(ArrowHead.Dot, Notation.StyleOf(m.Relationships.First(r => r.Kind == ElementKind.FeatureDecomposition && r.Target.Name == "Powertrain")).TargetHead);
        // Tree layout: children below parents.
        var car = d.NodeFor(m.OfKind(ElementKind.Feature).Single(f => f.Name == "Car"))!;
        var gps = d.NodeFor(m.OfKind(ElementKind.Feature).Single(f => f.Name == "GPS"))!;
        Assert.True(gps.Bounds.Y > car.Bounds.Y);
        var svg = SvgRenderer.Render(m, d);
        Assert.Contains(System.Net.WebUtility.HtmlEncode("«alternative»"), svg);
        Assert.Contains(System.Net.WebUtility.HtmlEncode("«requires»"), svg);
        Assert.Contains(" A22,22 ", svg);   // group arcs
        Assert.Equal(DiagramType.FeatureModel, Enum.GetValues<DiagramType>().Single(t => Metamodel.DiagramAbbreviation(t) == "fm"));
        Assert.True(Metamodel.CanOwn(ElementKind.Feature, ElementKind.Feature));
        Assert.NotNull(Metamodel.CheckEnds(ElementKind.Requires, ElementKind.Block, ElementKind.Feature));
    }
}

public class TradeStudyTests
{
    private static (ModelRepository M, TradeStudyDefinition Def, Element A, Element B, Element C) Setup()
    {
        var m = new ModelRepository("Trade");
        var pkg = m.Create(m.Root, ElementKind.Package, "Options");
        Element Alt(string name, double cost, double perf)
        {
            var b = m.Create(pkg, ElementKind.Block, name);
            m.Set(m.Create(b, ElementKind.Property, "cost"), FieldKeys.DefaultValue, cost.ToString(System.Globalization.CultureInfo.InvariantCulture));
            m.Set(m.Create(b, ElementKind.Property, "perf"), FieldKeys.DefaultValue, perf.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return b;
        }
        var a = Alt("A", 100, 50); var b = Alt("B", 200, 90); var c = Alt("C", 150, 70);
        var def = new TradeStudyDefinition
        {
            Alternatives = [a.Id, b.Id, c.Id],
            Criteria =
            [
                new TradeCriterion { Name = "cost", Weight = 3, Direction = "minimize" },
                new TradeCriterion { Name = "perf", Weight = 2, Direction = "maximize" },
            ],
        };
        return (m, def, a, b, c);
    }

    [Fact]
    public void Weighted_Scores_Rank_Alternatives()
    {
        var (m, def, a, b, c) = Setup();
        var r = TradeStudies.Evaluate(m, def);
        Assert.Empty(r.Problems);
        Assert.Equal([0.6, 0.4], r.Weights.Select(w => Math.Round(w, 9)));
        Assert.Equal(0.6, r.Rows.Single(x => x.Alternative == a).Score, 9);
        Assert.Equal(0.4, r.Rows.Single(x => x.Alternative == b).Score, 9);
        Assert.Equal(0.5, r.Rows.Single(x => x.Alternative == c).Score, 9);
        Assert.Equal(["A", "C", "B"], r.Ranked.Select(x => x.Alternative.Name));
        var csv = TradeStudies.ToCsv(r);
        Assert.StartsWith("Rank,Alternative,Score", csv);
        Assert.Contains("1,A,0.6", csv);
    }

    [Fact]
    public void Utility_Curves_Replace_Min_Max()
    {
        var (m, def, a, b, c) = Setup();
        def.Criteria[1].Normalization = "utility";
        def.Criteria[1].Utility = "40:0, 60:1";   // anything ≥ 60 is fully satisfactory
        var r = TradeStudies.Evaluate(m, def);
        Assert.Equal(0.5, r.Rows.Single(x => x.Alternative == a).Normalized[1], 9);
        Assert.Equal(1, r.Rows.Single(x => x.Alternative == b).Normalized[1], 9);
        Assert.Equal(1, r.Rows.Single(x => x.Alternative == c).Normalized[1], 9);
        Assert.Null(TradeStudies.UtilityCurve("5:1"));
    }

    [Fact]
    public void Sensitivity_Finds_Rank_Changes_And_Switch_Points()
    {
        var (m, def, _, _, _) = Setup();
        var cases = TradeStudies.Sensitivity(m, def, [0.5, 2.0]);
        Assert.Equal(4, cases.Count);
        var perfDouble = cases.Single(x => x.Criterion == "perf" && x.Factor == 2.0);
        Assert.True(perfDouble.WinnerChanged);
        Assert.Equal("B", perfDouble.Winner);
        Assert.False(cases.Single(x => x.Criterion == "perf" && x.Factor == 0.5).WinnerChanged);
        var th = TradeStudies.Thresholds(m, def).Single(t => t.Criterion == "perf");
        Assert.Equal(0.4, th.CurrentShare, 9);
        Assert.InRange(th.SwitchShare!.Value, 0.5, 0.52);
        Assert.Equal("B", th.NewWinner);
    }

    [Fact]
    public void Trade_Study_Persists_As_Element_And_Reads_Configurations()
    {
        var m = ProductLineSample.Create();
        var eco = m.OfKind(ElementKind.Configuration).Single(c => c.Name == "Eco");
        var touring = m.OfKind(ElementKind.Configuration).Single(c => c.Name == "Touring");
        var def = new TradeStudyDefinition
        {
            Alternatives = [eco.Id, touring.Id],
            Criteria =
            [
                new TradeCriterion { Name = "cost", Weight = 1, Direction = "minimize" },
                new TradeCriterion { Name = "range", Weight = 1, Direction = "maximize", Source = "tag" },
            ],
        };
        var study = TradeStudies.Create(m, m.Root, "Variant choice", def);
        var reloaded = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        var again = TradeStudyDefinition.Parse(reloaded.Find(study.Id)!);
        Assert.Equal(2, again.Criteria.Count);
        Assert.Equal("minimize", again.Criteria[0].Direction);
        var r = TradeStudies.Evaluate(reloaded, again);
        Assert.Equal(24300, r.Rows.Single(x => x.Alternative.Name == "Eco").Raw[0]);   // 15000 + 9000 + 300
        Assert.Equal(21600, r.Rows.Single(x => x.Alternative.Name == "Touring").Raw[0]);
        Assert.Equal("Touring", r.Winner!.Alternative.Name);
        Assert.Contains("cost ×1", string.Join(" ", Notation.Content(reloaded, reloaded.Find(study.Id)!, DiagramType.BlockDefinition).Compartments.SelectMany(c => c.Lines)));
    }

    [Fact]
    public void Parametric_Values_Can_Feed_Criteria()
    {
        var m = SampleModels.Vehicle();
        var vehicle = m.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle");
        var def = new TradeStudyDefinition { Alternatives = [vehicle.Id], Criteria = [new TradeCriterion { Name = "accelTime", Source = "parametric", Direction = "minimize" }] };
        var r = TradeStudies.Evaluate(m, def);
        Assert.Equal(100 / 3.6 * 1450 / 5600, r.Rows[0].Raw[0]!.Value, 6);
    }
}

public class SimulationDepthTests
{
    private static (ModelRepository M, Element Activity, Element Machine) Mission()
    {
        var m = new ModelRepository("Mission");
        var rocket = m.Create(m.Root, ElementKind.Block, "Rocket");
        m.Set(m.Create(rocket, ElementKind.Property, "temp"), FieldKeys.DefaultValue, "20");

        var act = m.Create(rocket, ElementKind.Activity, "Launch Sequence");
        var init = m.Create(act, ElementKind.InitialNode);
        var prep = m.Create(act, ElementKind.Action, "Prepare");
        m.Set(prep, ExtFieldKeys.Duration, "2s");
        var fork = m.Create(act, ElementKind.ForkNode);
        var launch = m.Create(act, ElementKind.Action, "Ignite");
        m.Set(launch, ExtFieldKeys.Duration, "3000ms");
        m.Set(launch, ExtFieldKeys.SendSignal, "launched");
        var wait = m.Create(act, ElementKind.Action, "Await liftoff");
        m.Set(wait, ExtFieldKeys.AcceptEvent, "launched");
        var track = m.Create(act, ElementKind.Action, "Track");
        m.Set(track, ExtFieldKeys.Duration, "1 min");
        m.Set(track, FieldKeys.Body, "tracked = 1");
        var join = m.Create(act, ElementKind.ForkNode);
        var fin = m.Create(act, ElementKind.ActivityFinalNode);
        m.Connect(ElementKind.ControlFlow, init, prep);
        m.Connect(ElementKind.ControlFlow, prep, fork);
        m.Connect(ElementKind.ControlFlow, fork, launch);
        m.Connect(ElementKind.ControlFlow, fork, wait);
        m.Connect(ElementKind.ControlFlow, wait, track);
        m.Connect(ElementKind.ControlFlow, launch, join);
        m.Connect(ElementKind.ControlFlow, track, join);
        m.Connect(ElementKind.ControlFlow, join, fin);

        var sm = m.Create(rocket, ElementKind.StateMachine, "Thermal");
        var si = m.Create(sm, ElementKind.InitialState);
        var idle = m.Create(sm, ElementKind.State, "Idle");
        var hot = m.Create(sm, ElementKind.State, "Burning");
        m.Set(hot, FieldKeys.Entry, "temp = 100");
        var cooling = m.Create(sm, ElementKind.State, "Cooling");
        m.Set(cooling, FieldKeys.Entry, "temp = 40");
        var done = m.Create(sm, ElementKind.FinalState);
        m.Connect(ElementKind.Transition, si, idle);
        m.Rename(m.Connect(ElementKind.Transition, idle, hot), "launched");
        m.Rename(m.Connect(ElementKind.Transition, hot, cooling), "after(10s)");
        var cool = m.Connect(ElementKind.Transition, cooling, done);
        m.Rename(cool, "when(temp < 50)");
        return (m, act, sm);
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("2.5s", 2.5)]
    [InlineData("200ms", 0.2)]
    [InlineData("1 min", 60)]
    [InlineData("2h", 7200)]
    [InlineData("x * 2", 6)]
    public void Durations_Parse_With_Units_Or_Expressions(string text, double seconds) =>
        Assert.Equal(seconds, DiscreteEventSimulation.ParseDuration(text, new Dictionary<string, double> { ["x"] = 3 })!.Value, 9);

    [Fact]
    public void Triggers_Parse_From_Transition_Names()
    {
        Assert.Equal((DiscreteEventSimulation.TriggerKind.After, "5s"), DiscreteEventSimulation.ParseTrigger("after(5s)"));
        Assert.Equal((DiscreteEventSimulation.TriggerKind.When, "x > 3"), DiscreteEventSimulation.ParseTrigger("when(x > 3)"));
        Assert.Equal((DiscreteEventSimulation.TriggerKind.Signal, "go"), DiscreteEventSimulation.ParseTrigger("go [ready] / x = 1"));
        Assert.Equal((DiscreteEventSimulation.TriggerKind.Completion, ""), DiscreteEventSimulation.ParseTrigger(""));
    }

    [Fact]
    public void Timed_Activity_And_State_Machine_Share_Clock_And_Signals()
    {
        var (m, act, sm) = Mission();
        var sim = new DiscreteEventSimulation(m, [act, sm]);
        sim.Start();
        sim.RunToCompletion();
        Assert.True(sim.Finished);
        Assert.Equal(65, sim.Clock, 9);                                   // 2 s + 3 s, then 60 s of tracking
        Assert.Equal(1, sim.Variables["tracked"]);
        Assert.Equal(40, sim.Variables["temp"]);
        var signal = Assert.Single(sim.Signals);
        Assert.Equal(5, signal.Time, 9);
        Assert.Equal(2, signal.ToRows.Count);                             // the accept event and the state machine
        Assert.Contains(sim.Trace, t => t.Kind == TraceKind.Transition && t.Text.Contains("after(10s)") && Math.Abs(t.Time - 15) < 1e-9);
        Assert.Contains(sim.Trace, t => t.Kind == TraceKind.Transition && t.Text.Contains("when(temp < 50)"));
        var track = sim.Spans.Single(s => s.Element.Name == "Track");
        Assert.Equal(5, track.Start, 9);
        Assert.Equal(65, track.End, 9);
        var waitSpan = sim.Spans.Single(s => s.Element.Name == "Await liftoff" && s.Kind == "wait");
        Assert.Equal(2, waitSpan.Start, 9);
        Assert.Equal(5, waitSpan.End, 9);
        var burning = sim.Spans.Single(s => s.Element.Name == "Burning");
        Assert.Equal(10, burning.End - burning.Start, 9);
        Assert.All(sim.Spans, s => Assert.False(s.Open));
    }

    [Fact]
    public void Stepping_Advances_The_Clock_Event_By_Event()
    {
        var (m, act, _) = Mission();
        var sim = new DiscreteEventSimulation(m, [act]);
        sim.Start();
        sim.RunUntil(3);
        Assert.Equal(3, sim.Clock, 9);
        Assert.Contains(sim.Active, e => e.Name == "Ignite");
        Assert.Contains(sim.Active, e => e.Name == "Await liftoff");
        Assert.Equal(5, sim.NextEventTime!.Value, 9);
        Assert.True(sim.Step());
        Assert.Equal(5, sim.Clock, 9);
    }

    [Fact]
    public void Time_Triggers_Are_Cancelled_When_The_State_Is_Left()
    {
        var m = new ModelRepository("T");
        var sm = m.Create(m.Root, ElementKind.StateMachine, "Timer");
        var i = m.Create(sm, ElementKind.InitialState);
        var a = m.Create(sm, ElementKind.State, "A");
        var b = m.Create(sm, ElementKind.State, "B");
        var c = m.Create(sm, ElementKind.State, "TimedOut");
        m.Connect(ElementKind.Transition, i, a);
        m.Rename(m.Connect(ElementKind.Transition, a, b), "go");
        m.Rename(m.Connect(ElementKind.Transition, a, c), "after(10)");
        var sim = new DiscreteEventSimulation(m, [sm]);
        sim.Start();
        sim.RunUntil(4);
        sim.Inject("go");
        sim.RunToCompletion();
        Assert.Contains(sim.Active, e => e.Name == "B");
        Assert.DoesNotContain(sim.Trace, t => t.Text.Contains("TimedOut"));
    }

    [Fact]
    public void Monte_Carlo_Samples_Distributions_Through_Parametrics()
    {
        var m = SampleModels.Vehicle();
        var vehicle = m.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle");
        m.Set(vehicle.OwnedElements.Single(p => p.Name == "mass"), ExtFieldKeys.Distribution, "normal(1450, 100)");
        var run = MonteCarlo.Run(m, vehicle, 3000, seed: 7);
        Assert.Empty(run.Problems);
        Assert.Single(run.Inputs);
        var accel = run.Samples["accelTime"];
        Assert.Equal(3000, accel.Count);
        var stats = SampleStats.Of(accel);
        Assert.InRange(stats.Mean, 7.12, 7.26);                               // 27.78 * 1450 / 5600 ≈ 7.19 s
        Assert.InRange(stats.StdDev, 0.45, 0.54);                             // 27.78 * 100 / 5600 ≈ 0.50 s
        Assert.True(stats.P5 < stats.P50 && stats.P50 < stats.P95);
        var p = MonteCarlo.Probability(accel, "<", 8);
        Assert.InRange(p, 0.92, 0.97);                                        // Φ(1.63) ≈ 0.948
        var (edges, counts) = MonteCarlo.Histogram(accel, 20);
        Assert.Equal(21, edges.Length);
        Assert.Equal(3000, counts.Sum());
        // Same seed, same samples.
        Assert.Equal(accel, MonteCarlo.Run(m, vehicle, 3000, seed: 7).Samples["accelTime"]);
    }

    [Theory]
    [InlineData("normal(10, 2)", "normal")]
    [InlineData("uniform(1, 5)", "uniform")]
    [InlineData("triangular(1, 2, 5)", "triangular")]
    [InlineData("lognormal(0, 0.5)", "lognormal")]
    public void Distributions_Parse_And_Sample_In_Range(string text, string kind)
    {
        var d = Distribution.Parse(text)!;
        Assert.Equal(kind, d.Kind);
        var rng = new Random(1);
        var xs = Enumerable.Range(0, 2000).Select(_ => d.Sample(rng)).ToList();
        if (kind == "uniform") Assert.All(xs, x => Assert.InRange(x, 1, 5));
        if (kind == "triangular") Assert.All(xs, x => Assert.InRange(x, 1, 5));
        if (kind == "normal") Assert.InRange(xs.Average(), 9.8, 10.2);
        Assert.Null(Distribution.Parse("uniform(5, 1)"));
        Assert.Null(Distribution.Parse("weird(1)"));
    }

    [Theory]
    [InlineData("The vehicle shall accelerate from 0 to 100 km/h in less than 8 s.", "<", 8)]
    [InlineData("The vehicle shall travel at least 600 km on a full tank.", ">=", 600)]
    [InlineData("The vehicle shall stop from 100 km/h within 40 m.", "<=", 40)]
    [InlineData("Mass ≤ 1500 kg", "<=", 1500)]
    public void Requirement_Thresholds_Parse(string text, string op, double value)
    {
        var t = MonteCarlo.ParseThreshold(text)!.Value;
        Assert.Equal(op, t.Op);
        Assert.Equal(value, t.Value);
    }
}

public class FmiTests
{
    private const string Fmi2 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <fmiModelDescription fmiVersion="2.0" modelName="Vehicle.Dynamics" guid="{abc-123}" description="Longitudinal dynamics">
          <CoSimulation modelIdentifier="VehicleDynamics" canHandleVariableCommunicationStepSize="true"/>
          <UnitDefinitions><Unit name="m/s"/><Unit name="N"/><Unit name="kg"/></UnitDefinitions>
          <TypeDefinitions><SimpleType name="Force"><Real quantity="Force" unit="N"/></SimpleType></TypeDefinitions>
          <ModelVariables>
            <ScalarVariable name="time" valueReference="0" causality="independent" variability="continuous"><Real/></ScalarVariable>
            <ScalarVariable name="force" valueReference="1" causality="input" variability="continuous"><Real declaredType="Force" start="0"/></ScalarVariable>
            <ScalarVariable name="speed" valueReference="2" causality="output" variability="continuous" description="Vehicle speed"><Real unit="m/s"/></ScalarVariable>
            <ScalarVariable name="mass" valueReference="3" causality="parameter" variability="fixed"><Real unit="kg" start="1450"/></ScalarVariable>
            <ScalarVariable name="gear" valueReference="4" causality="output" variability="discrete"><Integer/></ScalarVariable>
            <ScalarVariable name="der(speed)" valueReference="5" causality="local"><Real/></ScalarVariable>
          </ModelVariables>
          <ModelStructure><Outputs><Unknown index="3"/></Outputs></ModelStructure>
        </fmiModelDescription>
        """;

    private const string Fmi3 = """
        <fmiModelDescription fmiVersion="3.0" modelName="Battery" instantiationToken="{tok}">
          <CoSimulation modelIdentifier="Battery"/>
          <ModelVariables>
            <Float64 name="time" valueReference="0" causality="independent"/>
            <Float64 name="current" valueReference="1" causality="input" unit="A" start="0"/>
            <Float64 name="soc" valueReference="2" causality="output"/>
            <Float64 name="capacity" valueReference="3" causality="parameter" variability="fixed" start="75" unit="kWh"/>
            <Boolean name="fault" valueReference="4" causality="output"/>
          </ModelVariables>
        </fmiModelDescription>
        """;

    [Fact]
    public void Parses_Fmi2_Model_Description()
    {
        var md = FmiImport.Read(new MemoryStream(Encoding.UTF8.GetBytes(Fmi2)));
        Assert.Equal("2.0", md.FmiVersion);
        Assert.Equal("VehicleDynamics", md.ModelIdentifier);
        Assert.True(md.CoSimulation);
        Assert.Equal(6, md.Variables.Count);
        Assert.Equal("N", md.Variables.Single(v => v.Name == "force").Unit);   // from the declared type
        Assert.Equal(["m/s", "N", "kg"], md.Units);
    }

    [Fact]
    public void Imports_Fmu_Archive_As_Block_With_Ports_And_Values()
    {
        var zipBytes = new MemoryStream();
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("modelDescription.xml").Open())) w.Write(Fmi3);
            using (var w = new StreamWriter(zip.CreateEntry("binaries/darwin64/Battery.dylib").Open())) w.Write("not really a library");
        }
        zipBytes.Position = 0;
        var md = FmiImport.Read(zipBytes);
        Assert.Equal("3.0", md.FmiVersion);
        Assert.Equal("{tok}", md.Guid);
        Assert.Single(md.Binaries);

        var m = new ModelRepository("M");
        var block = FmiImport.CreateBlock(m, m.Root, md, "Battery.fmu");
        Assert.Equal("Battery", block.Name);
        Assert.Contains("FMU", block.Stereotypes);
        var current = block.OwnedElements.Single(e => e.Name == "current");
        Assert.Equal(ElementKind.Port, current.Kind);
        Assert.Equal("in", current.Get(FieldKeys.Direction));
        Assert.Equal("A", current.Get(ExtFieldKeys.Unit));
        var fault = block.OwnedElements.Single(e => e.Name == "fault");
        Assert.Equal("out", fault.Get(FieldKeys.Direction));
        Assert.Equal("Boolean", fault.TypeRef);
        var cap = block.OwnedElements.Single(e => e.Name == "capacity");
        Assert.Equal(ElementKind.Property, cap.Kind);
        Assert.Equal("75", cap.Get(FieldKeys.DefaultValue));
        Assert.DoesNotContain(block.OwnedElements, e => e.Name == "time");
        Assert.Equal("supported", block.Get(FieldKeys.TagPrefix + "fmi.cosimulation"));

        // The imported block can type a part, and its ports can be connected.
        var sys = m.Create(m.Root, ElementKind.Block, "System");
        var part = m.Create(sys, ElementKind.Property, "battery");
        m.Set(part, FieldKeys.Type, block.Id);
        Assert.Same(block, m.ResolveType(part));
        m.History.Undo(); m.History.Undo(); m.History.Undo(); m.History.Undo();
        Assert.Empty(m.Root.OwnedElements);
    }

    [Fact]
    public void Rejects_Non_Fmi_Xml() =>
        Assert.Throws<FormatException>(() => FmiImport.Read(new MemoryStream(Encoding.UTF8.GetBytes("<foo/>"))));
}
