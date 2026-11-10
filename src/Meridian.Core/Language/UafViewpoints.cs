using Meridian.Core.Model;

namespace Meridian.Core.Language;

/// <summary>A UAF viewpoint: a diagram type whose palette is filtered to the UAF profile stereotypes of that domain.</summary>
/// <param name="Elements">Palette entries: element kind and the stereotype applied on creation.</param>
/// <param name="Links">Relationship kinds offered, optionally with a stereotype (e.g. «Operational Exchange»).</param>
public sealed record UafViewpoint(DiagramType Type, string Code, string Name, string Domain,
    IReadOnlyList<(ElementKind Kind, string Stereotype)> Elements, IReadOnlyList<(ElementKind Kind, string? Stereotype)> Links);

/// <summary>
/// The UAF (Unified Architecture Framework) domains as diagram presets over the UAF profile. They share the
/// block-definition notation; what differs is the palette, the default stereotypes and the starter template.
/// </summary>
public static class UafViewpoints
{
    public static readonly IReadOnlyList<UafViewpoint> All =
    [
        new(DiagramType.UafStrategic, "St", "UAF Strategic View", "Strategic",
            [(ElementKind.Block, "Capability"), (ElementKind.Requirement, "Enterprise Vision"), (ElementKind.Requirement, "Enterprise Goal"), (ElementKind.Block, "Enterprise Phase")],
            [(ElementKind.Composition, null), (ElementKind.Generalization, null), (ElementKind.Dependency, null), (ElementKind.Allocate, null), (ElementKind.Trace, null)]),
        new(DiagramType.UafOperational, "Op", "UAF Operational View", "Operational",
            [(ElementKind.Block, "Operational Performer"), (ElementKind.Actor, "Operational Performer"), (ElementKind.Activity, "Operational Activity")],
            [(ElementKind.Dependency, "Operational Exchange"), (ElementKind.Association, null), (ElementKind.Composition, null), (ElementKind.Allocate, null)]),
        new(DiagramType.UafServices, "Sv", "UAF Services View", "Services",
            [(ElementKind.InterfaceBlock, "Service Specification"), (ElementKind.Block, "Service Specification"), (ElementKind.Activity, "Service Function")],
            [(ElementKind.Composition, null), (ElementKind.Generalization, null), (ElementKind.Realization, null), (ElementKind.Dependency, null), (ElementKind.Allocate, null)]),
        new(DiagramType.UafPersonnel, "Pr", "UAF Personnel View", "Personnel",
            [(ElementKind.Block, "Organization"), (ElementKind.Block, "Post"), (ElementKind.Block, "Responsibility"), (ElementKind.Block, "Competence")],
            [(ElementKind.Composition, null), (ElementKind.Association, null), (ElementKind.Dependency, null), (ElementKind.Allocate, null)]),
        new(DiagramType.UafResources, "Rs", "UAF Resources View", "Resources",
            [(ElementKind.Block, "Resource Performer"), (ElementKind.Block, "System"), (ElementKind.Component, "Software"), (ElementKind.Activity, "Function")],
            [(ElementKind.Composition, null), (ElementKind.Association, null), (ElementKind.Generalization, null), (ElementKind.Dependency, null), (ElementKind.Allocate, null)]),
        new(DiagramType.UafSecurity, "Sc", "UAF Security View", "Security",
            [(ElementKind.Requirement, "Security Control"), (ElementKind.Block, "Security Enclave"), (ElementKind.Requirement, "Security Constraint")],
            [(ElementKind.Composition, null), (ElementKind.Satisfy, null), (ElementKind.Dependency, null), (ElementKind.Trace, null), (ElementKind.Allocate, null)]),
        new(DiagramType.UafProjects, "Pj", "UAF Projects View", "Projects",
            [(ElementKind.Block, "Project"), (ElementKind.Block, "Project Milestone"), (ElementKind.Block, "Project Theme")],
            [(ElementKind.Composition, null), (ElementKind.Dependency, null), (ElementKind.Allocate, null)]),
        new(DiagramType.UafStandards, "Sd", "UAF Standards View", "Standards",
            [(ElementKind.Block, "Standard"), (ElementKind.Block, "Protocol")],
            [(ElementKind.Composition, null), (ElementKind.Generalization, null), (ElementKind.Dependency, null), (ElementKind.Trace, null)]),
        new(DiagramType.UafActualResources, "Ar", "UAF Actual Resources View", "Actual Resources",
            [(ElementKind.Block, "Actual Organization"), (ElementKind.Block, "Actual Post"), (ElementKind.Block, "Actual Person"), (ElementKind.Block, "Actual Resource")],
            [(ElementKind.Composition, null), (ElementKind.Association, null), (ElementKind.Dependency, null), (ElementKind.Allocate, null)]),
    ];

    public static UafViewpoint? Of(DiagramType t) => All.FirstOrDefault(v => v.Type == t);

    public static bool IsUaf(DiagramType t) => Of(t) != null;

    public static IReadOnlyList<PaletteItem> PaletteItems(UafViewpoint v) =>
        v.Elements.Select(x => new PaletteItem(x.Kind, Label: x.Kind == ElementKind.Actor ? x.Stereotype + " (actor)" : x.Stereotype, Stereotype: x.Stereotype))
            .Append(new PaletteItem(ElementKind.Comment)).ToList();

    public static IReadOnlyList<PaletteItem> PaletteLinks(UafViewpoint v) =>
        v.Links.Select(x => new PaletteItem(x.Kind, Label: x.Stereotype, Stereotype: x.Stereotype)).ToList();
}
