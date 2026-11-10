using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meridian.Assistant.Changes;
using Meridian.Assistant.Engine;
using Meridian.Assistant.Markdown;
using Meridian.Assistant.Tools;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;

namespace Meridian.Tests;

public class AssistantToolTests
{
    private readonly ModelRepository _m = SampleModels.Vehicle();
    private int _refs;
    private ToolContext Ctx(ChangeSet? set = null) => new(_m, set ?? new ChangeSet(1), () => ++_refs);

    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);
    private static JsonElement Parse(string s) => JsonDocument.Parse(s).RootElement;
    private static ToolResult Run(ToolContext ctx, string tool, object input) => Toolbox.Find(tool)!.Run(ctx, J(input));

    private Element Named(string name) => _m.AllElements.First(e => e.Name == name);

    // ------------------------------------------------------------------ schemas

    [Fact]
    public void Every_Tool_Schema_Is_Strict_And_Unique()
    {
        Assert.Equal(Toolbox.All.Count, Toolbox.All.Select(t => t.Name).Distinct().Count());
        foreach (var t in Toolbox.All)
        {
            Assert.Equal("object", t.Schema["type"]!.GetValue<string>());
            Assert.False(t.Schema["additionalProperties"]!.GetValue<bool>());
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
            // Strict tools: every nested object closes additionalProperties too.
            foreach (var o in Objects(t.Schema)) Assert.False(o["additionalProperties"]!.GetValue<bool>());
        }
        static IEnumerable<JsonObject> Objects(JsonNode? n) => n switch
        {
            JsonObject o => (o["type"]?.GetValue<string>() == "object" ? [o] : Array.Empty<JsonObject>()).Concat(o.SelectMany(kv => Objects(kv.Value))),
            JsonArray a => a.SelectMany(Objects),
            _ => [],
        };
    }

    [Fact]
    public void Schema_Validation_Rejects_Bad_Input()
    {
        var schema = new CreateRelationshipTool().Schema;
        Assert.Empty(ToolSchema.Validate(schema, J(new { kind = "Satisfy", source_id = "a", target_id = "b", confidence = 0.5 })));
        var errors = ToolSchema.Validate(schema, J(new { kind = "Satisfies", source_id = 3, extra = true, confidence = "high" }));
        Assert.Contains(errors, e => e.Contains("target_id is required"));
        Assert.Contains(errors, e => e.Contains("kind must be one of"));
        Assert.Contains(errors, e => e.Contains("source_id must be a string"));
        Assert.Contains(errors, e => e.Contains("extra is not a known property"));
        Assert.Contains(errors, e => e.Contains("confidence must be a number"));
        Assert.NotEmpty(ToolSchema.Validate(schema, Parse("[1,2]")));

        // A tool given invalid input returns an is_error result and runs nothing.
        var ctx = Ctx();
        var r = Run(ctx, "create_element", new { kind = "Block", name = "X", fields = new[] { new { key = "text" } } });
        Assert.True(r.IsError);
        Assert.Contains("owner_id is required", r.Content);
        Assert.Contains("value is required", r.Content);
        Assert.True(ctx.Changes.IsEmpty);
    }

    // ------------------------------------------------------------------ read tools

    [Fact]
    public void Search_Uses_Model_Query_Syntax()
    {
        var r = Run(Ctx(), "search_elements", new { query = "kind:Requirement where text contains shall" });
        Assert.False(r.IsError);
        var json = Parse(r.Content);
        Assert.Equal(5, json.GetProperty("total").GetInt32());
        Assert.Contains(json.GetProperty("results").EnumerateArray(), x => x.GetProperty("reqId").GetString() == "REQ-004");

        var blocks = Parse(Run(Ctx(), "search_elements", new { query = "kind:Block", text = "eng", limit = 5 }).Content);
        Assert.Equal("Engine", blocks.GetProperty("results")[0].GetProperty("name").GetString());

        var bad = Run(Ctx(), "search_elements", new { query = "kind:Block where (" });
        Assert.True(bad.IsError);
        Assert.Contains("does not parse", bad.Content);
    }

    [Fact]
    public void Get_Element_Returns_Fields_Owner_And_Relationships()
    {
        var engine = Named("Engine");
        var json = Parse(Run(Ctx(), "get_element", new { id = engine.Id }).Content);
        Assert.Equal("Structure", json.GetProperty("owner").GetProperty("name").GetString());
        Assert.Contains(json.GetProperty("outgoing").EnumerateArray(), r => r.GetProperty("kind").GetString() == "Satisfy");
        Assert.Equal("Engine", json.GetProperty("fields").GetProperty("name").GetString());

        var missing = Run(Ctx(), "get_element", new { id = "_nope" });
        Assert.True(missing.IsError);
        Assert.Contains("search_elements", missing.Content);
    }

    [Fact]
    public void Traceability_Validation_Diagrams_And_Sysml_Text()
    {
        var braking = _m.OfKind(ElementKind.Requirement).Single(r => r.Name == "Braking");
        var trace = Parse(Run(Ctx(), "get_traceability", new { requirement_id = braking.Id }).Content);
        Assert.Equal("BrakeSystem", trace.GetProperty("satisfiedBy")[0].GetProperty("element").GetProperty("name").GetString());
        Assert.True(trace.GetProperty("satisfied").GetBoolean());
        Assert.True(Run(Ctx(), "get_traceability", new { requirement_id = Named("Engine").Id }).IsError);

        var validation = Parse(Run(Ctx(), "validate_model", new { }).Content);
        Assert.True(validation.TryGetProperty("issues", out _));

        var diagrams = Parse(Run(Ctx(), "list_diagrams", new { }).Content);
        Assert.Equal(_m.Diagrams.Count(), diagrams.GetArrayLength());

        var structure = Named("Structure");
        var text = Run(Ctx(), "get_sysml_text", new { package_id = structure.Id });
        Assert.False(text.IsError);
        Assert.Contains("part def Engine", text.Content);
        Assert.Contains("@id", text.Content);
        Assert.True(Run(Ctx(), "get_sysml_text", new { package_id = Named("Engine").Id }).IsError);
    }

    [Fact]
    public void Report_Findings_Records_A_Checklist()
    {
        var ctx = Ctx();
        var r = Run(ctx, "report_findings", new
        {
            findings = new object[]
            {
                new { severity = "warning", category = "unverified_requirement", message = "REQ-004 has no test.", element_id = _m.OfKind(ElementKind.Requirement).First().Id },
                new { severity = "info", category = "other", message = "General note." },
            },
        });
        Assert.False(r.IsError);
        Assert.Equal(2, ctx.Findings.Count);
        Assert.NotNull(ctx.Findings[0].ElementId);
    }

    // ------------------------------------------------------------------ write tools propose, never apply

    [Fact]
    public void Write_Tools_Only_Propose_And_Check_The_Metamodel()
    {
        var before = _m.AllElements.Count();
        var undo = _m.History.UndoDescription;
        var ctx = Ctx();
        var reqPkg = Named("Requirements");

        var pkg = Parse(Run(ctx, "create_element", new { kind = "Package", owner_id = reqPkg.Id, name = "Thermal" }).Content);
        var pkgRef = pkg.GetProperty("reference").GetString()!;
        Assert.StartsWith("new:", pkgRef);

        var req = Run(ctx, "create_element", new
        {
            kind = "Requirement", owner_id = pkgRef, name = "Cabin temperature",
            fields = new[]
            {
                new { key = "reqId", value = "REQ-100" }, new { key = "text", value = "The HVAC shall hold 21 °C ± 1 °C." },
                new { key = "rationale", value = "Comfort" }, new { key = "verifyMethod", value = "test" },
            },
        });
        Assert.False(req.IsError, req.Content);
        var reqRef = Parse(req.Content).GetProperty("reference").GetString()!;

        Assert.False(Run(ctx, "create_relationship", new { kind = "Satisfy", source_id = Named("Engine").Id, target_id = reqRef, confidence = 0.7, reason = "Engine heat" }).IsError);

        // Metamodel and data checks come back as tool errors.
        Assert.Contains("cannot own", Run(ctx, "create_element", new { kind = "Block", owner_id = Named("Acceleration").Id, name = "X" }).Content);
        Assert.Contains("already used", Run(ctx, "create_element", new { kind = "Requirement", owner_id = reqPkg.Id, name = "Dup", fields = new[] { new { key = "reqId", value = "REQ-001" } } }).Content);
        Assert.Contains("verifyMethod must be one of", Run(ctx, "create_element", new { kind = "Requirement", owner_id = reqPkg.Id, name = "V", fields = new[] { new { key = "verifyMethod", value = "vibes" } } }).Content);
        Assert.True(Run(ctx, "create_relationship", new { kind = "Satisfy", source_id = Named("Engine").Id, target_id = "new:999" }).IsError);
        Assert.Contains("already exists", Run(ctx, "create_relationship", new { kind = "Satisfy", source_id = Named("Engine").Id, target_id = _m.OfKind(ElementKind.Requirement).Single(r => r.Name == "Engine Power").Id }).Content);
        Assert.True(Run(ctx, "create_relationship", new { kind = "Satisfy", source_id = Named("Engine").Id, target_id = Named("FuelTank").Id, confidence = 3 }).IsError);
        Assert.True(Run(ctx, "delete_element", new { element_id = _m.Root.Id }).IsError);
        Assert.True(Run(ctx, "update_fields", new { element_id = Named("Engine").Id, fields = new[] { new { key = "name", value = "Engine" } } }).IsError);

        var upd = Run(ctx, "update_fields", new { element_id = Named("Engine").Id, fields = new[] { new { key = "documentation", value = "Internal combustion" } } });
        Assert.False(upd.IsError);
        Assert.False(Run(ctx, "delete_element", new { element_id = Named("FuelInterface").Id, reason = "unused" }).IsError);

        Assert.Equal(5, ctx.Changes.Items.Count);
        Assert.Equal(3, ctx.Changes.Added);
        Assert.Equal(1, ctx.Changes.Changed);
        Assert.Equal(1, ctx.Changes.Removed);
        var edit = ctx.Changes.Items.Single(p => p.Kind == ProposalKind.UpdateFields).FieldChanges.Single();
        Assert.Null(edit.Old);
        Assert.Equal("Internal combustion", edit.New);

        // Nothing touched the model.
        Assert.Equal(before, _m.AllElements.Count());
        Assert.Equal(undo, _m.History.UndoDescription);
        Assert.Equal("", Named("Engine").Documentation);
    }

    // ------------------------------------------------------------------ change sets: apply, undo, replication

    [Fact]
    public void Apply_Is_One_Undoable_Replicated_Step()
    {
        var replica = ProjectFile.Deserialize(ProjectFile.Serialize(_m));
        _m.OpEmitted += replica.ApplyRemote;

        var ctx = Ctx();
        var structure = Named("Structure");
        var cool = Parse(Run(ctx, "create_element", new { kind = "Block", owner_id = structure.Id, name = "CoolingSystem" }).Content).GetProperty("reference").GetString();
        var radiator = Parse(Run(ctx, "create_element", new { kind = "Block", owner_id = structure.Id, name = "Radiator" }).Content).GetProperty("reference").GetString();
        Run(ctx, "create_relationship", new { kind = "Composition", source_id = cool, target_id = radiator });
        Run(ctx, "update_fields", new { element_id = Named("Engine").Id, fields = new[] { new { key = "documentation", value = "ICE" } } });
        Run(ctx, "delete_element", new { element_id = Named("FuelInterface").Id });
        var d = Run(ctx, "create_diagram_with_elements", new { diagram_type = "BlockDefinition", owner_id = structure.Id, name = "Cooling", element_ids = new[] { cool, radiator, Named("Engine").Id } });
        Assert.False(d.IsError, d.Content);

        var count = _m.AllElements.Count();
        var result = ctx.Changes.Apply(_m);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(ChangeSetState.Applied, ctx.Changes.State);
        Assert.Contains(_m.OfKind(ElementKind.Block), b => b.Name == "CoolingSystem");
        Assert.DoesNotContain(_m.AllElements, e => e.Name == "FuelInterface");
        Assert.Equal("ICE", Named("Engine").Documentation);
        var diagram = _m.Diagrams.Single(x => x.Name == "Cooling");
        Assert.Equal(3, diagram.Nodes.Count());
        Assert.Single(diagram.Edges);   // the composition is drawn
        Assert.Equal(3, diagram.Nodes.Select(n => (n.Bounds.X, n.Bounds.Y)).Distinct().Count());   // laid out, no overlap
        Assert.Equal(ctx.Changes.CreatedIds[cool!], _m.OfKind(ElementKind.Block).Single(b => b.Name == "CoolingSystem").Id);

        // Collaborators received every operation.
        Assert.Equal(_m.AllElements.Count(), replica.AllElements.Count());
        Assert.Contains(replica.OfKind(ElementKind.Block), b => b.Name == "Radiator");
        Assert.Equal("ICE", replica.Find(Named("Engine").Id)!.Documentation);

        // One undo step reverts everything (and replicates the undo).
        Assert.StartsWith("Assistant:", _m.History.UndoDescription);
        _m.History.Undo();
        Assert.DoesNotContain(_m.OfKind(ElementKind.Block), b => b.Name == "CoolingSystem");
        Assert.Contains(_m.AllElements, e => e.Name == "FuelInterface");
        Assert.Equal("", Named("Engine").Documentation);
        Assert.DoesNotContain(replica.OfKind(ElementKind.Block), b => b.Name == "CoolingSystem");
        _m.History.Redo();
        Assert.Contains(_m.OfKind(ElementKind.Block), b => b.Name == "CoolingSystem");
        Assert.NotEqual(count, _m.AllElements.Count());

        // Applying twice is refused.
        Assert.False(ctx.Changes.Apply(_m).Ok);
    }

    [Fact]
    public void Rejected_Changes_Skip_Their_Dependents()
    {
        var ctx = Ctx();
        var structure = Named("Structure");
        var a = Parse(Run(ctx, "create_element", new { kind = "Block", owner_id = structure.Id, name = "A" }).Content).GetProperty("reference").GetString();
        Run(ctx, "create_element", new { kind = "Property", owner_id = a, name = "mass" });
        Run(ctx, "create_element", new { kind = "Block", owner_id = structure.Id, name = "B" });
        ctx.Changes.Items[0].Accepted = false;
        Assert.False(ctx.Changes.IsEffective(ctx.Changes.Items[1]));
        var result = ctx.Changes.Apply(_m);
        Assert.True(result.Ok);
        Assert.Contains("1 skipped", result.Message);
        Assert.DoesNotContain(_m.AllElements, e => e.Name == "A");
        Assert.Contains(_m.AllElements, e => e.Name == "B");
    }

    [Fact]
    public void A_Failing_Change_Rolls_The_Whole_Set_Back()
    {
        var ctx = Ctx();
        var structure = Named("Structure");
        Run(ctx, "create_element", new { kind = "Block", owner_id = structure.Id, name = "Pump" });
        Run(ctx, "update_fields", new { element_id = Named("Engine").Id, fields = new[] { new { key = "documentation", value = "x" } } });
        var undo = _m.History.UndoDescription;
        var count = _m.AllElements.Count();
        _m.Delete(Named("Engine"));   // someone else deleted it after the proposal
        count = _m.AllElements.Count();
        undo = _m.History.UndoDescription;

        var result = ctx.Changes.Apply(_m);
        Assert.False(result.Ok);
        Assert.Equal(ChangeSetState.Failed, ctx.Changes.State);
        Assert.Equal(count, _m.AllElements.Count());
        Assert.DoesNotContain(_m.AllElements, e => e.Name == "Pump");
        Assert.Equal(undo, _m.History.UndoDescription);
    }

    [Fact]
    public void Viewers_Cannot_Apply()
    {
        var ctx = Ctx();
        Run(ctx, "create_element", new { kind = "Block", owner_id = Named("Structure").Id, name = "Pump" });
        _m.History.ReadOnly = true;
        var result = ctx.Changes.Apply(_m);
        Assert.False(result.Ok);
        Assert.Contains("view-only", result.Message);
        Assert.Equal(ChangeSetState.Pending, ctx.Changes.State);
        Assert.DoesNotContain(_m.AllElements, e => e.Name == "Pump");
    }

    [Fact]
    public void Sysml_Text_Is_Previewed_Then_Applied_Through_Sync()
    {
        var structure = Named("Structure");
        var ctx = Ctx();
        var text = Run(ctx, "get_sysml_text", new { package_id = structure.Id }).Content;

        var broken = Run(ctx, "apply_sysml_text", new { package_id = structure.Id, text = text.Replace("part def BrakeSystem", "part def BrakeSystem :>") });
        Assert.True(broken.IsError);
        Assert.Contains("line", broken.Content);
        Assert.True(Run(ctx, "apply_sysml_text", new { package_id = structure.Id, text }).IsError);   // nothing would change

        var ok = Run(ctx, "apply_sysml_text", new { package_id = structure.Id, text = text.Replace("part def BrakeSystem", "part def Brakes") });
        Assert.False(ok.IsError, ok.Content);
        var p = ctx.Changes.Items.Single();
        Assert.Contains(p.Preview, l => l.Contains("Brakes"));
        Assert.Contains(_m.OfKind(ElementKind.Block), b => b.Name == "BrakeSystem");   // dry run only

        Assert.True(ctx.Changes.Apply(_m).Ok);
        Assert.Contains(_m.OfKind(ElementKind.Block), b => b.Name == "Brakes");
        _m.History.Undo();
        Assert.Contains(_m.OfKind(ElementKind.Block), b => b.Name == "BrakeSystem");
    }

    // ------------------------------------------------------------------ markdown

    [Fact]
    public void Markdown_Renders_Mentions_And_Drops_Unsafe_Links()
    {
        var blocks = MarkdownLite.Parse("""
            ## Gaps
            The [Engine](el:_abc) has **no** `verify` link, see [docs](https://example.com) or [x](javascript:alert(1)).

            - [ ] fix *this*
            - [x] done
            1. first

            | id | name |
            |----|------|
            | REQ-1 | [Braking](el:_r1) |
            ```
            code <b>
            ```
            """);
        Assert.IsType<MdHeading>(blocks[0]);
        var para = Assert.IsType<MdParagraph>(blocks[1]);
        Assert.Contains(para.Inlines, i => i is MdMention { ElementId: "_abc", Label: "Engine" });
        Assert.Contains(para.Inlines, i => i is MdLink { Url: "https://example.com" });
        Assert.DoesNotContain(para.Inlines, i => i is MdLink l && l.Url.StartsWith("javascript"));
        Assert.Contains(para.Inlines, i => i is MdStrong);
        var list = Assert.IsType<MdList>(blocks[2]);
        Assert.False(list.Items[0].Checked);
        Assert.True(list.Items[1].Checked);
        Assert.Contains(blocks, b => b is MdList { Ordered: true });
        var table = Assert.IsType<MdTable>(blocks.Single(b => b is MdTable));
        Assert.Contains(table.Rows[0][1], i => i is MdMention { ElementId: "_r1" });
        Assert.Equal("code <b>", Assert.IsType<MdCodeBlock>(blocks[^1]).Code);
        // Half-streamed markup stays literal.
        Assert.Equal("**bold", MarkdownLite.PlainText(MarkdownLite.Inlines("**bold")));
    }
}

