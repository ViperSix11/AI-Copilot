# Codex Milestone 4: Observation-Driven Operational Memory

## Status and objective

This document replaces the static-map-indexing scope previously assigned to
Milestone 4. It is the authoritative Milestone 4 implementation contract. The
full tile exporter and full static map index described in
`map-knowledge-base.md` are not part of this milestone and must not be carried
forward from draft PR #9.

Papa Bear begins a mission with only basic map metadata and an official named-
location gazetteer. Vehicles, contacts, tactical objects, their condition, and
other operational facts become known only through an eligible own-side
observer, an explicitly authorized mission report, or an explicit player
report. The local application preserves the evidence and uncertainty instead
of turning the latest message into omniscient world truth.

Milestones 1-3 remain unchanged: the existing player telemetry, manual
`query_environment`, friendly-force and mission-capability read tools, and the
stateless OpenAI Responses tool loop continue to work.

## Scope and explicit non-goals

Milestone 4 implements:

- a small persistent, per-world official gazetteer;
- bounded, observer-centric own-side observations;
- a mission-scoped SQLite operational-memory database;
- deterministic identity, fusion, confidence, uncertainty, freshness,
  correction, contradiction, and retraction rules;
- privacy-safe local aliases for every entity exposed outside ingestion;
- `find_named_locations`, `query_operational_memory`,
  `record_player_observation`, and `correct_player_observation`;
- read-only Operational Memory diagnostics;
- typed reports routed through one ingestion boundary suitable for a future
  transcript adapter.

It does not implement full map tiles; scans of all buildings, roads,
vegetation, terrain, or mission objects; routes; landing-zone analysis;
support execution; waypoint assignment; arbitrary SQF; ACE; ballistics; voice
streaming; or new mission-declared support assets/capabilities.

## Existing implementation and engine audit

### Existing addon and transport

The Milestone 3 client addon already has a protocol 1.0 handshake, monotonic
event envelopes, 4 Hz player telemetry, 1 Hz friendly-force evaluation,
15-second full force reconciliation, and a non-coalescing `event|` Named Pipe
channel. The native DLL treats event payloads as opaque transport and needs no
Milestone 4 change. The desktop validates protocol envelopes and attaches them
to the active handshake before reducing them into `WorldStateStore`.

The friendly collector already provides the bounded set of eligible own-side
units and crewed vehicles. It does not export the unfiltered `vehicles` array.
The existing contact feed is perspective-bound but player/group-centric; it is
not sufficient to prove which friendly observer supplied each observation.
Milestone 4 therefore adds separate gazetteer and observation event families.
It does not duplicate observations into the 4 Hz telemetry envelope.

No full map exporter or static SQLite map index exists on `main`. This branch
must remain free of PR #9's tile, terrain, building, road, vegetation, water,
and R-tree indexing implementation.

### Official Arma commands and config sources

| Requirement | Documented source | Milestone 4 use and limit |
| --- | --- | --- |
| World metadata | `worldName`, `worldSize` | World name and square terrain side length only. No terrain samples are derived. |
| Official names | `configFile >> "CfgWorlds" >> worldName >> "Names"`; documented `Names` entries contain `name`, `type`, `position`, `radiusA`, and `radiusB` | Read the terrain configuration directly so runtime-created locations are not silently labeled official. Entries with missing/blank names or invalid positions are omitted. No building-density inference is allowed. |
| Location verification/fallback | `nearestLocations`, `text`, `type`, `locationPosition`, `size` | Useful for manual/live verification. Runtime locations are not added automatically to the persistent official gazetteer. |
| Grid presentation | `mapGridPosition`; the world's `Grid` config is available but terrain-specific | Export exactly three bounded `mapGridPosition` format samples (origin, center, far edge). Do not recursively export terrain grid config. Grid text is presentation metadata, not a replacement for world coordinates. |
| Side-known contacts | `targets`, `knowsAbout`, `targetKnowledge` | Query targets for one eligible observer. `targetKnowledge` supplies known-by flags, last-seen/threat times, perceived side, position error, and estimated position. Exact unrestricted object positions are not exported for enemy contacts. |
| Bounded physical candidates | `nearObjects`/`nearestObjects` | Run only around the observer, with a fixed type allowlist and fixed radius. Never use an empty type list. This is candidate discovery, not visibility authority. |
| View direction and line of sight | `eyePos`, `eyeDirection`, `aimPos`, `checkVisibility` | A physical candidate is observable only when inside the configured view cone and `checkVisibility` in `VIEW` LOD meets the threshold. The target and observer are ignored in the visibility calculation. |
| Identity | `netId` | A useful mission-scoped source key when non-empty. Official documentation does not promise persistence across deletion/recreation, respawn, save restore, or mission reload. Raw values remain local and never enter OpenAI context. |

