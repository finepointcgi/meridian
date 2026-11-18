using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization;
using Meridian.Serialization.Printing;

namespace Meridian.Tests;

/// <summary>Colour rules on diagrams: evaluation, editing (undo), rendering, legend and persistence.</summary>
public class ColorRulesTests
{
    private static (ModelRepository M, Diagram D, DiagramNode Engine, DiagramNode Wheel, DiagramNode Req) Model()
    {
        var m = new ModelRepository("Car");
        var d = m.CreateDiagram(m.Root, DiagramType.BlockDefinition, "Structure");
        var engine = m.Create(m.Root, ElementKind.Block, "Engine");
        m.Set(engine, FieldKeys.TagPrefix + "ASIL", "D");
        m.Set(engine, FieldKeys.TagPrefix + "mass", "1.2 t");
        var wheel = m.Create(m.Root, ElementKind.Block, "Wheel");
        m.Set(wheel, FieldKeys.TagPrefix + "ASIL", "B");
        m.Set(wheel, FieldKeys.TagPrefix + "mass", "900 kg");
        var req = m.Create(m.Root, ElementKind.Requirement, "Top speed");
        m.Set(req, VerificationKeys.Status, "fail");
        return (m, d, m.AddNode(d, engine, new Bounds(0, 0, 160, 80)), m.AddNode(d, wheel, new Bounds(240, 0, 160, 80)),
                m.AddNode(d, req, new Bounds(0, 200, 200, 90)));
    }

    [Fact]
    public void First_Matching_Rule_Wins()
    {
        var (m, d, engine, wheel, req) = Model();
        m.SetColorRules(d,
        [
            new ColorRule("Failed", "kind:Requirement where verificationResult contains fail", Fill: "#ff0000"),
            new ColorRule("ASIL D", "where tag:ASIL = D", Fill: "#ffaa00", Badge: "D"),
            new ColorRule("Blocks", "kind:Block", Fill: "#00ff00"),
        ]);
        var r = ColorRules.Evaluate(m, d);
        Assert.Equal("Failed", r.ForNode(req.Id)!.Label);
        Assert.Equal("ASIL D", r.ForNode(engine.Id)!.Label);
        Assert.Equal("Blocks", r.ForNode(wheel.Id)!.Label);
        Assert.Equal([1, 1, 1], ColorRules.MatchCounts(m, d));
    }

    [Fact]
    public void Conditions_Use_The_Query_Language_With_Units()
    {
        var (m, _, engine, wheel, req) = Model();
        bool Matches(string q, DiagramNode n) => ModelQuery.Parse(q).Matches(m, n.Element);
        Assert.True(Matches("where tag:mass > \"1000 kg\"", engine));        // 1.2 t = 1200 kg
        Assert.False(Matches("where tag:mass > \"1000 kg\"", wheel));
        Assert.True(Matches("where tag:mass <= 900", wheel));                 // a bare number compares with the number
        Assert.True(Matches("where tag:ASIL != D", wheel));
        Assert.True(Matches("where tag:ASIL is not empty", engine));
        Assert.True(Matches("where tag:owner is empty", engine));
        Assert.False(Matches("where name contains wheel", engine));
        Assert.NotNull(ColorRules.QueryError("where mass >"));
        Assert.Null(ColorRules.QueryError("kind:Block where mass > 5"));
    }

    [Fact]
    public void Edits_Are_Single_Undo_Steps_And_Unsafe_Values_Are_Dropped()
    {
        var (m, d, _, _, _) = Model();
        m.AddColorRule(d, new ColorRule("A", "kind:Block", Fill: "#123456"));
        m.AddColorRule(d, new ColorRule("B", "kind:Requirement", Fill: "red\" onload=\"x", Line: "#00f", Badge: "very long badge text"));
        Assert.Equal(["A", "B"], ColorRules.Of(d).Select(r => r.Label));
        var b = ColorRules.Of(d)[1];
        Assert.Null(b.Fill);
        Assert.Equal("#00f", b.Line);
        Assert.Equal(ColorRules.MaxBadgeLength, b.Badge!.Length);

        m.MoveColorRule(d, 1, -1);
        Assert.Equal(["B", "A"], ColorRules.Of(d).Select(r => r.Label));
        m.History.Undo();
        Assert.Equal(["A", "B"], ColorRules.Of(d).Select(r => r.Label));
        m.RemoveColorRule(d, 0);
        Assert.Single(ColorRules.Of(d));
        m.History.Undo();
        Assert.Equal(2, ColorRules.Of(d).Count);
        m.History.Undo(); m.History.Undo();
        Assert.Empty(ColorRules.Of(d));
        Assert.Equal("", d.Get(ColorRules.Field));
    }

