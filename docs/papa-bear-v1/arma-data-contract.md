# Arma data contract

## Principles

The bridge separates four channels:

1. lightweight live telemetry;
2. explicit read queries;
3. validated action commands;
4. asynchronous events and operation updates.

All messages are versioned JSON with `schema`, `messageId`, timestamps and correlation identifiers. The native extension remains transport-focused; game semantics belong in SQF/CBA adapters and the Windows application.

## Live telemetry

Target cadence is field-specific rather than one large 4 Hz snapshot:

- player pose, current vehicle and weapon state: 4 Hz;
- player group and known contacts: 1 Hz or event-driven;
- friendly-side force picture: event-driven plus periodic reconciliation;
- weather and ACE environment: on change plus low-rate refresh;
- mission objectives, markers and capabilities: event-driven;
- active operation states: event-driven with heartbeat.

The transport may batch deltas, but each entity retains its own observation time.

## Required player state

- map/world and mission identifiers;
- ATL/ASL position and terrain elevation;
- body, view and weapon direction;
- velocity, stance, life state and damage;
- current weapon, muzzle, magazine, ammunition and optic classes;
- current zeroing and ACE scope adjustments when available;
- vehicle class, role and relevant vehicle status;
- group and callsign references.

## Friendly force data

A mission-side collector should export own-side units and assets according to policy. Required summaries include position, role, group, status, task, vehicle readiness, medical readiness and logistics capacity. Large force pictures must use deltas and paging rather than duplicating every entity in every frame.

## Known contact data

Use Arma target knowledge, group knowledge, sensor contacts and explicit side reports. Export estimated positions and uncertainty, not unrestricted object positions. Every contact records its information source and age.

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

Planned queries include:

- `query_environment`
- `query_locations`
- `query_friendly_forces`
- `query_known_contacts`
- `query_assets`
- `query_mission_state`
- `query_weapon_profile`
- `query_ace_state`
- `query_operation`

Each query has bounded range/result limits and a timeout.

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
