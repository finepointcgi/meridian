using System.Net;
using System.Text;

namespace Meridian.Web.Integrations;

/// <summary>
/// HTML of the OSLC delegated UI pages other tools embed: the selection and creation dialogs (OSLC Delegated Dialogs,
/// postMessage and windowName protocols) and the small/large preview pages referenced from Compact documents.
/// Every value from the model is HTML-encoded; pages run under a CSP without remote content.
/// </summary>
public static class OslcPages
{
    /// <summary>The CSP of the dialog pages: inline style and script only, forms post back to Meridian.</summary>
    public const string DialogCsp = "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; form-action 'self'";

    /// <summary>A row in a selection dialog.</summary>
    public sealed record Choice(string Url, string Identifier, string Title, string? Detail = null);

    private static string H(string s) => WebUtility.HtmlEncode(s);

    private const string Style = """
        <style>
          :root{color-scheme:light dark}
          body{font:13px system-ui,-apple-system,Segoe UI,sans-serif;margin:0;color:#1d2330;background:#fff}
          @media (prefers-color-scheme:dark){body{color:#e4e7ee;background:#1b1e25} .row,header,footer{border-color:#30343d!important} input,select,textarea,button{background:#262a33;color:inherit}}
          header{padding:10px 12px;border-bottom:1px solid #e3e6eb;font-weight:600}
          form.search{display:flex;gap:6px;padding:8px 12px}
          form.search input{flex:1}
          input,select,textarea{padding:6px 8px;border:1px solid #c9ced8;border-radius:6px;font:inherit}
          .list{padding:0 12px;max-height:calc(100vh - 140px);overflow:auto}
          .row{display:flex;gap:8px;align-items:baseline;padding:5px 2px;border-bottom:1px solid #f0f2f5;cursor:pointer}
          .row b{white-space:nowrap} .row small{opacity:.65}
          footer{display:flex;justify-content:flex-end;gap:8px;padding:10px 12px;border-top:1px solid #e3e6eb}
          button{padding:6px 14px;border-radius:6px;border:1px solid #c9ced8;background:#f6f7f9;cursor:pointer}
          button.primary{background:#4f8cff;border-color:#4f8cff;color:#fff}
          .empty{color:#7a8396} .error{color:#c62828;padding:0 12px}
          .fields{display:flex;flex-direction:column;gap:8px;padding:12px}
          .fields label{display:flex;flex-direction:column;gap:3px;font-weight:600}
          .fields textarea{min-height:110px}
          .preview{padding:10px 12px} .preview h1{font-size:15px;margin:0 0 6px} .preview p{white-space:pre-wrap;margin:6px 0}
          .preview .meta{opacity:.7;font-size:12px} .preview ul{margin:4px 0;padding-left:18px}
        </style>
        """;

    // The response half of the OSLC delegated dialog protocols, shared by both dialogs.
    private const string RespondScript = """
          var hash = window.location.hash || "";
          if (hash.indexOf("oslc-core-windowName-1.0") >= 0 && !sessionStorage.getItem("oslcReturn")) {
            try { sessionStorage.setItem("oslcReturn", window.name); } catch (e) {}
          }
          function respond(results) {
            var json = JSON.stringify({ "oslc:results": results });
            if (hash.indexOf("oslc-core-windowName-1.0") >= 0) {
              var ret = "";
              try { ret = sessionStorage.getItem("oslcReturn") || ""; } catch (e) {}
              window.name = json;
              if (ret) window.location.href = ret;
            } else {
              var target = window.parent !== window ? window.parent : window.opener;
              if (target) target.postMessage("oslc-response:" + json, "*");
            }
          }
        """;

