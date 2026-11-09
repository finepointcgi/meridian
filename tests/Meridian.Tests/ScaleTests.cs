using System.Diagnostics;
using Meridian.Core.Model;
using Meridian.Core.Validation;
using Meridian.Serialization;

namespace Meridian.Tests;

public class ScaleTests
{
    /// <summary>~10,000 elements: blocks with properties, requirements, satisfy links, and a 400-shape diagram.</summary>
    private static ModelRepository BigModel()
    {
        var m = new ModelRepository("Big");
        var reqs = m.Create(m.Root, ElementKind.Package, "Requirements");
        var structure = m.Create(m.Root, ElementKind.Package, "Structure");
        var d = m.CreateDiagram(structure, DiagramType.BlockDefinition, "Big BDD");
        var requirements = new List<Element>();
        for (var i = 0; i < 1000; i++)
        {
            var r = m.Create(reqs, ElementKind.Requirement, $"Requirement {i}");
            m.Set(r, FieldKeys.Text, $"The system shall do thing {i}.");
            requirements.Add(r);
        }
        for (var i = 0; i < 1500; i++)
        {
            var b = m.Create(structure, ElementKind.Block, $"Block{i}");
            for (var p = 0; p < 4; p++) { var prop = m.Create(b, ElementKind.Property, $"p{p}"); m.Set(prop, FieldKeys.Type, "Real"); }
            m.Connect(ElementKind.Satisfy, b, requirements[i % requirements.Count]);
            if (i < 400) m.AddNode(d, b, new Bounds(i % 20 * 220, i / 20 * 160, 180, 120), showRelationships: false);
        }
        m.History.Clear();
        return m;
    }

    [Fact]
    public void Ten_Thousand_Elements_Stay_Fast()
    {
        var sw = Stopwatch.StartNew();
        var m = BigModel();
        var build = sw.Elapsed; sw.Restart();
        Assert.True(m.AllElements.Count() > 9_000);

        var issues = new ModelValidator().Validate(m);
        var validate = sw.Elapsed; sw.Restart();
        var json = ProjectFile.Serialize(m);
        var back = ProjectFile.Deserialize(json);
        var roundTrip = sw.Elapsed; sw.Restart();
        var d = back.Diagrams.Single();
        var sb = new System.Text.StringBuilder();
        SvgRenderer.RenderBody(sb, back, d, n => n.Bounds, null, null, new Bounds(0, 0, 1600, 900));
        var culledRender = sw.Elapsed;

        Assert.True(validate.TotalSeconds < 5, $"validation took {validate}");
        Assert.True(roundTrip.TotalSeconds < 5, $"save/load took {roundTrip}");
        Assert.True(culledRender.TotalSeconds < 2, $"render took {culledRender}");
        Assert.True(sb.Length < 400_000, "viewport culling should limit the markup");
        _ = (build, issues);
    }
}
