using Meridian.Core.CodeSync;
using Meridian.Core.CodeSync.Conformance;
using Meridian.Core.Model;
using Meridian.Serialization.CodeSync;

namespace Meridian.Tests;

/// <summary>Architecture conformance rules on hand-built models and code models (docs/ARCHITECTURE-CHECKS.md).</summary>
public class ArchitectureConformanceTests
{
    // ================================================================ helpers

    private static CodeType T(string symbol, CodeTypeKind kind = CodeTypeKind.Class, string[]? uses = null, string[]? implements = null,
                              string[]? methods = null, string[]? bases = null, (string Name, string Type)[]? members = null, int line = 3)
    {
        var dot = symbol.LastIndexOf('.');
        var name = symbol[(dot + 1)..];
        var ns = dot > 0 ? symbol[..dot] : "";
        var list = new List<CodeMember>();
        foreach (var (n, t) in members ?? []) list.Add(new CodeMember(n, CodeMemberKind.Property, t[(t.LastIndexOf('.') + 1)..], t, false, "public"));
        foreach (var m in methods ?? []) list.Add(new CodeMember(m, CodeMemberKind.Method, "void", null, false, "public"));
        return new CodeType(symbol, name, ns, kind, $"src/{ns.Replace('.', '/')}/{name}.cs", line, null,
            bases ?? [], implements ?? [], list, uses ?? []);
    }

    private static CodeModel Code(params CodeType[] types) => new("csharp", types, []);

    private static ArchitectureViolation Only(ConformanceResult r, ConformanceRule rule) => Assert.Single(r.Violations, v => v.Rule == rule);

    /// <summary>
    /// The thermal example: ThermalControl and PowerBus are connected in System's IBD only through ports typed by the
    /// interface block IPowerBus, which PowerBus realizes.
    /// </summary>
    private sealed class Thermal
    {
        public readonly ModelRepository M = new("Sat");
        public readonly Element Arch, IPowerBus, ThermalControl, PowerBus, System;

        public Thermal()
        {
            Arch = M.Create(M.Root, ElementKind.Package, "Architecture");
            IPowerBus = M.Create(Arch, ElementKind.InterfaceBlock, "IPowerBus");
            M.Create(IPowerBus, ElementKind.Operation, "Draw");
            ThermalControl = M.Create(Arch, ElementKind.Block, "ThermalControl");
            PowerBus = M.Create(Arch, ElementKind.Block, "PowerBus");
            var need = M.Create(ThermalControl, ElementKind.Port, "power");
            M.Set(need, FieldKeys.Type, IPowerBus.Id);
            var supply = M.Create(PowerBus, ElementKind.Port, "supply");
            M.Set(supply, FieldKeys.Type, IPowerBus.Id);
            M.Connect(ElementKind.Realization, PowerBus, IPowerBus);
            System = M.Create(Arch, ElementKind.Block, "Satellite");
            var thermal = M.Create(System, ElementKind.Property, "thermal");
            M.Set(thermal, FieldKeys.Type, ThermalControl.Id);
            var bus = M.Create(System, ElementKind.Property, "bus");
            M.Set(bus, FieldKeys.Type, PowerBus.Id);
            var c = M.Connect(ElementKind.Connector, need, supply);
            M.Set(c, FieldKeys.SourcePart, thermal.Id);
            M.Set(c, FieldKeys.TargetPart, bus.Id);
        }

        public static CodeModel DirectCall() => Code(
            T("Sat.Thermal.ThermalControl", uses: ["Sat.Power.PowerBus"], line: 12),
            T("Sat.Power.PowerBus", implements: ["Sat.Power.IPowerBus"], methods: ["Draw"]),
            T("Sat.Power.IPowerBus", CodeTypeKind.Interface, methods: ["Draw"]),
            T("Sat.Satellite", members: [("Thermal", "Sat.Thermal.ThermalControl"), ("Bus", "Sat.Power.PowerBus")]));

        public static CodeModel ThroughInterface() => Code(
            T("Sat.Thermal.ThermalControl", uses: ["Sat.Power.IPowerBus"], line: 12),
            T("Sat.Power.PowerBus", implements: ["Sat.Power.IPowerBus"], methods: ["Draw"]),
            T("Sat.Power.IPowerBus", CodeTypeKind.Interface, methods: ["Draw"]),
            T("Sat.Satellite", members: [("Thermal", "Sat.Thermal.ThermalControl"), ("Bus", "Sat.Power.PowerBus")]));
    }

