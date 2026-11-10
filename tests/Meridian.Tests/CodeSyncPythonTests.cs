using Meridian.Core.CodeSync;
using Meridian.Core.Model;
using Meridian.Serialization.CodeSync;

namespace Meridian.Tests;

public class CodeSyncPythonTests
{
    private static CodeModel Read(params (string Path, string Content)[] files) =>
        new PythonCodeReader().Read(files.Select(f => new CodeFile(f.Path, f.Content)).ToList());

    private static CodeType Type(CodeModel m, string symbol) => Assert.Single(m.Types, t => t.Symbol == symbol);
    private static CodeMember Member(CodeType t, string name) => Assert.Single(t.Members, x => x.Name == name);

    /// <summary>A src-layout package: dataclasses, protocols, ABCs, enums, relative imports and a re-exporting __init__.</summary>
    internal static readonly IReadOnlyList<CodeFile> Lander =
    [
        new("src/lander/__init__.py", "from .power.battery import Battery\nfrom .core import *\n__all__ = ['Battery']\n"),
        new("src/lander/power/__init__.py", ""),
        new("src/lander/power/battery.py", """"
            from __future__ import annotations

            from dataclasses import dataclass, field
            from typing import ClassVar, Optional

            from ..core import PowerSource, Mode
            from . import cell as cells


            @dataclass
            class Battery(PowerSource):
                """Stores energy for the night.

                Longer description that is not part of the summary.
                """

                cells: list[cells.Cell] = field(default_factory=list)
                spare: Optional["cells.Cell"] = None
                mode: Mode = Mode.SAFE
                MAX_CELLS: ClassVar[int] = 8
                _serial: str = ""
                __secret: bytes = b""

                def output(self) -> float:
                    """Present output in watts."""
                    return sum(c.volts for c in self.cells)

                def charge(self, watts: float, *, until: "datetime | None" = None, **opts) -> bool:
                    return watts > 0

                @property
                def voltage(self) -> float:
                    return 0.0

                @voltage.setter
                def voltage(self, value: float) -> None:
                    pass

                @staticmethod
                def nominal(cell_count: int) -> "Battery":
                    return Battery()

                @classmethod
                def empty(cls) -> Battery:
                    return cls()

                def __repr__(self) -> str:
                    return "Battery"
            """"),
        new("src/lander/power/cell.py", """
            class Cell:
                volts: float

                def __init__(self, volts: float, chemistry: "Chemistry" = None, label=None):
                    self.volts = volts
                    self.chemistry = chemistry
                    self.history: list[float] = []
                    self.count = 0
                    self.label = label or "cell"
                    self._parent = Holder()
                    self.volts = 2

            class Holder:
                pass

            class Chemistry:
                pass
            """),
        new("src/lander/core.py", """"
            import abc
            from abc import ABC, abstractmethod
            from enum import Enum, auto
            from typing import Protocol, Generic, TypeVar, overload, Union

            T = TypeVar("T")


            class PowerSource(Protocol):
                def output(self) -> float: ...


            class Device(ABC):
                @abstractmethod
                def reset(self) -> None:
                    ...


            class Sensor(metaclass=abc.ABCMeta):
                pass


            class Mode(Enum):
                """Operating modes."""
                SAFE = auto()
                NOMINAL = 2
                _ignore_ = []

                def label(self) -> str:
                    return self.name.lower()


            class Repo(Generic[T]):
                items: dict[str, T]

                @overload
                def get(self, key: str) -> T: ...
                @overload
                def get(self, key: int) -> T: ...
                def get(self, key):
                    return self.items[key]


            class Base:
                def run(self): pass


            class Rover(Base, Device):
                class Arm:
                    joints: int = 6

                arm: "Rover.Arm"
                parts: Union[list[Device], None]

                def __init__(self):
                    self.mode: Mode = Mode.SAFE

                def reset(self) -> None:
                    from .power.battery import Battery
                    b = Battery()
            """"),
        new("src/lander/app.py", """
            import lander.core
            import lander.power.cell as cellmod
            from lander import Battery
            from lander.core import (Rover,
                                     Mode as M)

            class Mission:
                battery: Battery
                rover: Rover
                mode: M
                cell: cellmod.Cell
                sensor: lander.core.Sensor

                def plan(self, steps, budget: float = 1.0):
                    for s in steps:
                        lander.core.Repo()
            """),
        new("scripts/tool.py", "class Tool:\n    def run(self, x: int) -> int:\n        return x\n"),
        new(".venv/lib/site-packages/pkg/mod.py", "class Hidden: pass"),
    ];

    [Fact]
    public void Modules_become_packages_and_classes_get_their_kinds()
    {
        var m = new PythonCodeReader().Read(Lander);
        Assert.Equal("python", m.Language);
        Assert.True(m.Warnings.Count == 0, string.Join("\n", m.Warnings));
        Assert.Equal(
            new[]
            {
                "lander.app.Mission", "lander.core.Base", "lander.core.Device", "lander.core.Mode", "lander.core.PowerSource", "lander.core.Repo",
                "lander.core.Rover", "lander.core.Rover.Arm", "lander.core.Sensor", "lander.power.battery.Battery", "lander.power.cell.Cell",
                "lander.power.cell.Chemistry", "lander.power.cell.Holder", "scripts.tool.Tool",
            }.Order(StringComparer.Ordinal),
            m.Types.Select(t => t.Symbol));

        Assert.Equal(CodeTypeKind.Interface, Type(m, "lander.core.PowerSource").Kind);   // Protocol
        Assert.Equal(CodeTypeKind.Interface, Type(m, "lander.core.Device").Kind);        // ABC
        Assert.Equal(CodeTypeKind.Interface, Type(m, "lander.core.Sensor").Kind);        // metaclass=ABCMeta
        var mode = Type(m, "lander.core.Mode");
        Assert.Equal((CodeTypeKind.Enum, "Operating modes."), (mode.Kind, mode.Doc));
        Assert.Equal(["SAFE", "NOMINAL", "label"], mode.Members.Select(x => x.Name));
        Assert.Equal("Repo[T]", Type(m, "lander.core.Repo").Name);
        Assert.Equal("lander.core", Type(m, "lander.core.Rover").Namespace);
        Assert.Equal("lander.power.battery", Type(m, "lander.power.battery.Battery").Namespace);
    }

    [Fact]
    public void Dataclass_fields_methods_properties_and_parameters()
    {
        var m = new PythonCodeReader().Read(Lander);
        var battery = Type(m, "lander.power.battery.Battery");
        Assert.Equal(("Stores energy for the night.", 11), (battery.Doc, battery.Line));
        Assert.Equal(["lander.core.PowerSource"], battery.Interfaces);   // a Protocol base is realized
        Assert.Empty(battery.BaseTypes);
        Assert.Equal(["cells", "spare", "mode", "MAX_CELLS", "_serial", "__secret", "output", "charge", "voltage", "nominal", "empty"], battery.Members.Select(x => x.Name));
        var cells = Member(battery, "cells");
        Assert.Equal(("list[cells.Cell]", "lander.power.cell.Cell", true), (cells.TypeName, cells.TypeSymbol, cells.IsCollection));
        var spare = Member(battery, "spare");
        Assert.Equal(("lander.power.cell.Cell", true), (spare.TypeSymbol, spare.IsOptional));   // Optional["cells.Cell"]
        Assert.Equal("lander.core.Mode", Member(battery, "mode").TypeSymbol);
        Assert.True(Member(battery, "MAX_CELLS").IsStatic);
        Assert.Equal(("protected", "private"), (Member(battery, "_serial").Visibility, Member(battery, "__secret").Visibility));

        var output = Member(battery, "output");
        Assert.Equal((CodeMemberKind.Method, "float", "Present output in watts."), (output.Kind, output.TypeName, output.Doc));
        Assert.Empty(output.Parameters);
        var charge = Member(battery, "charge");
        Assert.Equal([("watts", "float"), ("until", "\"datetime | None\""), ("**opts", "")], charge.Parameters.Select(p => (p.Name, p.TypeName)));
        Assert.Equal((CodeMemberKind.Property, "float"), (Member(battery, "voltage").Kind, Member(battery, "voltage").TypeName));
        var nominal = Member(battery, "nominal");
        Assert.True(nominal.IsStatic);
        Assert.Equal(["cell_count"], nominal.Parameters.Select(p => p.Name));   // no self to drop
        Assert.Equal("lander.power.battery.Battery", nominal.TypeSymbol);
        Assert.True(Member(battery, "empty").IsStatic);
        Assert.Empty(Member(battery, "empty").Parameters);

        var cell = Type(m, "lander.power.cell.Cell");
        Assert.Equal(["volts", "chemistry", "history", "count", "label", "_parent"], cell.Members.Select(x => x.Name));
        Assert.Equal("lander.power.cell.Chemistry", Member(cell, "chemistry").TypeSymbol);   // from the parameter's hint
        Assert.Equal(("list[float]", false), (Member(cell, "history").TypeName, Member(cell, "history").IsCollection));
        Assert.Equal("int", Member(cell, "count").TypeName);
        Assert.Equal("lander.power.cell.Holder", Member(cell, "_parent").TypeSymbol);   // Holder() call
        Assert.Equal("", Member(cell, "label").TypeName);
    }

    [Fact]
    public void Overloads_abstract_methods_nested_classes_and_imports()
    {
        var m = new PythonCodeReader().Read(Lander);
        var repo = Type(m, "lander.core.Repo");
        var gets = repo.Members.Where(x => x.Name == "get").ToList();
        Assert.Equal(["str", "int"], gets.Select(g => g.Parameters.Single().TypeName));
        Assert.All(gets, g => Assert.Null(g.TypeSymbol));   // T is a type parameter

        var device = Type(m, "lander.core.Device");
        Assert.True(Member(device, "reset").IsAbstract);
        var rover = Type(m, "lander.core.Rover");
        Assert.Equal(["lander.core.Base"], rover.BaseTypes);
        Assert.Equal(["lander.core.Device"], rover.Interfaces);
        Assert.Equal("lander.core.Rover.Arm", Member(rover, "arm").TypeSymbol);
        var parts = Member(rover, "parts");
        Assert.Equal(("lander.core.Device", true), (parts.TypeSymbol, parts.IsCollection));
        Assert.Equal("lander.core.Mode", Member(rover, "mode").TypeSymbol);
        Assert.Equal(["lander.power.battery.Battery"], rover.Dependencies);   // a function-local import

        var mission = Type(m, "lander.app.Mission");
        Assert.Equal("lander.power.battery.Battery", Member(mission, "battery").TypeSymbol);   // re-exported by lander/__init__.py
        Assert.Equal("lander.core.Rover", Member(mission, "rover").TypeSymbol);
        Assert.Equal("lander.core.Mode", Member(mission, "mode").TypeSymbol);   // "as" alias
        Assert.Equal("lander.power.cell.Cell", Member(mission, "cell").TypeSymbol);   // module alias
        Assert.Equal("lander.core.Sensor", Member(mission, "sensor").TypeSymbol);   // dotted module path
        var plan = Member(mission, "plan");
        Assert.Equal([("steps", ""), ("budget", "float")], plan.Parameters.Select(p => (p.Name, p.TypeName)));
        Assert.Equal(["lander.core.Repo"], mission.Dependencies);
    }

    [Fact]
    public void Skips_virtual_environments()
    {
        var r = new PythonCodeReader();
        Assert.True(r.Handles("pkg/mod.py"));
        Assert.True(r.Handles("pkg/stubs.pyi"));
        Assert.False(r.Handles(".venv/lib/site-packages/x.py"));
        Assert.False(r.Handles("pkg/__pycache__/mod.py"));
        Assert.False(r.Handles("pkg/mod.pyc"));
        Assert.DoesNotContain(r.Read(Lander).Types, t => t.Name == "Hidden");
    }

    [Fact]
    public void Broken_syntax_warns_and_keeps_the_rest()
    {
        var m = Read(
            ("pkg/__init__.py", ""),
            ("pkg/broken.py", "class Broken(Base:\n    x: int = (1,\n\nclass Fine:\n    b: 'Broken'\n    def m(self, a: int:\n        pass\n    def ok(self) -> int:\n        return 1\nclass\n\"\"\"unterminated"),
            ("pkg/junk.py", "))) def ( class : @ \n\t\tlambda: ]]] \\\n"));
        var fine = Type(m, "pkg.broken.Fine");
        Assert.Equal("pkg.broken.Broken", Member(fine, "b").TypeSymbol);
        Assert.Contains(fine.Members, x => x.Name == "ok");
        Assert.Contains(m.Types, t => t.Symbol == "pkg.broken.Broken");
        Assert.Contains(m.Warnings, w => w.StartsWith("pkg/broken.py:") && w.Contains("could not be parsed"));
    }

    [Fact]
    public void Output_is_deterministic_regardless_of_file_order()
    {
        var a = new PythonCodeReader().Read(Lander);
        var b = new PythonCodeReader().Read(Lander.Reverse().ToList());
        Assert.Equal(CodeSyncTestText.Describe(a), CodeSyncTestText.Describe(b));
    }

    [Fact]
    public void Syncing_python_builds_the_model_and_a_second_sync_changes_nothing()
    {
        var m = new ModelRepository("Lander");
        var options = new CodeSyncOptions { RepoId = "py", Commit = "c1" };
        var first = CodeModelMapper.Apply(m, new PythonCodeReader().Read(Lander), options);
        Assert.True(first.Warnings.Count == 0, string.Join("\n", first.Warnings));
        Element S(string symbol) => m.AllElements.Single(e => e.Get(CodeFieldKeys.Symbol) == symbol);
        Assert.Equal(ElementKind.Package, S("ns:lander.power.battery").Kind);
        Assert.Equal(ElementKind.InterfaceBlock, S("lander.core.PowerSource").Kind);
        Assert.Equal("(watts : float, until : \"datetime | None\", **opts) : bool", S("lander.power.battery.Battery.charge(float,\"datetime|None\",?)").Get(FieldKeys.Signature));
        Assert.Equal("(key : str) : T", S("lander.core.Repo.get(str)").Get(FieldKeys.Signature));
        Assert.Equal("(key : int) : T", S("lander.core.Repo.get(int)").Get(FieldKeys.Signature));
        Assert.Null(S("lander.core.Device.reset()").Get(FieldKeys.Signature) is { Length: > 0 } s ? s : null);   // "-> None" is no return
        Assert.Equal("0..1", S("lander.power.battery.Battery.spare").Multiplicity);
        Assert.Equal("*", S("lander.power.battery.Battery.cells").Multiplicity);
        Assert.Equal(ElementKind.Realization, S("real:lander.power.battery.Battery->lander.core.PowerSource").Kind);
        Assert.Equal(ElementKind.Generalization, S("gen:lander.core.Rover->lander.core.Base").Kind);

        var again = CodeModelMapper.Apply(m, new PythonCodeReader().Read(Lander.Reverse().ToList()), options with { Commit = "c2" });
        Assert.True(again.IsEmpty);
    }
}
