# The Signal — Interactive Avatar Prototype

Four parallel .NET prototypes for an interactive, voice-driven human avatar, so the
Azure, Anam, fully-local, and Atlas approaches can be compared side by side before committing to one.

## `src/TheSignal.Server` — Azure AI Foundry + Speech stack

Built on:

- **Azure AI Foundry** — hosts the LLM (chat) used to generate the avatar's responses.
- **Azure AI Speech: Speech-to-Text** — transcribes what the player says (browser mic).
- **Azure AI Speech: Text-to-Speech Avatar (real-time, prebuilt)** — renders a talking video avatar and speaks the LLM's reply.

This is a throwaway/spike prototype to validate feasibility and rough cost before investing in a real party-game build.

## `src/TheSignal.Anam.Server` — Anam turnkey stack

Built on **Anam** (anam.ai), using a **custom avatar** created in Anam Lab. Unlike the
Azure prototype, Anam's "turnkey" mode runs the entire pipeline — speech-to-text,
LLM, text-to-speech, and avatar rendering — on Anam's side. The server only mints a
short-lived session token; the browser client is just the Anam JS SDK streaming to
a `<video>` element. See [Setup](#setup-anam) below.

## `src/TheSignal.Local.Server` — fully local, no cloud services

Built entirely on open-source tools that run on your own machine — no API keys, no network calls:

- **[Ollama](https://ollama.com)** — runs the LLM locally (e.g. `llama3.2`) and serves an HTTP
  chat API on `localhost:11434`.
- **[Piper](https://github.com/rhasspy/piper)** — offline, CPU-friendly neural text-to-speech.
- **[Rhubarb Lip Sync](https://github.com/DanielSWolf/rhubarb-lip-sync)** — analyzes the
  synthesized audio and produces mouth-shape ("viseme") timing, offline.
- **[whisper.cpp](https://github.com/ggml-org/whisper.cpp)** + **ffmpeg** — transcribes recorded
  mic audio locally.

The avatar itself is a small procedurally-drawn face on an HTML `<canvas>` (no photorealistic
video) whose mouth shape is driven by the Rhubarb viseme timeline in sync with audio playback —
this keeps things real-time on CPU-only hardware. See [Setup](#setup-local) below.

## `src/TheSignal.AtlasTest` — Atlas (North Model Labs) realtime avatar quality test

A minimal, throwaway harness for evaluating [Atlas](https://northmodellabs.com)'s realtime
avatar rendering quality — not part of the local/offline effort, this is a cloud service
($7/hr, prorated per second) worth spiking on for comparison. It creates a passthrough
realtime session (Atlas renders video for audio you publish, no built-in STT/LLM/TTS), connects
over WebRTC via LiveKit, and publishes your **microphone directly** so you can just talk and watch
how well the avatar lip-syncs your live voice — the fastest way to judge rendering quality without
wiring up a full chat pipeline. See [Setup](#setup-atlas-test) below.

## Architecture (Azure prototype)

```
Browser (wwwroot/index.html + app.js)
  ├─ mic audio ──────────────► Azure Speech STT (via Speech SDK JS, token from server)
  ├─ WebRTC video/audio ◄────► Azure Speech real-time Avatar (ICE info from server)
  └─ HTTP ───────────────────► ASP.NET Core server (this repo)
                                   ├─ /api/speech/token   → mints short-lived Speech token
                                   ├─ /api/avatar/ice     → fetches WebRTC relay/ICE info
                                   ├─ /api/avatar/config  → avatar character/style/voice
                                   └─ /api/chat           → calls Azure AI Foundry chat completions
```

No Azure keys are ever sent to the browser — the server mints short-lived tokens and proxies the
chat call, so your Speech key and Foundry API key stay server-side.

## Prerequisites

1. **.NET 10 SDK** (already used to scaffold this project).
2. **Azure AI Speech resource** (Standard S0 tier — required for avatar access), in a region that
   supports [text to speech avatar](https://learn.microsoft.com/azure/ai-services/speech-service/text-to-speech-avatar/regions)
   (e.g. `westus2`, `westeurope`, `southeastasia`).
3. **Azure AI Foundry project** with a chat model deployed (e.g. `gpt-4o-mini`) — you said you
   already have this, you just need the deployment endpoint, deployment name, and API key.
4. A browser with mic access (Chrome/Edge recommended) — WebRTC + mic permissions are required.

## Configure secrets (local dev)

You can configure secrets either via `dotnet user-secrets` or via environment variables loaded
from a `.env` file — pick one.

### Option A: `.env` file + script

```bash
cp .env.example .env
# edit .env with your real Speech/Foundry values
source ./scripts/load-env.sh   # must be sourced, not executed, so vars export into your shell
dotnet run --project src/TheSignal.Server
```

`.env` is gitignored; `.env.example` is the committed template. ASP.NET Core reads environment
variables automatically and maps double underscores to config sections (e.g. `SPEECH__KEY` →
`Speech:Key`), so no code changes are needed.

### Option B: `dotnet user-secrets`

From `src/TheSignal.Server`, set secrets via `dotnet user-secrets` (already initialized):

```bash
dotnet user-secrets set "Speech:Key" "<your-speech-key>"
dotnet user-secrets set "Speech:Region" "<your-speech-region>"       # e.g. westus2
dotnet user-secrets set "Foundry:Endpoint" "<https://YOUR-RESOURCE.openai.azure.com>"
dotnet user-secrets set "Foundry:Deployment" "<your-chat-deployment-name>"
dotnet user-secrets set "Foundry:ApiKey" "<your-foundry-api-key>"
```

> If your Foundry model is deployed as a **serverless/model-inference endpoint** rather than an
> Azure OpenAI deployment, the chat-completions URL shape is different. Adjust
> `FoundryChatClient.GetReplyAsync` (in `Services/FoundryChatClient.cs`) to call your endpoint's
> `/chat/completions` route directly instead of the `/openai/deployments/{deployment}/...` path.

Optional — tweak the avatar character/voice in `appsettings.json` under `Avatar` (defaults to
`lisa` / `casual-sitting` / `en-US-AvaMultilingualNeural`). See [supported standard avatars](https://learn.microsoft.com/azure/ai-services/speech-service/text-to-speech-avatar/standard-avatars)
for other character/style combos.

## Run it

```bash
cd src/TheSignal.Server
dotnet run
```

Open the printed URL (e.g. `https://localhost:5001`), click **Start Conversation**, grant mic
permission, and talk. The avatar connects over WebRTC, listens via continuous STT, sends your
transcript to the LLM, and speaks the reply.

## Setup: Anam

1. Create an account at [lab.anam.ai](https://lab.anam.ai) and grab an API key from **Settings → API Keys**.
2. Create a custom avatar (and, optionally, a full Persona bundling avatar + voice + LLM + system
   prompt) in Anam Lab.
3. Configure via `.env`/user-secrets — either point at a saved Persona:
   ```bash
   ANAM__APIKEY=your-anam-api-key
   ANAM__PERSONAID=your-persona-id
   ```
   or build an ephemeral persona from individual pieces:
   ```bash
   ANAM__APIKEY=your-anam-api-key
   ANAM__AVATARID=your-custom-avatar-id
   ANAM__AVATARMODEL=cara-4
   ANAM__VOICEID=your-voice-id       # optional, uses avatar's default voice if omitted
   ANAM__LLMID=your-llm-id           # optional, uses Anam's default LLM if omitted
   ```
   `PersonaId` takes priority if both are set. The system prompt for the ephemeral path is
   hardcoded in `Options/AnamOptions.cs` (same party-host persona as the Azure prototype).
4. Run it:
   ```bash
   source ./scripts/load-env.sh
   dotnet run --project src/TheSignal.Anam.Server
   ```
5. Open the printed URL, click **Start Conversation**, grant mic permission. Anam handles STT,
   LLM, and TTS internally — there's no separate Azure Speech or Foundry call in this path.

Anam bills per minute of avatar streaming (with a monthly free-minutes allowance depending on
plan) — see [anam.ai/pricing](https://anam.ai/pricing). Custom avatars require at least the
Starter plan for commercial use.

## Setup: local voice rehearsal (free, no avatar)

Rehearse the full conversation loop — spoken dialog, player speech input, improvise generation,
scene pacing — with free local models, with **zero Anam calls**. This is the **Local voice
(free, bridge)** renderer mode in the director console: Evelyn's finalized lines are spoken by
Piper, scenes advance when the audio ends, and **Hold to Talk** feeds whisper.cpp transcriptions
into the existing player-input path. Anam stays for milestone smoke tests only.

1. Install the local tools:
   - **Piper**: a release binary plus a voice model (`.onnx`) from
     [rhasspy/piper releases](https://github.com/rhasspy/piper/releases)
   - **whisper.cpp**: `whisper-cli` plus a ggml model such as `ggml-base.en.bin` from
     [ggml-org/whisper.cpp](https://github.com/ggml-org/whisper.cpp)
   - **ffmpeg**: `apt install ffmpeg`
   - **Ollama** (optional, for real improvise dialog): [ollama.com](https://ollama.com), then
     `ollama pull llama3.2:3b` (any chat model works; smaller is fine for rehearsal)

   On this machine they're vendored (gitignored) instead of system-installed: Piper + models under
   `vendor/`, the `whisper.cpp` build under `vendor/whisper/`, voices/models under `vendor/models/`.
   The installed `whisper-cli` has no rpath, so `WHISPER_PATH` points at
   [scripts/whisper-cli-local.sh](scripts/whisper-cli-local.sh), which exports
   `LD_LIBRARY_PATH=vendor/whisper/lib` and runs it. `.env` carries the full paths.
2. Run everything with the launcher (preflight-checks the tools, creates the venv
   on first run, starts the bridge, then the game server; Ctrl-C stops both):
   ```bash
   ./scripts/run-local-voice.sh
   ```
   Or run the bridge by hand (Piper TTS + whisper.cpp STT over localhost HTTP):
   ```bash
   python3 -m venv .venv-voice
   . .venv-voice/bin/activate
   pip install fastapi "uvicorn[standard]" python-multipart
   PIPER_MODEL=/path/to/en_US-lessac-medium.onnx \
   WHISPER_MODEL=/path/to/ggml-base.en.bin \
   python3 tools/voice-bridge/service.py
   ```
   Keep it bound to `127.0.0.1` (the default): browsers block plain-http fetches from
   `https://localhost` pages to non-loopback addresses.
3. Add to `.env` (see `.env.example`):
   ```
   LOCAL__BRIDGEENDPOINT=http://127.0.0.1:8002
   OLLAMA__MODEL=llama3.2:3b   # empty keeps the deterministic [IMPROVISE: ...] stub
   PIPER_MODEL=/path/to/en_US-lessac-medium.onnx   # read by the bridge/launcher
   WHISPER_MODEL=/path/to/ggml-base.en.bin         # read by the bridge/launcher
   ```
4. Run the game server, choose **Local voice (free, bridge)** in the Renderer selector, and
   click **Start Renderer**. Lines are spoken in queue order and each scene completes on the
   real speech duration; typed **and** spoken player input both go through the same
   server-authoritative `/player-input` gate. With `OLLAMA__MODEL` set, improvise lines and
   player responses are generated by the local model through the same `IActorDialogGenerator`
   seam — plot knowledge and director intent stay application-owned.

This mode exercises the exact production contracts (`ActorUtterance`, completion-based
relative timing, renderer interface) — completion is driven by end-of-audio, exactly as the
Anam renderer's `endOfSpeech` event works in production.

## Setup: fully local (no cloud)

1. Install the local tools and make sure each is on your `PATH` (or set the full executable path
   in config):
   - **Ollama**: install from [ollama.com](https://ollama.com), then `ollama pull llama3.2` (or
     any other chat model — pick a smaller one like `phi3` or `llama3.2:3b` if you're CPU-only).
   - **Piper**: download a release binary + a voice model (`.onnx` + `.onnx.json`) from the
     [Piper voices repo](https://github.com/rhasspy/piper/releases).
   - **Rhubarb Lip Sync**: download a release binary from
     [DanielSWolf/rhubarb-lip-sync](https://github.com/DanielSWolf/rhubarb-lip-sync/releases).
   - **whisper.cpp**: build/download `whisper-cli` and a `ggml` model (e.g. `ggml-base.en.bin`)
     from [ggml-org/whisper.cpp](https://github.com/ggml-org/whisper.cpp).
   - **ffmpeg**: install via your package manager (`apt install ffmpeg`, `brew install ffmpeg`, etc.) —
     used to convert the browser's recorded audio to the 16kHz mono WAV whisper.cpp expects.
2. Configure paths via `dotnet user-secrets` (from `src/TheSignal.Local.Server`) or `.env`:
   ```bash
   dotnet user-secrets set "Ollama:Model" "llama3.2"
   dotnet user-secrets set "Piper:ExecutablePath" "/path/to/piper"
   dotnet user-secrets set "Piper:ModelPath" "/path/to/en_US-voice.onnx"
   dotnet user-secrets set "Rhubarb:ExecutablePath" "/path/to/rhubarb"
   dotnet user-secrets set "Whisper:ExecutablePath" "/path/to/whisper-cli"
   dotnet user-secrets set "Whisper:ModelPath" "/path/to/ggml-base.en.bin"
   ```
3. Make sure Ollama is running (`ollama serve`, or it's already running as a background service),
   then run:
   ```bash
   dotnet run --project src/TheSignal.Local.Server
   ```
4. Open the printed URL. Use the **Photo** picker to choose a pre-taken photo of a face — it's
   stored in the browser's `localStorage` only (never uploaded anywhere). A vendored, fully
   offline [MediaPipe Face Landmarker](https://ai.google.dev/edge/mediapipe/solutions/vision/face_landmarker)
   (WASM + model bundled under `wwwroot/vendor/mediapipe`) detects the mouth position once, and
   the canvas then animates that region in sync with the Rhubarb viseme timeline. If no photo is
   chosen, or no face is detected, it falls back to a procedural glowing-orb face.
5. Hold **Hold to Talk**, speak, and release. Transcription, chat, TTS, lip-sync analysis, and
   face detection all happen as local calls/subprocesses — nothing leaves your machine.

Trade-offs vs. Anam/Azure: the "avatar" is a real photo with just the mouth region animated (not a
full photorealistic talking-head video model), and push-to-talk instead of continuous listening
since local STT isn't streaming. Response latency depends entirely on your hardware (CPU-only will
be noticeably slower than the cloud prototypes).

### Optional: photorealistic video via MuseTalk (requires an NVIDIA GPU)

If you have a dedicated NVIDIA GPU (8GB+ VRAM), you can swap the animated-photo mouth for an
actual photorealistic talking-head video generated by [MuseTalk 1.5](https://github.com/TMElyralab/MuseTalk)
(open source, Apache-2.0-compatible, no per-use cost — just your own compute). **MuseTalk requires
CUDA and will not run on CPU/integrated graphics**; even on a modest GPU, generation is not
real-time (expect several seconds to a couple minutes per line), so this trades latency for
visual fidelity.

1. Follow MuseTalk's own [installation instructions](https://github.com/TMElyralab/MuseTalk#installation)
   (conda env, PyTorch+CUDA, mmcv/mmpose/mmdet, ffmpeg) and download the v1.5 weights into
   `./models/musetalkV15`, `./models/sd-vae`, and `./models/whisper` as documented there.
2. Copy [tools/musetalk-bridge/service.py](tools/musetalk-bridge/service.py) into the root of that
   MuseTalk checkout, then install the bridge's extra dependencies and run it from there:
   ```bash
   pip install fastapi "uvicorn[standard]" python-multipart
   python service.py   # serves http://127.0.0.1:8001, loads MuseTalk models once at startup
   ```
3. Point `TheSignal.Local.Server` at it:
   ```bash
   dotnet user-secrets set "MuseTalk:Endpoint" "http://127.0.0.1:8001"
   ```
4. Restart `TheSignal.Local.Server` and choose a photo as before — it's now also sent to the
   MuseTalk bridge to prepare a talking-head avatar. Each reply is rendered as an MP4 (audio
   already muxed in) and played in place of the animated-photo canvas.

Leave `MuseTalk:Endpoint` empty (the default) to keep using the CPU-friendly animated-photo path.

## Setup: Atlas test

1. Get an API key from the [Atlas dashboard](https://dashboard.northmodellabs.com/dashboard/keys)
   (you've already done this).
2. Configure it via `dotnet user-secrets` (from `src/TheSignal.AtlasTest`) or `.env`:
   ```bash
   dotnet user-secrets set "Atlas:ApiKey" "<your-atlas-api-key>"
   ```
3. Run it:
   ```bash
   dotnet run --project src/TheSignal.AtlasTest
   ```
4. Open the printed URL, optionally choose a reference photo (otherwise Atlas uses its default
   face), click **Start Session**, grant mic permission, and talk — the avatar should lip-sync
   your live voice over WebRTC. Click **End Session** when done; this is what stops billing
   (sessions are billed per second while active, so don't leave tabs open idle).

This harness intentionally skips STT/LLM/TTS — it's just for judging Atlas's rendering/lip-sync
quality as fast as possible, not a full conversational pipeline. The API key never reaches the
browser; `TheSignal.AtlasTest`'s two endpoints (`POST /api/session`, `DELETE /api/session/{id}`)
proxy to Atlas server-side.

## Known prototype limitations (fix before any real build)

- **Echo/feedback**: the mic can pick up the avatar's own voice from speakers. The client gates
  new turns while the avatar is speaking, but for a real deployment use a headset, push-to-talk,
  or proper echo cancellation.
- **No auth**: anyone who can reach the server can use your Azure keys indirectly. Fine for a
  local prototype; add auth before deploying anywhere shared.
- **No token refresh**: the Speech token lasts ~10 minutes. Long conversations will need a token
  refresh loop.
- **Stateless chat history**: conversation history lives in the browser tab only (lost on
  refresh/stop).

## Cost considerations to validate

- **Text to speech avatar** is billed per minute of rendered video and is the most expensive piece
  by far — check current pricing on the [Speech pricing page](https://azure.microsoft.com/pricing/details/cognitive-services/speech-services/)
  before running this at a party with many guests/long sessions.
- **Speech-to-text** is billed per audio hour.
- **Foundry LLM calls** are billed per token; keeping replies short (this prototype caps at ~200
  tokens and instructs the model to be terse) helps keep both LLM and avatar-speaking-time costs
  down.
- Always call `avatarSynthesizer.close()` (the **Stop** button does this) — idle sessions
  auto-close after 5 minutes, but don't rely on that during testing.