Bohemia documentation does **not** establish a universal HQ visibility model,
an official sensor-to-confidence conversion, an observation fusion distance,
product freshness windows, cross-session object identity, or a guarantee that
every mod terrain supplies complete `Names` or `Grid` config entries. Those are
explicit product policies below, and missing config is reported rather than
guessed.

## Initial mission knowledge

Immediately after a valid handshake, Papa Bear may know only:

1. the privacy-safe mission and session boundary;
2. world name and world size;
3. player pose/environment state already allowed by Milestones 1-2;
4. own-side friendly-force and automated-test-only mission capability state
   already allowed by Milestone 3;
5. the official named-location gazetteer described below; and
6. operational observations actually received in the current mission.

The gazetteer is static cartographic knowledge, not an observation and not
proof that a building, road, vehicle, unit, fortification, supply point, or
other object currently exists at a named location. No other terrain or object
knowledge is initialized.

## Official named-location gazetteer

### Collection and persistence

The addon reads only direct child classes of
`CfgWorlds.<worldName>.Names`. Each valid entry contains:

```text
configKey       terrain-config key, used locally for deterministic ordering
officialName    config-provided display name, bounded to 160 characters
locationType    config-provided location type, bounded to 64 characters
position        [x,y] world metres
size            optional [radiusA,radiusB] in metres
```

The result is sorted by normalized official name, type, and position, split
into pages of at most 64 locations, and sent once per session plus
one 30-second retry. The desktop assembles every page before replacing a
gazetteer. An incomplete transfer never deletes a valid cache.

The persistent gazetteer SQLite database is map-scoped, not mission-scoped. A
SHA-256 fingerprint is computed over schema version, normalized world name,
world size, grid metadata, and the complete sorted normalized location list.
A changed fingerprint creates/replaces that world's cached gazetteer
transactionally. World names and config keys are hashed for local database
keys; no
Windows account path is exposed to OpenAI.

Gazetteer readiness is `unavailable`, `receiving`, `ready`, or `failed`.
`ready` means a complete, validated page set is committed; it does not imply
knowledge of any non-gazetteer map feature.

### Gazetteer event schema v1

Schema: `arma-ai-bridge/arma3/map-gazetteer-v1`

```json
{
  "schema": "arma-ai-bridge/arma3/map-gazetteer-v1",
  "messageId": "message-000101",
  "missionId": "m4-live",
  "sessionId": "session-a1b2",
  "timestamp": 1.0,
  "sequence": 101,
  "gazetteerId": "gazetteer-000001",
  "pageIndex": 0,
  "pageCount": 1,
  "world": {
    "name": "Stratis",
    "sizeMeters": 8192,
    "grid": {
      "format": "arma-map-grid",
      "samples": [
        { "position": [0, 0], "grid": "000000" },
        { "position": [4096, 4096], "grid": "040040" },
        { "position": [8191, 8191], "grid": "081081" }
      ]
    }
  },
  "locations": [
    {
      "configKey": "StratisAirBase",
      "officialName": "Stratis Air Base",
      "locationType": "NameLocal",
      "position": [1910.0, 5685.0],
      "size": [500.0, 300.0]
    }
  ]
}
```

Required objects are closed and bounded. Page count is at most 64, total
locations at most 4096, coordinates must be finite and within a tolerant world
bound, and duplicate config keys or normalized records are rejected.

## Visibility and authority policy

### Observer eligibility

The maximum viewer boundary is the handshake's `playerSide`, narrowed by the
existing `AAB_friendlyForceVisibility` policy:

- `own-group`: living units in the player's group;
- `own-side`: living units whose group side equals `playerSide`.

A dismounted eligible unit is an observer. A vehicle is an observer only when
it contains at least one living eligible friendly crew member; one canonical
crew observer represents that vehicle for a collection turn. Dead,
incapacitated, null, opposing-side, empty, and mission-registered-but-uncrewed
vehicles are not observers.

