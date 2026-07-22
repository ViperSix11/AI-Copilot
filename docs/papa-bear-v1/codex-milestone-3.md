# Codex Milestone 3: Friendly Force Picture and Mission Capabilities

## Objective

Milestone 3 adds a read-only, own-side force picture and a typed mission
capability registry to the existing local world model. The Arma client addon
publishes session metadata, friendly-force deltas, periodic paged
reconciliations, and mission-declared capabilities over the existing duplex
Named Pipe. The desktop application validates and reduces those messages into
mission-scoped entities, exposes privacy-minimized local read tools, and shows
the result in World State diagnostics.

The existing `telemetry-v1` player feed, manual `query_environment` command,
and stateless OpenAI Responses tool loop remain available. This milestone does
not execute support actions.

## Scope and non-goals

In scope:

- an explicit mission/session identity and protocol feature handshake;
- own-side groups, units, occupied/crewed vehicles, and explicitly registered
  support assets;
- event-driven deltas plus periodic full reconciliation;
- typed, read-only mission capabilities;
- mission-scoped identities, provenance, timestamps, confidence, and freshness;
- purpose-specific OpenAI read tools and read-only diagnostics;
- deterministic desktop, schema-contract, and SQF fixture tests;
- a rebuilt and packaged PBO.

Out of scope:

- ACE integration or ACE medical data;
- enemy collection beyond the existing perspective-bound known-contact feed;
- static map indexing;
- voice input or output;
- support execution, asset assignment, waypoint mutation, or task mutation;
- arbitrary SQF or other arbitrary code execution;
- server-wide export or anti-cheat bypass.

## Existing implementation audit

### SQF telemetry

The client addon runs only when `hasInterface` is true. `fn_postInit.sqf`
starts one 4 Hz loop which calls `fn_collectTelemetry.sqf`, and a 10 Hz loop
which polls inbound commands. The telemetry collector publishes
`arma-ai-bridge/arma3/telemetry-v1` with mission time, frame number, map,
player, the player's occupied vehicle, known contacts, and sensor contacts.
Known contacts and sensor contacts are cached at 1 Hz. The only inbound command
is the validated, bounded `query_environment` request.

The current feed has no explicit mission/session ID, no protocol feature
advertisement, no stable group or occupied-vehicle ID, no friendly-force
collection, no tombstones, and no reconciliation marker. It also collects
`getPlayerUID` and `name` even though Milestone 2 intentionally excludes them
from OpenAI snapshots. Milestone 3 stops collecting those two fields because
no local feature uses them.

The known-contact collectors remain unchanged in authority: they use player,
group, and current-vehicle sensor knowledge rather than unrestricted enemy
object positions. Friendly collection is a separate message family and is
never added to each 4 Hz player telemetry envelope.

### Named Pipe and native transport

The native extension is transport-focused. It accepts newline-delimited UTF-8
messages, writes in 64 KiB chunks, keeps a 256-entry outbound queue, and
reconnects asynchronously. The WPF server accepts duplex lines up to 1 MiB.
Arma's official `callExtension` documentation states that input sent to an
extension has no current size limit; the documented 10,240-byte limit applies
to extension return data, not the outbound JSON used here.

There is one demonstrated transport defect for Milestone 3: every
`telemetry|...` extension call is tagged as a replaceable telemetry snapshot.
Enqueueing another such message replaces the newest queued telemetry entry.
If handshakes, force deltas, reconciliation pages, and capability registries
used that prefix, semantically independent messages could silently overwrite
one another before the pipe writes them. `query-result|...` is reliable but is
reserved for correlated command responses and is not an appropriate
unsolicited event channel.

The minimal transport change is therefore one generic `event|...` extension
prefix. It uses the existing non-coalescing outbound queue and carries opaque
JSON without interpreting game semantics. `telemetry|...`, `query-result|...`,
inbound polling, pipe framing, limits, and reconnection behavior remain intact.
Periodic full reconciliation repairs any event lost to the bounded queue or a
disconnect.

### Desktop world model and OpenAI loop

