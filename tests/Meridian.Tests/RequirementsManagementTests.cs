using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Core.Validation;
using Meridian.Serialization;

namespace Meridian.Tests;

public class RequirementsManagementTests
{
    private static Element Req(ModelRepository m, string id) => m.OfKind(ElementKind.Requirement).Single(r => r.Get(FieldKeys.RequirementId) == id);

    // ------------------------------------------------------------------ ids

    [Fact]
    public void New_Requirements_Use_The_Configured_Prefix_And_Renumber_Is_One_Undo_Step()
    {
        var m = new ModelRepository();
        var pkg = m.Create(m.Root, ElementKind.Package, "Reqs");
        Assert.Equal("REQ-001", m.Create(pkg, ElementKind.Requirement).Get(FieldKeys.RequirementId));
        RequirementIds.SetPrefix(m, "SYS-");
        var b = m.Create(pkg, ElementKind.Requirement);
        Assert.Equal("SYS-002", b.Get(FieldKeys.RequirementId));

        var child = m.Create(b, ElementKind.Requirement);
        m.Set(child, FieldKeys.RequirementId, "X-77");
        Assert.Equal(2, RequirementIds.Renumber(m));   // SYS-002 already in place
        Assert.Equal(["SYS-001", "SYS-002", "SYS-003"], RequirementIds.InDocumentOrder(m).Select(r => r.Get(FieldKeys.RequirementId)));
        m.History.Undo();
        Assert.Equal("X-77", child.Get(FieldKeys.RequirementId));
    }

    [Fact]
    public void Requirement_Attributes_Are_Fields_That_Undo_And_Save()
    {
        var m = SampleModels.Vehicle();
        var r = Req(m, "REQ-002");
        m.Set(r, ReqFields.Status, "approved");
        m.Set(r, ReqFields.CustomPrefix + "Safety level", "ASIL B");
        var copy = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        Assert.Equal("approved", Req(copy, "REQ-002").Get(ReqFields.Status));
        Assert.Equal(["Safety level"], ReqFields.CustomAttributeNames(copy));
        m.History.Undo();
        m.History.Undo();
        Assert.Equal("", r.Get(ReqFields.Status));
    }

    // ------------------------------------------------------------------ suspect links

    [Fact]
    public void Editing_Requirement_Text_Makes_Its_Trace_Links_Suspect_And_Undo_Clears_It()
    {
        var m = SampleModels.Vehicle();
        var accel = Req(m, "REQ-002");
        Assert.Empty(SuspectLinks.All(m));
        var traceCount = m.RelationshipsOf(accel).Count(r => SuspectLinks.IsTraceKind(r.Kind));

        m.Set(accel, FieldKeys.Text, "The vehicle shall accelerate from 0 to 100 km/h in less than 7 s.");
        var suspect = SuspectLinks.Of(m, accel).ToList();
        Assert.Equal(traceCount, suspect.Count);
        Assert.Contains(suspect, r => r.Kind == ElementKind.Satisfy);
        Assert.Contains(suspect, r => r.Kind == ElementKind.Verify);
        Assert.Contains(suspect, r => r.Kind == ElementKind.DeriveReqt);
        Assert.Contains(new ModelValidator().Validate(m), i => i.RuleId == "TRC-001");

        m.History.Undo();                 // one step undoes the text and the baseline together
        Assert.Empty(SuspectLinks.All(m));
        m.History.Redo();
        Assert.Equal(traceCount, SuspectLinks.All(m).Count());
    }

    [Fact]
    public void Clearing_Suspect_Links_Rebaselines_And_Further_Edits_Flag_Again()
    {
        var m = SampleModels.Vehicle();
        var braking = Req(m, "REQ-004");
        m.Rename(braking, "Emergency braking");
        var link = Assert.Single(SuspectLinks.Of(m, braking), r => r.Kind == ElementKind.Satisfy);
        SuspectLinks.Clear(m, link);
        Assert.False(SuspectLinks.IsSuspect(link));
        Assert.Equal(1, SuspectLinks.ClearAll(m));    // the verify link
        Assert.Empty(SuspectLinks.All(m));

        m.Set(braking, FieldKeys.Text, "Stop within 38 m.");
        Assert.True(SuspectLinks.IsSuspect(link));
        Assert.Contains("REQ-004", SuspectLinks.Describe(link));
        m.Set(braking, FieldKeys.Documentation, "only documentation");   // not watched
        m.History.Undo();
        m.History.Undo();
        Assert.False(SuspectLinks.IsSuspect(link));
    }

