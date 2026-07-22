# Product vision

## Mission

Papa Bear is a believable local radio assistant for Arma 3. The product grows
through small, live-accepted vertical proofs: Arma supplies measured facts,
local deterministic code establishes relationships, one OpenAI Responses turn
produces natural language, and ElevenLabs speaks the exact visible answer.

## Current product experience

Release 0.8 supports typed and bounded push-to-talk questions about the current
mission. It maintains privacy-minimized player, known-contact and friendly-force
state; can perform existing bounded read-only environment and force queries; and
can describe the player's measured position relative to official named places.

The contextual interpreter has four explicit fact layers:

1. measured Arma telemetry;
2. official cartographic names from the active world's configuration;
3. deterministic local distance, bearing, containment, freshness and ranking;
4. one grounded narrative response.

The complete gazetteer remains local. It contains only official named-location
configuration, never a scan of buildings, roads, vegetation, terrain, water or
runtime mission objects.

## Product principles

- Arma is authoritative for game state and perspective-bound knowledge.
- Numerical and spatial relationships are computed locally, not estimated by a
  language model.
- OpenAI receives purpose-specific minimized snapshots and bounded tool results.
- Typed and spoken turns use the same interpreter and conversation path.
- A completed transcript or text answer survives TTS and playback failure.
- Tools are typed, locally validated and read-only in the current product.
- Each broader capability requires a focused specification and live gate.

## Current non-goals

- no omniscient or side-wide hostile-state expansion;
- no arbitrary SQF, native, shell or operating-system execution;
- no full static map index, runtime terrain-object scan, SQLite map database or
  persistent operational memory;
- no player-report memory, proactive contact reports or empty-object perception;
- no ACE integration, firing-solution calculations, routes, landing-zone
  scoring or support execution;
- no always-on listening, wake word, streaming STT or streaming TTS;
- no anti-cheat bypass or hidden extension loading.

These are not release 0.8 dependencies. Git history preserves prior experiments;
future proposals must demonstrate a need and define their own fair-play boundary.

## Release 0.8 success

- the accepted release 0.7 voice pipeline remains green;
- a bounded official gazetteer loads once for the active mission session;
- ordinary position questions need one Responses request and no tool round;
- position relationships, rounding and freshness are deterministic;
- response profiles change tone only and cannot alter fact or safety policy;
- visible, historical, synthesized and replayed assistant text is identical;
- Windows tests, app/native/PBO build and live Stratis plus alternate-map checks
  pass before the draft PR becomes ready.
