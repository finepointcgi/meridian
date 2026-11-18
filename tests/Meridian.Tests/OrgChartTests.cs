using System.Text;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;
using Meridian.Serialization.Spreadsheets;

namespace Meridian.Tests;

/// <summary>Org charts from spreadsheets: column detection, manager resolution, layout, the diagram and re-import.</summary>
public class OrgChartTests
{
    private const string Csv = """
        Employee ID,Full Name,Job Title,Reports To,Department,Email,Photo URL
        1,Ada Lovelace,CEO,,Leadership,ada@example.com,http://x/a.png
        2,Grace Hopper,CTO,1,Engineering,,
        3,Alan Turing,Engineer,2,Engineering,,
        4,Katherine Johnson,Engineer,Grace Hopper,Engineering,,
        5,Mary Jackson,CFO,1,Finance,,
        """;

    private static OrgSheet Read(string csv) => OrgChartSheet.Read("people.csv", Encoding.UTF8.GetBytes(csv));

    [Fact]
    public void Columns_Are_Detected_By_Header_Synonyms()
    {
        var c = OrgChartSheet.DetectColumns(["Employee ID", "Full Name", "Job Title", "Reports To", "Department", "E-mail", "Photo URL"]);
        Assert.Equal(0, c[OrgColumn.Id]);
        Assert.Equal(1, c[OrgColumn.Name]);
        Assert.Equal(2, c[OrgColumn.Title]);
        Assert.Equal(3, c[OrgColumn.Manager]);
        Assert.Equal(4, c[OrgColumn.Department]);
        Assert.Equal(5, c[OrgColumn.Email]);
        Assert.Equal(6, c[OrgColumn.Photo]);

        foreach (var m in new[] { "manager", "SUPERVISOR", "Parent", "reports_to", "Manager (name)" })
            Assert.Equal(1, OrgChartSheet.DetectColumns(["Name", m])[OrgColumn.Manager]);
        var split = OrgChartSheet.DetectColumns(["First name", "Surname", "Role", "Team"]);
        Assert.Equal((0, 1, 2, 3), (split[OrgColumn.FirstName], split[OrgColumn.LastName], split[OrgColumn.Title], split[OrgColumn.Department]));
    }

    [Fact]
    public void Managers_Resolve_By_Id_Or_Name()
    {
        var s = Read(Csv);
        Assert.Empty(s.Warnings);
        Assert.Equal(5, s.People.Count);
        var t = s.Tree;
        Assert.Equal(["1"], t.Roots);
        Assert.Equal("1", t.ManagerOf["2"]);
        Assert.Equal("2", t.ManagerOf["4"]);   // "Grace Hopper" by name
        Assert.Equal(["2", "5"], t.ReportsOf["1"]);
        Assert.Equal("ada@example.com", t["1"].Email);
    }

    [Fact]
    public void Unknown_Managers_Self_And_Loops_Are_Warnings()
    {
        var s = Read("""
            Name,Manager
            Boss,
            A,Nobody
            B,B
            C,D
            D,E
            E,C
            F,Boss
            """);
        Assert.Equal(7, s.People.Count);
        Assert.Contains(s.Warnings, w => w.Contains("“Nobody”") && w.StartsWith("Row 3"));
        Assert.Contains(s.Warnings, w => w.Contains("own manager"));
        Assert.Contains(s.Warnings, w => w.Contains("loop") && w.Contains("C, D and E"));
        var t = s.Tree;
        Assert.Equal(["Boss", "A", "B", "C"], t.Roots);   // the loop is cut at its first row
        Assert.Equal("C", t.ManagerOf["E"]);
        Assert.Equal("Boss", t.ManagerOf["F"]);
    }

    [Fact]
    public void Missing_Columns_And_Duplicates_Are_Reported()
    {
        var s = Read("""
            Person,Position
            Kim,Lead
            Kim,Engineer
            ,Nobody
            """);
        Assert.Contains(s.Warnings, w => w.Contains("No “Manager”"));
        Assert.Contains(s.Warnings, w => w.Contains("two people are called “Kim”"));
        Assert.Contains(s.Warnings, w => w.Contains("no name"));
        Assert.Equal(["Kim", "Kim (2)"], s.Tree.Roots);

        var noName = Read("Who,Reports to\nX,\nY,X\n");
        Assert.Contains(noName.Warnings, w => w.Contains("using “Who”"));
        Assert.Equal("X", noName.Tree.ManagerOf["Y"]);
    }

    [Fact]
    public void Layout_Is_A_Top_Down_Tree_Without_Overlaps()
    {
        var t = Read(Csv).Tree;
        var b = OrgChart.Layout(t);
        Assert.Equal(5, b.Count);
        // Levels go down; managers are centred over their reports.
        Assert.True(b["1"].Y < b["2"].Y && b["2"].Y < b["3"].Y);
        Assert.Equal(b["2"].Y, b["5"].Y);
        Assert.Equal(b["2"].Center.X, (b["3"].Center.X + b["4"].Center.X) / 2, 3);
        Assert.Equal(b["1"].Center.X, (b["2"].Center.X + b["5"].Center.X) / 2, 3);
        AssertNoOverlap(b.Values.ToList());
    }

    [Fact]
    public void Many_Direct_Reports_Stack_Compactly_And_Roots_Sit_Side_By_Side()
    {
        var people = new List<OrgPerson> { new("Boss") };
        for (var i = 0; i < 12; i++) people.Add(new($"P{i}", Manager: "Boss"));
        people.Add(new("Other root"));
        var t = OrgChart.Resolve(people);
        var b = OrgChart.Layout(t);
        AssertNoOverlap(b.Values.ToList());
        var reports = Enumerable.Range(0, 12).Select(i => b[$"P{i}"]).ToList();
        // Two columns, not a row of twelve.
        Assert.Equal(2, reports.Select(r => r.X).Distinct().Count());
        Assert.True(reports.Max(r => r.Right) - reports.Min(r => r.X) < 3 * OrgChart.CardWidth);
        Assert.True(b["Other root"].X > reports.Max(r => r.Right));
        Assert.Equal(b["Boss"].Y, b["Other root"].Y);
    }

