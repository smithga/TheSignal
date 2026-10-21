# Development and Rehearsal Mode Implementation Checklist

## Goal

Make the complete game playable, testable, and repeatable without starting an
Anam session or consuming Anam avatar minutes.

The target flow is:

```text
Scene / Script
    ↓
Game Director / Scene Engine
    ↓
Rules + Game State + LLM
    ↓
Final Actor Utterance
    ↓
Actor Performance Layer
    ├── Development Renderer
    └── Anam Renderer
```

The game engine must not know which performance renderer is active. Anam is a
production rendering provider, not the owner of game rules, scene progression,
dialog generation, or conversation state.

## Definition of done

- [ ] A full game can be run in development mode without an Anam API call or
      session token.
- [ ] Scripted, improvised, and player-responsive dialog can all be exercised
      without Anam.
- [ ] The game engine produces finalized, provider-neutral actor utterances.
- [x] Development and Anam renderers consume the same utterance contract.
- [x] Game state and conversation history are owned by the application.
- [ ] Scene timing and renderer completion are deterministic and testable.
- [ ] Evelyn and Echo can both be rehearsed through their intended delivery
      channels.
- [ ] Production mode preserves the existing Anam avatar experience.
- [ ] Automated tests cover the core game without external services.

## Phase 0: Safety and baseline

- [ ] Rotate the Anam API key currently present in `appsettings.json`.
- [x] Remove the real Anam API key from tracked configuration.
- [x] Configure secrets through environment variables or .NET user secrets.
- [x] Add a safe, empty Anam configuration example for new developers.
- [x] Record the expected behavior of the current prototype before refactoring.
- [x] Capture a manual smoke-test script for:
  - [x] Starting and stopping a session.
  - [x] Assigning roles.
  - [x] Playing an exact scripted line.
  - [x] Sending an improvised direction.
  - [x] Running the game clock.
  - [x] Triggering a relative scene.
  - [x] Triggering the signal-lost effect.
  - [x] Copying an Echo message.
- [ ] Decide whether existing uncommitted/deleted prototype files should be
      retained, restored, or left outside this refactor.

## Phase 1: Provider-neutral domain model

- [x] Introduce a typed `DialogMode` enum for exact and improvised dialog.
- [x] Introduce a provider-neutral `DeliveryChannel` enum, initially including:
  - [x] `SpokenAvatar`
  - [x] `TextMessage`
  - [x] `Overlay`
- [x] Replace actor channels such as `anam-avatar` with narrative delivery
      channels such as `spoken-avatar`.
- [x] Introduce `PerformanceDirection` for optional delivery cues.
- [x] Keep narrative/LLM instructions separate from performance cues.
- [x] Introduce `ActorUtterance` as the renderer boundary, containing at least:
  - [x] Unique utterance ID.
  - [x] Actor ID.
  - [x] Final text.
  - [x] Delivery channel.
  - [x] Optional performance direction.
  - [x] Source scene/line identifiers for diagnostics.
- [x] Ensure domain records contain no Anam SDK, persona, avatar-model, or API
      terminology.
- [x] Decide where provider-specific actor configuration belongs rather than
      keeping `personaId` in the core actor definition.
- [x] Add validation for:
  - [x] Unknown actor IDs.
  - [x] Unknown dialog modes.
  - [x] Unknown trigger types.
  - [x] Missing referenced scenes.
  - [x] Duplicate scene IDs.
  - [x] Invalid role IDs and template fields.
  - [x] Invalid delivery channels.

## Phase 2: Game session and state ownership

- [x] Introduce a `GameSession` model with:
  - [x] Session ID.
  - [x] Lifecycle status.
  - [x] Logical elapsed time and clock status.
  - [x] Role assignments.
  - [x] Scene execution state.
  - [x] Started and completed scene timestamps.
  - [x] Current/pending utterances.
  - [x] Conversation transcript.
  - [x] Current scene context and revealed information.
  - [x] Final player decision/ending, when applicable.
- [x] Replace the singleton global role assignment with session-scoped state.
- [x] Introduce `IGameSessionStore`.
- [x] Implement an in-memory session store for the first version.
- [x] Define create, load, reset, and end session operations.
- [x] Decide whether browser refresh should recover the active session.
- [x] Ensure multiple simultaneous games cannot share roles or progression.
- [x] Define what state is authoritative on the server versus view-only state in
      the browser.
