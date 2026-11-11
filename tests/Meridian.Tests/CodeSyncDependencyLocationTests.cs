using Meridian.Core.CodeSync;
using Meridian.Core.CodeSync.Conformance;
using Meridian.Core.Model;
using Meridian.Serialization.CodeSync;

namespace Meridian.Tests;

/// <summary>
/// Where a dependency is used (<see cref="CodeType.DependencyLocations"/>): each reader records the file, line and kind of
/// every use of another scanned type, capped per target and deterministic; architecture violations list those lines.
/// </summary>
public class CodeSyncDependencyLocationTests
{
    private static CodeType Type(CodeModel m, string symbol) => Assert.Single(m.Types, t => t.Symbol == symbol);

    private static List<(int Line, string Kind)> Uses(CodeType t, string target) =>
        t.LocationsOf(target).Select(u => (u.Line, u.KindName)).ToList();

    private static List<CodeFile> Files(params (string Path, string Content)[] files) => files.Select(f => new CodeFile(f.Path, f.Content)).ToList();

    // ================================================================ C#

    private static readonly List<CodeFile> CSharpThermal = Files(
        ("src/Thermal/ThermalControl.cs", """
            namespace Sat.Thermal;
            using Sat.Power;

            public class ThermalControl : ControlBase
            {
                private PowerBus _bus;
                public PowerBus Bus => _bus;

                public ThermalControl(PowerBus bus) { _bus = bus; }

                public PowerBus Heat(double watts)
                {
                    var spare = new PowerBus();
                    PowerBus.Reset();
                    var level = PowerBus.Max;
                    return spare;
                }
            }
            """),
        ("src/Thermal/ControlBase.cs", """
            namespace Sat.Thermal;
            public abstract class ControlBase { }
            """),
        ("src/Power/PowerBus.cs", """
            namespace Sat.Power;
            public class PowerBus
            {
                public const int Max = 3;
                public static void Reset() { }
            }
            """));

    [Fact]
    public void The_CSharp_reader_records_each_line_that_uses_a_type_and_how()
    {
        var code = new CSharpCodeReader().Read(CSharpThermal);
        var t = Type(code, "Sat.Thermal.ThermalControl");

        Assert.Equal(
            [(6, "field"), (7, "field"), (9, "param"), (11, "return"), (13, "new"), (14, "call"), (15, "use")],
            Uses(t, "Sat.Power.PowerBus"));
        Assert.Equal([(4, "inherit")], Uses(t, "Sat.Thermal.ControlBase"));
        Assert.All(t.DependencyLocations, u => Assert.Equal("src/Thermal/ThermalControl.cs", u.File));
        Assert.Empty(t.OmittedDependencyLocations);
        // The type itself and types without uses record nothing.
        Assert.DoesNotContain(t.DependencyLocations, u => u.Symbol == t.Symbol);
        Assert.Empty(Type(code, "Sat.Power.PowerBus").DependencyLocations);
    }

    [Fact]
    public void Partial_classes_point_at_the_file_of_each_use()
    {
        var code = new CSharpCodeReader().Read(Files(
            ("a/Rover.cs", "namespace R; public partial class Rover { Wheel _w; }"),
            ("b/Rover.Drive.cs", "namespace R;\npublic partial class Rover\n{\n    void Go() { var w = new Wheel(); }\n}"),
            ("c/Wheel.cs", "namespace R; public class Wheel { }")));

        var uses = Type(code, "R.Rover").LocationsOf("R.Wheel").Select(u => u.Location).ToList();
        Assert.Equal(["a/Rover.cs:1", "b/Rover.Drive.cs:4"], uses);
    }

    // ================================================================ TypeScript, Java, Python

