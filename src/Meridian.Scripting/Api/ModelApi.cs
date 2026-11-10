using System.Text;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Scripting;

/// <summary>
/// The implementation of <see cref="IModelApi"/> over a <see cref="ModelRepository"/>. Every call is marshalled through
/// <see cref="IWorkbenchBridge.Invoke{T}"/> so scripts running on a worker thread never touch the model concurrently with the UI.
/// </summary>
public sealed class ModelApi : IModelApi
{
    public const string Version = "1.0";

    private readonly IWorkbenchBridge _bridge;
    private readonly bool _readOnly;

    public ModelApi(ModelRepository repository, IWorkbenchBridge? bridge = null, bool readOnly = false)
    {
        Repository = repository;
        _bridge = bridge ?? new HeadlessBridge();
        _readOnly = readOnly;
    }

    /// <summary>The wrapped repository (host code only; not part of <see cref="IModelApi"/>).</summary>
    public ModelRepository Repository { get; }

    /// <summary>Cancels the current run: every API call made after cancellation throws <see cref="OperationCanceledException"/>.</summary>
    public CancellationToken CancellationToken { get; set; }

    /// <summary>Number of edits made through this instance.</summary>
    public int EditCount { get; private set; }

    /// <summary>Optional extra sink for <see cref="Log"/> (the script console's own output pane).</summary>
    public Action<string>? LogSink { get; set; }

    public string ApiVersion => Version;

    // ------------------------------------------------------------------ plumbing

    internal T Run<T>(Func<T> work)
    {
        CancellationToken.ThrowIfCancellationRequested();
        return _bridge.Invoke(work);
    }

    internal void Run(Action work) => Run(() => { work(); return true; });

    internal void Edit(Action work)
    {
        if (IsReadOnly) throw new InvalidOperationException("The model is read-only here.");
        Run(() => { work(); EditCount++; return true; });
    }

    internal static Element Unwrap(IElement e) =>
        e is ElementWrapper w ? w.Element : throw new ArgumentException("Not an element of this model API", nameof(e));

    internal IElement Wrap(Element e) => e switch
    {
        Diagram d => new DiagramWrapper(this, d),
        Relationship r => new RelationshipWrapper(this, r),
        _ => new ElementWrapper(this, e),
    };

    internal IElement? WrapOrNull(Element? e) => e is null ? null : Wrap(e);

    internal static ElementKind ParseKind(string kind) =>
        Enum.TryParse<ElementKind>(kind?.Replace(" ", ""), true, out var k)
            ? k
            : throw new ArgumentException($"Unknown kind '{kind}'. Valid kinds: {string.Join(", ", Enum.GetNames<ElementKind>())}");

    private static DiagramType ParseDiagramType(string type) =>
        Enum.TryParse<DiagramType>(type?.Replace(" ", ""), true, out var t)
            ? t
            : throw new ArgumentException($"Unknown diagram type '{type}'. Valid types: {string.Join(", ", Enum.GetNames<DiagramType>())}");

    // ------------------------------------------------------------------ model

    public string ModelName => Run(() => Repository.Root.Name);
    public IElement Root => Wrap(Repository.Root);
    public bool IsReadOnly => _readOnly || Repository.History.ReadOnly;
    public IReadOnlyList<string> Kinds { get; } = Metamodel.ElementKinds.Select(k => k.ToString()).ToList();
    public IReadOnlyList<string> RelationshipKinds { get; } = Metamodel.RelationshipKinds.Select(k => k.ToString()).ToList();
    public IReadOnlyList<string> DiagramTypes { get; } = Enum.GetNames<DiagramType>();

    public IReadOnlyList<IElement> All =>
        Run(() => Repository.AllElements.Where(e => e.Owner != null).Select(Wrap).ToList());

    public IReadOnlyList<IElement> OfKind(string kind)
    {
        var k = ParseKind(kind);
        return Run(() => Repository.OfKind(k).Select(Wrap).ToList());
    }

