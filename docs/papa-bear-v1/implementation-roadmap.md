# Implementation roadmap

Each milestone is implemented on a separate branch/PR and must meet automated Windows CI plus its manual Arma acceptance test. Later milestones must not be pulled forward without an explicit architecture decision.

## M1 — Stabilize the OpenAI text tool loop

- preserve stateless reasoning context correctly across function-call rounds;
- expose safe, useful API errors;
- add mocked tests for direct and tool-assisted responses;
- keep existing PBO/DLL protocol unchanged.

Exit: direct questions and `query_environment` questions complete reliably.

## M2 — World-model foundation

- entity store, provenance, freshness and confidence;
- telemetry ingestion and delta processing;
- purpose-built context retrieval;
- session lifecycle and diagnostics.

Exit: current player/group/known-contact state can be queried locally without OpenAI.

## M3 — Friendly force picture and mission capabilities

- own-side unit, group, vehicle and support-asset collectors;
- mission capability registry;
- event-driven updates plus reconciliation;
- stale-position handling.

Exit: Papa Bear can accurately report friendly positions/status and available read-only assets.

## M4 — Observation-driven operational memory

- small persistent official named-location gazetteer;
- bounded own-side perception and authorized report ingestion;
- mission-scoped SQLite entities, immutable observations, provenance, uncertainty, freshness, confidence and conflicts;
- named-location, operational-memory and controlled player-report tools.

Exit: official named-location questions are answered without full map knowledge, unseen tactical objects remain unknown, and observed/reported objects retain provenance and last-known uncertainty.

## M5 — Papa Bear orchestrator and persona

- normal conversation and tool selection;
- context planner/retriever;
- persona/radio rendering separated from factual content;
- mission and short dialogue memory;
- evaluation suite for unsupported claims and stale information.

Exit: coherent normal conversation plus correct game-grounded answers in Papa Bear style.

## M6 — ACE3 capability adapter

- source/documentation audit of public ACE/CBA APIs;
- capability detection;
- medical, weather, weapon, ammunition and scope DTOs;
- versioned fallbacks and compatibility diagnostics.

Exit: current ACE configuration and relevant states are displayed and testable locally.

## M7 — Deterministic ballistics

- provider abstraction;
- ACE profile extraction;
- firing-solution calculation and click conversion;
- terrain-elevation projection from range/bearing;
- validation matrix against ACE Range Card/ATragMX.

Exit: agreed test profiles match ACE reference results within documented tolerances.

## M8 — Read-only operational reasoning

- routes, landing zones, cover and risk tools;
- available-asset evaluation;
- plan generation without game mutation;
- explicit assumptions and alternatives.

Exit: Papa Bear proposes feasible, evidence-backed plans but cannot yet execute them.

## M9 — Validated support requests

- action gateway and authorization policy;
- first mission capability, recommended: rotary transport/extraction;
- operation state machine, idempotency, cancellation and monitoring;
- proactive status messages.

Exit: one support request runs end-to-end in a controlled mission.

## M10 — Voice interface

- AssemblyAI streaming STT and push-to-talk;
- terminology/callsign customization;
- ElevenLabs streaming TTS and radio effects;
- interruption, cancellation and priority event queue.

Exit: stable typed and spoken parity with acceptable latency.

## M11 — Hardening and multiplayer packaging

- performance and soak tests;
- signatures/server guidance;
- permission profiles;
- installer/updater and compatibility matrix;
- privacy and data-control review.
