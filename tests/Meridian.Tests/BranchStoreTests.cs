using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>Baseline diff export. Store behaviour for branches and baselines is covered by StoreContract (SQLite and PostgreSQL).</summary>
public sealed class BranchStoreTests
{
    [Fact]
    public void Diff_Csv_Lists_Field_Changes_And_Guards_Formulas()
    {
        var a = SampleModels.Vehicle();
        var b = ProjectFile.Deserialize(ProjectFile.Serialize(a));
        var block = b.OfKind(ElementKind.Block).First();
        b.Set(block, FieldKeys.Documentation, "=HYPERLINK(\"x\")");
        b.Create(b.Root, ElementKind.Package, "New, \"quoted\" pkg");
        var csv = DiffCsv.Export(ModelDiff.Compare(a, b), "PDR 1.0", "Current");
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.Contains("PDR 1.0", lines[0]);
        Assert.StartsWith("Change,Element Kind,Element", lines[1]);
        Assert.Contains(lines, l => l.StartsWith("Added,") && l.Contains("\"New, \"\"quoted\"\" pkg\""));
        Assert.Contains(lines, l => l.StartsWith("Changed,") && l.Contains("documentation") && l.Contains("'=HYPERLINK"));
    }
}
