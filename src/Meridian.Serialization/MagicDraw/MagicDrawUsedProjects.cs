using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Meridian.Core.Language;
using Meridian.Core.Libraries;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>A Cameo used project (module) an import refers to: its name, its Cameo project id if known, and the ids referenced in it.</summary>
public sealed record MagicDrawUsedProject(string Name, string? ProjectId, IReadOnlyCollection<string> ReferencedIds);

/// <summary>A feature typed by an element of a used project (imported as the type's name).</summary>
public sealed record MagicDrawExternalType(string ElementId, string TargetId);

/// <summary>A relationship with an end in a used project (not imported: its end is not in the file).</summary>
public sealed record MagicDrawExternalRelationship(ElementKind Kind, string? Id, string SourceId, string TargetId, string OwnerId, string? Name);

public sealed partial class MagicDrawImportReport
{
    private readonly Dictionary<string, (string? Id, HashSet<string> Refs)> _used = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Used projects (modules) the imported project refers to, other than the standard SysML / UML profiles.</summary>
    public IReadOnlyList<MagicDrawUsedProject> UsedProjects =>
        _used.Select(kv => new MagicDrawUsedProject(kv.Key, kv.Value.Id, kv.Value.Refs)).OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Features typed by used-project elements (see <see cref="MagicDrawImporter.UseLibraries"/>).</summary>
    public List<MagicDrawExternalType> ExternalTypes { get; } = [];

    /// <summary>Relationships to used-project elements that could not be imported without them.</summary>
    public List<MagicDrawExternalRelationship> ExternalRelationships { get; } = [];

    /// <summary>The Cameo id of the imported project itself (from its project metadata), when the file has one.</summary>
    public string? ProjectId { get; internal set; }

    internal void UsedProject(string name, string? id = null, string? referencedId = null)
    {
        if (!_used.TryGetValue(name, out var u)) _used[name] = u = (id, new HashSet<string>(StringComparer.Ordinal));
        else if (u.Id is null && id is not null) _used[name] = u = (id, u.Refs);
        if (referencedId is not null) u.Refs.Add(referencedId);
    }
}

public static partial class MagicDrawImporter
{
    /// <summary>Field on an imported model's root: the Cameo project id (matches it when another import uses it).</summary>
    public const string ProjectIdField = "md:projectId";

    // Modules every Cameo project uses (profiles, libraries shipped with the tool): never offered as used projects.
    private static readonly string[] StandardModules =
        ["SysML", "UML_Standard_Profile", "UML Standard Profile", "MD_customization", "MagicDraw", "StandardProfile", "PrimitiveTypes", "QUDV",
         "ISO80000", "SI_", "SI ", "SIDefinitions", "Matrix", "Validation", "Traceability", "Requirement_Customization", "MBSE", "Cameo", "UAF", "BPMN"];

    /// <summary>The used-project (module) name of an href such as "Standard%20Parts.mdzip#_123", or null for standard or web references.</summary>
    public static string? ModuleOf(string? href)
    {
        if (string.IsNullOrEmpty(href)) return null;
        var hash = href.IndexOf('#');
        var file = hash >= 0 ? href[..hash] : href;
        if (file.Length == 0 || file.Contains("://", StringComparison.Ordinal)) return null;
        file = WebUtility.UrlDecode(file.Replace('\\', '/'));
        file = file[(file.LastIndexOf('/') + 1)..];
        var name = Regex.Replace(file, @"\.(mdzip|mdxml|xml|xmi)$", "", RegexOptions.IgnoreCase);
        if (name.Length == 0 || StandardModules.Any(s => name.StartsWith(s, StringComparison.OrdinalIgnoreCase))) return null;
        return name;
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
                if (ModuleOf(href) is { } module) { _report.UsedProject(module, referencedId: id); return module; }
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

        /// <summary>Used projects listed in the project's metadata and its used-project proxies.</summary>
        private void ReadProjectMetadata()
        {
            foreach (var entry in _src.OtherEntries)
            {
                var name = entry[(entry.LastIndexOf('/') + 1)..];
                var proxy = Regex.Match(name, @"^proxy\.[^_]*__(.+?)(\.mdzip)?$", RegexOptions.IgnoreCase);
                if (proxy.Success && ModuleOf(proxy.Groups[1].Value + ".mdzip") is { } module) _report.UsedProject(module);
                if (!name.EndsWith("ci.metamodel.project", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var s = _src.OpenBinary(entry);
                    if (s is null) continue;
                    using var reader = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                    var doc = XDocument.Load(reader);
                    if (doc.Root is { } root && root.Attributes().FirstOrDefault(a => a.Name.LocalName is "projectID" or "projectId" or "ID") is { } pid)
                        _report.ProjectId = pid.Value;
                    foreach (var u in doc.Descendants().Where(e => e.Name.LocalName.Contains("ProjectUsage", StringComparison.OrdinalIgnoreCase)
                                                                 || e.Name.LocalName.Equals("usedProject", StringComparison.OrdinalIgnoreCase)))
                    {
                        var uri = u.Attributes().FirstOrDefault(a => a.Name.LocalName.Contains("URI", StringComparison.OrdinalIgnoreCase) || a.Name.LocalName == "href")?.Value;
                        var id = u.Attributes().FirstOrDefault(a => a.Name.LocalName is "usedProjectID" or "usedProjectId" or "projectID" or "projectId")?.Value;
                        if (ModuleOf(uri) is { } used) _report.UsedProject(used, id);
                    }
                }
                catch (XmlException) { }   // metadata is optional
            }
        }
    }

    /// <summary>
    /// Instead of copies of a used project's content, makes an imported model refer to an existing Meridian project that holds it
    /// (docs/LIBRARIES.md, "Cameo import"): copies of the used packages' elements are removed (references to them keep their ids
    /// and resolve to the library once the usage exists), features typed by used-project elements get their ids back, and
    /// relationships to them that could not be imported are created. Returns how many elements were removed, retyped and linked.
    /// </summary>
    public static (int Removed, int Retyped, int Linked) UseLibraries(ModelRepository imported, MagicDrawImportReport report, ModelRepository existing, IReadOnlyCollection<string> packageIds)
    {
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
    /// import refers to, or every top-level package when it refers to none.
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
        var packages = used.ReferencedIds.Select(existing.Find).OfType<Element>().Select(TopPackage).OfType<Element>().Distinct().ToList();
        if (packages.Count == 0) packages = existing.Root.OwnedElements.Where(e => e.Kind == ElementKind.Package).ToList();
        return packages.Select(p => p.Id).ToList();
    }
}