Milestone 2's `TelemetryIngestService` validates `telemetry-v1` and applies
atomic observations to `WorldStateStore`. The store owns current player/map/
group/vehicle state, known-contact aliases, freshness, confidence, and local
session-reset heuristics. `WorldSnapshotBuilder` creates minimized snapshots;
the raw telemetry envelope is rejected by `OpenAiAssistantService`. The
Responses loop uses strict tools, `store: false`, local validation, replay of
all response output items, and `function_call_output` correlation by
`call_id`.

Milestone 3 extends this pipeline rather than adding a second store. Explicit
handshake identity supersedes the Milestone 2 map/time/frame reset heuristic
when available; the heuristic remains as backward compatibility for an older
addon. Local read tools query `WorldStateStore` directly. Only
`query_environment` still crosses the pipe as an inbound Arma command.

## Visibility and authority policy

The following policy is exact and is enforced in the SQF collector before
serialization:

1. The collector runs on the local interface client and takes `playerSide` as
   the maximum authority boundary. A mission setting may narrow this boundary
   but cannot broaden it to another side.
2. `AAB_friendlyForceVisibility` may be `"own-side"` (default) or
   `"own-group"`. `own-side` considers units returned for `playerSide` and
   groups whose `side` equals `playerSide`; `own-group` considers only the
   player's current group and its units.
3. A friendly vehicle is included only when at least one included friendly
   unit occupies it, or when it is an explicitly registered support asset
   whose allowed viewer sides include `playerSide`. The unfiltered `vehicles`
   collection is never exported.
4. A support asset exists only when the mission registers it in
   `AAB_supportAssets`. Registration cannot make an opposing-side crew visible:
   a linked vehicle with living non-friendly crew is rejected.
5. A mission capability exists only when registered in
   `AAB_missionCapabilities`. A capability with a non-empty
   `allowedRequesterSides` list is visible only when that list contains the
   local player's side.
6. The collector exports only data observable on that client or supplied by
   the mission through these registries. It does not use remote execution,
   server object dumps, curator state, opposing-side arrays, or hidden mission
   state.
7. The desktop rejects force/capability messages whose session does not match
   the active handshake. OpenAI receives only local aliases and the bounded
   fields selected by the requested snapshot builder.

Mission authors may set these mission-namespace variables before the addon
post-init completes:

```sqf
AAB_missionId = "west-coop-01";
AAB_friendlyForceVisibility = "own-side";
AAB_supportAssets = [];
AAB_missionCapabilities = [];
```

`AAB_missionId` must be a privacy-safe, mission-defined identifier. If absent,
the addon derives a local fingerprint from `worldName` and
`missionNameSource`; this fallback is not claimed to be a globally unique
mission identifier.

## Protocol envelope

All Milestone 3 event messages are UTF-8 JSON objects with these common fields:

| Field | Type | Rule |
| --- | --- | --- |
| `schema` | string | Exact versioned discriminator. |
| `messageId` | string | Monotonic, session-scoped `message-000001` form. |
| `missionId` | string | Mission-defined ID or documented local fallback. |
| `sessionId` | string | Privacy-safe random/clock-derived ID created once per local mission load. |
| `timestamp` | number | Arma mission `time` at collection. |
| `sequence` | integer | Strictly increasing event sequence within the session. |

Unknown optional fields are ignored. Missing required fields, unknown schema
versions, invalid types, session mismatches, duplicate/out-of-order sequence
numbers, oversized collections, and non-finite numbers are rejected without
logging payload contents.

### Session handshake v1

Schema: `arma-ai-bridge/arma3/session-handshake-v1`

```json
{
  "schema": "arma-ai-bridge/arma3/session-handshake-v1",
  "messageId": "message-000001",
  "missionId": "west-coop-01",
  "sessionId": "session-8f2c-71a0",
  "timestamp": 0.25,
  "sequence": 1,
  "protocol": { "major": 1, "minor": 0 },
  "world": { "name": "Altis", "sizeMeters": 30720 },
  "viewer": { "side": "WEST", "visibility": "own-side" },
  "features": [
    { "name": "player-telemetry", "version": 1 },
    { "name": "environment-query", "version": 1 },
    { "name": "friendly-force-picture", "version": 1 },
    { "name": "mission-capabilities", "version": 1 }
  ]
}
```