    // ================================================================ (a) dependencies

    [Fact]
    public void Calling_the_implementation_directly_when_the_model_connects_through_an_interface_is_a_violation()
    {
        var t = new Thermal();
        var r = ArchitectureConformance.Check(t.M, Thermal.DirectCall());

        var v = Only(r, ConformanceRule.Dependency);
        Assert.Equal(ViolationSeverity.Error, v.Severity);
        Assert.Equal("src/Sat/Thermal/ThermalControl.cs:12", v.Location);
        Assert.Equal(("Sat.Thermal.ThermalControl", "Sat.Power.PowerBus"), (v.CodeSymbol, v.TargetSymbol));
        Assert.Equal((t.ThermalControl.Id, t.PowerBus.Id), (v.ElementId, v.TargetElementId));
        Assert.Contains("ThermalControl uses PowerBus", v.Message);
        Assert.StartsWith("Depend on IPowerBus instead", v.FixHint);
        Assert.Equal("dependency", v.RuleId);
        Assert.Equal(1, r.Errors);
        Assert.Equal("1 violation", r.Summary);
    }

    [Fact]
    public void Going_through_the_shared_interface_conforms()
    {
        var t = new Thermal();
        var r = ArchitectureConformance.Check(t.M, Thermal.ThroughInterface());
        Assert.Empty(r.Violations);
        Assert.Equal("No architecture violations", r.Summary);
        Assert.Equal((4, 4), (r.TypesChecked, r.TypesMapped));
    }

    [Fact]
    public void A_hand_drawn_dependency_permits_the_call_and_the_permitted_paths_are_configurable()
    {
        var t = new Thermal();
        var dep = t.M.Connect(ElementKind.Dependency, t.ThermalControl, t.PowerBus);
        Assert.Empty(ArchitectureConformance.Check(t.M, Thermal.DirectCall()).Violations);

        // Without "dependency" among the permitted paths it no longer counts.
        var options = new ConformanceOptions { Permitted = PermittedPaths.All & ~PermittedPaths.Dependency };
        Assert.Single(ArchitectureConformance.Check(t.M, Thermal.DirectCall(), options).Violations);

        // «use» is a separate path.
        t.M.Set(dep, FieldKeys.Stereotypes, "use");
        Assert.Empty(ArchitectureConformance.Check(t.M, Thermal.DirectCall(), options).Violations);
        Assert.Single(ArchitectureConformance.Check(t.M, Thermal.DirectCall(), options with { Permitted = options.Permitted & ~PermittedPaths.Usage }).Violations);

        // The model root's field sets the paths for the project.
        t.M.Set(t.M.Root, ConformanceOptions.PermittedPathsField, "association, connector, shared-interface");
        Assert.Equal(PermittedPaths.Association | PermittedPaths.Connector | PermittedPaths.SharedInterface, ConformanceOptions.FromModel(t.M).Permitted);
        Assert.Single(ArchitectureConformance.Check(t.M, Thermal.DirectCall()).Violations, v => v.Rule == ConformanceRule.Dependency && v.CodeSymbol == "Sat.Thermal.ThermalControl");
    }

    [Fact]
    public void A_direct_connector_or_association_permits_the_dependency_in_either_direction()
    {
        var m = new ModelRepository("M");
        var a = m.Create(m.Root, ElementKind.Block, "A");
        var b = m.Create(m.Root, ElementKind.Block, "B");
        m.Connect(ElementKind.Association, a, b);
        var code = Code(T("X.A", uses: ["X.B"]), T("X.B", uses: ["X.A"]));
        Assert.Empty(ArchitectureConformance.Check(m, code).Violations);

        var m2 = new ModelRepository("M");
        var ctx = m2.Create(m2.Root, ElementKind.Block, "Ctx");
        var a2 = m2.Create(m2.Root, ElementKind.Block, "A");
        var b2 = m2.Create(m2.Root, ElementKind.Block, "B");
        var pa = m2.Create(ctx, ElementKind.Property, "a");
        m2.Set(pa, FieldKeys.Type, a2.Id);
        var pb = m2.Create(ctx, ElementKind.Property, "b");
        m2.Set(pb, FieldKeys.Type, b2.Id);
        m2.Connect(ElementKind.Connector, pb, pa);
        Assert.Empty(ArchitectureConformance.Check(m2, code).Violations);
    }

