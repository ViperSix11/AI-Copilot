# Product vision

## Mission

Papa Bear is a believable local radio assistant for Arma 3. Arma supplies
perspective-bound facts, local deterministic services retain and relate them,
OpenAI selects bounded relevant context and produces natural language, and
ElevenLabs optionally speaks the completed visible answer.

## Current 0.9.1 experience

Papa Bear accepts typed, push-to-talk and opt-in voice-activated input through
one assistant path. The current product combines:

1. a four-second, section-sampled State Mirror of current Arma information;
2. a short developing picture for contact changes;
3. mission-scoped contact history, player messages, structured reports,
   corrections and lore;
4. an in-memory official named-location gazetteer and locally authorized
   mission references;
5. a hierarchical context catalogue from which OpenAI requests only the
   information groups needed for the current turn;
6. deterministic local spatial language and a provider-boundary formatter that
   turns selected records into small English facts.

The complete State Mirror, database rows, raw telemetry and gazetteer are not
sent to OpenAI. The model receives a minimal seed and only the bounded,
privacy-projected facts returned by validated local tools.

## Product principles

- Arma is authoritative for game state and perspective-bound knowledge.
- Hidden opposing-side truth is never used to answer the player.
- Numerical and spatial relationships are computed locally, not estimated by a
  language model.
- Multiple observations, current/last-known state, age, uncertainty and
  reporting source remain distinguishable.
- Canonical player coordinates remain local and are excluded from ordinary
  model context.
- Typed and spoken turns use the same reasoning, memory and dialogue path.
- A completed transcript or text answer survives TTS and playback failure.
- Model-selected tools are typed, locally validated and bounded.
- Every broader capability requires a focused specification and live gate.

## Current non-goals

- no unrestricted server-world export or hidden hostile ground truth;
- no arbitrary SQF, C++, SQL, PowerShell, shell or operating-system execution;
- no complete static-map, building, road, vegetation, terrain-object or
  runtime-object index;
- no unseen empty-object perception or mission-wide object enumeration;
- no ACE integration or ballistic/firing-solution calculations;
- no route generation, waypoint assignment, landing-zone execution, support
  execution or autonomous game actions;
- no wake word, streaming STT/TTS, barge-in or radio audio effects;
- no anti-cheat bypass, hidden extension loading, installer, automatic updater
  or production signing.

Mission-declared assets and capabilities remain typed, read-only information.
They do not authorize execution.

## Current verification boundary

The 0.9.1 source baseline is integrated on `main` and its integration commit
passed the Windows build workflow. The deterministic desktop suite contains 320
passing tests at the 2026-07-23 documentation audit. Full live 0.9.1 Arma
acceptance is still required, and no Git tag or published GitHub Release exists.

This is a description of the current product, not a future roadmap. See
[`current-status.md`](current-status.md).
