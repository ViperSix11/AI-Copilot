# Static map knowledge base

## Objective

Papa Bear must have complete, locally queryable static knowledge of the loaded map. The full index is built once per map/mod fingerprint and reused in later sessions. OpenAI never receives the complete index; it receives retrieved results.

## Readiness model

At mission start:

1. calculate a fingerprint from world name, world size, Arma version, loaded mod set and relevant config hashes;
2. load a matching cached index when available;
3. otherwise build the index asynchronously in bounded tiles;
4. expose `mapKnowledgeStatus = unavailable | indexing | partial | ready | stale`;
5. do not claim complete map knowledge before status is `ready`.

A first-time index may take time. Player telemetry and direct live queries continue in degraded mode while indexing runs.

## Indexed data

- named locations and types (`NameCity`, `NameVillage`, `NameLocal`, bases and mission locations);
- position, size and orientation of locations;
- buildings and relevant classes;
- roads, road segments, intersections, bridges and connectivity;
- vegetation and forest-density tiles;
- water, coastlines and traversability;
- terrain elevation, slope, surface type and line-of-sight helpers;
- walls, rocks and major obstacles;
- airports, runways, helipads and logistics infrastructure;
- candidate landing zones with score components;
- map grid conversions;
- mission-added static objects and markers in a session overlay.

Arma's `nearestLocations` results are sorted from nearest to farthest and form the basis for named-location retrieval. Location names, types and positions must be stored directly rather than inferred from building density.

## Storage

Use SQLite with spatially indexed tile/entity tables. A lightweight R-tree or equivalent index must support radius, cone, corridor, route and nearest-neighbor queries.

Suggested tables:

```text
map_manifest
locations
terrain_tiles
buildings
roads
road_nodes
vegetation_tiles
water_tiles
obstacles
landing_zone_candidates
session_overlay
```

## Retrieval tools

- `find_nearest_locations`
- `find_locations_by_name`
- `query_map_area`
- `find_route`
- `find_landing_zones`
- `evaluate_line_of_sight`
- `evaluate_cover`
- `get_terrain_profile`

Results include distance, bearing, confidence, source and index version.

## Dynamic overlay

Destroyed buildings, placed fortifications, mission objects, blocked roads and temporary landing hazards are stored separately from the static index. Live data overrides static assumptions and expires according to mission rules.

## Performance constraints

- indexing work is time-sliced to avoid mission-frame stalls;
- tile size and detail level are configurable;
- large scans are paged and cancellable;
- cached data is invalidated by fingerprint changes;
- raw map entities are never placed wholesale into an OpenAI prompt.
