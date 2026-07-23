# Papa Bear v1 — architecture and milestone record

Status: **version 0.8 Unified State Mirror & Interpreter in draft; version 0.7 accepted live**.

The active release 0.8 model-context and persistence boundary is
[`release-0.8-tactical-memory.md`](release-0.8-tactical-memory.md). It
supersedes the earlier broad Milestone 5 model snapshot without changing the
accepted State Mirror collection or push-to-talk path.

This directory records the current product boundaries, accepted milestones and
future architecture decisions for Papa Bear. The repository is developed
incrementally: narrow vertical proofs of concept are accepted before broader
perception, memory or support features are added.

## Current product definition

Papa Bear is a local AI radio assistant for Arma 3. The current application:

- receives perspective-bound Arma telemetry through the SQF addon, native x64
  extension and local Named Pipe;
- maintains a privacy-minimized local world model;
- supports typed and push-to-talk questions through one shared assistant path;
- uses OpenAI audio transcription and OpenAI Responses;
- uses ElevenLabs exclusively for Papa Bear speech output;
- exposes only locally validated, read-only tools;
- does not execute arbitrary game or operating-system commands.

The accepted 0.7 proof of concept answers a spoken question about the player’s
current position using live Arma state and returns both visible text and spoken
ElevenLabs audio.

The 0.8 milestone adds one bounded unified state snapshot, a local SQLite
current-state mirror, one fixed compact multi-domain operational prompt,
official named-place interpretation, dynamic Arma player-group callsigns,
local two-stage radio acknowledgement and response profiles. Complete
collections stay local. The release patch adds conditional delayed
acknowledgements, speech-safe English, one bounded max-token retry and
configurable global press-and-hold PTT.

## Active architectural decisions

- Arma remains authoritative for game state and perception.
- Papa Bear must not receive unrestricted hidden enemy truth.
- Raw telemetry is normalized locally; it is not forwarded wholesale.
- Typed and spoken input share the same world snapshot, conversation and tool
  loop.
- Voice is an interface layer, not a separate reasoning system.
- One encrypted OpenAI key is used for transcription and Responses.
- ElevenLabs is the only speech-output provider in the active product.
- Failed TTS or playback must never discard a completed transcript or answer.
- Arma supplies measured facts; local services calculate spatial relations; one
  existing Responses request supplies natural language; ElevenLabs speaks the
  final visible answer with only deterministic speech pronunciation of numbers,
  units and the current Arma callsign permitted to differ.
- Tools remain bounded, typed, locally validated and read-only until a later
  milestone explicitly introduces an authorized action.
- ACE integration, firing-solution calculations, routes, support execution,
  observation-fusion memory, full map indexing and broad proactive notifications
  are not active product capabilities. Release 0.8 has only deterministic
  new/reacquired hostile-or-unknown contact announcements from accepted own-side
  target knowledge.

## Active documents

1. `product-vision.md` — long-term product goals and non-goals.
2. `persona-and-dialog.md` — Papa Bear role and radio behavior.
3. `world-model.md` — dynamic state, provenance, freshness and uncertainty.
4. `arma-data-contract.md` — current telemetry and query contracts.
5. `privacy-and-fair-play.md` — multiplayer and data boundaries.
6. `voice-architecture.md` — active OpenAI transcription/Responses and
   ElevenLabs output pipeline.
7. `implementation-roadmap.md` — accepted milestones and next focused steps.
8. `codex-milestone-1.md` — OpenAI tool-loop stabilization.
9. `codex-milestone-2.md` — local world-model foundation.
10. `codex-milestone-3.md` — friendly-force picture and read-only mission
    capabilities.
11. `codex-milestone-4a-voice-position-mvp.md` — the accepted push-to-talk
    position-answer proof of concept.

12. `codex-milestone-5-unified-state-mirror.md` — complete release 0.8 state,
    SQLite, interpretation, fixed compact operational context and diagnostics
    contract.
13. `codex-milestone-5-contextual-interpreter.md` — subordinate Phase A
    named-location, spatial-language and response-profile design.
Other documents describe deferred designs or historical experiments. They are
not automatically approved implementation scope. A later milestone must
explicitly reactivate them.

## Current implementation stack

- Windows 10/11 x64;
- C# / .NET 8 / WPF;
- NAudio microphone capture and playback;
- C++ x64 Arma extension;
- SQF client addon;
- duplex Windows Named Pipe;
- OpenAI audio transcription;
- OpenAI Responses API with strict function tools;
- ElevenLabs text-to-speech.

## Active product step

Complete release 0.8 automated verification and the exact Unified State Mirror
live gate. Release 0.9 remains the home for broader state-change detection and
radio notifications; the release-0.8 contact-announcement exception does not
authorize any other proactive category.
