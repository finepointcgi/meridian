using Meridian.Core.CodeSync;
using Meridian.Core.Model;
using Meridian.Serialization.CodeSync;

namespace Meridian.Tests;

public class CodeSyncJavaTests
{
    private static CodeModel Read(params (string Path, string Content)[] files) =>
        new JavaCodeReader().Read(files.Select(f => new CodeFile(f.Path, f.Content)).ToList());

    private static CodeType Type(CodeModel m, string symbol) => Assert.Single(m.Types, t => t.Symbol == symbol);
    private static CodeMember Member(CodeType t, string name) => Assert.Single(t.Members, x => x.Name == name);

    /// <summary>A Maven-style rover controller: packages, imports, generics, nested types, records and enums.</summary>
    internal static readonly IReadOnlyList<CodeFile> Rover =
    [
        new("src/main/java/com/acme/rover/power/Battery.java", """
            package com.acme.rover.power;

            import java.util.ArrayList;
            import java.util.List;
            import java.util.Optional;
            import com.acme.rover.core.*;

            /**
             * Stores energy for the night.
             * <p>See {@link Cell} for the chemistry.
             * @author someone
             */
            @Entity
            @Table(name = "battery")
            public class Battery extends Component implements PowerSource, Comparable<Battery> {
                /** Cells in series. */
                private final List<Cell> cells = new ArrayList<>();
                protected Cell[] spares;
                public Optional<Cell> backup = Optional.empty();
                public static final int MAX_CELLS = 8, MIN_CELLS = 2;
                double voltage;
                public Map<String, Cell> byName = new HashMap<String, Cell>(), other;

                public Battery(String serial) { super(serial); }

                /** Charges the battery. */
                @Override
                public boolean charge(double watts) { return watts > 0; }
                public boolean charge(double watts, int minutes) throws ChargeException { return true; }
                public synchronized <T extends Cell> List<T> select(Class<T> kind, String... tags) { return List.of(); }
                public double output() {
                    Telemetry.send(new Sample(voltage));
                    return voltage;
                }

                public static class Cell {
                    public double volts;
                    public enum Chemistry { LI_ION, NIMH }
                }
            }
            """),
        new("src/main/java/com/acme/rover/core/Component.java", """
            package com.acme.rover.core;

            public abstract class Component {
                protected String id;
                protected Component(String id) { this.id = id; }
                public abstract void reset();
                public String getId() { return id; }
            }
            """),
        new("src/main/java/com/acme/rover/core/PowerSource.java", """
            package com.acme.rover.core;

            public interface PowerSource extends Source<Double> {
                double MAX = 100.0;
                double output();
                default boolean healthy() { return output() > 0; }
                static PowerSource none() { return () -> 0; }
            }
            """),
        new("src/main/java/com/acme/rover/core/Source.java", "package com.acme.rover.core;\npublic interface Source<T> { T read(); }\n"),
        new("src/main/java/com/acme/rover/core/Telemetry.java", """
            package com.acme.rover.core;
            public final class Telemetry {
                private Telemetry() {}
                public static void send(Sample s) { }
            }
            """),
        new("src/main/java/com/acme/rover/core/Sample.java", "package com.acme.rover.core;\npublic record Sample(double value, java.time.Instant at) implements Comparable<Sample> {\n  public Sample { }\n  public int compareTo(Sample o) { return 0; }\n}\n"),
        new("src/main/java/com/acme/rover/core/ChargeException.java", "package com.acme.rover.core;\npublic class ChargeException extends Exception { }\n"),
        new("src/main/java/com/acme/rover/core/Mode.java", """
            package com.acme.rover.core;
            /** Operating modes. */
            public enum Mode {
                /** Safe mode. */
                SAFE("s") { @Override public String label() { return "safe"; } },
                NOMINAL("n"),
                SCIENCE("x");
                private final String code;
                Mode(String code) { this.code = code; }
                public String label() { return code; }
            }
            """),
        new("src/main/java/com/acme/rover/Rover.java", """
            package com.acme.rover;

            import com.acme.rover.power.Battery;
            import com.acme.rover.power.Battery.Cell;
            import com.acme.rover.core.Mode;
            import com.acme.rover.core.PowerSource;

            public class Rover {
                private Battery battery;
                public List<Battery> packs;
                public Cell reserve;
                public Mode mode = Mode.SAFE;
                public PowerSource source;
                public com.acme.rover.core.Telemetry telemetry;
                public Battery.Cell.Chemistry chemistry;
                @interface Tracked { String value() default ""; int level(); }
            }
            """),
        new("target/generated-sources/Gen.java", "package gen; public class Gen {}"),
        new("src/main/java/module-info.java", "module rover { }"),
    ];

