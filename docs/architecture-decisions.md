# Architecture Decisions

## Game-session authority

The server-side `GameSession` is authoritative for roles, logical time, scene
progression, context, pending utterances, and transcript history. Browser state
is a projection used to render controls and effects; it must not independently
advance the story once migration to the scene-engine API is complete.

## Browser refresh recovery

The browser will retain the active session ID in `sessionStorage`. A refresh in
the same tab will reload that session from the server. In-memory sessions do not
survive a server restart; durable recovery is intentionally deferred.

## Relative scene timing

An `after` trigger is measured from the prerequisite scene's completion time,
not its start time. Both timestamps are recorded.

## Scene concurrency

Only one scene may be active in a game session. Actor utterances are queued and
must be acknowledged in order. Non-spoken deliveries use the same correlation
contract. A renderer failure marks the active scene failed and clears its queue.

## Role assignment

Assignments belong to a `GameSession`, never a process-global singleton. Normal
play may use unseeded randomness; rehearsal and automated tests can provide a
seed for reproducible assignments.

## Sensitive player data

Full player profiles remain server-side. Browser-facing role assignments use
`PublicPlayerProfile`, which excludes email addresses and phone numbers.

## Player input during rehearsal

Development rehearsal accepts explicitly typed room input only; no microphone
or speech-to-text provider is active. Input is rejected while a scene or actor
utterance is active, so avatar output cannot be mistaken for player input.
Anonymous room input is supported. Player attribution is deferred until player
profiles have stable public identifiers. Production speech-to-text remains an
independent provider decision and is not coupled to avatar rendering.
