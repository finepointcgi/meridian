using System.Globalization;
using System.Text;
using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Serialization;

namespace Meridian.Web.Services;

/// <summary>
/// Workspace-side view of simulation-driven verification: running it with the right options (model copies, the variant
/// shown on diagrams, the FMI runtime), the cached last result of every requirement (with staleness) and the colouring of
/// requirements on diagrams by their last result.
/// </summary>
public sealed class VerificationView(Workspace ws)
{
    private Dictionary<string, StoredResult> _results = new(StringComparer.Ordinal);
    private int _version = -1;
    private ModelRepository? _model;

    /// <summary>Colour requirements on diagrams by their last result.</summary>
    public bool ShowOnDiagrams { get; private set; } = true;

    /// <summary>The reports of the last verification started from this workspace.</summary>
    public IReadOnlyList<VerificationReport> LastReports { get; private set; } = [];

    public void SetShowOnDiagrams(bool on)
    {
        if (ShowOnDiagrams == on) return;
        ShowOnDiagrams = on;
        ws.Bump();
    }

    /// <summary>Every requirement's last stored result, recomputed (with staleness) only when the model changes.</summary>
    public IReadOnlyDictionary<string, StoredResult> Results
    {
        get
        {
            if (_version == ws.ModelVersion && ReferenceEquals(_model, ws.Model)) return _results;
            var map = new Dictionary<string, StoredResult>(StringComparer.Ordinal);
            foreach (var r in ws.Model.OfKind(ElementKind.Requirement))
                if (Verification.LastResult(ws.Model, r) is { } s) map[r.Id] = s;
            _results = map;
            _version = ws.ModelVersion;
            _model = ws.Model;
            return _results;
        }
    }

    public StoredResult? ResultOf(Element requirement) => Results.GetValueOrDefault(requirement.Id);

    public static ModelRepository Copy(ModelRepository m) => ProjectFile.Deserialize(ProjectFile.Serialize(m));

    /// <summary>Options for a run from this workspace: model copies, the version label, and the variant shown on diagrams (ad hoc checks).</summary>
    public VerificationOptions Options(Func<CoSimPart, IReadOnlyDictionary<string, double>, ICoSimUnit>? fmu = null, bool useShownVariant = true, CancellationToken ct = default) => new()
    {
        Clone = Copy,
        FmuFactory = fmu,
        Configuration = useShownVariant && ws.VariantView.Active ? ws.VariantView.Configuration : null,
        ModelVersion = $"{ws.BranchName} #{Verification.ModelFingerprint(ws.Model)}",
        Cancellation = ct,
    };

    public bool Running { get; private set; }

    /// <summary>
    /// Runs <paramref name="run"/> (or every run plus the uncovered criteria when null) on a snapshot in the background,
    /// then records the results into the live model (one undo step per run).
    /// </summary>
    public async Task<IReadOnlyList<VerificationReport>> RunAsync(Element? run, VerificationOptions options)
    {
        if (Running) return LastReports;
        Running = true;
        ws.Bump();
        try
        {
            var live = ws.Model;
            var snapshot = Copy(live);
            var snapRun = run != null ? snapshot.Find(run.Id) : null;
            var snapOptions = new VerificationOptions
            {
                Clone = options.Clone, Analyzer = options.Analyzer, FmuFactory = options.FmuFactory, ModelVersion = options.ModelVersion,
                Now = options.Now, Cancellation = options.Cancellation,
                Configuration = options.Configuration is { } c ? snapshot.Find(c.Id) : null,
            };
            var reports = await Task.Run(() => snapRun != null
                ? (IReadOnlyList<VerificationReport>)[Verification.Execute(snapshot, snapRun, null, snapOptions)]
                : Verification.ExecuteAll(snapshot, snapOptions));
            if (!ReferenceEquals(live, ws.Model)) return [];   // the project changed while running
            var bound = reports.Select(r => Verification.Rebind(r, live)).ToList();
            if (!ws.ReadOnly) foreach (var r in bound) Verification.Record(live, r);
            LastReports = bound;
            ShowOnDiagrams = true;
            return bound;
        }
        finally
        {
            Running = false;
            ws.Bump();
        }
    }

    public static string Color(string status) => status switch
    {
        "pass" => "#2e9e5b",
        "fail" => "#d64545",
        _ => "#d08a00",
    };

    /// <summary>Outlines (and a status tag) for the requirements on <paramref name="d"/> that have a result. "" when off.</summary>
    public string Overlay(Diagram d, Func<DiagramNode, Bounds> bounds, double zi)
    {
        if (!ShowOnDiagrams) return "";
        var sb = new StringBuilder();
        foreach (var n in d.Nodes)
        {
            if (n.Element.Kind != ElementKind.Requirement || ResultOf(n.Element) is not { } r) continue;
            var b = bounds(n);
            var c = Color(r.Status);
            var dash = r.Stale ? $" stroke-dasharray=\"{F(6 * zi)},{F(4 * zi)}\"" : "";
            var tag = r.Status switch { "pass" => "✓ pass", "fail" => "✗ fail", _ => "? inconclusive" } + (r.Stale ? " · stale" : "");
            var tw = (tag.Length * 6.2 + 12) * zi;
            var tip = System.Net.WebUtility.HtmlEncode($"{r.Label}: {r.Value} (bound {r.Bound}, margin {r.Margin}) — {r.Variant}, {r.At}");
            sb.Append($"<g class=\"vfy-mark {r.Status}{(r.Stale ? " stale" : "")}\" data-req=\"{System.Net.WebUtility.HtmlEncode(n.Element.Id)}\" pointer-events=\"none\"><title>{tip}</title>")
              .Append($"<rect x=\"{F(b.X - 4 * zi)}\" y=\"{F(b.Y - 4 * zi)}\" width=\"{F(b.Width + 8 * zi)}\" height=\"{F(b.Height + 8 * zi)}\" rx=\"{F(6 * zi)}\" fill=\"{c}\" fill-opacity=\"0.07\" stroke=\"{c}\" stroke-width=\"{F(2.5 * zi)}\"{dash}/>")
              .Append($"<rect x=\"{F(b.X + b.Width - tw)}\" y=\"{F(b.Y - 20 * zi)}\" width=\"{F(tw)}\" height=\"{F(16 * zi)}\" rx=\"{F(4 * zi)}\" fill=\"{c}\"/>")
              .Append($"<text x=\"{F(b.X + b.Width - tw + 6 * zi)}\" y=\"{F(b.Y - 8 * zi)}\" font-size=\"{F(10.5 * zi)}\" font-weight=\"600\" fill=\"#fff\">{System.Net.WebUtility.HtmlEncode(tag)}</text></g>");
        }
        return sb.ToString();
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
