namespace Meridian.Scripting;

/// <summary>
/// The stable scripting and plugin facade over a Meridian model. Scripts see it as <c>Model</c>; plugins receive it
/// when their commands, validation rules, exporters and importers run.
/// </summary>
/// <remarks>
/// <para>Kinds are passed as strings (<c>"Block"</c>, <c>"Requirement"</c>, <c>"Satisfy"</c>…; case-insensitive) so the API does not
/// change when the internal metamodel enum gains members. <see cref="Kinds"/> and <see cref="RelationshipKinds"/> list the valid names.</para>
/// <para>Every mutation goes through the undoable repository. A script run, a plugin command or an explicit
/// <see cref="Batch"/> is recorded as one undo step.</para>
/// <para>Version 1.0 of this API. Members are only ever added, never removed or changed.</para>
/// </remarks>
public interface IModelApi
{
    /// <summary>Version of the scripting/plugin API, e.g. "1.0".</summary>
    string ApiVersion { get; }

    /// <summary>The model's (root element's) name.</summary>
    string ModelName { get; }

    /// <summary>The model root.</summary>
    IElement Root { get; }

    /// <summary>True when the API refuses every edit (viewers, validation rules, exporters).</summary>
    bool IsReadOnly { get; }

    /// <summary>Element kind names usable with <see cref="Create"/> and <see cref="OfKind"/>.</summary>
    IReadOnlyList<string> Kinds { get; }

    /// <summary>Relationship kind names usable with <see cref="Connect"/>.</summary>
    IReadOnlyList<string> RelationshipKinds { get; }

    /// <summary>Diagram type names usable with <see cref="CreateDiagram"/>.</summary>
    IReadOnlyList<string> DiagramTypes { get; }

    // ------------------------------------------------------------------ queries

    /// <summary>Every element (including relationships and diagrams), except the root.</summary>
    IReadOnlyList<IElement> All { get; }

    /// <summary>Elements of one kind, e.g. <c>OfKind("Block")</c>.</summary>
    IReadOnlyList<IElement> OfKind(string kind);

    /// <summary>
    /// Elements matching every given criterion (null = any): kind, exact name (case-insensitive),
    /// applied stereotype name, and owner (direct owner, or any ancestor when <paramref name="recursive"/>).
    /// </summary>
    IReadOnlyList<IElement> Query(string? kind = null, string? name = null, string? stereotype = null, IElement? owner = null, bool recursive = true);

    /// <summary>Elements whose name, requirement id or requirement text contains <paramref name="text"/>.</summary>
    IReadOnlyList<IElement> Search(string text);

    /// <summary>The element with this id, or null.</summary>
    IElement? Get(string id);

    /// <summary>The first element with this exact name (and kind), or null.</summary>
    IElement? FindByName(string name, string? kind = null);

    /// <summary>Every relationship, optionally of one kind.</summary>
    IReadOnlyList<IRelationship> Relationships(string? kind = null);

    /// <summary>Every diagram.</summary>
    IReadOnlyList<IDiagram> Diagrams { get; }

    // ------------------------------------------------------------------ edits (undoable)

    /// <summary>Creates an element under <paramref name="owner"/>. Throws if the owner cannot own that kind.</summary>
    IElement Create(IElement owner, string kind, string? name = null);

    /// <summary>Creates a relationship, e.g. <c>Connect("Verify", testCase, requirement)</c>.</summary>
    IRelationship Connect(string kind, IElement source, IElement target);

    /// <summary>Creates a diagram owned by <paramref name="owner"/>.</summary>
    IDiagram CreateDiagram(IElement owner, string diagramType, string? name = null);

    /// <summary>Sets a raw field (see <see cref="Fields"/>); null or empty removes it.</summary>
    void Set(IElement element, string field, string? value);

    /// <summary>Moves an element to a new owner.</summary>
    void Move(IElement element, IElement newOwner);

    /// <summary>Deletes an element, its subtree, its relationships and its diagram views.</summary>
    void Delete(IElement element);

    /// <summary>Groups every edit until disposal into one named undo step.</summary>
    IDisposable Batch(string description);

    // ------------------------------------------------------------------ workbench

    /// <summary>The element selected in the workbench (null when headless or nothing is selected).</summary>
    IElement? Selection { get; }

    /// <summary>The diagram open in the active tab, or null.</summary>
    IDiagram? ActiveDiagram { get; }

