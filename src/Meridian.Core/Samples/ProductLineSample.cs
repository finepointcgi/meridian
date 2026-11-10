using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Samples;

/// <summary>
/// A small product line: a feature model with mandatory / optional / alternative / or features and cross-tree
/// constraints, configurations, presence conditions on structure, a trade study and a timed, signalling behavior.
/// Used by the Variants panel ("Add example") and by tests.
/// </summary>
public static class ProductLineSample
{
    public static ModelRepository Create()
    {
        var m = new ModelRepository("Car Product Line");
        AddTo(m, m.Root);
        return m;
    }

    /// <summary>Adds the example into <paramref name="owner"/> (a package or the model). Returns the new package.</summary>
    public static Element AddTo(ModelRepository m, Element owner)
    {
        using var _ = m.History.Transaction("Add example product line");
        var pl = m.Create(owner, ElementKind.Package, "Product Line");
        var features = m.Create(pl, ElementKind.Package, "Features");
        var structure = m.Create(pl, ElementKind.Package, "Structure");

        Element F(Element parent, string name, string kind, params (string Tag, string Value)[] tags)
        {
            var f = m.Create(parent, ElementKind.Feature, name);
            if (kind.Length > 0) m.Set(f, ExtFieldKeys.FeatureKind, kind);
            foreach (var (t, v) in tags) m.Set(f, FieldKeys.TagPrefix + t, v);
            return f;
        }

        var car = F(features, "Car", "mandatory", ("cost", "15000"), ("mass", "1100"));
        var power = F(features, "Powertrain", "mandatory");
        m.Connect(ElementKind.FeatureDecomposition, car, power);
        var electric = F(power, "Electric", "alternative", ("cost", "9000"), ("mass", "420"), ("range", "450"));
        var combustion = F(power, "Combustion", "alternative", ("cost", "3500"), ("mass", "250"), ("range", "800"));
        var info = F(features, "Infotainment", "mandatory");
        m.Connect(ElementKind.FeatureDecomposition, car, info);
        var basic = F(info, "Basic", "alternative", ("cost", "300"), ("mass", "4"));
        var premium = F(info, "Premium", "alternative", ("cost", "1800"), ("mass", "9"));
        var gps = F(features, "GPS", "optional", ("cost", "600"), ("mass", "1"));
        m.Connect(ElementKind.FeatureDecomposition, car, gps);
        var comfort = F(features, "Comfort", "optional");
        m.Connect(ElementKind.FeatureDecomposition, car, comfort);
        F(comfort, "HeatedSeats", "or", ("cost", "400"), ("mass", "6"));
        F(comfort, "Sunroof", "or", ("cost", "1200"), ("mass", "25"));
        var towing = F(features, "Towing", "optional", ("cost", "700"), ("mass", "30"));
        m.Connect(ElementKind.FeatureDecomposition, car, towing);
        m.Connect(ElementKind.Requires, gps, premium);
        m.Connect(ElementKind.Excludes, towing, electric);

        Element Config(string name, params Element[] selected)
        {
            var c = m.Create(features, ElementKind.Configuration, name);
            m.Set(c, ExtFieldKeys.SelectedFeatures, Variability.Serialize(selected));
            return c;
        }
        Config("Eco", car, power, electric, info, basic);
        Config("Touring", car, power, combustion, info, premium, gps, towing);

        // Structure with presence conditions (the 150% model).
        var vehicle = m.Create(structure, ElementKind.Block, "Vehicle");
        var battery = m.Create(structure, ElementKind.Block, "Battery");
        m.Set(battery, ExtFieldKeys.PresenceCondition, "Electric");
        var tank = m.Create(structure, ElementKind.Block, "FuelTank");
        m.Set(tank, ExtFieldKeys.PresenceCondition, "Combustion");
        var nav = m.Create(structure, ElementKind.Block, "NavigationUnit");
        m.Set(nav, ExtFieldKeys.PresenceCondition, "GPS and not Basic");
        var hitch = m.Create(structure, ElementKind.Block, "TowHitch");
        m.Set(hitch, ExtFieldKeys.PresenceCondition, "Towing");
        var batt = m.Create(vehicle, ElementKind.Property, "battery");
        m.Set(batt, FieldKeys.Type, battery.Id);
        m.Set(batt, ExtFieldKeys.PresenceCondition, "Electric");
        var mass = m.Create(vehicle, ElementKind.Property, "mass");
        m.Set(mass, FieldKeys.Type, "Real");
        m.Set(mass, FieldKeys.DefaultValue, "1450");
        m.Connect(ElementKind.Composition, vehicle, battery);
        m.Connect(ElementKind.Composition, vehicle, tank);
        var navLink = m.Connect(ElementKind.Composition, vehicle, nav);
        m.Set(navLink, ExtFieldKeys.PresenceCondition, "GPS");
        m.Connect(ElementKind.Composition, vehicle, hitch);

        var fm = m.CreateDiagram(features, DiagramType.FeatureModel, "Car features");
        foreach (var (f, _) in Variability.Tree(m)) m.AddNode(fm, f, new Bounds(0, 0, 130, 56));
        foreach (var r in m.Relationships.Where(r => r.Kind is ElementKind.FeatureDecomposition or ElementKind.Requires or ElementKind.Excludes)) m.AddEdge(fm, r);
        EnsureDecompositionEdges(m, fm);
        m.SetBounds(fm, AutoLayout.Layered(fm), "Arrange");
        return pl;
    }

    /// <summary>
    /// Makes the feature tree visible on a feature model diagram: features nested by containment get a Feature
    /// Decomposition relationship from their parent (if missing), shown when both ends are on the diagram.
    /// </summary>
    public static void EnsureDecompositionEdges(ModelRepository m, Diagram d)
    {
        foreach (var f in Variability.Features(m).Where(f => f.Owner is { Kind: ElementKind.Feature }).ToList())
        {
            var parent = f.Owner!;
            var r = m.Outgoing(parent, ElementKind.FeatureDecomposition).FirstOrDefault(x => ReferenceEquals(x.Target, f))
                    ?? m.Connect(ElementKind.FeatureDecomposition, parent, f);
            m.AddEdge(d, r);
        }
    }
}
