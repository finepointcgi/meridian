using Meridian.Core.Model;

namespace Meridian.Core.Samples;

/// <summary>Ready-made profiles that can be added to any model.</summary>
public static class Profiles
{
    /// <summary>
    /// A practical subset of the Unified Architecture Framework (UAF 1.2): strategic, operational, resource and
    /// service stereotypes applicable to SysML blocks/activities/requirements, with layer colours and key tags.
    /// </summary>
    public static Element AddUaf(ModelRepository m, Element owner)
    {
        Element profile;
        using (m.History.Transaction("Add UAF profile"))
        {
            profile = m.Create(owner, ElementKind.Profile, UafProfileName);
            m.Set(profile, FieldKeys.Documentation, "Unified Architecture Framework (subset): apply these stereotypes to blocks, activities and requirements.");
            foreach (var def in UafStereotypes) AddStereotype(m, profile, def);
        }
        return profile;
    }

    public const string UafProfileName = "UAF";

    /// <summary>A stereotype definition: name, applicable metaclasses, header colour and tag definitions.</summary>
    public sealed record StereotypeDef(string Name, string Metaclass, string Color, params (string Tag, string Type, string? Values)[] Tags);

    /// <summary>The UAF stereotypes by domain (strategic, operational, services, personnel, resources, security, projects, standards, actual resources).</summary>
    public static readonly IReadOnlyList<StereotypeDef> UafStereotypes =
    [
        // Strategic
        new("Capability", "Block", "#f5deaa", ("maturity", "Enum", "planned, emerging, operational, retired"), ("priority", "Integer", null)),
        new("Enterprise Goal", "Requirement", "#ccccff", ("measure", "String", null)),
        new("Enterprise Phase", "Block", "#f5deaa", ("startDate", "String", null), ("endDate", "String", null)),
        // Operational
        new("Operational Performer", "Block, Actor", "#ffffb5", ("autonomy", "Enum", "manual, assisted, autonomous")),
        new("Operational Activity", "Activity, Action", "#ffffb5", ("frequency", "String", null)),
        new("Operational Exchange", "Connector, Dependency", "#ffffb5", ("classification", "Enum", "unclassified, restricted, secret")),
        // Services
        new("Service Specification", "Block, InterfaceBlock", "#b5ffff", ("serviceLevel", "String", null)),
        // Resources
        new("Resource Performer", "Block", "#c9e7b7", ("isCOTS", "Boolean", null)),
        new("System", "Block", "#c9e7b7", ("lifecycleCost", "Real", null)),
        new("Software", "Block, Component", "#c9e7b7", ("language", "String", null), ("version", "String", null)),
        new("Organization", "Block, Actor", "#ffe0e0", ("headcount", "Integer", null)),
        new("Function", "Activity, Action", "#c9e7b7", ("isAutomated", "Boolean", null)),
        // Security
        new("Security Control", "Requirement, Block", "#fbdede", ("controlFamily", "String", null)),
        // Added with the UAF viewpoint diagrams
        new("Enterprise Vision", "Requirement", "#ccccff", ("statement", "String", null)),
        new("Service Function", "Activity, Action", "#b5ffff"),
        new("Post", "Block", "#ffe0e0", ("grade", "String", null)),
        new("Responsibility", "Block", "#ffe0e0"),
        new("Competence", "Block", "#ffe0e0", ("level", "Enum", "basic, intermediate, expert")),
        new("Security Enclave", "Block", "#fbdede", ("classification", "Enum", "unclassified, restricted, secret")),
        new("Security Constraint", "Requirement", "#fbdede"),
        new("Project", "Block", "#e1d5e7", ("startDate", "String", null), ("endDate", "String", null)),
        new("Project Milestone", "Block", "#e1d5e7", ("date", "String", null)),
        new("Project Theme", "Block", "#e1d5e7"),
        new("Standard", "Block", "#dae8fc", ("version", "String", null), ("issuer", "String", null)),
        new("Protocol", "Block", "#dae8fc", ("layer", "String", null)),
        new("Actual Organization", "Block", "#f8cecc"),
        new("Actual Post", "Block", "#f8cecc"),
        new("Actual Person", "Block, Actor", "#f8cecc"),
        new("Actual Resource", "Block", "#f8cecc", ("serialNumber", "String", null)),
    ];

    private static Element AddStereotype(ModelRepository m, Element profile, StereotypeDef def)
    {
        var s = m.Create(profile, ElementKind.Stereotype, def.Name);
        m.Set(s, FieldKeys.Metaclass, def.Metaclass);
        m.Set(s, FieldKeys.Color, def.Color);
        foreach (var (tag, type, values) in def.Tags)
        {
            var t = m.Create(s, ElementKind.TagDefinition, tag);
            m.Set(t, FieldKeys.TagType, type == "String" ? null : type);
            m.Set(t, FieldKeys.TagValues, values);
        }
        return s;
    }

    /// <summary>
    /// Makes sure a UAF stereotype is defined in the model: adds the UAF profile at the root when there is none,
    /// or the missing stereotype to an existing (older, smaller) UAF profile. Unknown names are left alone.
    /// </summary>
    public static void EnsureUafStereotype(ModelRepository m, string name)
    {
        if (m.OfKind(ElementKind.Stereotype).Any(s => s.Name == name)) return;
        var profile = m.OfKind(ElementKind.Profile).FirstOrDefault(p => p.Name == UafProfileName);
        if (profile == null) { AddUaf(m, m.Root); return; }
        if (UafStereotypes.FirstOrDefault(d => d.Name == name) is { } def) AddStereotype(m, profile, def);
    }
}
