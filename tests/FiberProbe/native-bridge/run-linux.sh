#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -lt 2 ] || [ "$#" -gt 4 ]; then
  echo "Usage: bash $0 <stock-libwasmtime.so> <new-output-directory> [framework] [configuration]" >&2
  exit 1
fi
library="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
if [ ! -f "$library" ] || [ "$(basename "$library")" != libwasmtime.so ]; then
  echo "Expected an existing Linux libwasmtime.so." >&2
  exit 1
fi
mkdir -p "$2"
output="$(cd "$2" && pwd)"
if [ -e "$output/runner" ]; then
  echo "Use a fresh output directory; runner already exists." >&2
  exit 1
fi
framework="${3:-net10.0}"
configuration="${4:-Release}"
case "$framework/$configuration" in
  net9.0/Debug|net9.0/Release|net10.0/Debug|net10.0/Release) ;;
  *) echo "Expected net9.0/net10.0 and Debug/Release." >&2; exit 1 ;;
esac
case "$(uname -m)" in
  arm64|aarch64) platform=linux/arm64 ;;
  x86_64) platform=linux/amd64 ;;
  *) echo "Expected Arm64 or x64 host." >&2; exit 1 ;;
esac
platform="${WASMTIME_BRIDGE_DOCKER_PLATFORM:-$platform}"
compiler="${WASMTIME_BRIDGE_COMPILER_IMAGE:-rust:1-bookworm}"
sdk="${WASMTIME_BRIDGE_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
repo="$(cd "$(dirname "$0")/../../.." && pwd)"
restore_args=()
if [ -n "${WASMTIME_BRIDGE_RESTORE_CONFIG:-}" ]; then
  restore_args+=(-v "$WASMTIME_BRIDGE_RESTORE_CONFIG:/restore.config:ro" -e RestoreConfigFile=/restore.config)
fi
docker run --rm --platform "$platform" \
  -v "$repo/src/native/callback-bridge:/source:ro" -v "$output:/output" \
  "$compiler" cc -std=c11 -shared -fPIC -O2 -pthread -Wall -Wextra -Werror -fvisibility=hidden \
  /source/bridge.c -o /output/libwasmtime_callback_bridge.so
docker run --rm --platform "$platform" \
  -v "$repo:/repo:ro" -v "$library:/native/libwasmtime.so:ro" \
  -v "${NUGET_PACKAGES:-$HOME/.nuget/packages}:/root/.nuget/packages:ro" \
  -v "$output:/output" "${restore_args[@]}" \
  -e TestTargetFramework="$framework" -e Configuration="$configuration" \
  -e WASMTIME_FIBER_ITERATIONS="${WASMTIME_FIBER_ITERATIONS:-20}" \
  -e WASMTIME_FIBER_GC_STRESS="${WASMTIME_FIBER_GC_STRESS:-}" \
  ${WASMTIME_FIBER_TIMEOUT_SECONDS:+-e WASMTIME_FIBER_TIMEOUT_SECONDS="$WASMTIME_FIBER_TIMEOUT_SECONDS"} \
  -e TEST_FILTER="${WASMTIME_BRIDGE_TEST_FILTER:-FullyQualifiedName~NativeFibersPreserveManagedState|FullyQualifiedName~CallbackBridgePreservesValuesAndLifetimes}" \
  "$sdk" bash -lc '
    set -euo pipefail
    mkdir /work
    tar -C /repo --exclude=bin --exclude=obj -cf - src tests Directory.Build.props .config |
      tar -C /work -xf -
    cd /work
    dotnet build tests/Wasmtime.Tests.csproj -c "$Configuration" \
      -p:TestTargetFramework="$TestTargetFramework" -p:NuGetAudit=false \
      -p:NativeCallbackBridgeLibrary=/output/libwasmtime_callback_bridge.so \
      -p:WasmtimeNativeLibrary=/native/libwasmtime.so
    mkdir /output/runner
    cp -R "tests/bin/$Configuration/$TestTargetFramework/." /output/runner/
    export WASMTIME_CALLBACK_BRIDGE_EXPERIMENT=1
    export WASMTIME_FIBER_DUMP_DIRECTORY=/output/dumps
    dotnet vstest /output/runner/Wasmtime.Tests.dll \
      "--TestCaseFilter:$TEST_FILTER" \
      "--Logger:trx;LogFileName=callback-bridge.trx" --ResultsDirectory:/output
  '
