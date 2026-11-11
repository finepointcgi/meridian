using System.IO.Compression;
using System.Xml.Linq;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization;

public sealed record FmiVariable(
    string Name, string ValueReference, string Causality, string Variability, string Type, string? Unit, string? Start, string? Description);

/// <param name="DefaultStepSize">DefaultExperiment stepSize, as written.</param>
/// <param name="DefaultStopTime">DefaultExperiment stopTime, as written.</param>
/// <param name="VariableStepSize">CoSimulation canHandleVariableCommunicationStepSize.</param>
/// <param name="FixedInternalStepSize">FMI 3.0 CoSimulation fixedInternalStepSize, as written.</param>
public sealed record FmiModelDescription(
    string FmiVersion, string ModelName, string? Guid, string? Description, string? ModelIdentifier,
    bool CoSimulation, bool ModelExchange, IReadOnlyList<FmiVariable> Variables, IReadOnlyList<string> Units,
    IReadOnlyList<string> Binaries, string? DefaultStepSize = null, string? DefaultStopTime = null, bool VariableStepSize = true,
    string? FixedInternalStepSize = null);

/// <summary>
/// FMI 2.0 / 3.0 interface import: reads an FMU's modelDescription.xml (variables, causality, units) and creates a block
/// with value properties (parameters, locals) and in / out ports (inputs, outputs), so the FMU can be typed as a part and
/// connected in internal block diagrams.
/// <para>
/// Importing never runs the FMU's native code. Co-simulation (<see cref="Meridian.Core.Analysis.CoSimulation"/> with
/// <see cref="Fmi.FmuUnit"/>) runs it out of process; the block's fmi.sha256 tagged value names the stored .fmu
/// (<see cref="Fmi.FmuStore"/>) and the other fmi.* tagged values record where the FMU came from.
/// </para>
/// </summary>
public static class FmiImport
{
    /// <summary>Reads modelDescription.xml from an .fmu archive (a zip) or from the XML itself.</summary>
    public static FmiModelDescription Read(Stream fmuOrXml)
    {
        using var ms = new MemoryStream();
        fmuOrXml.CopyTo(ms);
        ms.Position = 0;
        var isZip = ms.Length > 4 && ms.GetBuffer()[0] == 'P' && ms.GetBuffer()[1] == 'K';
        if (!isZip) return Parse(XDocument.Load(ms));
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("modelDescription.xml", StringComparison.OrdinalIgnoreCase))
                    ?? throw new FormatException("The FMU has no modelDescription.xml at its root");
        using var s = SafeZip.Open(entry);
        var md = Parse(XDocument.Load(s));
        var bins = zip.Entries.Where(e => e.FullName.StartsWith("binaries/", StringComparison.OrdinalIgnoreCase) && e.Length > 0).Select(e => e.FullName).ToList();
        return md with { Binaries = bins };
    }

    public static FmiModelDescription Parse(XDocument doc)
    {
        var root = doc.Root ?? throw new FormatException("Empty modelDescription.xml");
        if (root.Name.LocalName != "fmiModelDescription") throw new FormatException("Not an FMI modelDescription.xml (root must be fmiModelDescription)");
        string? A(XElement e, string n) => e.Attribute(n)?.Value;
        var version = A(root, "fmiVersion") ?? "2.0";
        var cs = root.Elements().FirstOrDefault(e => e.Name.LocalName == "CoSimulation");
        var me = root.Elements().FirstOrDefault(e => e.Name.LocalName == "ModelExchange");
        var units = root.Elements().FirstOrDefault(e => e.Name.LocalName == "UnitDefinitions")?.Elements().Select(u => A(u, "name")).OfType<string>().ToList() ?? [];
        // Declared types may carry the unit (FMI 2.0 SimpleType/Real@unit, FMI 3.0 Float64Type@unit).
        var typeUnits = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in root.Elements().FirstOrDefault(e => e.Name.LocalName == "TypeDefinitions")?.Elements() ?? [])
        {
            var unit = A(t, "unit") ?? t.Elements().Select(x => A(x, "unit")).FirstOrDefault(x => x != null);
            if (A(t, "name") is { } tn && unit != null) typeUnits[tn] = unit;
        }

        var vars = new List<FmiVariable>();
        var mv = root.Elements().FirstOrDefault(e => e.Name.LocalName == "ModelVariables");
        foreach (var v in mv?.Elements() ?? [])
        {
            XElement typed;
            string type;
            if (v.Name.LocalName == "ScalarVariable")
            {
                // FMI 2.0: <ScalarVariable name causality><Real unit start/></ScalarVariable>
                typed = v.Elements().FirstOrDefault() ?? v;
                type = typed.Name.LocalName;
            }
            else
            {
                // FMI 3.0: <Float64 name causality unit start/>
                typed = v;
                type = v.Name.LocalName;
            }
            var unit = A(typed, "unit") ?? (A(typed, "declaredType") is { } dt && typeUnits.TryGetValue(dt, out var tu) ? tu : null);
            vars.Add(new FmiVariable(
                A(v, "name") ?? "", A(v, "valueReference") ?? "", A(v, "causality") ?? "local", A(v, "variability") ?? "continuous",
                type, unit, A(typed, "start"), A(v, "description")));
        }
        var experiment = root.Elements().FirstOrDefault(e => e.Name.LocalName == "DefaultExperiment");
        return new FmiModelDescription(version, A(root, "modelName") ?? "FMU", A(root, "guid") ?? A(root, "instantiationToken"),
            A(root, "description"), A(cs ?? me ?? root, "modelIdentifier"), cs != null, me != null, vars, units, [],
            experiment != null ? A(experiment, "stepSize") : null, experiment != null ? A(experiment, "stopTime") : null,
            cs == null || A(cs, "canHandleVariableCommunicationStepSize") is not ("false" or "0"), cs != null ? A(cs, "fixedInternalStepSize") : null);
    }

    /// <summary>Maps an FMI variable type to a primitive type name.</summary>
    public static string PrimitiveType(string fmiType) => fmiType switch
    {
        "Real" or "Float64" or "Float32" => "Real",
        "Integer" or "Int8" or "Int16" or "Int32" or "Int64" or "UInt8" or "UInt16" or "UInt32" or "UInt64" or "Enumeration" => "Integer",
        "Boolean" or "Clock" => "Boolean",
        "String" or "Binary" => "String",
        _ => "Real",
    };

    /// <summary>FMI types whose values a co-simulation passes between parts (strings, binaries and clocks are not).</summary>
    public static bool IsExchangeable(string fmiType) => fmiType is not ("String" or "Binary" or "Clock");

    /// <summary>The element name an FMI variable gets on import (array brackets and spaces replaced).</summary>
    public static string SimpleName(string fmiName) => fmiName.Replace("[", "_").Replace("]", "").Replace(" ", "_");

    /// <summary>Creates a block for the FMU in <paramref name="owner"/>. One undo step.</summary>
    /// <param name="fmuHash">SHA-256 of the .fmu in the <see cref="Fmi.FmuStore"/>, recorded so co-simulation can find it.</param>
    public static Element CreateBlock(ModelRepository m, Element owner, FmiModelDescription md, string? fileName = null, string? fmuHash = null)
    {
        using (m.History.Transaction($"Import FMU {md.ModelName}"))
        {
            var target = Metamodel.CanOwn(owner.Kind, ElementKind.Block) ? owner : m.DefaultOwnerFor(owner, ElementKind.Block);
            var name = string.Join("_", md.ModelName.Split(['.', ' '], StringSplitOptions.RemoveEmptyEntries).TakeLast(1));
            var block = m.Create(target, ElementKind.Block, name.Length > 0 ? name : "FMU");
            m.Set(block, FieldKeys.Stereotypes, "FMU");
            m.Set(block, FieldKeys.Documentation, md.Description);
            m.Set(block, FieldKeys.TagPrefix + "fmi.version", md.FmiVersion);
            m.Set(block, FieldKeys.TagPrefix + "fmi.modelName", md.ModelName);
            m.Set(block, FieldKeys.TagPrefix + "fmi.guid", md.Guid);
            m.Set(block, FieldKeys.TagPrefix + "fmi.modelIdentifier", md.ModelIdentifier);
            m.Set(block, FieldKeys.TagPrefix + "fmi.interface", md.CoSimulation && md.ModelExchange ? "CoSimulation, ModelExchange" : md.CoSimulation ? "CoSimulation" : md.ModelExchange ? "ModelExchange" : null);
            m.Set(block, FieldKeys.TagPrefix + "fmi.file", fileName);
            m.Set(block, FieldKeys.TagPrefix + "fmi.cosimulation", md.CoSimulation ? "supported" : "not supported by this FMU (Model Exchange only)");
            m.Set(block, FieldKeys.TagPrefix + "fmi.sha256", fmuHash);
            m.Set(block, FieldKeys.TagPrefix + "fmi.defaultStepSize", md.DefaultStepSize);
            m.Set(block, FieldKeys.TagPrefix + "fmi.defaultStopTime", md.DefaultStopTime);
            m.Set(block, FieldKeys.TagPrefix + "fmi.fixedInternalStepSize", md.FixedInternalStepSize);
            if (md.CoSimulation && !md.VariableStepSize) m.Set(block, FieldKeys.TagPrefix + "fmi.variableStepSize", "false");

            foreach (var v in md.Variables)
            {
                var isPort = v.Causality is "input" or "output";
                if (!isPort && v.Causality is not ("parameter" or "calculatedParameter" or "local" or "structuralParameter")) continue;   // skip independent (time)
                var simple = SimpleName(v.Name);
                if (simple.StartsWith("der(", StringComparison.Ordinal)) continue;   // derivatives are internal
                var e = m.Create(block, isPort ? ElementKind.Port : ElementKind.Property, simple);
                m.Set(e, FieldKeys.Type, PrimitiveType(v.Type));
                if (isPort) { m.Set(e, FieldKeys.Direction, v.Causality == "input" ? "in" : "out"); m.Set(e, FieldKeys.PortKind, "proxy"); }
                if (!isPort && v.Start != null) m.Set(e, FieldKeys.DefaultValue, v.Start);
                if (isPort && v.Start != null) m.Set(e, FieldKeys.TagPrefix + "fmi.start", v.Start);
                m.Set(e, ExtFieldKeys.Unit, v.Unit);
                m.Set(e, FieldKeys.Documentation, v.Description);
                m.Set(e, FieldKeys.TagPrefix + "fmi.valueReference", v.ValueReference);
                m.Set(e, FieldKeys.TagPrefix + "fmi.causality", v.Causality);
                m.Set(e, FieldKeys.TagPrefix + "fmi.type", v.Type);
                if (v.Variability != "continuous") m.Set(e, FieldKeys.TagPrefix + "fmi.variability", v.Variability);
            }
            return block;
        }
    }
}
