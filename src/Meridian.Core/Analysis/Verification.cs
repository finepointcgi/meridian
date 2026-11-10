using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

/// <summary>Field keys for simulation-driven verification (criteria on requirements, verification runs, recorded results).</summary>
public static class VerificationKeys
{
    /// <summary>Requirements: the machine-checkable criterion (see <see cref="VerificationCriterion"/>).</summary>
    public const string Criterion = "verify.criterion";

    // Results recorded on each requirement by the last run that checked it.
    public const string Status = "verify.status";            // pass / fail / inconclusive
    public const string Value = "verify.value";              // computed value with unit
    public const string Bound = "verify.bound";              // the bound it was compared with
    public const string Margin = "verify.margin";            // distance to the bound (positive = inside), with unit and %
    public const string Message = "verify.message";          // explanation (or why it is inconclusive)
    public const string At = "verify.at";                    // ISO 8601 UTC timestamp
    public const string ModelVersion = "verify.modelVersion";
    public const string Variant = "verify.variant";          // configuration name, or "150% model"
    public const string Run = "verify.run";                  // id of the verification run ("" for an ad-hoc check)
    public const string Analysis = "verify.analysisUsed";    // e.g. "Parametric (Vehicle)"
    public const string Fingerprint = "verify.fingerprint";  // hash of what the result depends on (staleness)
    public const string Dependencies = "verify.deps";        // comma separated element ids the fingerprint covers

    // Verification runs (test cases stereotyped «VerificationRun»).
    public const string RunStereotype = "VerificationRun";
    public const string RunAnalysis = "verify.analysis";        // default analysis: parametric / montecarlo / timed / cosim
    public const string Subject = "verify.subject";             // id of the block / behavior / system analysed
    public const string Requirements = "verify.requirements";   // comma separated requirement ids (plus outgoing «verify» links)
    public const string Configuration = "verify.configuration"; // id of the configuration (variant) to verify
    public const string Inputs = "verify.inputs";               // "mass = 1500 kg; driveForce = 6 kN"
    public const string Samples = "verify.samples";
    public const string Seed = "verify.seed";
    public const string StopTime = "verify.stopTime";
    public const string Step = "verify.step";
    public const string LastRun = "verify.lastRun";
    public const string Summary = "verify.summary";
    public const string Results = "verify.results";             // JSON: this run's result per requirement

    public static readonly IReadOnlyList<string> ResultKeys =
        [Status, Value, Bound, Margin, Message, At, ModelVersion, Variant, Run, Analysis, Fingerprint, Dependencies, LastRun, Summary, Results];

    public static readonly IReadOnlyList<string> RunKeys = [RunAnalysis, Subject, Requirements, Configuration, Inputs, Samples, Seed, StopTime, Step];
}

/// <summary>The result of checking one requirement.</summary>
public sealed record VerificationResult(
    Element Requirement, Verdict Verdict, string Criterion, string Value, string Bound, string Margin, double? RelativeMargin,
    string Message, string Analysis, IReadOnlyList<string> Notes, IReadOnlyList<string> Dependencies, string Fingerprint)
{
    public string Status => Verification.StatusText(Verdict);
}

/// <summary>What a verification run produced.</summary>
public sealed class VerificationReport
{
    public Element? Run { get; init; }
    public string Variant { get; init; } = Verification.FullModel;
    public string ModelVersion { get; init; } = "";
    public DateTime At { get; init; }
    public List<VerificationResult> Results { get; } = new();
    public List<string> Problems { get; } = new();
    public int Pass => Results.Count(r => r.Verdict == Verdict.Pass);
    public int Fail => Results.Count(r => r.Verdict == Verdict.Fail);
    public int Inconclusive => Results.Count(r => r.Verdict == Verdict.Inconclusive);
    public string Summary => $"{Pass} pass, {Fail} fail, {Inconclusive} inconclusive";
    public Verdict Overall => Fail > 0 ? Verdict.Fail : Inconclusive > 0 || Results.Count == 0 ? Verdict.Inconclusive : Verdict.Pass;
}

