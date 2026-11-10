using Bunit;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>Editing combined fragment operands (add, guard, separator drag) and placing pseudostates on the canvas.</summary>
public class OperandsWebTests : TestContext
{
    public OperandsWebTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        Services.AddSingleton<ImageCache>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private sealed record Seq(ModelRepository M, Diagram D, DiagramNode Fragment);

    private static Seq Build()
    {
        var m = new ModelRepository("Shop");
        var ia = m.Create(m.Root, ElementKind.Interaction, "Checkout");
        var d = m.CreateDiagram(ia, DiagramType.Sequence, "Checkout");
        m.AddNode(d, m.Create(ia, ElementKind.Lifeline, "client"), new Bounds(40, 20, 150, 400));
        m.AddNode(d, m.Create(ia, ElementKind.Lifeline, "shop"), new Bounds(300, 20, 150, 400));
        var f = m.Create(ia, ElementKind.CombinedFragment);
        m.Set(f, FieldKeys.Guard, "card ok");
        var n = m.AddNode(d, f, new Bounds(20, 100, 460, 200));
        m.History.Clear();
        return new Seq(m, d, n);
    }

    private (Workspace Ws, DiagramController C) Open(ModelRepository m, Diagram d)
    {
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(m);
        var c = ws.Open(d);
        c.SetZoom(1);
        return (ws, c);
    }