    [Fact]
    public void Relationships_created_by_code_sync_do_not_count_as_architecture()
    {
        var t = new Thermal();
        var mirrored = t.M.Connect(ElementKind.Dependency, t.ThermalControl, t.PowerBus);
        t.M.Set(mirrored, CodeFieldKeys.Symbol, "dep:Sat.Thermal.ThermalControl->Sat.Power.PowerBus");
        Assert.Single(ArchitectureConformance.Check(t.M, Thermal.DirectCall()).Violations);
    }

    [Fact]
    public void Blocks_the_model_does_not_constrain_are_skipped_unless_strict()
    {
        var m = new ModelRepository("M");
        m.Create(m.Root, ElementKind.Block, "A");
        m.Create(m.Root, ElementKind.Block, "B");
        var code = Code(T("X.A", uses: ["X.B"]), T("X.B"));
        Assert.Empty(ArchitectureConformance.Check(m, code).Violations);

        var strict = ArchitectureConformance.Check(m, code, new ConformanceOptions { Strict = true });
        Assert.Equal("X.A", Only(strict, ConformanceRule.Dependency).CodeSymbol);
        Assert.StartsWith("Remove the dependency from the code", strict.Violations[0].FixHint);

        m.Set(m.Root, ConformanceOptions.StrictField, "true");
        Assert.Single(ArchitectureConformance.Check(m, code).Violations);
    }

    [Fact]
    public void Code_symbol_maps_first_then_hand_made_elements_by_name()
    {
        var t = new Thermal();
        // A synced copy of ThermalControl elsewhere (code.symbol), plus the hand-made architecture block found by name.
        var codePkg = t.M.Create(t.M.Root, ElementKind.Package, "Code");
        var synced = t.M.Create(codePkg, ElementKind.Block, "ThermalControl");
        t.M.Set(synced, CodeFieldKeys.Symbol, "Sat.Thermal.ThermalControl");
        var r = ArchitectureConformance.Check(t.M, Thermal.DirectCall());
        // The violation names the block the architecture constrains.
        Assert.Equal(t.ThermalControl.Id, Only(r, ConformanceRule.Dependency).ElementId);

        // Names match loosely ("Power Bus" ~ PowerBus).
        t.M.Rename(t.PowerBus, "Power Bus");
        Assert.Equal(t.PowerBus.Id, Only(ArchitectureConformance.Check(t.M, Thermal.DirectCall()), ConformanceRule.Dependency).TargetElementId);

        // Elements synced from another repository are not this code's.
        var other = new ConformanceOptions { RepoId = "repo-2" };
        t.M.Set(synced, CodeFieldKeys.Repo, "repo-1");
        Assert.Equal(t.ThermalControl.Id, Only(ArchitectureConformance.Check(t.M, Thermal.DirectCall(), other), ConformanceRule.Dependency).ElementId);
    }

    // ================================================================ (b) layering

    private static (ModelRepository M, Element Domain, Element Infra, Element Shared) Layers()
    {
        var m = new ModelRepository("M");
        var domain = m.Create(m.Root, ElementKind.Package, "Domain");
        var infra = m.Create(m.Root, ElementKind.Package, "Infrastructure");
        var shared = m.Create(m.Root, ElementKind.Package, "Shared");
        foreach (var p in new[] { domain, infra, shared }) m.Set(p, FieldKeys.Stereotypes, "layer");
        m.Create(domain, ElementKind.Block, "Order");
        m.Create(infra, ElementKind.Block, "OrderRepository");
        m.Create(shared, ElementKind.Block, "Money");
        return (m, domain, infra, shared);
    }

    private static CodeModel LayerCode() => Code(
        T("Shop.Order", uses: ["Shop.Data.OrderRepository", "Shop.Money"]),
        T("Shop.Data.OrderRepository", uses: ["Shop.Order"]),
        T("Shop.Money"));

