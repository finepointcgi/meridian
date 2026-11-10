using System.Net;
using System.Text;
using Meridian.Core.Analysis;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Validation;

namespace Meridian.Serialization;

/// <summary>Document generation: a single self-contained HTML file with diagrams, element specs,
/// requirements, traceability coverage and validation results.</summary>
public static class HtmlReport
{
    private static string E(string s) => WebUtility.HtmlEncode(s);

    public static string Generate(ModelRepository model)
    {
        var sb = new StringBuilder();
        var cov = Traceability.Coverage(model);
        var issues = new ModelValidator().Validate(model);
        sb.Append($$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'">
            <title>{{E(model.Root.Name)}} — Model Report</title>
            <style>
            body{font-family:-apple-system,Segoe UI,Helvetica,Arial,sans-serif;margin:0;color:#1d2330;background:#fff}
            main{max-width:1100px;margin:0 auto;padding:24px 16px}
            h1{font-size:28px;margin:0 0 4px} h2{margin-top:40px;border-bottom:1px solid #dde;padding-bottom:6px} h3{margin-top:28px}
            table{border-collapse:collapse;width:100%;font-size:14px;margin:8px 0} th,td{border:1px solid #dde;padding:6px 8px;text-align:left;vertical-align:top}
            th{background:#f3f5f9} .muted{color:#667} .kw{color:#557;font-size:12px}
            .stats{display:flex;gap:16px;flex-wrap:wrap} .stat{border:1px solid #dde;border-radius:8px;padding:12px 16px;min-width:150px}
            .stat b{display:block;font-size:24px} .diagram{overflow-x:auto;border:1px solid #dde;border-radius:6px;padding:8px;background:#fafbfc}
            .Error{color:#b00020} .Warning{color:#9a6700} .Info{color:#0b5cad} nav a{display:block;padding:2px 0}
            </style></head><body><main>
            <h1>{{E(model.Root.Name)}}</h1><div class="muted">Generated {{DateTime.Now:yyyy-MM-dd HH:mm}} by Meridian</div>
            """);
        if (!string.IsNullOrWhiteSpace(model.Root.Documentation)) sb.Append($"<p>{E(model.Root.Documentation)}</p>");

        var elements = model.AllElements.Where(e => e is not Relationship && e is not Diagram && e.Kind != ElementKind.Model).ToList();
        sb.Append("<div class=\"stats\">");
        Stat(sb, "Elements", elements.Count.ToString());
        Stat(sb, "Relationships", model.Relationships.Count().ToString());
        Stat(sb, "Diagrams", model.Diagrams.Count().ToString());
        Stat(sb, "Requirements satisfied", $"{cov.Satisfied}/{cov.Total}");
        Stat(sb, "Requirements verified", $"{cov.Verified}/{cov.Total}");
        Stat(sb, "Validation errors", issues.Count(i => i.Severity == Severity.Error).ToString());
        sb.Append("</div>");

        sb.Append("<h2>Diagrams</h2>");
        foreach (var d in model.Diagrams.OrderBy(d => d.QualifiedName))
        {
            sb.Append($"<h3 id=\"{E(d.Id)}\">{E(d.Name)} <span class=\"kw\">{E(Metamodel.DisplayName(d.DiagramType))}</span></h3>");
            if (!string.IsNullOrWhiteSpace(d.Documentation)) sb.Append($"<p>{E(d.Documentation)}</p>");
            sb.Append("<div class=\"diagram\">").Append(SvgRenderer.Render(model, d)).Append("</div>");
        }

        var reqs = model.OfKind(ElementKind.Requirement).OrderBy(r => r.Get(FieldKeys.RequirementId)).ToList();
        if (reqs.Count > 0)
        {
            sb.Append("<h2>Requirements</h2><table><tr><th>Id</th><th>Name</th><th>Text</th><th>Satisfied by</th><th>Verified by</th></tr>");
            foreach (var r in reqs)
                sb.Append($"<tr id=\"{E(r.Id)}\"><td>{E(r.Get(FieldKeys.RequirementId))}</td><td>{E(r.Name)}</td><td>{E(r.Get(FieldKeys.Text))}</td>" +
                          $"<td>{Links(model.Incoming(r, ElementKind.Satisfy).Select(x => x.Source))}</td><td>{Links(model.Incoming(r, ElementKind.Verify).Select(x => x.Source))}</td></tr>");
            sb.Append("</table>");
        }

        sb.Append("<h2>Model elements</h2>");
        foreach (var pkg in elements.Where(e => e.Kind == ElementKind.Package).Prepend(model.Root).OrderBy(p => p.QualifiedName))
        {
            var members = pkg.OwnedElements.Where(e => e is not Relationship && e is not Diagram && e.Kind != ElementKind.Package && e.Kind != ElementKind.Requirement).ToList();
            if (members.Count == 0) continue;
            sb.Append($"<h3>{E(pkg.QualifiedName)}</h3>");
            foreach (var e in members) ElementSpec(sb, model, e);
        }

        if (issues.Count > 0)
        {
            sb.Append("<h2>Validation</h2><table><tr><th>Severity</th><th>Rule</th><th>Element</th><th>Message</th></tr>");
            foreach (var i in issues)
                sb.Append($"<tr><td class=\"{i.Severity}\">{i.Severity}</td><td>{E(i.RuleId)}</td><td>{E(i.Element.QualifiedName)}</td><td>{E(i.Message)}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void Stat(StringBuilder sb, string label, string value) =>
        sb.Append($"<div class=\"stat\"><b>{E(value)}</b><span class=\"muted\">{E(label)}</span></div>");

    private static string Links(IEnumerable<Element> els) =>
        string.Join(", ", els.Select(e => $"<a href=\"#{E(e.Id)}\">{E(e.DisplayName)}</a>"));

    private static void ElementSpec(StringBuilder sb, ModelRepository m, Element e)
    {
        var kw = Metamodel.Keyword(e.Kind) is { } k ? $"«{k}» " : "";
        sb.Append($"<h4 id=\"{E(e.Id)}\"><span class=\"kw\">{E(kw)}{E(Metamodel.DisplayName(e.Kind))}</span> {E(e.DisplayName)}</h4>");
        if (!string.IsNullOrWhiteSpace(e.Documentation)) sb.Append($"<p>{E(e.Documentation)}</p>");
        var features = e.OwnedElements.Where(c => c.Kind is ElementKind.Property or ElementKind.Port or ElementKind.Operation or ElementKind.EnumerationLiteral).ToList();
        if (features.Count > 0)
        {
            sb.Append("<table><tr><th>Feature</th><th>Kind</th><th>Description</th></tr>");
            foreach (var f in features)
            {
                var line = f.Kind == ElementKind.Operation ? f.Name + (f.Get(FieldKeys.Signature) is { Length: > 0 } s ? s : "()") :
                           f.Kind == ElementKind.EnumerationLiteral ? f.Name : Notation.FeatureLine(m, f);
                sb.Append($"<tr><td><code>{E(line)}</code></td><td>{Metamodel.DisplayName(f.Kind)}</td><td>{E(f.Documentation)}</td></tr>");
            }
            sb.Append("</table>");
        }
        var rels = m.RelationshipsOf(e).Where(r => r.Kind is not (ElementKind.Transition or ElementKind.ControlFlow or ElementKind.ObjectFlow)).ToList();
        if (rels.Count > 0)
        {
            sb.Append("<table><tr><th>Relationship</th><th>Direction</th><th>Element</th></tr>");
            foreach (var r in rels)
            {
                var outgoing = ReferenceEquals(r.Source, e);
                var other = outgoing ? r.Target : r.Source;
                sb.Append($"<tr><td>{Metamodel.DisplayName(r.Kind)}</td><td>{(outgoing ? "→" : "←")}</td><td><a href=\"#{E(other.Id)}\">{E(other.QualifiedName)}</a></td></tr>");
            }
            sb.Append("</table>");
        }
    }
}
