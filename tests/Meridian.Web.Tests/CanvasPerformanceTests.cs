using Bunit;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Meridian.Web.Settings;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>
/// The canvas on slow machines: wheel input added up per animation frame (meridian.installCanvas → DiagramView.OnWheel)
/// and the per-device "Visual effects" setting that drops shape shadows and panel blur.
/// </summary>
public class CanvasPerformanceTests : TestContext
{
    public CanvasPerformanceTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        Services.AddSingleton<ImageCache>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private DiagramController Open()
    {
        var m = new ModelRepository("Pkg");
        var d = m.CreateDiagram(m.Root, DiagramType.BlockDefinition, "Structure");
        m.AddNode(d, m.Create(m.Root, ElementKind.Block, "A"), new Bounds(0, 0, 160, 80));
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(m);
        var c = ws.Open(d);
        c.SetZoom(1);
        return c;
    }

    [Fact]
    public void A_Frame_Of_Wheel_Input_Pans_By_The_Sum_Of_Its_Deltas()
    {
        var c = Open();
        var (x, y) = (c.PanX, c.PanY);
        DiagramView.ApplyWheel(c, 300, 200, 30, 120, 0);
        Assert.Equal(x - 30, c.PanX, 6);
        Assert.Equal(y - 120, c.PanY, 6);
        Assert.Equal(1, c.Zoom, 6);
    }

    [Fact]
    public void Zoom_Steps_Match_One_Wheel_Notch_Each_And_Keep_The_Point_Under_The_Pointer()
    {
        var c = Open();
        var before = c.ToModel(300, 200);
        DiagramView.ApplyWheel(c, 300, 200, 0, 0, 3);
        Assert.Equal(1.1 * 1.1 * 1.1, c.Zoom, 6);
        var after = c.ToModel(300, 200);
        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Y, after.Y, 6);