Observers are visited round-robin. At most one observer is evaluated every
500 ms, at most 32 side-known contacts and 48 physical candidates are
considered, and at most 24 observations are emitted in one batch. A human
observer's physical search radius is 350 m; a crewed ground/ship vehicle's is
700 m; an aircraft observer's is 1,200 m. These are product resource limits,
not claims about engine sensor range.

### Target eligibility

Enemy units and crewed enemy vehicles are eligible only through that
observer's `targets`/`targetKnowledge`. The event carries the estimated
knowledge position and documented error margin, never a substitute exact
position obtained from the target object.

The physical-candidate path is limited to:

- unoccupied `LandVehicle`, `Air`, and `Ship` objects;
- `ReammoBox_F` and `WeaponHolderSimulated` supplies/weapons;
- uncrewed `StaticWeapon` objects;
- bounded tactical static types `Fortification`, `BagFence_base_F`,
  `HBarrier_base_F`, and `Land_BagBunker_base_F`.

A candidate containing any crew is rejected by this path. A candidate must be
within the observer-specific radius, within a 120-degree forward cone, and
have `checkVisibility` result at least `0.55`. The candidate type and state are
recorded only after those checks. An already known physical object may receive
a later changed/destroyed observation only when it passes the same observation
checks again. Disappearance or deletion alone is not evidence of destruction.

The collector must not call `allMissionObjects`, export `vehicles`, enumerate
another side, use curator/server dumps, call remote code, or infer target
presence from map/config data. It must not serialize candidates that failed
the eligibility checks.

### Provenance classes

Every observation has exactly one primary provenance:

- `visual`: bounded observer cone plus `checkVisibility`;
- `sensor`: observer target knowledge with sensor evidence and no current
  unit sight claim;
- `side-knowledge`: group/side target knowledge available to the eligible
  observer;
- `player-report`: an explicit present/past typed report accepted locally;
- `mission-report`: a record from an explicit, versioned, mission-authorized
  report registry.

`visual`, `sensor`, and `side-knowledge` are selected from the evidence fields
available in the observation. The desktop never upgrades provenance based on
model prose. Mission text and reports are untrusted data and cannot alter the
policy or tool set.

## Observation protocol

### Handshake feature additions

The existing protocol stays 1.0. The handshake adds optional features:

```json
{ "name": "map-gazetteer", "version": 1 }
{ "name": "operational-observations", "version": 1 }
```

An older desktop ignores these features/events; an older addon continues to
work with all Milestone 1-3 behavior.

### Operational observation batch v1

Schema: `arma-ai-bridge/arma3/operational-observation-batch-v1`

```json
{
  "schema": "arma-ai-bridge/arma3/operational-observation-batch-v1",
  "messageId": "message-000102",
  "missionId": "m4-live",
  "sessionId": "session-a1b2",
  "timestamp": 12.5,
  "sequence": 102,
  "batchId": "observation-batch-000021",
  "observations": [
    {
      "observationId": "source-observation-000301",
      "sourceEntityId": "net:2:14",
      "targetEntityId": "net:2:87",
      "provenance": "visual",
      "entityKind": "vehicle",
      "classification": "C_Offroad_01_F",
      "displayName": "Offroad",
      "perceivedSide": "UNKNOWN",
      "observedAt": 12.5,
      "position": [3230.0, 4810.0, 0.0],
      "positionErrorMeters": 3.0,
      "state": "intact",
      "alive": true,
      "confidenceBasis": "los-confirmed",
      "correlationHint": "",
      "retractsObservationId": ""
    }
  ]
}
```

Batches contain at most 24 closed records. Required strings are enum/length
bounded, positions and uncertainty are finite and bounded, and source IDs are
local transport correlation keys. A batch from the wrong session, a duplicate
observation ID, or an out-of-order protocol sequence is rejected without
logging payload contents.

Mission reports, if used, must be explicitly published by mission code through
`AAB_operationalReports` with a unique privacy-safe report ID, allowed viewer
sides, typed classification, position/uncertainty, report time, and optional
retraction ID. Merely placing an object or variable in the mission does not
authorize discovery. Mission-report publication is read-only and cannot
execute a capability. `allowedSides` must be a non-empty closed side list; a
missing, empty, malformed, or non-matching list fails closed for that viewer.
Each accepted registry entry must declare
`schema = "arma-ai-bridge/mission-report-v1"`; duplicate report IDs in one
collection pass are ignored after the first accepted record.

## Local entity and observation schemas