public sealed class VerificationOptions
{
    /// <summary>Copies a model (same element ids). Needed to verify a variant or to apply run inputs; the model itself is never changed by analysis.</summary>
    public Func<ModelRepository, ModelRepository>? Clone { get; init; }
    public IVerificationAnalyzer Analyzer { get; init; } = ModelAnalyzer.Instance;
    public Func<CoSimPart, IReadOnlyDictionary<string, double>, ICoSimUnit>? FmuFactory { get; init; }
    /// <summary>Verify this configuration instead of the run's (e.g. the variant shown on diagrams).</summary>
    public Element? Configuration { get; init; }
    /// <summary>A label for the model version (branch / commit); a content hash is used when empty.</summary>
    public string? ModelVersion { get; init; }
    public DateTime? Now { get; init; }
    public CancellationToken Cancellation { get; init; }
}

/// <summary>The last recorded result of a requirement, as stored in its fields.</summary>
public sealed record StoredResult(string Status, string Value, string Bound, string Margin, string Message, string At, string ModelVersion,
    string Variant, string RunId, string Analysis, bool Stale)
{
    /// <summary>"pass", "fail", "inconclusive", with " (stale)" appended when the model changed since.</summary>
    public string Label => Stale ? $"{Status} (stale)" : Status;
}

/// <summary>
/// Simulation-driven verification (in the style of Cameo's simulation-based requirement verification).
/// <para>
/// A requirement carries a criterion (<see cref="VerificationKeys.Criterion"/>, syntax in <see cref="VerificationCriterion"/>).
/// A verification run — a test case stereotyped «VerificationRun» — names the requirements (its «verify» links and/or a list),
/// the default analysis, the subject, a configuration (variant) and input overrides. Running it evaluates every criterion
/// on a copy of the model (variant resolved, inputs applied) and records per requirement the verdict, computed value,
/// margin, timestamp, model version and variant as fields of the requirement, plus a summary on the run; all in one undo
/// step, so results version, merge and replicate with the model. A fingerprint of everything a result read is stored
/// with it: when any of that changes the result is stale.
/// </para>
/// </summary>
public static partial class Verification
{
    public const string FullModel = "150% model";

    public static string StatusText(Verdict v) => v switch { Verdict.Pass => "pass", Verdict.Fail => "fail", _ => "inconclusive" };

    public static string Criterion(Element requirement) => requirement.Get(VerificationKeys.Criterion);
    public static bool HasCriterion(Element requirement) => Criterion(requirement).Trim().Length > 0;

    // ================================================================== runs

    public static bool IsRun(Element e) => e.Kind == ElementKind.TestCase && e.Stereotypes.Contains(VerificationKeys.RunStereotype);

    public static IEnumerable<Element> Runs(ModelRepository m) => m.OfKind(ElementKind.TestCase).Where(IsRun).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a verification run that verifies <paramref name="requirements"/> (one undo step).</summary>
    public static Element CreateRun(ModelRepository m, Element owner, string name, IEnumerable<Element> requirements, Element? subject = null, AnalysisKind? analysis = null)
    {
        using var _ = m.History.Transaction("New verification run");
        var run = m.Create(owner, ElementKind.TestCase, name);
        m.Set(run, FieldKeys.Stereotypes, VerificationKeys.RunStereotype);
        if (subject != null) m.Set(run, VerificationKeys.Subject, subject.Id);
        if (analysis is { } a) m.Set(run, VerificationKeys.RunAnalysis, AnalysisName(a));
        foreach (var r in requirements.Where(r => r.Kind == ElementKind.Requirement).Distinct())
            m.Connect(ElementKind.Verify, run, r);
        return run;
    }

    public static string AnalysisName(AnalysisKind k) => k switch
    {
        AnalysisKind.Parametric => "parametric", AnalysisKind.MonteCarlo => "montecarlo", AnalysisKind.Timed => "timed", _ => "cosim",
    };