The handshake is sent at addon startup and again every 30 seconds. A changed
`sessionId` is an authoritative world-model reset. A changed mission/map under
the same session is invalid and causes the desktop to await a new handshake.
The existing `telemetry-v1` envelope also carries additive `missionId` and
`sessionId` fields so high-rate telemetry cannot be attached to the wrong
session if pipe ordering is disturbed.

### Friendly-force snapshot v1

Schema: `arma-ai-bridge/arma3/friendly-force-snapshot-v1`

```json
{
  "schema": "arma-ai-bridge/arma3/friendly-force-snapshot-v1",
  "messageId": "message-000002",
  "missionId": "west-coop-01",
  "sessionId": "session-8f2c-71a0",
  "timestamp": 1.0,
  "sequence": 2,
  "reconciliationId": "reconcile-000001",
  "pageIndex": 0,
  "pageCount": 1,
  "groups": [],
  "units": [],
  "vehicles": [],
  "assets": []
}
```

Each page contains at most 32 units and bounded companion records. Pages share
one `reconciliationId`, collection time, and page count. The desktop assembles
all pages and atomically replaces the force picture. Missing pages are
discarded after five seconds and mark reconciliation degraded; an incomplete
snapshot never deletes valid current state.

Group record:

```json
{
  "id": "net:2:15",
  "callsign": "Alpha 1-1",
  "side": "WEST",
  "leaderId": "net:2:31",
  "unitIds": ["net:2:31", "net:2:32"],
  "positionATL": [3400.0, 5600.0, 0.0],
  "behaviour": "AWARE"
}
```

Unit record:

```json
{
  "id": "net:2:31",
  "groupId": "net:2:15",
  "callsign": "unit-001",
  "side": "WEST",
  "class": "B_Soldier_F",
  "role": "Rifleman",
  "positionATL": [3400.0, 5600.0, 0.0],
  "alive": true,
  "lifeState": "HEALTHY",
  "mobile": true,
  "damage": 0.0,
  "vehicleId": null,
  "vehicleRole": "",
  "medicalReadiness": "base-arma-healthy"
}
```

Vehicle record:

```json
{
  "id": "net:2:70",
  "side": "WEST",
  "class": "B_MRAP_01_F",
  "displayName": "Hunter",
  "positionATL": [3500.0, 5650.0, 0.0],
  "alive": true,
  "mobile": true,
  "damage": 0.0,
  "fuel": 1.0,
  "speedKph": 0.0,
  "crewUnitIds": ["net:2:40"],
  "cargoCapacity": 3,
  "emptyCargoSeats": 2
}
```

Support-asset record:

```json
{
  "id": "asset:eagle-one",
  "kind": "rotary_transport",
  "callsign": "Eagle One",
  "provider": "mission-script",
  "vehicleId": "net:2:80",
  "status": "available",
  "available": true,
  "capacity": 12
}
```

Allowed asset kinds are `rotary_transport`, `ground_transport`, `medevac`,
`resupply`, `reconnaissance`, `vehicle_recovery`, and `other`. Status is one of
`available`, `busy`, `degraded`, `unavailable`, or `unknown`.

### Friendly-force delta v1

Schema: `arma-ai-bridge/arma3/friendly-force-delta-v1`

```json
{
  "schema": "arma-ai-bridge/arma3/friendly-force-delta-v1",
  "messageId": "message-000003",
  "missionId": "west-coop-01",
  "sessionId": "session-8f2c-71a0",
  "timestamp": 2.0,
  "sequence": 3,
  "baseReconciliationId": "reconcile-000001",
  "upsertGroups": [],
  "upsertUnits": [],
  "upsertVehicles": [],
  "upsertAssets": [],
  "removedGroupIds": [],
  "removedUnitIds": [],
  "removedVehicleIds": [],
  "removedAssetIds": []
}
```

Upserts contain complete records for changed entities; removals are explicit
tombstones. Absence from a delta has no meaning. A sequence gap marks the force
picture degraded but later deltas may still be retained; the next complete
snapshot is authoritative and clears the gap. A snapshot omission is an
authoritative removal only after every page is assembled.

### Mission capabilities v1