    private static void Drag(DiagramController c, Pt from, Pt to)
    {
        var a = c.ToScreen(from);
        var b = c.ToScreen(to);
        c.PointerDown(a.X, a.Y, 0, Mods.None, 1);
        c.PointerMove((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        c.PointerMove(b.X, b.Y);
        c.PointerUp(Mods.None);
    }

    [Fact]
    public void Inspector_Adds_An_Operand_Edits_Its_Guard_And_Removes_It()
    {
        var s = Build();
        var (ws, _) = Open(s.M, s.D);
        ws.Select(s.Fragment.Element);
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = RenderComponent<OperandsSection>(p => p.Add(x => x.E, s.Fragment.Element));
        Assert.Contains("One operand", cut.Find("[data-testid=operands]").TextContent);

        cut.Find(".op-add").Click();
        cut.Render();
        var ops = InteractionOperands.Of(s.Fragment.Element);
        Assert.Equal(["card ok", "else"], ops.Select(o => o.Get(FieldKeys.Guard)));
        var guards = cut.FindAll(".op-guard input");
        Assert.Equal(2, guards.Count);
        Assert.Equal("else", guards[1].GetAttribute("value"));

        cut.FindAll(".op-guard input")[1].Change("card declined");
        Assert.Equal("card declined", ops[1].Get(FieldKeys.Guard));
        Assert.Contains("[card declined]", Meridian.Serialization.SvgRenderer.Render(s.M, s.D));

        cut.Find(".op-add").Click();
        cut.Render();
        Assert.Equal(3, InteractionOperands.Of(s.Fragment.Element).Count);
        cut.FindAll(".op-remove")[2].Click();
        Assert.Equal(2, InteractionOperands.Of(s.Fragment.Element).Count);

        // Selecting an operand (in the model tree) shows its fragment's operands, the selected one marked.
        cut.SetParametersAndRender(p => p.Add(x => x.E, ops[1]));
        Assert.Single(cut.FindAll(".op-row.on"));
    }

    [Fact]
    public void Dragging_A_Separator_Moves_It_In_One_Undo_Step_And_Never_Past_Its_Neighbours()
    {
        var s = Build();
        var (_, c) = Open(s.M, s.D);
        Assert.NotNull(c.AddOperand(s.Fragment));
        var sep = InteractionOperands.Layout(s.Fragment, s.Fragment.Bounds)[1].Top;
        Assert.Equal(200, sep);
        Assert.Equal((s.Fragment, 1), c.HitSeparator(new Pt(200, sep + 2)));
        Assert.Null(c.HitSeparator(new Pt(200, sep + 30)));

        s.M.History.Clear();
        Drag(c, new Pt(200, sep), new Pt(200, 160));
        Assert.Equal(DiagramController.Gesture.None, c.Current);
        Assert.Equal(160, InteractionOperands.Layout(s.Fragment, s.Fragment.Bounds)[1].Top);
        Assert.Equal(s.Fragment.Bounds, new Bounds(20, 100, 460, 200));   // the fragment itself didn't move
        Assert.Contains(s.Fragment, c.SelectedNodes);

        Drag(c, new Pt(200, 160), new Pt(200, 1000));   // clamped above the bottom
        Assert.Equal(300 - InteractionOperands.MinBand / 2, InteractionOperands.Layout(s.Fragment, s.Fragment.Bounds)[1].Top);

        s.M.History.Undo();
        Assert.Equal(160, InteractionOperands.Layout(s.Fragment, s.Fragment.Bounds)[1].Top);
        s.M.History.Undo();
        Assert.Equal(200, InteractionOperands.Layout(s.Fragment, s.Fragment.Bounds)[1].Top);
    }

    [Fact]
    public void Canvas_Draws_Separators_And_A_Grip_On_The_Selected_Fragment()
    {
        var s = Build();
        var (_, c) = Open(s.M, s.D);
        c.AddOperand(s.Fragment);
        c.Select(s.Fragment.Element);
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = RenderComponent<DiagramView>(p => p.Add(x => x.C, c));
        Assert.Single(cut.FindAll("line.operand-sep"));
        var grip = cut.Find("line.operand-grip");
        Assert.Equal("1", grip.GetAttribute("data-operand-grip"));
        Assert.Contains("ns-resize", grip.GetAttribute("style"));
        Assert.Contains("[else]", cut.Markup);
    }

    [Fact]
    public void Pseudostates_Come_From_The_Palette_With_Their_Kind_And_Entry_Points_Sit_On_A_States_Border()
    {
        var m = new ModelRepository("Door");
        var sm = m.Create(m.Root, ElementKind.StateMachine, "Door");
        var d = m.CreateDiagram(sm, DiagramType.StateMachine, "Door");
        var open = m.Create(sm, ElementKind.State, "Open");
        var on = m.AddNode(d, open, new Bounds(200, 200, 240, 160));
        var (_, c) = Open(m, d);

        RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = RenderComponent<DiagramView>(p => p.Add(x => x.C, c));
        Assert.Contains("Deep History", cut.Markup);
        Assert.Contains("Terminate", cut.Markup);

        var screen = c.ToScreen(new Pt(80, 80));
        c.CreateAtScreen(new PaletteItem(ElementKind.Pseudostate, Pseudostates.DeepHistory), screen.X, screen.Y);
        var deep = Assert.Single(m.OfKind(ElementKind.Pseudostate));
        Assert.Equal(Pseudostates.DeepHistory, Pseudostates.KindOf(deep));
        Assert.Equal((28.0, 28.0), (d.NodeFor(deep)!.Bounds.Width, d.NodeFor(deep)!.Bounds.Height));

        // An entry point dropped inside a state belongs to it and snaps to its border.
        var inside = c.ToScreen(new Pt(230, 280));
        c.CreateAtScreen(new PaletteItem(ElementKind.Pseudostate, Pseudostates.EntryPoint), inside.X, inside.Y);
        var entry = m.OfKind(ElementKind.Pseudostate).Single(p => Pseudostates.KindOf(p) == Pseudostates.EntryPoint);
        Assert.Same(open, entry.Owner);
        var en = d.NodeFor(entry)!;
        Assert.Equal(on.Id, en.ParentId);
        Assert.Equal(200, en.Bounds.Center.X, 3);   // on the left border
        Assert.Contains(">H*</text>", Meridian.Serialization.SvgRenderer.Render(m, d));
    }

    [Fact]
    public void Activity_Palette_Offers_Datastore_And_Central_Buffer_Nodes()
    {
        var m = new ModelRepository("Shop");
        var act = m.Create(m.Root, ElementKind.Activity, "Order");
        var d = m.CreateDiagram(act, DiagramType.Activity, "Order");
        var (_, c) = Open(m, d);
        var p = c.ToScreen(new Pt(200, 200));
        c.CreateAtScreen(new PaletteItem(ElementKind.ObjectNode, Subtypes.DataStore, "Data Store"), p.X, p.Y);
        var store = Assert.Single(m.OfKind(ElementKind.ObjectNode));
        Assert.Equal(Subtypes.DataStore, store.Get(FieldKeys.Subtype));
        Assert.Contains("datastore", Meridian.Serialization.SvgRenderer.Render(m, d));
    }
}
