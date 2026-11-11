using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Meridian.Core.Language;
using Meridian.Core.Libraries;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>Where a Cameo used project lives, from its usage URI.</summary>
public enum MagicDrawProjectLocation { Unknown, Local, TeamworkCloud }

/// <summary>What became of a used project's content in the import.</summary>
public enum MagicDrawUsageOutcome
{
    /// <summary>Only referenced: its elements are not in the file (types kept by name, relationships to it kept aside).</summary>
    Referenced,
    /// <summary>Its shared packages are in the file and were imported as ordinary (copied) packages.</summary>
    Copied,
    /// <summary>An existing Meridian project is used instead (see <see cref="MagicDrawImporter.UseLibraries"/>).</summary>
    Linked,
}

/// <summary>
/// A Cameo used project (module) an import refers to: its display name, its Cameo project id if known, and the ids referenced in it.
/// <see cref="Uri"/> is the usage's <c>usedProjectURI</c> (<c>local:/…</c>, <c>twcloud:/…</c>) when the project metadata has one;
/// <see cref="SharedPackageIds"/> the ids of its shared packages mounted in this project (from the usage's mount points).
/// </summary>
public sealed record MagicDrawUsedProject(string Name, string? ProjectId, IReadOnlyCollection<string> ReferencedIds)
{
    public string? Uri { get; init; }
    public MagicDrawProjectLocation Location { get; init; }
    public IReadOnlyCollection<string> SharedPackageIds { get; init; } = [];
    /// <summary>Shared packages of this project that are in the file (and so were copied into the import).</summary>
    public IReadOnlyCollection<string> CopiedPackageIds { get; init; } = [];
    public MagicDrawUsageOutcome Outcome { get; init; }
    /// <summary>The Meridian project used instead, when <see cref="Outcome"/> is <see cref="MagicDrawUsageOutcome.Linked"/>.</summary>
    public string? LinkedTo { get; init; }
    /// <summary>How it was found: "metadata" (project usages), "proxy" (used-project proxy entry), "reference" (hrefs only).</summary>
    public string Source { get; init; } = "reference";
}

/// <summary>A feature typed by an element of a used project (imported as the type's name).</summary>
public sealed record MagicDrawExternalType(string ElementId, string TargetId);

/// <summary>A relationship with an end in a used project (not imported: its end is not in the file).</summary>
public sealed record MagicDrawExternalRelationship(ElementKind Kind, string? Id, string SourceId, string TargetId, string OwnerId, string? Name);

public sealed partial class MagicDrawImportReport
{
    private sealed class Used(string name)
    {
        public string Name = name;
        public string? Id, Uri, LinkedTo;
        public MagicDrawProjectLocation Location;
        public string Source = "reference";
        public readonly HashSet<string> Refs = new(StringComparer.Ordinal);
        public readonly HashSet<string> Shared = new(StringComparer.Ordinal);
        public readonly HashSet<string> Copied = new(StringComparer.Ordinal);
    }

    private readonly List<Used> _used = [];
    private readonly SortedSet<string> _standard = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Used projects (modules) the imported project refers to, other than the standard SysML / UML profiles.</summary>
    public IReadOnlyList<MagicDrawUsedProject> UsedProjects =>
        _used.Select(u => new MagicDrawUsedProject(u.Name, u.Id, u.Refs)
        {
            Uri = u.Uri, Location = u.Location, SharedPackageIds = u.Shared, CopiedPackageIds = u.Copied, LinkedTo = u.LinkedTo, Source = u.Source,
            Outcome = u.LinkedTo != null ? MagicDrawUsageOutcome.Linked : u.Copied.Count > 0 ? MagicDrawUsageOutcome.Copied : MagicDrawUsageOutcome.Referenced,
        }).OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Standard profiles and libraries the project uses (SysML, UML Standard Profile, MD Customization …), which Meridian has built in.</summary>
    public IReadOnlyCollection<string> StandardProfiles => _standard;

    /// <summary>Ids of the packages the imported project itself shares (its own <c>sharePoints</c>), when it is a library.</summary>
    public SortedSet<string> SharedPackages { get; } = new(StringComparer.Ordinal);

    /// <summary>Features typed by used-project elements (see <see cref="MagicDrawImporter.UseLibraries"/>).</summary>
    public List<MagicDrawExternalType> ExternalTypes { get; } = [];