Schema: `arma-ai-bridge/arma3/mission-capabilities-v1`

```json
{
  "schema": "arma-ai-bridge/arma3/mission-capabilities-v1",
  "messageId": "message-000004",
  "missionId": "west-coop-01",
  "sessionId": "session-8f2c-71a0",
  "timestamp": 2.0,
  "sequence": 4,
  "registryVersion": 1,
  "capabilities": [
    {
      "id": "capability:rotary-transport",
      "capability": "rotary_transport",
      "enabled": true,
      "provider": "mission-script",
      "constraints": {
        "maxConcurrent": 1,
        "allowedRequesterSides": ["WEST"],
        "maxRangeMeters": 20000,
        "maxPassengers": 12,
        "supportsCasualties": false,
        "requiresConfirmation": true
      }
    }
  ]
}
```

Allowed capability kinds match the asset kinds plus `artillery`, `cas`,
`marker_management`, and `task_management`. This milestone reports those
declarations but exposes no action tool. Constraints are a closed, typed
object; arbitrary mission text and arbitrary parameters are not forwarded.
Unknown capability kinds or constraint fields are rejected from the registry
entry without rejecting other valid entries.

## Stable identity rules

All source identifiers are scoped to the handshake `sessionId`.

| Entity | Source identity | Fallback | OpenAI identity |
| --- | --- | --- | --- |
| Player | Existing `player:self`; source `netId player` retained locally when available | `player:self` | `player:self` |
| Unit | Non-empty `netId unit` | Collector-owned monotonic ID retained for that object reference during the session | `unit-001`, `unit-002`, ... |
| Group | Non-empty `netId group` | Collector-owned monotonic ID retained for that group reference during the session | `group-001`, `group-002`, ...; current group may be `group:self` |
| Vehicle | Non-empty `netId vehicle` | Collector-owned monotonic ID retained for that object reference during the session | `vehicle-001`, `vehicle-002`, ... |
| Support asset | Required mission-defined `id`, prefixed `asset:` | Linked vehicle source identity only when the mission ID is absent; identity quality is then best-effort | `asset-001`, `asset-002`, ... plus allowed tactical callsign |
| Capability | Required mission-defined capability ID | Deterministic capability-kind ID when unique | `capability-001`, ... plus capability enum |

The engine documents `netId` as a unique object/group ID and supports both
objects and groups. The documentation does not promise persistence across
respawn, deletion/recreation, saved-game restore, or mission reload, so the
product does not treat it as cross-session identity. Fallback IDs are local
collector identities, not engine guarantees. A respawned/recreated object is a
new entity unless the mission explicitly reuses a support/capability ID.

## Cadence, events, reconciliation, and stale state

- Player telemetry remains 4 Hz. Contacts remain cached at approximately 1 Hz.
- Friendly-force state is evaluated at 1 Hz and immediately after relevant
  `EntityCreated`, `EntityDeleted`, `EntityKilled`, `EntityRespawned`,
  `GroupCreated`, or `GroupDeleted` mission events, rate-limited to one
  collection per 250 ms.
- Changed entities and tombstones are sent as one delta. An empty delta is not
  sent.
- A full paged reconciliation is sent at startup and every 15 seconds.
- The handshake and complete capability registry are sent at startup, on
  detected registry changes, and every 30 seconds.
- A handshake older than 75 seconds (two missed refreshes plus tolerance)
  marks diagnostics degraded without discarding the last local picture.
- A pipe disconnect does not erase state. Ages continue locally. A new
  handshake session resets state atomically.
- Groups, units, vehicles, and assets are `live` through 2 seconds, `recent`
  through 15 seconds, `stale` through 45 seconds, and `historical` afterward.
  Capability state is `live` through 30 seconds, `recent` through 60 seconds,
  `stale` through 120 seconds, and `historical` afterward.
- Stale entities retain their last-known position locally. OpenAI snapshot
  metadata always includes age/freshness/confidence; historical entities are
  omitted. Read tools omit stale entities unless `includeStale` is explicitly
  true.
- Confidence is deterministic: full current observation `1.0`, delta current
  observation `0.95`, then freshness multipliers `1.0`, `0.85`, `0.6`, and
  `0.25`. No ACE medical confidence is implied by base-Arma status.
