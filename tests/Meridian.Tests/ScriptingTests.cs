using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Core.Validation;
using Meridian.Scripting;
using Meridian.Serialization;

namespace Meridian.Tests;

public class ModelApiTests
{
    [Fact]
    public void Queries_By_Kind_Name_Stereotype_And_Owner()
    {
        var m = SampleModels.Vehicle();
        var api = new ModelApi(m);
        var blocks = api.OfKind("block");
        Assert.Equal(m.OfKind(ElementKind.Block).Count(), blocks.Count);
        var vehicle = api.FindByName("Vehicle", "Block");
        Assert.NotNull(vehicle);
        Assert.Equal("Block", vehicle!.Kind);
        Assert.Same(m.Find(vehicle.Id), m.OfKind(ElementKind.Block).First(b => b.Name == "Vehicle"));
        Assert.Single(api.Query(kind: "Block", name: "vehicle"));

        var pkg = vehicle.Owner!;
        Assert.All(api.Query(owner: pkg, recursive: false), e => Assert.Equal(pkg, e.Owner));
        Assert.True(api.Query(owner: pkg).Count >= api.Query(owner: pkg, recursive: false).Count);

        vehicle.AddStereotype("system");
        Assert.Contains(vehicle, api.Query(stereotype: "System"));
        Assert.Throws<ArgumentException>(() => api.OfKind("NoSuchKind"));
    }

    [Fact]
    public void Reads_Fields_Tags_And_Relationships()
    {
        var m = SampleModels.Vehicle();
        var api = new ModelApi(m);
        var req = api.OfKind("Requirement").First(r => r.Incoming("Satisfy").Count > 0);
        Assert.False(string.IsNullOrEmpty(req.Get(Fields.RequirementId)));
        var sat = req.Incoming("Satisfy")[0];
        Assert.Equal(req, sat.Target);
        Assert.Contains(sat, sat.Source.Outgoing("Satisfy"));
        Assert.Contains(sat, req.Relationships);

        req.SetTag("priority", "high");
        Assert.Equal("high", req.GetTag("priority"));
        Assert.Equal("high", req.TaggedValues["priority"]);
        Assert.Equal("high", req.Fields["tag:priority"]);
    }

    [Fact]
    public void Edits_Are_Undoable_And_Batches_Are_One_Step()
    {
        var m = new ModelRepository("M");
        var api = new ModelApi(m);
        using (api.Batch("Build"))
        {
            var pkg = api.Create(api.Root, "Package", "P");
            var b = api.Create(pkg, "Block", "Engine");
            var r = api.Create(pkg, "Requirement", "Power");
            var t = api.Create(pkg, "TestCase", "T1");
            api.Connect("Satisfy", b, r);
            api.Connect("Verify", t, r);
            b.Name = "Motor";
            b.Documentation = "Makes it go";
            var d = api.CreateDiagram(pkg, "BlockDefinition", "BDD");
            d.Show(b);
            Assert.True(d.Shows(b));
        }
        Assert.Equal("Build", m.History.UndoDescription);
        Assert.Equal(2, m.Relationships.Count());
        Assert.Equal("Motor", m.OfKind(ElementKind.Block).Single().Name);
        m.History.Undo();
        Assert.Empty(m.Root.OwnedElements);
        Assert.True(api.EditCount >= 7);
    }

    [Fact]
    public void Delete_Move_And_ReadOnly()
    {
        var m = new ModelRepository("M");
        var api = new ModelApi(m);
        var a = api.Create(api.Root, "Package", "A");
        var b = api.Create(api.Root, "Package", "B");
        var blk = api.Create(a, "Block", "X");
        api.Move(blk, b);
        Assert.Equal(b, blk.Owner);
        blk.Delete();
        Assert.Null(api.Get(blk.Id));

        var ro = new ModelApi(m, readOnly: true);
        Assert.Throws<InvalidOperationException>(() => ro.Create(ro.Root, "Package", "C"));
    }

