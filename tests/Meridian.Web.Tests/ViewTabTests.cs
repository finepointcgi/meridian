using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Web.Services;

namespace Meridian.Web.Tests;

/// <summary>Tables, queries and report templates open as tabs next to the diagrams.</summary>
public class ViewTabTests
{
    private static Workspace Sample()
    {
        var ws = new Workspace();
        ws.UseLocalModel(SampleModels.Vehicle());
        return ws;
    }

    [Fact]
    public void A_Saved_View_Opens_Once_And_Becomes_The_Shown_Tab()
    {
        var ws = Sample();
        var view = SavedViews.SaveQuery(ws.Model, "kind:Block", "Blocks");
        var a = ws.OpenView(ViewTabKind.Query, view);
        var b = ws.OpenView(ViewTabKind.Query, view);
        Assert.Same(a, b);
        Assert.Single(ws.OpenViews);
        Assert.Same(a, ws.ActiveView);
        Assert.Equal("Blocks", a.Title);
    }

    [Fact]
    public void Unsaved_Views_Reuse_Their_Tab_And_A_Diagram_Click_Shows_The_Diagram()
    {
        var ws = Sample();
        var t1 = ws.OpenView(ViewTabKind.Table);
        var t2 = ws.OpenView(ViewTabKind.Table);
        Assert.Same(t1, t2);
        Assert.Equal("New custom table", t1.Title);
        ws.OpenView(ViewTabKind.Query);
        Assert.Equal(2, ws.OpenViews.Count);

        ws.Open(ws.Model.Diagrams.First());   // a diagram tab: showing it hides the view
        Assert.Null(ws.ActiveView);
        ws.ActivateView(t1);
        ws.Activate(ws.Active!);
        Assert.Null(ws.ActiveView);   // the diagram shows; the view tabs stay open
        Assert.Equal(2, ws.OpenViews.Count);
    }

    [Fact]
    public void Deleting_The_Saved_Element_Closes_Its_Tab()
    {
        var ws = Sample();
        var view = SavedViews.SaveQuery(ws.Model, "kind:Block", "Blocks");
        ws.OpenView(ViewTabKind.Query, view);
        ws.Model.Delete(view);
        Assert.Empty(ws.OpenViews);
        Assert.Null(ws.ActiveView);
    }

    [Fact]
    public void Closing_The_Shown_Tab_Shows_Its_Neighbour()
    {
        var ws = Sample();
        var a = ws.OpenView(ViewTabKind.Table);
        var b = ws.OpenView(ViewTabKind.Query);
        ws.CloseView(b);
        Assert.Same(a, ws.ActiveView);
        ws.CloseView(a);
        Assert.Null(ws.ActiveView);
    }
}