- A delta sequence gap, incomplete page set, or expired handshake marks
  reconciliation degraded in diagnostics. A complete later reconciliation
  restores healthy state.

## Privacy and prompt-boundary rules

- `name`, `profileName`, `getPlayerUID`, Steam UID, owner/client IDs, and
  Windows/user paths are never collected in the Milestone 3 force messages,
  never logged, and never sent to OpenAI. The unused `uid` and `name` fields are
  removed from the existing player telemetry payload.
- Raw `netId` and fallback source IDs may cross the local Named Pipe solely for
  reconciliation. They remain private store keys, are not logged, are redacted
  from generic UI message display, and are replaced by session aliases in
  diagnostics and all OpenAI snapshots/tool results.
- `groupId` is treated as a mission-defined tactical callsign, not a stable
  identity. It may be sent to OpenAI after length/control-character validation.
- A unit/asset callsign is sent only when supplied by the mission through the
  explicit registry or `AAB_callsign`; `name unit` is never used. Otherwise the
  desktop alias is used.
- Mission-provided callsigns and providers are untrusted data. They are
  length-bounded, treated as facts rather than instructions, and cannot change
  tool policy or enable actions.
- Current-situation prompts contain only summary counts for friendly forces.
  Detailed groups/units/vehicles/assets/capabilities enter context only through
  the purpose-specific read tool selected for the current question.
- `store: false`, stateless replay, strict function schemas, local validation,
  bounded results, and safe tool errors remain unchanged.

## Local read tools and snapshot builders

`query_friendly_forces` accepts required strict fields
`entityType` (`group|unit|vehicle|all`), `maxDistanceMeters` (100-50000),
`includeStale` (boolean), and `limit` (1-100). It returns only matching aliases,
callsigns, operational summaries, provenance, age, freshness, confidence, and
distance from the player.

`query_assets` accepts `kind` (`any` or an allowed asset kind),
`availableOnly`, `maxDistanceMeters`, `includeStale`, and `limit`. It returns
bounded support-asset summaries and linked privacy-safe vehicle summaries.

`query_mission_capabilities` accepts `enabledOnly` and `includeStale`. It
returns the typed registry, reconciliation metadata, and no action handle.

All arguments are required to satisfy OpenAI strict-schema requirements and
validated again locally. These tools read `WorldStateStore`; they never send a
command to Arma. `query_environment` retains its current pipe coordinator and
manual Map Query UI.

## Diagnostics

The read-only World State tab shows:

- connection, local session alias, handshake protocol/features, and reset;
- map/player/current group/current vehicle state from Milestone 2;
- friendly group, unit, vehicle, asset, and capability counts by freshness;
- bounded entity rows using privacy-safe aliases/callsigns only;
- last delta sequence, last complete reconciliation ID/time, pending pages,
  sequence-gap state, and registry version;
- the exact privacy-minimized current-situation snapshot sent before an OpenAI
  turn.

No diagnostics control mutates Arma or the world model.

## Deterministic automated acceptance

The complete Windows suite must prove:

1. All four new JSON schemas parse, expose the exact discriminators, require
   the common envelope fields, close privacy-sensitive objects with
   `additionalProperties: false`, and match checked-in valid wire fixtures.
2. SQF contract fixtures contain no profile name, UID, opposing-side entity,
   action command, or raw executable text field.
3. A handshake creates an explicit session and advertises all four expected
   features; a changed session resets all force/capability entities and aliases.
4. Older `telemetry-v1` without a handshake still passes all Milestone 2 tests;
   telemetry with explicit session identity attaches to the correct session.
5. One complete snapshot atomically creates groups, units, vehicles, and assets
   with stable aliases, timestamps, provenance, freshness, and confidence.
6. Snapshot pages do not change authoritative state until complete; duplicate,
   missing, inconsistent, expired, and cross-session pages are deterministic.
7. Deltas upsert changed entities, apply explicit tombstones, ignore duplicates,
   flag sequence gaps, and recover on full reconciliation.
8. Omitted delta entities remain and age; stale/historical classification and
   confidence decay use an injected `TimeProvider`.