    /// <summary>Relationships to used-project elements that could not be imported without them.</summary>
    public List<MagicDrawExternalRelationship> ExternalRelationships { get; } = [];

    /// <summary>The Cameo id of the imported project itself (from its project metadata), when the file has one.</summary>
    public string? ProjectId { get; internal set; }

    private Used? FindUsed(string name, string? id) =>
        (id is null ? null : _used.FirstOrDefault(u => string.Equals(u.Id, id, StringComparison.OrdinalIgnoreCase)))
        ?? _used.FirstOrDefault(u => MagicDrawImporter.MatchKey(u.Name) == MagicDrawImporter.MatchKey(name));

    internal void UsedProject(string name, string? id = null, string? referencedId = null, string? uri = null, MagicDrawProjectLocation location = MagicDrawProjectLocation.Unknown,
        string? source = null, IEnumerable<string>? shared = null)
    {
        var u = FindUsed(name, id);
        if (u is null) _used.Add(u = new Used(name));
        else if (u.Name.StartsWith(MagicDrawImporter.TeamworkPlaceholder, StringComparison.Ordinal) && !name.StartsWith(MagicDrawImporter.TeamworkPlaceholder, StringComparison.Ordinal))
            u.Name = name;   // a real name found later replaces "Teamwork Cloud project …"
        u.Id ??= id;
        u.Uri ??= uri;
        if (u.Location == MagicDrawProjectLocation.Unknown) u.Location = location;
        // The strongest evidence wins: metadata > proxy > reference.
        if (source == "metadata" || source == "proxy" && u.Source == "reference") u.Source = source;
        if (referencedId is not null) u.Refs.Add(referencedId);
        if (shared is not null) u.Shared.UnionWith(shared);
    }

    internal void StandardProfile(string name) => _standard.Add(name);

    /// <summary>Marks the shared packages of the used projects that are in the imported model (copied with it).</summary>
    internal void NoteCopies(Func<string, bool> inModel)
    {
        foreach (var u in _used) u.Copied.UnionWith(u.Shared.Where(inModel));
    }

    internal void Linked(string usedName, string meridianProject)
    {
        if (FindUsed(usedName, null) is { } u) u.LinkedTo = meridianProject;
    }

    private IEnumerable<string> UsedProjectLines()
    {
        if (_used.Count == 0 && _standard.Count == 0) yield break;
        if (_used.Count > 0)
        {
            yield return "  Used projects (Cameo modules):";
            foreach (var u in UsedProjects)
            {
                var where = u.Uri is { } uri ? $" — {uri}" : "";
                var id = u.ProjectId is { } pid ? $", id {pid}" : "";
                var what = u.Outcome switch
                {
                    MagicDrawUsageOutcome.Linked => $"linked: uses the Meridian project “{u.LinkedTo}” instead of a copy",
                    MagicDrawUsageOutcome.Copied => $"copied: {u.CopiedPackageIds.Count} shared package(s) were in the file and were imported into this project",
                    _ => "found, not in the file: references to it are kept by name (or by id once it is used from a Meridian project)",
                };
                yield return $"    {u.Name} ({u.Location switch { MagicDrawProjectLocation.TeamworkCloud => "Teamwork Cloud", MagicDrawProjectLocation.Local => "local file", _ => "location unknown" }}{id}{where}): {what}; {u.ReferencedIds.Count} referenced element(s).";
            }
        }
        if (_standard.Count > 0) yield return "  Standard profiles used (built into Meridian, not copied): " + string.Join(", ", _standard) + ".";
    }
}

public static partial class MagicDrawImporter
{
    /// <summary>Field on an imported model's root: the Cameo project id (matches it when another import uses it).</summary>
    public const string ProjectIdField = "md:projectId";

    /// <summary>Display name prefix of a Teamwork Cloud project whose name the file does not state.</summary>
    public const string TeamworkPlaceholder = "Teamwork Cloud project ";

    // Modules every Cameo project uses (profiles, libraries shipped with the tool): never offered as used projects.
    private static readonly string[] StandardModules =
        ["SysML", "UML_Standard_Profile", "UML Standard Profile", "MD_customization", "MD customization", "MagicDraw", "StandardProfile", "PrimitiveTypes", "QUDV",
         "ISO80000", "SI_", "SI ", "SIDefinitions", "Matrix", "Validation", "Traceability", "Requirement_Customization", "MBSE", "Cameo", "UAF", "BPMN"];

