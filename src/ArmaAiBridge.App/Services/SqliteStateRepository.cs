using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArmaAiBridge.App.Models;
using Microsoft.Data.Sqlite;

namespace ArmaAiBridge.App.Services;

public sealed class SqliteStateRepository : IStateRepository, IDisposable
{
    public const int SchemaVersion = 3;
    public const int ProtocolVersion = 2;
    private static readonly string[] DynamicTables =
    {
        "current_environment", "current_time_astronomy", "current_player", "current_loadout",
        "friendly_groups", "friendly_units", "group_waypoints", "known_contact_sources",
        "known_contacts", "current_tasks", "current_markers", "state_section_metadata"
    };

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly string _databasePath;
    private SqliteConnection? _connection;
    private StateRepositoryReadiness _readiness = StateRepositoryReadiness.Unavailable;
    private string _activeSessionHash = string.Empty;
    private string _activeSessionAlias = string.Empty;
    private string _activeMissionHash = string.Empty;
    private string _worldName = string.Empty;
    private double _worldSize;
    private long _lastSequence;
    private DateTimeOffset? _lastSnapshotAt;
    private string _diagnosticCode = string.Empty;
    private bool _disposed;

    public SqliteStateRepository(string? databasePath = null, TimeProvider? timeProvider = null)
    {
        _databasePath = databasePath ?? AppPaths.StateMirrorDatabase;
        _timeProvider = timeProvider ?? TimeProvider.System;
        OpenAndMigrate();
    }

    public event Action? StateChanged;

    public string DatabasePath => _databasePath;

    public StateIngestResult ApplyHandshake(
        string missionId,
        string sessionId,
        string worldName,
        double worldSize,
        DateTimeOffset receivedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(missionId) || string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(worldName) || !double.IsFinite(worldSize) || worldSize <= 0)
            return new StateIngestResult(TelemetryIngestStatus.Rejected, "state_handshake_invalid");

