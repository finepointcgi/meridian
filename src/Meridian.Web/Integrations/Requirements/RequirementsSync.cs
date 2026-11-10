using System.Text.Json;
using Meridian.Core.Analysis;
using Meridian.Core.Model;

namespace Meridian.Web.Integrations.Requirements;

/// <summary>
/// Sync state of an imported requirement, stored in the element field <see cref="ExternalRequirements.FieldKey"/> so it
/// is saved, versioned and replicated with the model. <see cref="Title"/> and <see cref="Text"/> are the values both
/// sides agreed on at the last sync (the base of the three-way comparison).
/// </summary>
public sealed record ExternalSync
{
    public string Connection { get; init; } = "";
    public string Id { get; init; } = "";
    public string Url { get; init; } = "";
    public string Identifier { get; init; } = "";
    public string Revision { get; init; } = "";
    public DateTime Synced { get; init; }
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    /// <summary>Links already published to the tool, as <c>kind|target</c>.</summary>
    public List<string> Published { get; init; } = [];
}

public static class ExternalRequirements
{
    /// <summary>Element field holding the <see cref="ExternalSync"/> JSON (not shown among the editable fields).</summary>
    public const string FieldKey = "externalSync";
    /// <summary>Visible requirement attributes (Inspector → custom attributes) with the stable external id, URL and revision.</summary>
    public const string IdAttribute = ReqFields.CustomPrefix + "External ID";
    public const string UrlAttribute = ReqFields.CustomPrefix + "External URL";
    public const string RevisionAttribute = ReqFields.CustomPrefix + "External revision";

    public static ExternalSync? Read(Element e)
    {
        var raw = e.Get(FieldKey);
        if (raw.Length == 0) return null;
        try { return JsonSerializer.Deserialize<ExternalSync>(raw, IntegrationJson.Options); }
        catch (JsonException) { return null; }
    }

    public static void Write(ModelRepository model, Element e, ExternalSync s)
    {
        SetIfChanged(model, e, FieldKey, JsonSerializer.Serialize(s, IntegrationJson.Options));
        SetIfChanged(model, e, IdAttribute, s.Identifier.Length > 0 ? s.Identifier : s.Id);
        SetIfChanged(model, e, UrlAttribute, s.Url);
        SetIfChanged(model, e, RevisionAttribute, s.Revision.Length > 0 ? s.Revision : null);
    }

    internal static void SetIfChanged(ModelRepository model, Element e, string key, string? value)
    {
        if (e.Get(key) != (value ?? "")) model.Set(e, key, value);
    }

    /// <summary>Requirements imported through <paramref name="connection"/>.</summary>
    public static IEnumerable<(Element Element, ExternalSync Sync)> Of(ModelRepository model, Guid connection)
    {
        var c = connection.ToString();
        foreach (var e in model.OfKind(ElementKind.Requirement))
            if (e.Fields.ContainsKey(FieldKey) && Read(e) is { } s && s.Connection == c) yield return (e, s);
    }

    /// <summary>Line endings and surrounding blanks do not count as a change.</summary>
    public static string Norm(string? s) => (s ?? "").Replace("\r\n", "\n").Trim();
}

/// <summary>Both Meridian and the tool changed a requirement since the last sync.</summary>
public sealed record RequirementConflict(string ElementId, string ExternalId, string Identifier, string Url,
    string BaseTitle, string BaseText, string LocalTitle, string LocalText, string RemoteTitle, string RemoteText, string RemoteRevision);

public enum ConflictChoice { KeepMeridian, TakeRemote }

/// <summary>What a sync did.</summary>
public sealed record RequirementsSyncResult(int Created, int Updated, int Unchanged, int LocalOnly, int Missing, int NewSuspectLinks,
    IReadOnlyList<RequirementConflict> Conflicts, int LinksPublished = 0, IReadOnlyList<string>? Errors = null)
{
    public IReadOnlyList<string> Problems => Errors ?? [];

    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{Created} new", $"{Updated} updated", $"{Unchanged} unchanged" };
            if (LocalOnly > 0) parts.Add($"{LocalOnly} changed only in Meridian");
            if (Missing > 0) parts.Add($"{Missing} no longer in the tool");
            if (NewSuspectLinks > 0) parts.Add($"{NewSuspectLinks} link{(NewSuspectLinks == 1 ? "" : "s")} now suspect");
            if (Conflicts.Count > 0) parts.Add($"{Conflicts.Count} conflict{(Conflicts.Count == 1 ? "" : "s")}");
            if (LinksPublished > 0) parts.Add($"{LinksPublished} link{(LinksPublished == 1 ? "" : "s")} published");
            if (Problems.Count > 0) parts.Add($"{Problems.Count} error{(Problems.Count == 1 ? "" : "s")} ({Problems[0]})");
            return string.Join(", ", parts);
        }
    }
}

