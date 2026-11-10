using Meridian.Core.CodeSync;
using Meridian.Serialization.CodeSync;

namespace Meridian.Tests;

public class CodeSyncReaderTests
{
    private static CodeModel Read(params (string Path, string Content)[] files) =>
        new CSharpCodeReader().Read(files.Select(f => new CodeFile(f.Path, f.Content)).ToList());

    private static CodeType Type(CodeModel m, string symbol) => Assert.Single(m.Types, t => t.Symbol == symbol);

    [Fact]
    public void Reads_classes_with_members_doc_and_location()
    {
        var m = Read(("src/Power/Battery.cs", """
            namespace Sat.Power;

            /// <summary>
            /// Stores energy for eclipse, see <see cref="Sat.Power.Eps"/>.
            /// </summary>
            public class Battery
            {
                public double Capacity { get; set; }
                public int cycles;
                private string _serial = "";
                public bool Charge(double watts) => watts > 0;
                public void Reset() { }
                public Battery() { }
                public static Battery operator +(Battery a, Battery b) => a;
            }
            """));
        Assert.Empty(m.Warnings);
        Assert.Equal("csharp", m.Language);
        var b = Type(m, "Sat.Power.Battery");
        Assert.Equal("Battery", b.Name);
        Assert.Equal("Sat.Power", b.Namespace);
        Assert.Equal(CodeTypeKind.Class, b.Kind);
        Assert.Equal("src/Power/Battery.cs", b.File);
        Assert.Equal(6, b.Line);
        Assert.Equal("Stores energy for eclipse, see Sat.Power.Eps.", b.Doc);
        Assert.Equal(["Capacity", "cycles", "_serial", "Charge", "Reset"], b.Members.Select(x => x.Name));
        var cap = b.Members[0];
        Assert.Equal((CodeMemberKind.Property, "double", "public"), (cap.Kind, cap.TypeName, cap.Visibility));
        Assert.Null(cap.TypeSymbol);
        Assert.Equal((CodeMemberKind.Field, "private"), (b.Members[2].Kind, b.Members[2].Visibility));
        Assert.Equal((CodeMemberKind.Method, "bool"), (b.Members[3].Kind, b.Members[3].TypeName));
        Assert.Equal("void", b.Members[4].TypeName);
    }

    [Fact]
    public void Records_structs_interfaces_and_enums_get_their_kinds()
    {
        var m = Read(("a.cs", """
            namespace N;
            public record Reading(double Volts, Mode Mode);
            public readonly record struct Point(int X, int Y);
            public struct Vec { public float X; }
            public interface ISensor { double Read(); string Name { get; } }
            public enum Mode { Off, Safe = 2, Nominal }
            """));
        Assert.Equal(CodeTypeKind.Record, Type(m, "N.Reading").Kind);
        Assert.Equal(CodeTypeKind.Record, Type(m, "N.Point").Kind);
        Assert.Equal(CodeTypeKind.Struct, Type(m, "N.Vec").Kind);
        Assert.Equal(CodeTypeKind.Interface, Type(m, "N.ISensor").Kind);
        var mode = Type(m, "N.Mode");
        Assert.Equal(CodeTypeKind.Enum, mode.Kind);
        Assert.Equal(["Off", "Safe", "Nominal"], mode.Members.Select(x => x.Name));
        Assert.All(mode.Members, x => Assert.Equal(CodeMemberKind.EnumValue, x.Kind));

        // Positional record parameters are properties; synthesized members (EqualityContract, Equals…) are not.
        var reading = Type(m, "N.Reading");
        Assert.Equal(["Volts", "Mode"], reading.Members.Select(x => x.Name));
        Assert.Equal("N.Mode", reading.Members[1].TypeSymbol);

        var sensor = Type(m, "N.ISensor");
        Assert.Equal(["Read", "Name"], sensor.Members.Select(x => x.Name));
        Assert.All(sensor.Members, x => Assert.Equal("public", x.Visibility));
    }

