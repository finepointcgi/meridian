using Meridian.Core.Libraries;

namespace Meridian.Core.Model;

/// <summary>
/// The library overlay (docs/LIBRARIES.md): packages of other projects ("used projects") mounted into this model as read-only,
/// referenced elements.
/// <list type="bullet">
/// <item>A library's root package is attached below its mount point and indexed like any other element, with its original ids,
/// so <see cref="Find"/>, <see cref="OfKind"/>, relationship incidence, type resolution and diagram shapes work unchanged.</item>
/// <item>Every element of a library is registered in an overlay map (element → <see cref="LibraryMount"/>). The map only exists
/// once a library is mounted, so models without libraries pay nothing.</item>
/// <item>Library elements cannot be changed: every public mutator calls <see cref="EnsureEditable"/>, remote operations that
/// touch them are ignored (<see cref="LibraryGuard"/>, which the server's permission check also applies), and the project file
/// skips library roots when it writes the model.</item>
/// </list>
/// Libraries are mounted by loaders only (no undo, no events, no replication): changing a pin rebuilds the model.
/// </summary>
public sealed partial class ModelRepository
{
    private Dictionary<Element, LibraryMount>? _libraryOf;
    private readonly List<LibraryMount> _libraries = [];
    private Dictionary<string, Element>? _missing;

    /// <summary>The mounted libraries, in load order.</summary>
    public IReadOnlyList<LibraryMount> Libraries => _libraries;

    public bool HasLibraries => _libraries.Count > 0;

    /// <summary>The library <paramref name="e"/> belongs to, or null for the project's own elements.</summary>
    public LibraryMount? LibraryOf(Element? e) =>
        e != null && _libraryOf != null && _libraryOf.TryGetValue(e, out var m) ? m : null;

    /// <summary>True for elements of a used project (read-only here).</summary>
    public bool IsLibraryElement(Element e) => _libraryOf != null && _libraryOf.ContainsKey(e);

    /// <summary>True for the root package of a mounted library (the project file skips these).</summary>
    public bool IsLibraryRoot(Element e) => _libraryOf != null && _libraryOf.TryGetValue(e, out var m) && ReferenceEquals(m.Root, e);

    /// <summary>The project's own elements (everything but library content).</summary>
    public IEnumerable<Element> OwnElements => _libraryOf is null ? _index.Values : _index.Values.Where(e => !_libraryOf.ContainsKey(e));

    /// <summary>Why <paramref name="e"/> can't be edited here (it comes from a library), or null.</summary>
    public string? ReadOnlyReason(Element e) => LibraryOf(e) is { } m ? ReadOnlyElementException.MessageFor(e, m.Info) : null;

    /// <summary>Throws <see cref="ReadOnlyElementException"/> when <paramref name="e"/> belongs to a library.</summary>
    public void EnsureEditable(Element e)
    {
        if (LibraryOf(e) is { } m) throw new ReadOnlyElementException(ReadOnlyElementException.MessageFor(e, m.Info));
    }

    /// <summary>Throws when deleting or replacing <paramref name="e"/> would take a mounted library with it.</summary>
    private void EnsureNoMountInside(Element e)
    {
        if (_libraries.Count == 0) return;
        foreach (var m in _libraries)
            if (ReferenceEquals(m.MountPoint, e) || e.IsAncestorOf(m.MountPoint))
                throw new InvalidOperationException(
                    $"“{e.DisplayName}” shows the used project “{m.Info.SourceProjectName}”. Remove the usage or change its mount point first (Project ▸ Used projects…).");
    }

    /// <summary>
    /// Mounts a library's (detached, fully built) root package below <paramref name="mountPoint"/> while loading: no undo,
    /// events or replication. Ids already in the model are not mounted again (the caller skips duplicates).
    /// </summary>
    internal LibraryMount MountLibraryForLoad(LibraryInfo info, Element mountPoint, Element root)
    {
        if (!Contains(mountPoint)) mountPoint = Root;
        root.Owner = mountPoint;
        mountPoint.Children.Add(root);
        IndexRecursive(root);
        var mount = new LibraryMount(info, root, mountPoint);
        _libraryOf ??= new Dictionary<Element, LibraryMount>(ReferenceEqualityComparer.Instance);
        _libraryOf[root] = mount;
        foreach (var e in root.Descendants())
            _libraryOf.TryAdd(e, mount);   // a nested library mounted earlier keeps its own mount
        _libraries.Add(mount);
        return mount;
    }

    /// <summary>
    /// Records a placeholder for a reference that doesn't resolve (see <see cref="MissingReference"/>), so later remote
    /// operations naming the same id find it.
    /// </summary>
    internal void RegisterMissing(Element placeholder)
    {
        _missing ??= new Dictionary<string, Element>(StringComparer.Ordinal);
        _missing.TryAdd(placeholder.Id, placeholder);
    }

    /// <summary>Placeholders for references that didn't resolve when the model was loaded (removed or unavailable library elements).</summary>
    public IEnumerable<Element> MissingReferences => _missing?.Values ?? Enumerable.Empty<Element>();

    private Element? FindOrMissing(string? id) => Find(id) ?? (id != null && _missing != null && _missing.TryGetValue(id, out var m) ? m : null);

    /// <summary>Picks an owner for a new relationship that isn't inside a library (relationships to library elements belong to the project).</summary>
    private Element EditableOwnerFor(Element preferred, Element other, ElementKind kind)
    {
        if (!IsLibraryElement(preferred)) return preferred;
        if (!IsLibraryElement(other)) return DefaultOwnerFor(other.Owner ?? Root, kind);
        return Root;
    }
}