        bool changed;
        lock (_gate)
        {
            if (!EnsureReady()) return Failed();
            string missionHash = Hash("mission\0" + missionId);
            string sessionHash = Hash("session\0" + missionId + "\0" + sessionId);
            changed = !string.Equals(sessionHash, _activeSessionHash, StringComparison.Ordinal);
            try
            {
                using SqliteTransaction transaction = _connection!.BeginTransaction();
                if (changed)
                {
                    ClearDynamic(transaction);
                    Execute(transaction, "UPDATE state_sessions SET active = 0;");
                    _lastSequence = 0;
                    _lastSnapshotAt = null;
                }

                string alias = "state-session-" + sessionHash[..8].ToLowerInvariant();
                Execute(transaction, """
                    INSERT INTO state_sessions(session_hash, mission_hash, alias, world_name, world_size, active, last_sequence, last_received_utc)
                    VALUES($session, $mission, $alias, $world, $size, 1, $sequence, $received)
                    ON CONFLICT(session_hash) DO UPDATE SET
                        active = 1, world_name = excluded.world_name, world_size = excluded.world_size,
                        last_received_utc = excluded.last_received_utc;
                    """,
                    ("$session", sessionHash), ("$mission", missionHash), ("$alias", alias),
                    ("$world", worldName), ("$size", worldSize), ("$sequence", _lastSequence),
                    ("$received", receivedAtUtc.ToString("O", CultureInfo.InvariantCulture)));
                Execute(transaction, """
                    INSERT INTO state_worlds(world_key, world_name, world_size, baseline_ready)
                    VALUES($key, $name, $size, COALESCE((SELECT baseline_ready FROM state_worlds WHERE world_key=$key), 0))
                    ON CONFLICT(world_key) DO UPDATE SET world_name=excluded.world_name, world_size=excluded.world_size;
                    """, ("$key", WorldKey(worldName, worldSize)), ("$name", worldName), ("$size", worldSize));
                transaction.Commit();
                _activeSessionHash = sessionHash;
                _activeMissionHash = missionHash;
                _activeSessionAlias = alias;
                _worldName = worldName;
                _worldSize = worldSize;
                _diagnosticCode = string.Empty;
            }
            catch (SqliteException)
            {
                _diagnosticCode = "state_handshake_commit_failed";
                return Failed();
            }
        }
        StateChanged?.Invoke();
        return new StateIngestResult(TelemetryIngestStatus.Applied, changed ? "state_session_reset" : string.Empty);
    }

    public StateIngestResult ApplySnapshot(StateSnapshotMessage snapshot)
    {
        lock (_gate)
        {
            if (!EnsureReady()) return Failed();
            string expected = Hash("session\0" + snapshot.MissionId + "\0" + snapshot.SessionId);
            if (!string.Equals(expected, _activeSessionHash, StringComparison.Ordinal))
                return new StateIngestResult(TelemetryIngestStatus.Rejected, "state_session_mismatch");
            if (snapshot.Sequence <= _lastSequence)
                return new StateIngestResult(TelemetryIngestStatus.OutOfOrder, "state_sequence_out_of_order");

            try
            {
                using SqliteTransaction transaction = _connection!.BeginTransaction();
                foreach (StateSnapshotSection section in snapshot.Sections.Values.OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    ApplySection(transaction, section, snapshot.PublishedAtGameTime, snapshot.ReceivedAtUtc);
                }
                Execute(transaction, """
                    UPDATE state_sessions SET last_sequence=$sequence, last_received_utc=$received
                    WHERE session_hash=$session;
                    """, ("$sequence", snapshot.Sequence),
                    ("$received", snapshot.ReceivedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                    ("$session", _activeSessionHash));
                transaction.Commit();
                _lastSequence = snapshot.Sequence;
                _lastSnapshotAt = snapshot.ReceivedAtUtc;
                _diagnosticCode = string.Empty;
            }
            catch (Exception exception) when (exception is SqliteException or JsonException or InvalidDataException)
            {
                _diagnosticCode = "state_snapshot_commit_failed";
                return Failed();
            }
        }
        StateChanged?.Invoke();
        return new StateIngestResult(TelemetryIngestStatus.Applied);
    }

    public void ReplaceNamedLocations(MapGazetteerSnapshot gazetteer)
    {
        if (gazetteer.Readiness is not (MapGazetteerReadiness.Ready or MapGazetteerReadiness.Empty)) return;
        lock (_gate)
        {
            if (!EnsureReady() || _worldName.Length == 0 ||
                !string.Equals(_worldName, gazetteer.WorldName, StringComparison.Ordinal)) return;
            string worldKey = WorldKey(gazetteer.WorldName, gazetteer.WorldSizeMeters);
            try
            {
                using SqliteTransaction transaction = _connection!.BeginTransaction();
                Execute(transaction, "DELETE FROM named_locations WHERE world_key=$world;", ("$world", worldKey));
                int ordinal = 1;
                foreach (MapGazetteerLocation location in gazetteer.Locations
                    .Where(item => NamedLocationEligibilityPolicy.IsAllowed(item.Type))
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Type, StringComparer.Ordinal)
                    .ThenBy(item => item.Key, StringComparer.Ordinal))
                {
                    Execute(transaction, """
                        INSERT INTO named_locations(world_key, alias, source_hash, official_name, normalized_name,
                            location_type, x, y, radius_a, radius_b, angle)
                        VALUES($world,$alias,$hash,$name,$normalized,$type,$x,$y,$ra,$rb,$angle);
                        """, ("$world", worldKey), ("$alias", $"location-{ordinal++:0000}"),
                        ("$hash", Hash(worldKey + "\0location\0" + location.Key)), ("$name", location.Name),
                        ("$normalized", location.Name.ToUpperInvariant()), ("$type", location.Type),
                        ("$x", location.X), ("$y", location.Y), ("$ra", location.RadiusA),
                        ("$rb", location.RadiusB), ("$angle", location.Angle));
                }
                Execute(transaction, "UPDATE state_worlds SET baseline_ready=1 WHERE world_key=$world;", ("$world", worldKey));
                transaction.Commit();
            }
            catch (SqliteException)
            {
                _diagnosticCode = "state_gazetteer_commit_failed";
                return;
            }
        }
        StateChanged?.Invoke();
    }

    public StatePlayer? GetPlayer()
    {
        lock (_gate)
        {
            StateSectionMetadata? metadata = GetMetadata("player");
            JsonElement? value = ReadScalar("current_player");
            if (value is null || metadata is null) return null;
            JsonElement root = value.Value;
            return new StatePlayer(
                Text(root, "alias", "player-self"), Text(root, "side"), Text(root, "groupAlias"),
                Text(root, "groupCallsign"),
                Vector(root, "positionATL"), Vector(root, "positionASL"), Text(root, "grid"), metadata);
        }
    }

    public StateEnvironment? GetEnvironment()
    {
        lock (_gate)
        {
            StateSectionMetadata? metadata = GetMetadata("environment");
            JsonElement? value = ReadScalar("current_environment");
            if (value is null || metadata is null) return null;
            JsonElement root = value.Value;
            double[] wind = Numbers(root, "wind", 2);
            return new StateEnvironment(Number(root, "overcast"), Number(root, "forecastOvercast"),
                Number(root, "rain"), Number(root, "fog"), Numbers(root, "fogParameters", 3),
                Number(root, "forecastFog"), wind.ElementAtOrDefault(0), wind.ElementAtOrDefault(1),
                Number(root, "windDirection"), Number(root, "windStrength"), Number(root, "gusts"),
                Number(root, "waves"), Number(root, "lightning"), Number(root, "humidity"),
                NullableNumber(root, "temperatureCelsius"), Number(root, "nextWeatherChange"), metadata);
        }
    }

    public StateTimeAstronomy? GetTimeAstronomy()
    {
        lock (_gate)
        {
            StateSectionMetadata? metadata = GetMetadata("timeAstronomy");
            JsonElement? value = ReadScalar("current_time_astronomy");
            if (value is null || metadata is null) return null;
            JsonElement root = value.Value;
            return new StateTimeAstronomy(IntNumbers(root, "missionDate", 5), Number(root, "daytime"),
                Number(root, "elapsedMissionTime"), Number(root, "timeMultiplier"), Number(root, "moonPhase"),
                Number(root, "sunOrMoon"), metadata);
        }
    }

    public StateLoadout? GetLoadout()
    {
        lock (_gate)
        {
            StateSectionMetadata? metadata = GetMetadata("loadout");
            JsonElement? value = ReadScalar("current_loadout");
            if (value is null || metadata is null) return null;
            JsonElement root = value.Value;
            StateMagazine[] magazines = Array(root, "magazines").Take(64).Select(item => new StateMagazine(
                Text(item, "class"), Text(item, "displayName"), Integer(item, "rounds"),
                Boolean(item, "loaded"), Text(item, "container"))).ToArray();
            StateMagazineTotal[] totals = Array(root, "magazineTotals").Take(64).Select(item => new StateMagazineTotal(
                Text(item, "class"), Text(item, "displayName"), Integer(item, "magazineCount"),
                Integer(item, "rounds"))).ToArray();
            JsonElement ballistic = root.GetProperty("ballisticProfile");
            StateBallisticProfile ballisticProfile = new(
                Boolean(ballistic, "available"), Text(ballistic, "reason"), Text(ballistic, "model"),
                Text(ballistic, "supportedProjectileType"), Text(ballistic, "weaponClass"),
                Text(ballistic, "weaponDisplayName"), Text(ballistic, "muzzleClass"), Text(ballistic, "fireMode"),
                Text(ballistic, "magazineClass"), Text(ballistic, "magazineDisplayName"),
                Text(ballistic, "ammunitionClass"), Text(ballistic, "ammunitionDisplayName"),
                Text(ballistic, "simulation"), Integer(ballistic, "loadedRounds"),
                Number(ballistic, "currentZeroingMeters"), Integer(ballistic, "currentZeroingIndex"),
                Number(ballistic, "initialSpeedMetersPerSecond"), Number(ballistic, "airFriction"),
                Number(ballistic, "gravityCoefficient"), Number(ballistic, "typicalSpeedMetersPerSecond"),
                Vector(ballistic, "shooterPositionASL"), Boolean(ballistic, "advancedBallisticsDetected"),
                Boolean(ballistic, "aceAdvancedBallisticsEnabled"), Boolean(ballistic, "aceAdapterAvailable"),
                Text(ballistic, "aceVersion"), Text(ballistic, "aceSupportedBaseline"),
                Boolean(ballistic, "aceProfileSupported"), Boolean(ballistic, "aceMuzzleVelocityVariationEnabled"),
                Number(ballistic, "aceMuzzleVelocityVariationStandardDeviationPercent"), Text(ballistic, "profileFingerprint"),
                Boolean(ballistic, "aceTemperatureCorrectionEnabled"), Boolean(ballistic, "aceBarrelLengthCorrectionEnabled"));
            return new StateLoadout(Text(root, "primaryWeapon"), Text(root, "launcher"), Text(root, "handgun"),
                Text(root, "selectedWeapon"), Text(root, "selectedWeaponDisplayName"), Text(root, "muzzle"),
                Text(root, "fireMode"), Text(root, "currentMagazine"), Integer(root, "loadedRounds"),
                Strings(root, "opticsAndAttachments"), Text(root, "binocular"), magazines, totals,
                Integer(root, "grenadeCount"), Integer(root, "throwableCount"), Integer(root, "mineCount"),
                Integer(root, "explosiveCount"), Strings(root, "assignedItems"), Text(root, "uniformClass"),
                Text(root, "vestClass"), Text(root, "backpackClass"), Text(root, "loadoutHash"), ballisticProfile, metadata);
        }
    }

    public IReadOnlyList<StateFriendlyGroup> GetFriendlyGroups(int limit = 100, bool includeStale = false)
    {
        lock (_gate)
        {
            StateSectionMetadata? metadata = GetMetadata("friendlyForces");
            if (metadata is null || (!includeStale && metadata.IsStale)) return System.Array.Empty<StateFriendlyGroup>();
            return ReadRows("friendly_groups", limit).Select(root =>
            {
                StateWaypoint? waypoint = root.TryGetProperty("waypoint", out JsonElement wp) && wp.ValueKind == JsonValueKind.Object
                    ? new StateWaypoint(Integer(wp, "index"), Vector(wp, "position"), Text(wp, "type")) : null;
                return new StateFriendlyGroup(Text(root, "alias"), Text(root, "callsign"), Text(root, "leaderAlias"),
                    Strings(root, "memberAliases"), Vector(root, "leaderPosition"), Text(root, "behaviour"),
                    Text(root, "combatMode"), Text(root, "formation"), waypoint,
                    OptionalVector(root, "expectedDestination"), Strings(root, "assignedTargetAliases"), metadata);
            }).ToArray();
        }
    }

    public IReadOnlyList<StateFriendlyUnit> GetFriendlyUnits(int limit = 100, bool includeStale = false)
    {
        lock (_gate)
        {
            StateSectionMetadata? metadata = GetMetadata("friendlyForces");
            if (metadata is null || (!includeStale && metadata.IsStale)) return System.Array.Empty<StateFriendlyUnit>();
            return ReadRows("friendly_units", limit).Select(root => new StateFriendlyUnit(
                Text(root, "alias"), Text(root, "groupAlias"), Text(root, "class"), Text(root, "displayRole"),
                Vector(root, "position"), Boolean(root, "alive"), Text(root, "lifeState"), Boolean(root, "mobile"),
                Number(root, "damage"), Text(root, "currentCommand"), Text(root, "assignedTargetAlias"),
                Text(root, "vehicleAlias"), Text(root, "vehicleRole"), metadata)).ToArray();
        }
    }

    public IReadOnlyList<StateKnownContact> GetKnownContacts(int limit = 100, bool includeStale = false)
    {
        lock (_gate)
        {
            StateSectionMetadata? metadata = GetMetadata("knownContacts");
            if (metadata is null || (!includeStale && metadata.IsStale)) return System.Array.Empty<StateKnownContact>();
            return ReadRows("known_contacts", limit).Select(root => new StateKnownContact(
                Text(root, "alias"), Text(root, "class"), Text(root, "displayName"), Text(root, "contactType"), Text(root, "perceivedSide"),
                Text(root, "relationship"),
                Vector(root, "estimatedPosition"), Number(root, "positionErrorMeters"),
                Number(root, "lastSeenAgeSeconds"), Number(root, "lastThreatAgeSeconds"),
                Strings(root, "observerGroupAliases"), metadata)).Where(ContactEligibilityPolicy.IsEligible).ToArray();
        }
    }

    public IReadOnlyList<StateTask> GetTasks(int limit = 100, bool includeStale = false)
    {
        lock (_gate)
        {
            StateSectionMetadata? metadata = GetMetadata("tasks");
            if (metadata is null || (!includeStale && metadata.IsStale)) return System.Array.Empty<StateTask>();
            return ReadRows("current_tasks", limit).Select(root => new StateTask(Text(root, "alias"),
                Text(root, "title"), Text(root, "description"), OptionalVector(root, "destination"),
                Text(root, "type"), Text(root, "status"), Text(root, "parentAlias"),
                Boolean(root, "active"), metadata)).ToArray();
        }
    }

    public IReadOnlyList<StateMarker> GetMarkers(int limit = 100, bool includeStale = false)
    {
        lock (_gate)
        {
            StateSectionMetadata? metadata = GetMetadata("markers");
            if (metadata is null || (!includeStale && metadata.IsStale)) return System.Array.Empty<StateMarker>();
            return ReadRows("current_markers", limit).Select(root => new StateMarker(Text(root, "alias"),
                Text(root, "text"), Vector(root, "position"), Text(root, "type"), Text(root, "color"),
                Text(root, "shape"), Numbers(root, "size", 2), Number(root, "direction"), Number(root, "alpha"),
                Numbers(root, "polyline", 128), metadata)).ToArray();
        }
    }

    public IReadOnlyList<MapGazetteerLocation> GetNamedLocations(string? query = null, int limit = 100)
    {
        lock (_gate)
        {
            if (!EnsureReady() || _worldName.Length == 0) return System.Array.Empty<MapGazetteerLocation>();
            int bounded = Math.Clamp(limit, 1, 100);
            using SqliteCommand command = _connection!.CreateCommand();
            command.CommandText = """
                SELECT alias, official_name, location_type, x, y, radius_a, radius_b, angle
                FROM named_locations WHERE world_key=$world
                  AND ($query='' OR normalized_name LIKE $pattern ESCAPE '\\' OR UPPER(location_type) LIKE $pattern ESCAPE '\\')
                ORDER BY normalized_name, alias LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$world", WorldKey(_worldName, _worldSize));
            string normalized = (query ?? string.Empty).Trim().ToUpperInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            command.Parameters.AddWithValue("$query", normalized);
            command.Parameters.AddWithValue("$pattern", "%" + normalized + "%");
            command.Parameters.AddWithValue("$limit", bounded);
            using SqliteDataReader reader = command.ExecuteReader();
            List<MapGazetteerLocation> result = new();
            while (reader.Read())
            {
                MapGazetteerLocation location = new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7));
                if (NamedLocationEligibilityPolicy.IsAllowed(location.Type)) result.Add(location);
            }
            return result;
        }
    }

    public IReadOnlyList<StateSectionMetadata> GetSectionMetadata()
    {
        lock (_gate) return ReadMetadata().ToArray();
    }

    public StateRepositoryDiagnostics GetDiagnostics()
    {
        lock (_gate)
        {
            Dictionary<string, int> counts = new(StringComparer.Ordinal);
            foreach ((string name, string table) in new[]
            {
                ("locations", "named_locations"), ("groups", "friendly_groups"), ("units", "friendly_units"),
                ("contacts", "known_contacts"), ("tasks", "current_tasks"), ("markers", "current_markers")
            }) counts[name] = Count(table);
            bool baseline = ScalarLong("SELECT COALESCE(baseline_ready,0) FROM state_worlds WHERE world_key=$key;",
                ("$key", WorldKey(_worldName, _worldSize))) == 1;
            long size = _databasePath == ":memory:" || !File.Exists(_databasePath) ? 0 : new FileInfo(_databasePath).Length;
            return new StateRepositoryDiagnostics(_readiness, _activeSessionAlias, _worldName, baseline,
                _lastSequence, _lastSnapshotAt, ReadMetadata().ToArray(), counts, size, SchemaVersion,
                ProtocolVersion, _diagnosticCode);
        }
    }

    public void ResetCache()
    {
        lock (_gate)
        {
            _connection?.Dispose();
            _connection = null;
            if (_databasePath != ":memory:")
            {
                foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
                    if (File.Exists(path)) File.Delete(path);
            }
            _activeSessionHash = _activeSessionAlias = _activeMissionHash = _worldName = string.Empty;
            _worldSize = 0;
            _lastSequence = 0;
            _lastSnapshotAt = null;
            OpenAndMigrate();
        }
        StateChanged?.Invoke();
    }

    private void ApplySection(
        SqliteTransaction transaction,
        StateSnapshotSection section,
        double publishedAtGameTime,
        DateTimeOffset receivedAtUtc)
    {
        bool preserve = section.Readiness is StateSectionReadiness.Failed or StateSectionReadiness.Unavailable;
        if (!preserve)
        {
            switch (section.Name)
            {
                case "player": ReplaceScalar(transaction, "current_player", SanitizePlayer(section.Payload)); break;
                case "environment": ReplaceScalar(transaction, "current_environment", StripMetadata(section.Payload)); break;
                case "timeAstronomy": ReplaceScalar(transaction, "current_time_astronomy", StripMetadata(section.Payload)); break;
                case "loadout": ReplaceScalar(transaction, "current_loadout", StripMetadata(section.Payload)); break;
                case "friendlyForces": ReplaceFriendlyForces(transaction, section.Payload); break;
                case "knownContacts": ReplaceContacts(transaction, section.Payload); break;
                case "tasks": ReplaceTasks(transaction, section.Payload); break;
                case "markers": ReplaceMarkers(transaction, section.Payload); break;
                default: throw new InvalidDataException("Unknown state section.");
            }
        }

        bool stale = preserve || section.Readiness == StateSectionReadiness.Stale;
        double sampleLagSeconds = Math.Max(0, publishedAtGameTime - section.SampledAtGameTime);
        string sampledUtc = receivedAtUtc.Subtract(TimeSpan.FromSeconds(sampleLagSeconds))
            .ToString("O", CultureInfo.InvariantCulture);
        string receivedUtc = receivedAtUtc.ToString("O", CultureInfo.InvariantCulture);
        string update = preserve
            ? "readiness=excluded.readiness, received_utc=excluded.received_utc, is_stale=excluded.is_stale"
            : "readiness=excluded.readiness, sampled_at=excluded.sampled_at, sampled_utc=excluded.sampled_utc, received_utc=excluded.received_utc, is_stale=excluded.is_stale";
        Execute(transaction, $"""
            INSERT INTO state_section_metadata(session_hash, section, readiness, sampled_at, sampled_utc, received_utc, is_stale)
            VALUES($session,$section,$readiness,$sampled,$sampledUtc,$received,$stale)
            ON CONFLICT(session_hash,section) DO UPDATE SET {update};
            """, ("$session", _activeSessionHash), ("$section", section.Name),
            ("$readiness", ReadinessText(section.Readiness)), ("$sampled", section.SampledAtGameTime),
            ("$sampledUtc", sampledUtc), ("$received", receivedUtc), ("$stale", stale ? 1 : 0));
    }

    private void ReplaceFriendlyForces(SqliteTransaction tx, JsonElement section)
    {
        Execute(tx, "DELETE FROM group_waypoints; DELETE FROM friendly_units; DELETE FROM friendly_groups;");
        foreach (JsonElement item in Array(section, "groups").Take(128))
        {
            string raw = RequiredText(item, "sourceId");
            string alias = Alias("group", raw);
            JsonObject value = new()
            {
                ["alias"] = alias,
                ["callsign"] = Text(item, "callsign"),
                ["leaderAlias"] = Alias("unit", Text(item, "leaderSourceId")),
                ["memberAliases"] = new JsonArray(Strings(item, "memberSourceIds").Select(id => (JsonNode?)Alias("unit", id)).ToArray()),
                ["leaderPosition"] = Clone(item, "leaderPosition"),
                ["behaviour"] = Text(item, "behaviour"),
                ["combatMode"] = Text(item, "combatMode"),
                ["formation"] = Text(item, "formation"),
                ["expectedDestination"] = Clone(item, "expectedDestination"),
                ["assignedTargetAliases"] = new JsonArray(Strings(item, "assignedTargetSourceIds").Select(id => (JsonNode?)Alias("contact", id)).ToArray())
            };
            if (item.TryGetProperty("waypoint", out JsonElement waypoint) && waypoint.ValueKind == JsonValueKind.Object)
            {
                JsonNode? node = JsonNode.Parse(waypoint.GetRawText());
                value["waypoint"] = node;
                Execute(tx, "INSERT INTO group_waypoints(session_hash, group_alias, payload_json) VALUES($session,$alias,$json);",
                    ("$session", _activeSessionHash), ("$alias", alias), ("$json", node!.ToJsonString()));
            }
            InsertEntity(tx, "friendly_groups", alias, HashId("group", raw), value);
        }
        foreach (JsonElement item in Array(section, "units").Take(512))
        {
            string raw = RequiredText(item, "sourceId");
            JsonObject value = new()
            {
                ["alias"] = Alias("unit", raw), ["groupAlias"] = Alias("group", Text(item, "groupSourceId")),
                ["class"] = Text(item, "class"), ["displayRole"] = Text(item, "displayRole"),
                ["position"] = Clone(item, "position"), ["alive"] = Boolean(item, "alive"),
                ["lifeState"] = Text(item, "lifeState"), ["mobile"] = Boolean(item, "mobile"),
                ["damage"] = Number(item, "damage"), ["currentCommand"] = Text(item, "currentCommand"),
                ["assignedTargetAlias"] = Alias("contact", Text(item, "assignedTargetSourceId")),
                ["vehicleAlias"] = Alias("vehicle", Text(item, "vehicleSourceId")),
                ["vehicleRole"] = Text(item, "vehicleRole")
            };
            InsertEntity(tx, "friendly_units", Alias("unit", raw), HashId("unit", raw), value);
        }
    }

    private void ReplaceContacts(SqliteTransaction tx, JsonElement section)
    {
        Execute(tx, "DELETE FROM known_contact_sources; DELETE FROM known_contacts;");
        foreach (JsonElement item in Array(section, "contacts").Take(256))
        {
            string raw = RequiredText(item, "sourceId");
            string alias = Alias("contact", raw);
            string[] groupAliases = Strings(item, "observerGroupSourceIds").Select(id => Alias("group", id)).Distinct(StringComparer.Ordinal).ToArray();
            JsonObject value = new()
            {
                ["alias"] = alias, ["class"] = Text(item, "class"), ["displayName"] = Text(item, "displayName"),
                ["contactType"] = Text(item, "contactType"), ["perceivedSide"] = Text(item, "perceivedSide"),
                ["relationship"] = Text(item, "relationship"), ["estimatedPosition"] = Clone(item, "estimatedPosition"),
                ["positionErrorMeters"] = Number(item, "positionErrorMeters"),
                ["lastSeenAgeSeconds"] = Number(item, "lastSeenAgeSeconds"),
                ["lastThreatAgeSeconds"] = Number(item, "lastThreatAgeSeconds"),
                ["observerGroupAliases"] = new JsonArray(groupAliases.Select(x => (JsonNode?)x).ToArray())
            };
            StateKnownContact candidate = new(alias, Text(item, "class"), Text(item, "displayName"), Text(item, "contactType"),
                Text(item, "perceivedSide"), Text(item, "relationship"), Vector(item, "estimatedPosition"),
                Number(item, "positionErrorMeters"), Number(item, "lastSeenAgeSeconds"),
                Number(item, "lastThreatAgeSeconds"), groupAliases,
                new StateSectionMetadata("knownContacts", StateSectionReadiness.Ready, 0, DateTimeOffset.UnixEpoch, 0, false));
            if (!ContactEligibilityPolicy.IsEligible(candidate)) continue;
            InsertEntity(tx, "known_contacts", alias, HashId("contact", raw), value);
            foreach (string groupAlias in groupAliases)
                Execute(tx, "INSERT INTO known_contact_sources(contact_alias,group_alias) VALUES($contact,$group);",
                    ("$contact", alias), ("$group", groupAlias));
        }
    }

    private void ReplaceTasks(SqliteTransaction tx, JsonElement section)
    {
        Execute(tx, "DELETE FROM current_tasks;");
        foreach (JsonElement item in Array(section, "tasks").Take(128))
        {
            string raw = RequiredText(item, "sourceId");
            JsonObject value = new()
            {
                ["alias"] = Alias("task", raw), ["title"] = Text(item, "title"),
                ["description"] = Text(item, "description"), ["destination"] = Clone(item, "destination"),
                ["type"] = Text(item, "type"), ["status"] = Text(item, "status"),
                ["parentAlias"] = Alias("task", Text(item, "parentSourceId")), ["active"] = Boolean(item, "active")
            };
            InsertEntity(tx, "current_tasks", Alias("task", raw), HashId("task", raw), value);
        }
    }

    private void ReplaceMarkers(SqliteTransaction tx, JsonElement section)
    {
        Execute(tx, "DELETE FROM current_markers;");
        foreach (JsonElement item in Array(section, "markers").Take(256))
        {
            string raw = RequiredText(item, "sourceId");
            JsonObject value = new()
            {
                ["alias"] = Alias("marker", raw), ["text"] = Text(item, "text"),
                ["position"] = Clone(item, "position"), ["type"] = Text(item, "type"),
                ["color"] = Text(item, "color"), ["shape"] = Text(item, "shape"),
                ["size"] = Clone(item, "size"), ["direction"] = Number(item, "direction"),
                ["alpha"] = Number(item, "alpha"), ["polyline"] = Clone(item, "polyline")
            };
            InsertEntity(tx, "current_markers", Alias("marker", raw), HashId("marker", raw), value);
        }
    }

    private JsonNode SanitizePlayer(JsonElement section)
    {
        JsonObject value = (JsonObject)StripMetadata(section);
        value.Remove("sourceId");
        string groupId = Text(section, "groupSourceId");
        value.Remove("groupSourceId");
        value["alias"] = "player-self";
        value["groupAlias"] = Alias("group", groupId);
        return value;
    }

    private static JsonNode StripMetadata(JsonElement section)
    {
        JsonObject value = (JsonObject)(JsonNode.Parse(section.GetRawText()) ?? new JsonObject());
        value.Remove("sampledAt");
        value.Remove("readiness");
        return value;
    }

    private void ReplaceScalar(SqliteTransaction tx, string table, JsonNode payload)
    {
        Execute(tx, $"DELETE FROM {table};");
        Execute(tx, $"INSERT INTO {table}(session_hash,payload_json) VALUES($session,$json);",
            ("$session", _activeSessionHash), ("$json", payload.ToJsonString()));
    }

    private void InsertEntity(SqliteTransaction tx, string table, string alias, string sourceHash, JsonNode payload)
        => Execute(tx, $"INSERT INTO {table}(session_hash,alias,source_hash,payload_json) VALUES($session,$alias,$hash,$json);",
            ("$session", _activeSessionHash), ("$alias", alias), ("$hash", sourceHash), ("$json", payload.ToJsonString()));

    private JsonElement? ReadScalar(string table)
    {
        if (!EnsureReady()) return null;
        using SqliteCommand command = _connection!.CreateCommand();
        command.CommandText = $"SELECT payload_json FROM {table} WHERE session_hash=$session LIMIT 1;";
        command.Parameters.AddWithValue("$session", _activeSessionHash);
        object? value = command.ExecuteScalar();
        return value is string json ? JsonDocument.Parse(json).RootElement.Clone() : null;
    }

    private IEnumerable<JsonElement> ReadRows(string table, int limit)
    {
        if (!EnsureReady()) yield break;
        using SqliteCommand command = _connection!.CreateCommand();
        command.CommandText = $"SELECT payload_json FROM {table} WHERE session_hash=$session ORDER BY alias LIMIT $limit;";
        command.Parameters.AddWithValue("$session", _activeSessionHash);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) yield return JsonDocument.Parse(reader.GetString(0)).RootElement.Clone();
    }

    private IEnumerable<StateSectionMetadata> ReadMetadata()
    {
        if (!EnsureReady() || _activeSessionHash.Length == 0) yield break;
        using SqliteCommand command = _connection!.CreateCommand();
        command.CommandText = "SELECT section,readiness,sampled_at,sampled_utc,received_utc,is_stale FROM state_section_metadata WHERE session_hash=$session ORDER BY section;";
        command.Parameters.AddWithValue("$session", _activeSessionHash);
        using SqliteDataReader reader = command.ExecuteReader();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        while (reader.Read())
        {
            DateTimeOffset sampled = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            DateTimeOffset received = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            yield return new StateSectionMetadata(reader.GetString(0), ParseReadiness(reader.GetString(1)), reader.GetDouble(2),
                received, Math.Max(0, (now - sampled).TotalSeconds), reader.GetInt64(5) != 0);
        }
    }

    private StateSectionMetadata? GetMetadata(string section)
        => ReadMetadata().FirstOrDefault(item => string.Equals(item.Section, section, StringComparison.Ordinal));

    private void OpenAndMigrate()
    {
        try
        {
            if (_databasePath != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            _connection = new SqliteConnection(_databasePath == ":memory:"
                ? "Data Source=:memory:;Pooling=False"
                : $"Data Source={_databasePath};Cache=Shared;Pooling=False");
            _connection.Open();
            using SqliteCommand pragmas = _connection.CreateCommand();
            pragmas.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragmas.ExecuteNonQuery();
            long version = ScalarLong("PRAGMA user_version;");
            if (version > SchemaVersion) throw new InvalidDataException("Unsupported newer state schema.");
            using SqliteTransaction transaction = _connection.BeginTransaction();
            Execute(transaction, SchemaSql);
            if (version == 1)
            {
                Execute(transaction, "ALTER TABLE state_section_metadata ADD COLUMN sampled_utc TEXT NOT NULL DEFAULT '1970-01-01T00:00:00.0000000+00:00';");
                Execute(transaction, "UPDATE state_section_metadata SET sampled_utc=received_utc;");
            }
            if (version < 3)
            {
                Execute(transaction, "DELETE FROM known_contact_sources; DELETE FROM known_contacts;");
            }
            Execute(transaction, $"PRAGMA user_version={SchemaVersion};");
            transaction.Commit();
            Execute(null, "UPDATE state_section_metadata SET readiness='stale', is_stale=1 WHERE readiness='ready';");
            LoadActiveSession();
            _readiness = StateRepositoryReadiness.Ready;
            _diagnosticCode = string.Empty;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _connection?.Dispose();
            _connection = null;
            _readiness = StateRepositoryReadiness.Failed;
            _diagnosticCode = "state_database_open_failed";
        }
    }

    private void LoadActiveSession()
    {
        using SqliteCommand command = _connection!.CreateCommand();
        command.CommandText = "SELECT session_hash,mission_hash,alias,world_name,world_size,last_sequence,last_received_utc FROM state_sessions WHERE active=1 LIMIT 1;";
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return;
        _activeSessionHash = reader.GetString(0); _activeMissionHash = reader.GetString(1);
        _activeSessionAlias = reader.GetString(2); _worldName = reader.GetString(3); _worldSize = reader.GetDouble(4);
        _lastSequence = reader.GetInt64(5);
        if (!reader.IsDBNull(6)) _lastSnapshotAt = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private void ClearDynamic(SqliteTransaction transaction)
    {
        foreach (string table in DynamicTables) Execute(transaction, $"DELETE FROM {table};");
    }

    private void Execute(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = _connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private long ScalarLong(string sql, params (string Name, object? Value)[] parameters)
    {
        if (!EnsureReady() && _connection is null) return 0;
        using SqliteCommand command = _connection!.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        object? result = command.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private int Count(string table) => (int)ScalarLong($"SELECT COUNT(*) FROM {table};");
    private bool EnsureReady() => !_disposed && _connection is not null && _readiness != StateRepositoryReadiness.Failed;
    private StateIngestResult Failed() => new(TelemetryIngestStatus.Rejected, _diagnosticCode.Length == 0 ? "state_database_unavailable" : _diagnosticCode);
    private string HashId(string kind, string raw) => Hash(_activeSessionHash + "\0" + kind + "\0" + raw);
    private string Alias(string kind, string raw) => string.IsNullOrWhiteSpace(raw) ? string.Empty : kind + "-" + HashId(kind, raw)[..8].ToLowerInvariant();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string WorldKey(string name, double size) => Hash(name.ToUpperInvariant() + "\0" + size.ToString("R", CultureInfo.InvariantCulture));
    private static string ReadinessText(StateSectionReadiness value) => value.ToString().ToLowerInvariant();
    private static StateSectionReadiness ParseReadiness(string value) => value switch
    {
        "ready" => StateSectionReadiness.Ready, "stale" => StateSectionReadiness.Stale,
        "unavailable" => StateSectionReadiness.Unavailable, _ => StateSectionReadiness.Failed
    };

    private static JsonNode? Clone(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? JsonNode.Parse(value.GetRawText()) : null;
    private static string RequiredText(JsonElement root, string name)
    {
        string value = Text(root, name); if (value.Length == 0) throw new InvalidDataException(name); return value;
    }
    private static string Text(JsonElement root, string name, string fallback = "")
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private static double Number(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) ? number : 0;
    private static double? NullableNumber(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) ? number : null;
    private static int Integer(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : 0;
    private static bool Boolean(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
    private static IEnumerable<JsonElement> Array(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();
    private static string[] Strings(JsonElement root, string name)
        => Array(root, name).Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToArray();
    private static double[] Numbers(JsonElement root, string name, int limit)
        => Array(root, name).Take(limit).Where(item => item.ValueKind == JsonValueKind.Number).Select(item => item.GetDouble()).ToArray();
    private static int[] IntNumbers(JsonElement root, string name, int limit)
        => Array(root, name).Take(limit).Where(item => item.ValueKind == JsonValueKind.Number).Select(item => item.GetInt32()).ToArray();
    private static WorldPosition Vector(JsonElement root, string name) => OptionalVector(root, name) ?? new WorldPosition(0, 0, 0);
    private static WorldPosition? OptionalVector(JsonElement root, string name)
    {
        double[] values = Numbers(root, name, 3); return values.Length >= 2 ? new WorldPosition(values[0], values[1], values.ElementAtOrDefault(2)) : null;
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS state_sessions(session_hash TEXT PRIMARY KEY, mission_hash TEXT NOT NULL,
            alias TEXT NOT NULL, world_name TEXT NOT NULL, world_size REAL NOT NULL, active INTEGER NOT NULL,
            last_sequence INTEGER NOT NULL DEFAULT 0, last_received_utc TEXT);
        CREATE TABLE IF NOT EXISTS state_worlds(world_key TEXT PRIMARY KEY, world_name TEXT NOT NULL,
            world_size REAL NOT NULL, baseline_ready INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS named_locations(world_key TEXT NOT NULL, alias TEXT NOT NULL,
            source_hash TEXT NOT NULL, official_name TEXT NOT NULL, normalized_name TEXT NOT NULL,
            location_type TEXT NOT NULL, x REAL NOT NULL, y REAL NOT NULL, radius_a REAL NOT NULL,
            radius_b REAL NOT NULL, angle REAL NOT NULL, PRIMARY KEY(world_key,alias));
        CREATE INDEX IF NOT EXISTS ix_named_locations_name ON named_locations(world_key,normalized_name);
        CREATE TABLE IF NOT EXISTS current_environment(session_hash TEXT PRIMARY KEY, payload_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS current_time_astronomy(session_hash TEXT PRIMARY KEY, payload_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS current_player(session_hash TEXT PRIMARY KEY, payload_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS current_loadout(session_hash TEXT PRIMARY KEY, payload_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS friendly_groups(session_hash TEXT NOT NULL, alias TEXT PRIMARY KEY,
            source_hash TEXT NOT NULL, payload_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS friendly_units(session_hash TEXT NOT NULL, alias TEXT PRIMARY KEY,
            source_hash TEXT NOT NULL, payload_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS group_waypoints(session_hash TEXT NOT NULL, group_alias TEXT PRIMARY KEY,
            payload_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS known_contacts(session_hash TEXT NOT NULL, alias TEXT PRIMARY KEY,
            source_hash TEXT NOT NULL, payload_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS known_contact_sources(contact_alias TEXT NOT NULL, group_alias TEXT NOT NULL,
            PRIMARY KEY(contact_alias,group_alias));
        CREATE TABLE IF NOT EXISTS current_tasks(session_hash TEXT NOT NULL, alias TEXT PRIMARY KEY,
            source_hash TEXT NOT NULL, payload_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS current_markers(session_hash TEXT NOT NULL, alias TEXT PRIMARY KEY,
            source_hash TEXT NOT NULL, payload_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS state_section_metadata(session_hash TEXT NOT NULL, section TEXT NOT NULL,
            readiness TEXT NOT NULL, sampled_at REAL NOT NULL, sampled_utc TEXT NOT NULL,
            received_utc TEXT NOT NULL, is_stale INTEGER NOT NULL,
            PRIMARY KEY(session_hash,section));
        """;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _connection?.Dispose();
            _connection = null;
        }
    }
}