    [Fact]
    public void Nested_and_generic_types_get_qualified_symbols()
    {
        var m = Read(("a.cs", """
            namespace N
            {
                public class Outer
                {
                    public Inner Child { get; set; } = new();
                    public class Inner { public Deeper? D; public class Deeper { } }
                }
                public class Box<T> { public T Value = default!; public Box<T>? Next; }
                public class User { public Box<Outer> B = new(); }
            }
            public class Global { }
            """));
        Assert.Equal(["Global", "N.Box`1", "N.Outer", "N.Outer.Inner", "N.Outer.Inner.Deeper", "N.User"], m.Types.Select(t => t.Symbol));
        var inner = Type(m, "N.Outer.Inner");
        Assert.Equal(("Inner", "N"), (inner.Name, inner.Namespace));
        Assert.Equal("N.Outer.Inner", Type(m, "N.Outer").Members[0].TypeSymbol);
        Assert.Equal("N.Outer.Inner.Deeper", inner.Members[0].TypeSymbol);
        var box = Type(m, "N.Box`1");
        Assert.Equal("Box<T>", box.Name);
        Assert.Equal("N.Box`1", box.Members[1].TypeSymbol);
        Assert.Null(box.Members[0].TypeSymbol);
        Assert.Equal("N.Box`1", Type(m, "N.User").Members[0].TypeSymbol);
        Assert.Equal("", Type(m, "Global").Namespace);
    }

    [Fact]
    public void Collections_unwrap_to_their_element_type()
    {
        var m = Read(("a.cs", """
            using System.Collections.Generic;
            namespace N;
            public class Cell { }
            public class Pack
            {
                public List<Cell> Cells { get; } = new();
                public Cell[] Spares = [];
                public IReadOnlyList<Cell> View => Cells;
                public IEnumerable<Cell> All => Cells;
                public ICollection<Cell>? Maybe;
                public Dictionary<string, Cell> ByName = new();
                public Cell Main = new();
                public Cell? Backup;
                public List<int> Numbers = new();
                public System.Collections.Immutable.ImmutableArray<Cell> Frozen;
            }
            """));
        var pack = Type(m, "N.Pack");
        foreach (var name in new[] { "Cells", "Spares", "View", "All", "Maybe", "ByName", "Frozen" })
        {
            var mem = Assert.Single(pack.Members, x => x.Name == name);
            Assert.Equal(("N.Cell", true), (mem.TypeSymbol, mem.IsCollection));
        }
        Assert.Equal(("N.Cell", false), (pack.Members.Single(x => x.Name == "Main").TypeSymbol, pack.Members.Single(x => x.Name == "Main").IsCollection));
        Assert.Equal("N.Cell", pack.Members.Single(x => x.Name == "Backup").TypeSymbol);
        Assert.Equal("Cell?", pack.Members.Single(x => x.Name == "Backup").TypeName);
        var numbers = pack.Members.Single(x => x.Name == "Numbers");
        Assert.Equal((null, false, "List<int>"), (numbers.TypeSymbol, numbers.IsCollection, numbers.TypeName));
    }

    [Fact]
    public void Inheritance_and_interfaces_are_restricted_to_scanned_types()
    {
        var m = Read(("a.cs", """
            using System;
            namespace N;
            public interface IUnit { }
            public interface IPowered : IUnit, IDisposable { }
            public abstract class Unit : IUnit { }
            public class Heater : Unit, IPowered, IComparable<Heater>, IExternal { public void Dispose() { } public int CompareTo(Heater? o) => 0; }
            public class Custom : External.BaseThing { }
            """));
        var heater = Type(m, "N.Heater");
        Assert.Equal(["N.Unit"], heater.BaseTypes);
        Assert.Equal(["N.IPowered"], heater.Interfaces);
        Assert.Equal(["N.IUnit"], Type(m, "N.IPowered").BaseTypes);
        Assert.Empty(Type(m, "N.IPowered").Interfaces);
        Assert.Equal(["N.IUnit"], Type(m, "N.Unit").Interfaces);
        Assert.Empty(Type(m, "N.Custom").BaseTypes);   // unresolved external base: no crash, not listed
    }

