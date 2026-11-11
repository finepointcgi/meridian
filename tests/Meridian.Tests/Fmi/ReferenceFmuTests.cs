using System.Diagnostics;
using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Serialization;
using Meridian.Serialization.Fmi;

namespace Meridian.Tests.Fmi;

/// <summary>
/// Co-simulation with real FMUs: the Modelica Association's Reference FMUs (BouncingBall, Dahlquist, Feedthrough), FMI 2.0
/// and 3.0, each run through Meridian.FmuHost in its own process exactly as the app runs them, checked against known
/// values and the reference results that ship with them.
/// </summary>
public class ReferenceFmuTests
{
    private sealed class Rig
    {
        public ModelRepository M { get; } = new("Reference");
        public Element System { get; }
        public Dictionary<string, string> FmuPaths { get; } = new();
        public Rig() { System = M.Create(M.Root, ElementKind.Block, "System"); }

        public Element Import(string fmuPath)
        {
            using var s = File.OpenRead(fmuPath);
            var block = FmiImport.CreateBlock(M, M.Root, FmiImport.Read(s), Path.GetFileName(fmuPath));
            FmuPaths[block.Id] = fmuPath;
            return block;
        }

        public Element Part(string name, Element type)
        {
            var p = M.Create(System, ElementKind.Property, name);
            M.Set(p, FieldKeys.Type, type.Id);
            return p;
        }

        public void Connect(Element fromPart, string fromPort, Element toPart, string toPort)
        {
            var a = M.ResolveType(fromPart)!.OwnedElements.Single(e => e.Kind == ElementKind.Port && e.Name == fromPort);
            var b = M.ResolveType(toPart)!.OwnedElements.Single(e => e.Kind == ElementKind.Port && e.Name == toPort);
            var r = M.Connect(ElementKind.Connector, a, b, System);
            M.Set(r, FieldKeys.SourcePart, fromPart.Id);
            M.Set(r, FieldKeys.TargetPart, toPart.Id);
        }

        public CoSimPlan Plan()
        {
            var plan = CoSimulation.Plan(M, System);
            Assert.False(plan.HasErrors, string.Join("\n", plan.Problems.Select(p => p.Message)));
            return plan;
        }

        public CoSimResult Run(CoSimOptions o, List<FmuUnit>? units = null, CancellationToken ct = default, IProgress<double>? progress = null)
        {
            var factory = FmuUnit.Factory(b => FmuPaths.GetValueOrDefault(b.Id), null, ct);
            var result = CoSimulation.Run(M, Plan(), o, (part, pars) =>
            {
                var u = (FmuUnit)factory(part, pars);
                units?.Add(u);
                return u;
            }, progress, ct);
            return result;
        }
    }

    private static void Succeeded(CoSimResult r) =>
        Assert.True(r.Completed && r.Error == null, $"Run failed: {r.Error}\n{string.Join("\n", r.Log)}");

    private static double At(CoSimResult r, string key, double t)
    {
        var i = r.Time.FindIndex(x => Math.Abs(x - t) < 1e-9);
        Assert.True(i >= 0, $"no sample at t = {t}");
        return r.Series[key][i];
    }

    // ================================================================== Dahlquist: x' = -k x, explicit Euler with h = 0.1

    [SkippableTheory]
    [InlineData("2.0")]
    [InlineData("3.0")]
    public void Dahlquist_Matches_The_Explicit_Euler_Solution_And_The_Reference_Result(string version)
    {
        var fmu = ReferenceFmus.Get("Dahlquist", version);
        var rig = new Rig();
        var block = rig.Import(fmu);
        Assert.Equal("0.1", block.Get(FieldKeys.TagPrefix + "fmi.defaultStepSize"));
        Assert.Equal("10", block.Get(FieldKeys.TagPrefix + "fmi.defaultStopTime"));
        rig.Part("d", block);
        var plan = rig.Plan();
        Assert.Equal(0.1, plan.SuggestedStep);
        Assert.Equal(10, plan.SuggestedStop);

        var r = rig.Run(new CoSimOptions { Stop = 10, Step = 0.1 });
        Succeeded(r);
        Assert.Equal(101, r.Time.Count);
        for (var n = 0; n <= 100; n++) Assert.Equal(Math.Pow(0.9, n), r.Series["d.x"][n], 12);
        Assert.Equal(0.3486784401, At(r, "d.x", 1), 12);

        var (time, cols) = ReferenceFmus.ReferenceResult("Dahlquist");
        for (var i = 0; i < time.Count; i++) Assert.Equal(cols["x"][i], At(r, "d.x", time[i]), 9);

        // A parameter changes the solution: k = 2 gives (1 - 0.2)^n.
        r = rig.Run(new CoSimOptions { Stop = 1, Step = 0.1, Parameters = { ["d.k"] = 2 } });
        Succeeded(r);
        Assert.Equal(Math.Pow(0.8, 10), r.Series["d.x"][^1], 12);
    }

