# Codex Milestone 5: Unified State Mirror & Interpreter

## Objective and release boundary

Release 0.8 is the **Unified State Mirror & Interpreter**. It preserves the
accepted 0.7 voice path and the already implemented Phase A named-location,
position-interpretation and response-profile work, then adds one bounded
periodic Arma state message and a local SQLite current-state mirror.

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
Visible answer, assistant history, ElevenLabs input and replay continue to use
one identical normalized string. Version 0.7 partial-success behavior remains
unchanged.

## Official Arma source audit

All SQF sources below are documented vanilla commands. The addon reads them;
it does not call setters or execute mission actions.

| Section | Documented sources | Local boundary |
| --- | --- | --- |
| player | [`getPosATL`](https://community.bohemia.net/wiki/getPosATL), [`getPosASL`](https://community.bohemia.net/wiki/getPosASL), [`mapGridPosition`](https://community.bohemia.net/wiki/mapGridPosition), [`side`](https://community.bohemia.net/wiki/side), [`group`](https://community.bohemia.net/wiki/group) | local player only |
| weather | [`overcast`](https://community.bohemia.net/wiki/overcast), [`overcastForecast`](https://community.bohemia.net/wiki/overcastForecast), [`rain`](https://community.bohemia.net/wiki/rain), [`fog`](https://community.bohemia.net/wiki/fog), [`fogParams`](https://community.bohemia.net/wiki/fogParams), [`fogForecast`](https://community.bohemia.net/wiki/fogForecast), [`wind`](https://community.bohemia.net/wiki/wind), [`windDir`](https://community.bohemia.net/wiki/windDir), [`windStr`](https://community.bohemia.net/wiki/windStr), [`gusts`](https://community.bohemia.net/wiki/gusts), [`waves`](https://community.bohemia.net/wiki/waves), [`lightnings`](https://community.bohemia.net/wiki/lightnings), [`humidity`](https://community.bohemia.net/wiki/humidity), [`ambientTemperature`](https://community.bohemia.net/wiki/ambientTemperature), [`nextWeatherChange`](https://community.bohemia.net/wiki/nextWeatherChange) | client weather; forecast fog may differ by machine; temperature is unavailable before Arma 3 2.06 |
| time/astronomy | [`date`](https://community.bohemia.net/wiki/date), [`daytime`](https://community.bohemia.net/wiki/daytime), [`time`](https://community.bohemia.net/wiki/time), [`timeMultiplier`](https://community.bohemia.net/wiki/timeMultiplier), [`moonPhase`](https://community.bohemia.net/wiki/moonPhase), [`moonIntensity`](https://community.bohemia.net/wiki/moonIntensity), [`sunOrMoon`](https://community.bohemia.net/wiki/sunOrMoon), [`sunDirection`](https://community.bohemia.net/wiki/sunDirection) | structured scalar/vector facts only; `getLightingAt` is deliberately omitted because its array is not a stable bounded product contract |
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

## Section contracts

### Player

The canonical player section contains only ATL/ASL position, map grid, local
side and group source identity. Camera position, eye direction, view focus,
cursor target and crosshair object are forbidden. Legacy telemetry fields may
still be parsed for compatibility, but the mirror does not use them.

### Environment and time

Environment contains current/forecast overcast, rain, fog plus bounded
`[value, decay, base]` fog parameters, forecast fog, horizontal wind vector,
engine wind direction/strength, gusts, waves, lightning, humidity, optional
ambient temperature and next weather-change game time. Time/astronomy contains
mission date, daytime, elapsed mission time, multiplier, moon phase/intensity,
sun-or-moon scalar and the bounded three-number sun direction. It never exports
an unstructured lighting array.

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
`StateContextSelector` recognizes German and English position, weather/wind,
time/darkness, weapons/ammunition, friendly forces, contacts/enemies,
tasks/objectives and markers. It performs no model call.

Every turn receives only world, session readiness, interpreted position,
environment summary, current-task summary, friendly/contact counts and section
freshness. The selector may add one bounded relevant section. Full groups,
contacts, tasks, markers and inventory never enter base context.

## Local tools

`query_state(section, includeStale, limit)` accepts only `environment`, `time`,
`loadout`, `friendly_forces`, `contacts`, `tasks`, `markers` and
`named_locations`. It calls typed repository methods and returns bounded DTOs.
It cannot expose SQL, paths, raw IDs, unlimited text or the database.
`find_named_locations` remains the spatial/name-ranked official lookup;
`query_state(named_locations)` is a small bounded list. Existing
`query_environment`, friendly-force, asset and capability tools remain read-only.

## One-request, privacy and diagnostics

The path remains microphone -> OpenAI transcription -> local selector and
interpreters -> one Responses turn -> normalized visible answer -> ElevenLabs
-> playback. Tool continuation rounds stay within that turn. There is no
background model call or proactivity.

Mission text is data, never policy. Logs may contain schema, counts, sequence,
readiness and safe codes only. They must not contain prompts, transcripts,
answers, tool payloads, raw IDs, task/marker text, loadouts, contact positions,
database contents, API keys or voice IDs. Only retrieved minimized results enter
OpenAI context.

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
15. German/English context selection chooses every required section.
16. ordinary section questions use one Responses request and bounded context.
17. raw IDs, paths, complete database and complete collections never enter
    default OpenAI context.
18. profiles, terminators, visible/history/TTS/replay identity and every 0.7
    regression remain green.
19. repository verifier, WPF win-x64, native x64, PBO and matching ZIP pass.

## Exact live acceptance

Use the extended Stratis mission and matching 0.8 app/DLL/PBO:

1. Confirm one gazetteer and handshake feature `state-snapshot@2`.
2. Confirm one snapshot about every four seconds, real section sample times and
   full reconciliation about every 30 seconds.
3. Ask weather/wind, time/lighting and ammunition in German/English; confirm
   local derivations and normally no tool call.
4. With two WEST groups, confirm friendly counts/status/current waypoint.
5. Let only the remote group know an EAST target. Confirm estimated
   position/error/source alias, never hostile actual position. If locality
   prevents access, confirm omission and record the limitation.
6. Create a visible task and marker; confirm answers, then removal via ready-empty.
7. Confirm named-place relation and response-profile/terminator behavior.
8. Change state without a question; confirm no model, TTS, speech or alert.
9. Run 15 minutes; confirm row counts/database size stabilize.
10. Restart app; cache is stale until handshake and a fresh snapshot.
11. Change mission; old dynamic state disappears before new state is visible.
12. Reset requires confirmation and preserves keys/profiles.
13. Logs contain no protected content.

The draft PR remains unmerged until this gate passes. Official documentation
cannot verify multiplayer locality coverage, third-party task/marker behavior,
natural-language quality, provider latency/quotas, Windows audio or long-run
database behavior; these remain live acceptance requirements.