    [Fact]
    public void Reads_packages_classes_members_and_docs()
    {
        var m = new JavaCodeReader().Read(Rover);
        Assert.Equal("java", m.Language);
        Assert.True(m.Warnings.Count == 0, string.Join("\n", m.Warnings));
        Assert.DoesNotContain(m.Types, t => t.Symbol == "gen.Gen");

        var battery = Type(m, "com.acme.rover.power.Battery");
        Assert.Equal((CodeTypeKind.Class, "com.acme.rover.power", 15), (battery.Kind, battery.Namespace, battery.Line));
        Assert.Equal("Stores energy for the night. See Cell for the chemistry.", battery.Doc);
        Assert.Equal(["com.acme.rover.core.Component"], battery.BaseTypes);
        Assert.Equal(["com.acme.rover.core.PowerSource"], battery.Interfaces);
        Assert.Equal(["cells", "spares", "backup", "MAX_CELLS", "MIN_CELLS", "voltage", "byName", "other", "charge", "charge", "select", "output"],
            battery.Members.Select(x => x.Name));
        var cells = Member(battery, "cells");
        Assert.Equal(("List<Cell>", "com.acme.rover.power.Battery.Cell", true, "private", "Cells in series."), (cells.TypeName, cells.TypeSymbol, cells.IsCollection, cells.Visibility, cells.Doc));
        Assert.Equal(("protected", true), (Member(battery, "spares").Visibility, Member(battery, "spares").IsCollection));
        var backup = Member(battery, "backup");
        Assert.Equal(("com.acme.rover.power.Battery.Cell", false, true), (backup.TypeSymbol, backup.IsCollection, backup.IsOptional));
        Assert.True(Member(battery, "MIN_CELLS").IsStatic);
        Assert.Equal("int", Member(battery, "MIN_CELLS").TypeName);
        Assert.Equal("internal", Member(battery, "voltage").Visibility);   // package-private
        Assert.Equal(("Map<String, Cell>", true), (Member(battery, "other").TypeName, Member(battery, "other").IsCollection));

        var charges = battery.Members.Where(x => x.Name == "charge").ToList();
        Assert.Equal(["double", "double,int"], charges.Select(c => string.Join(",", c.Parameters.Select(p => p.TypeName))));
        Assert.Equal("Charges the battery.", charges[0].Doc);
        Assert.Equal(("boolean", "watts"), (charges[0].TypeName, charges[0].Parameters[0].Name));
        var select = Member(battery, "select");
        Assert.Equal("List<T>", select.TypeName);
        Assert.Null(select.TypeSymbol);   // T is the method's type parameter
        Assert.Equal([("kind", "Class<T>"), ("tags", "String...")], select.Parameters.Select(p => (p.Name, p.TypeName)));

        var cell = Type(m, "com.acme.rover.power.Battery.Cell");
        Assert.True(cell.IsStatic);
        Assert.Equal("Cell", cell.Name);
        Assert.Equal(CodeTypeKind.Enum, Type(m, "com.acme.rover.power.Battery.Cell.Chemistry").Kind);

        var component = Type(m, "com.acme.rover.core.Component");
        Assert.True(component.IsAbstract);
        Assert.True(Member(component, "reset").IsAbstract);
        Assert.Equal(["id", "reset", "getId"], component.Members.Select(x => x.Name));   // constructors are not members
    }

    [Fact]
    public void Interfaces_records_enums_and_annotations()
    {
        var m = new JavaCodeReader().Read(Rover);
        var source = Type(m, "com.acme.rover.core.PowerSource");
        Assert.Equal(CodeTypeKind.Interface, source.Kind);
        Assert.Equal(["com.acme.rover.core.Source"], source.BaseTypes);   // interface extends → generalization
        Assert.Equal(["MAX", "output", "healthy", "none"], source.Members.Select(x => x.Name));
        Assert.Equal(("public", true), (Member(source, "MAX").Visibility, Member(source, "MAX").IsStatic));
        Assert.True(Member(source, "none").IsStatic);
        Assert.Equal("Source<T>", Type(m, "com.acme.rover.core.Source").Name);

        var sample = Type(m, "com.acme.rover.core.Sample");
        Assert.Equal(CodeTypeKind.Record, sample.Kind);
        Assert.Equal(["value", "at", "compareTo"], sample.Members.Select(x => x.Name));
        Assert.Equal(("java.time.Instant", "public"), (Member(sample, "at").TypeName, Member(sample, "at").Visibility));

        var mode = Type(m, "com.acme.rover.core.Mode");
        Assert.Equal((CodeTypeKind.Enum, "Operating modes."), (mode.Kind, mode.Doc));
        Assert.Equal(["SAFE", "NOMINAL", "SCIENCE", "code", "label"], mode.Members.Select(x => x.Name));
        Assert.Equal("Safe mode.", mode.Members[0].Doc);

        var tracked = Type(m, "com.acme.rover.Rover.Tracked");
        Assert.Equal(CodeTypeKind.Interface, tracked.Kind);
        Assert.Equal(["value", "level"], tracked.Members.Select(x => x.Name));
    }

