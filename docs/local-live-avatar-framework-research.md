# Local Live Avatar Framework Research

Goal: add a **local live avatar renderer** to The Signal as a first-class
alternative to the paid Anam renderer, behind the existing provider-neutral
`ActorUtterance` contract.

This document is research and decision support. It is not an implementation
plan; the implementation plan is a follow-on once the framework is chosen.

## Target hardware (the AI box)

| Component | Spec |
| --- | --- |
| GPU | NVIDIA RTX 4070 Ti — 16 GB VRAM, Ada (sm_89) |
| System RAM | 64 GB DDR4 |
| CUDA | 13.2 |
| OS | Linux |

Practical envelope this gives us:

- 16 GB VRAM is enough to run a talking-head model **plus** a local LLM
  (quantized 7–14B) **plus** a local TTS model concurrently.
- Ada + CUDA 13.2 means PyTorch 2.7+/2.8 wheels, TensorRT 10.x, and
  `flash-attn`/`xformers` are all available.
- 8 GB-class models (MuseTalk, Wav2Lip, GAN head avatars) run with large
  headroom. Diffusion-class models (MuseTalk v1.5, EchoMimicV2,
  DiffTalk) run, but real-time frame rates are not guaranteed.
- 16 GB is **not** enough for photorealistic neural head avatars that keep a
  full 3DMM/Gaussian avatar resident at high resolution (HighSyncNet-class,
  GAN Head Avatar at 512² with heavy super-resolution) at real-time rates.

## Requirements (derived from the existing architecture)

These come from `docs/architecture-decisions.md`,
`docs/development-rehearsal-mode-implementation-checklist.md`, and the
`ActorPerformanceRenderer` contract in
`src/TheSignal.Anam.Server/wwwroot/actor-performance-renderers.js`.

### Hard requirements

1. **Text-in, video-out.** The engine already owns dialog generation. The
   renderer receives a *finalized* `ActorUtterance.Text` and must render it.
   Any framework whose value proposition is "it also runs the LLM" (Realtime
   AI, LiveKit Agents, Anam, Tavus, Synthesia) is disqualified as the avatar
   engine — it can only be a transport.
2. **Deterministic completion signal.** The renderer must report "this
   utterance finished" so `SceneEngine.AcknowledgeUtteranceAsync` can advance
   the scene. Frame-accurate end-of-utterance is required; a 20 s timeout
   fallback is acceptable (the Anam renderer already does this).
3. **Serialized, single active performance.** Only one scene is active;
   utterances are queued in order. No concurrent avatar streams.
4. **Local only.** No cloud API keys, no per-minute billing, no network
   egress for rendering.
5. **Provider-neutral.** No provider vocabulary in the domain layer. The
   framework stays behind the renderer boundary.

### Strong preferences

6. **Live/real-time streaming**, not offline MP4. The current MuseTalk bridge
   produces an MP4 per line with multi-second latency; that is acceptable for
   rehearsal and unacceptable for a party of 14 people watching a TV.
7. **A stable identity for Evelyn.** She is one character across a ~90-minute
   session. Per-session avatar training is acceptable (once, before the game);
   per-utterance training is not.
8. **Delivery-channel fit.** Evelyn is `SpokenAvatar`; Echo is `TextMessage`
   (no avatar at all). The renderer only needs to handle `SpokenAvatar`.
9. **`PerformanceDirection` mapping.** Emotion/pace/intensity cues must map to
   something the framework can act on (expression parameter, emotion embedding,
   prosody control) — and unsupported cues must be harmless, exactly as the
   Anam renderer's cue allowlist behaves.
10. **Python service + HTTP/WebRTC boundary.** The game server is ASP.NET
    Core. The avatar must run as a separate local service, like
    `tools/musetalk-bridge/service.py` today.

## The pipeline split

"Which framework" is really four independent choices. Choosing a single
end-to-end suite (Realtime AI, ChatTTS+X, etc.) couples all four and makes
later swaps expensive. The Signal's architecture argues for keeping them
separate:

```
finalized text (ActorUtterance)
   → [TTS]            → audio
   → [LIPSYNC/AVATAR] → video frames driven by audio
   → [TRANSPORT]      → browser <video>
   → [COMPLETION]     → "utterance N finished"
```

The engine already supplies the last two concerns (utterance IDs, completion
acknowledgement, queueing), so the framework decision is mostly about
**TTS + avatar**, with transport as a secondary decision.

## Candidate frameworks

### Tier A — real-time, production-plausible on a 4070 Ti

