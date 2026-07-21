# Message schemas

The pipe transports newline-delimited UTF-8 JSON. Every message has a `schema` discriminator.

## Telemetry

Schema: `arma-ai-bridge/arma3/telemetry-v1`

Sent by Arma at 4 Hz. Contains player, map, vehicle, known-contact and sensor-contact state. It intentionally contains no precomputed environment probes.

## Command

Schema: `arma-ai-bridge/command-v1`

Sent by the Windows application. Version 0.2.0 supports `query_environment`. Every command has a unique `requestId`.

## Query result

Schema: `arma-ai-bridge/arma3/query-result-v1`

Sent by Arma after executing a command. The matching `requestId` correlates the result with the original command. `ok=false` carries an `error`; `ok=true` carries `result`.

Canonical examples and JSON Schemas are stored under `samples/` and `schemas/`.