    [Fact]
    public void Imports_nested_types_and_qualified_names_resolve()
    {
        var m = new JavaCodeReader().Read(Rover);
        var rover = Type(m, "com.acme.rover.Rover");
        Assert.Equal("com.acme.rover.power.Battery", Member(rover, "battery").TypeSymbol);
        Assert.Equal("List<Battery>", Member(rover, "packs").TypeName);
        Assert.True(Member(rover, "packs").IsCollection);
        Assert.Equal("com.acme.rover.power.Battery.Cell", Member(rover, "reserve").TypeSymbol);   // import of a nested type
        Assert.Equal("com.acme.rover.core.Mode", Member(rover, "mode").TypeSymbol);
        Assert.Equal("com.acme.rover.core.Telemetry", Member(rover, "telemetry").TypeSymbol);   // fully qualified
        Assert.Equal("com.acme.rover.power.Battery.Cell.Chemistry", Member(rover, "chemistry").TypeSymbol);

        // Usage: a static call, a creation and a throws clause.
        var battery = Type(m, "com.acme.rover.power.Battery");
        Assert.Equal(["com.acme.rover.core.ChargeException", "com.acme.rover.core.Sample", "com.acme.rover.core.Telemetry"], battery.Dependencies);
        Assert.Equal(["com.acme.rover.core.Sample"], Type(m, "com.acme.rover.core.Telemetry").Dependencies);
    }

    [Fact]
    public void Broken_syntax_warns_and_keeps_the_rest()
    {
        var m = Read(
            ("a/Broken.java", "package p;\npublic class Broken {\n  int x;\n  void bad( { }\n  ??? !!! ;\n  public String name;\n"),
            ("a/Fine.java", "package p;\npublic class Fine { Broken b; }"),
            ("a/Junk.java", "}}} class { interface ))) enum record ( @"));
        Assert.Contains(m.Types, t => t.Symbol == "p.Broken");
        Assert.Equal("p.Broken", Member(Type(m, "p.Fine"), "b").TypeSymbol);
        Assert.Contains(m.Warnings, w => w.StartsWith("a/Broken.java:") && w.Contains("could not be parsed"));
    }

    [Fact]
    public void Output_is_deterministic_regardless_of_file_order()
    {
        var a = new JavaCodeReader().Read(Rover);
        var b = new JavaCodeReader().Read(Rover.Reverse().ToList());
        Assert.Equal(CodeSyncTestText.Describe(a), CodeSyncTestText.Describe(b));
    }

    [Fact]
    public void Syncing_java_keeps_overloads_apart_and_a_second_sync_changes_nothing()
    {
        var m = new ModelRepository("Rover");
        var options = new CodeSyncOptions { RepoId = "java", Commit = "c1" };
        var first = CodeModelMapper.Apply(m, new JavaCodeReader().Read(Rover), options);
        Assert.True(first.Warnings.Count == 0, string.Join("\n", first.Warnings));
        Element S(string symbol) => m.AllElements.Single(e => e.Get(CodeFieldKeys.Symbol) == symbol);
        Assert.Equal("(watts : double) : boolean", S("com.acme.rover.power.Battery.charge(double)").Get(FieldKeys.Signature));
        Assert.Equal("(watts : double, minutes : int) : boolean", S("com.acme.rover.power.Battery.charge(double,int)").Get(FieldKeys.Signature));
        Assert.Equal("(kind : Class<T>, tags : String...) : List<T>", S("com.acme.rover.power.Battery.select(Class<T>,String...)").Get(FieldKeys.Signature));
        Assert.Equal("Charges the battery.", S("com.acme.rover.power.Battery.charge(double)").Documentation);
        Assert.Equal("true", S("com.acme.rover.core.Component").Get(FieldKeys.IsAbstract));
        Assert.Equal("true", S("com.acme.rover.core.Component.reset()").Get(FieldKeys.IsAbstract));
        Assert.Equal("", S("com.acme.rover.core.PowerSource.output()").Get(FieldKeys.IsAbstract));   // interface operations are not flagged
        Assert.Equal("true", S("com.acme.rover.core.PowerSource.none()").Get(CodeModelMapper.IsStaticKey));
        Assert.Equal(ElementKind.Block, S("com.acme.rover.power.Battery.Cell").Kind);
        Assert.Same(S("com.acme.rover.power.Battery"), S("com.acme.rover.power.Battery.Cell").Owner);

        var again = CodeModelMapper.Apply(m, new JavaCodeReader().Read(Rover.Reverse().ToList()), options with { Commit = "c2" });
        Assert.True(again.IsEmpty);
    }
}