    [SkippableTheory]
    [InlineData("2.0")]
    [InlineData("3.0")]
    public void A_Communication_Step_Smaller_Than_The_Fmus_Solver_Step_Holds_Outputs_Between_Its_Steps(string version)
    {
        var rig = new Rig();
        rig.Part("d", rig.Import(ReferenceFmus.Get("Dahlquist", version)));
        var r = rig.Run(new CoSimOptions { Stop = 1, Step = 0.025 });
        Succeeded(r);
        Assert.Equal(1, At(r, "d.x", 0.05), 12);                 // before the FMU's first 0.1 s solver step
        Assert.Equal(0.9, At(r, "d.x", 0.1), 12);
        Assert.Equal(0.9, At(r, "d.x", 0.175), 12);
        Assert.Equal(0.3486784401, r.Series["d.x"][^1], 12);
        if (version == "3.0")
            Assert.Contains(r.Problems, p => p.Severity == CoSimSeverity.Info && p.Message.Contains("internal step of 0.1 s"));
    }

    // ================================================================== BouncingBall: events inside the FMU

    [SkippableTheory]
    [InlineData("2.0")]
    [InlineData("3.0")]
    public void BouncingBall_Follows_The_Reference_Trajectory(string version)
    {
        var rig = new Rig();
        var block = rig.Import(ReferenceFmus.Get("BouncingBall", version));
        var ports = block.OwnedElements.Where(e => e.Kind == ElementKind.Port).ToDictionary(p => p.Name);
        Assert.Equal("m", ports["h"].Get(ExtFieldKeys.Unit));     // from the declared type Position
        Assert.Equal("m/s", ports["v"].Get(ExtFieldKeys.Unit));
        rig.Part("ball", block);
        var r = rig.Run(new CoSimOptions { Stop = 3, Step = 0.01 });
        Succeeded(r);
        Assert.Equal(301, r.Time.Count);

        var (time, cols) = ReferenceFmus.ReferenceResult("BouncingBall");
        for (var i = 0; i < time.Count; i++)
        {
            Assert.Equal(cols["h"][i], At(r, "ball.h", time[i]), 1e-9);
            Assert.Equal(cols["v"][i], At(r, "ball.v", time[i]), 1e-9);
        }
        // Known values: the first bounce near t = 0.45 s (√(2 / 9.81)), rebounding at e · v.
        var bounce = r.Series["ball.v"].FindIndex(v => v > 0);
        Assert.InRange(r.Time[bounce], 0.44, 0.47);
        Assert.InRange(r.Series["ball.v"][bounce], 0.7 * 4.3, 0.7 * 4.5);
        Assert.All(r.Series["ball.h"], h => Assert.True(h > -0.05, $"h = {h}"));
    }

    [SkippableFact]
    public void BouncingBall_In_Fmi_2_And_3_Give_The_Same_Result_And_Take_A_Tunable_Parameter()
    {
        var results = new List<CoSimResult>();
        foreach (var version in new[] { "2.0", "3.0" })
        {
            var rig = new Rig();
            rig.Part("ball", rig.Import(ReferenceFmus.Get("BouncingBall", version)));
            var r = rig.Run(new CoSimOptions { Stop = 2, Step = 0.01, Parameters = { ["ball.e"] = 0.5 } });
            Succeeded(r);
            results.Add(r);
        }
        for (var i = 0; i < results[0].Time.Count; i++) Assert.Equal(results[0].Series["ball.h"][i], results[1].Series["ball.h"][i], 9);
        var bounce = results[0].Series["ball.v"].FindIndex(v => v > 0);
        Assert.InRange(results[0].Series["ball.v"][bounce], 0.5 * 4.3, 0.5 * 4.5);
    }

    // ================================================================== Feedthrough: inputs of every type