9. Capability registries validate enums and typed constraints, replace
   atomically, filter requester sides, and age deterministically.
10. Friendly force, asset, and capability snapshot builders enforce filters,
    distances, limits, stale policy, and purpose-specific field selection.
11. Player/profile names, UIDs, raw `netId` values, raw fallback IDs, and source
    mission IDs do not occur in OpenAI current-situation snapshots or read-tool
    outputs.
12. OpenAI requests advertise the three new strict read tools, route their
    calls locally by name/call ID, preserve every reasoning/output item across
    rounds, and keep `store: false`.
13. Existing direct-answer, multi-tool `query_environment`, cancellation,
    redaction, map/time/frame reset, and manual query contract tests remain
    green.
14. Repository verification, Release tests, WPF publish, x64 native build, and
    PBO packaging succeed on Windows.

## Exact live Arma acceptance

### Test mission

In Eden Editor on Altis, create a single-player or authorized private
multiplayer WEST scenario with exactly this small force:

1. Infantry Group Alpha: four WEST infantry; make the first unit playable and
   name the fourth `aabHeloPilot` in Eden.
2. Infantry Group Bravo: four WEST infantry, separate from Alpha; name the
   fourth `aabGroundDriver` in Eden.
3. One empty WEST rotary transport named `aabHelo` in Eden.
4. One empty WEST ground vehicle named `aabGround` in Eden.

