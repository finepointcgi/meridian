using System.Text;
using Meridian.Core.Analysis;
using Meridian.Core.Model;

namespace Meridian.Core.Instances;

/// <summary>A column of an instance table: one structural feature of the classifier.</summary>
public sealed record InstanceColumn(Element Feature, string Header, string? Unit, bool InstanceValued, bool EnumValued);

/// <summary>A row of an instance table: one instance, its label ("vehicle1.engine" for a nested instance) and a cell per column.</summary>
public sealed record InstanceRow(Element Instance, string Label, IReadOnlyList<string> Cells);

/// <summary>
/// The Cameo "instance table": the instances of a classifier (and its specializations) as rows, with a column per feature
/// of the classifier holding each instance's slot value. Used by the instance table tab, CSV / XLSX export and reports.
/// </summary>
public sealed class InstanceTable
{
    public Element? Classifier { get; private init; }
    public IReadOnlyList<InstanceColumn> Columns { get; private init; } = [];
    public IReadOnlyList<InstanceRow> Rows { get; private init; } = [];

    /// <summary>"vehicle1.engine": the names of the instance and the instances it is nested in.</summary>
    public static string PathOf(Element instance) =>
        string.Join(".", instance.AncestorsAndSelf().TakeWhile(a => a.Kind == ElementKind.InstanceSpecification).Reverse().Select(a => a.DisplayName));

    public static InstanceTable Build(ModelRepository m, Element? classifier, bool includeNested = true)
    {
        var instances = (classifier != null ? InstanceModel.InstancesOf(m, classifier) : m.OfKind(ElementKind.InstanceSpecification).OrderBy(i => i.QualifiedName, StringComparer.OrdinalIgnoreCase))
            .Where(i => includeNested || i.Owner?.Kind != ElementKind.InstanceSpecification).ToList();
        IReadOnlyList<Element> features;
        if (classifier != null) features = InstanceModel.Features(m, classifier);
        else
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            features = instances.SelectMany(i => InstanceModel.InstanceFeatures(m, i)).Where(f => names.Add(f.Name)).ToList();
        }
        var columns = features.Select(f =>
        {
            var unit = InstanceModel.UnitOf(m, f);
            return new InstanceColumn(f, unit != null ? $"{f.Name} [{unit}]" : f.Name, unit, InstanceModel.IsInstanceValued(m, f), InstanceModel.IsEnumValued(m, f));
        }).ToList();
        var rows = instances.Select(i => new InstanceRow(i, PathOf(i), columns.Select(c => Cell(m, i, c.Feature)).ToList())).ToList();
        return new InstanceTable { Classifier = classifier, Columns = columns, Rows = rows };
    }

    /// <summary>The cell text of an instance for a feature: its slot's value (a feature of the same name counts for specializations).</summary>
    public static string Cell(ModelRepository m, Element instance, Element feature)
    {
        var slot = InstanceModel.SlotFor(instance, feature)
                   ?? InstanceModel.Slots(instance).FirstOrDefault(s => InstanceModel.FeatureOf(m, s)?.Name == feature.Name);
        return slot == null ? "" : InstanceModel.DisplayValue(m, slot);
    }

    /// <summary>Header row and cells as strings (for CSV / XLSX).</summary>
    public (IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows) Grid() =>
        (Columns.Select(c => c.Header).Prepend("Instance").Prepend("Id").ToList(),
         Rows.Select(r => (IReadOnlyList<string>)r.Cells.Prepend(r.Label).Prepend(r.Instance.Id).ToList()).ToList());

    /// <summary>RFC 4180 CSV with formula-injection protection.</summary>
    public string ToCsv()
    {
        var (header, rows) = Grid();
        var sb = new StringBuilder();
        static string Q(string s)
        {
            s = CsvSafety.Neutralize(s);
            return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        sb.AppendLine(string.Join(",", header.Select(Q)));
        foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(Q)));
        return sb.ToString();
    }
}