    [Fact]
    public void The_TypeScript_reader_records_imports_members_parameters_creations_and_calls()
    {
        var code = new TypeScriptCodeReader().Read(Files(
            ("src/thermal/thermal-control.ts", """
                import { PowerBus } from "../power/power-bus";
                import type { Base } from "./base";

                export class ThermalControl extends Base {
                  private bus: PowerBus;
                  constructor(other: PowerBus) {
                    super();
                    this.bus = new PowerBus();
                  }
                  heat(): PowerBus {
                    PowerBus.reset();
                    return this.bus;
                  }
                }
                """),
            ("src/thermal/base.ts", "export abstract class Base {}"),
            ("src/power/power-bus.ts", "export class PowerBus { static reset() {} }")));

        var t = Type(code, "src.thermal.ThermalControl");
        Assert.Equal([(1, "import"), (5, "field"), (6, "param"), (8, "new"), (10, "return"), (11, "call")], Uses(t, "src.power.PowerBus"));
        Assert.Equal([(2, "import"), (4, "inherit")], Uses(t, "src.thermal.Base"));
    }

    [Fact]
    public void The_Java_reader_records_imports_fields_parameters_creations_and_static_calls()
    {
        var code = new JavaCodeReader().Read(Files(
            ("src/main/java/sat/thermal/ThermalControl.java", """
                package sat.thermal;

                import sat.power.PowerBus;

                public class ThermalControl implements Controller {
                    private PowerBus bus;

                    public PowerBus heat(int watts, PowerBus spare) {
                        PowerBus local = new PowerBus();
                        PowerBus.reset();
                        return local;
                    }
                }
                """),
            ("src/main/java/sat/thermal/Controller.java", "package sat.thermal;\npublic interface Controller { }"),
            ("src/main/java/sat/power/PowerBus.java", "package sat.power;\npublic class PowerBus { public static void reset() { } }")));

        var t = Type(code, "sat.thermal.ThermalControl");
        // Line 8 returns and takes a PowerBus: one entry per line, the parameter wins.
        Assert.Equal([(3, "import"), (6, "field"), (8, "param"), (9, "new"), (10, "call")], Uses(t, "sat.power.PowerBus"));
        Assert.Equal([(5, "inherit")], Uses(t, "sat.thermal.Controller"));
    }

    [Fact]
    public void The_Python_reader_records_imports_annotations_and_class_calls_as_creations()
    {
        var code = new PythonCodeReader().Read(Files(
            ("lander/__init__.py", ""),
            ("lander/power.py", "class PowerBus:\n    @staticmethod\n    def reset():\n        pass\n"),
            ("lander/thermal.py", """
                from lander.power import PowerBus


                class ThermalControl:
                    bus: PowerBus

                    def heat(self, spare: "PowerBus") -> PowerBus:
                        local = PowerBus()
                        PowerBus.reset()
                        return local
                """)));

        var t = Type(code, "lander.thermal.ThermalControl");
        Assert.Equal([(1, "import"), (5, "field"), (7, "param"), (8, "new"), (9, "call")], Uses(t, "lander.power.PowerBus"));
    }

    // ================================================================ caps and determinism

