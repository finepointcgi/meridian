# FMI import and co-simulation

Meridian imports [FMI](https://fmi-standard.org) 2.0 and 3.0 FMUs as SysML blocks and co-simulates them, together with
ordinary Meridian blocks, along the connectors of an internal block diagram.

> **Security: this is not a sandbox.** Running an FMU executes the native code inside it. Meridian runs every FMU in a
> separate `Meridian.FmuHost` process, so an FMU that crashes or hangs cannot take Meridian down. That process still runs
> as the same operating-system user as Meridian, with the same file system and network access. A malicious FMU can read
> or change anything Meridian can. Only run FMUs you would run as a program on that machine.

## Import (Simulation panel › FMI import)

Choose an `.fmu`, or just its `modelDescription.xml`. Meridian reads the interface and creates a block with the `FMU`
stereotype:

| FMI variable                                    | Becomes                                                              |
|-------------------------------------------------|----------------------------------------------------------------------|
| `input` / `output`                              | proxy port, direction `in` / `out`, with the variable's unit and start value (`fmi.start`) |
| `parameter`, `calculatedParameter`, `local`     | value property, with the start value as its default value            |
| `independent` (time), `der(...)`                | skipped                                                              |

Each element keeps its `fmi.valueReference` and `fmi.causality`. The block records `fmi.version`, `fmi.guid`,
`fmi.modelIdentifier` and `fmi.sha256`. Importing never runs the FMU's code.

When running FMUs is enabled (see below), the `.fmu` file is also saved to the FMU store. The store keeps files by their
SHA-256, in `<DataPath>/fmus` or at `Meridian:Fmi:StorePath`. The block's `fmi.sha256` names its file. A block imported
from a bare `modelDescription.xml`, or on another server, can have its `.fmu` attached later in the Co-simulation tab.
Meridian checks that the attached file has the same GUID as the block.

## Building a system

1. Type parts of a system block with the imported FMU blocks.
2. Add parts typed by ordinary blocks for glue logic, such as gains, sums or unit conversions. Give each part `in` and
   `out` ports. An `out` port is computed in one of two ways:
   - from its **default value**, an expression over the block's input port names, its value properties and `time`
     (for example `k * u` or `u1 - u2`), or
   - from the block's **parametric constraints**: inputs bind to value properties with the same names, and the `out`
     port takes the solved value of the value property with its own name.
3. Connect output ports to input ports in an internal block diagram.

Before a run, the Co-simulation tab checks the wiring:

- each connector must join an output to an input, and each input can have only one driver;
- **units**: connected ports must have the same unit symbol. `m` and `km` count as a mismatch because no conversion is
  done. If only one end has a unit, you get a warning;
- string signals cannot be exchanged;
- ordinary (algebraic) blocks must not feed each other in a loop without an FMU in between.

Inputs with no connector keep a constant value: the FMU start value or the port's default value, otherwise 0. They are
listed with the parameters and can be overridden there.

## Running (Simulation panel › Co-simulation)

- Pick the system: a block, or one of its internal block diagrams.
- Set the start time, end time and communication step.
- Pick the master algorithm:
  - **Gauss-Seidel** (the default) steps the FMUs in data-flow order, so each FMU sees the outputs its predecessors just
    computed.
  - **Jacobi** steps every FMU from the same snapshot of the previous outputs.
- Algebraic blocks are re-evaluated after each FMU step. The step size is fixed, and the last step is shortened to land
  exactly on the end time.
- **Parameters** default to the value properties' default values. You can override them one by one, or fill them from a
  trade-study alternative or variant configuration. Values are matched by parameter name, `part.name` or plain `name`, so
  a "Heavy" alternative block whose `mass` property is 1800 sets every `mass` parameter.
- **Run** shows progress and can be cancelled. Every output of every part is recorded, keeping up to 5,000 samples per
  signal, thinned evenly over the run.
- **Plot**: click output chips to choose which signals are shown. Drag across the chart to zoom into a time range,
  double-click to reset, and hover to read values.
- **CSV** exports the recorded signals, with units in the column headers.
- **Requirement check**: pick an output, optionally a requirement (its text is parsed for a bound, such as "at most 4 m"),
  a comparison and a threshold. The check passes or fails on the whole run (with the worst value and the first violation
  time) or on the final value only. The threshold is drawn on the chart.
- The **FMU log** shows the FMU's logger messages, stdout and warnings.

An FMU can end the run early: FMI 3.0 `terminateSimulation`. An error status or a crash ends the run with a message. The
samples recorded up to that point are kept.

## FMI support

| Feature                           | Status                                                                     |
|-----------------------------------|----------------------------------------------------------------------------|
| FMI 2.0 Co-Simulation             | Supported: Instantiate, SetupExperiment, Enter/ExitInitializationMode, Set/Get Real, Integer, Boolean, String, DoStep, Terminate, FreeInstance, logger |
| FMI 3.0 Co-Simulation             | Supported: InstantiateCoSimulation (no event mode, early return or intermediate update), Set/Get Float32/64, (U)Int8–64, Boolean, String, DoStep, Terminate |
| Model Exchange only FMUs          | Not supported: they need a solver                                          |
| Variable step size, rollback, `Discard` | Not supported: a `Discard` status ends the run; try a smaller step   |
| FMI 3.0 clocks, binaries, arrays  | Not supported                                                              |
| Platforms                         | `binaries/` folders `darwin64` / `aarch64-darwin` / `x86_64-darwin`, `linux64` / `x86_64-linux` / `aarch64-linux`, `win64` / `x86_64-windows` |

The FMI 2.0 logger is variadic: Meridian logs its message text without substituting the `%` arguments.

## Enabling, permissions and isolation

| Setting                              | Default                       | Meaning                                                    |
|--------------------------------------|-------------------------------|------------------------------------------------------------|
| `Meridian:Fmi:Enabled`               | `true` in the desktop app (local mode), `false` on servers | Allow running FMUs at all |
| `Meridian:Fmi:StorePath`             | `<DataPath>/fmus`             | Where uploaded `.fmu` files are kept                       |
| `Meridian:Fmi:HostPath`              | next to Meridian              | `Meridian.FmuHost` executable or `.dll`, also `MERIDIAN_FMUHOST` |
| `Meridian:Fmi:CallTimeoutSeconds`    | `60`                          | Longest single call into an FMU before its host is killed  |

As environment variables, use `Meridian__Fmi__Enabled=true` and so on.

- **Owners only.** Only project owners can run FMUs or attach `.fmu` files. The check runs on the server. Anyone can
  open the tab and see the plan, and anyone can run a system made only of ordinary blocks, which runs no native code.
- **One process per FMU.** Meridian unpacks each FMU into its own temporary folder, rejecting paths that escape the
  folder and archives over 2 GB unpacked. It then starts `Meridian.FmuHost` with that folder as its working directory.
  The host talks to Meridian over stdin/stdout, one JSON request or response per line. Response lines carry a marker, so
  anything the FMU prints itself is passed back as log.
- **Crash and hang handling.** A segfault or abort ends only the host process, and the run reports, for example, "The
  FMU crashed (segmentation fault, SIGSEGV)". A call that takes longer than the timeout gets its host killed. Cancel
  also kills it. Hosts and temporary folders are cleaned up after every run.
- **Reduced environment.** The host does not inherit Meridian's environment variables, so connection strings and API
  keys are not passed on. It gets only `PATH`, the temp and home folders, locale, `DOTNET_ROOT` and the library search
  path.
- **Not a sandbox.** There is no seccomp or AppContainer, no separate user and no file system or network restriction.
  To run untrusted FMUs on a server, run Meridian itself in a disposable container or VM.

On macOS the desktop app signs `Meridian.FmuHost` with the app's entitlements. Without
`disable-library-validation`, the hardened runtime would refuse to load the FMUs' unsigned libraries.

## Packaging

- `build/package.sh` publishes `src/Meridian.FmuHost` self-contained into the desktop app folder, next to `Meridian`.
- The server build gets it through `Meridian.Web`'s project reference. Run it with `dotnet Meridian.Web.dll`, and the host
  starts as `dotnet Meridian.FmuHost.dll`.

## Tests

`tests/Meridian.Tests/Fmi` builds real FMUs with the system C compiler during the test run: `cc`, `clang`, `gcc` or
`$CC`. The FMUs are an FMI 2.0 first-order lag, a gain, one that segfaults and one that hangs, plus an FMI 3.0 lag. The
tests check:

- results against the analytic solution;
- two FMUs in a feedback loop, under Gauss-Seidel and Jacobi;
- an FMU driving a parametric block;
- crash isolation, the timeout and cancel;
- unit mismatch and wiring errors;
- zip-slip and missing platform binaries.

Without a C compiler (and on Windows), the runtime tests are skipped.

The FMI headers in `tests/Meridian.Tests/Fmi/native/fmi2` and `fmi3` are copies of the Modelica Association's FMI 2.0.4
and 3.0.2 headers, released under the BSD 2-Clause license. The license text is next to each set.