/// <summary>A Meridian link waiting to be published on an imported requirement.</summary>
public sealed record PendingLink(Element Requirement, ExternalSync Sync, OutboundLink Link, string Key);

/// <summary>
/// Import and re-sync of remote requirements into a model (pure: no I/O). Remote changes go through
/// <see cref="ModelRepository.Set"/>, so the trace links of a requirement whose name or text changed become suspect
/// through the ordinary suspect-link mechanism (<see cref="SuspectLinks"/>).
/// </summary>
public static class RequirementsSync
{
    /// <summary>
    /// Creates or updates requirements from <paramref name="remote"/> in one undoable step. Requirements changed on both
    /// sides are left alone and reported as conflicts unless <paramref name="choices"/> (by element id) resolves them.
    /// </summary>
    public static RequirementsSyncResult Apply(ModelRepository model, Guid connection, string connectionName, RequirementsToolConfig config,
        IReadOnlyList<RemoteRequirement> remote, IReadOnlyDictionary<string, ConflictChoice>? choices = null, DateTime? now = null)
    {
        var at = now ?? DateTime.UtcNow;
        var conn = connection.ToString();
        var byId = ExternalRequirements.Of(model, connection).GroupBy(x => x.Sync.Id).ToDictionary(g => g.Key, g => g.First());
        var adoptable = model.OfKind(ElementKind.Requirement).Where(e => !e.Fields.ContainsKey(ExternalRequirements.FieldKey) && e.Get(FieldKeys.RequirementId).Length > 0)
            .GroupBy(e => e.Get(FieldKeys.RequirementId), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var suspectBefore = SuspectLinks.All(model).ToHashSet();
        int created = 0, updated = 0, unchanged = 0, localOnly = 0;
        var conflicts = new List<RequirementConflict>();
        Element? target = null;

        Element Target()
        {
            if (target != null) return target;
            if (model.Find(config.TargetPackage) is { Kind: ElementKind.Package } p) return target = p;
            var name = $"Imported from {connectionName}";
            return target = model.Root.OwnedElements.FirstOrDefault(x => x.Kind == ElementKind.Package && x.Name == name)
                            ?? model.Create(model.Root, ElementKind.Package, name);
        }

        ExternalSync Base(RemoteRequirement r, ExternalSync? old) => new()
        {
            Connection = conn, Id = r.ExternalId, Url = r.Url, Identifier = r.Identifier, Revision = r.Revision, Synced = at,
            Title = r.Title, Text = r.Text, Published = old?.Published ?? [],
        };

        void TakeRemote(Element e, RemoteRequirement r)
        {
            if (e.Name != r.Title) model.Rename(e, r.Title);
            if (ExternalRequirements.Norm(e.Get(FieldKeys.Text)) != ExternalRequirements.Norm(r.Text)) model.Set(e, FieldKeys.Text, r.Text.Length > 0 ? r.Text : null);
        }

        using (model.History.Transaction($"Sync requirements from {connectionName}"))
        {
            foreach (var r in remote)
            {
                if (!byId.TryGetValue(r.ExternalId, out var hit))
                {
                    if (r.Identifier.Length > 0 && adoptable.Remove(r.Identifier, out var adopt))
                    {
                        // An existing Meridian requirement with the same id (e.g. from a ReqIF import) becomes the linked copy.
                        TakeRemote(adopt, r);
                        ExternalRequirements.Write(model, adopt, Base(r, null));
                        updated++;
                        continue;
                    }
                    var e = model.Create(Target(), ElementKind.Requirement, r.Title);
                    if (r.Identifier.Length > 0) model.Set(e, FieldKeys.RequirementId, r.Identifier);
                    if (r.Text.Length > 0) model.Set(e, FieldKeys.Text, r.Text);
                    ExternalRequirements.Write(model, e, Base(r, null));
                    created++;
                    continue;
                }

                var (local, s) = hit;
                var (localTitle, localText) = (local.Name, ExternalRequirements.Norm(local.Get(FieldKeys.Text)));
                var localChanged = localTitle != s.Title || localText != ExternalRequirements.Norm(s.Text);
                var remoteChanged = (r.Revision.Length == 0 || r.Revision != s.Revision)
                                    && (r.Title != s.Title || ExternalRequirements.Norm(r.Text) != ExternalRequirements.Norm(s.Text));
                if (!remoteChanged)
                {
                    if (r.Revision != s.Revision || r.Url != s.Url) ExternalRequirements.Write(model, local, s with { Revision = r.Revision, Url = r.Url, Synced = at });
                    if (localChanged) localOnly++; else unchanged++;
                    continue;
                }
                if (!localChanged || (localTitle == r.Title && localText == ExternalRequirements.Norm(r.Text)))
                {
                    // Only the tool changed it (or both made the same change).
                    if (!localChanged) TakeRemote(local, r);
                    ExternalRequirements.Write(model, local, Base(r, s));
                    if (!localChanged) updated++; else unchanged++;
                    continue;
                }
                // Changed on both sides.
                switch (choices?.GetValueOrDefault(local.Id))
                {
                    case ConflictChoice.TakeRemote:
                        TakeRemote(local, r);
                        ExternalRequirements.Write(model, local, Base(r, s));
                        updated++;
                        break;
                    case ConflictChoice.KeepMeridian:
                        // The remote state becomes the base: Meridian's version stays and counts as a local change from now on.
                        ExternalRequirements.Write(model, local, Base(r, s));
                        localOnly++;
                        break;
                    default:
                        conflicts.Add(new RequirementConflict(local.Id, r.ExternalId, r.Identifier, r.Url, s.Title, s.Text, localTitle, local.Get(FieldKeys.Text), r.Title, r.Text, r.Revision));
                        break;
                }
            }
        }
        var seen = remote.Select(r => r.ExternalId).ToHashSet();
        var missing = byId.Keys.Count(k => !seen.Contains(k));
        var newSuspect = SuspectLinks.All(model).Count(l => !suspectBefore.Contains(l));
        return new RequirementsSyncResult(created, updated, unchanged, localOnly, missing, newSuspect, conflicts);
    }

    private static string KindOf(ElementKind k) => k switch
    {
        ElementKind.Satisfy => "satisfy",
        ElementKind.Verify => "verify",
        _ => "trace",
    };

    /// <summary>
    /// Meridian «satisfy», «verify» and «trace» links on requirements imported through <paramref name="connection"/> that
    /// were not published yet. <paramref name="oslcUrl"/> gives Meridian's OSLC URL of an element (the link target).
    /// A «trace» between two imported requirements is published once, on its source.
    /// </summary>
    public static IReadOnlyList<PendingLink> PendingLinks(ModelRepository model, Guid connection, Func<Element, string> oslcUrl)
    {
        var imported = ExternalRequirements.Of(model, connection).ToDictionary(x => x.Element, x => x.Sync);
        var list = new List<PendingLink>();
        foreach (var (req, sync) in imported)
        {
            foreach (var rel in model.RelationshipsOf(req))
            {
                if (rel.Kind is not (ElementKind.Satisfy or ElementKind.Verify or ElementKind.Trace)) continue;
                Element other;
                if (rel.Kind == ElementKind.Trace)
                {
                    other = ReferenceEquals(rel.Source, req) ? rel.Target : rel.Source;
                    if (ReferenceEquals(rel.Target, req) && imported.ContainsKey(other)) continue;   // the source requirement publishes it
                }
                else
                {
                    if (!ReferenceEquals(rel.Target, req)) continue;   // satisfy / verify point at the requirement
                    other = rel.Source;
                }
                var otherSync = imported.GetValueOrDefault(other);
                var uri = otherSync?.Url is { Length: > 0 } remoteUrl ? remoteUrl : oslcUrl(other);
                var label = other.Kind == ElementKind.Requirement && other.Get(FieldKeys.RequirementId) is { Length: > 0 } rid ? $"{rid} {other.DisplayName}" : other.DisplayName;
                var link = new OutboundLink(KindOf(rel.Kind), uri, label, otherSync?.Id);
                var key = $"{link.Kind}|{uri}";
                if (!sync.Published.Contains(key)) list.Add(new PendingLink(req, sync, link, key));
            }
        }
        return list;
    }

    /// <summary>Records links as published (one undoable step).</summary>
    public static void MarkPublished(ModelRepository model, IEnumerable<(Element Requirement, string Key)> links)
    {
        var groups = links.GroupBy(l => l.Requirement).ToList();
        if (groups.Count == 0) return;
        using (model.History.Transaction("Record published requirement links"))
            foreach (var g in groups)
            {
                if (!model.Contains(g.Key) || ExternalRequirements.Read(g.Key) is not { } s) continue;
                var published = s.Published.Union(g.Select(l => l.Key)).ToList();
                ExternalRequirements.Write(model, g.Key, s with { Published = published });
            }
    }
}
