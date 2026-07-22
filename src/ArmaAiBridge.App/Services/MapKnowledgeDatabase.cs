using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArmaAiBridge.App.Models;
using Microsoft.Data.Sqlite;

namespace ArmaAiBridge.App.Services;

public sealed class MapKnowledgeDatabase
{
    public const int SchemaVersion = 1;
    private const int MaximumRows = 5_000_000;
    private readonly string _rootDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private string _databasePath = string.Empty;
    private string _fingerprint = string.Empty;
    private MapManifestData? _manifest;

    public MapKnowledgeDatabase(string? rootDirectory = null, TimeProvider? timeProvider = null)
    {
        _rootDirectory = rootDirectory ?? AppPaths.MapKnowledgeDirectory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string DatabasePath { get { lock (_gate) return _databasePath; } }
    public string Fingerprint { get { lock (_gate) return _fingerprint; } }
    public MapManifestData? ActiveManifest { get { lock (_gate) return _manifest; } }

    public MapKnowledgeDiagnostics Activate(MapManifestData manifest, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Directory.CreateDirectory(_rootDirectory);
        lock (_gate)
        {
            InvalidatePriorFingerprint(fingerprint);
            _manifest = manifest;
            _fingerprint = fingerprint;
            _databasePath = Path.Combine(_rootDirectory, fingerprint + ".sqlite3");
            using SqliteConnection connection = Open();
            ApplyMigrations(connection);
            EnsureManifest(connection, manifest, fingerprint);
            WriteCatalog(connection);
            return ReadDiagnostics(connection, exportActive: false, pendingPages: 0);
        }
    }

    public void MarkCurrentStale()
    {
        lock (_gate)
        {
            if (_databasePath.Length == 0 || !File.Exists(_databasePath)) return;
            using SqliteConnection connection = Open();
            SetReadinessCore(connection, MapKnowledgeReadiness.Stale, "");
            WriteCatalog(connection);
        }
    }

    public void SetReadiness(MapKnowledgeReadiness readiness, string error = "")
    {
        lock (_gate)
        {
            EnsureActive();
            using SqliteConnection connection = Open();
            SetReadinessCore(connection, readiness, SanitizeError(error));
            WriteCatalog(connection);
        }
    }

    public MapKnowledgeReadiness GetReadiness()
    {
        lock (_gate)
        {
            if (_databasePath.Length == 0 || !File.Exists(_databasePath)) return MapKnowledgeReadiness.Unavailable;
            try
            {
                using SqliteConnection connection = Open();
                return ParseReadiness(ScalarString(connection, "SELECT readiness FROM map_manifest WHERE id=1"));
            }
            catch (SqliteException) { return MapKnowledgeReadiness.Failed; }
        }
    }

    public int GetFirstMissingTile()
    {
        lock (_gate)
        {
            EnsureActive();
            using SqliteConnection connection = Open();
            HashSet<int> complete = new();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT tile_ordinal FROM tile_progress ORDER BY tile_ordinal";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) complete.Add(reader.GetInt32(0));
            int total = _manifest!.Export.TotalTiles;
            for (int index = 0; index < total; index++)
                if (!complete.Contains(index)) return index;
            return total;
        }
    }

    public bool IsReady()
        => GetReadiness() == MapKnowledgeReadiness.Ready && GetFirstMissingTile() == ActiveManifest?.Export.TotalTiles;

    public void CommitTile(MapTileBounds tile, IEnumerable<string> recordPages)
    {
        IReadOnlyList<string> pages = recordPages as IReadOnlyList<string> ?? recordPages.ToArray();
        lock (_gate)
        {
            EnsureActive();
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            if (TileAlreadyComplete(connection, transaction, tile.Ordinal))
            {
                transaction.Rollback();
                return;
            }

            int trees = 0, bushes = 0, forests = 0, waterSamples = 0, landSamples = 0;
            double density = 0;
            string waterClass = "unknown";
            long existingRows = ScalarLong(connection,
                "SELECT (SELECT COUNT(*) FROM locations)+(SELECT COUNT(*) FROM terrain_samples)+(SELECT COUNT(*) FROM buildings)+(SELECT COUNT(*) FROM road_segments)");
            int inserted = 0;
            foreach (string page in pages)
            {
                using JsonDocument document = JsonDocument.Parse(page);
                foreach (JsonElement record in document.RootElement.EnumerateArray())
                {
                    string kind = record.GetProperty("kind").GetString()!;
                    switch (kind)
                    {
                        case "location": UpsertLocation(connection, transaction, record); break;
                        case "terrain": UpsertTerrain(connection, transaction, record); break;
                        case "building": UpsertBuilding(connection, transaction, record); break;
                        case "road": UpsertRoad(connection, transaction, record); break;
                        case "vegetation":
                            trees = record.GetProperty("treeCount").GetInt32();
                            bushes = record.GetProperty("bushCount").GetInt32();
                            forests = record.GetProperty("forestCount").GetInt32();
                            density = record.GetProperty("densityPerHectare").GetDouble();
                            break;
                        case "water":
                            waterClass = record.GetProperty("classification").GetString()!;
                            waterSamples = record.GetProperty("waterSamples").GetInt32();
                            landSamples = record.GetProperty("landSamples").GetInt32();
                            break;
                    }
                    inserted++;
                    if (existingRows + inserted > MaximumRows) throw new InvalidDataException("database_row_limit_exceeded");
                }
            }

            UpsertTileSummary(connection, transaction, tile, trees, bushes, forests, density, waterClass, waterSamples, landSamples);
            using (SqliteCommand command = CreateCommand(connection, transaction, """
                INSERT INTO tile_progress(tile_ordinal,column_index,row_index,min_x,min_y,max_x,max_y,page_count,completed_utc)
                VALUES(@ordinal,@column,@row,@minX,@minY,@maxX,@maxY,@pages,@utc)
                """))
            {
                Add(command, "@ordinal", tile.Ordinal); Add(command, "@column", tile.Column); Add(command, "@row", tile.Row);
                Add(command, "@minX", tile.MinX); Add(command, "@minY", tile.MinY); Add(command, "@maxX", tile.MaxX); Add(command, "@maxY", tile.MaxY);
                Add(command, "@pages", pages.Count); Add(command, "@utc", UtcNow());
                command.ExecuteNonQuery();
            }
            using (SqliteCommand command = CreateCommand(connection, transaction, """
                UPDATE map_manifest SET completed_tiles=(SELECT COUNT(*) FROM tile_progress), updated_utc=@utc WHERE id=1
                """))
            {
                Add(command, "@utc", UtcNow()); command.ExecuteNonQuery();
            }
            transaction.Commit();
            WriteCatalog(connection);
        }
    }

    public void CompleteIndex()
    {
        lock (_gate)
        {
            EnsureActive();
            using SqliteConnection connection = Open();
            int complete = Convert.ToInt32(ScalarLong(connection, "SELECT COUNT(*) FROM tile_progress"), CultureInfo.InvariantCulture);
            if (complete != _manifest!.Export.TotalTiles) throw new InvalidDataException("incomplete_index");
            RebuildRoadIntersections(connection);
            SetReadinessCore(connection, MapKnowledgeReadiness.Ready, "");
            WriteCatalog(connection);
        }
    }

    public MapKnowledgeDiagnostics GetDiagnostics(bool exportActive = false, int pendingPages = 0)
    {
        lock (_gate)
        {
            if (_databasePath.Length == 0 || !File.Exists(_databasePath))
                return new MapKnowledgeDiagnostics(MapKnowledgeReadiness.Unavailable, "", "", "", SchemaVersion,
                    0, 0, 0, 0, 0, 0, 0, 0, "", exportActive, pendingPages);
            try
            {
                using SqliteConnection connection = Open();
                return ReadDiagnostics(connection, exportActive, pendingPages);
            }
            catch (SqliteException)
            {
                return new MapKnowledgeDiagnostics(MapKnowledgeReadiness.Failed, _manifest?.WorldName ?? "", _fingerprint,
                    _databasePath, SchemaVersion, 0, _manifest?.Export.TotalTiles ?? 0, 0, 0, 0, 0, 0, 0,
                    "sqlite_failure", exportActive, pendingPages);
            }
        }
    }

    public IReadOnlyList<MapLocationResult> FindNearestLocations(
        double originX, double originY, double maximumDistance, IReadOnlyList<string> locationTypes, int limit)
    {
        lock (_gate)
        {
            EnsureQueryable();
            using SqliteConnection connection = Open();
            StringBuilder sql = new("""
                SELECT l.stable_key,l.official_name,l.location_type,l.x,l.y,l.z_asl
                FROM location_rtree r JOIN locations l ON l.id=r.id
                WHERE r.max_x>=@minX AND r.min_x<=@maxX AND r.max_y>=@minY AND r.min_y<=@maxY
                """);
            if (locationTypes.Count > 0)
            {
                sql.Append(" AND lower(l.location_type) IN (");
                sql.Append(string.Join(',', Enumerable.Range(0, locationTypes.Count).Select(index => "@type" + index)));
                sql.Append(')');
            }
            using SqliteCommand command = connection.CreateCommand(); command.CommandText = sql.ToString();
            AddBounds(command, originX, originY, maximumDistance);
            for (int i = 0; i < locationTypes.Count; i++) Add(command, "@type" + i, locationTypes[i].ToLowerInvariant());
            List<MapLocationResult> values = new();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                double x = reader.GetDouble(3), y = reader.GetDouble(4);
                double distance = Distance(originX, originY, x, y);
                if (distance <= maximumDistance)
                    values.Add(new MapLocationResult(Alias(reader.GetString(0)), reader.GetString(1), reader.GetString(2), x, y,
                        reader.GetDouble(5), distance, Bearing(originX, originY, x, y)));
            }
            return values.OrderBy(value => value.DistanceMeters).ThenBy(value => value.OfficialName, StringComparer.Ordinal)
                .ThenBy(value => value.EntityId, StringComparer.Ordinal)
                .Take(limit).ToArray();
        }
    }

