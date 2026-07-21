# Codex Milestone 2: Local Provenance-Aware World Model

## Objective

Milestone 2 replaces direct telemetry-to-OpenAI forwarding with a local,
session-scoped world model. The desktop app ingests the existing
`telemetry-v1` Named Pipe messages, tracks the current player, group, occupied
vehicle, map, and known contacts, and builds minimized snapshots for specific
OpenAI purposes.

This milestone establishes the Phase 2 foundation from
`implementation-roadmap.md`. It does not add new Arma addon or native DLL
fields. It preserves the existing manual map-query flow and the stateless
OpenAI tool loop.

## Scope

Implement in the Windows desktop app:

- `TelemetryIngestService` for validation, normalization, and delta ingestion;
- `WorldStateStore` for mission-scoped entity state;
- stable local entity identity where the current feed supports it;
- observation timestamps, local receive timestamps, freshness, provenance,
  confidence, position, and uncertainty metadata;
- map and mission-reset detection using the evidence currently available;
- purpose-specific, privacy-minimized OpenAI snapshot builders;
- a read-only World State diagnostics tab;
- deterministic unit tests for ingestion, identity, freshness, reset, snapshot,
  and privacy behavior.

## Existing telemetry and protocol audit

Milestone 2 consumes the existing newline-delimited Named Pipe protocol and
the `arma-ai-bridge/arma3/telemetry-v1` envelope. The following limitations
were identified before implementation.

| Field or behavior | What is available | Limitation for identity or provenance | Milestone 2 treatment |
| --- | --- | --- | --- |
| Mission identity | Map name, map size, mission game time, and frame number | There is no mission or scenario identifier. Two consecutive missions on the same map cannot be distinguished with certainty if their clocks and frames do not regress. | Start a new local session when the map fingerprint changes or mission time/frame demonstrably regresses. Report the remaining ambiguity in diagnostics. |
| Player identity | Player UID, name, side, and group label | UID is stable but privacy-sensitive and must not be sent to OpenAI. | Use the local session-scoped identity `player:self`. Do not retain the UID in OpenAI snapshots or logs. |
| Group identity | `groupId group player` text | The label is not documented by this protocol as a globally unique or stable group identifier. No group network ID or membership list is present. | Use a best-effort session-scoped group identity derived locally from side and label. Mark its identity quality as best effort. Do not send the raw group label to OpenAI. |
| Contact identity | `netId` when available, otherwise `str object` | The message does not identify which source produced the value. The fallback has no protocol-level stability guarantee. | Treat the value as an opaque mission-scoped key, mark identity quality as best effort, and expose only locally assigned aliases such as `contact-001` to OpenAI. |
| Occupied vehicle identity | Class, display name, position, heading, status, and role | There is no vehicle ID. The feed cannot prove that two observations refer to the same vehicle after an exit/re-entry or vehicle change to the same class. | Model only the current occupied-vehicle slot as `vehicle:current`; mark identity quality as slot-only. |
| Contact observation time | Envelope mission time plus `lastSeenAgeSeconds` and `lastThreatAgeSeconds` | Contacts are cached by the addon for approximately one second, but the cache collection time is not carried in each contact. Repeated envelopes can contain the same cached observation. | Reconstruct the best available game-time observation from envelope time and age, retain unchanged cached observations, and record the local receive time separately. |
| Contact removal | A capped list of contacts in range/recent enough for the addon query | There are no tombstones. Absence cannot prove death, deletion, or loss of knowledge. | Keep the last known entity and let freshness/confidence decay. Never infer destruction or removal from omission. |
| Contact provenance | `knownByPlayer`, `knownByGroup`, perceived side, uncertainty, and sensor names | This establishes broad evidence sources, not a precise report, observer, or sensor event chain. | Record the available player/group/sensor source set and deterministic primary source. Do not claim more specific provenance. |
| Sensor contact geometry | Target ID/class/type, relationship, and sensors | There is no position, position error, or contact age. | Merge sensor evidence into a matching known contact. Retain unmatched sensor-only entities without inventing geometry. |
| Message identity/order | Mission time and frame number | There is no message ID or explicit reset event. | Use time/frame for best-effort ordering, duplicate handling, and reset detection. |
| Field-level timestamps | One envelope timestamp | Individual player, map, group, and vehicle fields have no separate observation time. | Attribute their observation to the envelope mission time and keep the desktop receive time independently. |

These limits do not require an addon or DLL change for this milestone. A future
protocol revision would be justified only by a demonstrated feature that needs
one of the unavailable guarantees—for example, an explicit mission ID for
unambiguous same-map lifecycle tracking, an identity-kind field for contact
keys, a vehicle network ID, or per-observation timestamps. Broader group and
friendly-force identity belongs to Milestone 3.

### Official documentation verification

Bohemia's official community documentation confirms that `time` is elapsed
mission time, `diag_frameNo` is persistent across missions until the game is
closed, `worldName` identifies the loaded world (with unreliable casing),
`groupId` returns a display name/callsign, `netId` is a unique object/group ID,
`targetKnowledge` supplies the knowledge fields used here, and
`assignedVehicleRole` returns an array. The ingest service therefore compares
world names case-insensitively and normalizes the existing array-form vehicle
role without changing the protocol.

Official documentation does **not** establish:

- stability or uniqueness for the addon's `str object` fallback;
- whether a telemetry contact ID came from `netId` or that fallback;
- an exact mission boundary from the combination of map name, `time`, and
  `diag_frameNo` in all same-map/reload/JIP cases;
