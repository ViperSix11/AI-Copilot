# Papa Bear v1 — architecture and milestone record

Status: **version 0.8 Contextual Interpreter in draft; version 0.7 accepted live**.

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

The 0.8 milestone adds local deterministic named-place interpretation to that
accepted path. It reads only official active-world `Names` configuration, keeps
the complete gazetteer local and uses the existing single Responses call.

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
  exact final visible text.
- Tools remain bounded, typed, locally validated and read-only until a later
  milestone explicitly introduces an authorized action.
- ACE, ballistics, routes, support execution, persistent operational memory and full map indexing are
  deferred and are not dependencies of the current POC.

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

12. `codex-milestone-5-contextual-interpreter.md` — release 0.8 bounded
    named-location and deterministic spatial-language contract.

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

Complete release 0.8 automated verification and the exact live Stratis plus
alternate-map acceptance. No later tactical-awareness or execution milestone is
active until it receives its own reviewed specification.