    /// <summary>OSLC delegated selection dialog over requirements or model elements.</summary>
    public static string Selector(string projectName, string query, string what, IReadOnlyList<Choice> choices)
    {
        var rows = new StringBuilder();
        foreach (var r in choices)
            rows.Append($"""<label class="row"><input type="checkbox" value="{H(r.Url)}" data-label="{H((r.Identifier + " " + r.Title).Trim())}"><b>{H(r.Identifier)}</b> {H(r.Title)}{(r.Detail is { Length: > 0 } d ? $" <small>{H(d)}</small>" : "")}</label>""");
        if (choices.Count == 0) rows.Append($"<p class=\"empty\">No matching {H(what)}.</p>");
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Select {{H(what)}} — Meridian</title>
            {{Style}}</head>
            <body>
            <header>Meridian — {{H(projectName)}}</header>
            <form class="search" method="get"><input name="q" value="{{H(query)}}" placeholder="Search {{H(what)}}…" autofocus><button type="submit">Search</button></form>
            <div class="list">{{rows}}</div>
            <footer><button type="button" id="cancel">Cancel</button><button type="button" class="primary" id="ok">Select</button></footer>
            <script>
            (function () {
            {{RespondScript}}
              // Keep the protocol fragment and other parameters across searches.
              document.querySelector("form.search").addEventListener("submit", function (ev) {
                ev.preventDefault();
                var params = new URLSearchParams(window.location.search);
                params.set("q", this.q.value);
                window.location.replace(window.location.pathname + "?" + params.toString() + hash);
              });
              document.getElementById("ok").addEventListener("click", function () {
                var picked = Array.prototype.slice.call(document.querySelectorAll(".row input:checked"))
                  .map(function (c) { return { "oslc:label": c.getAttribute("data-label"), "rdf:resource": c.value }; });
                respond(picked);
              });
              document.getElementById("cancel").addEventListener("click", function () { respond([]); });
            })();
            </script>
            </body></html>
            """;
    }

    /// <summary>
    /// OSLC delegated creation dialog. <paramref name="created"/> set: the page answers the embedding tool with the new
    /// resource immediately. <paramref name="token"/> is the anti-forgery token the POST must echo.
    /// </summary>
    public static string Creator(string projectName, IReadOnlyList<(string Id, string Name)> packages, string token, string? error = null,
        (string Url, string Label)? created = null, (string Title, string Id, string Text, string? Package)? values = null)
    {
        var v = values ?? ("", "", "", null);
        var options = new StringBuilder();
        foreach (var (id, name) in packages)
            options.Append($"""<option value="{H(id)}"{(id == v.Package ? " selected" : "")}>{H(name)}</option>""");
        var done = created is { } c
            ? $"respond([{{ \"oslc:label\": {System.Text.Json.JsonSerializer.Serialize(c.Label)}, \"rdf:resource\": {System.Text.Json.JsonSerializer.Serialize(c.Url)} }}]);"
            : "";
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>New requirement — Meridian</title>
            {{Style}}</head>
            <body>
            <header>New requirement in Meridian — {{H(projectName)}}</header>
            {{(error != null ? $"<p class=\"error\">{H(error)}</p>" : "")}}
            {{(created is { } cr ? $"<p class=\"preview\">Created {H(cr.Label)}.</p>" : "")}}
            <form method="post" id="create">
              <input type="hidden" name="token" value="{{H(token)}}">
              <div class="fields">
                <label>Title <input name="title" required maxlength="500" value="{{H(v.Title)}}" autofocus></label>
                <label>Requirement id <input name="identifier" maxlength="100" value="{{H(v.Id)}}" placeholder="assigned automatically"></label>
                <label>Text <textarea name="text" maxlength="20000">{{H(v.Text)}}</textarea></label>
                <label>Package <select name="package">{{options}}</select></label>
              </div>
              <footer><button type="button" id="cancel">Cancel</button><button type="submit" class="primary">Create</button></footer>
            </form>
            <script>
            (function () {
            {{RespondScript}}
              // The protocol fragment is not sent with the POST: keep it on the form's action.
              document.getElementById("create").action = window.location.pathname + window.location.search + hash;
              document.getElementById("cancel").addEventListener("click", function () { respond([]); });
              {{done}}
            })();
            </script>
            </body></html>
            """;
    }

    /// <summary>The small or large preview page of a requirement or model element (shown on link hovers).</summary>
    public static string Preview(string projectName, string kind, string identifier, string title, string text, string webUrl, bool large,
        IReadOnlyList<(string Label, IReadOnlyList<string> Names)> links)
    {
        var body = new StringBuilder();
        var shown = !large && text.Length > 280 ? text[..277] + "…" : text;
        if (shown.Length > 0) body.Append($"<p>{H(shown)}</p>");
        foreach (var (label, names) in links.Where(l => l.Names.Count > 0))
        {
            if (large)
            {
                body.Append($"<div class=\"meta\">{H(label)}</div><ul>");
                foreach (var n in names.Take(50)) body.Append($"<li>{H(n)}</li>");
                body.Append("</ul>");
            }
            else body.Append($"<div class=\"meta\">{H(label)}: {names.Count}</div>");
        }
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{H(identifier)}} {{H(title)}} — Meridian</title>
            {{Style}}</head>
            <body><div class="preview">
            <div class="meta">{{H(kind)}} · {{H(projectName)}}</div>
            <h1>{{H(identifier)}} {{H(title)}}</h1>
            {{body}}
            <div class="meta"><a href="{{H(webUrl)}}" target="_blank" rel="noopener">Open in Meridian</a></div>
            </div></body></html>
            """;
    }
}
