# Reference FMUs for the co-simulation tests

`ReferenceFmuTests` runs three of the Modelica Association's
[Reference FMUs](https://github.com/modelica/Reference-FMUs) through `Meridian.FmuHost`, in FMI 2.0 and 3.0
Co-Simulation, and checks the results against known values and the reference results that ship with them:

| Model        | What it checks                                                                                 |
|--------------|------------------------------------------------------------------------------------------------|
| Dahlquist    | `x' = -k x` with explicit Euler at h = 0.1: `x(n h) = 0.9^n`; parameters; a communication step smaller than the FMU's fixed solver step |
| BouncingBall | state events handled inside the FMU, `h` / `v` against `BouncingBall_out.csv`, units from declared types, a tunable parameter |
| Feedthrough  | inputs of several types passed straight through; string and binary variables are skipped, not fatal |

## Provenance and license

`src/` is a copy of the files these three models need from Reference-FMUs **v0.0.42**
(commit `75d16f87da2fba3c552c99cd683128a9591c1fa3`): `include/` (the FMI 2.0 and 3.0 headers, `model.h`,
`cosimulation.h`), `src/` (`fmi2Functions.c`, `fmi3Functions.c`, `common.c`, `cosimulation.c`) and, per model,
`config.h`, `model.c`, `FMI2.xml`, `FMI3.xml` and the reference result `<Model>_out.csv`. Nothing was changed. The
Reference FMUs are released under the 2-Clause BSD license; the license text is in [`LICENSE.txt`](LICENSE.txt).

## Prebuilt binaries

`fmus/<2.0|3.0>/<Model>/` is the unzipped FMU: `modelDescription.xml` (a copy of `src/<Model>/FMI<n>.xml`) and
`binaries/<platform>/<Model>.<dylib|so>`. The tests zip each folder into an `.fmu` at run time.

| Platform        | FMI 2.0 folder   | FMI 3.0 folder   | Built with                         | Tested here |
|-----------------|------------------|------------------|------------------------------------|-------------|
| macOS arm64     | `aarch64-darwin` | `aarch64-darwin` | Apple clang                        | yes         |
| macOS x86-64    | `darwin64`       | `x86_64-darwin`  | Apple clang `-arch x86_64`         | no          |
| Linux arm64     | `aarch64-linux`  | `aarch64-linux`  | gcc 13 (Ubuntu 24.04 container)    | yes (container) |
| Linux x86-64    | `linux64`        | `x86_64-linux`   | `x86_64-linux-gnu-gcc` 13 (cross)  | no          |

On a platform without a prebuilt binary the tests compile the FMU from `src/` with the system C compiler (`cc`,
`clang`, `gcc` or `$CC`). Without a binary and without a compiler, as on Windows, they are skipped.

## Rebuilding

```sh
cd tests/Meridian.Tests/Fmi/reference
./build.sh                                          # this machine
CC="cc -arch x86_64" FMU_ARCH=x86_64 ./build.sh     # Intel binaries on an Apple Silicon Mac
```

Linux binaries from a Mac (Docker):

```sh
docker run --rm -v "$PWD":/ref -w /ref mcr.microsoft.com/dotnet/sdk:10.0-noble sh -c \
  'apt-get update && apt-get install -y gcc gcc-x86-64-linux-gnu libc6-dev-amd64-cross &&
   ./build.sh && CC=x86_64-linux-gnu-gcc FMU_ARCH=x86_64 ./build.sh'
```

`build.sh` uses the flags of the upstream `CMakeLists.txt` (`-DFMI_VERSION=<n> -DDISABLE_PREFIX -fvisibility=hidden`).
For Windows, build with the upstream project instead (`cmake -DFMI_VERSION=2` / `3`, then copy
`temp/<Model>/binaries/<platform>/<Model>.dll` into `fmus/<n>.0/<Model>/binaries/`).

To update to a newer Reference-FMUs release, copy the same files over `src/`, update the version and commit above,
rebuild every platform and run `dotnet test --filter ReferenceFmuTests`.
