using System.Text.Json;
using System.Text.Json.Nodes;
using Meridian.Assistant.Changes;
using Meridian.Assistant.Engine;
using Meridian.Assistant.Tools;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization.Reports;

namespace Meridian.Tests;

/// <summary>AI-driven diagramming (plan_diagrams / create_diagrams), the SDD template and its AI narrative. Never calls the real API.</summary>
public class AutoDiagramAssistantTests
{
    private readonly ModelRepository _m = SampleModels.Vehicle();
    private int _refs;
    private ToolContext Ctx(ChangeSet? set = null) => new(_m, set ?? new ChangeSet(1), () => ++_refs);
    private static ToolResult Run(ToolContext ctx, string tool, object input) => Toolbox.Find(tool)!.Run(ctx, JsonSerializer.SerializeToElement(input));
    private Element Named(string name) => _m.AllElements.First(e => e.Name == name && e is not Diagram);

    [Fact]
    public void Plan_Diagrams_Lists_The_Deterministic_Plan_Read_Only()
    {
        var ctx = Ctx();
        var r = Run(ctx, "plan_diagrams", new { });
        Assert.False(r.IsError, r.Content);
        var json = JsonNode.Parse(r.Content)!;
        var diagrams = json["diagrams"]!.AsArray();
        Assert.Contains(diagrams, d => d!["diagram_type"]!.GetValue<string>() == "Package" && d["status"]!.GetValue<string>() == "new");
        Assert.All(diagrams, d => Assert.False(string.IsNullOrEmpty(d!["purpose"]!.GetValue<string>())));
        Assert.True(ctx.Changes.IsEmpty);
        Assert.True(Run(ctx, "plan_diagrams", new { scope_id = Named("Engine").Id }).IsError);
    }

    [Fact]
    public void Create_Diagrams_Proposes_One_Item_Per_Diagram_Laid_Out_By_The_Engine()
    {
        var ctx = Ctx();
        var vehicle = Named("Vehicle");
        var parts = AutoDiagrammer.Parts(_m, vehicle).Select(p => p.Id).ToArray();
        var r = Run(ctx, "create_diagrams", new
        {
            diagrams = new object[]
            {
                new { diagram_type = "Package", owner_id = _m.Root.Id, name = "Overview", purpose = "How the model is organised." },
                new { diagram_type = "InternalBlock", owner_id = vehicle.Id, name = "Vehicle – wiring", purpose = "How the vehicle's parts connect.", element_ids = parts },
                new { diagram_type = "InternalBlock", owner_id = vehicle.Id, name = "Bad", purpose = "x", element_ids = new[] { Named("Engine").Id } },
                new { diagram_type = "Activity", owner_id = _m.Root.Id, name = "Nope", purpose = "x" },
            },
        });
        Assert.False(r.IsError, r.Content);
        var json = JsonNode.Parse(r.Content)!;
        Assert.Equal(2, json["proposed"]!.AsArray().Count);
        Assert.Equal(2, json["problems"]!.AsArray().Count);
        Assert.Contains("own parts", json["problems"]![0]!.GetValue<string>());
        Assert.Equal(2, ctx.Changes.Items.Count);
        Assert.All(ctx.Changes.Items, p => { Assert.Equal(ProposalKind.CreateDiagram, p.Kind); Assert.False(string.IsNullOrEmpty(p.Purpose)); });

        // Reject the overview; the IBD alone is applied, laid out (ports on parts, no overlaps), as one undo step.
        ctx.Changes.Items[0].Accepted = false;
        var count = _m.Diagrams.Count();
        var result = ctx.Changes.Apply(_m);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(count + 1, _m.Diagrams.Count());
        var d = _m.Diagrams.Single(x => x.Name == "Vehicle – wiring");
        Assert.Equal("How the vehicle's parts connect.", d.Documentation);
        Assert.Equal(parts.Length, d.Nodes.Count(n => n.ParentId == null));
        var top = d.Nodes.Where(n => n.ParentId == null).Select(n => n.Bounds).ToList();
        for (var i = 0; i < top.Count; i++) for (var j = i + 1; j < top.Count; j++) Assert.False(top[i].Intersects(top[j]));
        _m.History.Undo();
        Assert.Equal(count, _m.Diagrams.Count());

        // All invalid: an error result, nothing proposed.
        var bad = Run(Ctx(), "create_diagrams", new { diagrams = new[] { new { diagram_type = "Activity", owner_id = _m.Root.Id, name = "X", purpose = "y" } } });
        Assert.True(bad.IsError);
    }