    [Fact]
    public void Uses_are_capped_per_target_and_the_rest_counted()
    {
        var body = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"        var b{i} = new PowerBus();"));
        var code = new CSharpCodeReader { MaxDependencyUsesPerTarget = 4 }.Read(Files(
            ("src/A.cs", $"namespace Sat;\npublic class A\n{{\n    void M()\n    {{\n{body}\n    }}\n}}"),
            ("src/PowerBus.cs", "namespace Sat; public class PowerBus { }")));

        var a = Type(code, "Sat.A");
        Assert.Equal([6, 7, 8, 9], a.LocationsOf("Sat.PowerBus").Select(u => u.Line));
        Assert.Equal(26, a.OmittedDependencyLocations["Sat.PowerBus"]);
        Assert.Equal(30, a.LocationCountOf("Sat.PowerBus"));
        Assert.Equal(CodeDependencyUses.DefaultMaxPerTarget, new TypeScriptCodeReader().MaxDependencyUsesPerTarget);
    }

    [Fact]
    public void Locations_stay_bounded_for_heavily_used_types_in_large_code()
    {
        // 10,000 modules that each use a hub type on 40 lines and two neighbours: the output grows with the number of
        // dependencies, not with the number of uses.
        const int n = 10_000;
        var files = new List<CodeFile> { new("src/hub.ts", "export class Hub { static tick(): void {} }") };
        for (var i = 0; i < n; i++)
        {
            var calls = string.Join("\n", Enumerable.Range(0, 40).Select(_ => "    Hub.tick();"));
            files.Add(new CodeFile($"src/m{i}.ts",
                $"import {{ Hub }} from './hub';\nimport {{ M{(i + 1) % n} }} from './m{(i + 1) % n}';\nexport class M{i} {{\n  next: M{(i + 1) % n};\n  run(): void {{\n{calls}\n  }}\n}}"));
        }
        var code = new TypeScriptCodeReader().Read(files);

        Assert.Equal(n + 1, code.Types.Count);
        var m0 = Type(code, "src.M0");
        Assert.Equal(CodeDependencyUses.DefaultMaxPerTarget, m0.LocationsOf("src.Hub").Count());
        Assert.Equal(41, m0.LocationCountOf("src.Hub"));   // the import and 40 calls
        Assert.All(code.Types, t => Assert.All(t.DependencyLocations.GroupBy(u => u.Symbol), g => Assert.True(g.Count() <= CodeDependencyUses.DefaultMaxPerTarget)));
        Assert.True(code.Types.Sum(t => t.DependencyLocations.Count) <= n * 2 * CodeDependencyUses.DefaultMaxPerTarget);
    }

    [Fact]
    public void Cap_keeps_one_use_per_line_with_the_most_telling_kind_whatever_the_input_order()
    {
        CodeDependencyUse U(string s, int line, CodeDependencyKind k, string file = "a.ts") => new(s, file, line, k);
        var uses = new[]
        {
            U("B", 9, CodeDependencyKind.Use), U("A", 3, CodeDependencyKind.Use), U("A", 3, CodeDependencyKind.New),
            U("A", 1, CodeDependencyKind.Import), U("A", 2, CodeDependencyKind.Field, "0.ts"), U("A", 5, CodeDependencyKind.Call),
            U("", 4, CodeDependencyKind.Use), U("A", 0, CodeDependencyKind.Use),
        };
        var (kept, omitted) = CodeDependencyUses.Cap(uses, 3);
        var (again, _) = CodeDependencyUses.Cap(uses.Reverse(), 3);

        Assert.Equal(["A@0.ts:2 field", "A@a.ts:1 import", "A@a.ts:3 new", "B@a.ts:9 use"], kept.Select(u => $"{u.Symbol}@{u.Location} {u.KindName}"));
        Assert.Equal(kept, again);
        Assert.Equal(1, omitted["A"]);
        Assert.False(omitted.ContainsKey("B"));
    }

    [Fact]
    public void Reading_is_deterministic_and_locations_survive_packing()
    {
        var reader = new CSharpCodeReader();
        var first = reader.Read(CSharpThermal);
        var second = reader.Read(CSharpThermal.AsEnumerable().Reverse().ToList());
        Assert.Equal(CodeModelJson.Serialize(first), CodeModelJson.Serialize(second));

        var unpacked = CodeModelJson.Unpack(CodeModelJson.Pack(first))!;
        var t = Type(unpacked, "Sat.Thermal.ThermalControl");
        Assert.Equal(Uses(Type(first, t.Symbol), "Sat.Power.PowerBus"), Uses(t, "Sat.Power.PowerBus"));
        Assert.DoesNotContain("KindName", CodeModelJson.Serialize(first));

        // Code stored before locations existed reads back with none.
        var old = CodeModelJson.Deserialize("""{"Language":"csharp","Types":[{"Symbol":"A","Name":"A","Namespace":"","Kind":0,"File":"a.cs","Line":1,"BaseTypes":[],"Interfaces":[],"Members":[],"Dependencies":[]}],"Warnings":[]}""")!;
        Assert.Empty(old.Types[0].DependencyLocations);
        Assert.Empty(old.Types[0].OmittedDependencyLocations);
    }

    // ================================================================ conformance

    /// <summary>ThermalControl and PowerBus are connected only through ports typed by IPowerBus.</summary>
    private static ModelRepository ThermalModel()
    {
        var m = new ModelRepository("Sat");
        var arch = m.Create(m.Root, ElementKind.Package, "Architecture");
        var iface = m.Create(arch, ElementKind.InterfaceBlock, "IPowerBus");
        var thermal = m.Create(arch, ElementKind.Block, "ThermalControl");
        var bus = m.Create(arch, ElementKind.Block, "PowerBus");
        var need = m.Create(thermal, ElementKind.Port, "power");
        m.Set(need, FieldKeys.Type, iface.Id);
        var supply = m.Create(bus, ElementKind.Port, "supply");
        m.Set(supply, FieldKeys.Type, iface.Id);
        m.Connect(ElementKind.Realization, bus, iface);
        return m;
    }

    [Fact]
    public void A_violation_lists_the_lines_that_break_the_rule_up_to_the_limit_with_a_count_of_the_rest()
    {
        var code = new CSharpCodeReader().Read(CSharpThermal);
        var r = ArchitectureConformance.Check(ThermalModel(), code, new ConformanceOptions { MaxLocationsPerViolation = 3 });

        var v = Assert.Single(r.Violations, v => v.Rule == ConformanceRule.Dependency && v.TargetSymbol == "Sat.Power.PowerBus");
        Assert.Equal("src/Thermal/ThermalControl.cs:4", v.Location);   // the type's declaration
        Assert.Equal(["src/Thermal/ThermalControl.cs:6 field", "src/Thermal/ThermalControl.cs:7 field", "src/Thermal/ThermalControl.cs:9 param"],
            v.Locations.Select(u => $"{u.Location} {u.KindName}"));
        Assert.Equal(4, v.MoreLocations);
        Assert.Equal("src/Thermal/ThermalControl.cs:6", v.FirstLocation);

        var all = ArchitectureConformance.Check(ThermalModel(), code).Violations.Single(x => x.Rule == ConformanceRule.Dependency);
        Assert.Equal(5, all.Locations.Count);
        Assert.Equal(2, all.MoreLocations);
    }

    [Fact]
    public void Layering_violations_point_at_lines_and_other_rules_do_not()
    {
        var m = new ModelRepository("Sat");
        var domain = m.Create(m.Root, ElementKind.Package, "Domain");
        m.Set(domain, FieldKeys.Stereotypes, ArchitectureConformance.LayerStereotype);
        m.Set(domain, FieldKeys.TagPrefix + ArchitectureConformance.NamespacesTag, "Sat.Thermal");
        m.Set(domain, FieldKeys.TagPrefix + ArchitectureConformance.AllowedTag, "none");
        var infra = m.Create(m.Root, ElementKind.Package, "Infrastructure");
        m.Set(infra, FieldKeys.Stereotypes, ArchitectureConformance.LayerStereotype);
        m.Set(infra, FieldKeys.TagPrefix + ArchitectureConformance.NamespacesTag, "Sat.Power");

        var r = ArchitectureConformance.Check(m, new CSharpCodeReader().Read(CSharpThermal));
        var layering = Assert.Single(r.Violations, v => v.Rule == ConformanceRule.Layering);
        Assert.Equal(6, layering.Locations[0].Line);
        Assert.All(r.Violations.Where(v => v.Rule == ConformanceRule.MissingInModel), v => Assert.Empty(v.Locations));
        Assert.Equal("src/Power/PowerBus.cs:2", r.Violations.First(v => v.Rule == ConformanceRule.MissingInModel).FirstLocation);
    }

    [Fact]
    public void Code_without_recorded_uses_still_reports_at_the_type()
    {
        var code = new CodeModel("csharp",
        [
            new CodeType("Sat.Thermal.ThermalControl", "ThermalControl", "Sat.Thermal", CodeTypeKind.Class, "t.cs", 12, null, [], [], [], ["Sat.Power.PowerBus"]),
            new CodeType("Sat.Power.PowerBus", "PowerBus", "Sat.Power", CodeTypeKind.Class, "p.cs", 3, null, [], [], [], []),
        ], []);
        var v = ArchitectureConformance.Check(ThermalModel(), code).Violations.Single(x => x.Rule == ConformanceRule.Dependency);
        Assert.Empty(v.Locations);
        Assert.Equal(0, v.MoreLocations);
        Assert.Equal("t.cs:12", v.FirstLocation);
    }
}