| Framework | Type | Real-time? | Identity stability | License | Notes |
| --- | --- | --- | --- | --- | --- |
| **Wav2Lip (+ Wav2Lip-GAN / Wav2Lip-HD variants)** | Audio→lip-sync GAN on a source video | Yes (well under real-time on Ada) | High — driven by a fixed source clip | Research code, non-commercial weights (Sourav-Mukherjee/Wav2Lip) | The classic. Very robust, very fast, mouth-only fidelity. Weakest on head motion and emotional expression. Non-commercial license is the main blocker for a commercial party product. |
| **Geneva / "Wav2Lip-HD" & 4K derivatives** | Same family | Yes | High | Mixed | Better resolution; same licensing ambiguity. |
| **EasyLive / LivePortrait-driven audio face** | Image/Video → motion + audio-driven lips | Yes | High | MIT-family (LivePortrait is MIT) | LivePortrait gives excellent *expression and head motion* from a driving signal; it is not audio-driven by itself. Pairing LivePortrait (expression/head) with an audio-driven lip model is a strong, permissively licensed combination. |
| **Gaze/ultralight talking heads (e.g. Ultralight-Digital-Human, ER-NeRF family)** | Lightweight GAN / NeRF | Yes, very fast | Per-person training required (minutes–hours) | Mostly MIT/Apache | Very low VRAM (fits easily). Quality is noticeably below photoreal; identity training per person is a real operational cost. |
| **InfiniteTalk / FrameChat-class audio-driven diffusion (chunked)** | Diffusion, chunk-streaming | Near-real-time with tuning | High | Check per-repo (many are CC-NC) | Newer audio-driven diffusion with streaming chunking; quality above GAN, latency above GAN. |

### Tier B — high fidelity, not real-time (rehearsal / pre-render)

| Framework | Type | Real-time? | Notes |
| --- | --- | --- | --- |
| **MuseTalk v1.5** | Latent-diffusion inpainting of mouth region | No (seconds per line) | Already integrated as `tools/musetalk-bridge/service.py`. Apache-2.0-compatible usage. Best "known quantity" in this repo. |
| **EchoMimicV2 / DiffTalk / Diffute** | Diffusion talking head / diffusion lip-sync | No | Higher visual quality than GAN; latency and VRAM make them rehearsal-only. |
| **VASA-1 (Microsoft)** | Audio→video diffusion | No (and not open-weights) | Highest quality in the category; not implementable locally. Excluded. |
| **SadTalker / TalkDiffusion** | Image→talking head | No | Good for offline asset generation (e.g. pre-rendered b-roll), not for live dialog. |

### Tier C — neural head avatars (real-time, per-identity, 3D)

| Framework | Type | Real-time? | Notes |
| --- | --- | --- | --- |
| **GaussianTalker / GazeTalker / SyncDreamer-style Gaussian avatars** | 3D Gaussian Splatting head + audio-driven blendshapes | Yes, on Ada | Excellent photoreal + head motion, genuinely real-time. Requires per-identity capture/training (multi-video, minutes to hours). 16 GB VRAM is workable for a single head. Strongest *quality-per-latency* option if we accept a training step. |
| **HighSyncNet** | 3DMM + audio-driven, real-time | Yes | Real-time, audio-driven, expression-aware. Research license; quality below Gaussian family. |
| **ERNeRF / ER-NeRF** | NeRF, audio-driven | Yes | Real-time, good quality, per-identity training, research license. |
| **GAGAvatar / ARTGaussian** | Gaussian avatar, audio/blendshape driven | Yes | Newer, permissive-ish licenses, per-identity training. |

### Tier D — transport / orchestration only (not avatar engines)

| Framework | Role | Notes |
| --- | --- | --- |
| **LiveKit Agents / Realtime AI / Pipecat** | Session transport, STT, turn-taking | Cloud-billed; their avatar layer is third-party. Useful only as a transport pattern (the Atlas spike already proved LiveKit works for us). |
| **Open-source WebRTC media servers (LiveKit server, MediaMTX, Janus)** | Self-hosted video transport | Self-hosted LiveKit server is the most attractive: it gives us the exact same transport shape the Atlas spike used, fully local. |
| **whisper.cpp / faster-whisper** | STT for player speech | Already chosen in the old local spike; independent of avatar choice. |
| **Piper / Kokoro / F5-TTS / ChatTTS / MeloTTS / XTTS-v2** | TTS feeding the avatar | Piper/Kokoro = fast, CPU-friendly, fixed voices. XTTS-v2 / F5-TTS / CosyVoice = voice cloning (needed for a consistent Evelyn voice), higher cost. |

## Evaluation matrix

Scored against the requirements above. 3 = strong fit, 2 = workable, 1 = weak, 0 = disqualifying.

| Framework | Text-in/video-out | Real-time | Evelyn identity | Emotion cues | License | Local | Effort | Total |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Wav2Lip family | 3 | 3 | 3 | 1 | 0 | 3 | 3 | 16 |
| LivePortrait + audio-lipsync combo | 3 | 3 | 3 | 3 | 3 | 3 | 2 | 20 |
| Ultralight / ER-NeRF class | 3 | 3 | 2 | 2 | 2 | 3 | 2 | 17 |
| InfiniteTalk-class diffusion | 3 | 2 | 3 | 2 | 1 | 3 | 2 | 16 |
| MuseTalk v1.5 (existing) | 3 | 1 | 3 | 1 | 3 | 3 | 3 | 17 |
| Gaussian avatar (GaussianTalker/GAGAvatar) | 3 | 3 | 3 | 3 | 2 | 3 | 1 | 18 |
| HighSyncNet | 3 | 3 | 3 | 3 | 1 | 3 | 2 | 18 |
| VASA-1 | 3 | 0 | 3 | 3 | 0 | 0 | 0 | 9 |

