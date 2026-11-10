using Meridian.Core.CodeSync;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization.CodeSync;

namespace Meridian.Tests;

public class CodeSyncTypeScriptTests
{
    private static CodeModel Read(params (string Path, string Content)[] files) =>
        new TypeScriptCodeReader().Read(files.Select(f => new CodeFile(f.Path, f.Content)).ToList());

    private static CodeType Type(CodeModel m, string symbol) => Assert.Single(m.Types, t => t.Symbol == symbol);
    private static CodeMember Member(CodeType t, string name) => Assert.Single(t.Members, x => x.Name == name);

    /// <summary>A small ground-station app: models in folders, a barrel index.ts, decorators, generics and overloads.</summary>
    internal static readonly IReadOnlyList<CodeFile> GroundStation =
    [
        new("src/power/battery.ts", """
            import { Cell } from "./cell";
            import type { PowerSource } from "../core/contracts";

            /** Stores energy for eclipse. @see Cell */
            export class Battery implements PowerSource {
              /** Cells in series. */
              cells: Cell[] = [];
              private readonly serial: string;
              spare?: Cell;
              static readonly MAX = 8;
              #secret = 1;

              constructor(serial: string, public readonly nominal: number) {
                this.serial = serial;
              }

              /** Charge with a power level. */
              charge(watts: number, until?: Date): boolean {
                const c = new Cell(3.7);
                return watts > 0 && c.volts > 0;
              }
              output(): number { return this.cells.length; }
              get capacity(): number { return 0 }
              set capacity(v: number) { }
            }
            """),
        new("src/power/cell.ts", """
            export class Cell {
              constructor(public volts: number) {}
            }
            """),
        new("src/power/index.ts", """
            export * from "./battery";
            export { Cell as PowerCell } from "./cell";
            """),
        new("src/core/contracts.ts", """
            export interface PowerSource {
              output(): number;
              readonly label?: string
              describe(prefix: string, ...rest: string[]): string
            }

            export interface Named { name: string }

            /** A raw reading. */
            export type Reading = Named & {
              value: number;
              at: Date;
            };

            export type Mode = "safe" | "nominal";

            export enum Health { Good, Degraded = 2, Failed = "failed" }

            export const enum Flag { A, B }
            """),
        new("src/core/repo.ts", """
            export abstract class Repository<T, K = string> {
              protected items = new Map<K, T>();
              abstract find(id: K): T | undefined;
              all(): Array<T> { return [...this.items.values()]; }
            }
            """),
        new("src/station/station.ts", """
            import { Battery, PowerCell } from "../power";
            import * as core from "../core/contracts";
            import { Repository } from "../core/repo";
            import { Logger } from "@acme/logging";
            import Antenna from "./antenna";

            function track(target: any) { }

            @track
            export class Station extends Repository<core.Reading> {
              @track() battery: Battery;
              backups: Map<string, Battery>;
              antenna: Antenna | null = null;
              mode: core.Mode = "safe";
              health: core.Health;
              source: core.PowerSource;
              log: Logger;

              find(id: string): core.Reading | undefined { return undefined; }

              /** Point the antenna. */
              point(az: number, el: number): void;
              point(target: core.Named): void;
              point(a: number | core.Named, el?: number): void {
                PowerCell.name;
              }

              async sync(readings: core.Reading[], opts: { retry: number } = { retry: 1 }): Promise<number> {
                return readings.length;
              }
            }
            """),
        new("src/station/antenna.tsx", """
            export default class Antenna {
              gain = 12.5
              dish = new Dish()
              render() { return <div className="x">{`az ${this.gain}`}</div>; }
            }
            class Dish { size = 3 }
            """),
        new("src/legacy/globals.ts", """
            namespace Legacy.Units {
              export class Meter { value: number }
              export interface Converter { convert(m: Meter): number }
            }
            class GlobalHelper { u: Legacy.Units.Meter }
            """),
        new("node_modules/lib/index.d.ts", "export declare class Hidden {}"),
        new("README.md", "# not code"),
    ];