public class AssistantEngineTests
{
    private readonly ModelRepository _m = SampleModels.Vehicle();
    private string Context => Prompts.Context(_m, "Vehicle", _m.OfKind(ElementKind.Block).First(), _m.Diagrams.First(), readOnly: false);

    [Fact]
    public async Task Tool_Loop_Streams_Text_And_Returns_Tool_Results()
    {
        var engine = _m.OfKind(ElementKind.Block).Single(b => b.Name == "Engine");
        var fake = new FakeAnthropicHandler()
            .Stream(new Sse().Text("Let me look.").ToolUse("toolu_1", "search_elements", "{\"query\": \"kind:Block where name = Engine\"}").End("tool_use"))
            .Stream(new Sse().Text("The ", $"[Engine](el:{engine.Id})", " satisfies REQ-005.").End("end_turn"));
        var conv = new Conversation();
        var updates = 0;
        await fake.Engine().RunTurnAsync(conv, _m, new TurnRequest("What satisfies engine power?", Context), () => updates++);

        Assert.Equal(2, fake.Requests.Count);
        Assert.True(updates > 3);
        var texts = conv.Items.OfType<AssistantTextItem>().ToList();
        Assert.Equal("Let me look.", texts[0].Text);
        Assert.Contains($"(el:{engine.Id})", texts[1].Text);
        Assert.All(texts, t => Assert.False(t.Streaming));
        Assert.Single(conv.Items.OfType<ToolActivityItem>(), t => t.Tool == "search_elements" && !t.IsError);
        Assert.False(conv.Busy);
        Assert.Equal(4, conv.History.Count);   // user, assistant(tool_use), user(tool_result), assistant
        Assert.Equal(200, conv.InputTokens);
        Assert.Equal(80, conv.CacheReadTokens);

        // The first request: model, effort, strict cached tools, cached system prompt, fallbacks, streaming.
        var first = JsonNode.Parse(fake.Requests[0].Body)!;
        Assert.EndsWith("/v1/messages", new Uri(fake.Requests[0].Url).AbsolutePath);
        Assert.Equal("claude-opus-5-5", first["model"]!.GetValue<string>());
        Assert.True(first["stream"]!.GetValue<bool>());
        Assert.Equal("medium", first["output_config"]!["effort"]!.GetValue<string>());
        Assert.Null(first["thinking"]);
        Assert.Equal("auto", first["tool_choice"]!["type"]!.GetValue<string>());
        Assert.Equal("default", first["fallbacks"]!.GetValue<string>());
        Assert.Contains("server-side-fallback-2026-07-01", fake.Requests[0].Headers["anthropic-beta"]);
        Assert.Equal("ephemeral", first["cache_control"]!["type"]!.GetValue<string>());   // automatic caching of the conversation
        Assert.Equal("ephemeral", first["system"]![0]!["cache_control"]!["type"]!.GetValue<string>());
        var tools = first["tools"]!.AsArray();
        Assert.Equal(Toolbox.All.Count, tools.Count);
        Assert.All(tools, t => Assert.True(t!["strict"]!.GetValue<bool>()));
        Assert.Equal("ephemeral", tools[^1]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Null(tools[0]!["cache_control"]);
        var userContent = first["messages"]![0]!["content"]!.AsArray();
        Assert.Contains("<context>", userContent[0]!["text"]!.GetValue<string>());
        Assert.Contains("Selection: Block", userContent[0]!["text"]!.GetValue<string>());
        Assert.Equal("What satisfies engine power?", userContent[1]!["text"]!.GetValue<string>());

        // The second request carries the assistant's tool call and our result.
        var second = JsonNode.Parse(fake.Requests[1].Body)!;
        var messages = second["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        var toolUse = messages[1]!["content"]!.AsArray().Single(b => b!["type"]!.GetValue<string>() == "tool_use")!;
        Assert.Equal("kind:Block where name = Engine", toolUse["input"]!["query"]!.GetValue<string>());
        var toolResult = messages[2]!["content"]![0]!;
        Assert.Equal("tool_result", toolResult["type"]!.GetValue<string>());
        Assert.Equal("toolu_1", toolResult["tool_use_id"]!.GetValue<string>());
        Assert.False(toolResult["is_error"]?.GetValue<bool>() ?? false);
        Assert.Contains(engine.Id, toolResult["content"]!.ToJsonString());
    }

    [Fact]
    public async Task Write_Tools_Produce_A_Change_Set_And_Bad_Input_Is_An_Error_Result()
    {
        var structure = _m.Root.OwnedElements.Single(e => e.Name == "Structure");
        var fake = new FakeAnthropicHandler()
            .Stream(new Sse()
                .ToolUse("toolu_1", "create_element", $"{{\"kind\":\"Block\",\"owner_id\":\"{structure.Id}\",\"name\":\"Gearbox\"}}")
                .ToolUse("toolu_2", "create_element", "{\"kind\":\"Gizmo\",\"name\":\"X\"}")
                .End("tool_use"))
            .Stream(new Sse().Text("I proposed a **Gearbox** block.").End("end_turn"));
        var conv = new Conversation();
        var count = _m.AllElements.Count();
        await fake.Engine(new AssistantOptions { ServerSideFallbacks = false }).RunTurnAsync(conv, _m, new TurnRequest("Add a gearbox", Context, AssistantEffort.High), () => { });

        Assert.Equal(count, _m.AllElements.Count());   // proposals only
        var card = Assert.Single(conv.Items.OfType<ChangeSetItem>());
        Assert.Equal("Gearbox", card.Changes.Items.Single().Name);
        Assert.IsType<ChangeSetItem>(conv.Items[^1]);

        var first = JsonNode.Parse(fake.Requests[0].Body)!;
        Assert.Equal("high", first["output_config"]!["effort"]!.GetValue<string>());
        Assert.Null(first["fallbacks"]);
        var results = JsonNode.Parse(fake.Requests[1].Body)!["messages"]![2]!["content"]!.AsArray();
        Assert.Equal(2, results.Count);
        Assert.False(results[0]!["is_error"]?.GetValue<bool>() ?? false);
        Assert.True(results[1]!["is_error"]!.GetValue<bool>());
        Assert.Contains("Invalid input", results[1]!["content"]!.ToJsonString());
        Assert.Contains(conv.Items.OfType<ToolActivityItem>(), t => t.IsError);

        // Applying notes the outcome for the next turn's context.
        Assert.True(card.Changes.Apply(_m).Ok);
        conv.NoteOutcome(card.Changes);
        fake.Stream(new Sse().Text("Done.").End("end_turn"));
        await fake.Engine().RunTurnAsync(conv, _m, new TurnRequest("Thanks", Context), () => { });
        var context = JsonNode.Parse(fake.Requests[2].Body)!["messages"]![4]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("applied change set #1", context);
        Assert.Contains("new:1 = _", context);
    }

    [Fact]
    public async Task Refusal_Is_Shown_And_Its_Tools_Never_Run()
    {
        var fake = new FakeAnthropicHandler()
            .Stream(new Sse().ToolUse("toolu_1", "delete_element", $"{{\"element_id\":\"{_m.Root.OwnedElements[0].Id}\"}}")
                             .End("refusal", new { type = "refusal", category = "cyber", explanation = "Policy." }));
        var conv = new Conversation();
        await fake.Engine().RunTurnAsync(conv, _m, new TurnRequest("Do something bad", Context), () => { });
        var notice = Assert.Single(conv.Items.OfType<NoticeItem>());
        Assert.Equal(NoticeKind.Refusal, notice.Kind);
        Assert.Empty(conv.Items.OfType<ChangeSetItem>());
        Assert.Empty(conv.Items.OfType<ToolActivityItem>());
        Assert.Empty(conv.History);   // the refused exchange is not replayed
        Assert.False(conv.Busy);
    }

    [Fact]
    public async Task Api_Errors_And_Stop_Leave_A_Clean_History()
    {
        var fake = new FakeAnthropicHandler().Error(HttpStatusCode.Unauthorized, "authentication_error", "invalid x-api-key");
        var conv = new Conversation();
        await fake.Engine().RunTurnAsync(conv, _m, new TurnRequest("Hello", Context), () => { });
        Assert.Contains("API key", Assert.Single(conv.Items.OfType<NoticeItem>()).Text);
        Assert.DoesNotContain("sk-ant", Assert.Single(conv.Items.OfType<NoticeItem>()).Text);
        Assert.Empty(conv.History);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        fake.Stream(new Sse().Text("never").End("end_turn"));
        await fake.Engine().RunTurnAsync(conv, _m, new TurnRequest("Hello", Context), () => { }, cts.Token);
        Assert.Contains(conv.Items.OfType<NoticeItem>(), n => n.Text == "Stopped.");
        Assert.Empty(conv.History);
        Assert.False(conv.Busy);

        // A truncated tool call is not executed.
        fake.Stream(new Sse().ToolUse("toolu_9", "search_elements", "{\"query\": \"kind:Bl").End("max_tokens"));
        await fake.Engine().RunTurnAsync(conv, _m, new TurnRequest("Hello", Context), () => { });
        Assert.Contains(conv.Items.OfType<NoticeItem>(), n => n.Text.Contains("output limit"));
        Assert.Empty(conv.Items.OfType<ToolActivityItem>());
        Assert.Empty(conv.History);
    }

    [Fact]
    public void Quick_Action_Prompts_Name_Their_Tools()
    {
        var req = _m.Root.OwnedElements.Single(e => e.Name == "Requirements");
        var gen = Prompts.GenerateRequirements(_m, req, "The pump shall move 10 l/min.");
        Assert.Contains(req.Id, gen);
        Assert.Contains("REQ-006", gen);
        Assert.Contains("<specification>", gen);
        Assert.Contains("create_relationship", Prompts.SuggestTraceLinks(null));
        Assert.Contains("report_findings", Prompts.ReviewModel(req));
        Assert.Contains("InternalBlock", Prompts.DiagramFromDescription(req, "Internal block diagram", "A pump"));
    }
}