    /// <summary>
    /// Ids of the root packages of profiles shipped with MagicDraw / Cameo, as they appear in hrefs into them (SysML profile, UML Standard
    /// Profile). Seeing one as a shared package marks a usage as a standard profile whatever its file is called.
    /// </summary>
    internal static readonly HashSet<string> StandardPackageIds = new(StringComparer.Ordinal)
    {
        "_11_5EAPbeta_be00301_1147424179914_458922_958",   // SysML profile
        "_9_0_be00301_1108053761194_467635_11463",          // UML Standard Profile
    };

    /// <summary>Is <paramref name="name"/> (a module file name without extension, or a project name) one of the standard profiles / libraries?</summary>
    public static bool IsStandardModule(string name) => StandardModules.Any(s => name.StartsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>The used-project (module) name of an href such as "Standard%20Parts.mdzip#_123", or null for standard or web references.</summary>
    public static string? ModuleOf(string? href)
    {
        if (string.IsNullOrEmpty(href)) return null;
        var (name, location) = UsedProjectName(href);
        if (name is null || location == MagicDrawProjectLocation.TeamworkCloud || IsStandardModule(name)) return null;
        return name;
    }

    /// <summary>
    /// Display name and location of a used project from its URI or href: <c>local:/Standard%20Parts.mdzip</c>,
    /// <c>file:/C:/models/Parts.mdzip</c> or <c>Parts.mdzip#_id</c> give the file name without extension;
    /// <c>twcloud:/&lt;project id&gt;/&lt;branch id&gt;</c> (and <c>teamwork:</c>) give the <c>projectName</c> / <c>name</c> query
    /// parameter when there is one, else "Teamwork Cloud project &lt;id&gt;". Web URIs (<c>http://…</c>) give null.
    /// </summary>
    public static (string? Name, MagicDrawProjectLocation Location) UsedProjectName(string uri)
    {
        var u = uri.Trim();
        var hash = u.IndexOf('#');
        if (hash >= 0) u = u[..hash];
        if (u.Length == 0) return (null, MagicDrawProjectLocation.Unknown);
        string? query = null;
        var q = u.IndexOf('?');
        if (q >= 0) { query = u[(q + 1)..]; u = u[..q]; }
        var scheme = Regex.Match(u, "^([A-Za-z][A-Za-z0-9+.-]*):");
        var s = scheme.Success ? scheme.Groups[1].Value.ToLowerInvariant() : "";
        if (s is "twcloud" or "teamwork" or "teamworkcloud" or "tw")
        {
            foreach (var kv in (query ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries))
                if (kv.Split('=', 2) is [var k, var v] && k.ToLowerInvariant() is "projectname" or "name" && v.Length > 0)
                    return (WebUtility.UrlDecode(v), MagicDrawProjectLocation.TeamworkCloud);
            var parts = u[(s.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            var id = parts.FirstOrDefault(p => p.Length > 0 && !p.Contains('.') && !p.Contains(':')) ?? parts.LastOrDefault() ?? "";
            return (TeamworkPlaceholder + (id.Length > 8 ? id[..8] : id), MagicDrawProjectLocation.TeamworkCloud);
        }
        if (s is "http" or "https") return (null, MagicDrawProjectLocation.Unknown);
        var file = WebUtility.UrlDecode((s.Length > 1 ? u[(s.Length + 1)..] : u).Replace('\\', '/'));
        file = file[(file.LastIndexOf('/') + 1)..];
        var name = Regex.Replace(file, @"\.(mdzip|mdxml|xml|xmi)$", "", RegexOptions.IgnoreCase);
        return (name.Length == 0 ? null : name, MagicDrawProjectLocation.Local);
    }

    /// <summary>Normalised name for matching a Cameo module with a Meridian project ("Standard_Parts" = "Standard Parts").</summary>
    public static string MatchKey(string name) => new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private sealed partial class Builder
    {
        /// <summary>Records which used project an href'ed reference in <paramref name="x"/> to <paramref name="id"/> points into.</summary>
        private string? NoteModuleReference(XElement x, string id)
        {
            foreach (var r in x.DescendantsAndSelf())
            {
                var href = r.Attribute("href")?.Value;
                if (href is null || !href.EndsWith("#" + id, StringComparison.Ordinal)) continue;
                // Files (Parts.mdzip#id, local:/…) and Teamwork Cloud projects (twcloud:/…#id); not standard profiles or web URIs.
                var (module, location) = UsedProjectName(href);
                if (module is null || IsStandardModule(module) || location == MagicDrawProjectLocation.Unknown) continue;
                _report.UsedProject(module, referencedId: id, location: location);
                return module;
            }
            return null;
        }

        /// <summary>A type from a used project: remembered so a usage can resolve it later.</summary>
        private void NoteExternalType(Element el, XElement typed)
        {
            var te = Child(typed, "type");
            var id = Ref(typed, "type");
            if (id is null || te is null) return;
            if (NoteModuleReference(te, id) is not null) _report.ExternalTypes.Add(new MagicDrawExternalType(el.Id, id));
        }

        /// <summary>A relationship whose end is in a used project.</summary>
        private bool NoteExternalRelationship(PendingRel p, ElementKind kind, string? src, string? tgt)
        {
            if (src is null || tgt is null) return false;
            var srcExternal = !_map.ContainsKey(src) && NoteModuleReference(p.X, src) is not null;
            var tgtExternal = !_map.ContainsKey(tgt) && NoteModuleReference(p.X, tgt) is not null;
            if (!srcExternal && !tgtExternal) return false;
            _report.ExternalRelationships.Add(new MagicDrawExternalRelationship(kind, p.Id, _map.TryGetValue(src, out var s) ? s.Id : src,
                _map.TryGetValue(tgt, out var t) ? t.Id : tgt, p.Owner.Id, Attr(p.X, "name")));
            return true;
        }

        /// <summary>Is <paramref name="entry"/> a project metadata resource (the project's usages, shared packages and id)?</summary>
        private static bool IsProjectMetadata(string entry)
        {
            var name = entry[(entry.LastIndexOf('/') + 1)..];
            return name.EndsWith("ci.metamodel.project", StringComparison.OrdinalIgnoreCase) || name.Equals("project.xmi", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".projectusages", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Used projects from the project metadata (<c>com.nomagic.ci.metamodel.project</c> / <c>project.xmi</c>: <c>projectUsages</c>
        /// with <c>usedProjectURI</c> and <c>mountPoints</c>, the project's own <c>sharePoints</c>) and from used-project proxy entries
        /// (<c>proxy.local__Name.mdzip</c>, <c>proxy.twcloud__id</c>). Standard profiles are recorded apart from user libraries.
        /// </summary>
        private void ReadProjectMetadata()
        {
            foreach (var entry in _src.OtherEntries.Where(IsProjectMetadata))
            {
                try
                {
                    using var s = _src.OpenBinary(entry);
                    if (s is null) continue;
                    using var reader = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBinaryBytes });
                    var doc = XDocument.Load(reader);
                    _usedBinaries.Add(entry);
                    ReadProjectDocument(doc);
                }
                catch (XmlException ex) { _report.Warn($"Project metadata {entry} is not readable ({ex.Message}); used projects were detected from references only."); }
            }
            foreach (var entry in _src.OtherEntries)
            {
                var name = entry[(entry.LastIndexOf('/') + 1)..];
                var proxy = Regex.Match(name, @"^proxy\.([A-Za-z]+)__(.+?)(\.mdzip)?$", RegexOptions.IgnoreCase);
                if (!proxy.Success) continue;
                var scheme = proxy.Groups[1].Value.ToLowerInvariant();
                var rest = WebUtility.UrlDecode(proxy.Groups[2].Value);
                var (display, location) = scheme == "twcloud" ? UsedProjectName("twcloud:/" + rest) : UsedProjectName(rest + ".mdzip");
                if (display is null) continue;
                if (IsStandardModule(display)) { _report.StandardProfile(StandardDisplayName(display)); continue; }
                _report.UsedProject(display, id: scheme == "twcloud" ? rest : null, location: location, source: "proxy");
            }
            // Shared packages of used projects that are in the file were copied with it.
            _report.NoteCopies(id => _map.ContainsKey(id));
        }

        private void ReadProjectDocument(XDocument doc)
        {
            if (doc.Root is not { } root) return;
            // Plain (un-namespaced) attributes only: xmi:id is not a project id.
            static string? A(XElement x, params string[] names) =>
                x.Attributes().FirstOrDefault(a => a.Name.Namespace == XNamespace.None && names.Contains(a.Name.LocalName, StringComparer.Ordinal) && a.Value.Length > 0)?.Value;
            static string? Fragment(string? href) => href is null ? null : href.LastIndexOf('#') is var h and >= 0 ? href[(h + 1)..] : null;
            // Reference features are an attribute holding an id, or a child element with href="…#id".
            static IEnumerable<string> RefIds(XElement x, params string[] names)
            {
                foreach (var a in x.Attributes().Where(a => names.Contains(a.Name.LocalName, StringComparer.OrdinalIgnoreCase)))
                    foreach (var v in a.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) yield return Fragment(v) ?? v;
                foreach (var c in x.Elements().Where(c => names.Contains(c.Name.LocalName, StringComparer.OrdinalIgnoreCase)))
                    if ((Fragment(c.Attribute("href")?.Value) ?? IdRef(c)) is { } id) yield return id;
            }
            // key / value property children (<properties key="…" value="…"/>), as Cameo stores project and usage options.
            static string? Prop(XElement x, params string[] keys) =>
                x.Elements().Where(c => c.Name.LocalName is "properties" or "property")
                 .FirstOrDefault(c => keys.Contains(A(c, "key", "name") ?? "", StringComparer.OrdinalIgnoreCase)) is { } p ? A(p, "value") : null;

            var project = root.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == "Project" || (XmiType(e) ?? "").EndsWith("Project", StringComparison.Ordinal)) ?? root;
            var ownId = A(project, "projectID", "projectId", "ID")
                ?? (A(project, "id") is { } pid && pid.StartsWith("PROJECT-", StringComparison.OrdinalIgnoreCase) ? pid : null)
                ?? Prop(project, "LOCAL_PROJECT_ID", "PROJECT_ID", "projectID");
            if (ownId is not null) _report.ProjectId ??= ownId;

            var usages = project.Descendants().Where(e => e.Name.LocalName is "projectUsages" or "ProjectUsage" or "usedProjects"
                                                         || (XmiType(e) ?? "").EndsWith("ProjectUsage", StringComparison.Ordinal)).ToList();
            foreach (var u in usages)
            {
                var usedRef = u.Elements().FirstOrDefault(c => c.Name.LocalName == "usedProject")?.Attribute("href")?.Value;
                var uri = A(u, "usedProjectURI", "usedProjectUri", "uri", "href") ?? usedRef;
                if (uri is null) continue;
                var (name, location) = UsedProjectName(uri);
                var explicitName = A(u, "usedProjectName", "projectName", "name") ?? Prop(u, "PROJECT_NAME", "projectName", "name");
                if (explicitName is not null) name = explicitName;
                if (name is null) continue;
                var id = A(u, "usedProjectID", "usedProjectId", "projectID", "projectId") ?? Prop(u, "LOCAL_PROJECT_ID", "PROJECT_ID", "projectID");
                var shared = u.Descendants().Where(e => e.Name.LocalName is "mountPoints" or "mountPoint" or "MountPoint")
                    .SelectMany(m => RefIds(m, "mountedPoint", "mountedElement", "object"))
                    .Distinct(StringComparer.Ordinal).ToList();
                if (IsStandardModule(name) || shared.Any(StandardPackageIds.Contains)) { _report.StandardProfile(StandardDisplayName(name)); continue; }
                _report.UsedProject(name, id, uri: uri, location: location, source: "metadata", shared: shared);
            }
            // The project's own shared packages (when the imported project is itself a library).
            foreach (var sp in project.Elements().Where(e => e.Name.LocalName is "sharePoints" or "sharePoint" or "SharePoint"))
                foreach (var id in RefIds(sp, "object", "sharedElement", "element"))
                    _report.SharedPackages.Add(id);
        }

        /// <summary>The usual name of a standard profile file ("UML_Standard_Profile" → "UML Standard Profile").</summary>
        private static string StandardDisplayName(string module)
        {
            var n = module.Replace('_', ' ').Trim();
            if (n.StartsWith("SysML", StringComparison.OrdinalIgnoreCase) && !n.Contains("customization", StringComparison.OrdinalIgnoreCase)) return "SysML";
            if (n.StartsWith("UML Standard Profile", StringComparison.OrdinalIgnoreCase)) return "UML Standard Profile";
            if (n.StartsWith("MD customization", StringComparison.OrdinalIgnoreCase)) return "MD Customization for SysML";
            return n;
        }
    }

    /// <summary>
    /// Instead of copies of a used project's content, makes an imported model refer to an existing Meridian project that holds it
    /// (docs/LIBRARIES.md, "Cameo import"): copies of the used packages' elements are removed (references to them keep their ids
    /// and resolve to the library once the usage exists), features typed by used-project elements get their ids back, and
    /// relationships to them that could not be imported are created. Returns how many elements were removed, retyped and linked.
    /// With <paramref name="used"/>, the report lists that used project as linked to <paramref name="existingName"/>.
    /// </summary>
    public static (int Removed, int Retyped, int Linked) UseLibraries(ModelRepository imported, MagicDrawImportReport report, ModelRepository existing, IReadOnlyCollection<string> packageIds,
        MagicDrawUsedProject? used = null, string? existingName = null)
    {
        if (used is not null) report.Linked(used.Name, existingName ?? existing.Root.Name);
        var library = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in packageIds)
            if (existing.Find(id) is { } pkg)
            {
                library.Add(pkg.Id);
                foreach (var d in pkg.Descendants()) library.Add(d.Id);
            }
        var removed = 0;
        foreach (var copy in imported.AllElements.Where(e => e.Owner != null && library.Contains(e.Id) && !library.Contains(e.Owner.Id)).ToList())
        {
            removed += 1 + copy.Descendants().Count();
            imported.DetachForRedaction(copy);
        }
        var retyped = 0;
        foreach (var t in report.ExternalTypes)
            if (library.Contains(t.TargetId) && imported.Find(t.ElementId) is { } el)
            {
                ModelRepository.SetFieldForLoad(el, FieldKeys.Type, t.TargetId);
                retyped++;
            }
        var linked = 0;
        foreach (var r in report.ExternalRelationships)
        {
            Element? End(string id) => imported.Find(id) ?? (library.Contains(id) ? MissingReference.Element(id) : null);
            if (End(r.SourceId) is not { } s || End(r.TargetId) is not { } t) continue;
            var owner = imported.Find(r.OwnerId) ?? imported.Root;
            if (!Metamodel.CanOwn(owner.Kind, r.Kind)) owner = imported.DefaultOwnerFor(owner, r.Kind);
            var rel = new Relationship(r.Kind, s, t, r.Id is { } rid && imported.Find(rid) is null ? rid : null);
            ModelRepository.SetFieldForLoad(rel, FieldKeys.Name, r.Name);
            imported.AttachForLoad(owner, rel);
            linked++;
        }
        imported.History.Clear();
        return (removed, retyped, linked);
    }

    /// <summary>
    /// The packages of an existing project to use for a Cameo used project: the top-level packages that contain the elements the
    /// import refers to, or every top-level package when it refers to none. The used project's shared packages (from its mount
    /// points in the project metadata) come first when the existing project has them.
    /// </summary>
    public static IReadOnlyList<string> SuggestPackages(ModelRepository existing, MagicDrawUsedProject used)
    {
        static Element? TopPackage(Element e)
        {
            Element? top = null;
            for (var a = e; a.Owner != null; a = a.Owner)
                if (a.Kind == ElementKind.Package) top = a;
            return top;
        }
        var shared = used.SharedPackageIds.Select(existing.Find).OfType<Element>().Where(e => e.Kind is ElementKind.Package or ElementKind.Profile).Select(e => e.Id).ToList();
        if (shared.Count > 0) return shared;
        var packages = used.ReferencedIds.Select(existing.Find).OfType<Element>().Select(TopPackage).OfType<Element>().Distinct().ToList();
        if (packages.Count == 0) packages = existing.Root.OwnedElements.Where(e => e.Kind == ElementKind.Package).ToList();
        return packages.Select(p => p.Id).ToList();
    }
}
