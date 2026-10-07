#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -lt 2 ] || [ "$#" -gt 5 ]; then
  echo "Usage: $0 <native-library> <new-output-directory> [framework] [configuration] [default|minimal]" >&2
  exit 1
fi
library="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
output="$(mkdir -p "$2" && cd "$2" && pwd)"
framework="${3:-net10.0}"
configuration="${4:-Release}"
mode="${5:-default}"
filter="FullyQualifiedName~ComponentFiberStressTests"
case "$mode" in
  default) ;;
  minimal) filter="$filter&DisplayName!~guest-gc" ;;
  *) echo "Expected default or minimal native features." >&2; exit 1 ;;
esac
case "$framework/$configuration" in
  net9.0/Debug|net9.0/Release|net10.0/Debug|net10.0/Release) ;;
  *) echo "Expected net9.0/net10.0 and Debug/Release." >&2; exit 1 ;;
esac
case "$(uname -s)" in
  Linux) library_name=libwasmtime.so ;;
  Darwin) library_name=libwasmtime.dylib ;;
  *) echo "This experiment runner supports Linux and macOS only." >&2; exit 1 ;;
esac
if [ "$(basename "$library")" != "$library_name" ] || [ ! -f "$library" ]; then
  echo "Expected an existing $library_name for this platform." >&2
  exit 1
fi
if [ -e "$output/runner" ]; then
  echo "Use a fresh output directory; runner already exists." >&2
  exit 1
fi
repo="$(cd "$(dirname "$0")/../../.." && pwd)"
cd "$repo"
dotnet build tests/Wasmtime.Tests.csproj -c "$configuration" \
  -p:TestTargetFramework="$framework" -p:NuGetAudit=false \
  -p:WasmtimeNativeLibrary="$library"
mkdir "$output/runner"
cp -R "tests/bin/$configuration/$framework/." "$output/runner/"
cp "$library" "$output/runner/FiberProbe/$library_name"
export WASMTIME_THREAD_FIBER_EXPERIMENT=1
export WASMTIME_FIBER_DUMP_DIRECTORY="$output/dumps"
dotnet vstest "$output/runner/Wasmtime.Tests.dll" \
  "--TestCaseFilter:$filter" \
  '--Logger:trx;LogFileName=thread-backed.trx' "--ResultsDirectory:$output"
