using Bunit;
using Meridian.Core.Instances;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

public class InstanceTableTests : TestContext
{
    private readonly Workspace _ws;
    private readonly Element _vehicle;

    public InstanceTableTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ws = Services.GetRequiredService<Workspace>();
        _ws.UseLocalModel(SampleModels.Vehicle());
        _vehicle = _ws.Model.OfKind(ElementKind.Block).Single(b => b.Name == "Vehicle");
        RenderComponent<MudBlazor.MudPopoverProvider>();
    }

    private Element Prop(string name) => _vehicle.OwnedElements.Single(p => p.Kind == ElementKind.Property && p.Name == name);

    private IRenderedComponent<InstanceTableTab> Render() => RenderComponent<InstanceTableTab>(p => p.Add(x => x.Classifier, _vehicle));

    private static List<AngleSharp.Dom.IElement> Rows(IRenderedComponent<InstanceTableTab> cut) => cut.FindAll(".gt-table tbody tr[data-instance]").ToList();

    [Fact]
    public void Lists_instances_with_a_column_per_feature_and_edits_slots_with_units()
    {
        var v1 = InstanceModel.CreateInstanceTree(_ws.Model, _vehicle);
        var cut = Render();
        Assert.Single(Rows(cut));
        var headers = cut.FindAll(".gt-table thead th.gt-h").Select(h => h.TextContent.Trim()).ToList();
        Assert.Equal("Instance", headers[0]);
        Assert.Contains("mass [kg]", headers);
        Assert.Contains("engine", headers);
        Assert.Equal("1450 kg", cut.Find("tr[data-instance='vehicle1'] input[data-feature='mass']").GetAttribute("value"));

        // A value in another unit is converted to the property's; the edit is one undo step.
        cut.Find("tr[data-instance='vehicle1'] input[data-feature='mass']").Change("1.6 t");
        Assert.Equal("1600 kg", InstanceTable.Cell(_ws.Model, v1, Prop("mass")));
        cut.WaitForAssertion(() => Assert.Equal("1600 kg", cut.Find("tr[data-instance='vehicle1'] input[data-feature='mass']").GetAttribute("value")));
        _ws.Model.History.Undo();
        Assert.Equal("1450 kg", InstanceTable.Cell(_ws.Model, v1, Prop("mass")));

        // A value that doesn't fit is refused with the reason; the model is unchanged.
        cut.Find("tr[data-instance='vehicle1'] input[data-feature='mass']").Change("3 m");
        cut.WaitForAssertion(() => Assert.Contains("m does not convert to kg", cut.Find("[data-testid='instance-error']").TextContent));
        Assert.Equal("1450 kg", InstanceTable.Cell(_ws.Model, v1, Prop("mass")));

        // Parts take instances by name.
        var spare = InstanceModel.CreateInstanceTree(_ws.Model, _ws.Model.OfKind(ElementKind.Block).Single(b => b.Name == "Engine"), _vehicle.Owner, "spare");
        cut.Find("tr[data-instance='vehicle1'] input[data-feature='engine']").Change("spare");
        Assert.Same(spare, Assert.Single(InstanceModel.ReferencedInstances(_ws.Model, InstanceModel.SlotFor(v1, Prop("engine"))!)));
    }

    [Fact]
    public void Rows_are_added_duplicated_and_deleted()
    {
        var cut = Render();
        Assert.Empty(Rows(cut));
        cut.Find("[data-testid='instance-add']").Click();
        cut.WaitForAssertion(() => Assert.Single(Rows(cut)));
        var v1 = _ws.Model.OfKind(ElementKind.InstanceSpecification).Single(i => i.Name == "vehicle1");
        Assert.Same(v1, _ws.Selected);   // the new row is selected

        cut.Find("[data-testid='instance-duplicate']").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, Rows(cut).Count));
        Assert.Equal("vehicle2", _ws.Selected!.Name);

        // Nested part instances are listed (and read-only as rows of their own) when "nested" is on — Vehicle has none of its type.
        Assert.Equal(["vehicle1", "vehicle2"], Rows(cut).Select(r => r.GetAttribute("data-instance")));

        Rows(cut).Single(r => r.GetAttribute("data-instance") == "vehicle2").QuerySelector("[data-testid='instance-delete']")!.Click();
        cut.WaitForAssertion(() => Assert.Single(Rows(cut)));
        Assert.DoesNotContain(_ws.Model.OfKind(ElementKind.InstanceSpecification), i => i.Name == "vehicle2");

        // Renaming a row renames the instance.
        cut.Find("input[data-testid='instance-name']").Change("fleetCar");
        Assert.Equal("fleetCar", v1.Name);
        cut.WaitForAssertion(() => Assert.Contains("1 instance", cut.Find("[data-testid='instance-count']").TextContent));
    }

    [Fact]
    public void Inspector_offers_to_delete_or_remap_a_slot_whose_feature_is_gone()
    {
        var v1 = InstanceModel.CreateInstanceTree(_ws.Model, _vehicle);
        var topSpeed = Prop("topSpeed");
        var slot = InstanceModel.SlotFor(v1, topSpeed)!;
        _ws.Model.Delete(topSpeed);   // the slot's feature no longer exists
        var speed = _ws.Model.Create(_vehicle, ElementKind.Property, "speed");
        _ws.Select(v1);
        var cut = RenderComponent<InstanceInspectorSection>(p => p.Add(x => x.E, v1));
        Assert.Contains("feature no longer exists", cut.Find("[data-testid='stale-slot']").TextContent);
        Assert.NotNull(cut.Find("[data-testid='stale-slot-remap']"));
        cut.Find("[data-testid='stale-slot-delete']").Click();
        Assert.False(_ws.Model.Contains(slot));
        _ = speed;
    }
}