- [x] Keep player PII server-side and expose only required public fields.
- [x] Make role assignment deterministic when a rehearsal seed is supplied.
- [x] Validate fixed player roles against `roles.json`.
- [x] Resolve the current `advocate` versus `believer` role mismatch.

## Phase 3: Scene engine

- [x] Introduce a `SceneEngine` that has no renderer or Anam dependency.
- [x] Define commands handled by the engine, including:
  - [x] Start game.
  - [x] Pause game.
  - [x] Advance logical time.
  - [x] Trigger scene manually.
  - [x] Trigger an individual line.
  - [x] Receive player input.
  - [x] Acknowledge utterance completion.
  - [x] Reset/end game.
- [x] Define engine events/results, including:
  - [x] Scene eligible.
  - [x] Scene started.
  - [x] Context changed.
  - [x] Utterance ready.
  - [x] Non-spoken delivery ready.
  - [x] Visual effect requested.
  - [x] Scene completed.
  - [x] Game state changed.
- [x] Move template resolution out of browser DOM code and into game logic.
- [x] Move trigger evaluation out of `app.js` and into the scene engine.
- [x] Define whether `after + wait` is measured from scene start or completion.
- [x] Record both start and completion time regardless of the chosen rule.
- [x] Serialize scene execution or explicitly define legal concurrency.
- [x] Prevent timer ticks from interleaving incompatible scenes.
- [x] Add cancellation behavior for reset, stop, and renderer failure.
- [x] Treat signal loss as a provider-neutral game/visual event.
- [x] Keep manual-only scenes under director control.
- [x] Use .NET `TimeProvider` so time can be controlled in tests.

## Phase 4: Dialog resolution and LLM ownership

- [x] Introduce a `DialogResolver` that turns scene dialog into finalized
      `ActorUtterance` instances.
- [x] Pass exact scripted lines through without an LLM call.
- [x] Introduce `IActorDialogGenerator` for improvised and conversational lines.
- [x] Define `DialogGenerationRequest`, including:
  - [x] Actor identity and character instructions.
  - [x] Current scene context.
  - [x] Director intent.
  - [x] Relevant game state/reveal constraints.
  - [x] Role/player context allowed for the actor.
  - [x] Conversation history.
- [x] Add a deterministic fake dialog generator for tests and offline rehearsal.
- [ ] Select and implement the production LLM provider independently of Anam.
- [x] Move plot knowledge and character instructions into application-owned
      prompts rather than the Anam persona session.
- [x] Make the application own and record final generated text before rendering.
- [x] Store player input and actor output in the game transcript.
- [x] Prevent hidden player PII from being included in prompts unless explicitly
      required.
- [x] Define prompt/version identifiers so rehearsals can be reproduced.
- [x] Add guardrails that prevent the LLM from revealing future-act knowledge.
- [x] Decide how development mode behaves when no external LLM is configured:
  - [x] Deterministic canned response.
  - [ ] Director types the final response.
  - [x] Optional local model (opt-in Ollama adapter behind the same generator seam).
- [x] Remove the `[DIRECTION — not something a player said: ...]` transport hack
      once director intent is represented structurally.

## Phase 5: Actor performance layer

- [x] Define a browser-side `ActorPerformanceRenderer` contract with:
  - [x] Start/initialize.
  - [x] Perform finalized utterance.
  - [x] Completion result.
  - [x] Cancel current performance.
  - [x] Stop/dispose.
- [x] Ensure the renderer receives no scene rules or raw LLM instructions.
- [x] Add a renderer selection/factory mechanism.
- [x] Keep renderer selection in composition/configuration code, outside the
      scene engine.
- [x] Define error and timeout behavior shared by all renderers.
- [x] Correlate completion with utterance IDs.
- [x] Implement a single performance queue to prevent overlapping speech.
- [x] Decide whether completion means audio ended, animation ended, or both.

### Development renderer

- [x] Implement `DevelopmentRenderer` without any Anam dependency.
- [x] Display actor name, final text, performance direction, and source scene.
- [x] Add a visible transcript/history view.
- [x] Support immediate completion for fast automated/manual testing.
- [x] Support director-controlled completion for pacing rehearsal.
- [ ] Optionally support browser speech synthesis, disabled by default for
      deterministic tests.
- [x] Make spoken-avatar output visually distinct from text-message output.
- [x] Preview signal-lost and other visual events.
- [x] Make development mode usable when Anam configuration is completely absent.

### Anam renderer

