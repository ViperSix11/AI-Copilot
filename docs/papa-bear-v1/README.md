# Papa Bear v1 — architecture and milestone record

Status: **version 0.7 Voice Position MVP accepted live**.

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
- Tools remain bounded, typed, locally validated and read-only until a later
  milestone explicitly introduces an authorized action.
- ACE, ballistics, persistent operational memory and full map indexing are
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

## Next product step

The next intended milestone is a narrow **Arma Knowledge Mirror**: aggregate
Arma’s existing own-side target knowledge across friendly groups and prove that
a remote friendly unit’s recognized enemy contact reaches Papa Bear. It must
not introduce a parallel visibility or perception simulation.