    [Fact]
    public async Task Diagram_My_Model_Turn_Produces_A_Reviewable_Change_Set()
    {
        var prompt = Prompts.DiagramModel(null);
        Assert.Contains("plan_diagrams", prompt);
        Assert.Contains("create_diagrams", prompt);
        Assert.Contains("diagram_type Package", Prompts.DiagramFromDescription(_m.Root, "Package diagram", "Three layers"));
        Assert.Contains("Package diagram", Prompts.DiagramKinds);

        var structure = Named("Structure");
        var create = JsonSerializer.Serialize(new
        {
            diagrams = new object[]
            {
                new { diagram_type = "Package", owner_id = _m.Root.Id, name = "Model map", purpose = "Where everything lives." },
                new { diagram_type = "BlockDefinition", owner_id = structure.Id, name = "Vehicle – structure", purpose = "The block hierarchy.", exclude_ids = new[] { Named("FuelInterface").Id } },
            },
        });
        var fake = new FakeAnthropicHandler()
            .Stream(new Sse().ToolUse("toolu_1", "plan_diagrams", "{}").End("tool_use"))
            .Stream(new Sse().ToolUse("toolu_2", "create_diagrams", create).End("tool_use"))
            .Stream(new Sse().Text("Proposed two views.").End("end_turn"));
        var conv = new Conversation();
        var context = Prompts.Context(_m, "Vehicle", null, null, readOnly: false);
        await fake.Engine().RunTurnAsync(conv, _m, new TurnRequest(prompt, context, AssistantEffort.High, "Diagram my model"), () => { });

        Assert.Equal(3, fake.Requests.Count);
        var tools = JsonNode.Parse(fake.Requests[0].Body)!["tools"]!.AsArray();
        Assert.Contains(tools, t => t!["name"]!.GetValue<string>() == "plan_diagrams" && t["strict"]!.GetValue<bool>());
        Assert.Contains(tools, t => t!["name"]!.GetValue<string>() == "create_diagrams" && t["strict"]!.GetValue<bool>());
        Assert.Equal("auto", JsonNode.Parse(fake.Requests[0].Body)!["tool_choice"]!["type"]!.GetValue<string>());
        var set = Assert.Single(conv.Items.OfType<ChangeSetItem>()).Changes;
        Assert.Equal(2, set.Items.Count);
        var result = set.Apply(_m);
        Assert.True(result.Ok, result.Message);
        var bdd = _m.Diagrams.Single(d => d.Name == "Vehicle – structure");
        Assert.DoesNotContain(bdd.Nodes, n => n.Element.Name == "FuelInterface");
        Assert.Contains(bdd.Nodes, n => n.Element.Name == "Engine");
        Assert.StartsWith("Assistant:", _m.History.UndoDescription);
    }

    [Fact]
    public void Sdd_Contains_The_Generated_Diagrams_As_Captioned_Figures()
    {
        var m = CubeSatSample.Create();
        var result = AutoDiagrammer.Apply(m, AutoDiagrammer.Plan(m));
        var doc = DocResolver.Resolve(m, ReportTemplates.Sdd(), new ResolveOptions { RenderDiagrams = false });
        var figures = doc.Figures.Select(f => f.Display).ToList();
        Assert.All(figures, f => Assert.StartsWith("Figure ", f));
        foreach (var d in result.Created) Assert.Contains(figures, f => f.Contains(d.Name));
        Assert.Contains(figures, f => f.EndsWith(": Package overview"));
        Assert.Contains(figures, f => f.Contains("EPS – internal structure: EPS – internal structure"));
        Assert.Contains(figures, f => f.Contains("– behavior:"));
        foreach (var title in new[] { "Package overview", "Structure", "Design elements", "Interfaces", "Behavior", "Requirements allocation" })
            Assert.Contains(doc.Headings, h => h.Text.Contains(title));
        // The auto-diagrammer's purpose line is printed with its figure.
        var html = System.Text.Encoding.UTF8.GetString(ReportEngine.Generate(m, ReportTemplates.Sdd(), ReportFormat.Html));
        Assert.Contains("How the model is organised into packages", html);
    }

    [Fact]
    public async Task Ai_Narrative_Is_Drafted_With_A_Strict_Tool_And_Inserted_Only_When_Kept()
    {
        var template = ReportTemplates.Sdd();
        var titles = template.Sections.Select(s => s.Title).ToList();
        var input = JsonSerializer.Serialize(new
        {
            sections = titles.Select(t => new { title = t, overview = $"Overview of {t} {{bad}}.", rationale = t == "Interfaces" ? "" : $"Why {t}." }),
        });
        var fake = new FakeAnthropicHandler()
            .Stream(new Sse().ToolUse("toolu_n", NarrativeWriter.ToolName, input).End("tool_use"))
            .Stream(new Sse().Text("No.").End("refusal", new { type = "refusal", category = "other", explanation = "Not today." }));
        var writer = new NarrativeWriter(new AssistantClientFactory(new HttpClient(fake), maxRetries: 0).Create("sk-ant-test-key-not-real-000000"));
        var r = await writer.DraftAsync(_m, "SDD-001 Vehicle", titles);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(titles.Count, r.Sections.Count);
        Assert.DoesNotContain(r.Sections, s => s.Overview.Contains('{'));

        var body = JsonNode.Parse(fake.Requests[0].Body)!;
        Assert.Equal("claude-opus-5-5", body["model"]!.GetValue<string>());
        Assert.Equal("auto", body["tool_choice"]!["type"]!.GetValue<string>());
        Assert.True(body["tools"]![0]!["strict"]!.GetValue<bool>());
        Assert.Contains("<model>", body["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Contains("Engine", body["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>());

        // The user unticks one section and edits another; only reviewed text goes in, at the top of each section.
        r.Sections[0].Include = false;
        r.Sections[1].Overview = "Edited overview.";
        NarrativeWriter.ApplyTo(template, r.Sections);
        Assert.Empty(template.Sections[0].Blocks);   // "Scope" has only subsections: nothing was added
        Assert.Equal("Edited overview.", ((TextBlock)template.Sections[1].Blocks[0]).Markdown);
        Assert.StartsWith("*Rationale.*", ((TextBlock)template.Sections[1].Blocks[1]).Markdown);

        var refused = await writer.DraftAsync(_m, "SDD", titles);
        Assert.False(refused.Ok);
        Assert.Contains("declined", refused.Error);
    }
}