- [x] Move all Anam SDK calls out of general game/director code.
- [x] Restrict Anam rendering to finalized utterances using `talk()`.
- [x] Stop using Anam `sendUserMessage()` for game dialog generation.
- [x] Map provider-neutral performance direction to supported Anam cues.
- [x] Keep unsupported cues harmless rather than leaking them into spoken text.
- [x] Wrap Anam speech-completion events behind the renderer contract.
- [x] Retain a bounded timeout as failure recovery, not normal timing.
- [x] Pin the Anam SDK version instead of importing `@latest`.
- [x] Create Anam session tokens only when the Anam renderer is selected.
- [x] Confirm development mode makes no request to `/api/anam/session-token`.
- [x] Decide whether saved Anam persona lookup is still needed once prompts and
      the LLM are application-owned.
- [x] Consolidate provider-specific actor/avatar configuration in one place.

## Phase 6: Player input

- [x] Introduce a provider-neutral player-input path.
- [x] Support typed player input in development mode.
- [ ] Decide on production speech-to-text independently of avatar rendering.
- [x] Define an `IPlayerInputProvider` only if more than one concrete input path
      is implemented.
- [ ] Associate input with a player when known; support anonymous room input.
- [x] Add player input to the application-owned conversation transcript.
- [x] Define listening behavior while an actor is performing.
- [x] Prevent avatar audio from being treated as player speech.
- [ ] Provide director controls to correct or replay mis-transcribed input.
- [x] Verify the complete conversation loop works with the development renderer.

## Phase 7: Echo and non-avatar delivery

- [ ] Preserve Echo as a separate actor with a `TextMessage` delivery channel.
- [ ] Render Echo messages in the development rehearsal UI.
- [ ] Preserve copy-to-clipboard as an interim production delivery option.
- [ ] Represent target player/role as structured delivery metadata.
- [ ] Show the intended recipient to the director without exposing unrelated PII.
- [ ] Decide whether automated SMS/email delivery is in scope for this refactor.
- [ ] If automated delivery is added, introduce it behind a dedicated delivery
      interface rather than the actor renderer.
- [ ] Record delivery attempts and outcomes in the game transcript.

## Phase 8: API and user interface

- [x] Add endpoints for game-session create/get/reset/end operations.
- [ ] Add endpoints or a command endpoint for director actions.
- [ ] Return finalized utterances/game events to the browser.
- [ ] Add a mode selector or configuration for `Development` and `Anam`.
- [ ] Clearly indicate the active renderer and whether paid services are active.
- [ ] Allow director controls in development mode without starting Anam.
- [ ] Separate “Start Game” from “Start Anam Stream.”
- [ ] Preserve scene list, manual line playback, clock controls, role assignment,
      and signal-lost controls.
- [ ] Display scene states such as pending, active, completed, and failed.
- [ ] Display the logical clock from authoritative session state.
- [ ] Add controls for fast-forwarding time in rehearsal mode.
- [ ] Add controls for pausing before/after each utterance.
- [ ] Show generated text before performance when rehearsal approval is enabled.
- [ ] Remove direct game-state mutation and `scene.__el` references from scene
      data objects.
- [ ] Decide whether polling, ordinary command responses, SSE, or SignalR is the
      simplest transport; avoid real-time infrastructure unless required.

## Phase 9: Content and loader cleanup

- [ ] Update actor frontmatter to use provider-neutral delivery channels.
- [ ] Move Anam persona/avatar identifiers into production renderer config.
- [ ] Align `ActorLoaderService` with the actual rich actor document headings.
- [ ] Decide which actor-document sections become model context.
- [ ] Preserve rich scene direction as narrative/model context.
- [ ] Represent short emotional cues separately as performance direction.
- [ ] Validate all scene actor references against loaded actors.
- [ ] Validate all `after` trigger references.
- [ ] Validate role templates when content loads.
- [ ] Add stable IDs to dialog lines if needed for replay and diagnostics.
- [ ] Decide whether Markdown parsing remains deliberately limited or should use
      a YAML/Markdown library.
- [ ] Add a content-validation command or startup report.

## Phase 10: Automated tests

### Loader and content tests

- [ ] Parse legacy scenes.
- [ ] Parse rich scenes.
- [ ] Parse exact and improvised dialog.
- [ ] Normalize role templates.
- [ ] Reject/report invalid content references.
- [ ] Load rich actor context correctly.

### Scene-engine tests

