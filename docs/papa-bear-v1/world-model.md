# Local world model

## Purpose

The world model is the local source of truth for current game state. It absorbs high-frequency Arma/ACE telemetry, normalizes it into entities, tracks changes and provides retrieved context to OpenAI and deterministic services.

Raw telemetry is evidence, not the long-term representation.

## Entity types

- `PlayerState`
- `GroupState`
- `FriendlyUnitState`
- `FriendlyVehicleState`
- `KnownContactState`
- `MissionObjectiveState`
- `MarkerState`
- `SupportAssetState`
- `OperationState`
- `WeatherState`
- `WeaponSystemState`
- `CapabilityState`

## Common entity fields

Every state record must include:

```text
entityId             stable local identifier
source               player | group | side-report | sensor | mission | ACE | derived
observedAtGameTime   time of observation
receivedAtUtc        local receipt time
freshnessClass       live | recent | stale | historical
confidence           0.0–1.0 or explicit categorical equivalent
position             exact or estimated position when available
positionErrorMeters  uncertainty radius when applicable
```

Derived values must retain references to their inputs. A recommendation must not silently become a fact.

## Friendly-force picture

For own-side units and vehicles, capture where mission/network locality permits:

- side, faction, group and callsign;
- current and last-known position;
- unit class, role and crew role;
- alive, conscious, mobile and transportable state;
- damage and ACE medical summary;
- weapon/ammunition readiness summary;
- vehicle fuel, damage, crew, passenger capacity and cargo;
- current task, waypoint and behavior where available;
- support capability and availability;
- last update and communication availability.

The exact visibility of friendly units is mission-configurable. Papa Bear must not imply a current position when only an old report exists.

## Known enemy contacts

Enemy contacts are allowed only when known through the player's side information system, for example:

- player or group target knowledge;
- reports from friendly units explicitly shared with HQ;
- vehicle sensors available to the player/side;
- mission-defined intelligence reports.

Store perceived type/side, estimated position, position error, reporting source, last-seen age and confidence. Never hydrate an enemy entity from unrestricted server truth.

## State reduction

The ingestion pipeline should:

1. validate schema and source;
2. normalize units and identifiers;
3. merge by stable identity;
4. retain last-known state and uncertainty;
5. emit meaningful deltas;
6. expire or downgrade stale data;
7. update query indexes;
8. publish events to the orchestrator.

## Retrieval for reasoning

The orchestrator requests a purpose-built context packet, not the entire world model. Examples:

- nearest available transport assets;
- friendly units within 2 km with casualties;
- known threats near a proposed route;
- current weapon/weather profile;
- active operation involving the player's group.

Every context packet includes time, provenance and uncertainty so Papa Bear can phrase the answer correctly.
