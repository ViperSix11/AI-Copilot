# Arma data contract

## Principles

The active bridge carries only versioned, bounded messages:

1. mission/session handshake and protocol features;
2. cached State Mirror snapshots and periodic reconciliation;
3. read-only mission capabilities, friendly-force compatibility messages and
   official named-location gazetteer data;
4. explicit bounded read-query requests/results and asynchronous world events.

The native x64 extension remains a duplex Named Pipe transport. Game semantics
and visibility decisions belong in SQF and the Windows application. The active
product has no action-command family and no arbitrary-command channel.

All model-generated tool arguments are validated locally. OpenAI cannot send
SQF, C++, SQL, PowerShell, file paths or operating-system commands through the
bridge.

## Session and protocol

`session-handshake-v1` identifies the current mission/session, world and
advertised protocol features. A mission change, respawn/playable-unit change,
group change or State Mirror session reset causes current identity and state to
be refreshed rather than reused across missions.

Transport identifiers are permitted only long enough to join records locally.
The desktop hashes them into mission-scoped identities. Raw netIds, player
profile names, UIDs and source mission/session identifiers do not enter model
context.

## Unified State Mirror

The active state message is `arma-ai-bridge/arma3/state-snapshot-v2`. In the
0.9.1 baseline, SQF publishes one bounded envelope every four seconds. Each
section carries its own game sample time and refresh cadence:

- player position, grid, side and group callsign: 1 second;
- friendly forces and side-wide own-side known contacts: 2 seconds;
- loadout, tasks and markers: 4 seconds;
- overcast, time and astronomy: 8 seconds;
- all sections: 30-second full reconciliation.

Every envelope contains all eight section wrappers. A ready, successfully
sampled empty array authoritatively clears that section. A failed or unavailable
section preserves its last good rows as stale. The desktop rejects out-of-order
sequences and atomically replaces current-state tables in one SQLite
transaction.

Optional SQF collectors fail independently through an `isNil { code }`
boundary, so one bad section cannot suppress the rest of the envelope. The
required player cache must exist before the first useful snapshot is sent.

## Player, environment and time

The local player section contains:

- ATL/ASL position and map grid for authorized local deterministic use;
- current side and privacy-safe group relationship;
- exact current `groupCallsign` from `groupId (group player)`.

The callsign is recollected every player cycle and replaced across respawn,
playable-unit, group, group-ID, mission and session changes. Empty callsign does
not fall back to a raw ID, alias, profile name or hardcoded identity.

Canonical player position, grid and elevation remain local and are deliberately
withheld from ordinary OpenAI context. Camera/free-look state, cursor target and
unrestricted object state are not collected.

Environment contains overcast and bounded storm state only. Temperature and
wind are not collected, stored or forwarded. Time/astronomy retains mission
date/time, time multiplier, moon phase and `sunOrMoon`.

## Friendly forces and known contacts

The snapshot exports current own-side groups, units and crewed vehicles within
the accepted mission/network perspective. It may include bounded friendly
equipment and crewed-vehicle cargo summaries for read-only resource questions.
Player-private inventory and unseen hostile equipment remain withheld.

Known hostile/unidentified contacts are aggregated from local own-side
representatives through Arma `targets`/`targetKnowledge`. Only engine-estimated
position, uncertainty, age and source-group references are accepted. Hostile
`getPos*`, unrestricted mission enumeration, opposing-side routes, orders,
waypoints, targets and inventories are forbidden.

The desktop retains observations and track lifecycle separately. Current,
last-known, reacquired and confirmed-dead states must not be collapsed.

## Tasks, markers and named locations

Tasks and positive-alpha markers visible to the local client are bounded state
inputs. Marker text and geometry may define a local semantic place reference,
including point, rectangle and ellipse areas. Raw marker identifiers are
discarded before model context. Marker text is untrusted data, not an
instruction.

The gazetteer contains only world metadata, map grid metadata and allowed
official `CfgWorlds >> worldName >> Names` entries. It never scans buildings,
roads, vegetation, terrain tiles, water, vehicles, units or runtime mission
objects.

Local position presentation chooses an authorized Bullseye or nearby semantic
place when available, then uses a six-digit grid fallback. OpenAI receives the
resulting natural phrase, not raw marker geometry or canonical coordinate
pairs.

## Mission capabilities and assets

Missions may declare typed read-only capability and asset records. These
records describe availability and constraints; they do not expose an execution
tool. Support dispatch, waypoint assignment, route execution and arbitrary SQF
remain out of scope.

## Model-facing local tools

The 0.9.1 assistant offers only locally executed typed tools:

- `inspect_context_catalogue`
- `query_context`
- `query_long_term_map_intelligence`
- `record_player_information`
- `record_event_assessment`
- eligible mission-memory operations:
  `remember_information`, `search_memory`, `update_memory`, `forget_memory`

`query_context` uses fixed information groups, category validation, bounded
detail/scope enums, capped time/distance/result limits and allowed requested
fields. Long-term map-intelligence access is separately permission-gated and
bounded to one narrow category/scope; it cannot request a complete map or
database.

The original typed/transcribed player message is retained locally before model
interpretation. Structured interpretation is stored separately so it cannot
replace the player's wording. Write tools may preserve or correct only explicit
player information or the current event assessment; they cannot change Arma
state or execute a game action.

Before a tool result is returned to OpenAI, local code converts selected typed
records into short English facts. Database rows, schemas, raw aliases,
timestamps, machine error objects and serialized query envelopes remain local.

The existing manual `query_environment` path and local diagnostic read services
remain available outside the normal context-on-demand model loop.

## Compatibility

Wire schema versions, SQLite schema versions, protocol features and product
versions are independent. Unknown optional fields are ignored; unsupported
required protocol/schema features fail explicitly. Matching application,
native DLL and PBO artifacts must come from the same source baseline.
