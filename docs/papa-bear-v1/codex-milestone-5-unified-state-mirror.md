# Codex Milestone 5: Unified State Mirror & Interpreter

## Objective and release boundary

Release 0.8 is the **Unified State Mirror & Interpreter**. It preserves the
accepted 0.7 voice path and the already implemented Phase A named-location,
position-interpretation and response-profile work, then adds one bounded
periodic Arma state message and a local SQLite current-state mirror. Every
operational assistant turn receives one frozen, fixed, bounded compact snapshot
and an immediate local English radio acknowledgement. The acknowledgement and
final answer use the current Arma player-group callsign, never a configured or
hardcoded substitute.

The release is user-initiated only. State changes never invoke OpenAI,
ElevenLabs or playback. This is not the abandoned static-map index or
observation-fusion design: the database stores current selected state, not an
append-only observation history.

Explicitly out of scope are ACE, ballistics, routes, support execution, a full
vehicle subsystem, unrestricted world enumeration, camera/cursor/view-focus
state, player reports, confidence fusion, R-tree indexing, arbitrary SQL or
SQF, and proactive notifications.

## Preserved Phase A contract

`map-gazetteer-v1`, `MapGazetteerStore`, `MapGazetteerCoordinator`,
`PositionInterpretationService`, `interpretedLocation`,
`find_named_locations`, `ResponseProfilePolicy`, the response-profile UI and
final-text terminator normalization remain authoritative subordinate designs.
The complete gazetteer stays local and is never repeated in dynamic snapshots.
The final answer remains one normalized text result. The visible form preserves
the exact Arma callsign; ElevenLabs input and replay may differ only by the
deterministic speech-only callsign pronunciation. Version 0.7 partial-success
behavior remains unchanged.

## Official Arma source audit

All SQF sources below are documented vanilla commands. The addon reads them;
it does not call setters or execute mission actions.

