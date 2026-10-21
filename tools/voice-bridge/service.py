#!/usr/bin/env python3
"""The Signal — local voice bridge (free rehearsal path).

A tiny FastAPI service, modelled on tools/musetalk-bridge/service.py, that gives
the director console two fully local capabilities so game flow can be rehearsed
without paying for any cloud avatar service:

    POST /tts   {"text": "..."}      -> audio/wav   (Piper, CPU, subprocess)
    POST /stt   multipart audio file -> {"text": ".."} (whisper.cpp via ffmpeg)
    GET  /health                    -> {"ok": true, "piper": true, "whisper": true}

Bind to 127.0.0.1 ONLY: the console is served over https://localhost, and
browsers block plain-http fetches from https pages except to localhost/127.0.0.1
("potentially trustworthy" origins). A LAN IP would be blocked as mixed content.

Setup (all free, local — see README "Setup: local voice rehearsal"):
    pip install fastapi "uvicorn[standard]" python-multipart
    # Piper: release binary + a .onnx voice from https://github.com/rhasspy/piper/releases
    # whisper.cpp: build whisper-cli, download e.g. ggml-base.en.bin from
    #              https://huggingface.co/ggerganov/whisper.cpp
    # ffmpeg: apt install ffmpeg

Run:
    PIPER_MODEL=/path/to/en_US-lessac-medium.onnx \
    WHISPER_MODEL=/path/to/ggml-base.en.bin \
    python3 service.py
"""

import os
import pathlib
import shutil
import subprocess
import tempfile

import uvicorn
from fastapi import FastAPI, File, HTTPException, UploadFile
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import Response

PIPER = os.environ.get("PIPER_PATH", "piper")
PIPER_MODEL = os.environ.get("PIPER_MODEL", "")
WHISPER_CLI = os.environ.get("WHISPER_PATH", "whisper-cli")
WHISPER_MODEL = os.environ.get("WHISPER_MODEL", "")
FFMPEG = os.environ.get("FFMPEG_PATH", "ffmpeg")
HOST = os.environ.get("VOICE_BRIDGE_HOST", "127.0.0.1")
PORT = int(os.environ.get("VOICE_BRIDGE_PORT", "8002"))
TTS_TIMEOUT_SECONDS = int(os.environ.get("VOICE_BRIDGE_TTS_TIMEOUT", "60"))
STT_TIMEOUT_SECONDS = int(os.environ.get("VOICE_BRIDGE_STT_TIMEOUT", "120"))

app = FastAPI(title="The Signal voice bridge")

# The console origin is https://localhost:PORT; CORS here is loopback-to-loopback
# for rehearsal only. Keep this service bound to 127.0.0.1.
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)


def _require(binary: str, model: str, label: str) -> None:
    if not shutil.which(binary):
        raise HTTPException(503, f"{label} binary '{binary}' not found on PATH (set {label.upper()}_PATH).")
    if not model:
        raise HTTPException(503, f"{label} model is not configured (set {label.upper()}_MODEL).")
    if not pathlib.Path(model).expanduser().is_file():
        raise HTTPException(503, f"{label} model file not found: {model}")


@app.get("/health")
def health():
    return {
        "ok": True,
        "piper": bool(shutil.which(PIPER)) and bool(PIPER_MODEL),
        "whisper": bool(shutil.which(WHISPER_CLI)) and bool(WHISPER_MODEL),
    }


@app.post("/tts")
async def tts(payload: dict):
    text = str(payload.get("text", "")).strip()
    if not text:
        raise HTTPException(400, "text is required")
    _require(PIPER, PIPER_MODEL, "piper")

    with tempfile.TemporaryDirectory(prefix="signal-tts-") as workdir:
        out = pathlib.Path(workdir) / "speech.wav"
        try:
            proc = subprocess.run(
                [PIPER, "--model", str(pathlib.Path(PIPER_MODEL).expanduser()),
                 "--output_file", str(out)],
                input=text.encode("utf-8"),
                capture_output=True,
                timeout=TTS_TIMEOUT_SECONDS,
            )
        except subprocess.TimeoutExpired:
            raise HTTPException(504, "Piper timed out generating speech.") from None
        if proc.returncode != 0 or not out.is_file():
            detail = proc.stderr.decode("utf-8", "replace").strip()
            raise HTTPException(500, f"Piper failed: {detail or 'no audio produced'}")
        return Response(content=out.read_bytes(), media_type="audio/wav")


@app.post("/stt")
async def stt(file: UploadFile = File(...)):
    _require(WHISPER_CLI, WHISPER_MODEL, "whisper")

    with tempfile.TemporaryDirectory(prefix="signal-stt-") as workdir:
        work = pathlib.Path(workdir)
        source = work / "recording"
        wav = work / "speech.wav"
        source.write_bytes(await file.read())

        def run(cmd, timeout):
            try:
                return subprocess.run(cmd, capture_output=True, timeout=timeout)
            except subprocess.TimeoutExpired:
                raise HTTPException(504, "whisper.cpp timed out transcribing.") from None

        convert = run([FFMPEG, "-y", "-i", str(source), "-ar", "16000", "-ac", "1", str(wav)], 30)
        if convert.returncode != 0:
            detail = convert.stderr.decode("utf-8", "replace").strip()
            raise HTTPException(400, f"ffmpeg failed to decode recording: {detail[-300:]}")

        # -np: no print banner, -nt: no timestamps -> plain text on stdout.
        transcribe = run([WHISPER_CLI, "-m", str(pathlib.Path(WHISPER_MODEL).expanduser()),
                          "-f", str(wav), "-np", "-nt"], STT_TIMEOUT_SECONDS)
        if transcribe.returncode != 0:
            detail = transcribe.stderr.decode("utf-8", "replace").strip()
            raise HTTPException(500, f"whisper.cpp failed: {detail[-300:]}")

        return {"text": transcribe.stdout.decode("utf-8", "replace").strip()}


if __name__ == "__main__":
    uvicorn.run(app, host=HOST, port=PORT, log_level="info")