    public IReadOnlyList<IElement> Query(string? kind = null, string? name = null, string? stereotype = null, IElement? owner = null, bool recursive = true)
    {
        ElementKind? k = kind is null ? null : ParseKind(kind);
        var o = owner is null ? null : Unwrap(owner);
        return Run(() => Repository.AllElements
            .Where(e => e.Owner != null)
            .Where(e => k is null || e.Kind == k)
            .Where(e => name is null || string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
            .Where(e => stereotype is null || e.Stereotypes.Contains(stereotype, StringComparer.OrdinalIgnoreCase))
            .Where(e => o is null || (recursive ? o.IsAncestorOf(e) : ReferenceEquals(e.Owner, o)))
            .Select(Wrap).ToList());
    }

    public IReadOnlyList<IElement> Search(string text) => Run(() => Repository.Search(text).Select(Wrap).ToList());

    public IElement? Get(string id) => Run(() => WrapOrNull(Repository.Find(id)));

    public IElement? FindByName(string name, string? kind = null)
    {
        ElementKind? k = kind is null ? null : ParseKind(kind);
        return Run(() => WrapOrNull(Repository.AllElements.FirstOrDefault(e => e.Name == name && (k is null || e.Kind == k))));
    }

    public IReadOnlyList<IRelationship> Relationships(string? kind = null)
    {
        ElementKind? k = kind is null ? null : ParseKind(kind);
        return Run(() => Repository.Relationships.Where(r => k is null || r.Kind == k).Select(r => (IRelationship)Wrap(r)).ToList());
    }

    public IReadOnlyList<IDiagram> Diagrams => Run(() => Repository.Diagrams.Select(d => (IDiagram)Wrap(d)).ToList());

    // ------------------------------------------------------------------ edits

    public IElement Create(IElement owner, string kind, string? name = null)
    {
        var k = ParseKind(kind);
        var o = Unwrap(owner);
        Element? created = null;
        Edit(() => created = Repository.Create(o, k, name));
        return Wrap(created!);
    }

    public IRelationship Connect(string kind, IElement source, IElement target)
    {
        var k = ParseKind(kind);
        if (!Metamodel.IsRelationship(k)) throw new ArgumentException($"'{kind}' is not a relationship kind");
        Relationship? r = null;
        Edit(() => r = Repository.Connect(k, Unwrap(source), Unwrap(target)));
        return (IRelationship)Wrap(r!);
    }

    public IDiagram CreateDiagram(IElement owner, string diagramType, string? name = null)
    {
        var t = ParseDiagramType(diagramType);
        Diagram? d = null;
        Edit(() => d = Repository.CreateDiagram(Unwrap(owner), t, name));
        return (IDiagram)Wrap(d!);
    }

    public void Set(IElement element, string field, string? value)
    {
        var e = Unwrap(element);
        Edit(() => Repository.Set(e, field, value));
    }

    public void Move(IElement element, IElement newOwner)
    {
        var e = Unwrap(element);
        var o = Unwrap(newOwner);
        Edit(() => Repository.Move(e, o));
    }

    public void Delete(IElement element)
    {
        var e = Unwrap(element);
        Edit(() => { if (Repository.Contains(e)) Repository.Delete(e); });
    }

    public IDisposable Batch(string description)
    {
        if (IsReadOnly) return new Scope(() => { });
        var scope = Run(() => Repository.History.Transaction(description));
        // Closing must succeed even after cancellation, or the undo history would stay locked in an open transaction.
        return new Scope(() => InvokeUnchecked(() => { scope.Dispose(); return true; }));
    }

    /// <summary>Runs on the model thread without the cancellation check (host clean-up only).</summary>
    internal T InvokeUnchecked<T>(Func<T> work) => _bridge.Invoke(work);

    private sealed class Scope(Action close) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) close(); }
    }

    // ------------------------------------------------------------------ workbench

    public IElement? Selection => Run(() => WrapOrNull(_bridge.Selected is { } s && Repository.Contains(s) ? s : null));
    public IDiagram? ActiveDiagram => Run(() => (IDiagram?)WrapOrNull(_bridge.ActiveDiagram));

    public void Select(IElement? element)
    {
        var e = element is null ? null : Unwrap(element);
        Run(() => _bridge.Select(e));
    }

    public void OpenDiagram(IDiagram diagram)
    {
        var d = (Diagram)Unwrap(diagram);
        Run(() => _bridge.Open(d));
    }

    public void Log(string message)
    {
        CancellationToken.ThrowIfCancellationRequested();
        LogSink?.Invoke(message);
        _bridge.Invoke(() => { _bridge.Log(message); return true; });
    }

    public void Status(string message) => Run(() => _bridge.Status(message));

    public void Download(string fileName, string content, string mimeType = "text/plain") =>
        DownloadBytes(fileName, Encoding.UTF8.GetBytes(content), mimeType);

    public void DownloadBytes(string fileName, byte[] content, string mimeType = "application/octet-stream") =>
        Run(() => _bridge.Download(fileName, mimeType, content));
}