    [Fact]
    public void Reads_classes_interfaces_enums_aliases_and_namespaces()
    {
        var m = new TypeScriptCodeReader().Read(GroundStation);
        Assert.Equal("typescript", m.Language);
        Assert.True(m.Warnings.Count == 0, string.Join("\n", m.Warnings));
        Assert.Equal(
            new[]
            {
                "src.core.Flag", "src.core.Health", "src.core.Named", "src.core.PowerSource", "src.core.Reading", "src.core.Repository",
                "src.legacy.GlobalHelper", "src.legacy.Legacy.Units.Converter", "src.legacy.Legacy.Units.Meter", "src.power.Battery", "src.power.Cell",
                "src.station.Antenna", "src.station.Dish", "src.station.Station",
            }.Order(StringComparer.Ordinal),
            m.Types.Select(t => t.Symbol));

        var battery = Type(m, "src.power.Battery");
        Assert.Equal((CodeTypeKind.Class, "src.power", "src/power/battery.ts", 5), (battery.Kind, battery.Namespace, battery.File, battery.Line));
        Assert.Equal("Stores energy for eclipse.", battery.Doc);
        Assert.Equal(["src.core.PowerSource"], battery.Interfaces);
        Assert.Equal(["cells", "serial", "spare", "MAX", "#secret", "nominal", "charge", "output", "capacity"], battery.Members.Select(x => x.Name));
        var cells = Member(battery, "cells");
        Assert.Equal((CodeMemberKind.Property, "Cell[]", "src.power.Cell", true, "Cells in series."), (cells.Kind, cells.TypeName, cells.TypeSymbol, cells.IsCollection, cells.Doc));
        Assert.Equal(7, cells.Line);
        Assert.Equal(("private", "string"), (Member(battery, "serial").Visibility, Member(battery, "serial").TypeName));
        Assert.True(Member(battery, "spare").IsOptional);
        Assert.Equal("src.power.Cell", Member(battery, "spare").TypeSymbol);
        Assert.True(Member(battery, "MAX").IsStatic);
        Assert.Equal("private", Member(battery, "#secret").Visibility);
        Assert.Equal(("number", "public"), (Member(battery, "nominal").TypeName, Member(battery, "nominal").Visibility));
        var charge = Member(battery, "charge");
        Assert.Equal((CodeMemberKind.Method, "boolean", "Charge with a power level."), (charge.Kind, charge.TypeName, charge.Doc));
        Assert.Equal([("watts", "number"), ("until", "Date")], charge.Parameters.Select(p => (p.Name, p.TypeName)));
        Assert.Equal((CodeMemberKind.Property, "number"), (Member(battery, "capacity").Kind, Member(battery, "capacity").TypeName));

        var source = Type(m, "src.core.PowerSource");
        Assert.Equal(CodeTypeKind.Interface, source.Kind);
        Assert.Equal(["output", "label", "describe"], source.Members.Select(x => x.Name));
        Assert.True(Member(source, "label").IsOptional);
        Assert.Equal([("prefix", "string"), ("rest", "string[]")], Member(source, "describe").Parameters.Select(p => (p.Name, p.TypeName)));

        var reading = Type(m, "src.core.Reading");
        Assert.Equal((CodeTypeKind.Interface, "A raw reading."), (reading.Kind, reading.Doc));
        Assert.Equal(["value", "at"], reading.Members.Select(x => x.Name));
        Assert.Equal(["src.core.Named"], reading.BaseTypes);
        Assert.DoesNotContain(m.Types, t => t.Name == "Mode");   // a union alias is not an object type

        var health = Type(m, "src.core.Health");
        Assert.Equal(CodeTypeKind.Enum, health.Kind);
        Assert.Equal(["Good", "Degraded", "Failed"], health.Members.Select(x => x.Name));
        Assert.All(health.Members, x => Assert.Equal(CodeMemberKind.EnumValue, x.Kind));
        Assert.Equal(["A", "B"], Type(m, "src.core.Flag").Members.Select(x => x.Name));

        var meter = Type(m, "src.legacy.Legacy.Units.Meter");
        Assert.Equal("src.legacy.Legacy.Units", meter.Namespace);
        Assert.Equal("src.legacy.Legacy.Units.Meter", Member(Type(m, "src.legacy.Legacy.Units.Converter"), "convert").Parameters[0].TypeSymbol);
        Assert.Equal("src.legacy.Legacy.Units.Meter", Member(Type(m, "src.legacy.GlobalHelper"), "u").TypeSymbol);
    }

