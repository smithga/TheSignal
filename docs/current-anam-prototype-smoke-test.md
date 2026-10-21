# Current Anam Prototype Smoke Test

This procedure captures the expected behavior of the current prototype before
the development/rehearsal-mode refactor. Run it against a locally configured
Anam account. Each run consumes Anam avatar minutes while streaming.

## Prerequisites

- Anam credentials are configured through the ignored `.env` file or .NET user
  secrets. No credentials should be committed to tracked configuration.
- At least one player exists in the local `game/players/players.json` file.
- The configured Anam persona or avatar is available.
- A supported browser has microphone permission.

## Start the application

1. Load local environment variables:

   ```bash
   source ./scripts/load-env.sh
   ```

2. Start the server:

   ```bash
   dotnet run --project src/TheSignal.Anam.Server
   ```

3. Open the URL printed by the server.

## Baseline checks

- [ ] The initial screen shows the Signal placeholder and an idle status.
- [ ] Open the director panel and confirm all scene playback controls are
      disabled before an Anam conversation starts.
- [ ] Confirm the scene list contains Acts I through VIII, the paradox, the
      decision, and all three endings.
- [ ] Confirm role assignments appear in the director panel.
- [ ] Click **Assign Roles** and confirm the displayed assignments update.

## Anam session lifecycle

- [ ] Click **Start Conversation**.
- [ ] Confirm the status progresses through connecting to listening.
- [ ] Confirm Evelyn's video replaces the placeholder.
- [ ] Confirm scene and clock controls become enabled.
- [ ] Speak to Evelyn and confirm she hears and responds to the room.
- [ ] Click **Stop** and confirm streaming ends, the placeholder returns, the
      clock resets, and director controls become disabled.

## Scripted dialog

- [ ] Start a conversation and open the director panel.
- [ ] Play one exact dialog line from a scene.
- [ ] Confirm Evelyn speaks the displayed words verbatim.
- [ ] Use a scene with a short direction such as `concerned` or `sad` and
      confirm the line is accepted and performed.
- [ ] Play a complete multi-line scene and confirm its lines play sequentially
      without cutting each other off.
- [ ] Confirm the scene is visually marked as fired after playback completes.

## Scene context and improvisation

- [ ] Click **Set Context (silent)** for a scene.
- [ ] Confirm Evelyn does not speak immediately.
- [ ] Ask a relevant follow-up question and confirm the response reflects the
      selected scene context.
- [ ] If an `(improvise)` line exists in the content, trigger it and confirm
      Evelyn generates her own wording rather than reading the direction.
- [ ] If no `(improvise)` line exists, record this check as not applicable; the
      UI and loader support it, but the current checked-in scenes do not use it.

## Role templates and player details

- [ ] Play the Act I opening line.
- [ ] Confirm `{{role:host.firstName}}` is replaced with the assigned host's
      first name.
- [ ] Confirm unresolved template syntax is not spoken.
- [ ] Confirm email addresses and phone numbers do not appear in browser API
      responses or the director panel.

## Clock and scene triggers

- [ ] Reset and start the game clock.
- [ ] Confirm it advances once per second and can be paused and resumed.
- [ ] Confirm the Act I timestamp scene fires when the clock reaches its
      configured time.
- [ ] For practical testing, temporarily use director controls to exercise
      later scenes rather than waiting through their full configured delays.
- [ ] Confirm an `after` scene becomes eligible relative to its prerequisite
      scene's recorded completion.
- [ ] Confirm manual scenes do not auto-fire.
- [ ] Reset the clock and confirm fired markers and the signal-lost overlay are
      cleared.

## Signal loss and Echo

- [ ] Trigger the interference scene and confirm the signal-lost overlay is
      displayed.
- [ ] Trigger or play a normal scene and confirm the overlay is hidden again.
- [ ] Click an Echo text-message line, if present in the loaded content.
- [ ] Confirm the line is copied rather than spoken by Evelyn.
- [ ] Confirm whole-scene playback skips non-avatar lines.

## Known baseline limitations

- Starting the game and enabling director controls requires an active Anam
  session.
- Anam owns speech recognition, conversational LLM state, TTS, and avatar
  rendering.
- Echo delivery is manual/out-of-band.
- Role assignment and scene progress are not persisted across a server restart.
- Scene execution can overlap when separate timer/manual triggers occur close
  together.
- Speech completion uses an Anam event with a 15-second fallback timeout.