    /// <summary>The requirements a run checks: its list plus the targets of its «verify» links, in id order.</summary>
    public static IReadOnlyList<Element> RequirementsOf(ModelRepository m, Element run)
    {
        var list = run.Get(VerificationKeys.Requirements).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m.Find).OfType<Element>()
            .Concat(m.Outgoing(run, ElementKind.Verify).Select(r => r.Target))
            .Where(r => r.Kind == ElementKind.Requirement).Distinct().ToList();
        return list.OrderBy(r => r.Get(FieldKeys.RequirementId), StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name).ToList();
    }

    public static Element? SubjectOf(ModelRepository m, Element run) => m.Find(run.Get(VerificationKeys.Subject));
    public static Element? ConfigurationOf(ModelRepository m, Element run) => m.Find(run.Get(VerificationKeys.Configuration)) is { Kind: ElementKind.Configuration } c ? c : null;

    public static AnalysisSettings SettingsOf(Element? run, Func<CoSimPart, IReadOnlyDictionary<string, double>, ICoSimUnit>? fmu)
    {
        int Int(string key, int def) => run != null && int.TryParse(run.Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : def;
        double? Seconds(string key) => run != null && run.Get(key) is { Length: > 0 } t ? TimedSimulationDuration(t) : null;
        return new AnalysisSettings
        {
            Samples = Math.Min(Int(VerificationKeys.Samples, 500), 100_000), Seed = Int(VerificationKeys.Seed, 12345),
            StopTime = Seconds(VerificationKeys.StopTime), Step = Seconds(VerificationKeys.Step), FmuFactory = fmu,
        };
    }

    /// <summary>Seconds from "10", "10 s", "2 min", "500 ms"; null when unreadable.</summary>
    private static double? TimedSimulationDuration(string text)
    {
        var m = Regex.Match(text, @"^\s*([-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)\s*(.*)$");
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            && Units.Parse(m.Groups[2].Value) is { } u && (u.Dimension == new Dimension(0, 0, 1, 0, 0, 0, 0) || m.Groups[2].Value.Trim().Length == 0))
            return u.ToSi(v);
        return DiscreteEventSimulation.ParseDuration(text);
    }

    // ================================================================== inputs

    /// <summary>Parses run inputs: "mass = 1500 kg; engine.maxPower = 180 kW" (one per line or separated by ';').</summary>
    public static IReadOnlyList<(string Name, double Value, string? Unit)> ParseInputs(string? text, List<string>? problems = null)
    {
        var list = new List<(string, double, string?)>();
        foreach (var raw in (text ?? "").Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = InputRe().Match(raw);
            if (!m.Success || !double.TryParse(m.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                problems?.Add($"Input '{raw}' is not 'name = number [unit]'");
                continue;
            }
            var unit = m.Groups["u"].Value.Trim();
            if (unit.Length > 0 && Units.Parse(unit) is null) { problems?.Add($"Input '{raw}': unknown unit '{unit}'"); continue; }
            list.Add((m.Groups["n"].Value.Trim(), v, unit.Length > 0 ? unit : null));
        }
        return list;
    }

    /// <summary>Problems reading a run's inputs (for the run editor).</summary>
    public static IReadOnlyList<string> ParseInputsProblems(Element run)
    {
        var problems = new List<string>();
        ParseInputs(run.Get(VerificationKeys.Inputs), problems);
        return problems;
    }

    [GeneratedRegex(@"^(?<n>[\p{L}_][\p{L}\p{N}_.]*)\s*=\s*(?<v>[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*(?<u>.*)$")]
    private static partial Regex InputRe();

    /// <summary>Writes input overrides as default values on <paramref name="m"/> (a copy), converting to each property's unit.</summary>
    private static void ApplyInputs(ModelRepository m, Element? subject, IReadOnlyList<(string Name, double Value, string? Unit)> inputs, List<string> problems)
    {
        foreach (var (name, value, unit) in inputs)
        {
            Element? prop = null;
            if (subject != null && ModelAnalyzer.Navigate(m, subject.Kind is ElementKind.Block or ElementKind.Class ? subject : subject.Owner ?? subject, name) is { Block: { } b } nav)
                prop = b.OwnedElements.FirstOrDefault(p => p.Kind == ElementKind.Property && string.Equals(p.Name, nav.Name, StringComparison.OrdinalIgnoreCase));
            if (prop is null)
            {
                var last = name.Split('.')[^1];
                var all = m.OfKind(ElementKind.Property).Where(p => string.Equals(p.Name, last, StringComparison.OrdinalIgnoreCase)).ToList();
                if (all.Count == 1) prop = all[0];
                else { problems.Add(all.Count == 0 ? $"Input '{name}': no such value property" : $"Input '{name}' is ambiguous: name the block (Block.{last})"); continue; }
            }
            var v = value;
            var target = CoSimulation.UnitOf(m, prop);
            if (unit != null && target != null)
            {
                if (Units.Convert(value, unit, target) is { } c) v = c;
                else { problems.Add($"Input '{name}': {unit} does not convert to {target}"); continue; }
            }
            else if (unit != null && Units.Parse(unit) is { } u) v = u.ToSi(value);   // no declared unit: store SI
            m.Set(prop, FieldKeys.DefaultValue, v.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    // ================================================================== execution

    /// <summary>
    /// Evaluates the criteria of <paramref name="requirements"/> (default: the run's, or every requirement with a criterion)
    /// without changing the model. Use <see cref="Record"/> to store the results.
    /// </summary>
    public static VerificationReport Execute(ModelRepository m, Element? run, IEnumerable<Element>? requirements = null, VerificationOptions? options = null)
    {
        options ??= new VerificationOptions();
        var reqs = (requirements ?? (run != null ? RequirementsOf(m, run) : m.OfKind(ElementKind.Requirement).Where(HasCriterion)
                .OrderBy(r => r.Get(FieldKeys.RequirementId), StringComparer.OrdinalIgnoreCase))).ToList();
        var config = options.Configuration ?? (run != null ? ConfigurationOf(m, run) : null);
        var report = new VerificationReport
        {
            Run = run, Variant = config?.Name ?? FullModel, At = options.Now ?? DateTime.UtcNow,
            ModelVersion = string.IsNullOrWhiteSpace(options.ModelVersion) ? "#" + ModelFingerprint(m) : options.ModelVersion!,
        };

        // The model analysed: the variant, with inputs applied, on a copy.
        var work = m;
        VariantResolution? resolution = null;
        var inputs = ParseInputs(run?.Get(VerificationKeys.Inputs), report.Problems);
        string? setupError = null;
        if (config != null || inputs.Count > 0)
        {
            if (options.Clone is null) setupError = "Verifying a variant or with inputs needs a model copy, which is not available here";
            else
            {
                work = options.Clone(m);
                if (config != null)
                {
                    resolution = Variability.Resolve(m, config);
                    report.Problems.AddRange(Variability.Validate(m, config).Select(p => $"Configuration '{config.Name}': {p.Message}"));
                    Variability.ApplyTo(work, resolution);
                }
                var subj = run != null && SubjectOf(m, run) is { } s ? work.Find(s.Id) : null;
                ApplyInputs(work, subj, inputs, report.Problems);
            }
        }

        var settings = SettingsOf(run, options.FmuFactory);
        var defaultKind = run != null ? VerificationCriterion.ParseAnalysis(run.Get(VerificationKeys.RunAnalysis)) : null;
        var subject = run != null ? SubjectOf(m, run) : null;
        var outputs = new Dictionary<(AnalysisKind, string), AnalysisOutput>();
        var extraDeps = new List<string>();
        if (run != null) extraDeps.Add(run.Id);
        if (config != null) extraDeps.AddRange(new[] { config.Id }.Concat(Variability.Features(m).Select(f => f.Id)));

        foreach (var req in reqs)
        {
            options.Cancellation.ThrowIfCancellationRequested();
            var text = Criterion(req).Trim();
            VerificationResult Done(CriterionResult cr, string analysis, IEnumerable<string> deps)
            {
                var d = deps.Concat(extraDeps).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
                return new VerificationResult(req, cr.Verdict, text, cr.Value, cr.Bound, cr.Margin, cr.RelativeMargin, cr.Message, analysis, cr.Notes, d, Fingerprint(m, d, text));
            }
            if (text.Length == 0) { report.Results.Add(Done(CriterionResult.Inconclusive("No verification criterion"), "", [])); continue; }
            if (setupError != null) { report.Results.Add(Done(CriterionResult.Inconclusive(setupError), "", [])); continue; }
            if (!VerificationCriterion.TryParse(text, out var crit, out var err)) { report.Results.Add(Done(CriterionResult.Inconclusive($"Criterion: {err}"), "", [])); continue; }
            if (resolution != null && !resolution.IsIncluded(req))
            {
                report.Results.Add(Done(CriterionResult.Inconclusive($"Not part of variant '{config!.Name}'"), "", [])); continue;
            }
            var kind = crit!.Analysis ?? defaultKind ?? AnalysisKind.Parametric;
            var target = ResolveTarget(work, m, crit, subject, req, kind, out var why);
            if (target is null) { report.Results.Add(Done(CriterionResult.Inconclusive(why), VerificationCriterion.Label(kind), [])); continue; }
            if (!outputs.TryGetValue((kind, target.Id), out var output))
            {
                try { output = options.Analyzer.Analyze(work, kind, target, settings, options.Cancellation); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    output = AnalysisOutput.Failed(VerificationCriterion.Label(kind), ex.Message);
                }
                output.Dependencies.Add(target.Id);
                outputs[(kind, target.Id)] = output;
            }
            if (output.Error != null) { report.Results.Add(Done(CriterionResult.Inconclusive(output.Error), output.Description, output.Dependencies)); continue; }
            var result = crit.Evaluate(output);
            if (output.Problems.Count > 0) result = result with { Notes = result.Notes.Concat(output.Problems.Take(3)).ToList() };
            report.Results.Add(Done(result, output.Description, output.Dependencies));
        }
        return report;
    }

    /// <summary>The element a criterion is evaluated on: its (target), the run's subject, else what satisfies the requirement.</summary>
    private static Element? ResolveTarget(ModelRepository work, ModelRepository original, VerificationCriterion c, Element? subject, Element req, AnalysisKind kind, out string why)
    {
        why = "";
        bool Fits(Element e) => kind switch
        {
            AnalysisKind.Timed => e.Kind is ElementKind.Activity or ElementKind.StateMachine or ElementKind.Block or ElementKind.Class,
            _ => e.Kind is ElementKind.Block or ElementKind.Class,
        };
        if (c.Target is { } name)
        {
            var hit = work.AllElements.Where(e => e.Name == name && Fits(e)).FirstOrDefault()
                      ?? work.AllElements.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) && Fits(e));
            if (hit is null) why = $"No {(kind == AnalysisKind.Timed ? "behavior or block" : "block")} called '{name}'{(work != original ? " in this variant" : "")}";
            return hit;
        }
        if (subject != null)
        {
            if (work.Find(subject.Id) is { } s) return s;
            why = $"The run's subject '{subject.Name}' is not part of this variant";
            return null;
        }
        // Satisfying elements: blocks, value properties (their block) or behaviors.
        foreach (var r in original.Incoming(req, ElementKind.Satisfy).Select(r => r.Source).Concat(original.Incoming(req, ElementKind.Refine).Select(r => r.Source)))
        {
            var cand = r.Kind == ElementKind.Property && r.Owner != null ? r.Owner : r;
            if (Fits(cand) && work.Find(cand.Id) is { } w) return w;
        }
        why = "No subject: name one in the criterion (e.g. parametric(Vehicle): …), set the run's subject, or add a «satisfy» link from a block";
        return null;
    }

    // ================================================================== recording

    /// <summary>Stores the report on the requirements (and the run) in one undo step. Returns the number of requirements updated.</summary>
    public static int Record(ModelRepository m, VerificationReport report)
    {
        var at = report.At.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var n = 0;
        using (m.History.Transaction(report.Run != null ? $"Verify ({report.Run.Name})" : "Verify requirements"))
        {
            foreach (var r in report.Results)
            {
                if (!m.Contains(r.Requirement)) continue;
                void S(string key, string? v) { if (r.Requirement.Get(key) != (v ?? "")) m.Set(r.Requirement, key, string.IsNullOrEmpty(v) ? null : v); }
                S(VerificationKeys.Status, r.Status);
                S(VerificationKeys.Value, r.Value);
                S(VerificationKeys.Bound, r.Bound);
                S(VerificationKeys.Margin, r.Margin);
                S(VerificationKeys.Message, r.Notes.Count > 0 ? $"{r.Message} — {string.Join("; ", r.Notes)}" : r.Message);
                S(VerificationKeys.At, at);
                S(VerificationKeys.ModelVersion, report.ModelVersion);
                S(VerificationKeys.Variant, report.Variant);
                S(VerificationKeys.Run, report.Run?.Id);
                S(VerificationKeys.Analysis, r.Analysis);
                S(VerificationKeys.Fingerprint, r.Fingerprint);
                S(VerificationKeys.Dependencies, string.Join(",", r.Dependencies));
                n++;
            }
            if (report.Run is { } run && m.Contains(run))
            {
                m.Set(run, VerificationKeys.LastRun, at);
                m.Set(run, VerificationKeys.Summary, $"{report.Summary} · {report.Variant} · {report.ModelVersion}");
                m.Set(run, VerificationKeys.Results, JsonSerializer.Serialize(report.Results.Select(x => new RunEntry
                {
                    Requirement = x.Requirement.Id, Status = x.Status, Value = x.Value, Bound = x.Bound, Margin = x.Margin, Message = x.Message,
                }).ToList()));
                m.Set(run, ReqFields.Verdict, StatusText(report.Overall));
            }
        }
        return n;
    }

    /// <summary>One row of a run's stored results.</summary>
    public sealed class RunEntry
    {
        public string Requirement { get; set; } = "";
        public string Status { get; set; } = "";
        public string Value { get; set; } = "";
        public string Bound { get; set; } = "";
        public string Margin { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public static IReadOnlyList<RunEntry> RunResults(Element run)
    {
        var json = run.Get(VerificationKeys.Results);
        if (json.Length == 0) return [];
        try { return JsonSerializer.Deserialize<List<RunEntry>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>Runs a verification run and records the results.</summary>
    public static VerificationReport Run(ModelRepository m, Element? run, VerificationOptions? options = null, IEnumerable<Element>? requirements = null)
    {
        var report = Execute(m, run, requirements, options);
        Record(m, report);
        return report;
    }

    /// <summary>
    /// Runs every verification run, then checks the requirements with a criterion that no run covers (ad hoc, on the
    /// 150% model or <see cref="VerificationOptions.Configuration"/>). Records everything.
    /// </summary>
    public static IReadOnlyList<VerificationReport> RunAll(ModelRepository m, VerificationOptions? options = null)
    {
        var reports = ExecuteAll(m, options);
        foreach (var r in reports) Record(m, r);
        return reports;
    }

    /// <summary>What <see cref="RunAll"/> would record, without changing the model.</summary>
    public static IReadOnlyList<VerificationReport> ExecuteAll(ModelRepository m, VerificationOptions? options = null)
    {
        options ??= new VerificationOptions();
        var reports = new List<VerificationReport>();
        var covered = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        foreach (var run in Runs(m).ToList())
        {
            var report = Execute(m, run, null, options);
            reports.Add(report);
            foreach (var r in report.Results) covered.Add(r.Requirement);
        }
        var rest = m.OfKind(ElementKind.Requirement).Where(r => HasCriterion(r) && !covered.Contains(r))
            .OrderBy(r => r.Get(FieldKeys.RequirementId), StringComparer.OrdinalIgnoreCase).ToList();
        if (rest.Count > 0 || reports.Count == 0) reports.Add(Execute(m, null, rest, options));
        return reports;
    }

    /// <summary>
    /// The same report against another copy of the model (elements matched by id, missing ones dropped): run the analyses
    /// on a snapshot in the background, then record into the live model.
    /// </summary>
    public static VerificationReport Rebind(VerificationReport report, ModelRepository target)
    {
        var copy = new VerificationReport
        {
            Run = report.Run is { } run ? target.Find(run.Id) : null,
            Variant = report.Variant, ModelVersion = report.ModelVersion, At = report.At,
        };
        copy.Problems.AddRange(report.Problems);
        foreach (var r in report.Results)
            if (target.Find(r.Requirement.Id) is { } req) copy.Results.Add(r with { Requirement = req });
        return copy;
    }

    // ================================================================== stored results and staleness

    public static StoredResult? LastResult(ModelRepository m, Element requirement)
    {
        var status = requirement.Get(VerificationKeys.Status);
        if (status.Length == 0) return null;
        return new StoredResult(status, requirement.Get(VerificationKeys.Value), requirement.Get(VerificationKeys.Bound), requirement.Get(VerificationKeys.Margin),
            requirement.Get(VerificationKeys.Message), requirement.Get(VerificationKeys.At), requirement.Get(VerificationKeys.ModelVersion),
            requirement.Get(VerificationKeys.Variant), requirement.Get(VerificationKeys.Run), requirement.Get(VerificationKeys.Analysis), IsStale(m, requirement));
    }

    /// <summary>True when something the last result depended on (the criterion, the analysed elements, the run, the variant) changed since.</summary>
    public static bool IsStale(ModelRepository m, Element requirement)
    {
        var stored = requirement.Get(VerificationKeys.Fingerprint);
        if (stored.Length == 0) return requirement.Get(VerificationKeys.Status).Length > 0;
        var deps = requirement.Get(VerificationKeys.Dependencies).Split(',', StringSplitOptions.RemoveEmptyEntries);
        return Fingerprint(m, deps, Criterion(requirement).Trim()) != stored;
    }

    /// <summary>Clears recorded results (one undo step).</summary>
    public static void ClearResults(ModelRepository m, IEnumerable<Element> requirements)
    {
        using var _ = m.History.Transaction("Clear verification results");
        foreach (var r in requirements)
            foreach (var k in VerificationKeys.ResultKeys)
                if (r.Get(k).Length > 0) m.Set(r, k, null);
    }

    private static readonly HashSet<string> Ignored = new(VerificationKeys.ResultKeys.Concat(
        [ReqFields.Verdict, ReqFields.SourceFingerprint, ReqFields.TargetFingerprint, FieldKeys.Documentation]), StringComparer.Ordinal);

    /// <summary>
    /// Hash of a criterion and of the elements (with their contents and the relationships touching them) a result read.
    /// Documentation, verification results and review bookkeeping are left out; diagrams are ignored.
    /// </summary>
    public static string Fingerprint(ModelRepository m, IEnumerable<string> dependencyIds, string criterion)
    {
        var sb = new StringBuilder("crit|").Append(criterion).Append('\n');
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rels = new SortedDictionary<string, Relationship>(StringComparer.Ordinal);
        foreach (var id in dependencyIds.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            if (m.Find(id) is not { } root) { sb.Append("missing|").Append(id).Append('\n'); continue; }
            foreach (var e in new[] { root }.Concat(root.Descendants()))
            {
                if (e is Diagram || !seen.Add(e.Id)) continue;
                Append(sb, e);
                foreach (var r in m.RelationshipsOf(e)) rels.TryAdd(r.Id, r);
            }
        }
        foreach (var r in rels.Values.Where(r => !seen.Contains(r.Id))) Append(sb, r);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())), 0, 10).ToLowerInvariant();
    }

    /// <summary>A short content hash of the whole model (ignoring results, documentation and diagrams), used as the model version.</summary>
    public static string ModelFingerprint(ModelRepository m)
    {
        var sb = new StringBuilder();
        foreach (var e in m.AllElements.Where(e => e is not Diagram).OrderBy(e => e.Id, StringComparer.Ordinal)) Append(sb, e);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())), 0, 4).ToLowerInvariant();
    }

    private static void Append(StringBuilder sb, Element e)
    {
        sb.Append(e.Id).Append('|').Append((int)e.Kind).Append('|').Append(e.Owner?.Id);
        if (e is Relationship r) sb.Append('|').Append(r.Source.Id).Append('>').Append(r.Target.Id);
        foreach (var (k, v) in e.Fields.Where(f => !Ignored.Contains(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal))
            sb.Append('|').Append(k).Append('=').Append(v.Replace("\n", "\\n"));
        sb.Append('\n');
    }

    /// <summary>Counts of the last results over the model's requirements with criteria.</summary>
    public static (int Pass, int Fail, int Inconclusive, int Stale, int NotRun) Summarize(ModelRepository m)
    {
        int pass = 0, fail = 0, inc = 0, stale = 0, none = 0;
        foreach (var r in m.OfKind(ElementKind.Requirement).Where(HasCriterion))
        {
            if (LastResult(m, r) is not { } s) { none++; continue; }
            if (s.Stale) stale++;
            switch (s.Status) { case "pass": pass++; break; case "fail": fail++; break; default: inc++; break; }
        }
        return (pass, fail, inc, stale, none);
    }
}