### Entity

```text
entityAlias              mission-scoped local alias (e.g. vehicle-004)
entityKind               contact | vehicle | supply | weapon | fortification | static | other
identityHash             SHA-256(session scope + source identity), never exposed
identityQuality          stable-mission | best-effort | fused-report
classification           bounded engine class or player-report category
displayLabel             privacy-safe class display label or local alias
perceivedSide            WEST | EAST | GUER | CIV | UNKNOWN
firstObservedGameTime
firstReceivedUtc
lastObservedGameTime
lastReceivedUtc
lastKnownPosition        optional x/y/z
positionErrorMeters      deterministic uncertainty radius
freshnessClass           live | recent | stale | historical
confidence               deterministic 0..1
state                    intact | damaged | destroyed | changed | unknown
conflictCount
corroborationCount
isRetracted              derived only when every active observation is retracted
```

### Observation

```text
observationAlias         mission-scoped immutable alias
entityAlias              resolved entity
sourceAlias              observer/report source alias
provenance               visual | sensor | side-knowledge | player-report | mission-report
sourceObservationHash    deduplication key, never exposed
observedAtGameTime
receivedAtUtc
position                 optional x/y/z
positionErrorMeters
baseConfidence
classification
perceivedSide
state
statementSummary         bounded, privacy-scrubbed typed report summary only
corroborates             observation aliases
contradicts              observation aliases
constraintLocationAlias optional official-location alias
constraintPosition       optional official-location x/y
constraintRadiusMeters   optional official-location radius
supersedes               optional prior player observation
retractedAtUtc           optional
```

The SQLite database also stores migration version, mission/session hashes,
world name, and session creation time. Raw JSON, prompts, profile names, UIDs,
raw net IDs, and Windows user names are never stored.

## Identity, deduplication, and fusion

1. A non-empty engine source ID is hashed with the active mission/session
   scope before persistence. The hash maps to one alias for that session.
2. `netId` is `stable-mission` only while the same source object exists in the
   same session. Fallback collector IDs are `best-effort`.
3. A source observation is idempotent by a local hash of session and the
   source observation ID. The source is responsible for unique immutable IDs;
   the typed payload remains as immutable evidence under that hash.
4. Player/mission reports initially create a `fused-report` entity unless they
   explicitly correct an existing observation alias.
5. A report may fuse with an engine entity when entity kind/classification are
   compatible, observation times are within 120 seconds, and the distance
   between positions is no greater than the sum of both uncertainty radii plus
   25 m. The older alias redirects locally to the surviving engine-backed
   alias; historical observations remain unchanged.
6. Engine entities with different non-empty source identity hashes never fuse
   solely because they are nearby.
7. A repeated source observation ID is deduplicated. Same-source updates with
   new IDs remain temporal evidence but do not count as corroboration.
   Different eligible sources produce corroboration.

## Position, uncertainty, confidence, and freshness

### Position and uncertainty

- Side-known contacts retain the engine's `targetKnowledge` estimated position
  and error margin, with a minimum error of 5 m.
- LOS-confirmed physical objects use the observed ATL position with 3 m base
  uncertainty for vehicles and 5 m for static/supply/weapon objects.
- Mission reports must supply an uncertainty radius of 1-5,000 m.
- Player range/bearing reports are resolved from the latest live player ATL
  position and the requested reference: absolute north or relative current
  view/body heading. World X is east and Y is north:
  `x = originX + sin(bearing) * range`,
  `y = originY + cos(bearing) * range`.
- Player-report uncertainty is
  `max(10, rangePrecision/2, range * sin(bearingPrecisionDegrees))`, then plus
  the player's current position uncertainty. Defaults are 50 m range precision
  and 15 degrees bearing precision; exact values accepted from a typed report
  are clamped to product bounds.
- A named location can contribute a second constraint radius using its
  configured size. If it does not intersect the report's range/bearing circle,
  both constraints are stored as contradictory; the named location never
  silently moves or replaces the reported coordinate.

### Deterministic base confidence

| Provenance | Base confidence |
| --- | ---: |
| LOS-confirmed visual | 0.90 |
| Current unit/group side knowledge with engine error | 0.75 |
| Sensor/aged side knowledge | 0.65 |
| Mission-authorized report | 0.70 |
| Explicit player report | 0.45 |