    [Fact]
    public void Layers_with_an_allowed_tag_reject_other_layers()
    {
        var (m, domain, infra, _) = Layers();
        Assert.Empty(ArchitectureConformance.Check(m, LayerCode()).Violations);   // undeclared layers are unrestricted

        m.Set(domain, FieldKeys.TagPrefix + ArchitectureConformance.AllowedTag, "Shared");
        var v = Only(ArchitectureConformance.Check(m, LayerCode()), ConformanceRule.Layering);
        Assert.Equal(("Shop.Order", "Shop.Data.OrderRepository"), (v.CodeSymbol, v.TargetSymbol));
        Assert.Contains("Domain may not depend on Infrastructure", v.Message);
        Assert.Contains("«allowed»", v.FixHint);

        m.Set(domain, FieldKeys.TagPrefix + ArchitectureConformance.AllowedTag, "none");
        Assert.Equal(2, ArchitectureConformance.Check(m, LayerCode()).Violations.Count(x => x.Rule == ConformanceRule.Layering));

        m.Set(domain, FieldKeys.TagPrefix + ArchitectureConformance.AllowedTag, "*");
        Assert.Empty(ArchitectureConformance.Check(m, LayerCode()).Violations);
        _ = infra;
    }

    [Fact]
    public void Layers_declared_with_allowed_dependencies_and_namespace_tags()
    {
        var (m, domain, infra, shared) = Layers();
        var allowed = m.Connect(ElementKind.Dependency, domain, shared);
        m.Set(allowed, FieldKeys.Stereotypes, "allowed");
        Assert.Equal("Shop.Data.OrderRepository", Only(ArchitectureConformance.Check(m, LayerCode()), ConformanceRule.Layering).TargetSymbol);

        // A layer can claim code namespaces without any block in it.
        var (m2, d2, i2, _) = Layers();
        foreach (var b in i2.OwnedElements.ToList()) m2.Delete(b);
        m2.Set(i2, FieldKeys.TagPrefix + ArchitectureConformance.NamespacesTag, "Shop.Data");
        m2.Set(d2, FieldKeys.TagPrefix + ArchitectureConformance.AllowedTag, "Shared");
        var v = Only(ArchitectureConformance.Check(m2, LayerCode(), new ConformanceOptions { DisabledRules = new HashSet<ConformanceRule> { ConformanceRule.MissingInModel } }), ConformanceRule.Layering);
        Assert.Equal(i2.Id, v.TargetElementId);   // the layer package stands in for the missing block
        _ = infra;
    }

    // ================================================================ (c) interface conformance

    [Fact]
    public void A_realizing_block_must_implement_the_interface_and_its_model_operations()
    {
        var t = new Thermal();
        t.M.Create(t.IPowerBus, ElementKind.Operation, "Reset");
        var code = Code(
            T("Sat.Thermal.ThermalControl", uses: ["Sat.Power.IPowerBus"]),
            T("Sat.Power.PowerBus", methods: ["Draw"]),   // forgot ": IPowerBus"
            T("Sat.Power.IPowerBus", CodeTypeKind.Interface, methods: ["Draw"]));
        var r = ArchitectureConformance.Check(t.M, code);
        var found = r.Violations.Where(v => v.Rule == ConformanceRule.InterfaceConformance).Select(v => v.Message).ToList();
        Assert.Equal(["PowerBus is missing operation Reset() of IPowerBus.", "PowerBus realizes IPowerBus in the model, but PowerBus does not implement IPowerBus."], found.Order(StringComparer.Ordinal));

        // Implementing the interface covers its code methods; the model's extra operation is still missing.
        var fixedCode = Code(code.Types[0], T("Sat.Power.PowerBus", implements: ["Sat.Power.IPowerBus"]), code.Types[2]);
        var v = Only(ArchitectureConformance.Check(t.M, fixedCode), ConformanceRule.InterfaceConformance);
        Assert.Contains("Reset()", v.Message);
        Assert.Equal(t.PowerBus.Id, v.ElementId);
        Assert.Equal(t.IPowerBus.Id, v.TargetElementId);

        // Through a base class that has the method.
        var viaBase = Code(code.Types[0], T("Sat.Power.PowerBus", implements: ["Sat.Power.IPowerBus"], bases: ["Sat.Power.BusBase"]), code.Types[2],
            T("Sat.Power.BusBase", methods: ["Reset"]));
        Assert.DoesNotContain(ArchitectureConformance.Check(t.M, viaBase).Violations, x => x.Rule == ConformanceRule.InterfaceConformance);
    }