Set tactical group callsigns to `Alpha 1-1` and `Bravo 1-1`. In mission
initialization, move the named units into the driver seats and publish the
registries shown below (for multiplayer, run the same client-local setup
before the addon's scheduled post-init passes its player/time wait):

```sqf
AAB_missionId = "m3-west-live-acceptance";
AAB_friendlyForceVisibility = "own-side";
aabHeloPilot moveInDriver aabHelo;
aabGroundDriver moveInDriver aabGround;

AAB_supportAssets = [
    createHashMapFromArray [
        ["id", "eagle-one"],
        ["kind", "rotary_transport"],
        ["callsign", "Eagle One"],
        ["provider", "mission-script"],
        ["vehicle", aabHelo],
        ["status", "available"],
        ["available", true],
        ["capacity", 8],
        ["allowedSides", ["WEST"]]
    ]
];

AAB_missionCapabilities = [
    createHashMapFromArray [
        ["id", "rotary-transport"],
        ["capability", "rotary_transport"],
        ["enabled", true],
        ["provider", "mission-script"],
        ["constraints", createHashMapFromArray [
            ["maxConcurrent", 1],
            ["allowedRequesterSides", ["WEST"]],
            ["maxRangeMeters", 20000],
            ["maxPassengers", 8],
            ["supportsCasualties", false],
            ["requiresConfirmation", true]
        ]]
    ],
    createHashMapFromArray [
        ["id", "ground-transport"],
        ["capability", "ground_transport"],
        ["enabled", true],
        ["provider", "mission-script"],
        ["constraints", createHashMapFromArray [
            ["maxConcurrent", 1],
            ["allowedRequesterSides", ["WEST"]],
            ["maxRangeMeters", 10000],
            ["maxPassengers", 3],
            ["supportsCasualties", false],
            ["requiresConfirmation", true]
        ]]
    ]
];
```

### Acceptance sequence

1. Build with `scripts/build.ps1`, confirm a new PBO exists under
   `artifacts/mod/@Arma_AI_Bridge/addons`, copy/use that packaged mod, and start
   the desktop app before previewing the mission.
2. Open World State. Within five seconds verify a healthy protocol 1.0
   handshake, mission/session present, features for telemetry, environment
   query, force picture, and capabilities, and a complete reconciliation.
3. Verify exactly two friendly groups, eight units, two vehicles, one
   `Eagle One` asset, and two enabled capabilities. The designated pilot and
   driver remain members of Alpha and Bravo, so Eden must not create extra
   crew for this acceptance mission.
4. Move Alpha at least 100 m. Within two seconds verify unit/group position
   changes arrive as a delta while unchanged Bravo identities remain stable.
5. Enter and exit `aabGround`. Verify the player's Milestone 2 occupied-vehicle
   state still updates at 4 Hz and the friendly vehicle alias does not change.
6. Kill one Bravo unit, then delete another in Zeus/debug console in the
   controlled test. Verify event-driven status/removal, no opposing-side data,
   and recovery to a healthy full reconciliation within 15 seconds.
7. Run `(AAB_supportAssets select 0) set ["status", "busy"];`
   `(AAB_supportAssets select 0) set ["available", false]; AAB_forceDirty = true;`
   on the local client. Verify a delta appears within two seconds and
   `query_assets` reports it unavailable without executing any action.
8. Ask: “Where are our friendly groups?”, “Which transport assets are
   available?”, and “What mission capabilities are enabled?” Verify the three
   corresponding local read tools are used, answers match diagnostics, and
   stale age is disclosed when material.
9. Run the existing manual Map Query and ask a terrain-dependent question.
   Verify `query_environment` still crosses the pipe and the stateless tool
   loop completes.
10. Inspect the OpenAI snapshot/tool diagnostics and application log. Confirm
    no profile name, UID, raw `netId`, source session/mission ID, full raw force
    database, API key, prompt, or tool result is present.
11. Stop/restart the desktop app during the same mission. Verify periodic
    handshake/reconciliation restores the picture. Restart the mission on
    Altis and verify a new session causes an atomic reset even though the map
    and mission ID are unchanged.

Live acceptance is complete only when all steps pass without UI freeze, pipe
disconnect, addon script error, support execution, or changed enemy-knowledge
behavior.

## Official documentation verification and limits

Official Bohemia documentation verifies:

- [`netId`](https://community.bohemia.net/wiki/netId) returns a unique ID for
  an object or group and supports both types;
- [`units`](https://community.bohemia.net/wiki/units) accepts a side and returns
  units belonging to that side;
- [`allGroups`](https://community.bohemia.net/wiki/allGroups) returns created
  groups for the regular sides;
- [`vehicles`](https://community.bohemia.net/wiki/vehicles) is local to the
  current client and includes empty and crewed vehicles, which is why this
  design does not export it wholesale;
- [`side`](https://community.bohemia.net/wiki/side) has special behavior for
  empty vehicles and dead/captive units, which is why group membership and
  friendly crew are used for authority checks;
- [`fullCrew`](https://community.bohemia.net/wiki/fullCrew) provides crew roles
  and can include empty seats;
- [mission event handlers](https://community.bohemia.net/wiki/Arma_3%3A_Event_Handlers/addMissionEventHandler)
  include entity creation/deletion/killed/respawn and group creation/deletion;
- [`missionNameSource`](https://community.bohemia.net/wiki/missionNameSource)
  identifies the loaded mission source but may be empty for older missions;
- [`callExtension`](https://community.bohemia.net/wiki/callExtension) currently
  has no outbound input-size limit and documents the separate return-size cap.

Official OpenAI [function-calling documentation](https://developers.openai.com/api/docs/guides/function-calling)
verifies strict JSON function tools, required fields with
`additionalProperties: false`, `call_id`-correlated `function_call_output`,
and replaying reasoning items for reasoning-model tool rounds. Current
[reasoning documentation](https://developers.openai.com/api/docs/guides/reasoning)
says stateless `store: false` responses return encrypted reasoning by default
and still accepts the legacy `reasoning.encrypted_content` include value, so
Milestone 3 preserves the already accepted Milestone 1 continuation pattern
rather than changing it.

Official documentation does **not** establish:

- a globally unique or cross-respawn/cross-mission lifetime for Arma `netId`;
- a built-in stable mission UUID suitable for this product;
- a built-in friendly-HQ visibility/authority policy;
- a built-in support-asset or mission-capability registry/taxonomy;
- product cadence, paging size, freshness thresholds, confidence weights,
  alias rules, or stale-state wording;
- that `groupId` is a unique or immutable identity rather than a tactical
  display callsign;
- stability of any fallback based on an object's string representation;
- ACE medical readiness (explicitly out of scope here).

Those items are explicit, testable product contracts in this milestone, not
claims about engine guarantees. The OpenAI developer-docs connector could not
be installed in the implementation environment because the Codex CLI returned
Windows `Access is denied`; the current official OpenAI web documentation was
used as the documented fallback.