Each independent compatible corroborating source adds `0.10 * (1-current)` up
to 0.98. Each unresolved conflicting active observation multiplies confidence
by 0.75, to a floor of 0.10. Retractions contribute neither confidence nor
conflict after their retraction time. These weights are product policy.

### Freshness and last-known state

| Observation/entity kind | Live | Recent | Stale | Historical |
| --- | --- | --- | --- | --- |
| Visual physical object | <= 5 s | <= 30 s | <= 180 s | > 180 s |
| Side/sensor contact | <= 10 s | <= 60 s | <= 180 s | > 180 s |
| Player/mission report | <= 30 s | <= 300 s | <= 1,800 s | > 1,800 s |

Freshness is calculated from injected desktop time and the last active
observation receive time. Confidence is multiplied by `1.0`, `0.85`, `0.55`,
or `0.25` for live, recent, stale, or historical state. A stale/historical
position is always labeled `last-known`; it is not projected forward. Omission
from a batch, loss of sight, or pipe disconnect never deletes or destroys an
entity. Historical entities stay in diagnostics and are excluded from OpenAI
unless a query explicitly requests history.

## Corrections, contradictions, and retractions

Observations are append-only. A correction creates a new player observation
with `supersedes`; the prior observation receives a retraction timestamp but
is retained. A pure retraction marks the selected player observation inactive
and recomputes the entity. Only player-report observations may be changed by
the player correction tool. Engine and mission evidence cannot be rewritten by
the model or player tool.

Compatible later observations corroborate. Incompatible class/side/state or
non-overlapping uncertainty regions within the comparison window create
bidirectional contradiction links. The query and diagnostics surfaces return
the active alternatives and conflict count; reconciliation does not select a
winner without new evidence or explicit correction.

## Controlled typed-report ingestion

`record_player_observation` is the sole Milestone 4 tool that creates a new
player-reported entity; `correct_player_observation` is its controlled
append-only correction/retraction companion. The record tool accepts an
exact `sourceQuote` from the latest typed user turn, `timeReference`
(`present|past`), a closed entity kind, bounded summary, range, bearing,
bearing reference (`absolute|view|body`), precision fields, and optional exact
named-location reference. Local validation requires:

- the quote is a literal substring of the current user turn;
- the turn is an affirmative first-person report, not a question, conditional,
  hypothetical, recommendation, or model assumption;
- supplied numeric range/bearing values or directional terms are present in
  the quote and within bounds;
- a live player position/heading exists;
- no identity/private fields or executable text are accepted.

Failure returns a safe tool error and writes nothing. Typed UI text enters this
same `IPlayerReportIngestor` boundary; a future final AssemblyAI transcript may
call that boundary, but no audio or voice code is added now.

`correct_player_observation` accepts a player observation alias, exact current
turn quote, `correct|retract`, and (for a correction) the same closed location
fields. It rejects non-player provenance and cross-session aliases.

## Local tools and prompt boundary

- `find_named_locations`: read-only; required `query`, `maxDistanceMeters`,
  and `limit`; blank query means nearest to the current player, otherwise a
  normalized substring search. Returns only official name/type/position/size,
  distance, bearing, and gazetteer fingerprint alias.
- `query_operational_memory`: read-only; required kind, optional bounded
  radius around the current player, freshness selection, conflict inclusion,
  and limit. Returns aliases, last-known labels, provenance summaries,
  freshness, confidence, uncertainty, corroboration, and contradictions.
- `record_player_observation`: controlled local write under the rules above.
- `correct_player_observation`: controlled local correction/retraction under
  the rules above.

Every OpenAI function uses strict JSON Schema with all properties required and
`additionalProperties: false`; nullable values use explicit null unions. Tool
arguments are validated again locally. The OpenAI system instruction states
that write tools are allowed only for explicit player reports and never for
questions or hypotheticals. Only bounded tool results enter context. The full
gazetteer, database, raw event batches, raw source IDs, player/profile names,
UIDs, prompts, and report history never enter OpenAI context or logs.

## Persistence boundaries and failure recovery

- Gazetteer: persistent across missions and sessions for the exact fingerprint.
- Operational memory: one database per privacy-safe mission hash. Each
  handshake session is a separate partition; no engine identity or last-known
  state fuses across sessions. Reopening the desktop during the same live
  session reuses that partition and idempotently resumes from stored evidence.
- A new session ID creates a clean active partition while retaining prior data
  only for local diagnostics/retention. OpenAI tools default to the active
  session and never expose source session/mission IDs.
