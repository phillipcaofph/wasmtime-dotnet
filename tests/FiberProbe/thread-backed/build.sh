#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -lt 1 ] || [ "$#" -gt 2 ]; then
  echo "Usage: $0 <fresh-wasmtime-source> [default|minimal]" >&2
  exit 1
fi
mode="${2:-default}"
case "$mode" in
  default|minimal) ;;
  *) echo "Expected default or minimal native features." >&2; exit 1 ;;
esac
macos_linker="${WASMTIME_THREAD_FIBER_MACOS_LINKER:-}"
if [ -n "$macos_linker" ]; then
  if [ "$(uname -s)" != Darwin ] || [ ! -x "$macos_linker" ]; then
    echo "WASMTIME_THREAD_FIBER_MACOS_LINKER requires macOS and an executable linker path." >&2
    exit 1
  fi
fi
source_dir="$(cd "$1" && pwd)"
script_dir="$(cd "$(dirname "$0")" && pwd)"
cd "$source_dir"
if ! grep -q '^version = "48.0.2"$' Cargo.toml; then
  echo "This patch is pinned to Wasmtime 48.0.2." >&2
  exit 1
fi
patch -p1 --forward < "$script_dir/wasmtime-v48.0.2.patch"
export RUSTFLAGS="${RUSTFLAGS:-} --cfg wasmtime_thread_fibers --check-cfg=cfg(wasmtime_thread_fibers)"
args=(--locked --release -p wasmtime-c-api)
if [ "$mode" = minimal ]; then
  args+=(--no-default-features --features cranelift,wat,component-model-async)
fi
if [ -n "$macos_linker" ]; then
  cargo rustc "${args[@]}" -- \
    -C "link-arg=-fuse-ld=$macos_linker"
else
  cargo build "${args[@]}"
fi
