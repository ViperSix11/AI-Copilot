# Implementation roadmap

Each milestone is developed on a focused branch/PR and must pass automated
Windows CI plus its explicit live Arma acceptance gate. Scope is intentionally
narrow: a functioning vertical product slice takes priority over speculative
breadth.

## M1 — OpenAI text tool loop

Status: **accepted**.

- repair multi-round stateless Responses tool handling;
- expose safe, useful API errors;
- add deterministic mocked tests;
- preserve the existing PBO/DLL protocol.

Exit achieved: direct and tool-assisted text questions complete reliably.

## M2 — Local world-model foundation

Status: **accepted**.

- local entity state with provenance, freshness and uncertainty;
- telemetry ingestion and session reset handling;
- purpose-built privacy-minimized snapshots;
- read-only diagnostics.

Exit achieved: current player, group and known-contact state can be inspected
locally without OpenAI.

## M3 — Friendly-force picture

Status: **accepted for the current read-only scope**.

- own-side groups, units and crewed vehicles;
- event-driven deltas and periodic full reconciliation;
- stale-position handling;
- read-only mission-declared asset and capability registry.

Exit achieved: Papa Bear can report current friendly positions and status from
the local world model. No support execution is available.

## M4A — Voice Position MVP

Status: **accepted live in version 0.7**.

- bounded 15-second push-to-talk microphone capture;
- OpenAI completed-utterance transcription;
- shared typed/spoken `AssistantTurnService`;
- fresh Arma world snapshot for every turn;
- ElevenLabs-only Papa Bear speech output;
- isolated microphone, transcription and voice tests;
- cancellation, replay and partial-success preservation;
- matching app, DLL and PBO artifact.

Exit achieved: the player asks by microphone for the current position and
receives a correct grounded text answer plus spoken ElevenLabs output.

## M4B — Arma Knowledge Mirror

Status: **next**.

Goal: read what Arma’s own-side AI already knows rather than constructing a
parallel perception simulation.

- aggregate `targets`, `targetKnowledge`, `knowsAbout` and documented sensor
  knowledge across eligible friendly groups;
- preserve source group, Arma estimated position, position error and age;
- deduplicate the same target across observers;
- exclude actual hidden enemy position and unrestricted world truth;
- add a bounded `query_known_contacts` local tool;
- prove that a remote WEST unit’s recognized EAST contact reaches Papa Bear.

Exit: a remote friendly unit recognizes an enemy and Papa Bear can accurately
report the same engine-provided contact knowledge.

## M4C — Named locations and proactive contact report

Status: planned after M4B.

- load only official high-level named locations;
- resolve the nearest named place to an already-known contact position;
- detect a first-known hostile contact transition;
- emit one deduplicated military contact message;
- speak the alert through the accepted ElevenLabs path.

Exit: Papa Bear announces one grounded military contact report with source
unit and location, without repeated radio spam.

## M5 — Player reports and selected physical objects

Status: deferred until M4B/M4C are stable.

- accept explicit spoken player observations;
- support correction and retraction;
- evaluate whether empty vehicles and tactical objects are represented by
  Arma’s knowledge model;
- add only narrowly scoped supplementary collection where a demonstrated gap
  exists;
- introduce mission memory only when required by accepted use cases.

## M6 — Voice and product hardening

- microphone and output-device selection;
- configurable global push-to-talk hotkey;
- latency and provider-failure measurement;
- streaming only when it produces a demonstrated UX benefit;
- installer, updater and release packaging;
- longer soak and regression tests.

## Later backlog

These are not active dependencies and require a new architecture decision:

- ACE/CBA integration;
- deterministic ballistics;
- routes and landing-zone evaluation;
- persistent operational memory;
- full static map indexing;
- validated transport, evacuation or support actions;
- multiplayer permissions, signatures and server packaging.
