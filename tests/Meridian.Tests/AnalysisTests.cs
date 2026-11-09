using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;

namespace Meridian.Tests;

public class AnalysisTests
{
    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("2 ^ 3 ^ 2", 512)]
    [InlineData("-2 ^ 2", -4)]
    [InlineData("sqrt(16) + max(1, 5, 3)", 9)]
    [InlineData("x > 3 && y == 2", 1)]
    [InlineData("!(x > 3) || false", 0)]
    [InlineData("engine.x * 2", 20)]
    public void Expressions_Evaluate(string expr, double expected)
    {
        var vars = new Dictionary<string, double> { ["x"] = 10, ["y"] = 2 };
        Assert.Equal(expected, Expressions.Evaluate(expr, vars), 9);
    }

    [Fact]
    public void Statements_Assign_And_Accumulate()
    {
        var vars = new Dictionary<string, double>();
        Expressions.Execute("a = 2; a += 3; b = a * 2; ok = b >= 10", vars);
        Assert.Equal(5, vars["a"]);
        Assert.Equal(10, vars["b"]);
        Assert.Equal(1, vars["ok"]);
        Assert.Throws<Expressions.ExpressionException>(() => Expressions.Evaluate("unknown + 1", vars));
    }

    [Fact]
    public void Parametric_Solver_Computes_Unknowns_From_Bindings()
    {
        var m = SampleModels.Vehicle();
        var vehicle = m.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle");
        var result = Parametrics.Solve(m, vehicle);
        Assert.True(result.Complete, string.Join("; ", result.Problems.Concat(result.Unsolved)));
        double V(string name) => result.Values.Single(v => v.Name == name).Value!.Value;
        Assert.Equal(100 / 3.6 * 1450 / 5600, V("accelTime"), 6);     // ≈ 7.19 s — meets REQ-002 (< 8 s)
        Assert.Equal(5600 * 50 / 1000.0, V("requiredPower"), 6);       // 280 kW
        Assert.True(result.Values.Single(v => v.Name == "accelTime").Computed);
        Assert.False(result.Values.Single(v => v.Name == "mass").Computed);
    }

    [Fact]
    public void Parametric_Solver_Inverts_Equations()
    {
        var m = SampleModels.Vehicle();
        var vehicle = m.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle");
        // Fix the acceleration time and leave the force unknown: the solver must invert time = k * mass / force.
        m.Set(vehicle.OwnedElements.Single(p => p.Name == "accelTime"), FieldKeys.DefaultValue, "8");
        m.Set(vehicle.OwnedElements.Single(p => p.Name == "driveForce"), FieldKeys.DefaultValue, null);
        var result = Parametrics.Solve(m, vehicle);
        Assert.True(result.Complete, string.Join("; ", result.Problems.Concat(result.Unsolved)));
        Assert.Equal(100 / 3.6 * 1450 / 8, result.Values.Single(v => v.Name == "driveForce").Value!.Value, 4);
    }

    [Fact]
    public void Activity_Simulation_Follows_Guards_Until_Final()
    {
        var m = SampleModels.Vehicle();
        var sim = new ActivitySimulator(m, m.OfKind(ElementKind.Activity).Single());
        sim.Start();
        sim.Run();
        Assert.True(sim.Finished);
        Assert.Equal(2, sim.Variables["attempts"]);       // cranked twice: first attempt fails the "started" guard
        Assert.Equal(800, sim.Variables["rpm"]);
        Assert.Contains(sim.Log, l => l.Contains("Activity final"));
    }

    [Fact]
    public void State_Machine_Simulation_Handles_Events_And_Guards()
    {
        var m = SampleModels.Vehicle();
        var sim = new StateMachineSimulator(m, m.OfKind(ElementKind.StateMachine).Single());
        sim.Start();
        Assert.Equal("Off", sim.Current!.Name);
        Assert.True(sim.Send("ignition"));
        Assert.Equal("Idling", sim.Current!.Name);
        Assert.Equal(800, sim.Variables["rpm"]);
        Assert.False(sim.Step());                          // throttle == 0: no completion transition enabled
        sim.Variables["throttle"] = 0.5;
        Assert.True(sim.Step());
        Assert.Equal("Running", sim.Current!.Name);
        Assert.Equal(800 + 0.5 * 5200, sim.Variables["rpm"]);
        Assert.False(sim.Send("keyOff"));                  // keyOff is only valid from Idling
    }
}