    [Fact]
    public void Workbench_Calls_Go_Through_The_Bridge()
    {
        var m = SampleModels.Vehicle();
        var bridge = new HeadlessBridge();
        var api = new ModelApi(m, bridge);
        var d = api.Diagrams[0];
        api.OpenDiagram(d);
        Assert.Equal(d, api.ActiveDiagram);
        api.Select(d.Elements.FirstOrDefault());
        api.Log("hi");
        api.Status("ok");
        api.Download("a.csv", "x,y");
        Assert.Contains("hi", bridge.Lines);
        Assert.Equal("ok", bridge.LastStatus);
        Assert.Equal("a.csv", bridge.Downloads.Single().FileName);
    }
}

public class ScriptEngineTests
{
    private static readonly ScriptEngine Engine = new(new ScriptEngineOptions { Timeout = TimeSpan.FromSeconds(20) });

    [Fact]
    public async Task Runs_A_Script_With_Globals_As_One_Undo_Step()
    {
        var m = SampleModels.Vehicle();
        var bridge = new HeadlessBridge();
        var api = new ModelApi(m, bridge);
        var r = await Engine.RunAsync("""
            var n = Model.OfKind("Block").Count;
            Log($"{n} blocks");
            var p = Model.Create(Model.Root, "Package", "FromScript");
            Model.Create(p, "Block", "A");
            return n;
            """, api, "demo");
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        Assert.Equal(m.OfKind(ElementKind.Block).Count() - 1, r.ReturnValue);
        Assert.Contains(bridge.Lines, l => l.EndsWith(" blocks"));
        Assert.Equal("Script: demo", m.History.UndoDescription);
        m.History.Undo();
        Assert.DoesNotContain(m.AllElements, e => e.Name == "FromScript");
    }

    [Fact]
    public async Task Compile_Errors_Have_Line_Numbers()
    {
        var r = await Engine.RunAsync("var a = 1;\nvar b = a +;\n", new ModelApi(new ModelRepository()));
        Assert.Equal(ScriptOutcome.CompileFailed, r.Outcome);
        Assert.Contains(r.Diagnostics, d => d.Line == 2 && d.Severity == "error");
    }

    [Fact]
    public async Task Runtime_Errors_Have_Line_Numbers_And_Roll_Back()
    {
        var m = new ModelRepository("M");
        var r = await Engine.RunAsync("Model.Create(Model.Root, \"Package\", \"P\");\n\nthrow new InvalidOperationException(\"boom\");", new ModelApi(m), "bad");
        Assert.Equal(ScriptOutcome.Failed, r.Outcome);
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal(3, d.Line);
        Assert.Contains("boom", d.Message);
        Assert.True(r.RolledBack);
        Assert.Empty(m.Root.OwnedElements);
        Assert.False(m.History.CanUndo);
    }

    [Theory]
    [InlineData("System.IO.File.ReadAllText(\"/etc/passwd\");")]
    [InlineData("using System.IO;\nFile.Exists(\"x\");")]
    [InlineData("System.Diagnostics.Process.Start(\"ls\");")]
    [InlineData("var t = typeof(string).Assembly;")]
    [InlineData("System.Environment.Exit(1);")]
    [InlineData("#r \"System.Net.Http\"\nvar x = 1;")]
    [InlineData("a: goto a;")]
    [InlineData("var c = new System.Net.Http.HttpClient();")]
    public async Task Policy_Blocks_Dangerous_Apis(string code)
    {
        var r = await Engine.RunAsync(code, new ModelApi(new ModelRepository()));
        Assert.Equal(ScriptOutcome.CompileFailed, r.Outcome);
    }

    [Fact]
    public async Task Allowed_Bcl_Apis_Work()
    {
        var r = await Engine.RunAsync("""
            var sw = new System.IO.StringWriter();
            sw.Write(string.Join(",", new List<int> { 1, 2 }.Select(i => i * 2)));
            var sb = new StringBuilder(sw.ToString());
            return Regex.Replace(sb.ToString(), "4", "four");
            """, new ModelApi(new ModelRepository()));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        Assert.Equal("2,four", r.ReturnValue);
    }

