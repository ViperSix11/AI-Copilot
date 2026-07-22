# Codex Milestone 4: Static Map Knowledge Base

## Objective and boundary

Milestone 4 builds a read-only, fingerprinted, persistent spatial index for the
loaded Arma terrain. The client addon exports bounded static-map tiles over the
existing duplex Named Pipe; the Windows application validates and commits
complete tiles to SQLite and exposes three bounded retrieval tools. OpenAI sees
only retrieved results, never the manifest inputs, addon list, complete index,
raw tile pages, database path, or SQLite file.

This milestone preserves the Milestone 1 stateless Responses tool loop,
Milestone 2 world model, Milestone 3 friendly-force and capability behavior,
and the manual `query_environment` command. It does not expand mission-declared
support assets or capabilities and adds no route planning, landing-zone
selection or execution, support execution, ACE integration, ballistics, voice,
waypoint mutation, or arbitrary SQF.

## Existing implementation and transport audit

The merged addon runs only on an interface client. It publishes 4 Hz player
telemetry through the coalescing `telemetry|` extension channel and publishes
session, friendly-force and capability events through the non-coalescing
`event|` channel. It polls the duplex pipe at 10 Hz for validated JSON commands.
The native extension does not interpret event payloads: it frames UTF-8 lines,
writes them in 64 KiB chunks, keeps 256 outbound events and 64 inbound commands,
and reconnects. The desktop rejects lines above 1 MiB.

Static export fits that existing transport. `map-manifest-v1`, `map-tile-v1`
and `map-index-progress-v1` use `event|`; map indexing controls use the existing
desktop-to-addon command queue. Tile pages are independent events and are not
sent through replaceable telemetry. A missing page is repaired by resuming from
the first incomplete tile. There is no demonstrated payload, framing, or
ordering defect that requires a native DLL change.

The existing `query_environment` command remains a live, player-centred,
bounded terrain-object query. It is not used to populate the static cache and
continues to be available while indexing is unavailable or incomplete.

## Official Arma command and config-source audit

| Data | Available command/config source audited | Milestone 4 treatment and documented limitation |
| --- | --- | --- |
| Named locations | `nearestLocations` with an empty type array, then `text`, `type`, `locationPosition`, and `size` | Arma 3 2.14+ documents the empty type array as all available location types. Export retains the required official name, engine type and position; `size` was audited but is not persisted because Milestone 4 does not expose area-shape semantics. Search is 2D. |
| Terrain | `getTerrainHeightASL`, `surfaceNormal`, `getTerrainInfo` | Elevation is ASL. Slope is derived deterministically as the angle between the documented surface normal and vertical. Samples are a bounded grid, not a lossless heightmap. |
| Buildings | `nearestTerrainObjects` for relevant building/house/infrastructure terrain types, with `typeOf`, `getModelInfo`, and `getPosWorld` | This command is preferred over entity-only searches because the documentation warns that distant map objects may be streamed out for `nearestObjects`. Static identity is derived from map fingerprint, normalized class/model, and rounded position; Arma does not document a persistent terrain-object UUID. |
| Roads | `nearRoads`, `getRoadInfo`, and `roadsConnectedTo` | Export uses `nearRoads` and `getRoadInfo`, which supply type, width, pedestrian flag, endpoints, and bridge flag. `roadsConnectedTo` was audited but is not used: the docs warn that connections are not necessarily bidirectional and ordinary connection lookup omits some pedestrian roads. The index canonicalizes endpoints and derives intersections from endpoint clusters, so it does not claim a complete pathfinding graph. |
| Vegetation | `nearestTerrainObjects` for tree, small-tree, bush and forest types | Only per-tile counts/density and category counts are persisted. Individual plants are deliberately not indexed. |
| Water/coast | `surfaceIsWater` plus the terrain sample grid | A tile is `water`, `land`, or `coast` from sampled water/land values. Bohemia documents that pond objects are detected only when loaded in memory; therefore sea/coast sampling is supported, while complete off-camera pond coverage is explicitly unverified. |
| Map extent/terrain grid | `worldName`, `worldSize`, `getTerrainInfo` | `worldSize` is the engine-calculated square terrain side length. World-name comparisons and fingerprints normalize case because the official page warns that casing is unreliable. |
| Display grid | `mapGridPosition` at fixed reference points and the active `CfgWorlds >> worldName >> Grid` source metadata | `mapGridPosition` output format is controlled by the current world's `Grid` config. The index stores reference labels and relevant config values; it does not reverse-engineer every custom grid format. |
| World config | `configFile >> "CfgWorlds" >> worldName`, `getText`/`getNumber`, and `configSourceAddonList` | Description, map size/zone, latitude, longitude and config-source addons are fingerprint metadata. Missing optional values are represented explicitly rather than guessed. |
| Product/mod state | `productVersion`, `allAddonsInfo`, `getLoadedModsInfo` | `allAddonsInfo` documents PBO prefix, revision, patched flag and PBO hash; these are stronger invalidation inputs than display mod names. Raw prefixes, mod names/directories, Workshop IDs and local paths never enter OpenAI context or normal logs. |

