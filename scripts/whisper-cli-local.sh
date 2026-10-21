#!/usr/bin/env bash
# Runs the vendored whisper.cpp CLI (vendor/whisper/bin) with its vendored
# shared libraries (vendor/whisper/lib) — the installed binary has no rpath.
# Point WHISPER_PATH in .env at this script.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export LD_LIBRARY_PATH="$ROOT/vendor/whisper/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
exec "$ROOT/vendor/whisper/bin/whisper-cli" "$@"
