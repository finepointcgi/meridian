using Meridian.Core.Model;

namespace Meridian.Core.Libraries;

/// <summary>What a usage of another project's package is pinned to.</summary>
public enum LibraryPinKind
{
    /// <summary>A committed version of the source project (its version number).</summary>
    Version,
    /// <summary>The current head of a branch of the source project ("main" for its main line).</summary>
    Branch,
    /// <summary>A baseline of the source project (its id).</summary>
    Baseline,
}

/// <summary>Encodings shared by the stores and the UI.</summary>
public static class LibraryPins
{
    public static string Code(LibraryPinKind k) => k switch
    {
        LibraryPinKind.Version => "version",
        LibraryPinKind.Branch => "branch",
        LibraryPinKind.Baseline => "baseline",
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    /// <summary>Unknown codes read as a pinned version (the most conservative pin).</summary>
    public static LibraryPinKind Kind(string? code) => code switch
    {
        "branch" => LibraryPinKind.Branch,
        "baseline" => LibraryPinKind.Baseline,
        _ => LibraryPinKind.Version,
    };

    /// <summary>A short label such as "v3", "main (head)" or "baseline PDR".</summary>
    public static string Label(LibraryPinKind kind, string value, string? name = null) => kind switch
    {
        LibraryPinKind.Version => $"v{value}",
        LibraryPinKind.Branch => $"{value} (head)",
        _ => $"baseline {name ?? value}",
    };

    /// <summary>
    /// The banner of a source project opened at a pin ("Open in source project"): "Viewing version 3 (pinned)", "Viewing baseline
    /// PDR — version 5 (pinned)" or "Viewing branch dev (pinned head)".
    /// </summary>
    public static string ViewLabel(LibraryPinKind kind, string label, int? version) => kind switch
    {
        LibraryPinKind.Version => $"Viewing version {version} (pinned)",
        LibraryPinKind.Baseline => $"Viewing {label} — version {version} (pinned)",
        _ => $"Viewing branch {label.Replace(" (head)", "", StringComparison.Ordinal)} (pinned head)",
    };
}

/// <summary>
/// One library as loaded into a using project: which project and package it comes from, what it is pinned to and what that
/// pin resolved to (<see cref="Resolved"/>: e.g. "v7" or "main@r42", used to detect updates), and where it is mounted.
/// Transitive libraries (used by a library) carry the usage that brought them in (<see cref="ViaUsageId"/>).
/// </summary>
public sealed record LibraryInfo(
    string UsageId,
    Guid SourceProjectId,
    string SourceProjectName,
    string RootPackageId,
    LibraryPinKind PinKind,
    string PinValue,
    string PinLabel,
    string MountId,
    string? ViaUsageId = null,
    string? Resolved = null)
{
    public bool IsTransitive => ViaUsageId is not null;

    /// <summary>"Standard Parts · v3" (the badge text in the tree).</summary>
    public string Badge => $"{SourceProjectName} · {PinLabel}";
}

/// <summary>A library's content for loading: its info and the JSON of its root package (the project-file element format).</summary>
public sealed record LibraryPayload(LibraryInfo Info, string RootJson);

/// <summary>A library mounted into a <see cref="ModelRepository"/>: read-only elements shown below <see cref="MountPoint"/>.</summary>
public sealed class LibraryMount(LibraryInfo info, Element root, Element mountPoint)
{
    public LibraryInfo Info { get; } = info;
    public Element Root { get; } = root;
    public Element MountPoint { get; } = mountPoint;
}

/// <summary>An edit of an element that belongs to a used project (library). The message says where to change it instead.</summary>
public sealed class ReadOnlyElementException(string message) : InvalidOperationException(message)
{
    public static string MessageFor(Element e, LibraryInfo info) =>
        $"“{e.DisplayName}” comes from the used project “{info.SourceProjectName}” ({info.PinLabel}) and is read-only here. Open it in the source project to change it.";
}

/// <summary>
/// Stand-ins for referenced elements that are not in the model: a library element removed by a library update, or one of a
/// library the user can't read. They keep the referenced id, so a project that is saved and loaded again still refers to it
/// (nothing is silently dropped), and validation reports them (LIB-001). Like redaction placeholders they have no owner and
/// are not indexed.
/// </summary>
public static class MissingReference
{
    /// <summary>Field set on placeholders (value "true").</summary>
    public const string Field = "missingRef";

    public static Element Element(string id)
    {
        var e = new Element(ElementKind.Shape, id);
        e.SetRaw(FieldKeys.Name, "missing reference");
        e.SetRaw(FieldKeys.Subtype, "rectangle");
        e.SetRaw(Field, "true");
        return e;
    }

    /// <summary>A stand-in for a relationship shown on a diagram that no longer exists (it keeps the path and its id).</summary>
    public static Relationship Relationship(string id, Element source, Element target)
    {
        var r = new Relationship(ElementKind.Dependency, source, target, id);
        r.SetRaw(Field, "true");
        return r;
    }

    public static bool Is(Element e) => e.Owner == null && e.Kind != ElementKind.Model && e.Get(Field) == "true";
}