Primary references are the Bohemia Interactive Community command pages for
[`nearestLocations`](https://community.bohemia.net/wiki/nearestLocations),
[`locationPosition`](https://community.bohemia.net/wiki/locationPosition),
[`getTerrainHeightASL`](https://community.bohemia.net/wiki/getTerrainHeightASL),
[`surfaceNormal`](https://community.bohemia.net/wiki/surfaceNormal),
[`nearestTerrainObjects`](https://community.bohemia.net/wiki/nearestTerrainObjects),
[`nearRoads`](https://community.bohemia.net/wiki/nearRoads),
[`getRoadInfo`](https://community.bohemia.net/wiki/getRoadInfo),
[`roadsConnectedTo`](https://community.bohemia.net/wiki/roadsConnectedTo),
[`surfaceIsWater`](https://community.bohemia.net/wiki/surfaceIsWater),
[`worldSize`](https://community.bohemia.net/wiki/worldSize),
[`getTerrainInfo`](https://community.bohemia.net/wiki/getTerrainInfo),
[`mapGridPosition`](https://community.bohemia.net/wiki/mapGridPosition),
[`allAddonsInfo`](https://community.bohemia.net/wiki/allAddonsInfo),
[`getLoadedModsInfo`](https://community.bohemia.net/wiki/getLoadedModsInfo), and
[`productVersion`](https://community.bohemia.net/wiki/productVersion).

Development packaging prefers the installed Arma Tools AddonBuilder. GitHub's
hosted Windows image does not provide Arma Tools, so CI uses a deterministic,
uncompressed fallback that implements the property entry, file entries,
terminator and contiguous data described by Bohemia's
[`PBO File Format`](https://community.bohemia.net/wiki/PBO_File_Format) page;
the page documents the trailing checksum as optional. That page explicitly
labels the internal format unofficial/undocumented, and no supported official
SDK specification for authoring a PBO on hosted CI was found. Structural
verification is automated, but engine loading of the fallback remains part of
the required live Stratis acceptance.

## Fingerprint and cache invalidation

The desktop computes lowercase hexadecimal SHA-256 over canonical UTF-8 JSON.
Object properties use the order below, numbers use invariant JSON formatting,
world/config/addon identifiers are trimmed and lower-cased, and addon records
are sorted ordinally by `(prefix, version, patched, hash)` before serialization.

Fingerprint inputs, exactly:

1. static index format version `1`;
2. normalized `worldName` and engine `worldSize`;
3. the five `getTerrainInfo` values;
4. world config class, description, `mapSize`, `mapZone`, `latitude`, and
   `longitude`, with explicit JSON null for an unavailable optional value;
5. `mapGridPosition` values at `[0,0]`, map centre, `[1000,0]`, and `[0,1000]`;
6. product short name, version, build number, build type, platform,
   architecture and Steam branch from `productVersion`;
7. every loaded `allAddonsInfo` record's prefix, revision, patched flag and
   PBO hash, capped at 4096 records; and
8. the sorted `configSourceAddonList` for the active world config.

`modIndex`, display mod name, mod directory, origin, and Workshop item ID do not
contribute because addon PBO hashes already represent loaded content and those
fields add privacy-sensitive labels without stronger invalidation. If addon
enumeration exceeds 4096 entries, a required product/config value is malformed,
or the canonical manifest exceeds 768 KiB, readiness is `failed`; the desktop
does not create a falsely reusable cache.

The local root is `%LOCALAPPDATA%\ArmA AI Bridge\MapKnowledge`. A privacy-safe
`cache-manifest-v1.json` records fingerprint, normalized world, index version,
relative database filename, readiness, progress and timestamps. Each fingerprint
uses `<fingerprint>.sqlite3`. Raw fingerprint inputs are stored only inside the
corresponding database for audit/reproduction, not in the catalog or UI.

An exact fingerprint and index-version match reuses a `ready` cache without
starting SQF export. An incomplete matching cache resumes at its first missing
tile. Any fingerprint or index-version difference marks the previously active
catalog entry `stale`, cancels its export generation, and opens a new database;
rows from a stale database are never mixed with or returned for the active map.
Corrupt databases are quarantined by leaving the file untouched, marking the
entry `failed`, and requiring an explicit later retry/new index version rather
than silently returning partial results as ready.

## Versioned protocol

All addon events retain the Milestone 3 common envelope: `schema`, `messageId`,
`missionId`, `sessionId`, `timestamp`, and monotonic `sequence`. Identifiers are
bounded ASCII-safe strings. Unknown optional fields may be ignored; missing
required fields, unknown schema versions, non-finite numbers, session mismatch,
wrong fingerprint/export ID, oversized arrays, impossible page coordinates, and
inconsistent duplicate pages are rejected without logging payload contents.

The session handshake adds feature `{ "name": "static-map-export", "version":
1 }`. Protocol major/minor remain 1.0 because all changes are additive.

### Map manifest v1

Schema: `arma-ai-bridge/arma3/map-manifest-v1`

```json
{
  "schema": "arma-ai-bridge/arma3/map-manifest-v1",
  "messageId": "message-000010",
  "missionId": "m4-stratis-live",
  "sessionId": "session-1234",
  "timestamp": 0.5,
  "sequence": 10,
  "indexVersion": 1,
  "world": {
    "name": "Stratis",
    "sizeMeters": 8192,
    "terrainInfo": [32, 256, 4, 2048, 0],
    "config": {
      "class": "Stratis",
      "description": "Stratis",
      "mapSize": 8192,
      "mapZone": 35,
      "latitude": -35,
      "longitude": 16,
      "sourceAddons": ["A3_Map_Stratis"]
    },
    "gridReferences": [
      { "position": [0, 0], "label": "000000" },
      { "position": [4096, 4096], "label": "040040" },
      { "position": [1000, 0], "label": "010000" },
      { "position": [0, 1000], "label": "000010" }
    ]
  },
  "product": {
    "shortName": "Arma3", "version": 2.20, "build": 123456,
    "buildType": "Stable", "platform": "Windows", "architecture": "x64",
    "branch": ""
  },
  "addons": [
    { "prefix": "a3\\map_stratis\\", "version": "123456", "patched": false, "hash": "abc123" }
  ],
  "export": {
    "tileSizeMeters": 512, "terrainSampleSpacingMeters": 128,
    "totalTiles": 256, "maxRecordsPerPage": 96
  }
}
```

The manifest is emitted after the session handshake, every 10 seconds while no
export is active, and every 30 seconds otherwise. The desktop, not SQF, computes
and owns the SHA-256 fingerprint.

### Map index command v1

Schema: `arma-ai-bridge/map-index-command-v1`

Start/resume:

```json
{
  "schema": "arma-ai-bridge/map-index-command-v1",
  "requestId": "map-index-1234",
  "command": "start",
  "parameters": {
    "sessionId": "session-1234",
    "exportId": "export-1234",
    "fingerprint": "64-lowercase-hex-characters",
    "indexVersion": 1,
    "startTileOrdinal": 37,
    "tileSizeMeters": 512,
    "terrainSampleSpacingMeters": 128,
    "maxRecordsPerPage": 96
  }
}
```

Cancellation uses the same envelope with `command: "cancel"` and required
`sessionId`, `exportId`, and `fingerprint`. The SQF dispatcher accepts only these
two commands and fixed numeric bounds; it never evaluates input as code.

### Map tile page v1

Schema: `arma-ai-bridge/arma3/map-tile-v1`

```json
{
  "schema": "arma-ai-bridge/arma3/map-tile-v1",
  "messageId": "message-000011",
  "missionId": "m4-stratis-live",
  "sessionId": "session-1234",
  "timestamp": 1.5,
  "sequence": 11,
  "exportId": "export-1234",
  "fingerprint": "64-lowercase-hex-characters",
  "indexVersion": 1,
  "tile": {
    "ordinal": 37, "column": 5, "row": 2,
    "minX": 2560, "minY": 1024, "maxX": 3072, "maxY": 1536
  },
  "pageIndex": 0,
  "pageCount": 2,
  "records": [
    { "kind": "location", "name": "Agia Marina", "locationType": "NameCityCapital", "positionASL": [2915.2, 6164.5, 21.9] },
    { "kind": "terrain", "positionASL": [2560, 1024, 12.4], "slopeDegrees": 4.2, "water": false },
    { "kind": "building", "class": "Land_i_House_Big_01_V1_F", "model": "i_house_big_01_v1_f.p3d", "terrainType": "HOUSE", "positionASL": [2700, 1100, 18.2] },
    { "kind": "road", "roadType": "ROAD", "widthMeters": 6, "pedestrian": false, "beginASL": [2600, 1100, 17], "endASL": [2660, 1120, 17.5], "bridge": false },
    { "kind": "vegetation", "treeCount": 44, "bushCount": 15, "forestCount": 0, "densityPerHectare": 2.25 },
    { "kind": "water", "classification": "land", "waterSamples": 0, "landSamples": 25 }
  ]
}
```

Records are a closed discriminated union. A page has at most 96 records, a tile
at most 256 pages, and the serialized event must remain below 768 KiB. Static
keys are derived by the desktop from fingerprint plus canonical record geometry;
raw object strings, `netId`, owner IDs, and object references are absent.

### Map index progress v1

Schema: `arma-ai-bridge/arma3/map-index-progress-v1`

```json
{
  "schema": "arma-ai-bridge/arma3/map-index-progress-v1",
  "messageId": "message-000012",
  "missionId": "m4-stratis-live",
  "sessionId": "session-1234",
  "timestamp": 2.0,
  "sequence": 12,
  "exportId": "export-1234",
  "fingerprint": "64-lowercase-hex-characters",
  "indexVersion": 1,
  "status": "indexing",
  "completedTiles": 38,
  "totalTiles": 256,
  "nextTileOrdinal": 38,
  "errorCode": null
}
```

Status is `started`, `indexing`, `completed`, `cancelled`, or `failed`.
`errorCode` is null or one of a closed, privacy-safe set; raw SQF exceptions and
payload text are not transported or logged.

## SQLite schema and migration strategy

Milestone 4 uses `Microsoft.Data.Sqlite` and the bundled SQLite R*Tree module.
SQLite documents R*Tree's first column as the integer row ID and remaining
coordinates as real values. Every logical spatial row therefore has an integer
primary key mirrored into a two-dimensional R*Tree table.

Migration 1 creates:

- `schema_migrations(version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL)`;
- `map_manifest(id INTEGER PRIMARY KEY CHECK(id=1), fingerprint TEXT UNIQUE,
  index_version INTEGER, canonical_json TEXT, world_name TEXT, world_size REAL,
  tile_size REAL, total_tiles INTEGER, readiness TEXT, completed_tiles INTEGER,
  created_utc TEXT, updated_utc TEXT, last_error TEXT NULL)`;
- `tile_progress(tile_ordinal INTEGER PRIMARY KEY, column_index INTEGER,
  row_index INTEGER, min_x REAL, min_y REAL, max_x REAL, max_y REAL,
  page_count INTEGER, completed_utc TEXT)`;
- `locations(id INTEGER PRIMARY KEY, stable_key TEXT UNIQUE, official_name TEXT,
  normalized_name TEXT, location_type TEXT, x REAL, y REAL, z_asl REAL)` and
  `location_rtree(id,min_x,max_x,min_y,max_y)`;
- `terrain_samples(id INTEGER PRIMARY KEY, stable_key TEXT UNIQUE, x REAL,
  y REAL, elevation_asl REAL, slope_degrees REAL, water INTEGER)` and
  `terrain_rtree`;
- `buildings(id INTEGER PRIMARY KEY, stable_key TEXT UNIQUE, class_name TEXT,
  model_name TEXT, terrain_type TEXT, x REAL, y REAL, z_asl REAL)` and
  `building_rtree`;
- `road_segments(id INTEGER PRIMARY KEY, stable_key TEXT UNIQUE, road_type TEXT,
  width_meters REAL, pedestrian INTEGER, bridge INTEGER, begin_x REAL,
  begin_y REAL, begin_z REAL, end_x REAL, end_y REAL, end_z REAL)` and a
  bounding-box `road_rtree`;
- `road_intersections(id INTEGER PRIMARY KEY, stable_key TEXT UNIQUE, x REAL,
  y REAL, z_asl REAL, connected_segments INTEGER)` and `intersection_rtree`;
- `tile_summaries(id INTEGER PRIMARY KEY, tile_ordinal INTEGER UNIQUE,
  vegetation counts/density, water_classification, water_samples, land_samples,
  bounds)` and `tile_rtree`.

`PRAGMA user_version` and `schema_migrations` must agree. Migrations run in one
transaction and are forward-only; a database with a newer unknown version is
`failed`, never downgraded. Foreign keys are enabled. One service owns writes.
Each fully assembled tile is committed with all entity and RTree upserts plus
its `tile_progress` marker in one transaction. Partial pages remain memory-only,
so a crash cannot mark a partial tile complete. Road intersections are rebuilt
deterministically from endpoint clusters after all tiles commit.

Spatial reads first use an RTree bounding square, then exact Euclidean distance
and deterministic tie-breaking. Name search uses a normalized indexed column
and parameterized SQL. SQLite transactions, not ad-hoc file locks, provide
atomicity. See Microsoft's
[`Microsoft.Data.Sqlite`](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/)
guidance and SQLite's official [R*Tree documentation](https://www.sqlite.org/rtree.html).

## Scheduling, paging, cancellation and recovery

- Default tiles are 512 m squares in row-major order from southwest to
  northeast. Edge tiles clamp to `worldSize`.
- Terrain/water samples default to a 128 m grid including tile edges. The
  desktop accepts only tile sizes 256-1024 m, sample spacing 32-256 m and page
  sizes 16-96; the manifest's fixed values are reused in the start command.
- Export runs in scheduled SQF. It checks the active generation before every
  category, page and tile; yields with `uiSleep` between categories/pages and at
  least once per tile; and never runs on `EachFrame`.
- Locations, buildings, roads, vegetation and terrain/water are collected
  separately for a tile. Candidate objects are filtered to half-open tile
  bounds (the world edge is inclusive), preventing ordinary duplicates.
- The desktop assembles pages by session/export/fingerprint/tile. Duplicate
  identical pages are ignored; conflicting duplicates fail the attempt.
- Disconnect, app shutdown, new session, changed fingerprint, and explicit
  cancellation invalidate the export generation. Already committed tiles stay
  resumable. Incomplete page sets are discarded.
- Resume begins at the smallest missing row-major tile. Re-exported committed
  rows are deterministic upserts, but a tile is not skipped unless its local
  completion marker exists.
- A recoverable disconnect leaves readiness `partial` if at least one tile is
  committed. A later matching manifest resumes automatically. Schema errors,
  impossible bounds, resource-limit violations, SQLite corruption, or SQF's
  closed `failed` status set readiness `failed` with a privacy-safe error code.

## Readiness semantics

| State | Exact meaning | Tool behavior |
| --- | --- | --- |
| `unavailable` | No compatible active session/feature/manifest or no committed current-fingerprint data | Static tools return state and a bounded unavailable error; `query_environment` remains usable. |
| `indexing` | A validated current-fingerprint export is actively running | Tools may return only committed-tile results, include progress/coverage and label them incomplete. |
| `partial` | Matching committed tiles exist but no export is currently active (disconnect, cancellation, restart before resume) | Queries are allowed only against completed tile coverage and disclose incompleteness. |
| `ready` | Every expected tile is atomically committed and derived intersections completed | Full static tools are enabled for the fingerprint. |
| `stale` | A retained cache does not match the active fingerprint or index version | Stale rows are never queried or sent to OpenAI. |
| `failed` | Validation, resource, migration, corruption, or exporter failure prevents a trustworthy active index | Tools return only the closed error code and no suspect rows. |

`ready` is fingerprint-specific, not mission-specific. A new mission/session on
the same exact map/mod fingerprint may reuse it. Diagnostics always shows state,
coverage, current fingerprint and error; Papa Bear must not phrase partial data
as complete map knowledge.

## Local read tools and prompt boundary

`find_nearest_locations` requires `maxDistanceMeters` (100-50000),
`locationTypes` (0-16 validated engine type strings), and `limit` (1-20). The
origin is the current player position from the Milestone 2 world model. Results
contain official name/type, privacy-safe local ID, position, distance and
bearing, plus readiness/coverage.

`find_locations_by_name` requires `name` (1-80 characters) and `limit` (1-20).
It performs normalized local name search and adds distance/bearing when current
player position is available.

`query_map_area` requires `radiusMeters` (100-5000), `categories` (one or more
of `location`, `terrain`, `building`, `road`, `vegetation`, `water`), and
`limitPerCategory` (1-25). Its origin is the current player. It returns bounded
nearby location/building/road/intersection rows and aggregate elevation, slope,
vegetation and water/coast summaries. It does not compute a route, landing zone,
firing solution, or action.

All three OpenAI definitions use strict schemas with every property required
and `additionalProperties: false`, matching OpenAI's official
[function-calling requirements](https://developers.openai.com/api/docs/guides/function-calling).
Arguments are validated again locally. Tool output is capped at 64 KiB and
contains at most the requested rows. The existing `store: false`, stateless
reasoning replay and three-round tool limit remain unchanged.

Map names, location names, class names and model names are untrusted facts. They
are length/control-character bounded and cannot change policy or tool routing.
Never include canonical manifests, addon hashes/prefixes, mod labels,
fingerprints beyond a short diagnostics tag, raw page JSON, SQLite schema/files,
database paths, Windows account paths, profile names, UIDs, raw `netId`, mission
registries, or unrestricted world state in an OpenAI request or application log.
The local diagnostics tab may show the full fingerprint and database path as
explicitly required, but neither is copied into assistant context.

The OpenAI developer-docs MCP installation was attempted before implementation
and Windows returned `Access is denied` even with approved elevation. The
official OpenAI web documentation was therefore used as the required fallback.

## Resource limits

- 1 MiB desktop line hard cap; 768 KiB manifest/tile soft cap.
- 4096 addon records; identifiers 128 characters, names/descriptions 256,
  model/prefix paths 384; control characters rejected or removed by field rule.
- 96 records/page, 256 pages/tile, 4096 pending pages, and one active export.
- 512 m default tiles, 128 m samples, maximum 16384 tiles and 5,000,000 static
  rows per database. A limit breach fails closed rather than dropping records
  while reporting `ready`.
- SQLite commands are parameterized; writes are serialized; tool result rows
  and JSON sizes are bounded. No SQL, filesystem path or SQF text comes from
  OpenAI arguments.
- Cache files persist by fingerprint. This milestone does not automatically
  delete stale caches; future cache eviction requires a separately specified,
  recoverable policy.

## Deterministic automated acceptance

The Windows suite must prove:

1. All four new JSON schemas have exact discriminators, required common fields,
   closed nested objects/unions and matching valid fixtures; malformed,
   cross-session, wrong-fingerprint, oversized and conflicting-page messages
   fail deterministically.
2. SQF contract fixtures advertise `static-map-export`, contain the expected
   command allowlist and collection commands, yield during export, and contain
   no profile name, UID, raw `netId`, arbitrary compile/eval, support execution,
   route, landing-zone, ACE, ballistics or voice behavior.
3. Canonical fingerprint output is stable under addon/config input ordering and
   world-name casing, and changes for every specified material input.
4. Migration 1 creates all tables/indexes/RTree virtual tables, records one
   migration, is idempotent, rejects newer unknown versions, and rolls back a
   failed migration.
5. Tile pages commit atomically only when complete; duplicates are idempotent;
   conflicting/missing pages do not mark completion; restart resumes at the
   first missing tile.
6. A changed fingerprint marks the old catalog entry stale and never mixes its
   rows. A ready exact-match cache starts no SQF export command.
7. SQLite spatial queries return deterministic nearest locations, name matches,
   buildings, roads/intersections, terrain aggregates, vegetation density and
   water/coast classification at boundaries and tie distances.
8. Readiness transitions cover unavailable, indexing, partial, ready, stale and
   failed, including disconnect, cancellation, recovery and complete rebuild.
9. The three tool builders enforce ranges/enums/limits, expose incomplete
   coverage, and keep canonical manifests, addon data, raw tile payloads,
   database paths and raw game IDs out of output.
10. OpenAI requests advertise and route all three strict static tools while
    preserving the four existing tools, `store: false`, stateless continuation,
    cancellation and safe tool errors.
11. Existing Milestone 1-3 telemetry, manual `query_environment`, world-model,
    friendly-force, asset and capability tests remain green without expanding
    support/capability collection.
12. Repository verification, Release tests, WPF publish, x64 native build and
    PBO packaging succeed. The uploaded development artifact contains the
    matching published app, `arma_ai_bridge_x64.dll`, `mod.cpp`, and a non-empty
    `addons/arma_ai_bridge_client.pbo` built from the same checkout.

## Exact live Arma acceptance

Live acceptance is a merge gate and is not replaced by automated fixtures.

### First-time Stratis index

1. Delete or move only the Stratis fingerprint entry shown by Map Knowledge
   diagnostics and its exact database file. Do not delete the MapKnowledge root
   recursively. Build `scripts/build.ps1`; confirm the packaged mod contains the
   newly built PBO and matching x64 DLL, then launch that package and the newly
   published desktop app.
2. In Eden, create a minimal local WEST mission on Stratis with one playable
   rifleman and no support/capability registry declarations. Set
   `AAB_missionId = "m4-stratis-live";` before addon post-init. Do not add ACE.
3. Preview the mission and open **Map Knowledge**. Within 12 seconds verify the
   exact Stratis fingerprint, index version 1, database path, `indexing` state,
   non-zero tile progress and no UI/RPT script error. World State must still
   show the Milestone 3 session and friendly force.
4. While indexing, move and use the manual Map Query. Confirm 4 Hz telemetry,
   `query_environment`, friendly-force tools and assistant UI remain responsive.
   Stop/restart the desktop once after at least five tiles. Verify `partial`,
   then automatic resume from the first missing tile without losing completed
   progress or changing the fingerprint.
5. Let the index finish. Verify `ready`, all expected tiles complete, non-zero
   counts for official named locations, terrain, buildings, roads and
   vegetation, both land/water samples, road intersections, no error, and a
   non-empty SQLite file. Progress must never exceed the total.
6. Ask “What is the nearest named settlement?” Confirm
   `find_nearest_locations` is used and the official Stratis name, measured
   distance and bearing agree with diagnostics/map inspection. Ask for a partial
   name and confirm `find_locations_by_name`; ask for terrain/building/road/
   vegetation/water around the player and confirm `query_map_area` returns only
   the bounded retrieved area.
7. Inspect OpenAI request/tool diagnostics and logs. Confirm no complete index,
   raw tile page, manifest/addon list, addon hash/prefix, database path, profile
   identity, UID, raw `netId`, mission support registry, API key, prompt or full
   tool result is logged or sent. Confirm no support/capability data appeared
   unless the mission explicitly declared it.

### Cached Stratis restart

8. Exit the mission, desktop app and Arma normally. Relaunch the same packaged
   app/mod and the same Stratis mission/mod set. Within 12 seconds diagnostics
   must move directly to `ready` with the identical fingerprint, database path,
   counts and completed-tile count. No `start` map-index command or tile progress
   may occur. Repeat the three map tools and verify identical deterministic
   results at the same player position.

### Altis cache-reuse smoke test

9. Run a minimal WEST mission on Altis with `AAB_missionId = "m4-altis-smoke";`.
   Verify a different fingerprint/database, `indexing` progress and responsive
   telemetry. A full first-time Altis completion is not the Stratis merge gate,
   but allow at least ten tiles to commit, stop/restart the desktop, and verify
   the identical Altis fingerprint resumes at the first missing tile rather than
   rebuilding completed tiles. If an existing ready Altis cache is available,
   verify direct cache reuse instead.
10. Return to Stratis and verify its prior ready fingerprint/cache is selected
    without cross-map counts or stale Altis rows.

Milestone 4 may be opened as a draft PR after automated/build acceptance, but it
must not be marked ready or merged until steps 1-8 pass in a live Arma session.
Any pond-coverage claim beyond the documented `surfaceIsWater` streaming
behavior remains explicitly unverified.