    [SkippableTheory]
    [InlineData("2.0")]
    [InlineData("3.0")]
    public void Feedthrough_Passes_Connected_Inputs_Of_Several_Types_And_Ignores_The_Ones_It_Cannot_Exchange(string version)
    {
        var rig = new Rig();
        var ft = rig.Import(ReferenceFmus.Get("Feedthrough", version));
        Assert.Equal("Int32", ft.OwnedElements.Single(p => p.Name == "Int32_input").Get(FieldKeys.TagPrefix + "fmi.type").Replace("Integer", "Int32"));
        if (version == "3.0") Assert.Equal("Binary", ft.OwnedElements.Single(p => p.Name == "Binary_input").Get(FieldKeys.TagPrefix + "fmi.type"));
        var source = rig.M.Create(rig.M.Root, ElementKind.Block, "Source");
        foreach (var (name, expr) in new[] { ("real", "2 * time + 0.5"), ("count", "7"), ("flag", "1") })
        {
            var port = rig.M.Create(source, ElementKind.Port, name);
            rig.M.Set(port, FieldKeys.Direction, "out");
            rig.M.Set(port, FieldKeys.DefaultValue, expr);
        }
        var src = rig.Part("src", source);
        var thru = rig.Part("thru", ft);
        rig.Connect(src, "real", thru, "Float64_continuous_input");
        rig.Connect(src, "count", thru, "Int32_input");
        rig.Connect(src, "flag", thru, "Boolean_input");
        var plan = rig.Plan();
        if (version == "3.0")
            Assert.Contains(plan.Problems, p => p.Severity == CoSimSeverity.Info && p.Message.Contains("Binary_input") && p.Message.Contains("not exchanged"));

        var r = rig.Run(new CoSimOptions { Stop = 1, Step = 0.1, Parameters = { ["thru.Float64_tunable_parameter"] = 3 } });
        Succeeded(r);
        for (var i = 1; i < r.Time.Count; i++)
        {
            // The FMU sees the source's value at the start of each step and passes it straight through.
            Assert.Equal(2 * r.Time[i - 1] + 0.5, r.Series["thru.Float64_continuous_output"][i], 9);
            Assert.Equal(7, r.Series["thru.Int32_output"][i]);
            Assert.Equal(1, r.Series["thru.Boolean_output"][i]);
        }
        Assert.True(double.IsNaN(r.Series["thru.String_output"][^1]));   // a string output is recorded as NaN, not an error
    }

    [SkippableFact]
    public void An_Fmi2_And_An_Fmi3_Fmu_Exchange_Values_Through_A_Connector()
    {
        var rig = new Rig();
        var d = rig.Part("d", rig.Import(ReferenceFmus.Get("Dahlquist", "2.0")));
        var thru = rig.Part("thru", rig.Import(ReferenceFmus.Get("Feedthrough", "3.0")));
        rig.Connect(d, "x", thru, "Float64_continuous_input");
        var r = rig.Run(new CoSimOptions { Stop = 1, Step = 0.1 });
        Succeeded(r);
        // Gauss-Seidel: Dahlquist steps first, so Feedthrough sees x(t + h) in the same step.
        for (var i = 0; i < r.Time.Count; i++) Assert.Equal(r.Series["d.x"][i], r.Series["thru.Float64_continuous_output"][i], 12);
    }

    // ================================================================== process handling

    [SkippableFact]
    public void Cancelling_A_Run_Stops_The_Fmu_Host_Process_And_Removes_The_Unpacked_Fmu()
    {
        var rig = new Rig();
        rig.Part("ball", rig.Import(ReferenceFmus.Get("BouncingBall", "3.0")));
        using var cts = new CancellationTokenSource();
        var units = new List<FmuUnit>();
        var progress = new SyncProgress(f => { if (f > 0.002) cts.Cancel(); });
        var r = rig.Run(new CoSimOptions { Stop = 10_000, Step = 0.001 }, units, cts.Token, progress);
        Assert.True(r.Cancelled, r.Error);
        Assert.False(r.Completed);
        var unit = Assert.Single(units);
        Assert.True(Exited(unit.HostProcessId), "the FMU host process is still running");
        Assert.False(Directory.Exists(unit.ExtractedTo));

        // The same FMU runs again normally afterwards.
        var again = rig.Run(new CoSimOptions { Stop = 0.1, Step = 0.01 });
        Succeeded(again);
    }

    [SkippableFact]
    public void A_Finished_Run_Leaves_No_Host_Process_Or_Files_Behind()
    {
        var rig = new Rig();
        rig.Part("d", rig.Import(ReferenceFmus.Get("Dahlquist", "2.0")));
        rig.Part("ball", rig.Import(ReferenceFmus.Get("BouncingBall", "3.0")));
        var units = new List<FmuUnit>();
        Succeeded(rig.Run(new CoSimOptions { Stop = 0.5, Step = 0.01 }, units));
        Assert.Equal(2, units.Count);
        Assert.Equal(2, units.Select(u => u.HostProcessId).Distinct().Count());   // one process per FMU
        Assert.All(units, u => Assert.True(Exited(u.HostProcessId)));
        Assert.All(units, u => Assert.False(Directory.Exists(u.ExtractedTo)));
    }

    private static bool Exited(int pid)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) return true;
            }
            catch (ArgumentException) { return true; }
            catch (InvalidOperationException) { return true; }
            Thread.Sleep(50);
        }
        return false;
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
}