    [Fact]
    public void Usage_dependencies_come_from_bodies_parameters_locals_and_static_calls()
    {
        var m = Read(("a.cs", """
            namespace N;
            public class Log { public static void Write(string s) { } }
            public class Clock { public static Clock Now => new(); }
            public class Packet { }
            public class Frame { }
            public class Buffer { }
            public class Part { }
            public class Base { }
            public class Radio : Base
            {
                public Part Antenna { get; } = new();
                public void Send(Packet p)
                {
                    Log.Write("tx");
                    var f = new Frame();
                    Buffer b = new();
                    var now = Clock.Now;
                    var again = new Radio();
                    var spare = new Part();
                    var unknown = new Missing.Thing();
                }
            }
            """));
        var radio = Type(m, "N.Radio");
        Assert.Equal(["N.Buffer", "N.Clock", "N.Frame", "N.Log", "N.Packet"], radio.Dependencies);
        Assert.Empty(Type(m, "N.Log").Dependencies);
    }

    [Fact]
    public void Partial_classes_across_files_merge()
    {
        var m = Read(
            ("b/Part2.cs", "namespace N; public partial class Ctrl { public int Mode; public void Step() { var t = new Timer(); } }"),
            ("a/Part1.cs", "namespace N; /// <summary>Controller.</summary>\npublic partial class Ctrl { public double Gain { get; set; } }"),
            ("a/Timer.cs", "namespace N; public class Timer { }"));
        var ctrl = Type(m, "N.Ctrl");
        Assert.Equal("a/Part1.cs", ctrl.File);
        Assert.Equal("Controller.", ctrl.Doc);
        Assert.Equal(["Gain", "Mode", "Step"], ctrl.Members.Select(x => x.Name));
        Assert.Equal(["N.Timer"], ctrl.Dependencies);
    }

    [Fact]
    public void Syntax_errors_warn_and_the_rest_is_read()
    {
        var m = Read(
            ("broken.cs", "namespace N; public class Broken { public int X { get; set; } public void M( { } }"),
            ("ok.cs", "namespace N; public class Fine { public Broken B { get; set; } = null!; }"));
        Assert.Contains(m.Warnings, w => w.StartsWith("broken.cs:") && w.Contains("syntax error"));
        Assert.Contains(m.Types, t => t.Symbol == "N.Broken");
        Assert.Equal("N.Broken", Type(m, "N.Fine").Members[0].TypeSymbol);
    }

    [Fact]
    public void Generated_files_are_skipped()
    {
        var reader = new CSharpCodeReader();
        Assert.True(reader.Handles("src/Program.cs"));
        Assert.False(reader.Handles("src/obj/Debug/Gen.cs"));
        Assert.False(reader.Handles("bin/Release/X.cs"));
        Assert.False(reader.Handles("Forms/Main.Designer.cs"));
        Assert.False(reader.Handles("Views/Index.g.cs"));
        Assert.False(reader.Handles("README.md"));
        var m = Read(
            ("obj/A.cs", "namespace N; public class FromObj { }"),
            ("Gen.g.cs", "namespace N; public class FromG { }"),
            ("Form.Designer.cs", "namespace N; public class FromDesigner { }"),
            ("Auto.cs", "// <auto-generated>\n//   by a tool\n// </auto-generated>\nnamespace N; public class FromAuto { }"),
            ("Real.cs", "namespace N; public class Real { public string S = \"<auto-generated>\"; }"),
            ("notes.txt", "class NotCode { }"));
        Assert.Equal(["N.Real"], m.Types.Select(t => t.Symbol));
    }

    [Fact]
    public void Output_is_deterministic_regardless_of_file_order()
    {
        var files = CubeSatCode.Files;
        var a = new CSharpCodeReader().Read(files);
        var b = new CSharpCodeReader().Read(files.Reverse().ToList());
        Assert.Equal(Describe(a), Describe(b));
        Assert.Equal(Describe(a), Describe(new CSharpCodeReader().Read(files)));

        static string Describe(CodeModel m) => string.Join("\n", m.Types.Select(t =>
            $"{t.Symbol}|{t.Kind}|{t.File}:{t.Line}|{t.Doc}|{string.Join(",", t.BaseTypes)}|{string.Join(",", t.Interfaces)}|{string.Join(",", t.Dependencies)}|" +
            string.Join(";", t.Members.Select(x => $"{x.Name}:{x.Kind}:{x.TypeName}:{x.TypeSymbol}:{x.IsCollection}:{x.Visibility}"))));
    }

