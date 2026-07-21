# Architecture

## Design principle

ArmA AI Bridge is deliberately perspective-bound. It receives what the local player, the player's group, and the current vehicle sensors know. It does not consume server ground truth.

## Components

### Arma client addon

The SQF addon starts after the local player exists. It builds serializable HashMaps and uses Arma 3's `toJSON` command. It never sends Arma objects directly to JSON.

The addon collects:

- player and vehicle state
- view vector and heading
- known targets from the player and group target sets
- `targetKnowledge` estimates rather than exact hidden positions
- `getSensorTargets` for the current vehicle
- terrain probes centered ahead of the player's view

### Native extension

`arma_ai_bridge_x64.dll` implements the official x64 Arma extension exports:

- `RVExtensionVersion`
- `RVExtension`
- `RVExtensionArgs`

`callExtension` must return quickly. Incoming telemetry is therefore copied into a bounded in-memory queue. A background worker owns the Named Pipe connection and forwards newline-delimited JSON.

### Windows application

The WPF application is the Named Pipe server. It:

- accepts one Arma bridge connection at a time
- parses and displays the latest JSON snapshot
- writes diagnostic logs
- stores encrypted API credentials

## Pipe protocol

Pipe name:

```text
ArmaAiBridge.Arma3.Telemetry
```

Transport:

- UTF-8
- one JSON document per line
- maximum accepted line length: 1 MiB
- extension queue capacity: 256 messages
- oldest message is discarded when the queue is full

## Future request path

Version 0.1 is telemetry-only. A second command pipe will later support explicit AI tool requests such as:

```text
query_environment(direction, distance, width)
query_known_contacts(max_age, max_distance)
query_group_status()
```

Periodic snapshots are retained as a fallback for immediate answers and connection diagnostics.
