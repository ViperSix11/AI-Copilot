using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArmaAiBridge.App.Models;
using Microsoft.Data.Sqlite;

namespace ArmaAiBridge.App.Services;

public sealed class SqliteStateRepository : IStateRepository, IMissionMemoryRepository, IDisposable
{
    public const int SchemaVersion = 5;
    public const int ProtocolVersion = 2;
    private static readonly string[] DynamicTables =
    {
        "current_environment", "current_time_astronomy", "current_player", "current_loadout",
        "friendly_groups", "friendly_units", "group_waypoints", "known_contact_sources",
        "known_contacts", "current_tasks", "current_markers", "reported_locations", "state_section_metadata"
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
    // Dialogue focus and player memory are session-scoped even though contact tracks remain mission-scoped.
    public string ActiveMissionKey { get { lock (_gate) return _activeSessionHash; } }

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
                    Execute(transaction, "DELETE FROM memory_entries;");
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
                    INSERT INTO missions(mission_hash, world_key, first_seen_utc, last_seen_utc)
                    VALUES($mission,$world,$received,$received)
                    ON CONFLICT(mission_hash) DO UPDATE SET last_seen_utc=excluded.last_seen_utc;
                    """, ("$mission", missionHash), ("$world", WorldKey(worldName, worldSize)),
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
                WorldPosition? playerPosition = snapshot.Sections.TryGetValue("player", out StateSnapshotSection? playerSection)
                    ? OptionalVector(playerSection.Payload, "positionATL") : null;
                foreach (StateSnapshotSection section in snapshot.Sections.Values.OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    ApplySection(transaction, section, snapshot.PublishedAtGameTime, snapshot.ReceivedAtUtc, playerPosition);
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
            return new StateEnvironment(Number(root, "overcast"), Number(root, "forecastOvercast"),
                Number(root, "rain"), Number(root, "fog"), Numbers(root, "fogParameters", 3),
                Number(root, "forecastFog"), Number(root, "waves"), Number(root, "lightning"),
                Number(root, "humidity"), Number(root, "nextWeatherChange"), metadata);
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
            return new StateLoadout(Text(root, "primaryWeapon"), Text(root, "launcher"), Text(root, "handgun"),
                Text(root, "selectedWeapon"), Text(root, "selectedWeaponDisplayName"), Text(root, "muzzle"),
                Text(root, "fireMode"), Text(root, "currentMagazine"), Integer(root, "loadedRounds"),
                Strings(root, "opticsAndAttachments"), Text(root, "binocular"), magazines, totals,
                Integer(root, "grenadeCount"), Integer(root, "throwableCount"), Integer(root, "mineCount"),
                Integer(root, "explosiveCount"), Strings(root, "assignedItems"), Text(root, "uniformClass"),
                Text(root, "vestClass"), Text(root, "backpackClass"), Text(root, "loadoutHash"), metadata);
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
        DateTimeOffset receivedAtUtc,
        WorldPosition? playerPosition)
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
                case "knownContacts": ReplaceContacts(transaction, section.Payload, receivedAtUtc, playerPosition); break;
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

    private void ReplaceContacts(SqliteTransaction tx, JsonElement section, DateTimeOffset receivedAtUtc, WorldPosition? playerPosition)
    {
        Execute(tx, "DELETE FROM known_contact_sources; DELETE FROM known_contacts;");
        Execute(tx, "UPDATE contact_tracks SET status='last-known' WHERE mission_hash=$mission AND status='current';",
            ("$mission", _activeMissionHash));
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
            if (!ContactEligibilityPolicy.IsEligible(candidate) ||
                candidate.Relationship is not ("hostile" or "unknown") ||
                candidate.PerceivedSide is "WEST" or "CIV") continue;
            InsertEntity(tx, "known_contacts", alias, HashId("contact", raw), value);
            string trackId = UpsertContactTrack(tx, raw, candidate, receivedAtUtc, playerPosition);
            foreach (string groupAlias in groupAliases)
            {
                Execute(tx, "INSERT INTO known_contact_sources(contact_alias,group_alias) VALUES($contact,$group);",
                    ("$contact", alias), ("$group", groupAlias));
                string callsign = FriendlyGroupCallsign(tx, groupAlias);
                if (callsign.Length > 0)
                    Execute(tx, """
                        INSERT INTO contact_reporters(track_id,group_alias,callsign,last_observed_utc)
                        VALUES($track,$group,$callsign,$observed)
                        ON CONFLICT(track_id,group_alias) DO UPDATE SET callsign=excluded.callsign,last_observed_utc=excluded.last_observed_utc;
                        """, ("$track", trackId), ("$group", groupAlias), ("$callsign", callsign),
                        ("$observed", receivedAtUtc.ToString("O", CultureInfo.InvariantCulture)));
            }
        }
    }

    private string UpsertContactTrack(SqliteTransaction tx, string raw, StateKnownContact contact,
        DateTimeOffset receivedAtUtc, WorldPosition? playerPosition)
    {
        string trackId = "contact-" + Hash(_activeMissionHash + "\0contact\0" + raw)[..12].ToLowerInvariant();
        DateTimeOffset observed = receivedAtUtc.Subtract(TimeSpan.FromSeconds(Math.Max(0, contact.LastSeenAgeSeconds)));
        DateTimeOffset threatened = receivedAtUtc.Subtract(TimeSpan.FromSeconds(Math.Max(0, contact.LastThreatAgeSeconds)));
        string description = ContactEligibilityPolicy.Description(contact);
        Execute(tx, """
            INSERT INTO contact_tracks(track_id,mission_hash,source_hash,contact_type,description,perceived_side,
                relationship,status,first_observed_utc,last_observed_utc,last_threat_utc,x,y,z,uncertainty_m,observation_count,corroborated)
            VALUES($id,$mission,$hash,$type,$description,$side,$relationship,'current',$observed,$observed,$threat,
                $x,$y,$z,$uncertainty,1,0)
            ON CONFLICT(track_id) DO UPDATE SET contact_type=excluded.contact_type,description=excluded.description,
                perceived_side=excluded.perceived_side,relationship=excluded.relationship,status='current',
                last_observed_utc=excluded.last_observed_utc,last_threat_utc=excluded.last_threat_utc,
                x=excluded.x,y=excluded.y,z=excluded.z,uncertainty_m=excluded.uncertainty_m,
                observation_count=contact_tracks.observation_count+1,
                corroborated=CASE WHEN contact_tracks.observation_count>=1 THEN 1 ELSE contact_tracks.corroborated END;
            """, ("$id", trackId), ("$mission", _activeMissionHash),
            ("$hash", Hash(_activeMissionHash + "\0contact\0" + raw)), ("$type", contact.ContactType),
            ("$description", description), ("$side", contact.PerceivedSide), ("$relationship", contact.Relationship),
            ("$observed", observed.ToString("O", CultureInfo.InvariantCulture)),
            ("$threat", threatened.ToString("O", CultureInfo.InvariantCulture)),
            ("$x", contact.EstimatedPosition.X), ("$y", contact.EstimatedPosition.Y), ("$z", contact.EstimatedPosition.Z),
            ("$uncertainty", Math.Max(0, contact.PositionErrorMeters)));
        Execute(tx, """
            INSERT INTO contact_observations(track_id,observed_utc,received_utc,x,y,z,uncertainty_m,player_x,player_y,player_z)
            VALUES($id,$observed,$received,$x,$y,$z,$uncertainty,$px,$py,$pz);
            """, ("$id", trackId), ("$observed", observed.ToString("O", CultureInfo.InvariantCulture)),
            ("$received", receivedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
            ("$x", contact.EstimatedPosition.X), ("$y", contact.EstimatedPosition.Y), ("$z", contact.EstimatedPosition.Z),
            ("$uncertainty", Math.Max(0, contact.PositionErrorMeters)),
            ("$px", playerPosition?.X), ("$py", playerPosition?.Y), ("$pz", playerPosition?.Z));
        return trackId;
    }

    private string FriendlyGroupCallsign(SqliteTransaction tx, string groupAlias)
    {
        using SqliteCommand command = _connection!.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT payload_json FROM friendly_groups WHERE session_hash=$session AND alias=$alias LIMIT 1;";
        command.Parameters.AddWithValue("$session", _activeSessionHash);
        command.Parameters.AddWithValue("$alias", groupAlias);
        object? value = command.ExecuteScalar();
        if (value is not string json) return string.Empty;
        using JsonDocument document = JsonDocument.Parse(json);
        return Text(document.RootElement, "callsign").Trim();
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
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 512));
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

    private long ScalarLong(SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = _connection!.CreateCommand();
        command.Transaction = transaction;
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
        CREATE TABLE IF NOT EXISTS missions(mission_hash TEXT PRIMARY KEY, world_key TEXT NOT NULL,
            first_seen_utc TEXT NOT NULL, last_seen_utc TEXT NOT NULL);
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
        CREATE TABLE IF NOT EXISTS contact_tracks(track_id TEXT PRIMARY KEY, mission_hash TEXT NOT NULL,
            source_hash TEXT NOT NULL, contact_type TEXT NOT NULL, description TEXT NOT NULL,
            perceived_side TEXT NOT NULL, relationship TEXT NOT NULL, status TEXT NOT NULL,
            first_observed_utc TEXT NOT NULL, last_observed_utc TEXT NOT NULL, last_threat_utc TEXT NOT NULL,
            x REAL NOT NULL, y REAL NOT NULL, z REAL NOT NULL, uncertainty_m REAL NOT NULL,
            observation_count INTEGER NOT NULL, corroborated INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX IF NOT EXISTS ix_contact_tracks_mission ON contact_tracks(mission_hash,status,last_observed_utc);
        CREATE TABLE IF NOT EXISTS contact_observations(observation_id INTEGER PRIMARY KEY AUTOINCREMENT,
            track_id TEXT NOT NULL REFERENCES contact_tracks(track_id) ON DELETE CASCADE,
            observed_utc TEXT NOT NULL, received_utc TEXT NOT NULL, x REAL NOT NULL, y REAL NOT NULL,
            z REAL NOT NULL, uncertainty_m REAL NOT NULL, player_x REAL, player_y REAL, player_z REAL);
        CREATE INDEX IF NOT EXISTS ix_contact_observations_track ON contact_observations(track_id,observed_utc);
        CREATE TABLE IF NOT EXISTS contact_reporters(track_id TEXT NOT NULL REFERENCES contact_tracks(track_id) ON DELETE CASCADE,
            group_alias TEXT NOT NULL, callsign TEXT NOT NULL, last_observed_utc TEXT NOT NULL,
            PRIMARY KEY(track_id,group_alias));
        CREATE TABLE IF NOT EXISTS contact_groups(group_id TEXT PRIMARY KEY, mission_hash TEXT NOT NULL,
            member_track_ids_json TEXT NOT NULL, x REAL NOT NULL, y REAL NOT NULL, updated_utc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS memory_entries(entry_id INTEGER PRIMARY KEY AUTOINCREMENT,
            mission_hash TEXT NOT NULL, content TEXT NOT NULL, provenance TEXT NOT NULL,
            created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL, deleted INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX IF NOT EXISTS ix_memory_entries_mission ON memory_entries(mission_hash,deleted,updated_utc);
        CREATE TABLE IF NOT EXISTS memory_entry_tags(entry_id INTEGER NOT NULL REFERENCES memory_entries(entry_id) ON DELETE CASCADE,
            tag TEXT NOT NULL, PRIMARY KEY(entry_id,tag));
        CREATE TABLE IF NOT EXISTS memory_entry_positions(entry_id INTEGER PRIMARY KEY REFERENCES memory_entries(entry_id) ON DELETE CASCADE,
            x REAL NOT NULL, y REAL NOT NULL, z REAL NOT NULL, uncertainty_m REAL NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS reported_locations(session_hash TEXT NOT NULL, location_key TEXT NOT NULL,
            label TEXT NOT NULL, grid TEXT NOT NULL, x REAL NOT NULL, y REAL NOT NULL,
            uncertainty_m REAL NOT NULL, reported_utc TEXT NOT NULL, PRIMARY KEY(session_hash,location_key));
        CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(content, content='memory_entries', content_rowid='entry_id');
        CREATE TRIGGER IF NOT EXISTS memory_ai AFTER INSERT ON memory_entries BEGIN
            INSERT INTO memory_fts(rowid,content) VALUES(new.entry_id,new.content);
        END;
        CREATE TRIGGER IF NOT EXISTS memory_ad AFTER DELETE ON memory_entries BEGIN
            INSERT INTO memory_fts(memory_fts,rowid,content) VALUES('delete',old.entry_id,old.content);
        END;
        CREATE TRIGGER IF NOT EXISTS memory_au AFTER UPDATE OF content ON memory_entries BEGIN
            INSERT INTO memory_fts(memory_fts,rowid,content) VALUES('delete',old.entry_id,old.content);
            INSERT INTO memory_fts(rowid,content) VALUES(new.entry_id,new.content);
        END;
        CREATE TABLE IF NOT EXISTS lore_sections(mission_hash TEXT NOT NULL, scope TEXT NOT NULL,
            content TEXT NOT NULL, enabled INTEGER NOT NULL, always_include INTEGER NOT NULL,
            updated_utc TEXT NOT NULL, PRIMARY KEY(mission_hash,scope));
        """;

    public IReadOnlyList<MissionContactTrack> GetContactTracks(int limit = 256, bool includeForgotten = false)
    {
        lock (_gate)
        {
            if (!EnsureReady() || _activeMissionHash.Length == 0) return System.Array.Empty<MissionContactTrack>();
            using SqliteCommand command = _connection!.CreateCommand();
            command.CommandText = """
                SELECT track_id,contact_type,description,perceived_side,relationship,status,
                    first_observed_utc,last_observed_utc,last_threat_utc,x,y,z,uncertainty_m,observation_count,corroborated,
                    COALESCE((SELECT group_concat(DISTINCT callsign) FROM contact_reporters r
                        WHERE r.track_id=contact_tracks.track_id AND callsign<>''),'')
                FROM contact_tracks WHERE mission_hash=$mission AND ($forgotten=1 OR status<>'forgotten')
                ORDER BY CASE status WHEN 'current' THEN 0 WHEN 'last-known' THEN 1 WHEN 'dead' THEN 2 ELSE 3 END,
                    last_observed_utc DESC,track_id LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$mission", _activeMissionHash);
            command.Parameters.AddWithValue("$forgotten", includeForgotten ? 1 : 0);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 256));
            using SqliteDataReader reader = command.ExecuteReader();
            List<MissionContactTrack> result = new();
            while (reader.Read()) result.Add(new MissionContactTrack(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                ParseDate(reader.GetString(6)), ParseDate(reader.GetString(7)), ParseDate(reader.GetString(8)),
                new WorldPosition(reader.GetDouble(9), reader.GetDouble(10), reader.GetDouble(11)), reader.GetDouble(12),
                reader.GetInt32(13), reader.GetInt64(14) != 0,
                reader.GetString(15).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(IsPrivacySafeCallsign).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray()));
            return result;
        }
    }

    public IReadOnlyList<MissionContactObservation> GetContactObservations(string trackId, int limit = 20)
    {
        lock (_gate)
        {
            if (!EnsureReady() || string.IsNullOrWhiteSpace(trackId)) return System.Array.Empty<MissionContactObservation>();
            using SqliteCommand command = _connection!.CreateCommand();
            command.CommandText = """
                SELECT o.observation_id,o.track_id,o.observed_utc,o.received_utc,o.x,o.y,o.z,o.uncertainty_m,
                    o.player_x,o.player_y,o.player_z FROM contact_observations o
                JOIN contact_tracks t ON t.track_id=o.track_id
                WHERE o.track_id=$id AND t.mission_hash=$mission ORDER BY o.observed_utc DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$id", trackId.Trim());
            command.Parameters.AddWithValue("$mission", _activeMissionHash);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50));
            using SqliteDataReader reader = command.ExecuteReader();
            List<MissionContactObservation> result = new();
            while (reader.Read()) result.Add(new MissionContactObservation(reader.GetInt64(0), reader.GetString(1),
                ParseDate(reader.GetString(2)), ParseDate(reader.GetString(3)),
                new WorldPosition(reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6)), reader.GetDouble(7),
                reader.IsDBNull(8) ? null : new WorldPosition(reader.GetDouble(8), reader.GetDouble(9), reader.GetDouble(10))));
            return result;
        }
    }

    public bool MarkContactDead(string trackId)
    {
        lock (_gate)
        {
            RequireActiveMission();
            Execute(null, "UPDATE contact_tracks SET status='dead' WHERE track_id=$id AND mission_hash=$mission AND status<>'forgotten';",
                ("$id", trackId.Trim()), ("$mission", _activeMissionHash));
            return ScalarLong("SELECT changes();") > 0;
        }
    }

    public bool ForgetContact(string trackId)
    {
        lock (_gate)
        {
            RequireActiveMission();
            Execute(null, "DELETE FROM contact_tracks WHERE track_id=$id AND mission_hash=$mission;",
                ("$id", trackId.Trim()), ("$mission", _activeMissionHash));
            return ScalarLong("SELECT changes();") > 0;
        }
    }

    public long Remember(string text, string provenance, IReadOnlyList<string>? tags = null, WorldPosition? position = null)
    {
        string content = RequireBoundedText(text, 2000, "Memory text");
        string source = NormalizeProvenance(provenance);
        lock (_gate)
        {
            RequireActiveSession();
            using SqliteTransaction tx = _connection!.BeginTransaction();
            string now = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
            Execute(tx, "INSERT INTO memory_entries(mission_hash,content,provenance,created_utc,updated_utc) VALUES($mission,$content,$source,$now,$now);",
                ("$mission", _activeSessionHash), ("$content", content), ("$source", source), ("$now", now));
            long id = ScalarLong(tx, "SELECT last_insert_rowid();");
            ReplaceMemoryDetails(tx, id, tags, position);
            tx.Commit();
            return id;
        }
    }

    public IReadOnlyList<MissionMemoryEntry> SearchMemory(string query, int limit = 12, int maximumCharacters = 6000)
    {
        lock (_gate)
        {
            if (!EnsureReady() || _activeSessionHash.Length == 0) return System.Array.Empty<MissionMemoryEntry>();
            string normalized = (query ?? string.Empty).Trim();
            using SqliteCommand command = _connection!.CreateCommand();
            command.CommandText = normalized.Length == 0
                ? "SELECT entry_id,content,provenance,created_utc,updated_utc FROM memory_entries WHERE mission_hash=$mission AND deleted=0 ORDER BY updated_utc DESC LIMIT $limit;"
                : "SELECT e.entry_id,e.content,e.provenance,e.created_utc,e.updated_utc FROM memory_fts f JOIN memory_entries e ON e.entry_id=f.rowid WHERE e.mission_hash=$mission AND e.deleted=0 AND memory_fts MATCH $query ORDER BY bm25(memory_fts),e.updated_utc DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$mission", _activeSessionHash);
            if (normalized.Length > 0) command.Parameters.AddWithValue("$query", FtsQuery(normalized));
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 12));
            using SqliteDataReader reader = command.ExecuteReader();
            List<(long Id, string Content, string Provenance, DateTimeOffset Created, DateTimeOffset Updated)> rows = new();
            int characters = 0;
            while (reader.Read())
            {
                string content = reader.GetString(1);
                if (characters + content.Length > Math.Clamp(maximumCharacters, 1, 6000)) break;
                rows.Add((reader.GetInt64(0), content, reader.GetString(2), ParseDate(reader.GetString(3)), ParseDate(reader.GetString(4))));
                characters += content.Length;
            }
            reader.Close();
            return rows.Select(row => new MissionMemoryEntry(row.Id, row.Content, row.Provenance, row.Created,
                row.Updated, ReadTags(row.Id), ReadMemoryPosition(row.Id))).ToArray();
        }
    }

    public bool UpdateMemory(long id, string text, IReadOnlyList<string>? tags = null, WorldPosition? position = null)
    {
        string content = RequireBoundedText(text, 2000, "Memory text");
        lock (_gate)
        {
            RequireActiveSession();
            using SqliteTransaction tx = _connection!.BeginTransaction();
            Execute(tx, "UPDATE memory_entries SET content=$content,updated_utc=$now WHERE entry_id=$id AND mission_hash=$mission AND deleted=0;",
                ("$content", content), ("$now", _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)),
                ("$id", id), ("$mission", _activeSessionHash));
            bool changed = ScalarLong(tx, "SELECT changes();") > 0;
            if (changed) ReplaceMemoryDetails(tx, id, tags, position);
            tx.Commit();
            return changed;
        }
    }

    public bool ForgetMemory(long id)
    {
        lock (_gate)
        {
            RequireActiveSession();
            Execute(null, "DELETE FROM memory_entries WHERE entry_id=$id AND mission_hash=$mission;",
                ("$id", id), ("$mission", _activeSessionHash));
            return ScalarLong("SELECT changes();") > 0;
        }
    }

    public void SaveReportedLocation(ReportedLocationAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        string key = RequireBoundedText(anchor.Key, 64, "Location key").ToLowerInvariant();
        string label = RequireBoundedText(anchor.Label, 80, "Location label");
        if (!System.Text.RegularExpressions.Regex.IsMatch(anchor.Grid ?? string.Empty, "^[0-9]{6}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            !double.IsFinite(anchor.Position.X) || !double.IsFinite(anchor.Position.Y) ||
            !double.IsFinite(anchor.UncertaintyRadiusMeters) || anchor.UncertaintyRadiusMeters is < 0 or > 10000)
            throw new InvalidOperationException("Reported location is invalid.");
        lock (_gate)
        {
            RequireActiveSession();
            Execute(null, """
                INSERT INTO reported_locations(session_hash,location_key,label,grid,x,y,uncertainty_m,reported_utc)
                VALUES($session,$key,$label,$grid,$x,$y,$uncertainty,$reported)
                ON CONFLICT(session_hash,location_key) DO UPDATE SET label=excluded.label,grid=excluded.grid,
                    x=excluded.x,y=excluded.y,uncertainty_m=excluded.uncertainty_m,reported_utc=excluded.reported_utc;
                """, ("$session", _activeSessionHash), ("$key", key), ("$label", label), ("$grid", anchor.Grid),
                ("$x", anchor.Position.X), ("$y", anchor.Position.Y), ("$uncertainty", anchor.UncertaintyRadiusMeters),
                ("$reported", anchor.ReportedAtUtc.ToString("O", CultureInfo.InvariantCulture)));
        }
    }

    public ReportedLocationAnchor? GetReportedLocation(string key)
    {
        string normalized = (key ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0) return null;
        lock (_gate)
        {
            if (!EnsureReady() || _activeSessionHash.Length == 0) return null;
            using SqliteCommand command = _connection!.CreateCommand();
            command.CommandText = "SELECT location_key,label,grid,x,y,uncertainty_m,reported_utc FROM reported_locations WHERE session_hash=$session AND location_key=$key LIMIT 1;";
            command.Parameters.AddWithValue("$session", _activeSessionHash);
            command.Parameters.AddWithValue("$key", normalized);
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read()
                ? new ReportedLocationAnchor(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    new WorldPosition(reader.GetDouble(3), reader.GetDouble(4), 0), reader.GetDouble(5), ParseDate(reader.GetString(6)))
                : null;
        }
    }

    public IReadOnlyList<LoreSection> GetLoreSections()
    {
        lock (_gate)
        {
            if (!EnsureReady()) return System.Array.Empty<LoreSection>();
            using SqliteCommand command = _connection!.CreateCommand();
            command.CommandText = "SELECT scope,content,enabled,always_include,updated_utc FROM lore_sections WHERE mission_hash IN ('common','player',$map,$mission) ORDER BY CASE scope WHEN 'Mission' THEN 0 WHEN 'Map' THEN 1 WHEN 'Player' THEN 2 WHEN 'Target' THEN 3 ELSE 4 END;";
            command.Parameters.AddWithValue("$mission", _activeMissionHash);
            command.Parameters.AddWithValue("$map", "map:" + WorldKey(_worldName, _worldSize));
            using SqliteDataReader reader = command.ExecuteReader();
            List<LoreSection> result = new();
            while (reader.Read()) result.Add(new LoreSection(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0, reader.GetInt64(3) != 0, ParseDate(reader.GetString(4))));
            return result;
        }
    }

    public void SaveLoreSection(string scope, string content, bool enabled, bool alwaysInclude)
    {
        string normalizedScope = NormalizeLoreScope(scope);
        string value = content?.Trim() ?? string.Empty;
        if (value.Length > 2000) throw new InvalidOperationException("A lore section is limited to 2000 characters.");
        lock (_gate)
        {
            RequireActiveMission();
            string mission = LoreScopeKey(normalizedScope);
            Execute(null, """
                INSERT INTO lore_sections(mission_hash,scope,content,enabled,always_include,updated_utc)
                VALUES($mission,$scope,$content,$enabled,$always,$now)
                ON CONFLICT(mission_hash,scope) DO UPDATE SET content=excluded.content,enabled=excluded.enabled,
                    always_include=excluded.always_include,updated_utc=excluded.updated_utc;
                """, ("$mission", mission), ("$scope", normalizedScope), ("$content", value),
                ("$enabled", enabled ? 1 : 0), ("$always", alwaysInclude ? 1 : 0),
                ("$now", _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)));
        }
    }

    public void ClearLoreSection(string scope)
    {
        string normalizedScope = NormalizeLoreScope(scope);
        lock (_gate)
        {
            RequireActiveMission();
            Execute(null, "DELETE FROM lore_sections WHERE mission_hash=$mission AND scope=$scope;",
                ("$mission", LoreScopeKey(normalizedScope)), ("$scope", normalizedScope));
        }
    }

    private void ReplaceMemoryDetails(SqliteTransaction tx, long id, IReadOnlyList<string>? tags, WorldPosition? position)
    {
        Execute(tx, "DELETE FROM memory_entry_tags WHERE entry_id=$id; DELETE FROM memory_entry_positions WHERE entry_id=$id;", ("$id", id));
        foreach (string tag in (tags ?? System.Array.Empty<string>()).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length is > 0 and <= 40).Distinct().Take(16))
            Execute(tx, "INSERT INTO memory_entry_tags(entry_id,tag) VALUES($id,$tag);", ("$id", id), ("$tag", tag));
        if (position is not null) Execute(tx, "INSERT INTO memory_entry_positions(entry_id,x,y,z) VALUES($id,$x,$y,$z);",
            ("$id", id), ("$x", position.X), ("$y", position.Y), ("$z", position.Z));
    }

    private string[] ReadTags(long id)
    {
        using SqliteCommand command = _connection!.CreateCommand();
        command.CommandText = "SELECT tag FROM memory_entry_tags WHERE entry_id=$id ORDER BY tag;";
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> result = new(); while (reader.Read()) result.Add(reader.GetString(0)); return result.ToArray();
    }

    private WorldPosition? ReadMemoryPosition(long id)
    {
        using SqliteCommand command = _connection!.CreateCommand();
        command.CommandText = "SELECT x,y,z FROM memory_entry_positions WHERE entry_id=$id;";
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? new WorldPosition(reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2)) : null;
    }

    private void RequireActiveMission()
    {
        if (!EnsureReady() || _activeMissionHash.Length == 0) throw new InvalidOperationException("No active Arma mission is available.");
    }
    private void RequireActiveSession()
    {
        if (!EnsureReady() || _activeSessionHash.Length == 0) throw new InvalidOperationException("No active Arma session is available.");
    }
    private static string RequireBoundedText(string value, int maximum, string name)
    {
        string result = value?.Trim() ?? string.Empty;
        if (result.Length is 0 || result.Length > maximum) throw new InvalidOperationException($"{name} must contain 1 to {maximum} characters.");
        return result;
    }
    private static string NormalizeProvenance(string value) => value.Trim().ToLowerInvariant() switch
    {
        "game-observed" => "game-observed", "derived" => "derived", "lore" => "lore", _ => "user-reported"
    };
    private static bool IsPrivacySafeCallsign(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 80 && !value.Any(char.IsControl);
    private static string NormalizeLoreScope(string value) => value.Trim().ToLowerInvariant() switch
    {
        "mission" => "Mission", "map" => "Map", "player" => "Player", "target" => "Target", "common" => "Common",
        _ => throw new InvalidOperationException("Unknown lore scope.")
    };
    private string LoreScopeKey(string scope) => scope switch
    {
        "Common" => "common", "Player" => "player", "Map" => "map:" + WorldKey(_worldName, _worldSize), _ => _activeMissionHash
    };
    private static string FtsQuery(string value)
    {
        string[] terms = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => new string(x.Where(char.IsLetterOrDigit).ToArray())).Where(x => x.Length > 0).Take(12).ToArray();
        return terms.Length == 0 ? "\"\"" : string.Join(" OR ", terms.Select(x => $"\"{x}\""));
    }
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

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