    [Fact]
    public async Task Infinite_Loops_Time_Out()
    {
        var engine = new ScriptEngine(new ScriptEngineOptions { Timeout = TimeSpan.FromMilliseconds(500) });
        var r = await engine.RunAsync("var i = 0;\nwhile (true) i++;", new ModelApi(new ModelRepository()));
        Assert.Equal(ScriptOutcome.TimedOut, r.Outcome);
        Assert.False(r.Abandoned);
    }

    [Fact]
    public async Task Cancellation_Stops_The_Script()
    {
        using var cts = new CancellationTokenSource(300);
        var r = await Engine.RunAsync("for (;;) { }", new ModelApi(new ModelRepository()), cancellationToken: cts.Token);
        Assert.Equal(ScriptOutcome.Cancelled, r.Outcome);
    }

    [Fact]
    public async Task Example_Scripts_Compile_And_Run()
    {
        foreach (var example in ExampleScripts.All)
        {
            var m = SampleModels.Vehicle();
            var bridge = new HeadlessBridge();
            var r = await Engine.RunAsync(example.Code, new ModelApi(m, bridge), example.Name);
            Assert.True(r.Success, example.Name + ": " + string.Join("\n", r.Diagnostics));
        }
    }

    [Fact]
    public async Task Example_Verify_Script_Verifies_Every_Leaf_Requirement()
    {
        var m = SampleModels.Vehicle();
        var code = ExampleScripts.All.Single(s => s.Id == "example:verify").Code;
        var r = await Engine.RunAsync(code, new ModelApi(m), "verify");
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        Assert.DoesNotContain(new ModelValidator().Validate(m), i => i.RuleId == "REQ-004");
    }

    [Fact]
    public async Task Example_Pascal_And_Csv_Scripts()
    {
        var m = new ModelRepository("M");
        var api = new ModelApi(m);
        api.Create(api.Root, "Block", "brake system");
        api.Create(api.Root, "Block", "fuel_tank");
        api.Create(api.Root, "Block", "AlreadyFine");
        var r = await Engine.RunAsync(ExampleScripts.All.Single(s => s.Id == "example:pascal").Code, api, "pascal");
        Assert.Equal(2, r.ReturnValue);
        Assert.Equal(["AlreadyFine", "BrakeSystem", "FuelTank"], m.OfKind(ElementKind.Block).Select(b => b.Name).Order().ToArray());

        var vehicle = SampleModels.Vehicle();
        var bridge = new HeadlessBridge();
        r = await Engine.RunAsync(ExampleScripts.All.Single(s => s.Id == "example:csv").Code, new ModelApi(vehicle, bridge), "csv");
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        var csv = System.Text.Encoding.UTF8.GetString(bridge.Downloads.Single().Content);
        Assert.StartsWith("Id,Name,Text", csv);
        Assert.Equal(vehicle.OfKind(ElementKind.Requirement).Count() + 1, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Saved_Scripts_Persist_In_The_Project_File()
    {
        var m = new ModelRepository("M");
        var id = ScriptLibrary.Save(m, "Hello", "Log(1);");
        Assert.Equal(id, ScriptLibrary.Save(m, "Hello", "Log(2);"));
        var loaded = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        var s = Assert.Single(ScriptLibrary.List(loaded));
        Assert.Equal(("Hello", "Log(2);"), (s.Name, s.Code));
        ScriptLibrary.Delete(loaded, s.Id);
        Assert.Empty(ScriptLibrary.List(loaded));
    }
}

public class PluginTests
{
    private static string HelloPluginPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Meridian.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        var path = Path.Combine(dir!.FullName, "samples", "HelloPlugin", "bin", config, "net10.0", "HelloPlugin.dll");
        Assert.True(File.Exists(path), $"Sample plugin not built: {path}");
        return path;
    }

    [Fact]
    public void Loads_The_Sample_Plugin_From_A_Plugins_Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "meridian-plugins-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(folder, "HelloPlugin");
        Directory.CreateDirectory(target);
        try
        {
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(HelloPluginPath())!))
                File.Copy(f, Path.Combine(target, Path.GetFileName(f)));
            File.WriteAllText(Path.Combine(folder, "NotAPlugin.dll"), "garbage");

