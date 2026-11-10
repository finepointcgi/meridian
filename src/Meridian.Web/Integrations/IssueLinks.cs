using System.Text.Json;
using Meridian.Core.Model;

namespace Meridian.Web.Integrations;

/// <summary>
/// One link from a model element to an issue. Links live in the element's <see cref="IssueLinks.FieldKey"/> field as a
/// JSON array, so they are saved with the model, versioned, and replicated to collaborators like any other edit.
/// Title, status and assignee are a cache of the tracker's values as of <see cref="Synced"/>.
/// </summary>
public sealed record IssueLink
{
    /// <summary>Id of the project's issue tracker connection.</summary>
    public string Connection { get; init; } = "";
    public string Key { get; init; } = "";
    public string Url { get; init; } = "";
    public string? Title { get; init; }
    public string? Status { get; init; }
    public string? Assignee { get; init; }
    public bool Closed { get; init; }
    /// <summary>The issue's labels, for trackers that report them (GitLab).</summary>
    public string[]? Labels { get; init; }
    public DateTime? Synced { get; init; }

    public static IssueLink From(Guid connection, IssueInfo i) => new()
    {
        Connection = connection.ToString(), Key = i.Key, Url = i.Url, Title = i.Title, Status = i.Status, Assignee = i.Assignee, Closed = i.Closed,
        Labels = i.Labels is { Length: > 0 } l ? l : null, Synced = DateTime.UtcNow,
    };

    public IssueLink Refreshed(IssueInfo i) => this with
    {
        Url = i.Url, Title = i.Title, Status = i.Status, Assignee = i.Assignee, Closed = i.Closed, Labels = i.Labels is { Length: > 0 } l ? l : null, Synced = DateTime.UtcNow,
    };
}

public static class IssueLinks
{
    /// <summary>Element field holding the links (not shown among the editable fields).</summary>
    public const string FieldKey = "issueLinks";

    public static IReadOnlyList<IssueLink> Read(Element e)
    {
        var raw = e.Get(FieldKey);
        if (raw.Length == 0) return [];
        try { return JsonSerializer.Deserialize<List<IssueLink>>(raw, IntegrationJson.Options) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string? Serialize(IEnumerable<IssueLink> links)
    {
        var list = links.ToList();
        return list.Count == 0 ? null : JsonSerializer.Serialize(list, IntegrationJson.Options);
    }

    public static bool Same(IssueLink a, IssueLink b) => a.Connection == b.Connection && string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds (or replaces) a link through the repository, so it is undoable and replicated.</summary>
    public static void Add(ModelRepository model, Element e, IssueLink link) =>
        model.Set(e, FieldKey, Serialize(Read(e).Where(l => !Same(l, link)).Append(link)));

    public static void Remove(ModelRepository model, Element e, IssueLink link) =>
        model.Set(e, FieldKey, Serialize(Read(e).Where(l => !Same(l, link))));

    public static void Replace(ModelRepository model, Element e, IReadOnlyList<IssueLink> links) => model.Set(e, FieldKey, Serialize(links));

    /// <summary>Every element in the model that carries at least one issue link.</summary>
    public static IEnumerable<Element> Linked(ModelRepository model) => model.AllElements.Where(e => e.Fields.ContainsKey(FieldKey));
}

/// <summary>Deep links back into the Meridian workbench: <c>{base}/?project={id}&amp;element={id}</c>.</summary>
public static class DeepLink
{
    public static string For(string baseUri, Guid projectId, string elementId) =>
        $"{baseUri.TrimEnd('/')}/?project={projectId}&element={Uri.EscapeDataString(elementId)}";

    public static (Guid Project, string? Element)? Parse(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return null;
        var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(u.Query);
        if (!q.TryGetValue("project", out var p) || !Guid.TryParse(p.ToString(), out var pid)) return null;
        return (pid, q.TryGetValue("element", out var el) && el.ToString().Length > 0 ? el.ToString() : null);
    }

    /// <summary>The panel name that opens the code-sync "What changed" review (<c>&amp;panel=code-changes</c>).</summary>
    public const string CodeChangesPanel = "code-changes";

    /// <summary>A link that opens a project on one of its branches, optionally with a panel open:
    /// <c>{base}/?project={id}&amp;branch={id}&amp;panel={name}</c>. An empty base gives a site-relative link.</summary>
    public static string ForBranch(string baseUri, Guid projectId, Guid branchId, string? panel = null) =>
        $"{baseUri.TrimEnd('/')}/?project={projectId}&branch={branchId}" + (panel is { Length: > 0 } ? $"&panel={Uri.EscapeDataString(panel)}" : "");

    /// <summary>The branch and panel of a deep link (both optional).</summary>
    public static (Guid? Branch, string? Panel) ParseView(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return (null, null);
        var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(u.Query);
        Guid? branch = q.TryGetValue("branch", out var b) && Guid.TryParse(b.ToString(), out var bid) ? bid : null;
        return (branch, q.TryGetValue("panel", out var p) && p.ToString().Length > 0 ? p.ToString() : null);
    }
}
