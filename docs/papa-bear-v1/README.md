# Papa Bear v1 — authoritative architecture specification

Status: approved product direction, implementation pending.

This directory is the source of truth for the planned Papa Bear system. Existing code is a prototype foundation and may be changed only through the milestones in `implementation-roadmap.md`.

## Product definition

Papa Bear is a persistent AI-operated HQ, operations and logistics officer connected to the player by radio. He supports ordinary conversation, answers questions from current Arma/ACE state, consults a complete local map knowledge base, computes deterministic firing solutions and can later plan and execute validated support requests.

The target system combines:

- general OpenAI reasoning and conversation;
- a local, provenance-aware Arma world model;
- a cached static map and equipment knowledge base;
- an ACE3/CBA integration adapter;
- deterministic services for ballistics, routes, landing zones and status aggregation;
- a typed action gateway for transport, evacuation, resupply and other mission capabilities;
- AssemblyAI streaming speech recognition and ElevenLabs speech output.

## Documents

1. `product-vision.md` — scope, goals and non-goals.
2. `persona-and-dialog.md` — Papa Bear role and radio behavior.
3. `world-model.md` — dynamic state, provenance, freshness and confidence.
4. `arma-data-contract.md` — telemetry and query contract.
5. `map-knowledge-base.md` — full static map indexing and retrieval.
6. `ace3-integration.md` — ACE capability detection and versioned adapter.
7. `ballistics-service.md` — deterministic firing-solution requirements.
8. `action-gateway.md` — support requests, validation and operation state machines.
9. `voice-architecture.md` — AssemblyAI/OpenAI/ElevenLabs pipeline.
10. `privacy-and-fair-play.md` — multiplayer, data and safety boundaries.
11. `implementation-roadmap.md` — ordered milestones and exit criteria.
12. `codex-milestone-1.md` — first bounded Codex task.
13. `codex-milestone-2.md` — local provenance-aware world-model foundation.

## Binding architectural decisions

- The local application owns the world model and persistent knowledge base. OpenAI receives only relevant retrieved context.
- Static map data is fully indexed and cached per map/mod fingerprint before Papa Bear reports full map readiness.
- Dynamic telemetry is normalized into entities and deltas; raw 4 Hz JSON is not forwarded wholesale to OpenAI.
- Enemy information is limited to knowledge available to the player's side.
- Model output is advisory until a typed local tool validates and executes it.
- Arbitrary SQF or operating-system command generation is prohibited.
- Ballistic values are deterministic and ACE-aware. The language model may request and explain a solution but may not calculate or guess it.
- Papa Bear can converse without using a tool. He uses tools whenever current game facts are required.
- Voice is an interface layer, not the owner of reasoning or game state.

## Primary technical references

- OpenAI Responses API and function calling: https://platform.openai.com/docs/api-reference/responses
- OpenAI stateless reasoning context: https://platform.openai.com/docs/api-reference/responses-streaming
- ACE3 public frameworks: https://ace3.acemod.org/wiki/framework/
- ACE3 Advanced Ballistics: https://ace3.acemod.org/wiki/feature/advanced-ballistics
- ACE3 Advanced Ballistics Framework: https://ace3.acemod.org/wiki/framework/advanced-ballistics-framework.html
- ACE3 ATragMX Framework: https://ace3.acemod.org/wiki/framework/atragmx
- ACE3 Scopes Framework: https://ace3.acemod.org/wiki/framework/scopes-framework
- ACE3 Weather Framework: https://ace3.acemod.org/wiki/framework/weather-framework
- Arma `nearestLocations`: https://community.bohemia.net/wiki/nearestLocations
- AssemblyAI voice-bot reference pipeline: https://www.assemblyai.com/blog/real-time-ai-voice-bot-python