    /// <summary>Selects an element in the containment tree and the active diagram.</summary>
    void Select(IElement? element);

    /// <summary>Opens a diagram in a tab and activates it.</summary>
    void OpenDiagram(IDiagram diagram);

    /// <summary>Writes a line to the script output and the Output dock.</summary>
    void Log(string message);

    /// <summary>Shows a message in the status bar.</summary>
    void Status(string message);

    /// <summary>Offers a text file to the user as a download.</summary>
    void Download(string fileName, string content, string mimeType = "text/plain");

    /// <summary>Offers a binary file to the user as a download.</summary>
    void DownloadBytes(string fileName, byte[] content, string mimeType = "application/octet-stream");
}

/// <summary>Well-known field names for <see cref="IElement.Get"/> / <see cref="IElement.Set"/>.</summary>
public static class Fields
{
    public const string Name = "name";
    public const string Documentation = "documentation";
    public const string Type = "type";
    public const string Multiplicity = "multiplicity";
    public const string RequirementId = "reqId";
    public const string Text = "text";
    public const string Body = "body";
    public const string Stereotypes = "stereotypes";
    public const string Subtype = "subtype";
    public const string Visibility = "visibility";
    public const string DefaultValue = "defaultValue";
}

/// <summary>A model element, as seen by scripts and plugins. Wrappers compare equal when they wrap the same element.</summary>
public interface IElement
{
    string Id { get; }

    /// <summary>Kind name, e.g. "Block", "Requirement", "Satisfy", "Diagram".</summary>
    string Kind { get; }

    /// <summary>Name; setting it renames the element (undoable).</summary>
    string Name { get; set; }

    /// <summary>Documentation; setting it is undoable.</summary>
    string Documentation { get; set; }

    /// <summary>"Vehicle::Powertrain::Engine".</summary>
    string QualifiedName { get; }

    /// <summary>Owning element (null for the root).</summary>
    IElement? Owner { get; }

    /// <summary>Directly owned elements.</summary>
    IReadOnlyList<IElement> Children { get; }

    /// <summary>All owned elements, transitively.</summary>
    IReadOnlyList<IElement> Descendants { get; }

    bool IsRelationship { get; }
    bool IsDiagram { get; }

    /// <summary>Every raw field (name, documentation, reqId, text, stereotypes, tag:… ).</summary>
    IReadOnlyDictionary<string, string> Fields { get; }

    /// <summary>A raw field value, "" when unset.</summary>
    string Get(string field);

    /// <summary>Sets a raw field (undoable); null or "" removes it.</summary>
    void Set(string field, string? value);

    /// <summary>Applied stereotype names.</summary>
    IReadOnlyList<string> Stereotypes { get; }
    bool HasStereotype(string name);
    void AddStereotype(string name);
    void RemoveStereotype(string name);

    /// <summary>Tagged values by tag name (without the "tag:" prefix).</summary>
    IReadOnlyDictionary<string, string> TaggedValues { get; }
    string GetTag(string tag);
    void SetTag(string tag, string? value);

    /// <summary>Every relationship with this element at either end.</summary>
    IReadOnlyList<IRelationship> Relationships { get; }

    /// <summary>Relationships with this element as the source, optionally of one kind.</summary>
    IReadOnlyList<IRelationship> Outgoing(string? kind = null);

    /// <summary>Relationships with this element as the target, optionally of one kind.</summary>
    IReadOnlyList<IRelationship> Incoming(string? kind = null);

    /// <summary>Deletes this element (undoable).</summary>
    void Delete();
}

/// <summary>A directed relationship (Satisfy, Verify, Generalization, Association…).</summary>
public interface IRelationship : IElement
{
    IElement Source { get; }
    IElement Target { get; }
}

/// <summary>A diagram.</summary>
public interface IDiagram : IElement
{
    /// <summary>Diagram type name, e.g. "BlockDefinition", "Requirement".</summary>
    string DiagramType { get; }

    /// <summary>Elements shown as shapes on this diagram.</summary>
    IReadOnlyList<IElement> Elements { get; }

    /// <summary>True when the element has a shape on this diagram.</summary>
    bool Shows(IElement element);

    /// <summary>Adds a shape for the element at (x, y) (undoable).</summary>
    void Show(IElement element, double x = 40, double y = 40);
}