    [Fact]
    public void Suspect_Fingerprints_Replicate_And_Save()
    {
        var json = ProjectFile.Serialize(SampleModels.Vehicle());
        var a = ProjectFile.Deserialize(json);
        var b = ProjectFile.Deserialize(json);
        a.OpEmitted += op => b.ApplyRemote(op);
        a.Set(Req(a, "REQ-005"), FieldKeys.Text, "The engine shall deliver at least 160 kW.");
        Assert.Equal(ProjectFile.Serialize(a), ProjectFile.Serialize(b));
        Assert.NotEmpty(SuspectLinks.All(b));
        Assert.NotEmpty(SuspectLinks.All(ProjectFile.Deserialize(ProjectFile.Serialize(a))));
    }

    // ------------------------------------------------------------------ traceability table

    [Fact]
    public void Traceability_Table_Lists_Links_Coverage_And_Gaps()
    {
        var m = SampleModels.Vehicle();
        var rows = TraceabilityTable.Build(m);
        Assert.Equal(m.OfKind(ElementKind.Requirement).Count(), rows.Count);
        var accel = rows.Single(r => r.Id == "REQ-002");
        Assert.Contains(accel[TraceColumn.SatisfiedBy], l => l.Other.Name == "Vehicle");
        Assert.Contains(accel[TraceColumn.VerifiedBy], l => l.Other.Name == "Acceleration Test");
        Assert.Contains(accel[TraceColumn.DerivedTo], l => l.Other.Get(FieldKeys.RequirementId) == "REQ-005");
        Assert.Equal(TraceCoverage.Full, accel.Coverage);
        var range = rows.Single(r => r.Id == "REQ-003");
        Assert.True(range.VerifyGap);
        Assert.Contains(TraceabilityTable.Apply(rows, new TraceFilter(NotVerified: true)), r => r.Id == "REQ-003");
        Assert.DoesNotContain(TraceabilityTable.Apply(rows, new TraceFilter(NotVerified: true)), r => r.Id == "REQ-002");
        Assert.Single(TraceabilityTable.Apply(rows, new TraceFilter(Text: "600 km")));
        Assert.NotEmpty(TraceabilityTable.CoverageByPackage(rows));
    }

    [Fact]
    public void Cell_Links_Are_Created_Through_The_Repository_And_Undo()
    {
        var m = SampleModels.Vehicle();
        var range = Req(m, "REQ-003");
        var test = m.Create(m.Root, ElementKind.TestCase, "Range Test");
        m.Set(test, ReqFields.Verdict, "fail");
        Assert.Contains(test, TraceabilityTable.Candidates(m, range, TraceColumn.VerifiedBy));
        var link = TraceabilityTable.Link(m, range, TraceColumn.VerifiedBy, test);
        Assert.Same(range, link.Target);
        var row = TraceabilityTable.Build(m).Single(r => r.Id == "REQ-003");
        Assert.False(row.VerifyGap);
        Assert.DoesNotContain(test, TraceabilityTable.Candidates(m, range, TraceColumn.VerifiedBy));
        Assert.Contains(TraceabilityTable.Chains(m), c => ReferenceEquals(c.Test, test) && c.Verdict == "fail");
        m.History.Undo();
        Assert.False(m.Contains(link));
    }

