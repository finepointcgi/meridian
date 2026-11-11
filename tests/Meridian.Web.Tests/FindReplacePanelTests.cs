using Bunit;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

public class FindReplacePanelTests : TestContext
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    public FindReplacePanelTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        RenderComponent<MudBlazor.MudPopoverProvider>();
    }

    private (Workspace Ws, IRenderedComponent<FindReplacePanel> Cut, FindReplacePanel.PanelState State) Open(ModelRepository m)
    {
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(m);
        var state = new FindReplacePanel.PanelState();
        var cut = RenderComponent<FindReplacePanel>(p => p.Add(x => x.State, state));
        return (ws, cut, state);
    }

    private static void Type(IRenderedComponent<FindReplacePanel> cut, string testId, string text) =>
        cut.Find($"[data-testid={testId}]").Input(text);

    private static Element Block(Workspace ws, string name) => ws.Model.OfKind(ElementKind.Block).Single(b => b.Name == name);

    [Fact]
    public void Typing_Streams_Grouped_Results_With_Highlighted_Snippets()
    {
        var (_, cut, state) = Open(SampleModels.Vehicle());
        Type(cut, "fr-find", "Engine");
        cut.WaitForAssertion(() =>
        {
            Assert.False(state.Running);
            Assert.Contains("matches in", cut.Find("[data-testid=fr-status]").TextContent);
        }, Wait);
        var groups = cut.FindAll("[data-testid=fr-group]");
        Assert.True(groups.Count >= 5, $"{groups.Count} groups");
        var engine = groups.First(g => g.QuerySelector(".fr-name")!.TextContent == "Engine");
        Assert.Contains("Block", engine.TextContent);
        Assert.Equal("Engine", engine.QuerySelector(".fr-row mark")!.TextContent);
        Assert.Contains("Name", engine.QuerySelector(".fr-flabel")!.TextContent);
        Assert.Equal(state.Matches, cut.FindAll("[data-testid=fr-row]").Count);
    }

    [Fact]
    public void Clicking_A_Result_Selects_The_Element()
    {
        var (ws, cut, state) = Open(SampleModels.Vehicle());
        Type(cut, "fr-find", "maxPower");
        cut.WaitForAssertion(() => Assert.True(!state.Running && state.Hits.Count > 0), Wait);
        cut.FindAll("[data-testid=fr-row]").First().Click();
        Assert.Equal("maxPower", ws.Selected?.Name);
    }

    [Fact]
    public void Replace_All_Previews_A_Diff_Then_Applies_As_One_Undo_Step()
    {
        var (ws, cut, state) = Open(SampleModels.Vehicle());
        ws.Model.History.Clear();
        cut.Find("[data-testid=fr-case]").Click();
        Type(cut, "fr-replace", "Motor");
        Type(cut, "fr-find", "Engine");
        cut.WaitForAssertion(() => Assert.True(!state.Running && state.Matches > 3), Wait);
        Assert.Contains("Motor", cut.Find(".fr-row ins").TextContent);

        cut.Find("[data-testid=fr-replace-all]").Click();
        var preview = cut.Find("[data-testid=fr-preview]");
        Assert.Contains("Preview:", preview.TextContent);
        var row = cut.FindAll(".fr-diff tbody tr").First(r => r.Children[0].TextContent == "Engine" && r.Children[1].TextContent == "Name");
        Assert.Equal("Engine", row.QuerySelector("td.before del")!.TextContent);
        Assert.Equal("Motor", row.QuerySelector("td.after ins")!.TextContent);
        Assert.Equal("Engine", Block(ws, "Engine").Name);   // nothing changed yet

        cut.Find("[data-testid=fr-apply]").Click();
        cut.WaitForAssertion(() => Assert.Contains("Replaced in", cut.Find("[data-testid=fr-message]").TextContent), Wait);
        Assert.Single(ws.Model.OfKind(ElementKind.Block), b => b.Name == "Motor");
        Assert.DoesNotContain(ws.Model.AllElements, e => e.Name.Contains("Engine", StringComparison.Ordinal));
        Assert.StartsWith("Replace “Engine” with “Motor”", ws.Model.History.UndoDescription);
        // The search runs again: nothing left to find.
        cut.WaitForAssertion(() => Assert.True(!state.Running && state.Hits.All(h => h.Fields.All(f => f.ReadOnly || f.Key != FieldKeys.Name))), Wait);

        ws.Model.History.Undo();
        Assert.Equal("Engine", Block(ws, "Engine").Name);
        Assert.False(ws.Model.History.CanUndo);
    }

    [Fact]
    public void Unchecked_Matches_Are_Left_Alone_By_Replace_Selected()
    {
        var m = new ModelRepository("M");
        var a = m.Create(m.Root, ElementKind.Block, "Pump A");
        var b = m.Create(m.Root, ElementKind.Block, "Pump B");
        var (_, cut, state) = Open(m);
        Type(cut, "fr-replace", "Valve");
        Type(cut, "fr-find", "Pump");
        cut.WaitForAssertion(() => Assert.True(!state.Running && state.Hits.Count == 2), Wait);
        // Uncheck the whole of "Pump B" with its element checkbox.
        cut.FindAll("[data-testid=fr-group]").Single(g => g.GetAttribute("data-element") == b.Id).QuerySelector(".fr-gchk")!.Change(false);
        cut.Find("[data-testid=fr-replace-selected]").Click();
        Assert.Single(cut.FindAll(".fr-diff tbody tr"));
        cut.Find("[data-testid=fr-apply]").Click();
        cut.WaitForAssertion(() => Assert.Equal("Valve A", a.Name), Wait);
        Assert.Equal("Pump B", b.Name);
    }

    [Fact]
    public void Find_Only_And_Clashing_Matches_Are_Greyed_With_The_Reason()
    {
        var m = new ModelRepository("M");
        var a = m.Create(m.Root, ElementKind.Block, "Pump");
        m.Create(m.Root, ElementKind.Block, "Valve");
        m.Set(a, FieldKeys.Stereotypes, "PumpUnit");
        var (_, cut, state) = Open(m);
        cut.Find("[data-testid=fr-in-Stereotypes]").Change(true);
        Type(cut, "fr-replace", "Valve");
        Type(cut, "fr-find", "Pump");
        cut.WaitForAssertion(() => Assert.True(!state.Running && state.Matches == 2), Wait);
        var ro = cut.Find("[data-testid=fr-row].ro");
        Assert.Contains("Find only", ro.TextContent);
        Assert.True(ro.QuerySelector("input")!.HasAttribute("disabled"));
        Assert.Contains("1 read-only", cut.Find("[data-testid=fr-status]").TextContent);

        cut.Find("[data-testid=fr-replace-all]").Click();
        var rows = cut.FindAll(".fr-diff tbody tr");
        Assert.All(rows, r => Assert.Contains("skipped", r.ClassName));
        Assert.Contains(rows, r => r.TextContent.Contains("already has a Block named “Valve”"));
        Assert.True(cut.Find("[data-testid=fr-apply]").HasAttribute("disabled"));
    }

    [Fact]
    public void Bad_Regex_And_Too_Many_Results_Explain_Themselves()
    {
        var (_, cut, state) = Open(LargeModelGenerator.Generate(LargeModelOptions.Cameo(20_000)));
        cut.Find("[data-testid=fr-regex]").Click();
        Type(cut, "fr-find", "(unclosed");
        cut.WaitForAssertion(() => Assert.Contains("Invalid regular expression", cut.Find("[data-testid=fr-status]").TextContent), Wait);

        cut.Find("[data-testid=fr-regex]").Click();
        Type(cut, "fr-find", "e");
        cut.WaitForAssertion(() => Assert.True(!state.Running && state.Truncated), Wait);
        Assert.Contains("refine your search", cut.Find("[data-testid=fr-truncated]").TextContent);
        // Only the first page of elements is rendered.
        Assert.Equal(FindReplacePanel.PageSize, cut.FindAll("[data-testid=fr-group]").Count);
        cut.Find("[data-testid=fr-show-more]").Click();
        Assert.Equal(Math.Min(state.Hits.Count, 2 * FindReplacePanel.PageSize), cut.FindAll("[data-testid=fr-group]").Count);
    }

    [Fact]
    public void Scope_And_Kind_Narrow_The_Search()
    {
        var (ws, cut, state) = Open(SampleModels.Vehicle());
        ws.Select(Block(ws, "Engine"));
        cut.Find("[data-testid=fr-scope]").Change("Selection");
        Type(cut, "fr-find", "e");
        cut.WaitForAssertion(() => Assert.True(!state.Running && state.Hits.Count == 1), Wait);
        Assert.Same(Block(ws, "Engine"), state.Hits[0].Element);

        cut.Find("[data-testid=fr-scope]").Change("Model");
        cut.Find("[data-testid=fr-kind]").Change("Requirement");
        cut.WaitForAssertion(() => Assert.True(!state.Running && state.Hits.Count > 1), Wait);
        Assert.All(state.Hits, h => Assert.Equal(ElementKind.Requirement, h.Element!.Kind));
    }
}