    [Fact]
    public void A_provided_port_requires_the_interface_too()
    {
        var m = new ModelRepository("M");
        var i = m.Create(m.Root, ElementKind.InterfaceBlock, "ISensor");
        m.Create(i, ElementKind.Operation, "Read");
        var b = m.Create(m.Root, ElementKind.Block, "Thermometer");
        var p = m.Create(b, ElementKind.Port, "out");
        m.Set(p, FieldKeys.Type, i.Id);
        var code = Code(T("X.Thermometer", methods: ["Sample"]));
        Assert.Empty(ArchitectureConformance.Check(m, code).Violations);   // a plain port is a required interface, not a provided one
        m.Set(p, FieldKeys.Stereotypes, "provided");
        Assert.Equal("Thermometer is missing operation Read() of ISensor.", Only(ArchitectureConformance.Check(m, code), ConformanceRule.InterfaceConformance).Message);
    }

    // ================================================================ (d) missing

    [Fact]
    public void Missing_in_code_and_missing_in_model_are_warnings()
    {
        var m = new ModelRepository("M");
        var b = m.Create(m.Root, ElementKind.Block, "Heater");
        m.Set(b, FieldKeys.Stereotypes, "implemented");
        m.Create(m.Root, ElementKind.Block, "Radio");
        var code = Code(T("Sat.Radio"), T("Sat.Internal.Cache"));
        var r = ArchitectureConformance.Check(m, code);
        var missingCode = Only(r, ConformanceRule.MissingInCode);
        Assert.Equal((ViolationSeverity.Warning, b.Id, "", (string?)null), (missingCode.Severity, missingCode.ElementId, missingCode.Location, missingCode.File));
        var missingModel = Only(r, ConformanceRule.MissingInModel);
        Assert.Equal(("Sat.Internal.Cache", "src/Sat/Internal/Cache.cs:3", (string?)null), (missingModel.CodeSymbol, missingModel.Location, missingModel.ElementId));
        Assert.Equal((0, 2), (r.Errors, r.Warnings));
        Assert.Equal("2 warnings", r.Summary);

        // A partial push says nothing about missing code.
        Assert.DoesNotContain(ArchitectureConformance.Check(m, code, new ConformanceOptions { CodeIsComplete = false }).Violations, v => v.Rule == ConformanceRule.MissingInCode);

        // A block linked by code.symbol is found by symbol.
        m.Set(b, CodeFieldKeys.Symbol, "Sat.Power.HeaterImpl");
        Assert.Single(ArchitectureConformance.Check(m, code).Violations, v => v.Rule == ConformanceRule.MissingInCode);
        Assert.DoesNotContain(ArchitectureConformance.Check(m, Code(T("Sat.Radio"), T("Sat.Power.HeaterImpl"))).Violations, v => v.Rule == ConformanceRule.MissingInCode);

        // Rules can be switched off per project.
        m.Set(m.Root, ConformanceOptions.DisabledRulesField, "missing-in-model, missing-in-code");
        Assert.Empty(ArchitectureConformance.Check(m, code).Violations);
    }

    // ================================================================ suppression and determinism

    [Fact]
    public void An_allow_dependency_suppresses_the_violation()
    {
        var t = new Thermal();
        var allow = t.M.Connect(ElementKind.Dependency, t.ThermalControl, t.PowerBus);
        t.M.Set(allow, FieldKeys.Stereotypes, "allow");
        var r = ArchitectureConformance.Check(t.M, Thermal.DirectCall());
        Assert.Empty(r.Violations);
        Assert.Equal("Sat.Thermal.ThermalControl", Assert.Single(r.Suppressed).CodeSymbol);

        // Limited to other rules, it does not apply.
        t.M.Set(allow, FieldKeys.TagPrefix + ArchitectureConformance.RulesTag, "layering");
        Assert.Single(ArchitectureConformance.Check(t.M, Thermal.DirectCall()).Violations);

        // On a package it covers everything inside.
        t.M.Set(allow, FieldKeys.TagPrefix + ArchitectureConformance.RulesTag, null);
        t.M.Delete(allow);
        var pkgAllow = t.M.Connect(ElementKind.Dependency, t.Arch, t.Arch);
        t.M.Set(pkgAllow, FieldKeys.Stereotypes, "allow");
        Assert.Empty(ArchitectureConformance.Check(t.M, Thermal.DirectCall()).Violations);
    }