        DiagramView.ApplyWheel(c, 300, 200, 0, 0, -3);
        Assert.Equal(1, c.Zoom, 6);
    }

    [Fact]
    public void OnWheel_Redraws_The_Canvas_At_The_New_Pan()
    {
        var c = Open();
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = RenderComponent<DiagramView>(p => p.Add(x => x.C, c));
        cut.InvokeAsync(() => cut.Instance.OnWheel(300, 200, 0, 100, 0));
        Assert.Contains(FormattableString.Invariant($"translate({c.PanX:0.##},{c.PanY:0.##})"), cut.Markup);
    }

    [Fact]
    public void OnPan_Moves_The_View_To_Where_The_Browser_Dragged_It()
    {
        var c = Open();
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = RenderComponent<DiagramView>(p => p.Add(x => x.C, c));
        cut.InvokeAsync(() => cut.Instance.OnPan(-120.5, 64.25));
        Assert.Equal(-120.5, c.PanX);
        Assert.Equal(64.25, c.PanY);
        Assert.Contains("class=\"viewport\" transform=\"translate(-120.5,64.25) scale(1)\"", cut.Markup);
        cut.InvokeAsync(() => cut.Instance.OnPan(double.NaN, 0));   // nonsense from the page is ignored
        Assert.Equal(-120.5, c.PanX);
    }

    public static TheoryData<string> SampleModelNames => new() { "vehicle", "cruise", "cubesat", "pump", "brake", "product-line" };

    private static ModelRepository Sample(string name) => name switch
    {
        "vehicle" => Meridian.Core.Samples.SampleModels.Vehicle(),
        "cruise" => Meridian.Core.Samples.AdaptiveCruiseSample.Create(),
        "cubesat" => Meridian.Core.Samples.CubeSatSample.Create(),
        "pump" => Meridian.Core.Samples.InfusionPumpSample.Create(),
        "brake" => Meridian.Core.Samples.ParkingBrakeSample.Create(),
        _ => Meridian.Core.Samples.ProductLineSample.Create(),
    };

    [Theory]
    [MemberData(nameof(SampleModelNames))]
    public void Body_Parts_Join_To_The_Whole_Body_And_Each_Is_Well_Formed(string sample)
    {
        var m = Sample(sample);
        Assert.NotEmpty(m.Diagrams);
        foreach (var d in m.Diagrams)
        {
            var whole = new System.Text.StringBuilder();
            Meridian.Serialization.SvgRenderer.RenderBody(whole, m, d, n => n.Bounds);
            var parts = Meridian.Serialization.SvgRenderer.RenderBodyParts(m, d, n => n.Bounds);
            Assert.Equal(whole.ToString(), string.Concat(parts));
            Assert.True(d.Nodes.Count == 0 || parts.Count > 1, $"{d.Name}: drawn as one piece");
            // Blazor inserts each piece on its own, so none may open an element that another closes.
            foreach (var part in parts)
                System.Xml.Linq.XElement.Parse($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\">{part}</svg>");
        }
    }

    [Fact]
    public void Moving_One_Shape_Changes_Only_Its_Piece_And_Its_Connectors()
    {
        var m = Meridian.Core.Samples.SampleModels.Vehicle();
        var d = m.Diagrams.Where(x => x.Edges.Count > 0).OrderByDescending(x => x.Nodes.Count).First();
        // A shape that isn't a container (moving one of those redraws what it holds).
        var moved = d.Nodes.First(n => !d.Nodes.Any(c => c.ParentId == n.Id) && d.Edges.Any(e => ReferenceEquals(e.Source, n) || ReferenceEquals(e.Target, n)));
        var before = Meridian.Serialization.SvgRenderer.RenderBodyParts(m, d, n => n.Bounds);
        var after = Meridian.Serialization.SvgRenderer.RenderBodyParts(m, d, n => ReferenceEquals(n, moved) ? n.Bounds with { X = n.Bounds.X + 40 } : n.Bounds);
        Assert.Equal(before.Count, after.Count);
        var changed = Enumerable.Range(0, before.Count).Where(i => before[i] != after[i]).ToList();
        var touching = d.Edges.Count(e => ReferenceEquals(e.Source, moved) || ReferenceEquals(e.Target, moved));
        Assert.Contains(changed, i => after[i].Contains($"data-node=\"{moved.Id}\""));
        // Its own piece, its connectors, and at most a few others whose routes step around it.
        Assert.InRange(changed.Count, 1, 1 + touching + 3);
        Assert.True(changed.Count < before.Count / 2 || before.Count < 8, $"{changed.Count} of {before.Count} pieces changed");
    }

    [Fact]
    public void Visual_Effects_Is_A_Device_Setting_Kept_In_The_Cookie()
    {
        var reduced = new DevicePreferences(Density.Compact, FontScale.Large, VisualEffects.Reduced);
        Assert.Equal(reduced, DevicePreferences.FromCookie(reduced.ToCookie()));
        // Cookies written before the setting existed keep the automatic choice.
        Assert.Equal(new DevicePreferences(Density.Compact, FontScale.Small), DevicePreferences.FromCookie("compact.small"));
        Assert.Equal(VisualEffects.Automatic, DevicePreferences.FromCookie("comfortable.normal.bogus").Effects);
    }

    [Fact]
    public void Visual_Effects_Adds_The_Classes_App_Css_Reads()
    {
        var prefs = new UserPreferences();
        Assert.DoesNotContain("effects", ThemeService.ClassesFor(true, prefs, new DevicePreferences(), ""));
        Assert.Contains("reduce-effects", ThemeService.ClassesFor(true, prefs, new DevicePreferences(Effects: VisualEffects.Reduced), "").Split(' '));
        Assert.Contains("m-reduce-effects", ThemeService.ClassesFor(true, prefs, new DevicePreferences(Effects: VisualEffects.Reduced), "m-").Split(' '));
        Assert.Contains("full-effects", ThemeService.ClassesFor(true, prefs, new DevicePreferences(Effects: VisualEffects.Full), "").Split(' '));
    }
}