    [Fact]
    public void Simple_Conditions_Round_Trip_Through_The_Builder()
    {
        var c = new ColorRules.SimpleCondition(ElementKind.Requirement, ModelValues.VerificationResultKey, QueryOp.Contains, "fail");
        var q = c.ToQuery();
        Assert.Equal("kind:Requirement where verificationResult contains fail", q);
        Assert.Equal(c, ColorRules.AsSimple(q));
        Assert.Equal(new ColorRules.SimpleCondition(null, "tag:ASIL", QueryOp.Eq, "D"), ColorRules.AsSimple("where tag:ASIL = D"));
        Assert.Null(ColorRules.AsSimple("kind:Block where a = 1 and b = 2"));
        Assert.Null(ColorRules.AsSimple("kind:Block in:Pkg"));
        Assert.Equal(new ColorRules.SimpleCondition(null, null), ColorRules.AsSimple(""));
    }

    [Fact]
    public void Rule_Colours_Override_Manual_Colours_In_Svg_With_Badge_And_Legend()
    {
        var (m, d, engine, wheel, req) = Model();
        m.SetNodeStyle(d, engine, "fill", "#abcdef");
        m.SetNodeStyle(d, engine, "stroke", "#111111");
        m.SetNodeStyle(d, wheel, "fill", "#fedcba");
        m.SetColorRules(d, [new ColorRule("Safety critical", "where tag:ASIL = D", Fill: "#e01010", Badge: "D!")]);
        var svg = SvgRenderer.Render(m, d);
        Assert.Contains("#e01010", svg);
        Assert.DoesNotContain("#abcdef", svg);          // the rule's fill replaces the manual one…
        Assert.Contains("#111111", svg);                // …but a part the rule doesn't set keeps the manual colour
        Assert.Contains("#fedcba", svg);                // shapes no rule matches are untouched
        Assert.Contains("rule-badge", svg);
        Assert.Contains("D!", svg);
        Assert.DoesNotContain("Safety critical", svg.Replace("<title>Safety critical</title>", ""));   // no legend yet

        m.Set(d, FieldKeys.Legend, "true");
        svg = SvgRenderer.Render(m, d);
        Assert.Contains("legend-rule", svg);
        Assert.Contains(">Safety critical<", svg);

        // A rule that matches nothing is not listed.
        m.SetColorRules(d, [new ColorRule("Nothing", "kind:Actor", Fill: "#e01010")]);
        Assert.DoesNotContain("legend-rule", SvgRenderer.Render(m, d));
        Assert.DoesNotContain("#e01010", SvgRenderer.Render(m, d));

    }

    [Fact]
    public void Rules_Apply_To_Paths_And_In_Print()
    {
        var (m, d, engine, wheel, _) = Model();
        var rel = m.Connect(ElementKind.Association, engine.Element, wheel.Element);
        m.AddEdge(d, rel);
        m.SetColorRules(d, [new ColorRule("Links", "kind:Association", Line: "#0a0b0c")]);
        Assert.Contains("stroke=\"#0a0b0c\"", SvgRenderer.Render(m, d));
        m.SetColorRules(d, [new ColorRule("Blocks", "kind:Block", Fill: "#c0ffee")]);
        var print = PrintSource.Create(m, d, new PageSetup());
        Assert.Contains("#c0ffee", print.Inner);
    }

    [Fact]
    public void Rules_Survive_Project_File_And_Xmi()
    {
        var (m, d, _, _, _) = Model();
        var rules = new[] { new ColorRule("Failed", "kind:Requirement where verificationResult contains fail", "#ff0000", "#800000", "#ffffff", "✗") };
        m.SetColorRules(d, rules);

        var back = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        Assert.Equal(rules, ColorRules.Of((Diagram)back.Find(d.Id)!));

        var (viaXmi, _) = XmiExchange.Import(XmiExchange.ToXml(m));
        var xd = viaXmi.Diagrams.Single(x => x.Name == "Structure");
        Assert.Equal(rules, ColorRules.Of(xd));
    }

    [Fact]
    public void Malformed_Rules_Are_Ignored()
    {
        var (m, d, _, _, _) = Model();
        m.Set(d, ColorRules.Field, "not json");
        Assert.Empty(ColorRules.Of(d));
        Assert.DoesNotContain("rule-badge", SvgRenderer.Render(m, d));
        m.Set(d, ColorRules.Field, "[{\"label\":\"x\",\"query\":\"where (\",\"fill\":\"#ff0000\"}, 5]");
        Assert.Single(ColorRules.Of(d));
        Assert.DoesNotContain("#ff0000", SvgRenderer.Render(m, d));   // an invalid condition matches nothing
    }
}
