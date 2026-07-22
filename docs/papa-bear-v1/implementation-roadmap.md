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

## M5 / Release 0.8 — Unified State Mirror & Interpreter

Status: **implementation draft; live acceptance pending**.

- preserve the accepted Phase A gazetteer, position interpretation, response
  profiles and exact final-text terminators;
- publish `state-snapshot-v2` every four seconds from field-cadence caches;
- mirror selected player, environment, time/astronomy, loadout, own-side force,
  contact, task and marker state into a bounded SQLite current-state repository;
- derive environment, loadout, force and contact summaries locally;
- select bounded German/English question context deterministically;
- add strict typed `query_state` while preserving existing read tools;
- retain one Responses turn and user-initiated behavior only.

Exit: all automated gates pass and the complete Stratis Unified State Mirror
gate confirms locality-safe contacts, bounded storage/context, restart and
mission-reset behavior, profiles, voice regressions and no proactivity.

## Release 0.9 — Proactive state-change detection and radio notifications

Status: **planned; not active**. It may react to accepted state changes only
after 0.8 passes live acceptance and receives its own reviewed specification.

## Deferred backlog

The following require new evidence and explicit authorization; they are not
active dependencies:

- proactive contact, weather or task reports;
- player-report or persistent operational memory;
- empty vehicles and tactical-object perception;
- full static map indexing or a spatial database;
- ACE/CBA integration and advanced ballistics; release 0.8 contains only its
  explicitly bounded deterministic Vanilla-config solver;
- routes, landing-zone evaluation and support execution;
- always-on/streaming voice and audio effects; release 0.8 includes only its
  registered configurable global press-and-hold hotkey.