    [Fact]
    public void Type_cap_and_cancellation_are_respected()
    {
        var src = "namespace N;\n" + string.Concat(Enumerable.Range(0, 30).Select(i => $"public class C{i} {{ }}\n"));
        var m = new CSharpCodeReader { MaxTypes = 10 }.Read([new CodeFile("many.cs", src)]);
        Assert.Equal(10, m.Types.Count);
        Assert.Contains(m.Warnings, w => w.Contains("first 10"));

        var big = new CSharpCodeReader { MaxFileLength = 10 }.Read([new CodeFile("big.cs", src)]);
        Assert.Empty(big.Types);
        Assert.Contains(big.Warnings, w => w.StartsWith("big.cs: skipped"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new CSharpCodeReader().Read([new CodeFile("a.cs", src)], cts.Token));
    }
}

/// <summary>A small CubeSat flight-software code base (Power, Comms, Thermal) used by the code sync tests.</summary>
internal static class CubeSatCode
{
    public const string Satellite = """
        using System.Collections.Generic;
        using Sat.Comms;
        using Sat.Power;
        using Sat.Thermal;

        namespace Sat;

        /// <summary>The 3U CubeSat: owns every subsystem.</summary>
        public class Satellite
        {
            public Eps Eps { get; } = new();
            public Radio Radio { get; } = new();
            public ThermalControl Thermal { get; } = new();
            public Mode Mode { get; private set; } = Mode.Safe;

            public void Tick(double seconds)
            {
                var report = new HealthReport(Eps.Battery.StateOfCharge, Thermal.Hottest());
                Radio.Send(new Packet(report.ToString()));
            }
        }

        public enum Mode { Safe, Nominal, Science }

        public record HealthReport(double StateOfCharge, double MaxTemperature);
        """;

    public const string Power = """
        using System.Collections.Generic;

        namespace Sat.Power
        {
            /// <summary>Electrical power system.</summary>
            public class Eps : ISubsystem
            {
                public Battery Battery { get; } = new();
                public List<SolarPanel> Panels { get; } = new();
                public IPowerSource? Backup { get; set; }
                public string Name => "EPS";
                public double Budget() => 0;
            }

            /// <summary>Li-ion battery pack.</summary>
            public class Battery : IPowerSource
            {
                public double Capacity { get; set; } = 40;
                public double StateOfCharge { get; set; }
                public double Output() => Capacity * StateOfCharge;
            }

            public class SolarPanel : IPowerSource
            {
                public double PeakPower { get; set; } = 4.2;
                public double Output() => PeakPower;
            }

            public interface IPowerSource { double Output(); }
        }

        namespace Sat
        {
            public interface ISubsystem { string Name { get; } }
        }
        """;

    public const string Comms = """
        namespace Sat.Comms;

        /// <summary>S-band radio.</summary>
        public class Radio : Sat.ISubsystem
        {
            public string Name => "COMMS";
            public double DownlinkRate { get; set; } = 2;
            public Antenna Antenna { get; } = new();
            public bool Send(Packet p) { var q = new PacketQueue(); return q.Push(p); }
        }

        public class Antenna { public double Gain { get; set; } }

        public record Packet(string Payload);

        internal class PacketQueue { public bool Push(Packet p) => true; }
        """;

    public const string Thermal = """
        using System.Collections.Generic;
        using System.Linq;

        namespace Sat.Thermal;

        public class ThermalControl : Sat.ISubsystem
        {
            public string Name => "THERMAL";
            public List<Heater> Heaters { get; } = new();
            public Sensor[] Sensors { get; set; } = [];
            public double Hottest() => Sensors.Length == 0 ? 0 : Sensors.Max(s => s.Celsius);
        }

        public class Heater { public double Watts { get; set; } }

        public class Sensor { public double Celsius { get; set; } }
        """;

    public static IReadOnlyList<CodeFile> Files =>
    [
        new("src/Satellite.cs", Satellite),
        new("src/Power/Power.cs", Power),
        new("src/Comms/Radio.cs", Comms),
        new("src/Thermal/Thermal.cs", Thermal),
    ];
}