- [ ] Timestamp trigger fires once.
- [ ] Relative trigger uses the documented start/completion rule.
- [ ] Manual scenes never auto-fire.
- [ ] Scene execution is serialized as intended.
- [ ] Pause and resume preserve logical time.
- [ ] Reset clears all transient state.
- [ ] Renderer failure does not corrupt scene state.
- [ ] Cancellation stops pending execution.
- [ ] Two game sessions remain isolated.

### Dialog tests

- [ ] Exact dialog bypasses the LLM.
- [ ] Improvised dialog uses `IActorDialogGenerator`.
- [ ] Final generated text is recorded before rendering.
- [ ] Role templates resolve from the current session.
- [ ] Missing roles produce a clear validation/runtime error.
- [ ] Reveal constraints are included in generation requests.

### Renderer-contract tests

- [ ] Development renderer completes without network access.
- [ ] Development and Anam renderers accept the same utterance shape.
- [ ] Utterance completion is correlated by ID.
- [ ] Timeouts and cancellation have consistent results.
- [ ] The engine behaves identically regardless of selected renderer.

### End-to-end tests

- [ ] Run all scenes with a fake clock, fake LLM, and development renderer.
- [ ] Run a complete decision path for ANSWER.
- [ ] Run a complete decision path for DESTROY.
- [ ] Run a complete decision path for WAIT.
- [ ] Exercise player questions and improvised Evelyn responses.
- [ ] Exercise Echo delivery and signal-loss events.
- [ ] Assert that development mode performs no Anam HTTP/SDK calls.

## Phase 11: Migration and rollout

- [ ] Introduce development mode before removing the existing Anam path.
- [ ] Compare exact scripted playback between old and new paths.
- [ ] Compare emotional cue behavior on the Anam avatar.
- [ ] Conduct a full rehearsal using only development mode.
- [ ] Conduct a short production-renderer smoke test to limit avatar-minute use.
- [ ] Verify stop/reset always ends the Anam stream.
- [ ] Add logging for session, scene, utterance, renderer, and completion IDs.
- [ ] Avoid logging player PII, secrets, or full sensitive prompts by default.
- [ ] Document configuration for development and production modes.
- [ ] Document the rehearsal workflow for directors.
- [ ] Document how to add a scene, actor, delivery channel, and renderer.
- [ ] Remove obsolete direct Anam calls from general UI/game code.
- [ ] Rename the project away from `TheSignal.Anam.Server` if Anam is no longer
      the defining application boundary.
- [ ] Update the README architecture diagram and setup instructions.

## Decisions to make

- [ ] Which LLM provider will own production improvisation and conversation?
- [ ] Which speech-to-text provider will handle production player speech?
- [ ] Should development mode use typed input only, optional browser speech, or
      both?
- [ ] Should session state survive browser refresh and server restart?
- [ ] Does relative scene timing start at scene start or scene completion?
- [ ] Are simultaneous actors/overlapping deliveries ever valid?
- [ ] Should the director approve generated dialog before it is performed?
- [ ] Is automatic SMS/email delivery for Echo part of this implementation?
- [ ] Should role assignments be random in production but seeded in rehearsal?
- [ ] Is the Anam saved persona still useful when its LLM/system prompt no
      longer owns game behavior?

## Suggested implementation order

- [ ] 1. Secure configuration and add baseline smoke tests.
- [ ] 2. Add provider-neutral domain records and content validation.
- [ ] 3. Add `GameSession`, session store, and `SceneEngine` with unit tests.
- [ ] 4. Add finalized utterance generation and a deterministic fake LLM.
- [x] 5. Add the development renderer and complete a no-Anam rehearsal.
- [x] 6. Extract the existing Anam behavior into `AnamRenderer` using only final
      utterances.
- [ ] 7. Move free conversation and player input outside Anam.
- [ ] 8. Complete Echo delivery behavior and UI polish.
- [ ] 9. Run end-to-end rehearsal and production smoke tests.
- [ ] 10. Remove obsolete coupling and update documentation.

## Progress notes

Use this section for implementation discoveries, scope changes, and decisions
that affect later checklist items.

- 2026-09-30: Removed Anam credentials and persona selection from tracked
  `appsettings.json`. Local `.env` remains ignored and is loaded through
  `scripts/load-env.sh` using ASP.NET Core's `ANAM__...` convention.
- 2026-09-30: Captured the pre-refactor manual baseline in
  `docs/current-anam-prototype-smoke-test.md`. The live Anam checks still need
  to be executed and recorded by a configured operator.
