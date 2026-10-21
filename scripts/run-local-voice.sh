#!/usr/bin/env bash
# Starts the free local voice-rehearsal stack end to end:
#   1. Preflight checks (Piper, whisper.cpp, ffmpeg, models, Ollama).
#   2. Voice bridge (Piper TTS + whisper.cpp STT) on 127.0.0.1:8002.
#   3. TheSignal.Anam.Server with the loaded .env.
# Zero Anam calls. Ctrl-C stops the bridge and the server together.
#
# Usage: ./scripts/run-local-voice.sh
# Configure via .env: PIPER_MODEL, WHISPER_MODEL, OLLAMA__MODEL, ...

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

if [[ ! -f .env ]]; then
  echo "No .env file — copy .env.example to .env and fill in the local-voice keys first." >&2
  exit 1
fi
set -a
# shellcheck disable=SC1090
source .env
set +a

# --- Preflight -----------------------------------------------------------------

missing=()
command -v "${PIPER_PATH:-piper}"       >/dev/null 2>&1 || missing+=("piper       — release binary + a .onnx voice: https://github.com/rhasspy/piper/releases")
command -v "${WHISPER_PATH:-whisper-cli}" >/dev/null 2>&1 || missing+=("whisper-cli — build whisper.cpp: https://github.com/ggml-org/whisper.cpp")
command -v "${FFMPEG_PATH:-ffmpeg}"     >/dev/null 2>&1 || missing+=("ffmpeg      — apt install ffmpeg")
command -v python3                      >/dev/null 2>&1 || missing+=("python3")
if [[ ${#missing[@]} -gt 0 ]]; then
  echo "Missing local tools (set PIPER_PATH/WHISPER_PATH/FFMPEG_PATH in .env for non-PATH installs):" >&2
  printf '  - %s\n' "${missing[@]}" >&2
  exit 1
fi
if [[ -z "${PIPER_MODEL:-}" || ! -f "${PIPER_MODEL:-}" ]]; then
  echo "Set PIPER_MODEL in .env to your Piper .onnx voice model (see .env.example)." >&2
  exit 1
fi
if [[ -z "${WHISPER_MODEL:-}" || ! -f "${WHISPER_MODEL:-}" ]]; then
  echo "Set WHISPER_MODEL in .env to your ggml model, e.g. ggml-base.en.bin (see .env.example)." >&2
  exit 1
fi

# Ollama is optional: empty OLLAMA__MODEL keeps the deterministic improvise stub.
OLLAMA_ENDPOINT="${OLLAMA__ENDPOINT:-http://127.0.0.1:11434}"
if [[ -n "${OLLAMA__MODEL:-}" ]] && ! curl -fsS "$OLLAMA_ENDPOINT/api/version" >/dev/null 2>&1; then
  echo "Ollama is not answering at $OLLAMA_ENDPOINT — run 'ollama serve', or set OLLAMA__MODEL= in .env to use the stub." >&2
  exit 1
fi

# --- Voice bridge --------------------------------------------------------------

VENV="$ROOT/.venv-voice"
if [[ ! -x "$VENV/bin/python" ]]; then
  echo "Creating Python venv at .venv-voice (one-time setup)..."
  python3 -m venv "$VENV"
  "$VENV/bin/pip" install --quiet --disable-pip-version-check fastapi "uvicorn[standard]" python-multipart
fi

BRIDGE_HOST="${VOICE_BRIDGE_HOST:-127.0.0.1}"
BRIDGE_PORT="${VOICE_BRIDGE_PORT:-8002}"
echo "Starting voice bridge on $BRIDGE_HOST:$BRIDGE_PORT (logs above)..."
"$VENV/bin/python" tools/voice-bridge/service.py &
BRIDGE_PID=$!
trap 'kill "$BRIDGE_PID" 2>/dev/null || true' EXIT INT TERM

bridge_ready=no
for _ in {1..40}; do
  if curl -fsS "http://$BRIDGE_HOST:$BRIDGE_PORT/health" >/dev/null 2>&1; then
    bridge_ready=yes
    break
  fi
  sleep 0.5
done
if [[ "$bridge_ready" != yes ]]; then
  echo "Voice bridge did not become healthy on $BRIDGE_HOST:$BRIDGE_PORT — check its output above." >&2
  exit 1
fi
echo "Voice bridge healthy. In the console, pick 'Local voice (free, bridge)' as the Renderer."

# --- Game server (foreground; Ctrl-C stops everything via the trap) -----------

# Run in the foreground (not exec) so the EXIT trap always stops the bridge.
dotnet run --project src/TheSignal.Anam.Server
