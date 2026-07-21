# Architecture

## Data paths

```text
Telemetry (continuous)
Arma SQF -> callExtension telemetry|JSON -> native queue -> duplex Named Pipe -> WPF

Map query (on demand)
WPF -> command JSON -> duplex Named Pipe -> native inbound queue
    -> SQF poll -> query_environment -> callExtension query-result|JSON
    -> native outbound queue -> WPF
```

The native extension never calls cloud APIs and never blocks `callExtension` on network work. It only enqueues outgoing data, buffers incoming commands and returns immediately.

## Continuous telemetry

Continuous telemetry contains only inexpensive, perspective-scoped state:

- map name, size, grid and time
- player position, view direction, stance, damage and weapon state
- current vehicle state
- contacts known by the player or group
- current vehicle sensor contacts

No terrain scan is part of the periodic snapshot.

## Dynamic environment queries

`query_environment` currently accepts:

- origin: `player`
- shape: `circle` or `cone`
- direction: `view` or `body`
- radius/range: 25 to 1,500 metres
- cone angle: 5 to 180 degrees
- categories: `building`, `vegetation`, `road`, `wall`, `rock`
- maximum results per category: 1 to 50

The result includes total matches, nearest distance and a bounded object list with position, bearing and relative bearing. Aggregate analysis includes vegetation density and buildings near dense vegetation.

## Performance boundaries

The Windows application validates user input. SQF validates and clamps the same values again, so malformed or future AI-generated tool calls cannot trigger an unbounded full-map scan. Larger queries will later use tiled caching.
