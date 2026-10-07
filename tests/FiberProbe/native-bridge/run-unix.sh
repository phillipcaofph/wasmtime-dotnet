#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -lt 1 ] || [ "$#" -gt 4 ]; then
  echo "Usage: bash $0 <new-output-directory> [framework] [configuration] [stock-wasmtime-library]" >&2
  exit 1
fi
mkdir -p "$1"
output="$(cd "$1" && pwd)"
framework="${2:-net10.0}"
configuration="${3:-Release}"
case "$framework/$configuration" in
  net9.0/Debug|net9.0/Release|net10.0/Debug|net10.0/Release) ;;
  *) echo "Expected net9.0/net10.0 and Debug/Release." >&2; exit 1 ;;
esac
case "$(uname -s)" in
  Linux) library_name=libwasmtime_callback_bridge.so; wasmtime_name=libwasmtime.so ;;
  Darwin) library_name=libwasmtime_callback_bridge.dylib; wasmtime_name=libwasmtime.dylib ;;
  *) echo "The prototype supports Linux and macOS only." >&2; exit 1 ;;
esac
native_args=()
if [ "$#" -eq 4 ]; then
  library="$(cd "$(dirname "$4")" && pwd)/$(basename "$4")"
  if [ ! -f "$library" ] || [ "$(basename "$library")" != "$wasmtime_name" ]; then
    echo "Expected an existing $wasmtime_name for this platform." >&2
    exit 1
  fi
  native_args+=("-p:WasmtimeNativeLibrary=$library")
fi
if [ -e "$output/runner" ]; then
  echo "Use a fresh output directory; runner already exists." >&2
  exit 1
fi
repo="$(cd "$(dirname "$0")/../../.." && pwd)"
cmake -S "$repo/tests/FiberProbe/native-bridge" -B "$output/native" -DCMAKE_BUILD_TYPE=Release
cmake --build "$output/native"
cd "$repo"
dotnet build tests/Wasmtime.Tests.csproj -c "$configuration" \
  -p:TestTargetFramework="$framework" -p:NuGetAudit=false \
  -p:NativeCallbackBridgeLibrary="$output/native/$library_name" "${native_args[@]}"
mkdir "$output/runner"
cp -R "tests/bin/$configuration/$framework/." "$output/runner/"
unset WASMTIME_THREAD_FIBER_EXPERIMENT
export WASMTIME_CALLBACK_BRIDGE_EXPERIMENT=1
export WASMTIME_FIBER_DUMP_DIRECTORY="$output/dumps"
dotnet vstest "$output/runner/Wasmtime.Tests.dll" \
  '--TestCaseFilter:FullyQualifiedName~NativeFibersPreserveManagedState|FullyQualifiedName~CallbackBridgePreservesValuesAndLifetimes' \
  '--Logger:trx;LogFileName=callback-bridge.trx' "--ResultsDirectory:$output"
