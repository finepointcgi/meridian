using Bunit;
using Meridian.Core.Derivation;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>"Create diagrams from diagrams" in the UI: the derive / update dialog, menu entries, the badge and the inspector chain.</summary>
public class DerivationWebTests : TestContext
{
    private readonly Workspace _ws;

    public DerivationWebTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ws = Services.GetRequiredService<Workspace>();
        _ws.UseLocalModel(SampleModels.Vehicle());
        RenderComponent<MudPopoverProvider>();
    }

    private Element UseCase(string name) => _ws.Model.OfKind(ElementKind.UseCase).Single(e => e.Name == name);

    private sealed class FakeAssistant : IDerivationAssistant
    {
        public string Name => "Fake";
        public int Calls { get; private set; }
        public Task<DerivationAiResult> FillInAsync(DerivationAiRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new DerivationAiResult(true, null, ["Driver opens the door", "Driver sits down", "If the seat is wrong, Driver adjusts the seat"], new Dictionary<string, string>()));
        }
    }

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Ref)> Show(DerivationKind kind, Element source, IDerivationAssistant? ai = null)
    {
        var provider = RenderComponent<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        IDialogReference? reference = null;
        var p = new DialogParameters<DeriveDialog> { { x => x.Kind, kind }, { x => x.Source, source }, { x => x.Assistant, ai } };
        await provider.InvokeAsync(async () => reference = await dialogs.ShowAsync<DeriveDialog>("Derive", p));
        return (provider, reference!);
    }

    [Fact]
    public async Task Derive_Dialog_Lists_The_Plan_With_Checkboxes_And_Warnings_And_Returns_It()
    {
        var (provider, reference) = await Show(DerivationKind.ActivityFromUseCase, UseCase("Drive Vehicle"));
        Assert.Contains("Create activity from use case", provider.Find(".dv-title").TextContent);
        Assert.Contains("skeleton", provider.Find(".dv-warn").TextContent);   // no description
        Assert.Empty(provider.FindAll("[data-testid=derive-ai]"));            // assistant off: no AI switch
        Assert.Empty(provider.FindAll(".dv-preview"));                        // nothing to compare with yet
        var rows = provider.FindAll(".dv-row");
        Assert.Contains(rows, r => r.TextContent.Contains("Activity “Drive Vehicle”"));
        Assert.Contains(rows, r => r.TextContent.Contains("Swimlane “Driver”"));
        Assert.All(rows, r => Assert.Contains("new", r.QuerySelector(".dv-status")!.TextContent));
        Assert.Contains("1 diagram", provider.Find(".dv-counts").TextContent);
        Assert.Equal("Create", provider.Find("button.dv-create").TextContent.Trim());

        provider.Find("button.dv-create").Click();
        var answer = Assert.IsType<DeriveDialog.Answer>((await reference.Result)!.Data);
        Assert.False(answer.Dismissed);
        var r = DiagramDerivation.Apply(_ws.Model, answer.Plan);
        Assert.Contains("(⌘Z to undo)", r.Summary);
        Assert.Single(UseCase("Drive Vehicle").OwnedElements, e => e.Kind == ElementKind.Activity);
    }

    [Fact]
    public async Task Ai_Switch_Appears_With_An_Assistant_And_Its_Steps_Show_In_The_Preview()
    {
        var ai = new FakeAssistant();
        var (provider, _) = await Show(DerivationKind.ActivityFromUseCase, UseCase("Drive Vehicle"), ai);
        var sw = provider.Find("[data-testid=derive-ai] input");
        await provider.InvokeAsync(() => sw.Change(true));
        Assert.Equal(1, ai.Calls);
        provider.WaitForAssertion(() => Assert.Contains(provider.FindAll(".dv-row"), r => r.TextContent.Contains("Adjusts the seat")));
        Assert.Contains("Steps written by Fake", provider.Find(".dv-ai-note").TextContent);
        Assert.Contains(provider.FindAll(".dv-row"), r => r.TextContent.Contains("written by AI"));

        // Not for a derivation where AI has nothing to add.
        var (other, _) = await Show(DerivationKind.StructureFromSequence, _ws.Model.OfKind(ElementKind.Interaction).Single(), ai);
        Assert.Empty(other.FindAll("[data-testid=derive-ai]"));
    }

    [Fact]
    public async Task Update_Dialog_Shows_The_Diff_With_Highlights_And_Accept_Or_Dismiss()
    {
        var uc = UseCase("Refuel");
        _ws.Model.Set(uc, FieldKeys.Documentation, "1. Driver opens the flap.\n2. Driver fills the tank.");
        DiagramDerivation.Apply(_ws.Model, DiagramDerivation.Plan(_ws.Model, DerivationKind.ActivityFromUseCase, uc));
        _ws.Model.Set(uc, FieldKeys.Documentation, "1. Driver opens the fuel flap.\n3. Driver pays.");

        var (provider, reference) = await Show(DerivationKind.ActivityFromUseCase, uc);
        Assert.StartsWith("Update from source", provider.Find(".dv-title").TextContent);
        var rows = provider.FindAll(".dv-row");
        Assert.Contains(rows, r => r.ClassList.Contains("chg-changed") && r.TextContent.Contains("Opens the fuel flap"));
        Assert.Contains(rows, r => r.ClassList.Contains("chg-added") && r.TextContent.Contains("Pays"));
        Assert.Contains(rows, r => r.ClassList.Contains("chg-removed") && r.TextContent.Contains("Fills the tank"));
        var preview = provider.Find(".dv-preview");
        Assert.NotEmpty(preview.QuerySelectorAll(".chg-layer .chg-added"));
        Assert.NotEmpty(preview.QuerySelectorAll(".chg-layer .chg-changed"));
        Assert.NotEmpty(preview.QuerySelectorAll(".chg-layer .chg-removed"));
        Assert.Equal("Accept", provider.Find("button.dv-create").TextContent.Trim());

        // Unticking the removal takes its ghost out of the preview.
        var removeRow = provider.FindAll(".dv-row").First(r => r.ClassList.Contains("chg-removed") && r.TextContent.Contains("Fills the tank"));
        removeRow.QuerySelector("input")!.Change(false);
        Assert.DoesNotContain(provider.Find(".dv-preview").QuerySelectorAll(".chg-ghost"), g => g.TextContent.Contains("Fills the tank"));

        provider.Find("button.dv-dismiss").Click();
        var answer = Assert.IsType<DeriveDialog.Answer>((await reference.Result)!.Data);
        Assert.True(answer.Dismissed);
        var d = DiagramDerivation.ExistingDiagrams(_ws.Model, DerivationKind.ActivityFromUseCase, uc).Single();
        Assert.True(DiagramDerivation.IsStale(_ws.Model, d));
        DiagramDerivation.Dismiss(_ws.Model, answer.Plan.Kind, answer.Plan.Source);
        Assert.False(DiagramDerivation.IsStale(_ws.Model, d));
    }

    [Fact]
    public void Context_Menu_Entries_Offer_Only_Valid_Derivations()
    {
        var m = _ws.Model;
        DerivationOffer? ran = null;
        Task Run(DerivationOffer o) { ran = o; return Task.CompletedTask; }

        var uc = DerivationUi.MenuEntries(m, UseCase("Refuel"), readOnly: false, Run);
        Assert.Equal(["Create from…", "Create activity from use case…"], uc.Select(e => e.Label));
        Assert.True(uc[0].Header);
        uc[1].Action!();
        Assert.Equal(DerivationKind.ActivityFromUseCase, ran!.Kind);

        var sd = m.Diagrams.Single(d => d.DiagramType == DiagramType.Sequence);
        var seq = DerivationUi.MenuEntries(m, sd, false, Run).Select(e => e.Label).ToList();
        Assert.Contains("Create BDD + IBD from sequence…", seq);
        Assert.Contains(seq, l => l.StartsWith("Create state machine for Engine"));
        var reqs = DerivationUi.MenuEntries(m, m.Root.OwnedElements.Single(e => e.Name == "Requirements"), false, Run).Select(e => e.Label).ToList();
        Assert.Contains("Create use cases from requirements…", reqs);
        Assert.Contains("Propose satisfy and verify links…", reqs);
        Assert.Empty(DerivationUi.MenuEntries(m, m.OfKind(ElementKind.ValueType).First(), false, Run));
        Assert.Empty(DerivationUi.MenuEntries(m, UseCase("Refuel"), readOnly: true, Run));

        // Once derived, the entry offers an update instead of a duplicate.
        DiagramDerivation.Apply(m, DiagramDerivation.Plan(m, DerivationKind.ActivityFromUseCase, UseCase("Refuel")));
        Assert.Equal("Update activity from use case…", DerivationUi.MenuEntries(m, UseCase("Refuel"), false, Run)[1].Label);
    }

    [Fact]
    public void Badge_Shows_When_The_Source_Changed_And_Offers_Update()
    {
        var uc = UseCase("Refuel");
        var d = DiagramDerivation.Apply(_ws.Model, DiagramDerivation.Plan(_ws.Model, DerivationKind.ActivityFromUseCase, uc)).Diagrams.Single();
        Diagram? asked = null;
        var cut = RenderComponent<DerivedSourceBadge>(p => p.Add(x => x.Diagram, d).Add(x => x.OnUpdate, (Diagram x) => asked = x));
        Assert.Empty(cut.FindAll("[data-testid=derive-badge]"));

        _ws.Model.Set(uc, FieldKeys.Documentation, "1. Driver opens the flap.");
        cut.Render();
        Assert.Contains("Source changed", cut.Find("[data-testid=derive-badge]").TextContent);
        cut.Find("[data-testid=derive-update]").Click();
        Assert.Same(d, asked);

        // A diagram that was not derived never shows it.
        var plain = RenderComponent<DerivedSourceBadge>(p => p.Add(x => x.Diagram, _ws.Model.Diagrams.First(x => x.Get(DeriveFields.Source).Length == 0)));
        Assert.Empty(plain.FindAll("[data-testid=derive-badge]"));
    }

    [Fact]
    public void Inspector_Chain_Links_Open_And_Offers_Create_Next()
    {
        var uc = UseCase("Refuel");
        DiagramDerivation.Apply(_ws.Model, DiagramDerivation.Plan(_ws.Model, DerivationKind.ActivityFromUseCase, uc));
        var act = uc.OwnedElements.Single(e => e.Kind == ElementKind.Activity);
        Element? opened = null;
        DerivationOffer? next = null;
        var cut = RenderComponent<DerivationChainSection>(p => p.Add(x => x.E, act)
            .Add(x => x.OnOpen, (Element e) => opened = e).Add(x => x.OnCreate, (DerivationOffer o) => next = o));
        var chain = cut.Find("[data-testid=derive-chain]");
        Assert.Equal(["Requirements", "Use case", "Activity", "Sequence", "IBD"], chain.QuerySelectorAll("[data-stage]").Select(x => x.GetAttribute("data-stage")));
        Assert.Contains("missing", chain.QuerySelector("[data-stage='Requirements']")!.ClassName);   // Refuel refines no requirement
        Assert.Contains("missing", chain.QuerySelector("[data-stage='IBD']")!.ClassName);
        Assert.NotNull(cut.Find(".link.current"));
        Assert.Contains("Activity", cut.Find(".link.current").TextContent);

        cut.Find(".link[data-stage='Use case']").Click();
        Assert.True(opened is Diagram { DiagramType: DiagramType.UseCase } || ReferenceEquals(opened, uc));
        cut.Find("button.create-next[data-stage='Sequence']").Click();
        Assert.Equal(DerivationKind.SequenceFromActivity, next!.Kind);
        Assert.Same(act, next.Source);
        Assert.Contains("Derived (activity) from", cut.Find(".derive-origin").TextContent);
    }
}
