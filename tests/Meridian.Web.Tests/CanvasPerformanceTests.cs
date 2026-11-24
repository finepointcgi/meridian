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
