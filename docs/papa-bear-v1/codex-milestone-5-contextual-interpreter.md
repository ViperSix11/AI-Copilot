# Codex Milestone 5 Phase A: Contextual Interpreter

> This is the preserved subordinate Phase A design. The complete release 0.8
> boundary is `codex-milestone-5-unified-state-mirror.md`.
> Any question-specific state-selection language below is superseded by that
> document's fixed compact operational snapshot contract.

## Objective and release boundary

Milestone 5 / release 0.8 turns measured Arma position facts into concise,
natural military language. Arma remains authoritative for measurements, local
deterministic services establish spatial relationships, the existing single
OpenAI Responses request renders the answer, and ElevenLabs speaks exactly the
final visible text.

The product version is `0.8` and the assembly version is `0.8.0`.

Phase A adds only an official named-location gazetteer, local position
interpretation, one bounded gazetteer read tool, and a response-style profile.
It preserves the accepted 0.7 voice pipeline, stateless tool loop, partial
success, replay, Milestones 1–3 world state, native transport, and PBO
packaging. It does not add another model request.

Explicitly out of Phase A are full static-map indexing, runtime terrain-object
scans and observation-fusion operational memory. SQLite current-state mirroring,
selected weather/loadout/task/marker state and own-side contact aggregation are
defined by the parent release specification. Still deferred are proactive
contact reports, empty-object perception, player-report memory, ACE, routes,
landing-zone analysis, support execution, always-on audio, wake words, VAD and
streaming STT/TTS. Phase A itself added no ballistics or global hotkey; the
later parent release patch narrowly authorizes the Vanilla-config solver and
registered press-and-hold PTT defined there.

## Existing implementation and official-command audit

The accepted addon publishes a versioned session handshake and world events on
the non-coalescing `event|` extension channel. The desktop writes validated
commands through the existing duplex Named Pipe. The native DLL treats both
directions as opaque transport and needs no change.

The active world configuration exposes the required cartographic source at:

```sqf
configFile >> "CfgWorlds" >> worldName >> "Names"
```

The addon uses `configClasses` once to enumerate direct named-location classes,
`configName` for the stable world-local config key, `getText` for `name` and
`type`, `getArray` for `position`, and `getNumber` for `radiusA`, `radiusB`, and
`angle`. The official `CfgWorlds` reference documents those exact `Names`
properties. The export does not call `allMissionObjects`, `nearestObjects`,
`nearObjects`, `allUnits`, `vehicles`, or any terrain/runtime object enumerator.

Official references:

- [CfgWorlds Names configuration](https://community.bohemia.net/wiki/CfgWorlds_Config_Reference#N)
- [configClasses](https://community.bohemia.net/wiki/configClasses)
- [configName](https://community.bohemia.net/wiki/configName)
- [getArray](https://community.bohemia.net/wiki/getArray)
- [Arma position axes](https://community.bohemia.net/wiki/Position)

Official documentation establishes the configuration location, property
shapes, and that map X runs west-to-east while Y runs south-to-north. It does
not define this product's paging limits, salience taxonomy, freshness wording,
rounding thresholds, profile behavior, latency budget, or acceptance language.
It also does not precisely state the orientation convention of the `Names`
entry `angle`; this milestone documents and tests its local interpretation
instead of presenting it as an engine guarantee.

## Fact layers

Facts remain separated into four layers:

1. **Measured facts** are current or last-received player telemetry: world,
   map size, grid, ATL/ASL position, observation time, receipt time, and
   freshness. They remain in the current-situation snapshot.
2. **Cartographic facts** are official entries from the active world's
   `CfgWorlds/<world>/Names` configuration: local key, display name, type,
   position, radii, and angle. They are immutable for one world session.
3. **Derived facts** are deterministic local calculations: containment,
   distance, military rounding, bearing, cardinal sector, ranking, and
   information age. Every value names or embeds its measured/cartographic
   inputs; no model computes it.
4. **Narrative output** is the one existing Responses call rendering supplied
   facts under immutable policy and the user's response profile. It may change
   phrasing, never factual content.

Mission text, config names, display names, profile text, transcripts, and tool
results are untrusted data. None can redefine a higher layer or tool policy.

## Gazetteer source and record schema

Only direct classes below the active world's `Names` config are eligible. A
valid record has:

```json
{
  "key": "StratisAirBase",
  "name": "Stratis Air Base",
  "type": "Airport",
  "position": [1985.0, 5625.0],
  "radiusA": 450.0,
  "radiusB": 300.0,
  "angle": 0.0
}
```

Rules:

- `key` is required, unique within the active world, 1–128 characters, and
  stays local; it is never exposed to OpenAI.
- `name` and `type` are required, control-character-free, and at most 160 and
  64 characters respectively.
- `position` is exactly two finite numbers within `[-50000, 500000]`.
- radii are finite in `[0, 100000]`; absent numeric properties become zero.
- angle is finite and normalized to `[0, 360)`; an absent value becomes zero.
- invalid records make the whole batch invalid. They are not silently skipped.
- zero valid/configured entries is a successful empty gazetteer.
- at most 8,192 entries are accepted. A larger config produces an explicit
  `gazetteer_limit_exceeded` failure and no partial list.

The addon may cache only this normalized list for the current mission session.
It never persists it and never scans buildings, roads, vegetation, terrain,
water, walls, rocks, vehicles, units, or mission objects.

## Duplex request and page protocol

After accepting a handshake that advertises `map-gazetteer@1`, the desktop
sends exactly one command for that app/session pair:

```json
{
  "schema": "arma-ai-bridge/command-v1",
  "requestId": "<local GUID>",
  "command": "request_map_gazetteer",
  "parameters": {}
}
```

The addon lazily collects and caches the list on the first request. Later app
requests in the same Arma session re-page the cache without rescanning config.
A new addon session owns a new cache. Collection runs scheduled and yields at
least once per 128 entries so telemetry and command polling remain responsive.

Every response page uses schema
`arma-ai-bridge/arma3/map-gazetteer-v1` and the existing world-event envelope:

```json
{
  "schema": "arma-ai-bridge/arma3/map-gazetteer-v1",
  "messageId": "message-000005",
  "missionId": "mission-local-id",
  "sessionId": "session-local-id",
  "timestamp": 1.5,
  "sequence": 5,
  "requestId": "<local GUID>",
  "batchId": "gazetteer-000001",
  "pageIndex": 0,
  "pageCount": 1,
  "world": { "name": "Stratis", "sizeMeters": 8192.0 },
  "totalLocations": 1,
  "status": "complete",
  "errorCode": null,
  "locations": []
}
```

Protocol rules:

- each page carries the session, mission, request, batch, and world identity;
- pages contain at most 128 locations and page count is 1–64;
- a zero-location success has one empty page and `totalLocations: 0`;
- successful pages use `status: complete` and null `errorCode`;
- a collection failure is one page with `status: failed`, a safe enumerated
  `errorCode`, no locations, and no config content in logs;
- all pages agree on envelope identity, request, batch, page count, world,
  total, status, and error; the envelope timestamp is the publication time of
  each page and may differ between scheduled pages;
- page indexes are zero-based, unique, and strictly below page count;
- the sum of locations must equal `totalLocations` exactly;
- duplicate keys, duplicate pages, missing pages, invalid fields, oversized
  totals, session/world mismatch, and unknown status/error values reject the
  candidate batch;
- an incomplete batch expires after 10 seconds and never becomes active;
- the desktop validates checked-in JSON Schema and the same invariants in code.

The handshake adds `{ "name": "map-gazetteer", "version": 1 }`. Protocol
major remains 1; this is an additive feature and message family.

## In-memory MapGazetteerStore

`MapGazetteerStore` is independent of `WorldStateStore` but keyed to its active
source session and world. It owns one pending batch and one immutable active
gazetteer. Readiness is:

```text
unavailable | requesting | assembling | ready | empty | failed
```

On a new session or world it atomically clears active/pending data and returns
to `unavailable`. The first valid page moves it to `assembling`; only a complete
validated batch swaps the active list and yields `ready` or `empty`. Failure or
expiry yields `failed` with a safe diagnostic code. No partial list is ever
queryable.

Safe diagnostics expose readiness, world, received/page counts, active location
count, last request/completion times, and safe error code. They do not show
source mission/session IDs, full JSON, config keys, or the complete gazetteer.
World State diagnostics shows this bounded summary.

## Position interpretation

`PositionInterpretationService` consumes one `WorldStateView` plus the active
gazetteer snapshot and returns an immutable compact interpretation. It never
performs I/O and never calls a model.

Output fields are:

```json
{
  "status": "live",
  "worldName": "Stratis",
  "grid": "035046",
  "measuredPosition": { "x": 3500.0, "y": 4600.0, "z": 12.0 },
  "informationAgeSeconds": 0.4,
  "primaryReference": {
    "name": "Agia Marina",
    "type": "NameCity",
    "inside": false,
    "distanceMeters": 742.6,
    "roundedDistanceMeters": 750,
    "distanceKlicks": null,
    "bearingFromReference": 315,
    "directionFromReference": "northwest"
  },
  "alternativeReferences": []
}
```

`status` is `live` only when player freshness is `live` or `recent`; `stale`
and `historical` become `last-known`. Information age is the deterministic
non-negative player metadata age. No interpretation may say current when the
status is last-known.

The measured position stays in the purpose-specific snapshot for grounding and
explicit coordinate requests. Immutable instructions prohibit rendering raw
coordinates for ordinary position questions and permit them only when the user
explicitly asks for coordinates/grid precision.

### Containment

A configured area exists only when both radii are greater than zero. The local
policy treats `angle` as clockwise degrees from map north, rotates the
player-minus-reference vector into the location frame, and evaluates:

```text
(localX / radiusA)^2 + (localY / radiusB)^2 <= 1
```

The boundary counts as inside. Zero-radius entries are point references and
never `inside`. This convention is a versioned product rule because official
documentation lists `angle` but does not define its orientation semantics.

### Distance, bearing, and direction

All geometry is 2D map geometry:

- `distanceMeters` is Euclidean centre distance rounded to 0.1 m;
- `bearingFromReference` is `atan2(deltaX, deltaY)`, normalized to `[0,360)`,
  then rounded to the nearest whole degree away from zero at midpoint;
- the vector always runs **from the named reference to the player**;
- eight sectors have 45-degree width centred on north, northeast, east,
  southeast, south, southwest, west, and northwest; north covers
  `[337.5,360)` and `[0,22.5)`.

Military distance rounding uses midpoint-away-from-zero:

| Exact 2D distance | `roundedDistanceMeters` | `distanceKlicks` |
| --- | --- | --- |
| below 100 m | nearest 10 m | null |
| 100 m through 999.9 m | nearest 50 m | null |
| 1,000 m or more | nearest 100 m | kilometres rounded to 0.1 |

The model receives exact derived distance plus these prevalidated spoken forms
and is instructed not to recalculate or substitute them.

## Deterministic reference ranking

Every valid location is categorized case-insensitively:

1. settlement/transport: `NameCityCapital`, `NameCity`, `NameVillage`,
   `CityCenter`, `Airport`, `NameMarine`, `Port`;
2. meaningful geography: `NameLocal`, `Mount`, `Bay`, `Lake`, `BorderCrossing`;
3. minor: `Hill`, `ViewPoint`, `RockArea`;
4. other official named type.

Ranking is deterministic:

1. containing locations always precede non-containing locations, ordered by
   category tier, centre distance, normalized name, then key;
2. outside locations use `effectiveDistance = centreDistance + penalty`, with
   penalties 0 m, 250 m, 500 m, and 750 m for tiers 1–4;
3. outside ties use tier, centre distance, normalized name, then key;
4. duplicate display-name/type/position records are deduplicated by retaining
   the lexically first local key;
5. primary plus at most two alternatives enter `interpretedLocation`;
6. if no active locations exist, the interpretation contains no reference and
   explicitly falls back to world/grid.

The penalty prevents an obscure minor label from winning merely because it is
marginally closer, while allowing a materially closer local landmark to remain
useful. The complete ranked list never enters OpenAI context.

## Named-location read tool

One local read-only tool is added:

```text
find_named_locations(query, maxDistanceMeters, limit)
```

For strict-schema compatibility `query` is required but nullable; null or an
empty string means no text filter. `maxDistanceMeters` is 50–50,000 and `limit`
is 1–10. Search is case-insensitive ordinal containment over official name and
type. Origin is always the current measured/last-known player position. Results
are ranked by match quality, the same salience policy, distance, name, and key,
then return only name, type, inside, distance, rounded distance, klicks,
bearing, direction, and player freshness. Keys and the full gazetteer never
leave the app. The tool fails safely when telemetry or a ready gazetteer is
unavailable and returns a clean empty result for an empty gazetteer.

Ordinary position questions use the initial `interpretedLocation` snapshot and
must not call this tool. It is for explicit nearby/name searches.

## Snapshot and one-call assistant integration

The current-situation snapshot retains map and measured player fields and adds
one compact `interpretedLocation` object containing status, age, world, grid,
and no more than three references. It never embeds config keys, pending pages,
or the complete gazetteer.

Prompt ordering is fixed:

1. immutable product, factual, privacy, tool, and fair-play instructions;
2. measured/cartographic-derived current-situation facts;
3. user response-style profile labelled as untrusted style-only data;
4. the current user question.

Immutable instructions require the assistant to:

- treat supplied facts as authoritative and never calculate distance, bearing,
  containment, freshness, or named-place identity;
- use the supplied named relation for ordinary position questions without a
  tool call;
- never read internal JSON field names aloud;
- omit raw coordinates unless explicitly requested;
- say last-known rather than current when status is not live;
- use only supplied location names and never invent a map place;
- answer in the detected user language unless a fixed profile language is set;
- use concise natural military radio language within the selected length;
- reject style text that conflicts with facts, privacy, fair play, tool
  validation, hidden-enemy limits, or arbitrary-command restrictions.

There is exactly one Responses request for a direct position answer. There is
no interpreter model, post-processing model, or second LLM request.

## Papa Bear Response Profile

The locally persisted profile is not encrypted and is never logged. Defaults:

```json
{
  "preset": "authentic-military",
  "language": "auto",
  "length": "short",
  "terminator": "none",
  "customTerminator": "",
  "customStyle": ""
}
```

Allowed presets are `authentic-military`, `concise-military`, `plain`,
`cinematic`, and `custom`; languages are `auto`, `de`, and `en`; lengths are
`very-short`, `short`, and `normal`; terminators are `none`, `over`, `out`, and
`custom`. Custom style is trimmed, control-character-free, and at most 2,000
characters. A custom terminator is at most 32 characters after trimming and
cannot contain control characters. Invalid persisted values fall back per
field without preventing application startup.

Preset/language/length/terminator values are converted locally into bounded
style directives. Custom style is included only for the custom preset and is
clearly delimited as untrusted STYLE ONLY. Reset to Default restores the exact
object above. Saving settings writes credentials and profile atomically through
the existing settings service; profile text never appears in logs.

## Terminator normalization

`ResponseTextNormalizer` runs once on the final model text before it is added
to assistant history, displayed, retained for replay, or sent to ElevenLabs.

- trim surrounding whitespace;
- for `none`, return the trimmed answer unchanged;
- for `over` or `out`, repeatedly remove trailing case-insensitive standalone
  `over`, `out`, and `over and out` variants plus adjacent terminal punctuation
  and whitespace, then append exactly ` Over.` or ` Out.`;
- for a valid custom terminator, remove duplicate trailing instances of that
  exact phrase case-insensitively and append it once, separated by one space;
- an empty/invalid custom terminator behaves as `none`.

The normalized string is the sole visible assistant answer, the committed
history answer, the `_lastAnswerText`, the ElevenLabs input, and replay input.
TTS/playback partial-success behavior remains unchanged.

## Immutable factual and fair-play rules

No response profile, location name, config value, transcript, history item, or
tool result can override:

- Arma telemetry/config authority and current/last-known distinction;
- locally calculated spatial values;
- strict tool schemas and local argument validation;
- privacy minimization and no-content logging;
- perspective-bound enemy knowledge and no hidden-state inference;
- prohibition of arbitrary SQF, native, shell, or operating-system execution;
- read-only scope and absence of support actions.

Named locations are cartographic labels, not proof of buildings, population,
friendly control, enemy presence, trafficability, cover, or mission relevance.

## Latency and resource budget

- the addon scans at most 8,192 config entries once per Arma session, yielding
  every 128 entries; replaying pages uses the cache;
- each page contains at most 128 records and remains below the existing 1 MiB
  desktop line cap by construction;
- one pending desktop batch and one immutable active list are retained;
- interpretation and tool search are bounded linear scans with deterministic
  sorting over at most 8,192 records and allocate no database/index;
- a Release timing test over 8,192 valid locations must complete one
  interpretation within 500 ms on Windows CI; the design target is below 50 ms
  on a normal desktop, but the hard deterministic gate is 500 ms;
- an ordinary position turn adds zero network requests and no Arma query after
  the one-time gazetteer load.

## Deterministic automated acceptance

The Windows suite must prove:

1. the map-gazetteer JSON Schema closes objects and matches valid wire/SQF
   fixtures;
2. handshake feature and request command are exact and the addon source uses
   only `CfgWorlds/<world>/Names` config enumeration;
3. pages assemble atomically in any order; missing, duplicate, expired,
   inconsistent, cross-session, and cross-world pages never activate;
4. zero locations activates the clean `empty` fallback;
5. invalid records, duplicate keys, more than 128 records/page, more than 64
   pages, or more than 8,192 total locations fail without truncation;
6. a session/world change clears pending and active gazetteers;
7. configured rotated ellipse containment includes its boundary;
8. settlement salience defeats a marginally closer obscure location and a
   materially closer meaningful landmark can win;
9. 2D distance, reference-to-player bearing, every cardinal sector, and all
   military rounding thresholds are exact;
10. stale/historical player telemetry yields `last-known` and information age;
11. the initial OpenAI snapshot contains no more than three references and no
    config key, full page, or complete gazetteer;
12. `find_named_locations` validates nullable query, distance, and limit and
    returns only bounded privacy-safe derived fields;
13. an ordinary typed or spoken position question uses the same interpreter,
    makes exactly one Responses request, and needs no tool call;
14. immutable instructions omit raw coordinates by default, allow them on an
    explicit request, prohibit invented locations/calculations, and precede
    profile text;
15. profile defaults, validation, persistence, and reset are deterministic;
    custom style cannot replace immutable instructions;
16. Over/Out/custom normalization is idempotent and appends exactly once;
17. visible answer, history, ElevenLabs input, retained replay text, and replay
    synthesis use the identical normalized value;
18. TTS/playback failure still preserves visible normalized text and retry
    never repeats transcription or Responses;
19. maximum-size interpretation completes within the 500 ms Release gate;
20. every existing 0.7 regression, repository verifier, WPF publish, native x64
    build, PBO package/verify, and artifact-content check remains green.

Seeded private values in tests must not occur in logs: questions, answers,
transcripts, style text, full gazetteer/page JSON, config keys, source
mission/session IDs, snapshots, tool results, provider bodies, credentials, or
voice IDs.

## Exact live Arma acceptance

Build release 0.8 and use the matching app, DLL, `mod.cpp`, and PBO. Complete
the following on Stratis and one alternate or modded map:

1. Start the desktop app and mission; confirm handshake advertises
   `map-gazetteer@1`, diagnostics transition requesting/assembling to ready or
   empty, and only one config collection occurs for that session.
2. Ask an ordinary position question. Confirm one Responses request, no local
   tool call, one correct official named-place relation, and matching visible
   and spoken text.
3. Independently check centre distance and the reference-to-player direction
   on the map; they must match the derived values and military rounding.
4. Move across the reference centre/sector boundary and repeat; direction must
   change to the correct new relation.
5. Enter a configured location ellipse and confirm the answer describes the
   player as inside that place.
6. Ask in German and English with language `auto`; verify corresponding output.
7. Select fixed German then fixed English and verify each overrides the input
   language.
8. Change preset/custom style and confirm tone/length changes while map,
   location, distance, direction, freshness, privacy, and tool policy do not.
9. Select `Over`; confirm the visible answer ends in exactly one `Over.`,
   ElevenLabs speaks that exact text, and replay uses the same text.
10. Explicitly request coordinates and confirm the answer includes the measured
    coordinates/grid; repeat an ordinary position question and confirm raw
    coordinates are omitted.
11. On a world/config fixture with zero valid `Names` entries, confirm the
    answer naturally falls back to world and grid without inventing a place.
12. Let telemetry age into stale state and confirm the answer says last-known,
    never current.
13. Restart only the desktop app in the same mission; confirm one new app
    request receives cached pages without another config scan.
14. Restart or change the mission/world; confirm old locations reset before the
    new batch can activate.
15. Run all accepted 0.7 microphone, transcription, typed/spoken parity,
    partial-success, replay, friendly-force, environment-query, and privacy
    checks.
16. Inspect logs and confirm there is no question, answer, transcript, style
    text, complete gazetteer/page, config key, source session/mission ID,
    snapshot, tool result, credential, or voice ID.

The draft PR remains unmerged until every live step passes. Live Windows audio,
actual provider latency/quotas, language quality, modded-map config quality,
and exact natural-language compliance cannot be established by official API or
Arma command documentation and require this gate.