- Map or mission mismatch closes the active writer before opening the new
  database. Transactions make entity and observation updates atomic.
- Corrupt/incompatible databases are left untouched, status becomes `failed`,
  and a redacted error is shown. No silent destructive migration occurs.
- Schema migration v1 creates metadata, entities, observations,
  observation_links, alias_redirects, and indexes on active session, entity,
  received time, provenance, freshness inputs, and x/y bounds. No static-map
  object tables or spatial tile R-tree is created.
- Default operational-memory retention is the current mission plus explicitly
  local diagnostics. There is no cloud synchronization.

## Diagnostics

Add a read-only **Operational Memory** tab showing:

- active session alias, world, database/gazetteer status, schema version, and
  redacted local database path;
- gazetteer fingerprint alias and location count;
- entity and observation counts by kind, provenance, and freshness;
- bounded entity rows with alias, last-known position, uncertainty, state,
  confidence, freshness, corroboration, and conflict count;
- bounded observation rows with source alias, provenance, times,
  uncertainty, contradiction/corroboration links, supersession, and retraction;
- last accepted observation batch and any validation/database error code.

It displays no raw source ID, UID, player/profile name, prompt, API key, or full
report text and has no mutation controls.

## Deterministic automated acceptance

The Windows suite must prove:

1. Main contains no full-map tile/static-index protocol, terrain/building/road/
   vegetation scan, `allMissionObjects`, mission-wide `vehicles` export, or
   static-map object SQLite tables.
2. Gazetteer and observation schemas require the common versioned envelope,
   closed/bounded records, exact discriminators, finite coordinates, paging,
   and checked-in valid fixtures; privacy fields are rejected.
3. SQF fixtures show `CfgWorlds.<world>.Names` gazetteer collection and bounded
   observer iteration, fixed candidate classes/radii/counts, forward-cone and
   LOS checks, crew rejection, and no opposing-side enumeration.
4. Gazetteer pages commit atomically, fingerprint deterministically regardless
   of source order, persist/reuse, reject incomplete/mismatched pages, and do
   not contain non-name static objects.
5. SQLite migration v1 is idempotent and creates only operational-memory and
   gazetteer structures; a session reset partitions active evidence.
6. Raw net IDs and fallback IDs hash to stable session aliases, are not stored
   verbatim, and never occur in diagnostics/OpenAI results.
7. Duplicate batches/observations are idempotent. Same-source repeats do not
   corroborate. Independent compatible observations do.
8. Report-to-engine fusion follows kind/time/uncertainty thresholds;
   distinct engine identities do not proximity-fuse.
9. Position resolution for absolute and relative bearing is deterministic;
   uncertainty follows declared precision; contradictory named-location and
   range/bearing constraints remain visible.
10. Freshness and confidence advance with injected time. Omission retains a
    stale last-known state and never infers deletion/destruction.
11. Corrections append and supersede, retractions retain history, conflicts
    lower confidence, and later compatible evidence can resolve/corroborate.
12. Player-report writes are accepted only for exact explicit present/past
    report text. Questions, hypotheticals, invented numeric arguments,
    cross-session aliases, and corrections of engine evidence write nothing.
13. All four strict OpenAI tools are advertised and locally routed; bounded
    outputs contain no raw source IDs, profile names, UIDs, full database,
    raw batch, or arbitrary executable field.
14. Existing Milestone 1 direct/multi-tool/cancellation behavior, Milestone 2
    telemetry/reset/snapshot behavior, Milestone 3 force/capability behavior,
    and manual `query_environment` tests remain green.
15. Repository verification, Release tests, WPF publish, x64 native build, and
    PBO packaging pass on Windows.

## Exact live Arma acceptance

### Mission setup

Use a local/private Eden mission on Stratis with `AAB_missionId =
"m4-observational-memory-live"`, a playable WEST player, one additional WEST
AI observer named `aabObserver`, one empty civilian Offroad named
`aabObservedVehicle`, and one EAST infantry unit named `aabHiddenEnemy`.
Place the Offroad 200 m behind an opaque building from the player and at least
500 m from the additional observer. Place the EAST unit behind terrain where
neither WEST unit has target knowledge. Do not register support assets or
mission capabilities.

### Acceptance sequence

1. Build/package the PBO, native extension, and WPF app; start the app and the
   packaged addon. Verify handshake features include gazetteer and operational
   observations while all Milestone 3 features remain healthy.