    [Fact]
    public void Deep_Chains_Lay_Out_Without_Recursion()
    {
        var people = Enumerable.Range(0, 20_000).Select(i => new OrgPerson($"P{i}", Manager: i == 0 ? null : $"P{i - 1}")).ToList();
        var b = OrgChart.Layout(OrgChart.Resolve(people));
        Assert.Equal(20_000, b.Count);
        Assert.True(b["P19999"].Y > b["P0"].Y);
    }

    private static void AssertNoOverlap(List<Bounds> all)
    {
        for (var i = 0; i < all.Count; i++)
            for (var j = i + 1; j < all.Count; j++)
                Assert.False(all[i].Intersects(all[j]), $"{all[i]} overlaps {all[j]}");
    }

    [Fact]
    public void Build_Draws_Cards_And_Lines_Renders_And_Undoes()
    {
        var m = new ModelRepository("Org");
        var r = OrgChart.Build(m, m.Root, Read(Csv).Tree, "People", "people.csv");
        var d = r.Diagram;
        Assert.Equal(DiagramType.Whiteboard, d.DiagramType);
        Assert.Equal(5, r.Added);
        Assert.Equal(5, d.Nodes.Count(n => OrgChart.IsCard(n.Element)));
        Assert.Equal(4, d.Edges.Count);
        Assert.Equal("Grace Hopper", d.Edges.Single(e => e.Target.Element.Name == "Alan Turing").Source.Element.Name);
        var grace = d.Nodes.Single(n => n.Element.Name == "Grace Hopper");
        Assert.Equal("CTO", grace.Element.Get(OrgChart.TitleField));
        Assert.Equal("Engineering", grace.Element.Get(OrgChart.DepartmentField));
        Assert.Equal(EdgeRouting.Orthogonal, EdgeRouter.StyleOf(d, d.Edges[0]));
        // Departments get distinct band colours; people of one department share theirs.
        var turing = d.Nodes.Single(n => n.Element.Name == "Alan Turing");
        var mary = d.Nodes.Single(n => n.Element.Name == "Mary Jackson");
        Assert.Equal(grace.Style[OrgChart.AccentStyle], turing.Style[OrgChart.AccentStyle]);
        Assert.NotEqual(grace.Style[OrgChart.AccentStyle], mary.Style[OrgChart.AccentStyle]);

        var svg = SvgRenderer.Render(m, d);
        Assert.Contains("font-weight=\"600\">Grace Hopper</text>", svg);
        Assert.Contains(">CTO</text>", svg);
        Assert.Contains(">Finance</text>", svg);

        // Saved and loaded, and one undo step.
        var back = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        Assert.Equal(5, back.Diagrams.Single().Nodes.Count(n => OrgChart.IsCard(n.Element)));
        m.History.Undo();
        Assert.Empty(m.Diagrams);
    }

    [Fact]
    public void Reimport_Updates_The_Same_Diagram_In_Place()
    {
        var m = new ModelRepository("Org");
        var first = OrgChart.Build(m, m.Root, Read(Csv).Tree, "People", "people.csv").Diagram;
        var graceNode = first.Nodes.Single(n => n.Element.Name == "Grace Hopper");

        // Grace is promoted, Mary leaves, Joan joins under Grace, Alan now reports to Ada.
        var changed = """
            Employee ID,Full Name,Job Title,Reports To,Department
            1,Ada Lovelace,CEO,,Leadership
            2,Grace Hopper,Chief Scientist,1,Engineering
            3,Alan Turing,Engineer,1,Engineering
            4,Katherine Johnson,Engineer,2,Engineering
            6,Joan Clarke,Analyst,2,Engineering
            """;
        var r = OrgChart.Build(m, m.Root, Read(changed).Tree, "ignored", "people.csv");
        Assert.True(r.Replaced);
        Assert.Same(first, r.Diagram);
        Assert.Single(m.Diagrams);
        Assert.Equal((1, 1, 1), (r.Added, r.Updated, r.Removed));
        Assert.Same(graceNode, first.Nodes.Single(n => n.Element.Name == "Grace Hopper"));
        Assert.Equal("Chief Scientist", graceNode.Element.Get(OrgChart.TitleField));
        Assert.DoesNotContain(first.Nodes, n => n.Element.Name == "Mary Jackson");
        Assert.DoesNotContain(m.AllElements, e => e.Name == "Mary Jackson");
        string Mgr(string name) => first.Edges.Single(e => e.Target.Element.Name == name).Source.Element.Name;
        Assert.Equal("Ada Lovelace", Mgr("Alan Turing"));
        Assert.Equal("Grace Hopper", Mgr("Joan Clarke"));
        Assert.Equal(4, first.Edges.Count);

        m.History.Undo();
        Assert.Equal("CTO", graceNode.Element.Get(OrgChart.TitleField));
        Assert.Contains(first.Nodes, n => n.Element.Name == "Mary Jackson");
    }

    [Fact]
    public void Org_Chart_Template_Exists()
    {
        var t = Templates.FindDiagram("orgchart")!;
        var m = new ModelRepository("T");
        var d = t.Create(m, m.Root);
        Assert.Equal(DiagramType.Whiteboard, d.DiagramType);
        Assert.Equal(8, d.Nodes.Count(n => OrgChart.IsCard(n.Element)));
        Assert.Equal(7, d.Edges.Count);
    }
}