## Recommendation

**Primary candidate: a two-stage local pipeline — `LivePortrait` (head motion +
expression) driven by an audio-driven lip-sync stage, streamed over a
self-hosted LiveKit server.**

Why this combination:

- It is the only Tier-A option that scores 3 on *emotion cues*, which matters
  more for The Signal than raw photorealism. Evelyn's performance direction
  (`concerned`, `warm`, `surprised`) is a first-class content feature
  (`PerformanceDirection`), and the Anam renderer already maps a cue allowlist.
  A mouth-only model (Wav2Lip) cannot express those cues.
- MIT-family licensing (LivePortrait) avoids the Wav2Lip non-commercial
  blocker, which is the single biggest risk for a product intended for paying
  parties.
- Identity stability is high: Evelyn is a fixed source video/clip, prepared
  once, with no per-utterance training.
- Real-time on a 4070 Ti is realistic for a 512² stream; 16 GB VRAM leaves
  room for the LLM and TTS on the same box.

**Fallback / rehearsal path: keep MuseTalk v1.5.** It is already integrated,
Apache-compatible, and gives us a known-good offline renderer while the live
path is built. It also serves as the quality bar the live renderer must beat.

**Stretch candidate: a Gaussian head avatar (GaussianTalker / GAGAvatar).**
If we are willing to do a one-time capture-and-train session for Evelyn, this
is the best quality-per-latency option on this hardware and is genuinely
real-time. It is the right choice for the *final production* look; the
LivePortrait combo is the right choice for *fast iteration*.

**Explicitly not recommended:** any end-to-end suite (Realtime AI, LiveKit
Agents' avatar integrations, Tavus, Synthesia). They own the LLM, which
conflicts with the architecture decision that the application owns dialog
generation.

## Integration design sketch

This is the shape the chosen framework must fit; it is deliberately
framework-agnostic.

```
SceneEngine → ActorUtterance (SpokenAvatar)
   ↓  POST /utterances {id, text, direction}
Local Avatar Service (Python, FastAPI)
   ├─ TTS: text → audio (Piper/Kokoro for rehearsal; XTTS-v2/F5-TTS for production voice)
   ├─ Avatar: audio (+ direction → expression/emotion vector) → frames
   └─ Transport: publish frames+audio to self-hosted LiveKit room
   ↓
Browser: <video> via LiveKit JS SDK  (same shape as the Atlas spike)
   ↓
POST /api/game/sessions/{id}/utterances/{utteranceId}/complete
```

Key properties this preserves:

- The engine stays authoritative; the avatar service is stateless per
  utterance except for the prepared identity.
- Completion is driven by the service reporting end-of-audio for a specific
  utterance ID, mapped onto the existing `PerformanceQueue` →
  `acknowledgeUtterance` flow.
- `PerformanceDirection` maps to a framework-specific cue vector with an
  allowlist, mirroring `AnamRenderer.applySupportedDirection`.
- A new `LocalRenderer` class in `actor-performance-renderers.js` implements
  the same `start()/perform()/cancel()/stop()` contract; `createRenderer()`
  gains a `local` mode. No changes to `SceneEngine`, `DialogResolver`,
  `GameSession`, or `ActorUtterance`.
- The paid-services warning becomes a three-way selector: Development (free) /
  Local (free, local GPU) / Anam (paid).

## Open questions to resolve before implementation

1. **Voice.** Does Evelyn need a cloned voice (XTTS-v2 / F5-TTS / CosyVoice)
   for consistency, or is a fixed Piper/Kokoro voice acceptable for rehearsal?
2. **Identity capture.** Is a one-time multi-video capture of the Evelyn
   performer acceptable (unlocks the Gaussian path), or must she be built from
   a single photo/video?
3. **Transport.** Self-hosted LiveKit server (matches the Atlas spike, gives
   WebRTC + stats + resilience) versus a simpler MJPEG/WebSocket frame stream
   for the first prototype?
4. **Latency budget.** What is the acceptable time from "utterance ready" to
   first visible frame? (Target: < 1.5 s; Anam is roughly in this range.)
5. **Co-location.** Does the LLM + TTS + avatar all run on the AI box
   concurrently, and what quantization does that force on the LLM?
6. **License audit.** Confirm the exact license of each chosen component
   (LivePortrait, the audio-lip model, LiveKit server, the TTS voice model)
   against commercial use.

## Suggested next step

Run a **two-week bake-off** on the AI box, scoring against the matrix above:

- **Track 1 (fast, low risk):** Wav2Lip + Piper + WebSocket frame stream.
  Establishes the renderer contract, completion signalling, and latency
  baseline with the least novel work.
- **Track 2 (target quality):** LivePortrait + audio-driven lips + XTTS-v2 +
  self-hosted LiveKit. Establishes the production look and emotion-cue path.

Both tracks land behind the same `LocalRenderer` contract, so the bake-off
result is a renderer swap, not a rewrite.
