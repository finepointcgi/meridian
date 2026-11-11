#!/bin/sh
# Builds the Reference FMUs used by the co-simulation tests (BouncingBall, Dahlquist and Feedthrough, FMI 2.0 and 3.0
# Co-Simulation) from the vendored sources in src/, for one platform, into fmus/<version>/<Model>/binaries/<platform>/.
# The modelDescription.xml next to the binaries is copied from src/<Model>/FMI<version>.xml. The tests zip each folder
# into an .fmu at run time.
#
#   ./build.sh                                   # this machine (cc)
#   CC="cc -arch x86_64" FMU_ARCH=x86_64 ./build.sh   # Intel binaries on an Apple Silicon Mac
#   CC=x86_64-linux-gnu-gcc FMU_ARCH=x86_64 ./build.sh # Linux x86-64 from an arm64 Linux (cross compiler)
#
# FMU_OS (Darwin / Linux) and FMU_ARCH (aarch64 / x86_64) default to this machine. Windows is not covered by this script:
# build there with the upstream CMake project (see README.md).
set -eu

here=$(cd "$(dirname "$0")" && pwd)
CC=${CC:-cc}
os=${FMU_OS:-$(uname -s)}
arch=${FMU_ARCH:-$(uname -m)}
case "$arch" in
  arm64|aarch64) arch=aarch64 ;;
  x86_64|amd64) arch=x86_64 ;;
  *) echo "unsupported architecture: $arch" >&2; exit 1 ;;
esac
case "$os" in
  Darwin)
    ext=dylib; libs=""
    fmi3="$arch-darwin"
    if [ "$arch" = x86_64 ]; then fmi2=darwin64; else fmi2=aarch64-darwin; fi ;;
  Linux)
    ext=so; libs="-lm"
    fmi3="$arch-linux"
    if [ "$arch" = x86_64 ]; then fmi2=linux64; else fmi2=aarch64-linux; fi ;;
  *) echo "unsupported OS: $os (use the upstream CMake build)" >&2; exit 1 ;;
esac

for version in 2 3; do
  if [ "$version" = 2 ]; then platform=$fmi2; else platform=$fmi3; fi
  for model in BouncingBall Dahlquist Feedthrough; do
    out="$here/fmus/$version.0/$model"
    mkdir -p "$out/binaries/$platform"
    cp "$here/src/$model/FMI$version.xml" "$out/modelDescription.xml"
    # Same flags as the upstream CMakeLists.txt: FMI_VERSION, DISABLE_PREFIX (plain fmi2… / fmi3… exports), hidden internals.
    # shellcheck disable=SC2086
    $CC -shared -fPIC -O2 -fvisibility=hidden -DFMI_VERSION=$version -DDISABLE_PREFIX \
      -I"$here/src/include" -I"$here/src/$model" \
      "$here/src/src/fmi${version}Functions.c" "$here/src/src/common.c" "$here/src/src/cosimulation.c" "$here/src/$model/model.c" \
      -o "$out/binaries/$platform/$model.$ext" $libs
    echo "built fmus/$version.0/$model/binaries/$platform/$model.$ext"
  done
done
