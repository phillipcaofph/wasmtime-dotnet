#!/usr/bin/env bash
set -euo pipefail

if [ "$(uname -s)" != Linux ]; then
  echo "Use run-unix.sh for the macOS experiment." >&2
  exit 1
fi
exec bash "$(dirname "$0")/run-unix.sh" "$@"
