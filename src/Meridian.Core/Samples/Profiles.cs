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
            profile = m.Create(owner, ElementKind.Profile, "UAF");
            m.Set(profile, FieldKeys.Documentation, "Unified Architecture Framework (subset): apply these stereotypes to blocks, activities and requirements.");
            void St(string name, string meta, string color, params (string Tag, string Type, string? Values)[] tags)
            {
                var s = m.Create(profile, ElementKind.Stereotype, name);
                m.Set(s, FieldKeys.Metaclass, meta);
                m.Set(s, FieldKeys.Color, color);
                foreach (var (tag, type, values) in tags)
                {
                    var t = m.Create(s, ElementKind.TagDefinition, tag);
                    m.Set(t, FieldKeys.TagType, type == "String" ? null : type);
                    m.Set(t, FieldKeys.TagValues, values);
                }
            }
            // Strategic
            St("Capability", "Block", "#f5deaa", ("maturity", "Enum", "planned, emerging, operational, retired"), ("priority", "Integer", null));
            St("Enterprise Goal", "Requirement", "#ccccff", ("measure", "String", null));
            St("Enterprise Phase", "Block", "#f5deaa", ("startDate", "String", null), ("endDate", "String", null));
            // Operational
            St("Operational Performer", "Block, Actor", "#ffffb5", ("autonomy", "Enum", "manual, assisted, autonomous"));
            St("Operational Activity", "Activity, Action", "#ffffb5", ("frequency", "String", null));
            St("Operational Exchange", "Connector, Dependency", "#ffffb5", ("classification", "Enum", "unclassified, restricted, secret"));
            // Services
            St("Service Specification", "Block, InterfaceBlock", "#b5ffff", ("serviceLevel", "String", null));
            // Resources
            St("Resource Performer", "Block", "#c9e7b7", ("isCOTS", "Boolean", null));
            St("System", "Block", "#c9e7b7", ("lifecycleCost", "Real", null));
            St("Software", "Block, Component", "#c9e7b7", ("language", "String", null), ("version", "String", null));
            St("Organization", "Block, Actor", "#ffe0e0", ("headcount", "Integer", null));
            St("Function", "Activity, Action", "#c9e7b7", ("isAutomated", "Boolean", null));
            // Security
            St("Security Control", "Requirement, Block", "#fbdede", ("controlFamily", "String", null));
        }
        return profile;
    }
}