            var pm = new PluginManager(localMode: true, registerValidationGlobally: false);
            pm.LoadFrom(folder);

            var info = Assert.Single(pm.Plugins, p => p.Loaded);
            Assert.Equal(("Hello Plugin", "1.0.0"), (info.Name, info.Version));
            Assert.Contains(pm.Plugins, p => !p.Loaded && p.Error != null);          // the garbage dll is reported, not thrown
            Assert.Contains(pm.Messages, x => x.Contains("ERROR") && x.Contains("NotAPlugin"));
            Assert.Contains(pm.Messages, x => x.Contains("Hello from Hello Plugin"));

            var m = SampleModels.Vehicle();
            var bridge = new HeadlessBridge();
            var api = new ModelApi(m, bridge);
            pm.Commands.Single(c => c.Title == "Hello: say hello").Run(api);
            Assert.Contains(bridge.Lines, l => l.StartsWith("Hello, Vehicle System"));

            var issues = pm.CoreRules.Single().Check(m).ToList();
            Assert.NotEmpty(issues);
            Assert.All(issues, i => Assert.Equal("HELLO-001", i.RuleId));

            var md = System.Text.Encoding.UTF8.GetString(pm.Exporters.Single().Export(new ModelApi(m, readOnly: true)));
            Assert.StartsWith("# Vehicle System", md);

            var importer = pm.Importers.Single();
            importer.Import(api, "Alpha\nBeta\n\n"u8.ToArray(), "parts.txt");
            Assert.Equal(2, m.AllElements.Single(e => e.Name == "parts").OwnedElements.Count);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { /* the plugin dll stays loaded */ }
        }
    }

    [Fact]
    public void Missing_Folder_And_Failing_Plugins_Are_Logged_Not_Thrown()
    {
        var pm = new PluginManager(registerValidationGlobally: false);
        pm.LoadFrom(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid()));
        Assert.Contains(pm.Messages, x => x.Contains("no plugins folder"));

        var info = pm.Add(new BrokenPlugin());
        Assert.False(info.Loaded);
        Assert.Empty(pm.Commands);   // partial registrations are discarded
        Assert.Contains(pm.Messages, x => x.Contains("Initialize failed"));
    }

    [Fact]
    public void Global_Rules_Join_Every_Validator_And_Are_Isolated()
    {
        var pm = new PluginManager();
        pm.Add(new RulePlugin());
        try
        {
            var m = new ModelRepository("GlobalTest");
            m.Create(m.Root, ElementKind.Block, "B");
            var issues = new ModelValidator().Validate(m);
            Assert.Contains(issues, i => i.RuleId == "TEST-GLOBAL-1" && i.Element.Name == "B");
            Assert.Contains(issues, i => i.RuleId == "TEST-GLOBAL-2" && i.Message.Contains("failed"));
        }
        finally
        {
            pm.UnregisterValidationRules();
        }
        Assert.DoesNotContain(ExternalValidationRules.Snapshot(), r => r.Id.StartsWith("TEST-GLOBAL"));
    }

    private sealed class BrokenPlugin : IMeridianPlugin
    {
        public string Name => "Broken";
        public string Version => "0";
        public void Initialize(IPluginHost host)
        {
            host.RegisterCommand("Half", _ => { });
            throw new InvalidOperationException("nope");
        }
    }

    private sealed class RulePlugin : IMeridianPlugin
    {
        public string Name => "Rules";
        public string Version => "1";
        public void Initialize(IPluginHost host)
        {
            host.RegisterValidationRule("TEST-GLOBAL-1", "Blocks named B", m => m.ModelName != "GlobalTest" ? [] : m.Query(kind: "Block", name: "B").Select(b => new PluginIssue(IssueSeverity.Warning, "B!", b)));
            host.RegisterValidationRule("TEST-GLOBAL-2", "Throws", m => m.ModelName == "GlobalTest" ? throw new Exception("bad rule") : []);
        }
    }
}