    public IReadOnlyList<MapLocationResult> FindLocationsByName(string name, int limit, double? originX, double? originY)
    {
        lock (_gate)
        {
            EnsureQueryable();
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT stable_key,official_name,location_type,x,y,z_asl FROM locations
                WHERE normalized_name LIKE @name ESCAPE '\' ORDER BY
                CASE WHEN normalized_name=@exact THEN 0 WHEN normalized_name LIKE @prefix ESCAPE '\' THEN 1 ELSE 2 END,
                official_name COLLATE NOCASE,stable_key LIMIT @limit
                """;
            string normalized = NormalizeSearch(name);
            string escaped = EscapeLike(normalized);
            Add(command, "@name", "%" + escaped + "%"); Add(command, "@exact", normalized);
            Add(command, "@prefix", escaped + "%"); Add(command, "@limit", limit);
            List<MapLocationResult> values = new();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                double x = reader.GetDouble(3), y = reader.GetDouble(4);
                double distance = originX.HasValue && originY.HasValue ? Distance(originX.Value, originY.Value, x, y) : -1;
                double bearing = distance >= 0 ? Bearing(originX!.Value, originY!.Value, x, y) : -1;
                values.Add(new MapLocationResult(Alias(reader.GetString(0)), reader.GetString(1), reader.GetString(2), x, y,
                    reader.GetDouble(5), distance, bearing));
            }
            return values;
        }
    }

    public IReadOnlyList<MapBuildingResult> QueryBuildings(double originX, double originY, double radius, int limit)
    {
        lock (_gate)
        {
            EnsureQueryable(); using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT b.stable_key,b.class_name,b.model_name,b.terrain_type,b.x,b.y,b.z_asl
                FROM building_rtree r JOIN buildings b ON b.id=r.id
                WHERE r.max_x>=@minX AND r.min_x<=@maxX AND r.max_y>=@minY AND r.min_y<=@maxY
                """;
            AddBounds(command, originX, originY, radius); List<MapBuildingResult> values = new();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                double x = reader.GetDouble(4), y = reader.GetDouble(5), distance = Distance(originX, originY, x, y);
                if (distance <= radius) values.Add(new(Alias(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), x, y, reader.GetDouble(6), distance, Bearing(originX, originY, x, y)));
            }
            return values.OrderBy(value => value.DistanceMeters).ThenBy(value => value.EntityId, StringComparer.Ordinal).Take(limit).ToArray();
        }
    }

    public IReadOnlyList<MapRoadResult> QueryRoads(double originX, double originY, double radius, int limit)
    {
        lock (_gate)
        {
            EnsureQueryable(); using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.stable_key,s.road_type,s.width_meters,s.pedestrian,s.bridge,s.begin_x,s.begin_y,s.end_x,s.end_y
                FROM road_rtree r JOIN road_segments s ON s.id=r.id
                WHERE r.max_x>=@minX AND r.min_x<=@maxX AND r.max_y>=@minY AND r.min_y<=@maxY
                """;
            AddBounds(command, originX, originY, radius); List<MapRoadResult> values = new();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                double bx = reader.GetDouble(5), by = reader.GetDouble(6), ex = reader.GetDouble(7), ey = reader.GetDouble(8);
                double distance = DistanceToSegment(originX, originY, bx, by, ex, ey);
                if (distance <= radius) values.Add(new(Alias(reader.GetString(0)), reader.GetString(1), reader.GetDouble(2),
                    reader.GetBoolean(3), reader.GetBoolean(4), bx, by, ex, ey, distance));
            }
            return values.OrderBy(value => value.DistanceMeters).ThenBy(value => value.EntityId, StringComparer.Ordinal).Take(limit).ToArray();
        }
    }

    public IReadOnlyList<MapIntersectionResult> QueryIntersections(double originX, double originY, double radius, int limit)
    {
        lock (_gate)
        {
            EnsureQueryable(); using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT i.stable_key,i.x,i.y,i.z_asl,i.connected_segments
                FROM intersection_rtree r JOIN road_intersections i ON i.id=r.id
                WHERE r.max_x>=@minX AND r.min_x<=@maxX AND r.max_y>=@minY AND r.min_y<=@maxY
                """;
            AddBounds(command, originX, originY, radius); List<MapIntersectionResult> values = new();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                double x = reader.GetDouble(1), y = reader.GetDouble(2), distance = Distance(originX, originY, x, y);
                if (distance <= radius) values.Add(new(Alias(reader.GetString(0)), x, y, reader.GetDouble(3), reader.GetInt32(4), distance));
            }
            return values.OrderBy(value => value.DistanceMeters).ThenBy(value => value.EntityId, StringComparer.Ordinal).Take(limit).ToArray();
        }
    }

    public MapTerrainSummary QueryTerrain(double originX, double originY, double radius)
    {
        lock (_gate)
        {
            EnsureQueryable(); using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT t.x,t.y,t.elevation_asl,t.slope_degrees FROM terrain_rtree r JOIN terrain_samples t ON t.id=r.id
                WHERE r.max_x>=@minX AND r.min_x<=@maxX AND r.max_y>=@minY AND r.min_y<=@maxY
                ORDER BY t.stable_key
                """;
            AddBounds(command, originX, originY, radius); List<(double Elevation, double Slope)> values = new();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) if (Distance(originX, originY, reader.GetDouble(0), reader.GetDouble(1)) <= radius)
                values.Add((reader.GetDouble(2), reader.GetDouble(3)));
            return values.Count == 0 ? new(0, null, null, null, null, null) : new(values.Count,
                values.Min(value => value.Elevation), values.Max(value => value.Elevation), values.Average(value => value.Elevation),
                values.Average(value => value.Slope), values.Max(value => value.Slope));
        }
    }

    public IReadOnlyList<MapTileSummaryResult> QueryTileSummaries(double originX, double originY, double radius, int limit)
    {
        lock (_gate)
        {
            EnsureQueryable(); using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.tile_ordinal,s.tree_count,s.bush_count,s.forest_count,s.vegetation_density,s.water_classification,s.water_samples,s.land_samples
                FROM tile_rtree r JOIN tile_summaries s ON s.id=r.id
                WHERE r.max_x>=@minX AND r.min_x<=@maxX AND r.max_y>=@minY AND r.min_y<=@maxY
                ORDER BY s.tile_ordinal LIMIT @limit
                """;
            AddBounds(command, originX, originY, radius); Add(command, "@limit", limit);
            List<MapTileSummaryResult> values = new(); using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) values.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetDouble(4), reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7)));
            return values;
        }
    }

    private SqliteConnection Open()
        => Open(_databasePath);

    private static SqliteConnection Open(string databasePath)
    {
        SqliteConnectionStringBuilder builder = new() { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        SqliteConnection connection = new(builder.ToString()); connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;";
        command.ExecuteNonQuery(); return connection;
    }

    private void InvalidatePriorFingerprint(string fingerprint)
    {
        string catalogPath = Path.Combine(_rootDirectory, "cache-manifest-v1.json");
        if (!File.Exists(catalogPath)) return;
        JsonObject? catalog = ReadCatalog(catalogPath);
        string previous = ReadNodeString(catalog?["currentFingerprint"]);
        if (string.Equals(previous, fingerprint, StringComparison.Ordinal) || !IsFingerprint(previous)) return;

        string previousPath = Path.Combine(_rootDirectory, previous + ".sqlite3");
        if (File.Exists(previousPath))
        {
            try
            {
                using SqliteConnection connection = Open(previousPath);
                if (ScalarLong(connection, "PRAGMA user_version") == SchemaVersion &&
                    string.Equals(ScalarString(connection, "SELECT fingerprint FROM map_manifest WHERE id=1"), previous, StringComparison.Ordinal))
                    SetReadinessCore(connection, MapKnowledgeReadiness.Stale, "fingerprint_changed");
            }
            catch (SqliteException)
            {
                // A broken stale cache must not prevent indexing a new, valid fingerprint.
            }
        }

        foreach (JsonNode? entry in catalog?["entries"] as JsonArray ?? [])
        {
            if (entry is not JsonObject value ||
                !string.Equals(ReadNodeString(value["fingerprint"]), previous, StringComparison.Ordinal)) continue;
            value["readiness"] = "stale";
            value["updatedAtUtc"] = UtcNow();
        }
        WriteJsonAtomically(catalogPath, catalog!);
    }

    private static void ApplyMigrations(SqliteConnection connection)
    {
        long version = ScalarLong(connection, "PRAGMA user_version");
        if (version > SchemaVersion) throw new InvalidDataException("database_version_newer");
        if (version == SchemaVersion) return;
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = CreateCommand(connection, transaction, Migration1Sql);
        command.ExecuteNonQuery();
        using SqliteCommand versionCommand = CreateCommand(connection, transaction, "PRAGMA user_version=1;");
        versionCommand.ExecuteNonQuery();
        using SqliteCommand migration = CreateCommand(connection, transaction,
            "INSERT INTO schema_migrations(version,applied_utc) VALUES(1,@utc)");
        Add(migration, "@utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)); migration.ExecuteNonQuery();
        transaction.Commit();
    }

    private void EnsureManifest(SqliteConnection connection, MapManifestData manifest, string fingerprint)
    {
        string? existing = ScalarString(connection, "SELECT fingerprint FROM map_manifest WHERE id=1");
        if (existing is not null && !string.Equals(existing, fingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("database_fingerprint_mismatch");
        if (existing is not null) return;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO map_manifest(id,fingerprint,index_version,canonical_json,world_name,world_size,tile_size,total_tiles,readiness,completed_tiles,created_utc,updated_utc,last_error)
            VALUES(1,@fingerprint,@version,@canonical,@world,@size,@tile,@total,'unavailable',0,@utc,@utc,NULL)
            """;
        Add(command, "@fingerprint", fingerprint); Add(command, "@version", manifest.IndexVersion);
        Add(command, "@canonical", Encoding.UTF8.GetString(MapFingerprint.BuildCanonicalJson(manifest)));
        Add(command, "@world", manifest.WorldName); Add(command, "@size", manifest.WorldSizeMeters);
        Add(command, "@tile", manifest.Export.TileSizeMeters); Add(command, "@total", manifest.Export.TotalTiles); Add(command, "@utc", UtcNow());
        command.ExecuteNonQuery();
    }

    private void UpsertLocation(SqliteConnection c, SqliteTransaction tx, JsonElement record)
    {
        double[] p = Vector(record, "positionASL"); string name = record.GetProperty("name").GetString()!;
        string type = record.GetProperty("locationType").GetString()!;
        string key = StableKey("location", type, name, p);
        long id = UpsertPoint(c, tx, "locations", key,
            "official_name,normalized_name,location_type,x,y,z_asl", "@name,@normalized,@type,@x,@y,@z",
            command => { Add(command, "@name", name); Add(command, "@normalized", NormalizeSearch(name)); Add(command, "@type", type); AddVector(command, p); });
        UpsertPointRtree(c, tx, "location_rtree", id, p[0], p[1]);
    }

    private void UpsertTerrain(SqliteConnection c, SqliteTransaction tx, JsonElement record)
    {
        double[] p = Vector(record, "positionASL"); string key = StableKey("terrain", "", "", p);
        long id = UpsertPoint(c, tx, "terrain_samples", key,
            "x,y,elevation_asl,slope_degrees,water", "@x,@y,@z,@slope,@water",
            command => { AddVector(command, p); Add(command, "@slope", record.GetProperty("slopeDegrees").GetDouble()); Add(command, "@water", record.GetProperty("water").GetBoolean()); });
        UpsertPointRtree(c, tx, "terrain_rtree", id, p[0], p[1]);
    }

    private void UpsertBuilding(SqliteConnection c, SqliteTransaction tx, JsonElement record)
    {
        double[] p = Vector(record, "positionASL"); string cls = record.GetProperty("class").GetString()!;
        string model = record.GetProperty("model").GetString()!, type = record.GetProperty("terrainType").GetString()!;
        string key = StableKey("building", cls, model + "|" + type, p);
        long id = UpsertPoint(c, tx, "buildings", key,
            "class_name,model_name,terrain_type,x,y,z_asl", "@class,@model,@type,@x,@y,@z",
            command => { Add(command, "@class", cls); Add(command, "@model", model); Add(command, "@type", type); AddVector(command, p); });
        UpsertPointRtree(c, tx, "building_rtree", id, p[0], p[1]);
    }

    private void UpsertRoad(SqliteConnection c, SqliteTransaction tx, JsonElement record)
    {
        double[] begin = Vector(record, "beginASL"), end = Vector(record, "endASL");
        if (ComparePoint(begin, end) > 0) (begin, end) = (end, begin);
        string type = record.GetProperty("roadType").GetString()!;
        string key = StableKey("road", type, "", begin.Concat(end).ToArray());
        using SqliteCommand command = CreateCommand(c, tx, """
            INSERT INTO road_segments(stable_key,road_type,width_meters,pedestrian,bridge,begin_x,begin_y,begin_z,end_x,end_y,end_z)
            VALUES(@key,@type,@width,@pedestrian,@bridge,@bx,@by,@bz,@ex,@ey,@ez)
            ON CONFLICT(stable_key) DO UPDATE SET road_type=excluded.road_type,width_meters=excluded.width_meters,pedestrian=excluded.pedestrian,
            bridge=excluded.bridge,begin_x=excluded.begin_x,begin_y=excluded.begin_y,begin_z=excluded.begin_z,end_x=excluded.end_x,end_y=excluded.end_y,end_z=excluded.end_z
            RETURNING id
            """);
        Add(command, "@key", key); Add(command, "@type", type); Add(command, "@width", record.GetProperty("widthMeters").GetDouble());
        Add(command, "@pedestrian", record.GetProperty("pedestrian").GetBoolean()); Add(command, "@bridge", record.GetProperty("bridge").GetBoolean());
        Add(command, "@bx", begin[0]); Add(command, "@by", begin[1]); Add(command, "@bz", begin[2]);
        Add(command, "@ex", end[0]); Add(command, "@ey", end[1]); Add(command, "@ez", end[2]);
        long id = (long)command.ExecuteScalar()!;
        using SqliteCommand rtree = CreateCommand(c, tx, "INSERT OR REPLACE INTO road_rtree VALUES(@id,@minX,@maxX,@minY,@maxY)");
        Add(rtree, "@id", id); Add(rtree, "@minX", Math.Min(begin[0], end[0])); Add(rtree, "@maxX", Math.Max(begin[0], end[0]));
        Add(rtree, "@minY", Math.Min(begin[1], end[1])); Add(rtree, "@maxY", Math.Max(begin[1], end[1])); rtree.ExecuteNonQuery();
    }

    private static void UpsertTileSummary(SqliteConnection c, SqliteTransaction tx, MapTileBounds tile, int trees, int bushes,
        int forests, double density, string waterClass, int waterSamples, int landSamples)
    {
        using SqliteCommand command = CreateCommand(c, tx, """
            INSERT INTO tile_summaries(tile_ordinal,tree_count,bush_count,forest_count,vegetation_density,water_classification,water_samples,land_samples,min_x,min_y,max_x,max_y)
            VALUES(@ordinal,@trees,@bushes,@forests,@density,@water,@waterSamples,@landSamples,@minX,@minY,@maxX,@maxY)
            ON CONFLICT(tile_ordinal) DO UPDATE SET tree_count=excluded.tree_count,bush_count=excluded.bush_count,forest_count=excluded.forest_count,
            vegetation_density=excluded.vegetation_density,water_classification=excluded.water_classification,water_samples=excluded.water_samples,
            land_samples=excluded.land_samples,min_x=excluded.min_x,min_y=excluded.min_y,max_x=excluded.max_x,max_y=excluded.max_y RETURNING id
            """);
        Add(command, "@ordinal", tile.Ordinal); Add(command, "@trees", trees); Add(command, "@bushes", bushes); Add(command, "@forests", forests);
        Add(command, "@density", density); Add(command, "@water", waterClass); Add(command, "@waterSamples", waterSamples); Add(command, "@landSamples", landSamples);
        Add(command, "@minX", tile.MinX); Add(command, "@minY", tile.MinY); Add(command, "@maxX", tile.MaxX); Add(command, "@maxY", tile.MaxY);
        long id = (long)command.ExecuteScalar()!;
        using SqliteCommand rtree = CreateCommand(c, tx, "INSERT OR REPLACE INTO tile_rtree VALUES(@id,@minX,@maxX,@minY,@maxY)");
        Add(rtree, "@id", id); Add(rtree, "@minX", tile.MinX); Add(rtree, "@maxX", tile.MaxX); Add(rtree, "@minY", tile.MinY); Add(rtree, "@maxY", tile.MaxY); rtree.ExecuteNonQuery();
    }

    private void RebuildRoadIntersections(SqliteConnection connection)
    {
        Dictionary<(long X, long Y), List<(double X, double Y, double Z, long Segment)>> endpoints = new();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,begin_x,begin_y,begin_z,end_x,end_y,end_z FROM road_segments";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                AddEndpoint(endpoints, reader.GetInt64(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3));
                AddEndpoint(endpoints, reader.GetInt64(0), reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6));
            }
        }
        using SqliteTransaction tx = connection.BeginTransaction();
        CreateCommand(connection, tx, "DELETE FROM intersection_rtree; DELETE FROM road_intersections;").ExecuteNonQuery();
        foreach ((var coordinate, var points) in endpoints.OrderBy(pair => pair.Key.X).ThenBy(pair => pair.Key.Y))
        {
            int connections = points.Select(point => point.Segment).Distinct().Count(); if (connections < 2) continue;
            double x = points.Average(point => point.X), y = points.Average(point => point.Y), z = points.Average(point => point.Z);
            string key = StableKey("intersection", "", "", new[] { x, y, z });
            using SqliteCommand insert = CreateCommand(connection, tx, "INSERT INTO road_intersections(stable_key,x,y,z_asl,connected_segments) VALUES(@key,@x,@y,@z,@count) RETURNING id");
            Add(insert, "@key", key); Add(insert, "@x", x); Add(insert, "@y", y); Add(insert, "@z", z); Add(insert, "@count", connections);
            long id = (long)insert.ExecuteScalar()!; UpsertPointRtree(connection, tx, "intersection_rtree", id, x, y);
        }
        tx.Commit();
    }

    private static long UpsertPoint(SqliteConnection c, SqliteTransaction tx, string table, string key, string columns, string values, Action<SqliteCommand> bind)
    {
        string[] names = columns.Split(','); string updates = string.Join(',', names.Select(name => name + "=excluded." + name));
        using SqliteCommand command = CreateCommand(c, tx, $"INSERT INTO {table}(stable_key,{columns}) VALUES(@key,{values}) ON CONFLICT(stable_key) DO UPDATE SET {updates} RETURNING id");
        Add(command, "@key", key); bind(command); return (long)command.ExecuteScalar()!;
    }

    private static void UpsertPointRtree(SqliteConnection c, SqliteTransaction tx, string table, long id, double x, double y)
    {
        using SqliteCommand command = CreateCommand(c, tx, $"INSERT OR REPLACE INTO {table} VALUES(@id,@x,@x,@y,@y)");
        Add(command, "@id", id); Add(command, "@x", x); Add(command, "@y", y); command.ExecuteNonQuery();
    }

    private MapKnowledgeDiagnostics ReadDiagnostics(SqliteConnection connection, bool exportActive, int pendingPages)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT world_name,fingerprint,index_version,completed_tiles,total_tiles,readiness,COALESCE(last_error,'') FROM map_manifest WHERE id=1";
        using SqliteDataReader reader = command.ExecuteReader(); if (!reader.Read()) throw new InvalidDataException("missing_manifest_row");
        return new(ParseReadiness(reader.GetString(5)), reader.GetString(0), reader.GetString(1), _databasePath, reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
            Count(connection, "locations"), Count(connection, "terrain_samples"), Count(connection, "buildings"), Count(connection, "road_segments"),
            Count(connection, "road_intersections"), Count(connection, "tile_summaries"), reader.GetString(6), exportActive, pendingPages);
    }

    private void WriteCatalog(SqliteConnection connection)
    {
        MapKnowledgeDiagnostics d = ReadDiagnostics(connection, false, 0);
        string catalogPath = Path.Combine(_rootDirectory, "cache-manifest-v1.json");
        JsonArray entries = new();
        if (File.Exists(catalogPath))
        {
            JsonObject? previous = ReadCatalog(catalogPath);
            foreach (JsonNode? entry in previous?["entries"] as JsonArray ?? [])
            {
                if (entry is JsonObject value &&
                    !string.Equals(ReadNodeString(value["fingerprint"]), d.Fingerprint, StringComparison.Ordinal))
                    entries.Add(value.DeepClone());
            }
        }
        entries.Add(JsonSerializer.SerializeToNode(new
        {
            fingerprint = d.Fingerprint,
            worldName = d.WorldName,
            indexVersion = d.IndexVersion,
            databaseFile = Path.GetFileName(d.DatabasePath),
            readiness = ToWire(d.Readiness),
            completedTiles = d.CompletedTiles,
            totalTiles = d.TotalTiles,
            updatedAtUtc = UtcNow()
        }));
        JsonObject catalog = new()
        {
            ["schema"] = "arma-ai-bridge/map-cache-manifest-v1",
            ["currentFingerprint"] = d.Fingerprint,
            ["entries"] = entries
        };
        WriteJsonAtomically(catalogPath, catalog);
    }

    private void SetReadinessCore(SqliteConnection connection, MapKnowledgeReadiness readiness, string error)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE map_manifest SET readiness=@state,last_error=@error,updated_utc=@utc WHERE id=1";
        Add(command, "@state", ToWire(readiness)); Add(command, "@error", error.Length == 0 ? DBNull.Value : error);
        Add(command, "@utc", UtcNow()); command.ExecuteNonQuery();
    }

    private static bool TileAlreadyComplete(SqliteConnection c, SqliteTransaction tx, int ordinal)
    {
        using SqliteCommand command = CreateCommand(c, tx, "SELECT COUNT(*) FROM tile_progress WHERE tile_ordinal=@ordinal");
        Add(command, "@ordinal", ordinal); return (long)command.ExecuteScalar()! > 0;
    }

    private static void AddEndpoint(Dictionary<(long X, long Y), List<(double X, double Y, double Z, long Segment)>> values, long segment, double x, double y, double z)
    {
        var key = ((long)Math.Round(x, MidpointRounding.AwayFromZero), (long)Math.Round(y, MidpointRounding.AwayFromZero));
        if (!values.TryGetValue(key, out var points)) values[key] = points = new(); points.Add((x, y, z, segment));
    }

    private void EnsureActive() { if (_manifest is null || _databasePath.Length == 0) throw new InvalidOperationException("map_knowledge_unavailable"); }
    private void EnsureQueryable()
    {
        EnsureActive(); MapKnowledgeReadiness readiness = GetReadiness();
        if (readiness is MapKnowledgeReadiness.Unavailable or MapKnowledgeReadiness.Stale or MapKnowledgeReadiness.Failed)
            throw new InvalidOperationException("map_knowledge_" + ToWire(readiness));
    }

    private static int Count(SqliteConnection connection, string table) => checked((int)ScalarLong(connection, $"SELECT COUNT(*) FROM {table}"));
    private static long ScalarLong(SqliteConnection connection, string sql) { using SqliteCommand c = connection.CreateCommand(); c.CommandText = sql; return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture); }
    private static string? ScalarString(SqliteConnection connection, string sql) { using SqliteCommand c = connection.CreateCommand(); c.CommandText = sql; object? value = c.ExecuteScalar(); return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture); }
    private static SqliteCommand CreateCommand(SqliteConnection c, SqliteTransaction tx, string sql) { SqliteCommand command = c.CreateCommand(); command.Transaction = tx; command.CommandText = sql; return command; }
    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);
    private static void AddVector(SqliteCommand command, double[] p) { Add(command, "@x", p[0]); Add(command, "@y", p[1]); Add(command, "@z", p[2]); }
    private static void AddBounds(SqliteCommand command, double x, double y, double radius) { Add(command, "@minX", x-radius); Add(command, "@maxX", x+radius); Add(command, "@minY", y-radius); Add(command, "@maxY", y+radius); }
    private static double[] Vector(JsonElement record, string name) => record.GetProperty(name).EnumerateArray().Select(value => value.GetDouble()).ToArray();
    private static int ComparePoint(double[] a, double[] b) { int x=a[0].CompareTo(b[0]); return x!=0?x:(a[1].CompareTo(b[1]) is int y && y!=0?y:a[2].CompareTo(b[2])); }
    private string StableKey(string kind, string one, string two, IReadOnlyList<double> p)
    {
        string text = string.Join('|', new[] { _fingerprint, kind, one.Trim().ToLowerInvariant(), two.Trim().ToLowerInvariant() }
            .Concat(p.Select(value => Math.Round(value, 2).ToString("0.00", CultureInfo.InvariantCulture))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
    private static string Alias(string stableKey) => "map-" + stableKey[..12];
    private static string NormalizeSearch(string value) => string.Join(' ', value.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    private string UtcNow() => _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
    private static string SanitizeError(string value) => new(value.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(64).ToArray());
    private static bool IsFingerprint(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static JsonObject? ReadCatalog(string path)
    {
        try
        {
            JsonObject? value = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return value?["entries"] is JsonArray ? value : null;
        }
        catch (JsonException) { return null; }
    }
    private static string ReadNodeString(JsonNode? value)
        => value is JsonValue scalar && scalar.TryGetValue(out string? result) ? result ?? string.Empty : string.Empty;
    private static void WriteJsonAtomically(string path, JsonNode value)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
    private static string ToWire(MapKnowledgeReadiness value) => value.ToString().ToLowerInvariant();
    private static MapKnowledgeReadiness ParseReadiness(string? value) => Enum.TryParse(value, true, out MapKnowledgeReadiness result) ? result : MapKnowledgeReadiness.Failed;
    private static double Distance(double ax,double ay,double bx,double by) => Math.Sqrt(Math.Pow(bx-ax,2)+Math.Pow(by-ay,2));
    private static double Bearing(double ax,double ay,double bx,double by) { double value=Math.Atan2(bx-ax,by-ay)*180/Math.PI; return value<0?value+360:value; }
    private static double DistanceToSegment(double px,double py,double ax,double ay,double bx,double by) { double dx=bx-ax,dy=by-ay,l=dx*dx+dy*dy;if(l==0)return Distance(px,py,ax,ay);double t=Math.Clamp(((px-ax)*dx+(py-ay)*dy)/l,0,1);return Distance(px,py,ax+t*dx,ay+t*dy); }

    private const string Migration1Sql = """
        CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);
        CREATE TABLE map_manifest(id INTEGER PRIMARY KEY CHECK(id=1),fingerprint TEXT NOT NULL UNIQUE,index_version INTEGER NOT NULL,canonical_json TEXT NOT NULL,world_name TEXT NOT NULL,world_size REAL NOT NULL,tile_size REAL NOT NULL,total_tiles INTEGER NOT NULL,readiness TEXT NOT NULL,completed_tiles INTEGER NOT NULL,created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL,last_error TEXT NULL);
        CREATE TABLE tile_progress(tile_ordinal INTEGER PRIMARY KEY,column_index INTEGER NOT NULL,row_index INTEGER NOT NULL,min_x REAL NOT NULL,min_y REAL NOT NULL,max_x REAL NOT NULL,max_y REAL NOT NULL,page_count INTEGER NOT NULL,completed_utc TEXT NOT NULL);
        CREATE TABLE locations(id INTEGER PRIMARY KEY,stable_key TEXT NOT NULL UNIQUE,official_name TEXT NOT NULL,normalized_name TEXT NOT NULL,location_type TEXT NOT NULL,x REAL NOT NULL,y REAL NOT NULL,z_asl REAL NOT NULL);
        CREATE INDEX ix_locations_normalized_name ON locations(normalized_name);
        CREATE VIRTUAL TABLE location_rtree USING rtree(id,min_x,max_x,min_y,max_y);
        CREATE TABLE terrain_samples(id INTEGER PRIMARY KEY,stable_key TEXT NOT NULL UNIQUE,x REAL NOT NULL,y REAL NOT NULL,elevation_asl REAL NOT NULL,slope_degrees REAL NOT NULL,water INTEGER NOT NULL);
        CREATE VIRTUAL TABLE terrain_rtree USING rtree(id,min_x,max_x,min_y,max_y);
        CREATE TABLE buildings(id INTEGER PRIMARY KEY,stable_key TEXT NOT NULL UNIQUE,class_name TEXT NOT NULL,model_name TEXT NOT NULL,terrain_type TEXT NOT NULL,x REAL NOT NULL,y REAL NOT NULL,z_asl REAL NOT NULL);
        CREATE VIRTUAL TABLE building_rtree USING rtree(id,min_x,max_x,min_y,max_y);
        CREATE TABLE road_segments(id INTEGER PRIMARY KEY,stable_key TEXT NOT NULL UNIQUE,road_type TEXT NOT NULL,width_meters REAL NOT NULL,pedestrian INTEGER NOT NULL,bridge INTEGER NOT NULL,begin_x REAL NOT NULL,begin_y REAL NOT NULL,begin_z REAL NOT NULL,end_x REAL NOT NULL,end_y REAL NOT NULL,end_z REAL NOT NULL);
        CREATE VIRTUAL TABLE road_rtree USING rtree(id,min_x,max_x,min_y,max_y);
        CREATE TABLE road_intersections(id INTEGER PRIMARY KEY,stable_key TEXT NOT NULL UNIQUE,x REAL NOT NULL,y REAL NOT NULL,z_asl REAL NOT NULL,connected_segments INTEGER NOT NULL);
        CREATE VIRTUAL TABLE intersection_rtree USING rtree(id,min_x,max_x,min_y,max_y);
        CREATE TABLE tile_summaries(id INTEGER PRIMARY KEY,tile_ordinal INTEGER NOT NULL UNIQUE,tree_count INTEGER NOT NULL,bush_count INTEGER NOT NULL,forest_count INTEGER NOT NULL,vegetation_density REAL NOT NULL,water_classification TEXT NOT NULL,water_samples INTEGER NOT NULL,land_samples INTEGER NOT NULL,min_x REAL NOT NULL,min_y REAL NOT NULL,max_x REAL NOT NULL,max_y REAL NOT NULL);
        CREATE VIRTUAL TABLE tile_rtree USING rtree(id,min_x,max_x,min_y,max_y);
        """;
}