| Section | Documented sources | Local boundary |
| --- | --- | --- |
| player | [`getPosATL`](https://community.bohemia.net/wiki/getPosATL), [`getPosASL`](https://community.bohemia.net/wiki/getPosASL), [`mapGridPosition`](https://community.bohemia.net/wiki/mapGridPosition), [`side`](https://community.bohemia.net/wiki/side), [`group`](https://community.bohemia.net/wiki/group), [`groupId`](https://community.bohemia.net/wiki/groupId) | local player only; `groupId (group player)` returns the current group name string |
| weather | [`overcast`](https://community.bohemia.net/wiki/overcast), [`overcastForecast`](https://community.bohemia.net/wiki/overcastForecast), [`rain`](https://community.bohemia.net/wiki/rain), [`fog`](https://community.bohemia.net/wiki/fog), [`fogParams`](https://community.bohemia.net/wiki/fogParams), [`fogForecast`](https://community.bohemia.net/wiki/fogForecast), [`wind`](https://community.bohemia.net/wiki/wind), [`windDir`](https://community.bohemia.net/wiki/windDir), [`windStr`](https://community.bohemia.net/wiki/windStr), [`gusts`](https://community.bohemia.net/wiki/gusts), [`waves`](https://community.bohemia.net/wiki/waves), [`lightnings`](https://community.bohemia.net/wiki/lightnings), [`humidity`](https://community.bohemia.net/wiki/humidity), [`ambientTemperature`](https://community.bohemia.net/wiki/ambientTemperature), [`nextWeatherChange`](https://community.bohemia.net/wiki/nextWeatherChange) | client weather; forecast fog may differ by machine; temperature is unavailable before Arma 3 2.06 |
| time/astronomy | [`date`](https://community.bohemia.net/wiki/date), [`daytime`](https://community.bohemia.net/wiki/daytime), [`time`](https://community.bohemia.net/wiki/time), [`timeMultiplier`](https://community.bohemia.net/wiki/timeMultiplier), [`moonPhase`](https://community.bohemia.net/wiki/moonPhase), [`sunOrMoon`](https://community.bohemia.net/wiki/sunOrMoon) | mission time plus the minimal moon phase and sun-or-moon scalar used for deterministic daylight classification; no lighting-incidence or star-visibility collector |
| loadout | [`primaryWeapon`](https://community.bohemia.net/wiki/primaryWeapon), [`secondaryWeapon`](https://community.bohemia.net/wiki/secondaryWeapon), [`handgunWeapon`](https://community.bohemia.net/wiki/handgunWeapon), [`currentWeapon`](https://community.bohemia.net/wiki/currentWeapon), [`currentMuzzle`](https://community.bohemia.net/wiki/currentMuzzle), [`currentWeaponMode`](https://community.bohemia.net/wiki/currentWeaponMode), [`currentMagazine`](https://community.bohemia.net/wiki/currentMagazine), [`ammo`](https://community.bohemia.net/wiki/ammo), weapon-item getters, [`magazinesAmmoFull`](https://community.bohemia.net/wiki/magazinesAmmoFull), [`assignedItems`](https://community.bohemia.net/wiki/assignedItems), container getters and [`hashValue`](https://community.bohemia.net/wiki/hashValue) | local player; arrays are capped and normalized |
| groups/units | [`units`](https://community.bohemia.net/wiki/units), [`leader`](https://community.bohemia.net/wiki/leader), [`groupID`](https://community.bohemia.net/wiki/groupID), [`behaviour`](https://community.bohemia.net/wiki/behaviour), [`combatMode`](https://community.bohemia.net/wiki/combatMode), [`formation`](https://community.bohemia.net/wiki/formation), [`currentWaypoint`](https://community.bohemia.net/wiki/currentWaypoint), [`waypointPosition`](https://community.bohemia.net/wiki/waypointPosition), [`waypointType`](https://community.bohemia.net/wiki/waypointType), [`expectedDestination`](https://community.bohemia.net/wiki/expectedDestination), [`assignedTarget`](https://community.bohemia.net/wiki/assignedTarget), [`currentCommand`](https://community.bohemia.net/wiki/currentCommand), `alive`, `lifeState`, `canMove`, `damage` | own side or configured own group only; no opposing-unit enumeration |
| contacts | [`targets`](https://community.bohemia.net/wiki/targets), [`targetKnowledge`](https://community.bohemia.net/wiki/targetKnowledge), [`getSensorTargets`](https://community.bohemia.net/wiki/getSensorTargets), [`local`](https://community.bohemia.net/wiki/local) | an eligible local unit represents each own-side group; groups with no local representative are omitted; the local player's vehicle sensors contribute only targets whose estimated state can be read through `targetKnowledge`; estimated position/error are exported, never hostile `getPos` |
| tasks | [`simpleTasks`](https://community.bohemia.net/wiki/simpleTasks), [`currentTask`](https://community.bohemia.net/wiki/currentTask), `taskName`, `taskDescription`, `taskDestination`, `taskType`, `taskState`, `taskParent` | tasks visible/assigned to the local player only; text is bounded and never logged |
| markers | [`allMapMarkers`](https://community.bohemia.net/wiki/allMapMarkers) and documented marker getters including [`markerPolyline`](https://community.bohemia.net/wiki/markerPolyline) | markers present on the local client with alpha greater than zero; raw variable names never leave the repository |

Official documentation does not guarantee that a client owns every remote AI
group, that every marker channel is visible in every UI state, or that
third-party task/marker content is trustworthy. The implementation therefore
fails closed on locality, labels client-known markers accurately, treats all
mission text as untrusted data and requires live multiplayer acceptance for
remote-group coverage.

## Canonical protocol

The existing `session-handshake-v1` remains authoritative and advertises
`{ "name": "state-snapshot", "version": 2 }`. Dynamic state uses
`arma-ai-bridge/arma3/state-snapshot-v2`.

The required envelope is `schema`, `messageId`, `missionId`, `sessionId`,
`timestamp`, `sequence`, `fullReconciliation` and `sections`. `timestamp` is
publication game time. Every section has its own `sampledAt` and `readiness`;
publication time never replaces real sample time. Required sections are
`player`, `environment`, `timeAstronomy`, `loadout`, `friendlyForces`,
`knownContacts`, `tasks` and `markers`.

Readiness is exactly `ready`, `stale`, `unavailable` or `failed`.

- `ready` plus an empty collection authoritatively clears prior rows.
- `stale` may carry the last bounded value but is never described as current.
- `failed` or `unavailable` preserves prior rows and marks them stale.
- a missing required section rejects the whole snapshot.
- invalid data rejects the whole snapshot; there are no silent partial writes.
- a new handshake session clears all dynamic current-state rows atomically.
- duplicate or lower sequences do not mutate state.
- the addon publishes every four seconds and marks every 30-second publication
  as a full reconciliation.

The checked-in JSON Schema closes every object, caps every array/string and
uses finite bounded numbers. The wire payload may contain source IDs required
for local reconciliation. They are stored only as keyed hashes; OpenAI and
diagnostics receive session aliases.

## Collection caches and cadence

The client keeps one bounded cache per section:

| Cache | Collection cadence |
| --- | --- |
| player position/grid | 1 second |
| friendly groups/units and known contacts | 2 seconds |
| loadout, tasks and markers | 4 seconds |
| weather, time and astronomy | 8 seconds |
| unified publication | 4 seconds |
| forced full reconciliation flag | 30 seconds |

Collection uses scheduled SQF and yields between bounded sections. A failed
collector replaces only its cache metadata with `failed`; it does not invent
values or discard a previously sampled value. Limits are 128 groups, 512
units, 256 contacts, 128 tasks, 256 markers, 64 magazines, 64 item classes per
container and 128 polyline coordinates per marker.

Each due collector runs inside the documented `isNil { code }` evaluation
boundary. Player is required before the first useful publication. Environment,
time/astronomy, loadout, friendly forces, known contacts, tasks and markers
invoke `_fail` independently; a nil/error result preserves that section's last
good cache, marks it failed, emits only a throttled section-name RPT indication
and continues to the publication footer. No task, marker, loadout or contact
content is logged.

## Section contracts

### Player

The canonical player section contains only ATL/ASL position, map grid, local
side, group source identity and `groupCallsign`. The callsign is the exact
result of `groupId (group player)` and is recollected on the normal one-second
player cycle. Mission, session, respawn, playable-unit, group-membership and
mission-authored group-ID changes therefore replace it naturally. Empty is a
valid value and never falls back to a raw ID or alias. Camera position, eye direction, view focus,
cursor target and crosshair object are forbidden. Legacy telemetry fields may
still be parsed for compatibility, but the mirror does not use them.

### Environment and time

Environment contains current/forecast overcast, rain, fog plus bounded
`[value, decay, base]` fog parameters, forecast fog, horizontal wind vector,
engine wind direction/strength, gusts, waves, lightning, humidity, optional
ambient temperature and next weather-change game time. Time/astronomy contains
only mission date, daytime, elapsed mission time, multiplier, moon phase and
the sun-or-moon scalar used for deterministic daylight/darkness classification.
`getLighting`, `lightDirection`, `starsVisibility` and `moonIntensity` are not
part of the release 0.8 collector, wire contract, repository model or OpenAI
context. A command/collector failure marks the whole time/astronomy section
failed. Environment `lightning` remains because it describes thunderstorm
activity, not general light incidence.

### Loadout

The normalized loadout contains primary, launcher, handgun, selected weapon,
muzzle, mode, current magazine, loaded rounds, weapon optics/attachments,
binocular, individual magazines with remaining rounds, totals by class,
classified grenades/throwables and mine/explosive totals, assigned items,
uniform/vest/backpack classes, bounded container summaries and a deterministic
loadout hash. Display labels are resolved locally from config. Full inventory
rows are available only to deterministic summary and explicit
`query_state(loadout)`, never base context.

### Own-side groups, units and contacts

Groups contain source identity, callsign, leader/member identities, leader
position, behaviour, combat mode, formation, current waypoint index/position/
type, documented expected destination and assigned targets when available.
Units contain source/group identity, class/display role, position, alive/life
state, mobility, base-Arma damage, current command, assigned target and existing
mounted/seat-role fields. No independent vehicle subsystem is added.

Only configured own-side groups contribute contacts. An eligible local unit
reads `targets` and `targetKnowledge`. Each row contains source target identity,
class/broad type, perceived side, engine-estimated position, error margin,
last-seen/threat ages and contributing friendly-group source identities. The
same target is deduplicated; the lowest-error/newest estimate wins and all
sources remain. Hostile `getPos*` is forbidden. Singleplayer normally has local
representatives; multiplayer server/headless-owned groups may be absent and are
omitted rather than guessed. Existing player-vehicle sensor contacts remain
supported, without a full sensor-platform model.

### Tasks and markers

Tasks are limited to `simpleTasks player` and contain source identity, bounded
title/description, destination, type, status, parent and active flag. Markers
are limited to local-client nontransparent markers and contain source identity,
bounded visible text, position, type, color, shape, size, direction, alpha and
capped polyline. Text is never logged and is untrusted prompt data.

## SQLite current-state mirror

`Microsoft.Data.Sqlite` backs one bounded database under the local-state
directory. Schema version 2 contains:

```text
state_sessions, state_worlds, named_locations,
current_environment, current_time_astronomy, current_player, current_loadout,
friendly_groups, friendly_units, group_waypoints,
known_contacts, known_contact_sources, current_tasks, current_markers,
state_section_metadata
```

There is no R-tree or snapshot history. Arma IDs are SHA-256 keyed to the
session and presented as sequential privacy-safe aliases. SQL is fixed and
parameterized. One accepted snapshot is one transaction. Ready collection
sections are delete-and-replaced; failed/unavailable sections only update
readiness/receipt metadata and stale flags while retaining the last-good sample
time. Section metadata stores both game sample time and its receipt-relative UTC
projection, so age reflects the supplied sample time rather than publication.
Version 1 migrates in place by seeding that projection from its receipt time.
WAL mode and one application writer keep reads
nonblocking. Migrations use transactional `PRAGMA user_version`; unknown newer
versions fail closed.

On restart, cached rows are stale. A matching handshake activates a session;
a newer snapshot restores readiness. World name alone is not session identity.
Reset Local State Cache requires confirmation, deletes only this database and
preserves credentials/profiles. The gazetteer coordinator copies only a fully
active gazetteer to `named_locations`; config keys are hashed and never returned.

## Typed repository and deterministic interpretation

`IStateRepository` exposes bounded reads for player, environment/time, loadout,
friendly groups/units, contacts, tasks, markers, named locations and section
metadata. `SqliteStateRepository` is the only SQL implementation. Neither the
model nor tools can supply SQL.

Deterministic services derive environment wind/rain/fog/temperature/daytime and
age; loadout weapon/ammunition/grenade/explosive/attachment summary; force
group/unit/wounded/incapacitated/dead counts; contact counts by side/type,
newest age, uncertainty and stale count; and the existing position relation.
The former keyword-selected `StateContextSelector` is removed. One deterministic
operational/non-operational classifier decides only whether a state block is
appropriate, never which operational domains the model may see.

Once a valid v2 snapshot is accepted, SQLite is the canonical dynamic-state
authority. Every operational question receives one frozen compact DTO with
world, player position/grid/ASL elevation and optional exact `groupCallsign`,
five nearest official locations, environment, time/daylight, loadout, own-side
summary plus at most eight groups, contact summary plus at most eight contacts,
the active task plus at most two additional tasks, at most five relevant
markers and explicit local capability flags. Individual friendly units are
omitted by default. Attachments and magazine summary rows are each capped at
eight. `validatedBallisticSolver` is always false in release 0.8.

Missing optional strings and objects are omitted; meaningful zero counts,
ammunition, rain/fog and false capability flags remain. Normal ready/live
metadata and current-fact ages are omitted. Only semantic qualifiers such as
`lastKnown`, `stale`, `unavailable`, `approximate`, contact
`positionErrorMeters` and `lastSeenSecondsAgo` may appear. Internal repository
records, paths, raw IDs, aliases, full inventories, complete collections and
legacy compatibility projections are never serialized. Clearly
non-operational conversation receives no operational state block.

The fixed operational root is:

```text
schema: arma-ai-bridge/operational-snapshot-v1
world
player
namedLocations
environment
time
loadout
friendlyForces
knownContacts
tasks
markers
capabilities
```

Before v2 is accepted, genuine legacy telemetry may use the bounded legacy
snapshot path. The two sources are never mixed. Diagnostics labels the active
source and shows the exact current player group callsign or `unavailable`, never
a source identity. The right-hand World State view previews the same fixed
compact eligible shape.

## Local tools

Standard turns attach no tool definitions. A deterministic local pre-classifier
attaches only strict `query_environment` for an explicit terrain-object request
that needs buildings, vegetation, roads, walls or rocks absent from the compact
snapshot. Other bounded read services remain local application capabilities but
are not advertised to the model in the standard release 0.8 request.

## One-request, privacy and diagnostics

After a valid typed question or completed transcription, the application
selects one of eight local English acknowledgement templates, inserts only the
current dynamic callsign placeholder, displays it immediately and starts the
Responses request. Spoken turns synthesize/play the acknowledgement through
ElevenLabs while the model request runs; generated acknowledgement audio is
cached locally. It is never model-generated, never entered into history and
never counted as the final answer. The visible status says Papa Bear is working.
Final text is displayed as soon as Responses completes and final speech waits
for acknowledgement playback, preventing overlap.

The visible acknowledgement retains exact Arma form. Speech-only formatting
may pronounce digits and separators without modifying stored identity. Empty
callsign uses exactly `Papa Bear copies. Stand by.` and the final prompt omits
direct address. The final prompt requires the current snapshot callsign to
override any earlier-history callsign and normally appear once at the start.

History is at most three user/assistant pairs and 4,000 characters. It contains
no prior state block. Tool continuation rounds stay within the one current
Responses turn. There is no acknowledgement model call, background state model
call or proactivity.

Mission text is data, never policy. Logs may contain schema, counts, sequence,
readiness and safe codes only. They must not contain prompts, transcripts,
answers, tool payloads, raw IDs, task/marker text, loadouts, contact positions,
database contents, API keys or voice IDs. Only retrieved minimized results enter
OpenAI context. Per-turn safe metrics are compact-snapshot UTF-8 bytes, section
record counts, history message/character counts, selected-tool count, provider
input/output/reasoning token totals, acknowledgement variation ID and total
response latency. Metric logs contain no callsign or content.

### Responses terminal-state contract

Release 0.8 keeps non-streaming `gpt-5-mini`, `store:false`, locally replayed
encrypted reasoning items and the existing bounded tool-continuation loop. A
direct answer uses exactly one Responses request. The request explicitly uses
`text.format.type=text` and `max_output_tokens=1200`; that bound includes both
reasoning and visible output tokens. There is no automatic retry, background
request, model switch or second interpreter request.

The raw HTTP parser reads top-level `status`, `error` and
`incomplete_details.reason`, then inspects every output item. Function calls
continue through the existing validated local tool loop. Ordered non-empty
`output_text` parts are concatenated. A documented `refusal` part is a valid
final answer and follows the same normalizer, history, UI, TTS and replay path.
Reasoning items are never visible answer text. Unknown item types are ignored
except for sanitized type counts.

`incomplete` with `max_output_tokens` or `max_tokens` maps to
`responses_incomplete_max_tokens`; `content_filter` maps to
`responses_incomplete_content_filter`. `failed` and `cancelled` use safe
terminal failures. A response that claims `completed` but has no function call,
visible text or refusal is malformed. Responses without `status` retain bounded
compatibility with existing completed test fixtures. Safe diagnostics may
contain status, incomplete reason, effective model, item-type counts, message
statuses, presence flags and aggregate input/output/reasoning token counts.
They never contain response bodies, IDs, reasoning, output/refusal text,
questions, snapshots, profiles or tool payloads.

Diagnostics shows session alias, database/baseline readiness, last sequence and
receipt, per-section readiness/age, row counts, database size, schema versions
and safe code. It does not show raw IDs, task/marker text, contact positions,
loadout contents or rows. A confirmed Reset Local State Cache action is the only
write control and does not touch credentials/profiles.

## Deterministic automated acceptance

The Windows suite must prove:

1. Phase A gazetteer and position regressions remain green.
2. v2 schema/SQF fixtures close and cap every field.
3. sample times reflect 1/2/4/8-second caches and four-second publication.
4. duplicate/out-of-order sequences do not mutate state.
5. SQLite ingest is atomic.
6. failed/unavailable sections preserve rows as stale.
7. ready empty sections clear rows.
8. session change clears dynamic rows atomically and retains matched baseline.
9. restart rows remain stale until handshake plus fresh snapshot.
10. weather/astronomy and loadout aggregation are exact.
11. groups, waypoints and unit state are bounded.
12. contacts deduplicate sources and never use hostile actual position.
13. tasks/markers are client-scoped, bounded and absent from logs.
14. typed repository and `query_state` validation reject arbitrary SQL/sections.
15. every operational question receives every fixed compact domain; clearly
    non-operational questions receive no operational state block.
16. location/group/contact/task/marker/attachment/magazine limits are
    5/8/8/3/5/8/8 regardless of mission size; meaningful zeros remain.
17. raw IDs, aliases, paths, complete database/collections, normal readiness,
    current ages, duplicate legacy facts and empty placeholders never enter the
    fixed operational context.
18. standard requests attach zero tools; an explicit terrain-object request
    attaches only strict `query_environment`; snapshot state stays frozen for
    the turn and previous snapshots never enter bounded history.
19. repository verifier, WPF win-x64, native x64, PBO and matching ZIP pass.
20. active runtime/schema/payload fixtures contain no `sunDirection`,
    `getLighting`, `lightDirection`, `starsVisibility` or `moonIntensity`;
    per-section failure isolation remains contract-protected.
21. completed text, multipart text, refusal, function continuation, incomplete,
    failed, cancelled, reasoning-only, unknown-item and missing-status Responses
    shapes are deterministic and content-redacted in diagnostics.
22. the Responses budget is 1200, explicit text format is present, reasoning
    usage is counted, direct answers do not retry and the default remains
    `gpt-5-mini`.
23. `groupId (group player)` reaches canonical `groupCallsign`, updates on the
    next player snapshot, clears on a new session and appears identically in
    acknowledgement state, diagnostics and final model context.
24. eight English-only placeholder acknowledgements are local, immediate,
    non-repeating, absent from model history and followed by one final answer;
    empty callsign uses the neutral fallback.
25. speech-only digit formatting preserves stored/visible callsign identity,
    acknowledgement audio caches locally, final speech cannot overlap it and
    no hardcoded callsign remains in active runtime or templates.
26. profiles, terminators, partial-success text/TTS/replay behavior and every
    accepted 0.7 regression remain green.

## Exact live acceptance

Use the extended Stratis mission and matching 0.8 app/DLL/PBO:

1. Confirm one gazetteer and handshake feature `state-snapshot@2`.
2. Confirm one snapshot about every four seconds, real section sample times and
   full reconciliation about every 30 seconds.
3. Set the player group ID to `Alpha 1-1`; confirm exact State Mirror and
   diagnostics value, immediate English acknowledgement and the same current
   callsign in the final answer. Change group/unit and group ID, respawn and
   start a new session; confirm the next player sample replaces it without
   retaining the prior identity. Empty group ID must use the neutral fallback.
4. Ask `What is my position?`, `What is the nearest town?`, `What is my current
   loadout?`, `Which friendly group is closest to the newest contact?` and `I
   need a firing solution. Range 1200 metres, bearing 223.` Confirm immediate
   acknowledgement, natural cross-domain use, no status/freshness narration,
   zero standard tools and no fabricated firing solution.
5. Ask an explicit buildings/roads-ahead question; confirm the request exposes
   only `query_environment`.
6. With two WEST groups, confirm friendly counts/status/current waypoint.
7. Let only the remote group know an EAST target. Confirm estimated
   position/error/source alias, never hostile actual position. If locality
   prevents access, confirm omission and record the limitation.
8. Create a visible task and marker; confirm answers, then removal via ready-empty.
9. Confirm response-profile/terminator behavior and spoken digit formatting.
10. Change state without a question; confirm no model, TTS, speech or alert.
11. Run 15 minutes; confirm row counts/database size stabilize and snapshot
    bounds do not grow with mission size.
12. Restart app; cache is stale until handshake and a fresh snapshot.
13. Change mission; old dynamic state and callsign disappear before new state is visible.
14. Reset requires confirmation and preserves keys/profiles.
15. Logs contain only approved metrics and no protected content or callsign.

Live regression for the release-blocking astronomy fault and its final cleanup:

- before the fix, RPT repeats `Error Undefined variable in expression:
  sunDirection`, State Mirror sequence remains zero and World State waits for
  its first valid telemetry observation;
- after the fix, that RPT error is absent, the handshake advertises
  `state-snapshot@2`, the first snapshot is accepted within approximately five
  seconds, sequence is greater than zero and all eight wrappers appear;
- `timeAstronomy` is ready with only mission date/time, multiplier, moon phase
  and `sunOrMoon`, or explicitly failed without blocking other sections;
- runtime SQF, schema and payload contain no `getLighting`, `lightDirection`,
  `starsVisibility` or `moonIntensity`.

Live regression for the release-blocking Responses failure:

- before the fix, `status=incomplete`, `reason=max_output_tokens` and a single
  reasoning output item are misreported as `Missing final output text`;
- after the fix, that shape reports `responses_incomplete_max_tokens`, includes
  only safe status/type/token metadata and performs no automatic retry;
- ask weather/wind, ammunition and position in the accepted State Mirror
  session; each completed direct answer uses one Responses request and the same
  normalized answer, with only deterministic spoken callsign pronunciation
  permitted to differ from visible text, while an incomplete result is precise
  and redacted.

The draft PR remains unmerged until this gate passes. Official OpenAI reference
material documents terminal statuses, incomplete reasons, refusal/output text,
explicit text format, combined visible/reasoning output limits and reasoning
usage. It cannot verify this account's live `gpt-5-mini` completion quality,
latency, quota behavior or exact mission-context token demand. Official Arma
documentation likewise cannot verify multiplayer locality coverage,
third-party task/marker behavior, Windows audio or long-run database behavior;
these remain live acceptance requirements.