- a collection timestamp or tombstone semantic for the cached contact arrays;
- product-specific freshness thresholds or confidence weights.

Those are reported limitations or explicit local policy, not engine guarantees.
No acceptance criterion in this milestone depends on treating them as official
facts.

## Local data model

The store is session-scoped and maintains:

- session and map metadata;
- the current player;
- the player's current group summary;
- the current occupied-vehicle slot, when present;
- known contacts keyed by their opaque telemetry identity;
- sensor evidence keyed by the same opaque identity when possible.

Every exposed entity carries:

- a local entity ID or privacy-safe alias;
- identity quality (`stable`, `best-effort`, or `slot`);
- primary provenance and the available evidence-source set;
- mission-game observation time;
- desktop UTC receive time;
- computed age and freshness class;
- deterministic confidence;
- position and uncertainty where supplied by telemetry.

No hidden entities are synthesized. The store represents only what the
existing player/group/sensor telemetry reports.

## Identity rules

- Player: `player:self`, stable within every local session.
- Group: a session-scoped local ID from normalized side and group label; raw
  group text remains local and is excluded from OpenAI context.
- Current vehicle: `vehicle:current`, explicitly a slot rather than an object
  identity.
- Contacts: raw opaque IDs are used only as local dictionary keys. A monotonic,
  session-scoped alias is assigned on first observation and remains stable for
  the rest of that session.
- A session reset clears all aliases and entity state.

## Time, freshness, and confidence

All age calculations use an injected `TimeProvider` so production follows UTC
and tests can advance time deterministically.

Freshness thresholds are intentionally conservative:

| Entity type | Live | Recent | Stale | Historical |
| --- | --- | --- | --- | --- |
| Player, group, map, current vehicle | up to 1 s | up to 5 s | up to 30 s | over 30 s |
| Known or sensor contact | up to 5 s | up to 30 s | up to 120 s | over 120 s |

Confidence starts from the evidence available in the feed and decays by
freshness. Direct player observations are highest confidence; player-known
contacts rank above group-only or sensor-only evidence. Position uncertainty
is retained as supplied and is never silently converted into precise
coordinates.

## Ingestion and delta behavior

`TelemetryIngestService`:

1. ignores non-telemetry Named Pipe messages;
2. validates the telemetry schema discriminator and required shapes;
3. normalizes finite numeric and vector values without retaining the raw JSON;
4. applies one atomic observation to `WorldStateStore`;
5. reports validation failures without logging message contents, names, UIDs,
   group labels, or object IDs.

The store applies changes under one lock and publishes a state-change event
after the update. Repeated cached contacts do not create a new observation
unless their reported evidence changes. Missing contacts are retained and age
naturally because omission is not a tombstone.

## Mission and map lifecycle

A new local world session is created when:

- the normalized map name or map size changes;
- mission game time regresses beyond a small ordering tolerance; or
- the frame number materially regresses.

The reset atomically clears the player, group, vehicle, contacts, and aliases,
then ingests the triggering observation into the new session. Pipe disconnects
mark the feed disconnected but do not immediately erase state, allowing a
same-mission reconnect. Diagnostics displays the last reset reason and the
same-map mission-identity limitation.

## Purpose-specific OpenAI snapshots

Snapshot construction is separate from storage. Milestone 2 provides:

- a current-situation snapshot for general assistant turns;
- a known-contacts snapshot for contact-focused reasoning.

Snapshots include only the fields needed for their stated purpose. They omit
player UID/name, raw group labels, raw contact/object IDs, unused telemetry,
and the original JSON. Contact aliases are stable only within the local
session. Historical entities are omitted from OpenAI snapshots while remaining
visible in local diagnostics.

`AssistantPanel` builds a current-situation snapshot from `WorldStateStore`
before every request. `OpenAiAssistantService` receives that minimized
snapshot instead of raw telemetry. The existing `store: false` request, local
tool validation, bounded tool loop, manual `map.query`, and Named Pipe query
correlation behavior remain unchanged.

## Diagnostics UI

Add a read-only **World State** tab that shows:

- connection/session/map/reset summary;
- latest observation and freshness;
- current player/group/vehicle summaries;
- contact counts by freshness and provenance;
- the exact privacy-minimized current-situation JSON that would be sent to
  OpenAI.

The UI must not mutate world state and must not expose API keys. It refreshes
from store events and a timer so freshness changes remain visible even without
new telemetry.

## Deterministic verification

Unit tests cover:

- valid ingestion and rejection/ignoring of invalid or unrelated messages;
- stable player, group, vehicle-slot, and contact-alias behavior;
- contact provenance, uncertainty, freshness, and confidence decay;
- retention of omitted contacts;
- map-change, time-regression, and frame-regression resets;
- privacy and purpose-specific snapshot field selection;
- OpenAI requests containing the world snapshot rather than raw telemetry;
- preservation of the existing stateless tool loop and local map-query path.

The Windows build and full test project must pass before publishing. Live Arma
acceptance for Milestone 2 should confirm session/reset diagnostics and the
snapshot content, but does not block deterministic desktop verification.

## Explicit non-goals

- Arma addon or native DLL changes;
- ACE integration;
- voice input/output;
- static map indexing or retrieval;
- support-action execution;
- broader friendly-force/member telemetry;
- persistent cross-mission identity;
- prediction of unseen entities or inferred destruction from contact absence.