    [Fact]
    public void Orphan_Design_Elements_And_Chains()
    {
        var m = SampleModels.Vehicle();
        var orphans = TraceabilityTable.OrphanDesignElements(m);
        Assert.DoesNotContain(orphans, b => b.Name == "Vehicle");
        var lonely = m.Create(m.Root, ElementKind.Block, "Lonely");
        Assert.Contains(lonely, TraceabilityTable.OrphanDesignElements(m));

        var chains = TraceabilityTable.Chains(m);
        Assert.Contains(chains, c => c.Requirement.Get(FieldKeys.RequirementId) == "REQ-001" && c.Derived?.Get(FieldKeys.RequirementId) == "REQ-002");
        Assert.Contains(chains, c => c.Derived?.Get(FieldKeys.RequirementId) == "REQ-003" && c.Gap);
    }

    // ------------------------------------------------------------------ spreadsheets

    [Fact]
    public void Xlsx_Round_Trip_Diff_And_Apply()
    {
        var m = SampleModels.Vehicle();
        m.Set(Req(m, "REQ-002"), ReqFields.Priority, "high");
        var bytes = RequirementsSheet.ExportXlsx(m);
        var rows = Xlsx.Read(new MemoryStream(bytes));
        Assert.Equal("Id", rows[0][0]);
        Assert.Empty(RequirementsSheet.Diff(m, rows).Rows);     // unchanged round trip

        var header = rows[0];
        int Col(string h) => header.IndexOf(h);
        var accelRow = rows.First(r => r.Count > 0 && r[0] == "REQ-002");
        while (accelRow.Count < header.Count) accelRow.Add("");
        accelRow[Col("Status")] = "approved";
        accelRow[Col("Priority")] = "critical";
        rows.Remove(rows.First(r => r.Count > 0 && r[0] == "REQ-003"));
        var added = Enumerable.Repeat("", header.Count).ToList();
        added[Col("Id")] = "REQ-100";
        added[Col("Name")] = "Noise";
        added[Col("Text")] = "Interior noise below 70 dB.";
        added[Col("Parent Id")] = "REQ-001";
        rows.Add(added);

        var reread = Xlsx.Read(new MemoryStream(Xlsx.Write(new Xlsx.Sheet("R", rows[0], rows.Skip(1).Select(r => (IReadOnlyList<string>)r).ToList()))));
        var diff = RequirementsSheet.Diff(m, reread);
        var changed = Assert.Single(diff.Rows, r => r.Kind == SheetChangeKind.Changed);
        Assert.Equal(2, changed.Fields.Count);
        var add = Assert.Single(diff.Rows, r => r.Kind == SheetChangeKind.Added);
        var rem = Assert.Single(diff.Rows, r => r.Kind == SheetChangeKind.Removed);
        Assert.False(rem.Accept);
        Assert.True(add.Accept);

        var target = m.Root.OwnedElements.Single(e => e.Name == "Requirements");
        var before = ProjectFile.Serialize(m);
        var (a, c, r) = RequirementsSheet.Apply(m, target, diff.Rows);
        Assert.Equal((1, 1, 0), (a, c, r));
        Assert.Equal("approved", Req(m, "REQ-002").Get(ReqFields.Status));
        Assert.Same(Req(m, "REQ-001"), Req(m, "REQ-100").Owner);
        Assert.Contains(m.OfKind(ElementKind.Requirement), x => x.Get(FieldKeys.RequirementId) == "REQ-003");
        m.History.Undo();
        Assert.Equal(before, ProjectFile.Serialize(m));
    }

    [Fact]
    public void Csv_Rows_Use_The_Same_Diff_And_Traceability_Exports()
    {
        var m = SampleModels.Vehicle();
        var csv = RequirementsSheet.ExportCsv(m);
        var rows = RequirementsSheet.ReadRows("r.csv", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv)));
        Assert.Empty(RequirementsSheet.Diff(m, rows).Rows);

        var table = TraceabilityExport.ToCsv(TraceabilityTable.Build(m));
        Assert.StartsWith("Id,Name,Text,Derived from", table);
        Assert.Contains("Acceleration Test", table);
        var book = Xlsx.Read(new MemoryStream(TraceabilityExport.ToXlsx(m)));
        Assert.Equal("Satisfied by", book[0][6]);
        Assert.Equal(m.OfKind(ElementKind.Requirement).Count() + 1, book.Count);
    }
}
