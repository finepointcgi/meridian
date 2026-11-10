using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Scripting;

namespace Meridian.Tests;

/// <summary>The C# script examples in docs-site/docs/scripting.md compile and do what the page says.</summary>
public class DocsExampleTests
{
    private static readonly ScriptEngine Engine = new(new ScriptEngineOptions { Timeout = TimeSpan.FromSeconds(30) });

    [Fact]
    public async Task Unsatisfied_Requirements_Example_Runs()
    {
        var m = CubeSatSample.Create();
        var bridge = new HeadlessBridge();
        var r = await Engine.RunAsync("""
            foreach (var r in Model.OfKind("Requirement").Where(r => r.Incoming("Satisfy").Count == 0))
                Log($"{r.Get(Fields.RequirementId)} {r.Name}");
            """, new ModelApi(m, bridge));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        Assert.Contains("SAT-004 Disposal", bridge.Lines);
    }

    [Fact]
    public async Task Add_Test_Cases_Example_Verifies_Every_Leaf_Requirement()
    {
        var m = AdaptiveCruiseSample.Create();
        var r = await Engine.RunAsync("""
            var tests = Model.FindByName("Verification", "Package") ?? Model.Create(Model.Root, "Package", "Verification");
            using (Model.Batch("Add test cases"))
                foreach (var r in Model.OfKind("Requirement").Where(r => r.Incoming("Verify").Count == 0
                                                                     && !r.Children.Any(c => c.Kind == "Requirement")))
                    Model.Connect("Verify", Model.Create(tests, "TestCase", "Test " + r.Name), r);
            """, new ModelApi(m, new HeadlessBridge()));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        Assert.All(m.OfKind(ElementKind.Requirement), req => Assert.NotEmpty(m.Incoming(req, ElementKind.Verify)));
        Assert.Contains(m.OfKind(ElementKind.TestCase), t => t.Name == "Test Stop and Go");
    }

    [Fact]
    public async Task Hazard_Csv_Example_Downloads_The_Hazard_Log()
    {
        var m = InfusionPumpSample.Create();
        var bridge = new HeadlessBridge();
        var r = await Engine.RunAsync("""
            var sb = new StringBuilder("Id,Hazard,Severity,Controls\n");
            foreach (var h in Model.OfKind("Block").Where(b => b.HasStereotype("Hazard")))
                sb.AppendLine($"{h.GetTag("hazardId")},{h.Name},{h.GetTag("severity")},"
                            + string.Join(" ", h.Incoming("Trace").Select(t => t.Source.Get(Fields.RequirementId))));
            Download("hazards.csv", sb.ToString(), "text/csv");
            """, new ModelApi(m, bridge));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        var (name, _, content) = bridge.Downloads.Single();
        Assert.Equal("hazards.csv", name);
        var csv = System.Text.Encoding.UTF8.GetString(content);
        Assert.Contains("H-04,Programming Error,critical,RC-004 RC-007", csv);
        Assert.Equal(8, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }
}
