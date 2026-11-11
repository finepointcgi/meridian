using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Serialization;
using Meridian.Serialization.Fmi;

namespace Meridian.Tests.Fmi;

/// <summary>Co-simulation: the FMU runtime (real FMUs built from C at test time), the master algorithm and the safety checks.</summary>
public class CoSimulationTests
{
    // ---------------------------------------------------------------- model helpers

    private sealed class Rig
    {
        public ModelRepository M { get; } = new("CoSim");
        public Element System { get; }
        public Dictionary<string, string> FmuPaths { get; } = new();   // block id → .fmu
        public Rig() { System = M.Create(M.Root, ElementKind.Block, "System"); }

        public Element ImportFmu(string fmuPath)
        {
            using var s = File.OpenRead(fmuPath);
            var block = FmiImport.CreateBlock(M, M.Root, FmiImport.Read(s), Path.GetFileName(fmuPath));
            FmuPaths[block.Id] = fmuPath;
            return block;
        }

        public Element ImportXml(string xml)
        {
            var block = FmiImport.CreateBlock(M, M.Root, FmiImport.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml))));
            return block;
        }

        public Element Part(string name, Element type)
        {
            var p = M.Create(System, ElementKind.Property, name);
            M.Set(p, FieldKeys.Type, type.Id);
            return p;
        }

        public Relationship Connect(Element fromPart, string fromPort, Element toPart, string toPort)
        {
            var a = M.ResolveType(fromPart)!.OwnedElements.Single(e => e.Kind == ElementKind.Port && e.Name == fromPort);
            var b = M.ResolveType(toPart)!.OwnedElements.Single(e => e.Kind == ElementKind.Port && e.Name == toPort);
            var r = M.Connect(ElementKind.Connector, a, b, System);
            M.Set(r, FieldKeys.SourcePart, fromPart.Id);
            M.Set(r, FieldKeys.TargetPart, toPart.Id);
            return r;
        }

        public CoSimResult Run(CoSimOptions o, FmuRuntimeOptions? ro = null)
        {
            var plan = CoSimulation.Plan(M, System);
            Assert.False(plan.HasErrors, string.Join("\n", plan.Problems.Select(p => p.Message)));
            return CoSimulation.Run(M, plan, o, FmuUnit.Factory(b => FmuPaths.GetValueOrDefault(b.Id), ro));
        }
    }

    private static TestFmus.Set Fmus()
    {
        var set = TestFmus.Get();
        Skip.If(set == null, TestFmus.SkipReason);
        return set!;
    }

    private static void Succeeded(CoSimResult r) =>
        Assert.True(r.Completed && r.Error == null, $"Run failed: {r.Error}\n{string.Join("\n", r.Log)}");

    // ---------------------------------------------------------------- FMU runtime

    [SkippableFact]
    public void Fmi2_Lag_Matches_The_Analytic_Step_Response()
    {
        var fmus = Fmus();
        var rig = new Rig();
        var lag = rig.Part("lag", rig.ImportFmu(fmus.Lag));
        var o = new CoSimOptions { Stop = 5, Step = 0.01, Parameters = { ["lag.T"] = 2, ["lag.u"] = 1 } };
        var r = rig.Run(o);
        Succeeded(r);
        var y = r.Series["lag.y"];
        Assert.Equal(501, r.Time.Count);
        Assert.Equal(5, r.Time[^1], 12);
        for (var i = 0; i < r.Time.Count; i++)
            Assert.Equal(1 - Math.Exp(-r.Time[i] / 2), y[i], 9);   // exact discretisation: matches at every communication point
        Assert.Contains(r.Log, l => l.Contains("instantiated"));     // logger callback reached Meridian
        Assert.Equal("m", r.Units["lag.y"]);
        Assert.NotNull(lag);
    }

    [SkippableFact]
    public void Fmi3_Lag_Matches_The_Analytic_Solution_And_Can_End_The_Run()
    {
        var fmus = Fmus();
        var rig = new Rig();
        rig.Part("lag", rig.ImportFmu(fmus.Lag3));
        var r = rig.Run(new CoSimOptions { Stop = 3, Step = 0.05, Parameters = { ["lag.T"] = 0.5, ["lag.x0"] = 2, ["lag.u"] = 1 } });
        Succeeded(r);
        for (var i = 0; i < r.Time.Count; i++)
            Assert.Equal(1 + Math.Exp(-r.Time[i] / 0.5), r.Series["lag.y"][i], 9);

        // terminateSimulation from fmi3DoStep ends the run early (and cleanly).
        var early = rig.Run(new CoSimOptions { Stop = 3, Step = 0.05, Parameters = { ["lag.stopAt"] = 1 } });
        Succeeded(early);
        Assert.Equal(1, early.Time[^1], 9);
        Assert.Contains(early.Problems, p => p.Message.Contains("asked to end"));
    }

    [SkippableFact]
    public void Two_Fmus_In_A_Feedback_Loop_Follow_The_Closed_Loop_Solution()
    {
        // lag: T y' = u - y, gain: u = k y  ⇒  y' = (k - 1) y / T, y(0) = x0  ⇒  y = x0 e^((k-1) t / T).
        var fmus = Fmus();
        foreach (var algorithm in new[] { CoSimAlgorithm.GaussSeidel, CoSimAlgorithm.Jacobi })
        {
            var rig = new Rig();
            var lag = rig.Part("lag", rig.ImportFmu(fmus.Lag));
            var gain = rig.Part("gain", rig.ImportFmu(fmus.Gain));
            rig.Connect(lag, "y", gain, "u");
            rig.Connect(gain, "y", lag, "u");
            var plan = CoSimulation.Plan(rig.M, rig.System);
            Assert.Equal(2, plan.Connections.Count);
            Assert.DoesNotContain(plan.AllParameters, p => p.IsInput);   // both inputs are driven

            var r = rig.Run(new CoSimOptions { Stop = 2, Step = 0.001, Algorithm = algorithm, Parameters = { ["lag.x0"] = 1, ["gain.k"] = 0.5 } });
            Succeeded(r);
            var maxErr = r.Time.Select((t, i) => Math.Abs(r.Series["lag.y"][i] - Math.Exp(-0.5 * t))).Max();
            Assert.True(maxErr < 1e-3, $"{algorithm}: max error {maxErr}");
            // Gauss-Seidel: the gain steps after the lag and sees its new output; Jacobi: both step from the previous snapshot.
            var seen = algorithm == CoSimAlgorithm.GaussSeidel ? r.Series["lag.y"][^1] : r.Series["lag.y"][^2];
            Assert.Equal(0.5 * seen, r.Series["gain.y"][^1], 9);
        }
    }

    [SkippableFact]
    public void Fmu_Drives_A_Native_Parametric_Block()
    {
        var fmus = Fmus();
        var rig = new Rig();
        var lag = rig.Part("lag", rig.ImportFmu(fmus.Lag));
        var scale = rig.M.Create(rig.M.Root, ElementKind.Block, "Scale");
        var u = rig.M.Create(scale, ElementKind.Port, "u");
        rig.M.Set(u, FieldKeys.Direction, "in");
        rig.M.Set(u, ExtFieldKeys.Unit, "m");
        var y = rig.M.Create(scale, ElementKind.Port, "y");
        rig.M.Set(y, FieldKeys.Direction, "out");
        rig.M.Set(y, FieldKeys.DefaultValue, "k * u + time");
        var k = rig.M.Create(scale, ElementKind.Property, "k");
        rig.M.Set(k, FieldKeys.DefaultValue, "3");
        var s = rig.Part("scale", scale);
        rig.Connect(lag, "y", s, "u");

        var plan = CoSimulation.Plan(rig.M, rig.System);
        Assert.Contains(plan.AllParameters, p => p.Key == "scale.k" && p.Default == 3);
        var r = rig.Run(new CoSimOptions { Stop = 1, Step = 0.1, Parameters = { ["lag.u"] = 1, ["scale.k"] = 4 } });
        Succeeded(r);
        for (var i = 0; i < r.Time.Count; i++)
            Assert.Equal(4 * (1 - Math.Exp(-r.Time[i])) + r.Time[i], r.Series["scale.y"][i], 9);

        var csv = r.ToCsv().Split('\n');
        Assert.Equal("time,lag.y [m],scale.y", csv[0]);
        Assert.StartsWith("0,0,0", csv[1]);
        var check = r.Check("scale.y", "<=", 3, "always");
        Assert.NotNull(check);
        Assert.False(check!.Pass);
        Assert.True(check.FirstViolation > 0.5);
        Assert.True(r.Check("scale.y", "<=", 4)!.Pass);
    }

    [SkippableFact]
    public void A_Crashing_Fmu_Ends_The_Run_But_Not_The_Test_Host()
    {
        var fmus = Fmus();
        var rig = new Rig();
        var lag = rig.Part("lag", rig.ImportFmu(fmus.Lag));
        var crash = rig.Part("crash", rig.ImportFmu(fmus.Crash));
        rig.Connect(lag, "y", crash, "u");
        var r = rig.Run(new CoSimOptions { Stop = 2, Step = 0.1, Parameters = { ["lag.u"] = 1 } });

        Assert.False(r.Completed);
        Assert.NotNull(r.Error);
        Assert.StartsWith("crash:", r.Error);
        Assert.Contains("Meridian is unaffected", r.Error);
        Assert.Contains("crashed", r.Error);
        Assert.True(r.Time.Count >= 5, "the samples before the crash are kept");
        Assert.Equal(0.5, r.Time[^1], 9);
        Assert.False(Process.GetCurrentProcess().HasExited);

        // The healthy FMU's host was shut down too; the next run works.
        var again = new Rig();
        again.Part("lag", again.ImportFmu(fmus.Lag));
        Succeeded(again.Run(new CoSimOptions { Stop = 0.2, Step = 0.1 }));
    }

    [SkippableFact]
    public void A_Hanging_Fmu_Is_Killed_After_The_Call_Timeout()
    {
        var fmus = Fmus();
        var rig = new Rig();
        rig.Part("hang", rig.ImportFmu(fmus.Hang));
        var sw = Stopwatch.StartNew();
        var r = rig.Run(new CoSimOptions { Stop = 1, Step = 0.05 }, new FmuRuntimeOptions { CallTimeout = TimeSpan.FromSeconds(1.5) });
        Assert.False(r.Completed);
        Assert.Contains("did not respond", r.Error);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
    }

    [SkippableFact]
    public void Cancelling_Stops_The_Run_And_The_Host()
    {
        var fmus = Fmus();
        var rig = new Rig();
        rig.Part("lag", rig.ImportFmu(fmus.Lag));
        using var cts = new CancellationTokenSource();
        var plan = CoSimulation.Plan(rig.M, rig.System);
        var progress = new SyncProgress(f => { if (f > 0.1) cts.Cancel(); });
        var r = CoSimulation.Run(rig.M, plan, new CoSimOptions { Stop = 1000, Step = 0.01 },
            FmuUnit.Factory(b => rig.FmuPaths.GetValueOrDefault(b.Id), null, cts.Token), progress, cts.Token);
        Assert.True(r.Cancelled);
        Assert.False(r.Completed);
        Assert.True(r.Time.Count > 1);
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }

    // ---------------------------------------------------------------- checks that need no compiler

    [Fact]
    public void Unit_Mismatch_Across_A_Connector_Is_An_Error()
    {
        var rig = new Rig();
        var lag = rig.Part("lag", rig.ImportXml(TestFmus.LagDescription("lag", "m")));
        var gain = rig.Part("gain", rig.ImportXml(TestFmus.GainDescription("gain_s", "s")));
        rig.Connect(lag, "y", gain, "u");
        var plan = CoSimulation.Plan(rig.M, rig.System);
        Assert.True(plan.HasErrors);
        Assert.Contains(plan.Problems, p => p.Severity == CoSimSeverity.Error && p.Message.Contains("unit mismatch, m → s"));

        var r = CoSimulation.Run(rig.M, plan, new CoSimOptions(), (_, _) => throw new InvalidOperationException("must not start FMUs"));
        Assert.False(r.Completed);
        Assert.NotNull(r.Error);
    }

    /// <summary>A stand-in unit: constant outputs, records the inputs of every step.</summary>
    private sealed class StubUnit(double[] outputs) : ICoSimUnit
    {
        public List<double[]> Inputs { get; } = new();
        public bool HasState => true;
        public bool TerminateRequested => false;
        public double[] Initialize(double start, double stop, double[] inputs) { Inputs.Add(inputs); return outputs; }
        public double[] Step(double t, double h, double[] inputs) { Inputs.Add(inputs); return outputs; }
        public double[] Evaluate(double t, double[] inputs) => outputs;
        public IReadOnlyList<string> DrainLog() => [];
        public void Dispose() { }
    }

    [Fact]
    public void Compatible_Units_Across_A_Connector_Are_Converted()
    {
        var rig = new Rig();
        var lag = rig.Part("lag", rig.ImportXml(TestFmus.LagDescription("lag", "km")));
        var gain = rig.Part("gain", rig.ImportXml(TestFmus.GainDescription("gain", "m")));
        rig.Connect(lag, "y", gain, "u");
        var plan = CoSimulation.Plan(rig.M, rig.System);
        Assert.False(plan.HasErrors, string.Join("\n", plan.Problems.Select(p => p.Message)));
        Assert.Contains(plan.Problems, p => p.Severity == CoSimSeverity.Info && p.Message.Contains("converted from km to m (× 1000)"));
        var conn = Assert.Single(plan.Connections);
        Assert.Equal(1000, conn.Scale, 9);

        var units = new Dictionary<string, StubUnit> { ["lag"] = new([2.5]), ["gain"] = new([0]) };
        var r = CoSimulation.Run(rig.M, plan, new CoSimOptions { Stop = 0.1, Step = 0.05 }, (part, _) => units[part.Name]);
        Assert.True(r.Completed, r.Error);
        Assert.All(units["gain"].Inputs.Skip(1), i => Assert.Equal(2500, i[0], 9));   // 2.5 km arrive as 2500 m

        // Temperatures convert with an offset.
        Assert.Equal(273.15, Units.Convert(0, "degC", "K")!.Value, 9);
    }

    [Fact]
    public void An_Fmu_That_Cannot_Change_Its_Step_Size_Rejects_A_Shortened_Last_Step()
    {
        var rig = new Rig();
        var xml = TestFmus.LagDescription("lag", "m").Replace("canHandleVariableCommunicationStepSize=\"true\"", "canHandleVariableCommunicationStepSize=\"false\"")
            .Replace("<ModelVariables>", "<DefaultExperiment startTime=\"0\" stopTime=\"4\" stepSize=\"0.2\"/><ModelVariables>");
        var block = rig.ImportXml(xml);
        Assert.Equal("false", block.Get(FieldKeys.TagPrefix + "fmi.variableStepSize"));
        rig.Part("lag", block);
        var plan = CoSimulation.Plan(rig.M, rig.System);
        Assert.Equal(0.2, plan.SuggestedStep);
        Assert.Equal(4, plan.SuggestedStop);
        var r = CoSimulation.Run(rig.M, plan, new CoSimOptions { Stop = 1, Step = 0.3 }, (_, _) => throw new InvalidOperationException("must not start FMUs"));
        Assert.False(r.Completed);
        Assert.Contains("cannot change its communication step size", r.Error);
        Assert.Contains("1.2 s", r.Error);
        // A whole number of steps is fine.
        r = CoSimulation.Run(rig.M, plan, new CoSimOptions { Stop = 1.2, Step = 0.3 }, (_, _) => new StubUnit([0]));
        Assert.True(r.Completed, r.Error);
    }

    [Fact]
    public void Plan_Reports_Wiring_Problems()
    {
        var rig = new Rig();
        var a = rig.Part("a", rig.ImportXml(TestFmus.LagDescription("lag", "m")));
        var b = rig.Part("b", rig.ImportXml(TestFmus.LagDescription("lag", "m")));
        var c = rig.Part("c", rig.ImportXml(TestFmus.GainDescription("gain", "m")));
        rig.Connect(a, "y", b, "y");          // two outputs
        rig.Connect(a, "y", c, "u");
        rig.Connect(b, "y", c, "u");          // second driver
        var plan = CoSimulation.Plan(rig.M, rig.System);
        Assert.Contains(plan.Problems, p => p.Message.Contains("connects two outputs"));
        Assert.Contains(plan.Problems, p => p.Message.Contains("driven by more than one output"));
        Assert.True(CoSimulation.SameUnit("m/s", " m / s "));
        Assert.True(CoSimulation.SameUnit("1", ""));
        Assert.False(CoSimulation.SameUnit("km", "m"));
    }

    [Fact]
    public void Parameters_Come_From_Value_Properties_And_Trade_Alternatives()
    {
        var rig = new Rig();
        rig.Part("lag", rig.ImportXml(TestFmus.LagDescription("lag", "m")));
        var plan = CoSimulation.Plan(rig.M, rig.System);
        Assert.Equal(["lag.T", "lag.x0", "lag.u"], plan.AllParameters.Select(p => p.Key).ToArray());
        Assert.Equal(1, plan.AllParameters.First().Default);

        var alt = rig.M.Create(rig.M.Root, ElementKind.Block, "Slow variant");
        var t = rig.M.Create(alt, ElementKind.Property, "T");
        rig.M.Set(t, FieldKeys.DefaultValue, "10");
        var overrides = CoSimulation.ParametersFrom(rig.M, plan, alt);
        Assert.Equal(10, overrides["lag.T"]);
        Assert.Single(overrides);
    }

    [Fact]
    public void Algebraic_Loop_Between_Native_Blocks_Is_Rejected()
    {
        var rig = new Rig();
        var blk = rig.M.Create(rig.M.Root, ElementKind.Block, "Echo");
        var u = rig.M.Create(blk, ElementKind.Port, "u"); rig.M.Set(u, FieldKeys.Direction, "in");
        var y = rig.M.Create(blk, ElementKind.Port, "y"); rig.M.Set(y, FieldKeys.Direction, "out"); rig.M.Set(y, FieldKeys.DefaultValue, "u");
        var a = rig.Part("a", blk);
        var b = rig.Part("b", blk);
        rig.Connect(a, "y", b, "u");
        rig.Connect(b, "y", a, "u");
        Assert.Contains(CoSimulation.Plan(rig.M, rig.System).Problems, p => p.Message.StartsWith("Algebraic loop"));
    }

    [Fact]
    public void Fmu_Without_A_Binary_For_This_Platform_Fails_Clearly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "meridian-test-fmus", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var fmu = Path.Combine(dir, "nobin.fmu");
        using (var zip = ZipFile.Open(fmu, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("modelDescription.xml").Open())) w.Write(TestFmus.LagDescription("lag", "m"));
            zip.CreateEntry("binaries/someos99/lag.bin").Open().Dispose();
        }
        var ex = Assert.Throws<CoSimException>(() => new FmuUnit(fmu, "x", ["u"], ["y"], new Dictionary<string, double>()));
        Assert.Contains("no binary for this platform", ex.Message);
        Assert.Contains("someos99", ex.Message);
    }

    [Fact]
    public void Fmu_Archive_Rejects_Path_Traversal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "meridian-test-fmus", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var fmu = Path.Combine(dir, "evil.fmu");
        using (var zip = ZipFile.Open(fmu, ZipArchiveMode.Create))
            using (var w = new StreamWriter(zip.CreateEntry("../../evil.txt").Open())) w.Write("x");
        Assert.Throws<InvalidDataException>(() => FmuArchive.Extract(fmu));
    }

    [Fact]
    public void Fmu_Store_Is_Content_Addressed()
    {
        var store = new FmuStore(Path.Combine(Path.GetTempPath(), "meridian-test-fmus", Guid.NewGuid().ToString("N")));
        var hash = store.Save([1, 2, 3]);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, store.Save([1, 2, 3]));
        Assert.NotNull(store.Find(hash));
        Assert.Null(store.Find("../../etc/passwd"));
        Assert.Null(store.Find(new string('0', 64)));
    }
}
