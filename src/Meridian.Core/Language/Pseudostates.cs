using Meridian.Core.Model;

namespace Meridian.Core.Language;

/// <summary>
/// State machine pseudostates (<see cref="ElementKind.Pseudostate"/>): the kind is the element's subtype. Notation follows UML 2.5
/// §14.2.4: choice ◇, junction ●, fork / join ▬, shallow history Ⓗ, deep history Ⓗ*, entry point ○ and exit point ⊗ (on the border of
/// a composite state), terminate ✕.
/// </summary>
public static class Pseudostates
{
    public const string Choice = "choice", Junction = "junction", Fork = "fork", Join = "join", ShallowHistory = "shallowHistory",
        DeepHistory = "deepHistory", EntryPoint = "entryPoint", ExitPoint = "exitPoint", Terminate = "terminate";

    /// <summary>The pseudostate kind of <paramref name="e"/>; a pseudostate without a subtype is a choice. Null for other elements.</summary>
    public static string? KindOf(Element e) => e.Kind != ElementKind.Pseudostate ? null
        : e.Get(FieldKeys.Subtype) is { Length: > 0 } s && Subtypes.PseudostateKinds.Contains(s) ? s : Choice;

    public static bool Is(Element e, string kind) => KindOf(e) == kind;

    public static NodeShape Shape(string? kind) => kind switch
    {
        Junction => NodeShape.FilledCircle,
        Fork or Join => NodeShape.Bar,
        ShallowHistory or DeepHistory or EntryPoint or ExitPoint or Terminate => NodeShape.Pseudostate,
        _ => NodeShape.Diamond,
    };

    public static (double W, double H) Size(string? kind) => kind switch
    {
        Junction => (16, 16),
        Fork or Join => (100, 8),
        EntryPoint or ExitPoint => (18, 18),
        ShallowHistory or DeepHistory => (28, 28),
        Terminate => (22, 22),
        _ => (30, 30),
    };

    /// <summary>Entry and exit points sit on the border of the composite state that owns them.</summary>
    public static bool IsConnectionPoint(Element e) => KindOf(e) is EntryPoint or ExitPoint;

    /// <summary>The «keyword» shown for a pseudostate in lists ("choice", "deep history").</summary>
    public static string Label(string? kind) => kind == null ? "" : Subtypes.Display(kind);
}