    [Fact]
    public void Project_suppressions_match_symbols_and_names_with_wildcards()
    {
        var t = new Thermal();
        Assert.Empty(ArchitectureConformance.Check(t.M, Thermal.DirectCall(), new ConformanceOptions { Suppressions = ["dependency: Sat.Thermal.* -> Sat.Power.PowerBus"] }).Violations);
        Assert.Empty(ArchitectureConformance.Check(t.M, Thermal.DirectCall(), new ConformanceOptions { Suppressions = ["*: ThermalControl"] }).Violations);
        Assert.Empty(ArchitectureConformance.Check(t.M, Thermal.DirectCall(), new ConformanceOptions { Suppressions = ["Architecture::ThermalControl -> *"] }).Violations);
        Assert.Single(ArchitectureConformance.Check(t.M, Thermal.DirectCall(), new ConformanceOptions { Suppressions = ["layering: Sat.Thermal.*"] }).Violations);
        Assert.Single(ArchitectureConformance.Check(t.M, Thermal.DirectCall(), new ConformanceOptions { Suppressions = ["dependency: Sat.Thermal.* -> Sat.Other"] }).Violations);

        t.M.Set(t.M.Root, ConformanceOptions.SuppressionsField, "# known debt\ndependency: Sat.Thermal.ThermalControl -> Sat.Power.*");
        var r = ArchitectureConformance.Check(t.M, Thermal.DirectCall());
        Assert.Empty(r.Violations);
        Assert.Single(r.Suppressed);
    }

    [Fact]
    public void The_result_is_deterministic()
    {
        var (m, domain, _, _) = Layers();
        m.Set(domain, FieldKeys.TagPrefix + ArchitectureConformance.AllowedTag, "none");
        var code = Code(LayerCode().Types.Concat([T("Shop.Z.Extra", uses: ["Shop.Order"]), T("Shop.A.Extra2")]).ToArray());
        var first = ArchitectureConformance.Check(m, code, new ConformanceOptions { Strict = true });
        var reversed = ArchitectureConformance.Check(m, Code(code.Types.Reverse().ToArray()), new ConformanceOptions { Strict = true });
        Assert.Equal(first.Violations, reversed.Violations);
        Assert.True(first.Violations.Count >= 4);
        Assert.Equal(first.Violations.OrderBy(v => v.Rule).ToList(), first.Violations);
    }

    [Fact]
    public void Code_models_pack_into_compact_json_and_back()
    {
        var code = Thermal.DirectCall();
        var packed = CodeModelJson.Pack(code);
        var back = CodeModelJson.Unpack(packed)!;
        Assert.Equal(code.Types.Select(t => (t.Symbol, t.File, t.Line, string.Join(",", t.Dependencies), string.Join(",", t.Members.Select(m => m.Name)))),
                     back.Types.Select(t => (t.Symbol, t.File, t.Line, string.Join(",", t.Dependencies), string.Join(",", t.Members.Select(m => m.Name)))));
        Assert.DoesNotContain("\n", CodeModelJson.Serialize(code));
        Assert.Null(CodeModelJson.Unpack("not base64!"));
        Assert.Null(CodeModelJson.Unpack(null));
        var model = new Thermal().M;
        Assert.Equal(ArchitectureConformance.Check(model, code).Violations, ArchitectureConformance.Check(model, back).Violations);
    }

    [Fact]
    public void Real_csharp_code_read_by_the_reader_reports_the_line_of_the_type()
    {
        var files = new[]
        {
            new CodeFile("src/Power/PowerBus.cs", "namespace Sat.Power;\n\npublic interface IPowerBus { void Draw(); }\n\npublic class PowerBus : IPowerBus { public void Draw() {} }\n"),
            new CodeFile("src/Thermal/ThermalControl.cs", "using Sat.Power;\nnamespace Sat.Thermal;\n\n/// <summary>Heaters.</summary>\npublic class ThermalControl\n{\n    public void Tick() { new PowerBus().Draw(); }\n}\n"),
        };
        var code = new CSharpCodeReader().Read(files);
        var t = new Thermal();
        var v = Only(ArchitectureConformance.Check(t.M, code), ConformanceRule.Dependency);
        Assert.Equal("src/Thermal/ThermalControl.cs:5", v.Location);
        Assert.Contains("Depend on IPowerBus instead", v.FixHint);
    }
}
