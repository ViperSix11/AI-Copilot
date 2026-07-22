# Implementation roadmap

Each milestone uses a focused branch and draft PR, deterministic Windows tests,
matching app/native/PBO packaging and an explicit live Arma acceptance gate.
Later ideas are not implementation authority.

## M1 — OpenAI text tool loop

Status: **accepted**.

Repaired the stateless Responses tool loop, surfaced safe provider errors and
preserved the existing bridge protocol.

## M2 — Local world model

Status: **accepted**.

Added provenance, freshness, uncertainty, reset handling, minimized
purpose-specific snapshots and read-only diagnostics.

## M3 — Friendly-force picture

Status: **accepted for read-only scope**.

Added mission/session handshake, own-side groups, units and crewed vehicles,
deltas plus reconciliation, and read-only mission assets/capabilities.

## M4A / Release 0.7 — Voice Position MVP

Status: **accepted live**.

Added 15-second push-to-talk, OpenAI completed-utterance transcription, one
shared typed/spoken turn service, ElevenLabs output, replay, cancellation and
partial-success preservation.

## M5 / Release 0.8 — Contextual Interpreter

Status: **implementation draft; live acceptance pending**.

- export only official `CfgWorlds >> worldName >> Names` entries in strict,
  bounded pages over the existing duplex transport;
- assemble an in-memory, mission/world-scoped gazetteer atomically;
- derive containment, distance, rounded distance, bearing, cardinal direction,
  salience ranking and live/last-known status locally;
- add compact `interpretedLocation` context and bounded
  `find_named_locations` lookup;
- add local style-only response profiles and exact final-text terminators;
- retain one Responses request for an ordinary position question.

Exit: all automated gates pass and live Stratis plus alternate-map acceptance
confirms correct place relations, language/profile behavior, identical visible
and spoken text, one gazetteer collection and no second Responses request.

## Next milestone selection

No follow-on feature is active yet. Select the next smallest live product gap
after 0.8 acceptance, write its specification first and preserve all accepted
boundaries.

## Deferred backlog

The following require new evidence and explicit authorization; they are not
active dependencies:

- broader own-side hostile-knowledge aggregation or proactive contact reports;
- player-report or persistent operational memory;
- empty vehicles and tactical-object perception;
- full static map indexing or a spatial database;
- ACE/CBA integration and deterministic ballistics;
- routes, landing-zone evaluation and support execution;
- always-on/streaming voice, global hotkeys and release hardening.
