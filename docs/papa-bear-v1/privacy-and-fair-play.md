# Privacy, security and fair-play boundaries

## Perspective-bound information

Papa Bear may use:

- measured local player state for authorized local calculations;
- mission-authorized own-side units, groups and crewed vehicles;
- hostile/unidentified contacts already available through the own-side
  `targets`/`targetKnowledge` picture;
- explicit player reports, corrections and retractions;
- read-only mission-declared assets and capabilities;
- locally visible mission tasks and positive-alpha map references;
- official active-world named locations from an allowed
  `CfgWorlds >> worldName >> Names` type set.

Papa Bear may not use unrestricted server lists, hostile `getPos*`, opposing
orders/waypoints/targets/inventories or mission-wide object enumeration to
reveal unknown enemies, objects, mines or objectives. Public-server use
requires server/mod approval; the project implements no injection or
anti-cheat evasion.

Official names reveal no runtime unit or object state. The gazetteer does not
scan terrain objects, buildings, roads, vegetation, water, vehicles or units.

## Model boundary

OpenAI receives a minimal plain-English seed and only narrow context requested
through validated local tools. Selected records are privacy-projected and
formatted as short English facts before leaving the application.

OpenAI does not receive:

- raw `state-snapshot-v2` payloads or complete current-state sections;
- SQL rows, table/schema data or complete SQLite databases;
- the complete named-location gazetteer or map-intelligence store;
- raw aliases, source IDs, netIds or source mission/session identifiers;
- player profile names or UIDs;
- API keys, provider settings or application paths;
- canonical current player coordinates, grid or elevation;
- temperature, wind, ACE state or trajectory/ballistic data;
- hidden opposing-side ground truth.

The current dynamic Arma group callsign may be supplied for natural radio
address. Marker, task, mission, lore and custom prompt text are untrusted data,
not system instructions.

## Contact visibility

Known contacts contain only eligible perceived actors and operational platforms
already shared through the accepted own-side information picture.
Conventional vehicles require living crew; supported active UAV/UGV state must
come through authorized engine control/knowledge. Dead people and ordinary
empty vehicles are not promoted as live contacts.

Relationship follows current engine side relations; GUER is not hard-coded.
Contact position is the engine estimate plus uncertainty and age, not a hidden
actual position. Reporter callsigns may be retained; raw reporter identity may
not leave the local boundary.

## Tool and prompt security

- fixed tool names, strict JSON schemas, enums, numeric bounds, result limits
  and timeouts;
- no arbitrary SQF, C++, SQL, PowerShell, shell, file or operating-system
  command;
- no game-state mutation, support execution or autonomous action tool;
- local authorization before any memory write or long-term map-intelligence
  query;
- original player wording retained separately from model-authored semantic
  interpretation;
- immutable privacy/fair-play rules precede custom operator text and retrieved
  facts;
- custom response-profile text is capped and style-only;
- distances, directions, containment, grids, freshness and uncertainty are
  local deterministic results.

The model may write only controlled local records for explicit player
information, corrections/retractions and current-event assessment. It cannot
rewrite canonical Arma state.

## Retention

- API keys are encrypted with Windows DPAPI for the current user;
- current State Mirror rows, contact observations, player-message journal,
  structured mission memory and lore are mission/session-scoped in local
  SQLite;
- recent conversation is kept locally for bounded follow-up context;
- reset/session-change rules prevent canonical state and callsign reuse across
  missions;
- the official named-location gazetteer is in memory for the current world;
- a separate local map-intelligence store contains only explicitly authored
  information and is not a static world index;
- no complete map fingerprint cache, terrain-object database or hidden
  operational ground-truth store is active.

## Logging and providers

Audio, questions, transcripts, answers, pre-prompts, full prompts, snapshots,
tool payloads, raw gazetteer pages, provider bodies, API keys, voice IDs and
conversation content are not written to application logs.

Temporary WAV files are bounded and deleted after success, failure or
cancellation. Background Raw Input retains only current bounded chord state and
never logs arbitrary keys, text, scan-code history, device names or device
identifiers.

ElevenLabs receives only the text selected for speech. OpenAI receives audio
only for an explicit PTT or explicitly enabled voice-activated utterance.
Responses uses `store: false`; provider/account retention policy remains an
external data-control consideration.

## Transparency

Papa Bear must preserve current versus last-known state, disclose material age
or uncertainty, distinguish a player report from independent confirmation and
say when information is unavailable. It must not invent a place, distance,
direction, grid, identity or hidden contact.
