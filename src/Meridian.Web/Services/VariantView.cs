using System.Text;
using Meridian.Core.Analysis;
using Meridian.Core.Model;

namespace Meridian.Web.Services;

/// <summary>How diagrams show the elements a variant leaves out.</summary>
public enum VariantDisplay { Off, Grey, Hide }

/// <summary>
/// "Show variant" on diagrams: a chosen configuration and whether elements its presence conditions exclude (and, on
/// feature model diagrams, features it does not select) are greyed or hidden. Per user (lives in the workspace, not the model).
/// </summary>
public sealed class VariantView(Workspace ws)
{
    private VariantResolution? _resolution;
    private int _version = -1;
    private ModelRepository? _model;
    private string? _resolvedFor;

    public string? ConfigurationId { get; private set; }
    public VariantDisplay Mode { get; private set; }

    public Element? Configuration => ws.Model.Find(ConfigurationId) is { Kind: ElementKind.Configuration } c ? c : null;
    public bool Active => Mode != VariantDisplay.Off && Configuration != null;

    /// <summary>Shows <paramref name="configurationId"/> on diagrams with <paramref name="mode"/> (Off clears it).</summary>
    public void Show(string? configurationId, VariantDisplay mode)
    {
        if (ConfigurationId == configurationId && Mode == mode) return;
        ConfigurationId = configurationId;
        Mode = configurationId == null ? VariantDisplay.Off : mode;
        ws.Bump();
    }

    public void SetMode(VariantDisplay mode)
    {
        if (Mode == mode) return;
        if (mode != VariantDisplay.Off && Configuration == null)
            ConfigurationId = ws.Model.OfKind(ElementKind.Configuration).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.Id;
        Mode = ConfigurationId == null ? VariantDisplay.Off : mode;
        ws.Bump();
    }

    /// <summary>The configuration's resolution, recomputed only when the model changes.</summary>
    public VariantResolution? Resolution
    {
        get
        {
            if (Configuration is not { } c) return null;
            if (_resolution != null && _version == ws.ModelVersion && ReferenceEquals(_model, ws.Model) && _resolvedFor == c.Id) return _resolution;
            _resolution = Variability.Resolve(ws.Model, c);
            _version = ws.ModelVersion;
            _model = ws.Model;
            _resolvedFor = c.Id;
            return _resolution;
        }
    }

    /// <summary>True when the variant leaves <paramref name="e"/> out (an unselected feature counts as left out).</summary>
    public bool IsOut(Element e)
    {
        if (Resolution is not { } r) return false;
        if (e.Kind == ElementKind.Feature) return !r.SelectedFeatures.Contains(e);
        return !r.IsIncluded(e);
    }

    /// <summary>A style sheet that greys or hides the variant's absent nodes and connectors on <paramref name="d"/> ("" when off).</summary>
    public string StyleFor(Diagram d)
    {
        if (!Active) return "";
        var sel = new List<string>();
        foreach (var n in d.Nodes) if (IsOut(n.Element)) sel.Add($"g[data-node=\"{Css(n.Id)}\"]");
        foreach (var x in d.Edges) if (IsOut(x.Relationship) || IsOut(x.Source.Element) || IsOut(x.Target.Element)) sel.Add($"g[data-edge=\"{Css(x.Id)}\"]");
        if (sel.Count == 0) return "";
        var rule = Mode == VariantDisplay.Hide ? "display:none" : "opacity:.22;filter:grayscale(1)";
        var sb = new StringBuilder("<style>");
        sb.Append(string.Join(",", sel)).Append('{').Append(rule).Append('}');
        return sb.Append("</style>").ToString();
    }

    /// <summary>An id as a CSS string body (quotes, backslashes and markup characters escaped).</summary>
    private static string Css(string id) => id.Replace("\\", "\\5C ").Replace("\"", "\\22 ").Replace("<", "\\3C ").Replace(">", "\\3E ").Replace("&", "\\26 ");

    /// <summary>Elements on <paramref name="d"/> the variant leaves out.</summary>
    public int CountOut(Diagram d) => Active ? d.Nodes.Count(n => IsOut(n.Element)) : 0;

    public static string Label(VariantDisplay m) => m switch { VariantDisplay.Grey => "Grey out", VariantDisplay.Hide => "Hide", _ => "Off" };
}