2. Open Operational Memory. Verify a ready Stratis gazetteer with official
   names and map/grid metadata, but zero map buildings, roads, terrain tiles,
   vegetation tiles, or pre-seeded operational objects.
3. Before moving either friendly, query for vehicles. Verify the unseen empty
   Offroad is unknown and `aabHiddenEnemy` never appears.
4. Move `aabObserver` to within 100 m facing the Offroad with unobstructed LOS.
   Within three seconds verify a privacy-safe vehicle alias appears with
   visual provenance, approximately 3 m uncertainty, and no raw engine ID.
5. Move the observer away/behind the building without deleting the Offroad.
   After more than 30 seconds verify the entity is stale as defined, retains a
   clearly labeled last-known position, and is not reported destroyed.
6. Face the Offroad's last-known bearing and type an explicit report such as:
   `I saw an empty offroad 200 metres ahead 60 seconds ago.` Verify
   `record_player_observation` creates a
   separate lower-confidence player observation at the deterministic position
   from the player's current view heading and stores report uncertainty.
7. Move `aabObserver` until the reported Offroad is visually confirmed inside
   the report uncertainty region. Verify the friendly visual observation fuses
   with/corroborates the report, confidence rises, both observation records
   remain visible, and the entity alias is stable.
8. Type a question or hypothetical (`Could there be a truck 400 metres east?`)
   and verify no write tool commits an observation. Then explicitly correct or
   retract the prior player report and verify append-only history and recomputed
   state.
9. Reveal then forget/occlude a controlled EAST test target only if needed to
   verify the side-knowledge path. At every earlier step, confirm the hidden
   opposing unit never appears. No debug enumeration may be used as evidence.
10. Ask for a named location and known operational objects. Verify
    `find_named_locations` and `query_operational_memory` return only bounded
    retrieved results. Run the existing friendly-force tools and manual
    `query_environment`; verify unchanged behavior and stateless tool rounds.
11. Restart only the desktop during the same session and verify idempotent
    operational-memory recovery. Restart the mission and verify a new session
    begins with no active operational entities while the Stratis gazetteer is
    reused.
12. Inspect diagnostics and logs. Confirm no raw net ID, player/profile name,
    UID, full prompt, full database, raw event payload, API key, arbitrary SQF,
    support execution, or hidden opposing-side entity appears.

Live acceptance passes only when the full sequence succeeds without UI freeze,
pipe disconnect, addon script error, visibility-policy breach, or regression
of Milestones 1-3. The PR remains draft until this live mission is completed.

## Official verification limits to report

The implementation can cite official Bohemia documentation for the commands
and config shapes in the audit and official OpenAI documentation for strict
function tools and call-ID-correlated outputs. It cannot verify from official
documentation:

- complete/correct `Names` or `Grid` config data in every third-party terrain;
- a cross-respawn or cross-session lifetime for `netId`;
- that product observer radii/FOV/LOS thresholds exactly reproduce human or AI
  perception;
- the product confidence/fusion/freshness/uncertainty weights;
- the semantic truth of mission-authored or player-authored reports.

These remain explicit, deterministic, testable product contracts. Missing
official evidence must be reported in the draft PR rather than presented as an
engine guarantee.

### Official references used

- Bohemia Interactive: [`CfgWorlds` config reference](https://community.bohemia.net/wiki/CfgWorlds_Config_Reference),
  [`worldSize`](https://community.bohemia.net/wiki/worldSize),
  [`mapGridPosition`](https://community.bohemia.net/wiki/mapGridPosition), and
  [`nearestLocations`](https://community.bohemia.net/wiki/nearestLocations).
- Bohemia Interactive: [`targets`](https://community.bohemia.net/wiki/targets),
  [`targetKnowledge`](https://community.bohemia.net/wiki/targetKnowledge), and
  [`knowsAbout`](https://community.bohemia.net/wiki/knowsAbout).
- Bohemia Interactive: [`nearestObjects`](https://community.bohemia.net/wiki/nearestObjects),
  [`eyeDirection`](https://community.bohemia.net/wiki/eyeDirection),
  [`checkVisibility`](https://community.bohemia.net/wiki/checkVisibility), and
  [`netId`](https://community.bohemia.net/wiki/netId).
- OpenAI: [function calling with strict tools and function-call outputs](https://developers.openai.com/api/docs/guides/function-calling).
