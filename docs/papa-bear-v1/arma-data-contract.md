# Arma data contract

## Principles

The bridge separates four channels:

1. cached state snapshots;
2. explicit read queries;
3. validated action commands;
4. asynchronous events and operation updates.

All messages are versioned JSON with `schema`, `messageId`, timestamps and correlation identifiers. The native extension remains transport-focused; game semantics belong in SQF/CBA adapters and the Windows application.

## Unified State Mirror (release 0.8)

The active state message is `arma-ai-bridge/arma3/state-snapshot-v2`. SQF sends
one bounded envelope every four seconds, but each section carries its own game
sample time and is refreshed independently:

- player position, grid, side and group callsign: 1 second;
- friendly forces and side-wide own-side known contacts: 2 seconds;
- loadout, tasks and markers: 4 seconds;
- weather, time and astronomy: 8 seconds;
- all sections: 30-second full reconciliation.

Every envelope contains all eight section wrappers. A ready, successfully
sampled empty array authoritatively clears that section. A failed or unavailable
section preserves its last good rows as stale. The desktop rejects out-of-order
sequences and atomically replaces current-state tables in one SQLite transaction.
Optional SQF collectors fail independently through an `isNil { code }`
boundary, so one bad section cannot suppress publication. The required player
cache must exist before the first useful snapshot is sent.

Time/astronomy retains only mission date/time, time multiplier, moon phase and
`sunOrMoon`. Release 0.8 does not collect or export `getLighting` derivatives,
`lightDirection`, `starsVisibility` or `moonIntensity`. Environment
`lightning` remains the separate thunderstorm-activity fact.

## Required player state

- map/world and mission identifiers in the session envelope;
- ATL/ASL position and map grid;
- side and privacy-safe group reference for local filtering/relationships;
- exact current `groupCallsign` from `groupId (group player)`.

The callsign is recollected every player cycle and replaced across respawn,
playable-unit, group, group-ID, mission and State Mirror session changes. It is
stored and shown exactly as supplied by Arma. Empty never falls back to a raw
group ID, alias, profile name or hardcoded identity. A deterministic speech-only
formatter may pronounce its digits and separators without changing stored or
visible identity.

Body/view heading, velocity, stance, life state, damage and legacy player
weapon placeholders are not canonical release 0.8 player facts. Loadout facts
come only from the loadout section. There is no release 0.8 vehicle subsystem.

Camera position, free-look direction, cursor target and unrestricted object
state are intentionally excluded.

## Firing-solution boundary

Release 0.8 exports ordinary loadout identity and ammunition counts only. It
does not export trajectory coefficients, zeroing profiles or external-mod
state, and it defines no firing-solution or terrain-height command.

## Friendly force data

The snapshot exports own-side groups and units. Raw netIds are transport-only
source references: the desktop hashes them for joins and exposes only
mission-scoped aliases. Player profile names and UIDs are never collected.

## Known contact data

The active collector aggregates `targets`/`targetKnowledge` from local own-side
representatives. It exports only Arma's estimated position, position error,
knowledge age and source-group references. Hostile `getPos*`, mission-wide
enumeration and hidden opposing-side state are forbidden.

## Mission capability registry

Missions can publish typed capabilities such as:

```json
{
  "capability": "rotary_transport",
  "enabled": true,
  "provider": "mission-script",
  "constraints": {
    "maxConcurrent": 1,
    "allowedRequesterSides": ["WEST"]
  }
}
```

No action tool is exposed to OpenAI unless a corresponding capability is active.

## Read-query families

Local read-query capabilities include:

- `find_named_locations`
- `query_friendly_forces`
- `query_assets`
- `query_mission_capabilities`
- `query_state`

`query_state` accepts only a fixed section enum, `includeStale` and a capped
limit. It cannot accept SQL, file paths, SQF or commands. These remain local
application reads. Standard model requests advertise no tools. The legacy
manual environment command and bounded terrain-height lookup may remain
internal, but no model declaration or allowed-tool route can return individual
buildings, roads, vegetation, walls, rocks, lights or unnamed static-object
coordinates.

Once v2 is active, every operational question receives the same fixed compact
`operational-snapshot-v1` domains with limits of five locations, eight groups,
eight contacts, three tasks, five markers, eight attachments and eight magazine
summary rows. Individual units, normal live metadata, raw IDs/aliases and full
collections remain local. Meaningful zeros and semantic stale/last-known/
approximate/unavailable qualifiers remain. Non-operational conversation receives
no operational state block.

## Action-command families

Planned commands include:

- create, modify or cancel support request;
- assign or release a mission-declared asset;
- create/update an HQ marker;
- issue a mission-defined waypoint/task;
- acknowledge boarding, landing or completion conditions.

Actions require local authorization, validation and an idempotency key.

## Compatibility

The bridge advertises protocol and feature versions at connection time. Unknown fields are ignored; unknown mandatory capabilities fail explicitly. Arma, ACE and mod versions are included in the session fingerprint.