- 2026-09-30: Added provider-neutral `DialogMode`, `DeliveryChannel`,
  `PerformanceDirection`, and `ActorUtterance` domain types. Evelyn now uses the
  narrative `spoken-avatar` channel; the loader temporarily accepts the legacy
  `anam-avatar` value for compatibility.
- 2026-09-30: Added application-owned `GameSession` state, a session-store
  abstraction, an initial in-memory implementation, and create/get/reset/end
  HTTP endpoints. The existing browser flow does not consume these sessions
  yet.
- 2026-09-30: Added a renderer-independent `SceneEngine` with explicit scene
  lifecycle, completion-based relative timing, serialized execution, logical
  clock commands, provider-neutral visual events, and HTTP command endpoints.
  Dialog/utterance production and browser migration remain pending.
- 2026-09-30: Removed provider persona IDs from actor content, separated rich
  narrative context from typed performance direction, and added cross-content
  validation for actors, scenes, triggers, roles, templates, and channels.
- 2026-09-30: Replaced global role state with session-owned assignments,
  including seeded rehearsal assignment and fixed-role validation. Architecture
  decisions for authority, refresh recovery, timing, concurrency, and PII are
  recorded in `docs/architecture-decisions.md`.
- 2026-09-30: Added server-side template resolution, deterministic rehearsal
  dialog generation, finalized utterance events, player-input capture,
  individual-line triggering, ordered completion acknowledgement, and renderer
  failure handling. Browser migration remains before Phase 3 is fully closed.
- 2026-09-30: Phase 4 now has a provider-neutral dialog-generation request,
  application-owned plot/actor/scene context, versioned prompt inputs, safe
  public role context, and a deterministic no-network rehearsal generator.
  Azure/Foundry is explicitly out of scope; the production LLM provider remains
  undecided.
- 2026-09-30: Added a queued browser-side actor performance boundary with
  development and Anam renderers. Development mode renders finalized spoken and
  text-message utterances, supports immediate or director-controlled completion,
  previews visual events, and never loads the Anam SDK or requests a token. The
  Anam renderer is lazy-loaded, uses only `talk()` with finalized text, maps a
  small allowlist of performance cues, and correlates speech completion by
  utterance ID. Anam SDK 4.27.1 is pinned. Saved-persona lookup is retained only
  to source rendering IDs; game plot and character prompts are no longer sent to
  Anam. Browser speech synthesis remains intentionally unimplemented because it
  is optional and deterministic rehearsal is the current priority.
- 2026-09-30: Added typed anonymous room input to development rehearsal. The
      server records input, generates an Evelyn response through the existing
      dialog-generator contract, and returns a finalized utterance for the active
      renderer. Input is rejected during scenes or actor performances; production
      speech-to-text and stable player attribution remain open decisions.
- 2026-09-30: Verified the typed conversation loop in a local browser with the
      Development renderer: player input produced a finalized Evelyn utterance,
      completion recorded both transcript entries, and input re-enabled afterward.
      The player-input API also returned 409 while a prior utterance was pending.
- 2026-10-21: Added the free local voice-rehearsal path so the conversation loop
      can be rehearsed with real speech without any Anam calls. A new
      `tools/voice-bridge/service.py` FastAPI bridge (bound to `127.0.0.1:8002`;
      browsers block plain-http fetches from `https://localhost` pages to
      non-loopback origins) serves Piper TTS and whisper.cpp STT as subprocess
      calls. A `LocalVoiceRenderer` joins the renderer factory as the
      **Local voice (free, bridge)** mode: spoken-avatar lines are voiced by
      Piper, and completion fires on end-of-audio — mirroring the Anam
      renderer's `endOfSpeech` pacing through the same utterance-complete
      endpoint. Director push-to-talk (MediaRecorder → bridge `/stt`) feeds
      transcriptions into the existing server-authoritative `/player-input`
      gate, so scene/utterance 409 gating is unchanged. Improvise and
      player-response dialog now has an opt-in `OllamaDialogGenerator` behind
      `IActorDialogGenerator` (empty `Ollama:Model` keeps the deterministic
      stub, so default config makes zero network calls); `.env` keys
      `LOCAL__BRIDGEENDPOINT` and `OLLAMA__MODEL` configure it, and
      `GET /api/local/configuration` reports state to the console. Phase 5's
      optional browser speech-synthesis item stays open — speech arrives via
      the Piper bridge in a dedicated renderer mode instead, keeping
      Development mode text-only and deterministic. Production STT/LLM
      selection (Phase 6) remains undecided; this path is rehearsal-only.