    [Fact]
    public void Imports_barrels_defaults_and_namespace_imports_resolve_across_files()
    {
        var m = new TypeScriptCodeReader().Read(GroundStation);
        var station = Type(m, "src.station.Station");
        Assert.Equal(["src.core.Repository"], station.BaseTypes);
        Assert.Equal("src.power.Battery", Member(station, "battery").TypeSymbol);   // via the index.ts barrel (export *)
        var backups = Member(station, "backups");
        Assert.Equal(("src.power.Battery", true), (backups.TypeSymbol, backups.IsCollection));
        var antenna = Member(station, "antenna");
        Assert.Equal(("src.station.Antenna", false, true), (antenna.TypeSymbol, antenna.IsCollection, antenna.IsOptional));   // default import, T | null
        Assert.Equal("src.core.Health", Member(station, "health").TypeSymbol);   // core.Health through "* as core"
        Assert.Equal("src.core.PowerSource", Member(station, "source").TypeSymbol);
        Assert.Null(Member(station, "mode").TypeSymbol);   // a union alias stays text
        Assert.Equal(("Logger", null), (Member(station, "log").TypeName, Member(station, "log").TypeSymbol));   // package import: not resolved

        // Overload signatures are the methods; the implementation behind them is not listed again.
        var points = station.Members.Where(x => x.Name == "point").ToList();
        Assert.Equal(2, points.Count);
        Assert.Equal(["number,number", "core.Named"], points.Select(p => string.Join(",", p.Parameters.Select(x => x.TypeName))));
        Assert.Equal("Point the antenna.", points[0].Doc);
        Assert.Equal("src.core.Named", points[1].Parameters[0].TypeSymbol);

        var sync = Member(station, "sync");
        Assert.Equal("Promise<number>", sync.TypeName);
        Assert.Equal(("src.core.Reading", true), (sync.Parameters[0].TypeSymbol, sync.Parameters[0].IsCollection));
        Assert.Equal("{ retry: number }", sync.Parameters[1].TypeName);

        // Re-exported alias (export { Cell as PowerCell }) used in a body is a dependency; types already held are not.
        Assert.Contains("src.power.Cell", station.Dependencies);
        Assert.Contains("src.core.Reading", station.Dependencies);
        Assert.DoesNotContain("src.power.Battery", station.Dependencies);
        Assert.Empty(Type(m, "src.power.Battery").Dependencies);   // Cell is held by "cells"; PowerSource is realized

        var dish = Member(Type(m, "src.station.Antenna"), "dish");
        Assert.Equal(("Dish", "src.station.Dish"), (dish.TypeName, dish.TypeSymbol));   // inferred from "= new Dish()"
    }

    [Fact]
    public void Generic_classes_keep_their_type_parameters_out_of_resolution()
    {
        var m = new TypeScriptCodeReader().Read(GroundStation);
        var repo = Type(m, "src.core.Repository");
        Assert.Equal(("Repository<T, K>", true), (repo.Name, repo.IsAbstract));
        var find = Member(repo, "find");
        Assert.True(find.IsAbstract);
        Assert.Equal(("T | undefined", null, true), (find.TypeName, find.TypeSymbol, find.IsOptional));
        Assert.Equal("protected", Member(repo, "items").Visibility);
    }

    [Fact]
    public void Skips_node_modules_and_other_files()
    {
        var r = new TypeScriptCodeReader();
        Assert.True(r.Handles("src/a.ts"));
        Assert.True(r.Handles("src/view.tsx"));
        Assert.True(r.Handles("types/global.d.ts"));
        Assert.False(r.Handles("node_modules/lib/index.d.ts"));
        Assert.False(r.Handles("web/node_modules/x/y.ts"));
        Assert.False(r.Handles("src/a.js"));
        Assert.DoesNotContain(r.Read(GroundStation).Types, t => t.Name == "Hidden");
    }

    [Fact]
    public void Broken_syntax_warns_and_keeps_the_rest()
    {
        var m = Read(
            ("a.ts", "export class Broken {\n  x: number\n  bad(: void {\n  y = ;\n"),
            ("b.ts", "import { Broken } from './a';\nexport class Fine { b: Broken; c: { unclosed: number }\n  @@@ ### \n  d = `tpl ${ { a: 1 } }`;\n  e = /re}gex/g;\n  f: string }\n"),
            ("c.ts", "}}}} class ))) interface { enum"),
            ("d.ts", "export class Last { ok: boolean }"));
        Assert.Contains(m.Types, t => t.Symbol == "Broken");
        Assert.Equal("Broken", Member(Type(m, "Fine"), "b").TypeSymbol);
        Assert.Contains(Type(m, "Fine").Members, x => x.Name == "f");
        Assert.Contains(m.Types, t => t.Symbol == "Last");
        Assert.Contains(m.Warnings, w => w.StartsWith("a.ts:") && w.Contains("could not be parsed"));
    }

