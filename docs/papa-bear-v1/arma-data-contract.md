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

- player position and motion: 1 second;
- current weapon, vehicle and loadout: 2 seconds;
- friendly forces and side-wide own-side known contacts: 4 seconds;
- weather, time, tasks and markers: 8 seconds;
- all sections: 30-second full reconciliation.

Every envelope contains all eight section wrappers. A ready, successfully
sampled empty array authoritatively clears that section. A failed or unavailable
section preserves its last good rows as stale. The desktop rejects out-of-order
sequences and atomically replaces current-state tables in one SQLite transaction.

## Required player state

- map/world and mission identifiers;
- ATL/ASL position and terrain elevation;
- body heading, velocity, stance, life state and damage;
- velocity, stance, life state and damage;
- current weapon, muzzle, magazine, ammunition and optic classes;
- vehicle class, role and relevant vehicle status;
- privacy-safe group reference.

Camera position, free-look direction, cursor target and unrestricted object
state are intentionally excluded.

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

Active queries include:

- `query_environment`
- `find_named_locations`
- `query_friendly_forces`
- `query_assets`
- `query_mission_capabilities`
- `query_state`

`query_state` accepts only a fixed section enum, `includeStale` and a capped
limit. It cannot accept SQL, file paths, SQF or commands. Initial OpenAI context
always contains a bounded base summary plus only deterministically selected
question-relevant state. Full mirror rows and raw snapshot payloads never cross
the prompt boundary.

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