    [Fact]
    public void Output_is_deterministic_regardless_of_file_order()
    {
        var a = new TypeScriptCodeReader().Read(GroundStation);
        var b = new TypeScriptCodeReader().Read(GroundStation.Reverse().ToList());
        Assert.Equal(CodeSyncTestText.Describe(a), CodeSyncTestText.Describe(b));
    }

    [Fact]
    public void Limits_and_cancellation_are_respected()
    {
        var src = string.Concat(Enumerable.Range(0, 30).Select(i => $"export class C{i} {{ }}\n"));
        var m = new TypeScriptCodeReader { MaxTypes = 10 }.Read([new CodeFile("many.ts", src)]);
        Assert.Equal(10, m.Types.Count);
        Assert.Contains(m.Warnings, w => w.Contains("first 10"));
        Assert.Contains(new TypeScriptCodeReader { MaxFileLength = 10 }.Read([new CodeFile("big.ts", src)]).Warnings, w => w.StartsWith("big.ts: skipped"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new TypeScriptCodeReader().Read([new CodeFile("a.ts", src)], cts.Token));
    }

    [Fact]
    public void Syncing_typescript_builds_the_model_and_a_second_sync_changes_nothing()
    {
        var m = new ModelRepository("Mission");
        var options = new CodeSyncOptions { RepoId = "ts", Commit = "c1" };
        var first = CodeModelMapper.Apply(m, new TypeScriptCodeReader().Read(GroundStation), options);
        Assert.True(first.Warnings.Count == 0, string.Join("\n", first.Warnings));
        Element S(string symbol) => m.AllElements.Single(e => e.Get(CodeFieldKeys.Symbol) == symbol);
        Assert.Equal(ElementKind.Package, S("ns:src.station").Kind);
        var station = S("src.station.Station");
        Assert.Equal(ElementKind.Block, station.Kind);
        Assert.Equal(S("src.power.Battery").Id, S("src.station.Station.battery").TypeRef);
        Assert.Equal("*", S("src.station.Station.backups").Multiplicity);
        Assert.Equal("0..1", S("src.station.Station.antenna").Multiplicity);
        Assert.Equal("(az : number, el : number)", S("src.station.Station.point(number,number)").Get(FieldKeys.Signature));
        Assert.Equal("(target : core.Named)", S("src.station.Station.point(core.Named)").Get(FieldKeys.Signature));
        Assert.Equal("true", S("src.core.Repository").Get(FieldKeys.IsAbstract));
        Assert.Equal("true", S("src.core.Repository.find(K)").Get(FieldKeys.IsAbstract));
        Assert.Equal("true", S("src.power.Battery.MAX").Get(CodeModelMapper.IsStaticKey));
        Assert.Equal("Cells in series.", S("src.power.Battery.cells").Documentation);
        Assert.Equal(ElementKind.Realization, Assert.IsType<Relationship>(S("real:src.power.Battery->src.core.PowerSource")).Kind);
        Assert.Equal(ElementKind.Generalization, Assert.IsType<Relationship>(S("gen:src.station.Station->src.core.Repository")).Kind);
        Assert.Equal(ElementKind.Enumeration, S("src.core.Health").Kind);

        var again = CodeModelMapper.Apply(m, new TypeScriptCodeReader().Read(GroundStation.Reverse().ToList()), options with { Commit = "c2" });
        Assert.True(again.IsEmpty, string.Join(", ", again.Changed.Select(id => m.Find(id)?.Get(CodeFieldKeys.Symbol))));
    }
}

/// <summary>Shared text form of a code model, for determinism checks.</summary>
internal static class CodeSyncTestText
{
    public static string Describe(CodeModel m) => string.Join("\n", m.Types.Select(t =>
        $"{t.Symbol}|{t.Name}|{t.Kind}|{t.File}:{t.Line}|{t.Doc}|{t.IsAbstract}|{t.IsStatic}|{string.Join(",", t.BaseTypes)}|{string.Join(",", t.Interfaces)}|{string.Join(",", t.Dependencies)}|" +
        string.Join(";", t.Members.Select(x => $"{x.Name}:{x.Kind}:{x.TypeName}:{x.TypeSymbol}:{x.IsCollection}:{x.Visibility}:{x.IsStatic}:{x.IsAbstract}:{x.IsOptional}:{x.Line}:{x.Doc}(" +
            string.Join(",", x.Parameters.Select(p => $"{p.Name}:{p.TypeName}:{p.TypeSymbol}:{p.IsCollection}:{p.Direction}")) + ")")))) + "\n" + string.Join("\n", m.Warnings);
}
