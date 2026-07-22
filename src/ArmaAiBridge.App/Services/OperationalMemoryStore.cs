using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArmaAiBridge.App.Models;
using Microsoft.Data.Sqlite;

namespace ArmaAiBridge.App.Services;

public sealed class OperationalMemoryStore : IPlayerReportIngestor, IDisposable
{
    public const int SchemaVersion = 1;
    private const double ReportFusionWindowSeconds = 120;
    private readonly object _gate = new();
    private readonly WorldStateStore _worldState;
    private readonly TimeProvider _timeProvider;
    private readonly string _rootDirectory;
    private readonly Dictionary<string, EntityEntry> _entitiesByAlias = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EntityEntry> _entitiesByIdentityHash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ObservationEntry> _observationsByAlias = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observationHashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sourceAliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _aliasRedirects = new(StringComparer.Ordinal);
    private readonly List<NamedLocationState> _locations = new();
    private SqliteConnection? _connection;
    private long _version;
    private int _nextEntityAlias = 1;
    private int _nextObservationAlias = 1;
    private int _nextSourceAlias = 1;
    private string _missionHash = string.Empty;
    private string _sessionHash = string.Empty;
    private string _sessionAlias = string.Empty;
    private string _worldName = string.Empty;
    private string _databasePath = string.Empty;
    private string _gazetteerFingerprint = string.Empty;
    private string _lastBatchAlias = string.Empty;
    private string _diagnosticCode = string.Empty;
    private OperationalMemoryReadiness _readiness = OperationalMemoryReadiness.Unavailable;
    private GazetteerReadiness _gazetteerReadiness = GazetteerReadiness.Unavailable;
    private bool _disposed;

    public OperationalMemoryStore(
        WorldStateStore worldState,
        TimeProvider? timeProvider = null,
        string? rootDirectory = null)
    {
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rootDirectory = rootDirectory ?? AppPaths.OperationalMemoryDirectory;
    }

    public event Action? StateChanged;

    internal void ActivateSession(SessionHandshakeObservation handshake)
    {
        lock (_gate)
        {
            string missionHash = Hash("mission\0" + handshake.Envelope.MissionId);
            string sessionHash = Hash("session\0" + handshake.Envelope.MissionId + "\0" + handshake.Envelope.SessionId);
            if (_sessionHash == sessionHash && _readiness == OperationalMemoryReadiness.Ready)
                return;

            ResetInMemory();
            _missionHash = missionHash;
            _sessionHash = sessionHash;
            _sessionAlias = "memory-session-" + sessionHash[..8].ToLowerInvariant();
            _worldName = handshake.WorldName;
            _databasePath = Path.Combine(_rootDirectory, $"operational-{missionHash[..16].ToLowerInvariant()}.sqlite3");
            try
            {
                Directory.CreateDirectory(_rootDirectory);
                _connection?.Dispose();
                _connection = Open(_databasePath);
                MigrateOperational(_connection);
                PersistSession(handshake.Envelope.ReceivedAtUtc);
                LoadSession();
                LoadCachedGazetteer(handshake.WorldName, handshake.WorldSizeMeters);
                _readiness = OperationalMemoryReadiness.Ready;
                _diagnosticCode = string.Empty;
            }
            catch (Exception exception) when (exception is SqliteException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _connection?.Dispose();
                _connection = null;
                _readiness = OperationalMemoryReadiness.Failed;
                _diagnosticCode = "operational_database_open_failed";
            }
            _version++;
        }
        StateChanged?.Invoke();
    }

    internal void BeginGazetteerReceive()
    {
        lock (_gate)
        {
            _gazetteerReadiness = GazetteerReadiness.Receiving;
            _version++;
        }
        StateChanged?.Invoke();
    }

    internal void MarkGazetteerFailed(string diagnosticCode)
    {
        lock (_gate)
        {
            _gazetteerReadiness = GazetteerReadiness.Failed;
            _diagnosticCode = diagnosticCode;
            _version++;
        }
        StateChanged?.Invoke();
    }

    internal TelemetryIngestResult ApplyGazetteer(MapGazetteerObservation gazetteer)
    {
        lock (_gate)
        {
            if (!IsActive(gazetteer.Envelope)) return Rejected("operational_session_mismatch");
            try
            {
                string fingerprint = GazetteerFingerprint(gazetteer);
                string gazetteerPath = Path.Combine(_rootDirectory, "gazetteer.sqlite3");
                using SqliteConnection connection = Open(gazetteerPath);
                MigrateGazetteer(connection);
                using SqliteTransaction transaction = connection.BeginTransaction();
                string worldKey = WorldKey(gazetteer.WorldName, gazetteer.WorldSizeMeters);
                Execute(connection, transaction,
                    "DELETE FROM gazetteer_locations WHERE world_key = $world; DELETE FROM gazetteer_maps WHERE world_key = $world;",
                    ("$world", worldKey));
                Execute(connection, transaction, """
                    INSERT INTO gazetteer_maps(world_key, fingerprint, world_name, world_size, grid_json, updated_utc)
                    VALUES($world, $fingerprint, $name, $size, $grid, $updated);
                    """,
                    ("$world", worldKey), ("$fingerprint", fingerprint), ("$name", gazetteer.WorldName),
                    ("$size", gazetteer.WorldSizeMeters),
                    ("$grid", JsonSerializer.Serialize(gazetteer.GridSamples)),
                    ("$updated", gazetteer.Envelope.ReceivedAtUtc.ToString("O", CultureInfo.InvariantCulture)));

                int ordinal = 1;
                foreach (GazetteerLocationObservation location in gazetteer.Locations
                    .OrderBy(item => Normalize(item.OfficialName), StringComparer.Ordinal)
                    .ThenBy(item => item.LocationType, StringComparer.Ordinal)
                    .ThenBy(item => item.Position.X)
                    .ThenBy(item => item.Position.Y))
                {
                    string alias = $"location-{ordinal++:000}";
                    Execute(connection, transaction, """
                        INSERT INTO gazetteer_locations(
                            world_key, alias, config_key_hash, official_name, normalized_name, location_type,
                            x, y, size_x, size_y)
                        VALUES($world, $alias, $key, $name, $normalized, $type, $x, $y, $sx, $sy);
                        """,
                        ("$world", worldKey), ("$alias", alias), ("$key", Hash(location.ConfigKey)),
                        ("$name", location.OfficialName), ("$normalized", Normalize(location.OfficialName)),
                        ("$type", location.LocationType), ("$x", location.Position.X), ("$y", location.Position.Y),
                        ("$sx", location.SizeX), ("$sy", location.SizeY));
                }
                transaction.Commit();
                LoadCachedGazetteer(gazetteer.WorldName, gazetteer.WorldSizeMeters);
                _gazetteerFingerprint = fingerprint;
                _gazetteerReadiness = GazetteerReadiness.Ready;
                _diagnosticCode = string.Empty;
                _version++;
            }
            catch (Exception exception) when (exception is SqliteException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _gazetteerReadiness = GazetteerReadiness.Failed;
                _diagnosticCode = "gazetteer_commit_failed";
                _version++;
                return Rejected(_diagnosticCode);
            }
        }
        StateChanged?.Invoke();
        return new TelemetryIngestResult(TelemetryIngestStatus.Applied);
    }

    internal TelemetryIngestResult ApplyObservationBatch(OperationalObservationBatch batch)
    {
        lock (_gate)
        {
            if (!IsActive(batch.Envelope)) return Rejected("operational_session_mismatch");
            if (_connection is null || _readiness != OperationalMemoryReadiness.Ready)
                return Rejected("operational_memory_unavailable");

            try
            {
                using SqliteTransaction transaction = _connection.BeginTransaction();
                int applied = 0;
                foreach (OperationalObservation observation in batch.Observations)
                {
                    if (observation.RetractsObservationId.Length > 0)
                    {
                        string retractHash = Hash(_sessionHash + "\0source-observation\0" + observation.RetractsObservationId);
                        ObservationEntry? prior = _observationsByAlias.Values.FirstOrDefault(item => item.SourceHash == retractHash);
                        if (prior is not null && prior.RetractedAtUtc is null)
                        {
                            prior.RetractedAtUtc = batch.Envelope.ReceivedAtUtc;
                            PersistRetraction(prior, transaction);
                            Recompute(prior.Entity);
                            PersistLinks(prior.Entity, transaction);
                            PersistEntity(prior.Entity, transaction);
                            applied++;
                        }
                        continue;
                    }

                    string sourceObservationHash = Hash(_sessionHash + "\0source-observation\0" + observation.SourceObservationId);
                    if (!_observationHashes.Add(sourceObservationHash)) continue;
                    string identityHash = Hash(_sessionHash + "\0target\0" + observation.TargetEntityId);
                    string sourceHash = Hash(_sessionHash + "\0source\0" + observation.SourceEntityId);
                    EntityEntry entity = GetOrCreateEngineEntity(identityHash, observation, transaction);
                    string sourceAlias = GetSourceAlias(sourceHash, transaction);
                    ObservationEntry entry = new(
                        $"observation-{_nextObservationAlias++:000000}", sourceObservationHash, entity, sourceAlias,
                        observation.Provenance, observation.ObservedAtGameTime, batch.Envelope.ReceivedAtUtc,
                        observation.Position, observation.PositionErrorMeters, BaseConfidence(observation.Provenance),
                        observation.Classification, observation.PerceivedSide, observation.State,
                        SafeSummary(observation.EntityKind, observation.Classification, observation.Provenance),
                        constraintConflict: false, constraintLocationAlias: string.Empty,
                        constraintPosition: null, constraintRadiusMeters: null, supersedes: string.Empty);
                    entity.Observations.Add(entry);
                    _observationsByAlias.Add(entry.Alias, entry);
                    PersistObservation(entry, transaction);
                    Recompute(entity);
                    PersistLinks(entity, transaction);
                    PersistEntity(entity, transaction);
                    applied++;
                }
                transaction.Commit();
                _lastBatchAlias = "batch-" + Hash(batch.BatchId)[..8].ToLowerInvariant();
                _diagnosticCode = string.Empty;
                _version++;
                if (applied == 0)
                    return new TelemetryIngestResult(TelemetryIngestStatus.OutOfOrder, DiagnosticCode: "duplicate_observation_batch");
            }
            catch (SqliteException)
            {
                _readiness = OperationalMemoryReadiness.Failed;
                _diagnosticCode = "operational_observation_commit_failed";
                _version++;
                return Rejected(_diagnosticCode);
            }
        }
        StateChanged?.Invoke();
        return new TelemetryIngestResult(TelemetryIngestStatus.Applied);
    }

    public OperationalMemoryView GetCurrentView()
    {
        lock (_gate)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            OperationalEntityState[] entities = _entitiesByAlias.Values
                .OrderBy(item => item.Alias, StringComparer.Ordinal)
                .Select(item => BuildEntity(item, now))
                .ToArray();
            OperationalObservationState[] observations = _observationsByAlias.Values
                .OrderByDescending(item => item.ReceivedAtUtc)
                .ThenBy(item => item.Alias, StringComparer.Ordinal)
                .Select(BuildObservation)
                .ToArray();
            return new OperationalMemoryView(
                _version, _readiness, _gazetteerReadiness, _sessionAlias, _worldName, SchemaVersion,
                RedactedPath(_databasePath), FingerprintAlias(_gazetteerFingerprint), _locations.Count,
                _locations.ToArray(), entities, observations, _lastBatchAlias, _diagnosticCode);
        }
    }

    public string FindNamedLocations(JsonElement arguments)
    {
        lock (_gate)
        {
            try
            {
                EnsureOnlyProperties(arguments, "query", "maxDistanceMeters", "limit");
                string query = ReadRequiredStringAllowEmpty(arguments, "query", 160);
                double maxDistance = ReadRequiredNumber(arguments, "maxDistanceMeters", 100, 50000);
                int limit = ReadRequiredInteger(arguments, "limit", 1, 50);
                WorldPosition origin = RequirePlayerPosition();
                string normalized = Normalize(query);
                object[] results = _locations
                .Select(location => new { Location = location, Distance = Distance(origin, location.Position) })
                .Where(item => item.Distance <= maxDistance &&
                    (normalized.Length == 0 || Normalize(item.Location.OfficialName).Contains(normalized, StringComparison.Ordinal)))
                .OrderBy(item => item.Distance)
                .ThenBy(item => item.Location.OfficialName, StringComparer.Ordinal)
                .Take(limit)
                .Select(item => (object)new Dictionary<string, object?>
                {
                    ["alias"] = item.Location.Alias,
                    ["officialName"] = item.Location.OfficialName,
                    ["locationType"] = item.Location.LocationType,
                    ["position"] = Position(item.Location.Position),
                    ["sizeMeters"] = item.Location.SizeX is null ? null : new[] { Round(item.Location.SizeX.Value), Round(item.Location.SizeY ?? 0) },
                    ["distanceMeters"] = Round(item.Distance),
                    ["bearingDegrees"] = Round(Bearing(origin, item.Location.Position))
                }).ToArray();
                return Serialize(new Dictionary<string, object?>
                {
                    ["schema"] = "arma-ai-bridge/local/named-locations-v1",
                    ["readiness"] = EnumText(_gazetteerReadiness),
                    ["fingerprintAlias"] = FingerprintAlias(_gazetteerFingerprint),
                    ["locations"] = results
                });
            }
            catch (ToolValidationException exception) { return ToolError(exception.Code); }
        }
    }

    public string QueryOperationalMemory(JsonElement arguments)
    {
        lock (_gate)
        {
            try
            {
                EnsureOnlyProperties(arguments, "entityKind", "maxDistanceMeters", "freshness", "includeConflicts", "limit");
                if (_readiness != OperationalMemoryReadiness.Ready)
                    throw new ToolValidationException("operational_memory_unavailable");
                string kind = ReadEnum(arguments, "entityKind", "any", "contact", "vehicle", "supply", "weapon", "fortification", "static", "other");
                double maxDistance = ReadRequiredNumber(arguments, "maxDistanceMeters", 100, 50000);
                string freshness = ReadEnum(arguments, "freshness", "live", "recent", "stale", "historical", "any");
                bool includeConflicts = ReadRequiredBoolean(arguments, "includeConflicts");
                int limit = ReadRequiredInteger(arguments, "limit", 1, 100);
                WorldPosition origin = RequirePlayerPosition();
                DateTimeOffset now = _timeProvider.GetUtcNow();
                object[] results = _entitiesByAlias.Values
                .Select(entity => new { State = BuildEntity(entity, now), Distance = entity.Position is null ? 0 : Distance(origin, entity.Position) })
                .Where(item => (kind == "any" || EnumText(item.State.Kind) == kind) &&
                    (item.State.Position is null || item.Distance <= maxDistance) &&
                    (freshness == "any" || EnumText(item.State.Freshness) == freshness) &&
                    (includeConflicts || item.State.ConflictCount == 0))
                .OrderBy(item => item.State.Freshness)
                .ThenBy(item => item.Distance)
                .ThenBy(item => item.State.Alias, StringComparer.Ordinal)
                .Take(limit)
                .Select(item => (object)new Dictionary<string, object?>
                {
                    ["alias"] = item.State.Alias,
                    ["kind"] = EnumText(item.State.Kind),
                    ["classification"] = item.State.Classification,
                    ["label"] = item.State.DisplayLabel,
                    ["perceivedSide"] = item.State.PerceivedSide,
                    ["position"] = Position(item.State.Position),
                    ["positionStatus"] = item.State.IsLastKnown ? "last-known" : "observed",
                    ["uncertaintyMeters"] = Round(item.State.PositionErrorMeters),
                    ["distanceMeters"] = item.State.Position is null ? null : Round(item.Distance),
                    ["state"] = item.State.State,
                    ["freshness"] = EnumText(item.State.Freshness),
                    ["confidence"] = Round(item.State.Confidence),
                    ["provenance"] = item.State.IsRetracted ? Array.Empty<string>() :
                        _entitiesByAlias[item.State.Alias].Observations.Where(o => o.RetractedAtUtc is null)
                            .Select(o => EnumText(o.Provenance)).Distinct(StringComparer.Ordinal).Order().ToArray(),
                    ["corroborationCount"] = item.State.CorroborationCount,
                    ["conflictCount"] = item.State.ConflictCount,
                    ["observations"] = item.State.IsRetracted
                        ? Array.Empty<object>()
                        : item.State.Alias.Length == 0
                            ? Array.Empty<object>()
                            : ObservationEvidence(_entitiesByAlias[item.State.Alias], now)
                }).ToArray();
                return Serialize(new Dictionary<string, object?>
                {
                    ["schema"] = "arma-ai-bridge/local/operational-memory-v1",
                    ["session"] = _sessionAlias,
                    ["entities"] = results
                });
            }
            catch (ToolValidationException exception) { return ToolError(exception.Code); }
        }
    }

    public string RecordPlayerObservation(JsonElement arguments, string currentUserTurn)
    {
        lock (_gate)
        {
            try
            {
                EnsureOnlyProperties(arguments, "sourceQuote", "timeReference", "entityKind", "classification", "state",
                    "rangeMeters", "bearingDegrees", "bearingReference", "rangePrecisionMeters",
                    "bearingPrecisionDegrees", "ageSeconds", "namedLocation");
                ReportInput input = ParseReport(arguments, currentUserTurn, correction: false);
                if (_connection is null || _readiness != OperationalMemoryReadiness.Ready)
                    throw new ToolValidationException("operational_memory_unavailable");
                return RecordPlayerObservation(input, supersedes: string.Empty);
            }
            catch (ToolValidationException exception)
            {
                return ToolError(exception.Code);
            }
        }
    }

    public string CorrectPlayerObservation(JsonElement arguments, string currentUserTurn)
    {
        lock (_gate)
        {
            try
            {
                EnsureOnlyProperties(arguments, "sourceQuote", "action", "observationAlias", "timeReference", "entityKind",
                    "classification", "state", "rangeMeters", "bearingDegrees", "bearingReference",
                    "rangePrecisionMeters", "bearingPrecisionDegrees", "ageSeconds", "namedLocation");
                string observationAlias = ReadRequiredString(arguments, "observationAlias", 64);
                string sourceQuote = ReadRequiredString(arguments, "sourceQuote", 500);
                string action = ReadEnum(arguments, "action", "correct", "retract");
                ValidateExactQuote(sourceQuote, currentUserTurn);
                if (!ContainsCorrectionLanguage(sourceQuote, action))
                    throw new ToolValidationException("explicit_correction_required");
                if (!_observationsByAlias.TryGetValue(observationAlias, out ObservationEntry? prior) ||
                    prior.Provenance != OperationalProvenance.PlayerReport || prior.RetractedAtUtc is not null)
                    throw new ToolValidationException("player_observation_not_found");
                if (_connection is null) throw new ToolValidationException("operational_memory_unavailable");

                if (action == "retract")
                {
                    ValidateRetractionPayload(arguments);
                    using SqliteTransaction transaction = _connection.BeginTransaction();
                    prior.RetractedAtUtc = _timeProvider.GetUtcNow();
                    PersistRetraction(prior, transaction);
                    Recompute(prior.Entity);
                    PersistLinks(prior.Entity, transaction);
                    PersistEntity(prior.Entity, transaction);
                    transaction.Commit();
                    _version++;
                    StateChanged?.Invoke();
                    return Serialize(new Dictionary<string, object?>
                    {
                        ["schema"] = "arma-ai-bridge/local/player-observation-result-v1",
                        ["status"] = "retracted",
                        ["observationAlias"] = prior.Alias,
                        ["entityAlias"] = prior.Entity.Alias
                    });
                }

                ReportInput replacement = ParseReport(arguments, currentUserTurn, correction: true);
                if (replacement.Kind != prior.Entity.Kind)
                    throw new ToolValidationException("correction_entity_kind_mismatch");

                ReportGeometry geometry = ResolveReportGeometry(replacement);
                DateTimeOffset receivedAt = _timeProvider.GetUtcNow();
                using SqliteTransaction correctionTransaction = _connection.BeginTransaction();
                prior.RetractedAtUtc = receivedAt;
                PersistRetraction(prior, correctionTransaction);

                string sourceHash = Hash(_sessionHash + "\0source\0player:self");
                string sourceAlias = GetSourceAlias(sourceHash, correctionTransaction);
                string observationHash = Hash(_sessionHash + "\0player-observation\0" + Guid.NewGuid().ToString("N"));
                ObservationEntry replacementEntry = new(
                    $"observation-{_nextObservationAlias++:000000}", observationHash, prior.Entity, sourceAlias,
                    OperationalProvenance.PlayerReport, geometry.ObservedAtGameTime, receivedAt, geometry.Position,
                    geometry.UncertaintyMeters, BaseConfidence(OperationalProvenance.PlayerReport), replacement.Classification,
                    "UNKNOWN", replacement.State, SafeSummary(replacement.Kind, replacement.Classification,
                        OperationalProvenance.PlayerReport), geometry.ConstraintConflict, geometry.ConstraintLocationAlias,
                    geometry.ConstraintPosition, geometry.ConstraintRadiusMeters, prior.Alias);
                prior.Entity.Observations.Add(replacementEntry);
                _observationsByAlias.Add(replacementEntry.Alias, replacementEntry);
                _observationHashes.Add(observationHash);
                PersistObservation(replacementEntry, correctionTransaction);
                Recompute(prior.Entity);
                PersistLinks(prior.Entity, correctionTransaction);
                PersistEntity(prior.Entity, correctionTransaction);
                correctionTransaction.Commit();
                _version++;
                StateChanged?.Invoke();
                return Serialize(new Dictionary<string, object?>
                {
                    ["schema"] = "arma-ai-bridge/local/player-observation-result-v1",
                    ["status"] = "corrected",
                    ["observationAlias"] = replacementEntry.Alias,
                    ["entityAlias"] = prior.Entity.Alias,
                    ["position"] = Position(geometry.Position),
                    ["uncertaintyMeters"] = Round(geometry.UncertaintyMeters),
                    ["confidence"] = BaseConfidence(OperationalProvenance.PlayerReport),
                    ["constraintConflict"] = geometry.ConstraintConflict,
                    ["namedLocationConstraintAlias"] = geometry.ConstraintLocationAlias.Length == 0
                        ? null : geometry.ConstraintLocationAlias
                });
            }
            catch (ToolValidationException exception)
            {
                return ToolError(exception.Code);
            }
        }
    }

    private string RecordPlayerObservation(ReportInput input, string supersedes)
    {
        ReportGeometry geometry = ResolveReportGeometry(input);
        string sourceHash = Hash(_sessionHash + "\0source\0player:self");
        string identityHash = Hash(_sessionHash + "\0player-report\0" + Guid.NewGuid().ToString("N"));
        using SqliteTransaction transaction = _connection!.BeginTransaction();
        string sourceAlias = GetSourceAlias(sourceHash, transaction);
        EntityEntry entity = CreateEntity(identityHash, input.Kind, OperationalIdentityQuality.FusedReport,
            input.Classification, input.Classification, "UNKNOWN", transaction);
        string observationHash = Hash(_sessionHash + "\0player-observation\0" + Guid.NewGuid().ToString("N"));
        ObservationEntry entry = new(
            $"observation-{_nextObservationAlias++:000000}", observationHash, entity, sourceAlias,
            OperationalProvenance.PlayerReport, geometry.ObservedAtGameTime, _timeProvider.GetUtcNow(), geometry.Position,
            geometry.UncertaintyMeters, BaseConfidence(OperationalProvenance.PlayerReport), input.Classification, "UNKNOWN", input.State,
            SafeSummary(input.Kind, input.Classification, OperationalProvenance.PlayerReport),
            geometry.ConstraintConflict, geometry.ConstraintLocationAlias, geometry.ConstraintPosition,
            geometry.ConstraintRadiusMeters, supersedes);
        entity.Observations.Add(entry);
        _observationsByAlias.Add(entry.Alias, entry);
        _observationHashes.Add(observationHash);
        PersistObservation(entry, transaction);
        Recompute(entity);
        PersistLinks(entity, transaction);
        PersistEntity(entity, transaction);
        transaction.Commit();
        _version++;
        StateChanged?.Invoke();
        return Serialize(new Dictionary<string, object?>
        {
            ["schema"] = "arma-ai-bridge/local/player-observation-result-v1",
            ["status"] = supersedes.Length == 0 ? "recorded" : "corrected",
            ["observationAlias"] = entry.Alias,
            ["entityAlias"] = entity.Alias,
            ["position"] = Position(geometry.Position),
            ["uncertaintyMeters"] = Round(geometry.UncertaintyMeters),
            ["confidence"] = BaseConfidence(OperationalProvenance.PlayerReport),
            ["constraintConflict"] = geometry.ConstraintConflict,
            ["namedLocationConstraintAlias"] = geometry.ConstraintLocationAlias.Length == 0
                ? null : geometry.ConstraintLocationAlias
        });
    }

    private ReportGeometry ResolveReportGeometry(ReportInput input)
    {
        WorldStateView world = _worldState.GetCurrentView();
        WorldPosition origin = world.Player?.Metadata.Position ?? throw new ToolValidationException("player_position_unavailable");
        double observedAt = Math.Max(0, world.LastObservedAtGameTime - (input.AgeSeconds ?? 0));
        WorldPosition? position = null;
        double? uncertainty = null;
        bool constraintConflict = false;
        NamedLocationState? named = input.NamedLocation.Length == 0 ? null :
            _locations.FirstOrDefault(item => string.Equals(item.OfficialName, input.NamedLocation, StringComparison.OrdinalIgnoreCase));
        double? constraintRadius = named is null ? null : Math.Max(25, Math.Max(named.SizeX ?? 0, named.SizeY ?? 0));

        if (input.RangeMeters is not null && input.BearingDegrees is not null)
        {
            double bearing = input.BearingReference switch
            {
                "view" => world.Player!.ViewHeading + input.BearingDegrees.Value,
                "body" => world.Player!.BodyHeading + input.BearingDegrees.Value,
                _ => input.BearingDegrees.Value
            };
            double radians = NormalizeBearing(bearing) * Math.PI / 180;
            position = new WorldPosition(
                origin.X + Math.Sin(radians) * input.RangeMeters.Value,
                origin.Y + Math.Cos(radians) * input.RangeMeters.Value,
                origin.Z);
            uncertainty = Math.Max(10,
                Math.Max(input.RangePrecisionMeters / 2,
                    input.RangeMeters.Value * Math.Sin(input.BearingPrecisionDegrees * Math.PI / 180)));
            uncertainty += world.Player.Metadata.PositionErrorMeters ?? 0;
        }
        else if (named is not null)
        {
            position = named.Position;
            uncertainty = Math.Max(25, Math.Max(named.SizeX ?? 0, named.SizeY ?? 0));
        }

        if (named is not null && position is not null && uncertainty is not null)
        {
            constraintConflict = Distance(position, named.Position) > uncertainty.Value + constraintRadius!.Value;
        }
        return new ReportGeometry(observedAt, position, uncertainty, constraintConflict,
            named?.Alias ?? string.Empty, named?.Position, constraintRadius);
    }

    private EntityEntry GetOrCreateEngineEntity(
        string identityHash,
        OperationalObservation observation,
        SqliteTransaction transaction)
    {
        if (_entitiesByIdentityHash.TryGetValue(identityHash, out EntityEntry? existing))
        {
            TryMergeCompatibleReport(existing, observation, transaction);
            return existing;
        }
        EntityEntry entity = CreateEntity(identityHash, observation.EntityKind,
            observation.Provenance == OperationalProvenance.MissionReport
                ? OperationalIdentityQuality.FusedReport
                : observation.TargetEntityId.StartsWith("fallback:", StringComparison.Ordinal)
                ? OperationalIdentityQuality.BestEffort : OperationalIdentityQuality.StableMission,
            observation.Classification, observation.DisplayName, observation.PerceivedSide, transaction);

        TryMergeCompatibleReport(entity, observation, transaction);
        return entity;
    }

    private void TryMergeCompatibleReport(
        EntityEntry engine,
        OperationalObservation observation,
        SqliteTransaction transaction)
    {
        if (engine.IdentityQuality == OperationalIdentityQuality.FusedReport) return;
        EntityEntry? report = _entitiesByAlias.Values
            .Where(item => !ReferenceEquals(item, engine) && item.IdentityQuality == OperationalIdentityQuality.FusedReport &&
                item.Kind == observation.EntityKind && item.Position is not null && observation.Position is not null &&
                CompatibleClassification(item.Classification, observation.Classification) &&
                Math.Abs(item.LastObservedAtGameTime - observation.ObservedAtGameTime) <= ReportFusionWindowSeconds &&
                Distance(item.Position, observation.Position) <= (item.PositionErrorMeters ?? 0) +
                    (observation.PositionErrorMeters ?? 0) + 25)
            .OrderBy(item => Distance(item.Position!, observation.Position!))
            .FirstOrDefault();
        if (report is not null) MergeReportIntoEngine(report, engine, transaction);
    }

    private EntityEntry CreateEntity(
        string identityHash,
        OperationalEntityKind kind,
        OperationalIdentityQuality quality,
        string classification,
        string displayLabel,
        string perceivedSide,
        SqliteTransaction transaction)
    {
        string alias = $"{EnumText(kind)}-{_nextEntityAlias++:000}";
        EntityEntry entity = new(alias, kind, quality, classification, displayLabel, perceivedSide);
        _entitiesByAlias.Add(alias, entity);
        _entitiesByIdentityHash.Add(identityHash, entity);
        Execute(_connection!, transaction,
            "INSERT INTO entity_identities(session_hash, identity_hash, entity_alias) VALUES($session, $identity, $alias);",
            ("$session", _sessionHash), ("$identity", identityHash), ("$alias", alias));
        return entity;
    }

    private void MergeReportIntoEngine(EntityEntry report, EntityEntry engine, SqliteTransaction transaction)
    {
        foreach (ObservationEntry observation in report.Observations.ToArray())
        {
            observation.Entity = engine;
            engine.Observations.Add(observation);
            Execute(_connection!, transaction,
                "UPDATE observations SET entity_alias = $target WHERE session_hash = $session AND observation_alias = $observation;",
                ("$target", engine.Alias), ("$session", _sessionHash), ("$observation", observation.Alias));
        }
        foreach ((string identity, EntityEntry value) in _entitiesByIdentityHash.Where(pair => ReferenceEquals(pair.Value, report)).ToArray())
        {
            _entitiesByIdentityHash[identity] = engine;
            Execute(_connection!, transaction,
                "UPDATE entity_identities SET entity_alias = $target WHERE session_hash = $session AND identity_hash = $identity;",
                ("$target", engine.Alias), ("$session", _sessionHash), ("$identity", identity));
        }
        _entitiesByAlias.Remove(report.Alias);
        _aliasRedirects[report.Alias] = engine.Alias;
        Execute(_connection!, transaction,
            "INSERT OR REPLACE INTO alias_redirects(session_hash, old_alias, new_alias) VALUES($session, $old, $new); DELETE FROM entities WHERE session_hash = $session AND entity_alias = $old;",
            ("$session", _sessionHash), ("$old", report.Alias), ("$new", engine.Alias));
        Recompute(engine);
    }

    private void Recompute(EntityEntry entity)
    {
        foreach (ObservationEntry item in entity.Observations)
        {
            item.Corroborates.Clear();
            item.Contradicts.Clear();
        }
        ObservationEntry[] active = entity.Observations.Where(item => item.RetractedAtUtc is null).ToArray();
        for (int i = 0; i < active.Length; i++)
        {
            for (int j = i + 1; j < active.Length; j++)
            {
                ObservationEntry first = active[i], second = active[j];
                if (first.SourceAlias == second.SourceAlias ||
                    Math.Abs(first.ObservedAtGameTime - second.ObservedAtGameTime) > ReportFusionWindowSeconds)
                    continue;
                if (Compatible(first, second))
                {
                    first.Corroborates.Add(second.Alias);
                    second.Corroborates.Add(first.Alias);
                }
                else
                {
                    first.Contradicts.Add(second.Alias);
                    second.Contradicts.Add(first.Alias);
                }
            }
        }
        if (active.Length == 0)
        {
            entity.IsRetracted = true;
            return;
        }

        entity.IsRetracted = false;
        ObservationEntry newest = active.OrderByDescending(item => item.ReceivedAtUtc).First();
        entity.Classification = newest.Classification;
        entity.DisplayLabel = newest.Classification.Length == 0 ? entity.Alias : newest.Classification;
        entity.PerceivedSide = newest.PerceivedSide;
        entity.State = newest.State;
        entity.FirstObservedAtGameTime = active.Min(item => item.ObservedAtGameTime);
        entity.LastObservedAtGameTime = active.Max(item => item.ObservedAtGameTime);
        entity.LastReceivedAtUtc = active.Max(item => item.ReceivedAtUtc);
        entity.ConflictCount = active.Sum(item => item.Contradicts.Count) / 2 + active.Count(item => item.ConstraintConflict);
        entity.CorroborationCount = active.SelectMany(item => item.Corroborates).Distinct(StringComparer.Ordinal).Count() / 2;

        ObservationEntry[] positioned = active.Where(item => item.Position is not null).ToArray();
        if (positioned.Length > 0)
        {
            if (entity.ConflictCount == 0)
            {
                double weight = positioned.Sum(item => 1 / Math.Max(1, item.PositionErrorMeters ?? 50));
                entity.Position = new WorldPosition(
                    positioned.Sum(item => item.Position!.X / Math.Max(1, item.PositionErrorMeters ?? 50)) / weight,
                    positioned.Sum(item => item.Position!.Y / Math.Max(1, item.PositionErrorMeters ?? 50)) / weight,
                    positioned.Sum(item => item.Position!.Z / Math.Max(1, item.PositionErrorMeters ?? 50)) / weight);
                entity.PositionErrorMeters = positioned.Min(item => item.PositionErrorMeters ?? 50);
            }
            else
            {
                ObservationEntry latestPosition = positioned.OrderByDescending(item => item.ReceivedAtUtc).First();
                entity.Position = latestPosition.Position;
                entity.PositionErrorMeters = latestPosition.PositionErrorMeters;
            }
        }
    }

    private static bool Compatible(ObservationEntry first, ObservationEntry second)
    {
        if (!CompatibleClassification(first.Classification, second.Classification)) return false;
        if (first.PerceivedSide != "UNKNOWN" && second.PerceivedSide != "UNKNOWN" && first.PerceivedSide != second.PerceivedSide) return false;
        if (first.State != "unknown" && second.State != "unknown" && first.State != second.State) return false;
        if (first.Position is null || second.Position is null) return true;
        return Distance(first.Position, second.Position) <=
            (first.PositionErrorMeters ?? 0) + (second.PositionErrorMeters ?? 0) + 25;
    }

    private static bool CompatibleClassification(string first, string second)
        => first.Length == 0 || second.Length == 0 ||
           Normalize(first) == Normalize(second) || Normalize(first).Contains(Normalize(second), StringComparison.Ordinal) ||
           Normalize(second).Contains(Normalize(first), StringComparison.Ordinal);

    private OperationalEntityState BuildEntity(EntityEntry entity, DateTimeOffset now)
    {
        ObservationEntry? newest = entity.Observations.Where(item => item.RetractedAtUtc is null)
            .OrderByDescending(item => item.ReceivedAtUtc).FirstOrDefault();
        double age = newest is null ? double.MaxValue : Math.Max(0, (now - newest.ReceivedAtUtc).TotalSeconds);
        WorldFreshness freshness = Freshness(newest?.Provenance, age);
        double baseConfidence = entity.Observations.Where(item => item.RetractedAtUtc is null)
            .Select(item => item.BaseConfidence).DefaultIfEmpty(0).Max();
        for (int i = 0; i < entity.CorroborationCount; i++) baseConfidence += 0.10 * (1 - baseConfidence);
        for (int i = 0; i < entity.ConflictCount; i++) baseConfidence *= 0.75;
        double confidence = Math.Clamp(baseConfidence * FreshnessMultiplier(freshness), entity.IsRetracted ? 0 : 0.1, 0.98);
        return new OperationalEntityState(
            entity.Alias, entity.Kind, entity.IdentityQuality, entity.Classification,
            entity.DisplayLabel.Length == 0 ? entity.Alias : entity.DisplayLabel, entity.PerceivedSide,
            entity.FirstObservedAtGameTime, entity.LastObservedAtGameTime, entity.LastReceivedAtUtc,
            entity.Position, entity.PositionErrorMeters, freshness, entity.IsRetracted ? 0 : confidence,
            entity.State, entity.ConflictCount, entity.CorroborationCount,
            freshness is WorldFreshness.Stale or WorldFreshness.Historical, entity.IsRetracted);
    }

    private static OperationalObservationState BuildObservation(ObservationEntry observation) => new(
        observation.Alias, observation.Entity.Alias, observation.SourceAlias, observation.Provenance,
        observation.ObservedAtGameTime, observation.ReceivedAtUtc, observation.Position,
        observation.PositionErrorMeters, observation.BaseConfidence, observation.Classification,
        observation.PerceivedSide, observation.State, observation.Summary,
        observation.Corroborates.Order(StringComparer.Ordinal).ToArray(),
        observation.Contradicts.Order(StringComparer.Ordinal).ToArray(), observation.ConstraintConflict,
        observation.ConstraintLocationAlias, observation.ConstraintPosition, observation.ConstraintRadiusMeters,
        observation.Supersedes, observation.RetractedAtUtc);

    private static object[] ObservationEvidence(EntityEntry entity, DateTimeOffset now)
        => entity.Observations
            .OrderByDescending(item => item.ReceivedAtUtc)
            .ThenBy(item => item.Alias, StringComparer.Ordinal)
            .Take(6)
            .Select(item => (object)new Dictionary<string, object?>
            {
                ["alias"] = item.Alias,
                ["sourceAlias"] = item.SourceAlias,
                ["provenance"] = EnumText(item.Provenance),
                ["observedAtGameTime"] = Round(item.ObservedAtGameTime),
                ["freshness"] = EnumText(Freshness(item.Provenance,
                    Math.Max(0, (now - item.ReceivedAtUtc).TotalSeconds))),
                ["baseConfidence"] = Round(item.BaseConfidence),
                ["position"] = Position(item.Position),
                ["uncertaintyMeters"] = Round(item.PositionErrorMeters),
                ["classification"] = item.Classification,
                ["state"] = item.State,
                ["corroborates"] = item.Corroborates.Order(StringComparer.Ordinal).Take(6).ToArray(),
                ["contradicts"] = item.Contradicts.Order(StringComparer.Ordinal).Take(6).ToArray(),
                ["constraintConflict"] = item.ConstraintConflict,
                ["namedLocationConstraint"] = item.ConstraintLocationAlias.Length == 0 ? null : new Dictionary<string, object?>
                {
                    ["locationAlias"] = item.ConstraintLocationAlias,
                    ["position"] = Position(item.ConstraintPosition),
                    ["radiusMeters"] = Round(item.ConstraintRadiusMeters)
                },
                ["supersedes"] = item.Supersedes.Length == 0 ? null : item.Supersedes,
                ["active"] = item.RetractedAtUtc is null
            })
            .ToArray();

    private ReportInput ParseReport(JsonElement arguments, string currentUserTurn, bool correction)
    {
        string sourceQuote = ReadRequiredString(arguments, "sourceQuote", 500);
        ValidateExactQuote(sourceQuote, currentUserTurn);
        if (!correction && !IsExplicitReport(currentUserTurn)) throw new ToolValidationException("explicit_player_report_required");
        string timeReference = ReadEnum(arguments, "timeReference", "present", "past");
        OperationalEntityKind kind = ParseKind(ReadEnum(arguments, "entityKind", "contact", "vehicle", "supply", "weapon", "fortification", "static", "other"));
        string classification = ReadEnum(arguments, "classification", "unknown", "infantry", "vehicle", "car", "truck",
            "offroad", "tank", "apc", "aircraft", "helicopter", "boat", "crate", "ammo", "weapon",
            "fortification", "static");
        string state = ReadEnum(arguments, "state", "intact", "damaged", "destroyed", "changed", "unknown");
        double? range = ReadNullableNumber(arguments, "rangeMeters", 1, 50000);
        double? bearing = ReadNullableNumber(arguments, "bearingDegrees", -360, 360);
        string bearingReference = ReadEnum(arguments, "bearingReference", "absolute", "view", "body");
        double rangePrecision = ReadRequiredNumber(arguments, "rangePrecisionMeters", 1, 5000);
        double bearingPrecision = ReadRequiredNumber(arguments, "bearingPrecisionDegrees", 1, 90);
        double? age = ReadNullableNumber(arguments, "ageSeconds", 0, 86400);
        string namedLocation = ReadNullableString(arguments, "namedLocation", 160);
        if ((range is null) != (bearing is null)) throw new ToolValidationException("range_and_bearing_required_together");
        if (range is null && namedLocation.Length == 0) throw new ToolValidationException("report_position_required");
        if (classification != "unknown" && !Normalize(sourceQuote).Contains(classification, StringComparison.Ordinal))
            throw new ToolValidationException("classification_not_in_player_report");
        if (state != "unknown" && !ContainsState(sourceQuote, state))
            throw new ToolValidationException("state_not_in_player_report");
        if (range is not null && !ContainsRange(sourceQuote, range.Value)) throw new ToolValidationException("range_not_in_player_report");
        if (bearing is not null && !ContainsBearing(sourceQuote, bearing.Value, bearingReference))
            throw new ToolValidationException("bearing_not_in_player_report");
        if (timeReference == "past" && (age is null || !ContainsAge(sourceQuote, age.Value)))
            throw new ToolValidationException("past_age_not_in_player_report");
        if (timeReference == "present" && age is not null && age.Value > 5)
            throw new ToolValidationException("present_report_age_invalid");
        if (namedLocation.Length > 0)
        {
            NamedLocationState? named = _locations.FirstOrDefault(item =>
                string.Equals(item.OfficialName, namedLocation, StringComparison.OrdinalIgnoreCase));
            if (named is null || !sourceQuote.Contains(named.OfficialName, StringComparison.OrdinalIgnoreCase))
                throw new ToolValidationException("named_location_not_in_player_report");
            namedLocation = named.OfficialName;
        }
        return new ReportInput(sourceQuote, timeReference, kind, classification, state, range, bearing,
            bearingReference, rangePrecision, bearingPrecision, age, namedLocation);
    }

    private static void ValidateRetractionPayload(JsonElement arguments)
    {
        _ = ReadEnum(arguments, "timeReference", "present", "past");
        _ = ReadEnum(arguments, "entityKind", "contact", "vehicle", "supply", "weapon", "fortification", "static", "other");
        _ = ReadEnum(arguments, "classification", "unknown", "infantry", "vehicle", "car", "truck", "offroad", "tank",
            "apc", "aircraft", "helicopter", "boat", "crate", "ammo", "weapon", "fortification", "static");
        _ = ReadEnum(arguments, "state", "intact", "damaged", "destroyed", "changed", "unknown");
        double? range = ReadNullableNumber(arguments, "rangeMeters", 1, 50000);
        double? bearing = ReadNullableNumber(arguments, "bearingDegrees", -360, 360);
        _ = ReadEnum(arguments, "bearingReference", "absolute", "view", "body");
        _ = ReadRequiredNumber(arguments, "rangePrecisionMeters", 1, 5000);
        _ = ReadRequiredNumber(arguments, "bearingPrecisionDegrees", 1, 90);
        _ = ReadNullableNumber(arguments, "ageSeconds", 0, 86400);
        _ = ReadNullableString(arguments, "namedLocation", 160);
        if ((range is null) != (bearing is null)) throw new ToolValidationException("range_and_bearing_required_together");
    }

    private static void ValidateExactQuote(string quote, string turn)
    {
        if (turn.Length == 0 || !turn.Contains(quote, StringComparison.OrdinalIgnoreCase))
            throw new ToolValidationException("source_quote_not_in_current_turn");
        if (quote.Contains('?') || turn.Contains('?')) throw new ToolValidationException("questions_cannot_create_observations");
    }

    private static bool IsExplicitReport(string quote)
    {
        string value = " " + Normalize(quote) + " ";
        string[] rejected = { " if ", " could ", " would ", " might ", " maybe ", " perhaps ", " suppose ", " imagine ", " hypothetical ", " is there ", " are there ", " könnte ", " vielleicht ", " angenommen " };
        if (rejected.Any(value.Contains)) return false;
        string[] accepted = { " i see ", " i saw ", " i observed ", " i spotted ", " i found ", " ive seen ", " i have seen ", " we see ", " we saw ", " reporting ", " report ", " there is ", " there was ", " ich sehe ", " ich sah ", " ich habe gesehen ", " wir sehen ", " meldung " };
        return accepted.Any(value.Contains);
    }

    private static bool ContainsCorrectionLanguage(string quote, string action)
    {
        string value = Normalize(quote);
        string[] words = action == "retract"
            ? new[] { "retract", "withdraw", "ignore my report", "cancel my report", "zurückziehen", "widerrufe" }
            : new[] { "correction", "correct", "i meant", "korrigiere", "korrektur", "gemeint" };
        return words.Any(value.Contains);
    }

    private static bool ContainsRange(string quote, double expected)
    {
        foreach (Match match in Regex.Matches(quote, @"(?i)(\d+(?:[.,]\d+)?)\s*(km|kilomet(?:er|re)s?|m|met(?:er|re)s?)\b"))
        {
            if (!double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) continue;
            if (match.Groups[2].Value.StartsWith("k", StringComparison.OrdinalIgnoreCase)) value *= 1000;
            if (Math.Abs(value - expected) <= 0.5) return true;
        }
        return false;
    }

    private static bool ContainsBearing(string quote, double expected, string reference)
    {
        string normalized = Normalize(quote);
        if (reference == "absolute")
        {
            if (Math.Abs(NormalizeBearing(expected) - 0) <= 0.5 && new[] { "north", "norden" }.Any(normalized.Contains)) return true;
            if (Math.Abs(NormalizeBearing(expected) - 90) <= 0.5 && new[] { "east", "osten" }.Any(normalized.Contains)) return true;
            if (Math.Abs(NormalizeBearing(expected) - 180) <= 0.5 && new[] { "south", "süden" }.Any(normalized.Contains)) return true;
            if (Math.Abs(NormalizeBearing(expected) - 270) <= 0.5 && new[] { "west", "westen" }.Any(normalized.Contains)) return true;
        }
        if (reference != "absolute")
        {
            if (Math.Abs(expected) <= 0.5 && new[] { "ahead", "in front", "twelve oclock", "12 oclock", "voraus", "vor mir" }.Any(normalized.Contains)) return true;
            if (Math.Abs(expected - 90) <= 0.5 && new[] { "right", "three oclock", "3 oclock", "rechts" }.Any(normalized.Contains)) return true;
            if (Math.Abs(expected + 90) <= 0.5 && new[] { "left", "nine oclock", "9 oclock", "links" }.Any(normalized.Contains)) return true;
            if (Math.Abs(Math.Abs(expected) - 180) <= 0.5 && new[] { "behind", "six oclock", "6 oclock", "hinter" }.Any(normalized.Contains)) return true;
        }
        return Regex.Matches(quote, @"(?i)(\d+(?:[.,]\d+)?)\s*(?:°|deg(?:rees?)?|bearing)?")
            .Select(match => double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : double.NaN)
            .Any(value => double.IsFinite(value) && Math.Abs(value - Math.Abs(expected)) <= 0.5);
    }

    private static bool ContainsAge(string quote, double expected)
    {
        foreach (Match match in Regex.Matches(quote, @"(?i)(\d+(?:[.,]\d+)?)\s*(seconds?|secs?|minutes?|mins?|hours?|stunden?|minuten?|sekunden?)\b"))
        {
            if (!double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) continue;
            string unit = match.Groups[2].Value.ToLowerInvariant();
            if (unit.StartsWith("min")) value *= 60;
            else if (unit.StartsWith("hour") || unit.StartsWith("stund")) value *= 3600;
            if (Math.Abs(value - expected) <= 1) return true;
        }
        return false;
    }

    private static bool ContainsState(string quote, string state)
    {
        string value = Normalize(quote);
        string[] evidence = state switch
        {
            "intact" => new[] { "intact", "undamaged", "unbeschädigt", "einsatzbereit" },
            "damaged" => new[] { "damaged", "disabled", "beschädigt", "kaputt" },
            "destroyed" => new[] { "destroyed", "wreck", "burned out", "zerstört", "wrack" },
            "changed" => new[] { "changed", "moved", "verändert", "bewegt" },
            _ => new[] { "unknown", "unbekannt" }
        };
        return evidence.Any(value.Contains);
    }

    private void PersistSession(DateTimeOffset receivedAtUtc)
    {
        Execute(_connection!, null, """
            INSERT OR IGNORE INTO sessions(session_hash, session_alias, world_name, created_utc)
            VALUES($hash, $alias, $world, $created);
            """, ("$hash", _sessionHash), ("$alias", _sessionAlias), ("$world", _worldName),
            ("$created", receivedAtUtc.ToString("O", CultureInfo.InvariantCulture)));
    }

    private void PersistEntity(EntityEntry entity, SqliteTransaction transaction)
    {
        Execute(_connection!, transaction, """
            INSERT INTO entities(
                session_hash, entity_alias, kind, identity_quality, classification, display_label, perceived_side,
                first_game_time, last_game_time, last_received_utc, x, y, z, uncertainty, state,
                conflict_count, corroboration_count, is_retracted)
            VALUES($session,$alias,$kind,$quality,$classification,$label,$side,$first,$last,$received,$x,$y,$z,$uncertainty,$state,$conflicts,$corroboration,$retracted)
            ON CONFLICT(session_hash, entity_alias) DO UPDATE SET
                classification=excluded.classification, display_label=excluded.display_label,
                perceived_side=excluded.perceived_side, first_game_time=excluded.first_game_time,
                last_game_time=excluded.last_game_time, last_received_utc=excluded.last_received_utc,
                x=excluded.x, y=excluded.y, z=excluded.z, uncertainty=excluded.uncertainty,
                state=excluded.state, conflict_count=excluded.conflict_count,
                corroboration_count=excluded.corroboration_count, is_retracted=excluded.is_retracted;
            """,
            ("$session", _sessionHash), ("$alias", entity.Alias), ("$kind", EnumText(entity.Kind)),
            ("$quality", EnumText(entity.IdentityQuality)), ("$classification", entity.Classification),
            ("$label", entity.DisplayLabel), ("$side", entity.PerceivedSide),
            ("$first", entity.FirstObservedAtGameTime), ("$last", entity.LastObservedAtGameTime),
            ("$received", entity.LastReceivedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
            ("$x", entity.Position?.X), ("$y", entity.Position?.Y), ("$z", entity.Position?.Z),
            ("$uncertainty", entity.PositionErrorMeters), ("$state", entity.State),
            ("$conflicts", entity.ConflictCount), ("$corroboration", entity.CorroborationCount),
            ("$retracted", entity.IsRetracted ? 1 : 0));
    }

    private void PersistObservation(ObservationEntry observation, SqliteTransaction transaction)
    {
        Execute(_connection!, transaction, """
            INSERT INTO observations(
                session_hash, observation_alias, source_hash, entity_alias, source_alias, provenance,
                observed_game_time, received_utc, x, y, z, uncertainty, base_confidence,
                classification, perceived_side, state, summary, constraint_conflict,
                constraint_location_alias, constraint_x, constraint_y, constraint_radius,
                supersedes, retracted_utc)
            VALUES($session,$alias,$hash,$entity,$source,$provenance,$game,$received,$x,$y,$z,$uncertainty,$confidence,$classification,$side,$state,$summary,$constraint,$constraintAlias,$constraintX,$constraintY,$constraintRadius,$supersedes,NULL);
            """,
            ("$session", _sessionHash), ("$alias", observation.Alias), ("$hash", observation.SourceHash),
            ("$entity", observation.Entity.Alias), ("$source", observation.SourceAlias),
            ("$provenance", EnumText(observation.Provenance)), ("$game", observation.ObservedAtGameTime),
            ("$received", observation.ReceivedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
            ("$x", observation.Position?.X), ("$y", observation.Position?.Y), ("$z", observation.Position?.Z),
            ("$uncertainty", observation.PositionErrorMeters), ("$confidence", observation.BaseConfidence),
            ("$classification", observation.Classification), ("$side", observation.PerceivedSide),
            ("$state", observation.State), ("$summary", observation.Summary),
            ("$constraint", observation.ConstraintConflict ? 1 : 0),
            ("$constraintAlias", observation.ConstraintLocationAlias),
            ("$constraintX", observation.ConstraintPosition?.X), ("$constraintY", observation.ConstraintPosition?.Y),
            ("$constraintRadius", observation.ConstraintRadiusMeters), ("$supersedes", observation.Supersedes));
    }

    private void PersistRetraction(ObservationEntry observation, SqliteTransaction transaction)
        => Execute(_connection!, transaction,
            "UPDATE observations SET retracted_utc = $retracted WHERE session_hash = $session AND observation_alias = $alias;",
            ("$retracted", observation.RetractedAtUtc?.ToString("O", CultureInfo.InvariantCulture)),
            ("$session", _sessionHash), ("$alias", observation.Alias));

    private void PersistLinks(EntityEntry entity, SqliteTransaction transaction)
    {
        string[] aliases = entity.Observations.Select(item => item.Alias).ToArray();
        foreach (string alias in aliases)
            Execute(_connection!, transaction,
                "DELETE FROM observation_links WHERE session_hash=$session AND from_alias=$alias;",
                ("$session", _sessionHash), ("$alias", alias));
        foreach (ObservationEntry observation in entity.Observations)
        {
            foreach (string target in observation.Corroborates)
                Execute(_connection!, transaction,
                    "INSERT OR IGNORE INTO observation_links(session_hash,from_alias,to_alias,relation) VALUES($session,$from,$to,'corroborates');",
                    ("$session", _sessionHash), ("$from", observation.Alias), ("$to", target));
            foreach (string target in observation.Contradicts)
                Execute(_connection!, transaction,
                    "INSERT OR IGNORE INTO observation_links(session_hash,from_alias,to_alias,relation) VALUES($session,$from,$to,'contradicts');",
                    ("$session", _sessionHash), ("$from", observation.Alias), ("$to", target));
        }
    }

    private string GetSourceAlias(string sourceHash, SqliteTransaction transaction)
    {
        if (_sourceAliases.TryGetValue(sourceHash, out string? alias)) return alias;
        alias = $"observer-{_nextSourceAlias++:000}";
        _sourceAliases.Add(sourceHash, alias);
        Execute(_connection!, transaction,
            "INSERT INTO source_identities(session_hash, source_hash, source_alias) VALUES($session,$hash,$alias);",
            ("$session", _sessionHash), ("$hash", sourceHash), ("$alias", alias));
        return alias;
    }

    private void LoadSession()
    {
        using SqliteCommand entities = Command(_connection!, null,
            "SELECT entity_alias,kind,identity_quality,classification,display_label,perceived_side,first_game_time,last_game_time,last_received_utc,x,y,z,uncertainty,state,conflict_count,corroboration_count,is_retracted FROM entities WHERE session_hash=$session;",
            ("$session", _sessionHash));
        using (SqliteDataReader reader = entities.ExecuteReader())
        {
            while (reader.Read())
            {
                EntityEntry entry = new(reader.GetString(0), ParseKind(reader.GetString(1)), ParseQuality(reader.GetString(2)),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5))
                {
                    FirstObservedAtGameTime = reader.GetDouble(6), LastObservedAtGameTime = reader.GetDouble(7),
                    LastReceivedAtUtc = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                    Position = reader.IsDBNull(9) ? null : new WorldPosition(reader.GetDouble(9), reader.GetDouble(10), reader.GetDouble(11)),
                    PositionErrorMeters = reader.IsDBNull(12) ? null : reader.GetDouble(12), State = reader.GetString(13),
                    ConflictCount = reader.GetInt32(14), CorroborationCount = reader.GetInt32(15), IsRetracted = reader.GetInt32(16) != 0
                };
                _entitiesByAlias.Add(entry.Alias, entry);
            }
        }
        using SqliteCommand identities = Command(_connection!, null,
            "SELECT identity_hash,entity_alias FROM entity_identities WHERE session_hash=$session;", ("$session", _sessionHash));
        using (SqliteDataReader reader = identities.ExecuteReader())
            while (reader.Read() && _entitiesByAlias.TryGetValue(reader.GetString(1), out EntityEntry? entity))
                _entitiesByIdentityHash[reader.GetString(0)] = entity;
        using SqliteCommand sources = Command(_connection!, null,
            "SELECT source_hash,source_alias FROM source_identities WHERE session_hash=$session;", ("$session", _sessionHash));
        using (SqliteDataReader reader = sources.ExecuteReader())
            while (reader.Read()) _sourceAliases[reader.GetString(0)] = reader.GetString(1);
        using SqliteCommand observations = Command(_connection!, null,
            "SELECT observation_alias,source_hash,entity_alias,source_alias,provenance,observed_game_time,received_utc,x,y,z,uncertainty,base_confidence,classification,perceived_side,state,summary,constraint_conflict,constraint_location_alias,constraint_x,constraint_y,constraint_radius,supersedes,retracted_utc FROM observations WHERE session_hash=$session;",
            ("$session", _sessionHash));
        using (SqliteDataReader reader = observations.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!_entitiesByAlias.TryGetValue(reader.GetString(2), out EntityEntry? entity)) continue;
                ObservationEntry entry = new(reader.GetString(0), reader.GetString(1), entity, reader.GetString(3),
                    ParseProvenance(reader.GetString(4)), reader.GetDouble(5),
                    DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                    reader.IsDBNull(7) ? null : new WorldPosition(reader.GetDouble(7), reader.GetDouble(8), reader.GetDouble(9)),
                    reader.IsDBNull(10) ? null : reader.GetDouble(10), reader.GetDouble(11), reader.GetString(12),
                    reader.GetString(13), reader.GetString(14), reader.GetString(15), reader.GetInt32(16) != 0,
                    reader.GetString(17), reader.IsDBNull(18) ? null : new WorldPosition(reader.GetDouble(18), reader.GetDouble(19), 0),
                    reader.IsDBNull(20) ? null : reader.GetDouble(20), reader.GetString(21))
                {
                    RetractedAtUtc = reader.IsDBNull(22) ? null : DateTimeOffset.Parse(reader.GetString(22), CultureInfo.InvariantCulture)
                };
                entity.Observations.Add(entry);
                _observationsByAlias.Add(entry.Alias, entry);
                _observationHashes.Add(entry.SourceHash);
            }
        }
        foreach (EntityEntry entity in _entitiesByAlias.Values) Recompute(entity);
        _nextEntityAlias = NextAlias(_entitiesByAlias.Keys) + 1;
        _nextObservationAlias = NextAlias(_observationsByAlias.Keys) + 1;
        _nextSourceAlias = NextAlias(_sourceAliases.Values) + 1;
    }

    private void LoadCachedGazetteer(string worldName, double worldSize)
    {
        _locations.Clear();
        string path = Path.Combine(_rootDirectory, "gazetteer.sqlite3");
        if (!File.Exists(path))
        {
            _gazetteerReadiness = GazetteerReadiness.Unavailable;
            return;
        }
        using SqliteConnection connection = Open(path);
        MigrateGazetteer(connection);
        string worldKey = WorldKey(worldName, worldSize);
        using SqliteCommand map = Command(connection, null,
            "SELECT fingerprint FROM gazetteer_maps WHERE world_key=$world;", ("$world", worldKey));
        object? value = map.ExecuteScalar();
        if (value is not string fingerprint)
        {
            _gazetteerReadiness = GazetteerReadiness.Unavailable;
            return;
        }
        _gazetteerFingerprint = fingerprint;
        using SqliteCommand locations = Command(connection, null,
            "SELECT alias,official_name,location_type,x,y,size_x,size_y FROM gazetteer_locations WHERE world_key=$world ORDER BY alias;",
            ("$world", worldKey));
        using SqliteDataReader reader = locations.ExecuteReader();
        while (reader.Read())
            _locations.Add(new NamedLocationState(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                new WorldPosition(reader.GetDouble(3), reader.GetDouble(4), 0),
                reader.IsDBNull(5) ? null : reader.GetDouble(5), reader.IsDBNull(6) ? null : reader.GetDouble(6)));
        _gazetteerReadiness = GazetteerReadiness.Ready;
    }

    private static void MigrateOperational(SqliteConnection connection)
    {
        EnsureSupportedSchema(connection, "operational-memory");
        Execute(connection, null, """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            PRAGMA user_version=1;
            CREATE TABLE IF NOT EXISTS schema_metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT OR REPLACE INTO schema_metadata(key,value) VALUES('schema_version','1');
            CREATE TABLE IF NOT EXISTS sessions(session_hash TEXT PRIMARY KEY, session_alias TEXT NOT NULL, world_name TEXT NOT NULL, created_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS entities(
                session_hash TEXT NOT NULL, entity_alias TEXT NOT NULL, kind TEXT NOT NULL, identity_quality TEXT NOT NULL,
                classification TEXT NOT NULL, display_label TEXT NOT NULL, perceived_side TEXT NOT NULL,
                first_game_time REAL NOT NULL DEFAULT 0, last_game_time REAL NOT NULL DEFAULT 0, last_received_utc TEXT NOT NULL DEFAULT '',
                x REAL, y REAL, z REAL, uncertainty REAL, state TEXT NOT NULL DEFAULT 'unknown',
                conflict_count INTEGER NOT NULL DEFAULT 0, corroboration_count INTEGER NOT NULL DEFAULT 0,
                is_retracted INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(session_hash,entity_alias));
            CREATE TABLE IF NOT EXISTS entity_identities(session_hash TEXT NOT NULL, identity_hash TEXT NOT NULL, entity_alias TEXT NOT NULL, PRIMARY KEY(session_hash,identity_hash));
            CREATE TABLE IF NOT EXISTS source_identities(session_hash TEXT NOT NULL, source_hash TEXT NOT NULL, source_alias TEXT NOT NULL, PRIMARY KEY(session_hash,source_hash));
            CREATE TABLE IF NOT EXISTS observations(
                session_hash TEXT NOT NULL, observation_alias TEXT NOT NULL, source_hash TEXT NOT NULL,
                entity_alias TEXT NOT NULL, source_alias TEXT NOT NULL, provenance TEXT NOT NULL,
                observed_game_time REAL NOT NULL, received_utc TEXT NOT NULL, x REAL, y REAL, z REAL,
                uncertainty REAL, base_confidence REAL NOT NULL, classification TEXT NOT NULL,
                perceived_side TEXT NOT NULL, state TEXT NOT NULL, summary TEXT NOT NULL,
                constraint_conflict INTEGER NOT NULL DEFAULT 0, constraint_location_alias TEXT NOT NULL DEFAULT '',
                constraint_x REAL, constraint_y REAL, constraint_radius REAL,
                supersedes TEXT NOT NULL DEFAULT '', retracted_utc TEXT,
                PRIMARY KEY(session_hash,observation_alias), UNIQUE(session_hash,source_hash));
            CREATE TABLE IF NOT EXISTS observation_links(session_hash TEXT NOT NULL, from_alias TEXT NOT NULL, to_alias TEXT NOT NULL, relation TEXT NOT NULL, PRIMARY KEY(session_hash,from_alias,to_alias,relation));
            CREATE TABLE IF NOT EXISTS alias_redirects(session_hash TEXT NOT NULL, old_alias TEXT NOT NULL, new_alias TEXT NOT NULL, PRIMARY KEY(session_hash,old_alias));
            CREATE INDEX IF NOT EXISTS ix_entities_session_kind ON entities(session_hash,kind);
            CREATE INDEX IF NOT EXISTS ix_entities_session_position ON entities(session_hash,x,y);
            CREATE INDEX IF NOT EXISTS ix_observations_session_entity ON observations(session_hash,entity_alias,received_utc);
            CREATE INDEX IF NOT EXISTS ix_observations_session_provenance ON observations(session_hash,provenance,received_utc);
            """);
    }

    private static void MigrateGazetteer(SqliteConnection connection)
    {
        EnsureSupportedSchema(connection, "gazetteer");
        Execute(connection, null, """
            PRAGMA journal_mode=WAL;
            PRAGMA user_version=1;
            CREATE TABLE IF NOT EXISTS schema_metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT OR REPLACE INTO schema_metadata(key,value) VALUES('schema_version','1');
            CREATE TABLE IF NOT EXISTS gazetteer_maps(world_key TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, world_name TEXT NOT NULL, world_size REAL NOT NULL, grid_json TEXT NOT NULL, updated_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS gazetteer_locations(
                world_key TEXT NOT NULL, alias TEXT NOT NULL, config_key_hash TEXT NOT NULL,
                official_name TEXT NOT NULL, normalized_name TEXT NOT NULL, location_type TEXT NOT NULL,
                x REAL NOT NULL, y REAL NOT NULL, size_x REAL, size_y REAL, PRIMARY KEY(world_key,alias));
            CREATE INDEX IF NOT EXISTS ix_gazetteer_name ON gazetteer_locations(world_key,normalized_name);
            CREATE INDEX IF NOT EXISTS ix_gazetteer_position ON gazetteer_locations(world_key,x,y);
            """);
    }

    private static void EnsureSupportedSchema(SqliteConnection connection, string databaseKind)
    {
        using SqliteCommand userVersionCommand = Command(connection, null, "PRAGMA user_version;");
        long userVersion = Convert.ToInt64(userVersionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        long metadataVersion = 0;
        using (SqliteCommand tableCommand = Command(connection, null,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_metadata';"))
        {
            if (Convert.ToInt64(tableCommand.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                using SqliteCommand metadataCommand = Command(connection, null,
                    "SELECT value FROM schema_metadata WHERE key='schema_version';");
                object? value = metadataCommand.ExecuteScalar();
                if (value is string text && (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out metadataVersion) || metadataVersion < 0))
                    throw new InvalidDataException(databaseKind + "_schema_version_invalid");
            }
        }
        if (Math.Max(userVersion, metadataVersion) > SchemaVersion)
            throw new InvalidDataException(databaseKind + "_schema_version_newer_than_supported");
    }

    private static SqliteConnection Open(string path)
    {
        SqliteConnection connection = new($"Data Source={path};Mode=ReadWriteCreate;Cache=Shared;Pooling=False");
        connection.Open();
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = Command(connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static string GazetteerFingerprint(MapGazetteerObservation gazetteer)
    {
        StringBuilder canonical = new();
        canonical.Append(SchemaVersion).Append('|').Append(Normalize(gazetteer.WorldName)).Append('|')
            .Append(gazetteer.WorldSizeMeters.ToString("R", CultureInfo.InvariantCulture));
        foreach (MapGridSample sample in gazetteer.GridSamples.OrderBy(item => item.Position.X).ThenBy(item => item.Position.Y))
            canonical.Append('|').Append(sample.Position.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(sample.Position.Y.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(sample.Grid);
        foreach (GazetteerLocationObservation location in gazetteer.Locations
            .OrderBy(item => Normalize(item.OfficialName), StringComparer.Ordinal).ThenBy(item => item.LocationType, StringComparer.Ordinal)
            .ThenBy(item => item.Position.X).ThenBy(item => item.Position.Y))
            canonical.Append('|').Append(Normalize(location.OfficialName)).Append('|').Append(location.LocationType).Append('|')
                .Append(location.Position.X.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(location.Position.Y.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(location.SizeX?.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(location.SizeY?.ToString("R", CultureInfo.InvariantCulture));
        return Hash(canonical.ToString());
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string WorldKey(string world, double size) => Hash(Normalize(world) + "\0" + size.ToString("R", CultureInfo.InvariantCulture));
    private bool IsActive(ProtocolEnvelope envelope) => _sessionHash == Hash("session\0" + envelope.MissionId + "\0" + envelope.SessionId);
    private WorldPosition RequirePlayerPosition() => _worldState.GetCurrentView().Player?.Metadata.Position ?? throw new ToolValidationException("player_position_unavailable");
    private static double Distance(WorldPosition first, WorldPosition second) => Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));
    private static double Bearing(WorldPosition first, WorldPosition second) => NormalizeBearing(Math.Atan2(second.X - first.X, second.Y - first.Y) * 180 / Math.PI);
    private static double NormalizeBearing(double value) => (value % 360 + 360) % 360;
    private static object? Position(WorldPosition? position) => position is null ? null : new[] { Round(position.X), Round(position.Y), Round(position.Z) };
    private static double Round(double value) => Math.Round(value, 3);
    private static double? Round(double? value) => value is null ? null : Round(value.Value);
    private static string Normalize(string value) => Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");
    private static string EnumText<T>(T value) where T : struct, Enum => Regex.Replace(value.ToString(), "([a-z0-9])([A-Z])", "$1-$2").ToLowerInvariant();
    private static string Serialize(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    private static string FingerprintAlias(string value) => value.Length < 8 ? string.Empty : "gazetteer-" + value[..8].ToLowerInvariant();
    private static string RedactedPath(string path) => path.Length == 0 ? string.Empty : Path.Combine("…", "OperationalMemory", Path.GetFileName(path));
    private static string SafeSummary(OperationalEntityKind kind, string classification, OperationalProvenance provenance)
        => $"{EnumText(kind)} {classification} observed via {EnumText(provenance)}".Trim()[..Math.Min(160, $"{EnumText(kind)} {classification} observed via {EnumText(provenance)}".Trim().Length)];
    private static double BaseConfidence(OperationalProvenance provenance) => provenance switch
    {
        OperationalProvenance.Visual => 0.90,
        OperationalProvenance.SideKnowledge => 0.75,
        OperationalProvenance.Sensor => 0.65,
        OperationalProvenance.MissionReport => 0.70,
        _ => 0.45
    };
    private static WorldFreshness Freshness(OperationalProvenance? provenance, double age) => provenance switch
    {
        OperationalProvenance.Visual => age <= 5 ? WorldFreshness.Live : age <= 30 ? WorldFreshness.Recent : age <= 180 ? WorldFreshness.Stale : WorldFreshness.Historical,
        OperationalProvenance.Sensor or OperationalProvenance.SideKnowledge => age <= 10 ? WorldFreshness.Live : age <= 60 ? WorldFreshness.Recent : age <= 180 ? WorldFreshness.Stale : WorldFreshness.Historical,
        _ => age <= 30 ? WorldFreshness.Live : age <= 300 ? WorldFreshness.Recent : age <= 1800 ? WorldFreshness.Stale : WorldFreshness.Historical
    };
    private static double FreshnessMultiplier(WorldFreshness freshness) => freshness switch { WorldFreshness.Live => 1, WorldFreshness.Recent => 0.85, WorldFreshness.Stale => 0.55, _ => 0.25 };
    private static int NextAlias(IEnumerable<string> aliases) => aliases.Select(value => Regex.Match(value, @"(\d+)$")).Where(match => match.Success).Select(match => int.Parse(match.Value, CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
    private static OperationalEntityKind ParseKind(string value) => value switch { "contact" => OperationalEntityKind.Contact, "vehicle" => OperationalEntityKind.Vehicle, "supply" => OperationalEntityKind.Supply, "weapon" => OperationalEntityKind.Weapon, "fortification" => OperationalEntityKind.Fortification, "static" => OperationalEntityKind.Static, _ => OperationalEntityKind.Other };
    private static OperationalIdentityQuality ParseQuality(string value) => value switch { "stable-mission" => OperationalIdentityQuality.StableMission, "best-effort" => OperationalIdentityQuality.BestEffort, _ => OperationalIdentityQuality.FusedReport };
    private static OperationalProvenance ParseProvenance(string value) => value switch { "visual" => OperationalProvenance.Visual, "sensor" => OperationalProvenance.Sensor, "side-knowledge" => OperationalProvenance.SideKnowledge, "mission-report" => OperationalProvenance.MissionReport, _ => OperationalProvenance.PlayerReport };

    private static string ReadRequiredString(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String) throw new ToolValidationException(name + "_required");
        string result = value.GetString()?.Trim() ?? string.Empty;
        if (result.Length == 0 || result.Length > maximum || result.Any(char.IsControl)) throw new ToolValidationException(name + "_invalid");
        return result;
    }
    private static string ReadRequiredStringAllowEmpty(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new ToolValidationException(name + "_required");
        string result = value.GetString()?.Trim() ?? string.Empty;
        if (result.Length > maximum || result.Any(char.IsControl)) throw new ToolValidationException(name + "_invalid");
        return result;
    }
    private static void EnsureOnlyProperties(JsonElement root, params string[] allowed)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new ToolValidationException("arguments_not_object");
        HashSet<string> names = new(allowed, StringComparer.Ordinal);
        if (root.EnumerateObject().Any(property => !names.Contains(property.Name)))
            throw new ToolValidationException("unexpected_tool_argument");
    }
    private static string ReadNullableString(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value)) throw new ToolValidationException(name + "_required");
        if (value.ValueKind == JsonValueKind.Null) return string.Empty;
        if (value.ValueKind != JsonValueKind.String) throw new ToolValidationException(name + "_invalid");
        string result = value.GetString()?.Trim() ?? string.Empty;
        if (result.Length > maximum || result.Any(char.IsControl)) throw new ToolValidationException(name + "_invalid");
        return result;
    }
    private static string ReadEnum(JsonElement root, string name, params string[] allowed)
    {
        string value = ReadRequiredString(root, name, 64).ToLowerInvariant();
        if (!allowed.Contains(value, StringComparer.Ordinal)) throw new ToolValidationException(name + "_invalid");
        return value;
    }
    private static double ReadRequiredNumber(JsonElement root, string name, double minimum, double maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result) || !double.IsFinite(result) || result < minimum || result > maximum)
            throw new ToolValidationException(name + "_invalid");
        return result;
    }
    private static double? ReadNullableNumber(JsonElement root, string name, double minimum, double maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value)) throw new ToolValidationException(name + "_required");
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result) || !double.IsFinite(result) || result < minimum || result > maximum)
            throw new ToolValidationException(name + "_invalid");
        return result;
    }
    private static int ReadRequiredInteger(JsonElement root, string name, int minimum, int maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result) || result < minimum || result > maximum)
            throw new ToolValidationException(name + "_invalid");
        return result;
    }
    private static bool ReadRequiredBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ToolValidationException(name + "_invalid");
        return value.GetBoolean();
    }
    private static string ToolError(string code) => Serialize(new Dictionary<string, object?> { ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = "The local tool policy rejected this request." } });
    private static TelemetryIngestResult Rejected(string code) => new(TelemetryIngestStatus.Rejected, DiagnosticCode: code);

    private void ResetInMemory()
    {
        _entitiesByAlias.Clear(); _entitiesByIdentityHash.Clear(); _observationsByAlias.Clear();
        _observationHashes.Clear(); _sourceAliases.Clear(); _aliasRedirects.Clear(); _locations.Clear();
        _nextEntityAlias = _nextObservationAlias = _nextSourceAlias = 1;
        _gazetteerFingerprint = _lastBatchAlias = _diagnosticCode = string.Empty;
        _gazetteerReadiness = GazetteerReadiness.Unavailable;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection?.Dispose();
    }

    private sealed class EntityEntry
    {
        public EntityEntry(string alias, OperationalEntityKind kind, OperationalIdentityQuality quality, string classification, string displayLabel, string perceivedSide)
        { Alias = alias; Kind = kind; IdentityQuality = quality; Classification = classification; DisplayLabel = displayLabel; PerceivedSide = perceivedSide; }
        public string Alias { get; }
        public OperationalEntityKind Kind { get; }
        public OperationalIdentityQuality IdentityQuality { get; }
        public string Classification { get; set; }
        public string DisplayLabel { get; set; }
        public string PerceivedSide { get; set; }
        public double FirstObservedAtGameTime { get; set; }
        public double LastObservedAtGameTime { get; set; }
        public DateTimeOffset LastReceivedAtUtc { get; set; }
        public WorldPosition? Position { get; set; }
        public double? PositionErrorMeters { get; set; }
        public string State { get; set; } = "unknown";
        public int ConflictCount { get; set; }
        public int CorroborationCount { get; set; }
        public bool IsRetracted { get; set; }
        public List<ObservationEntry> Observations { get; } = new();
    }

    private sealed class ObservationEntry
    {
        public ObservationEntry(string alias, string sourceHash, EntityEntry entity, string sourceAlias, OperationalProvenance provenance,
            double observedAtGameTime, DateTimeOffset receivedAtUtc, WorldPosition? position, double? positionErrorMeters,
            double baseConfidence, string classification, string perceivedSide, string state, string summary,
            bool constraintConflict, string constraintLocationAlias, WorldPosition? constraintPosition,
            double? constraintRadiusMeters, string supersedes)
        { Alias = alias; SourceHash = sourceHash; Entity = entity; SourceAlias = sourceAlias; Provenance = provenance; ObservedAtGameTime = observedAtGameTime; ReceivedAtUtc = receivedAtUtc; Position = position; PositionErrorMeters = positionErrorMeters; BaseConfidence = baseConfidence; Classification = classification; PerceivedSide = perceivedSide; State = state; Summary = summary; ConstraintConflict = constraintConflict; ConstraintLocationAlias = constraintLocationAlias; ConstraintPosition = constraintPosition; ConstraintRadiusMeters = constraintRadiusMeters; Supersedes = supersedes; }
        public string Alias { get; }
        public string SourceHash { get; }
        public EntityEntry Entity { get; set; }
        public string SourceAlias { get; }
        public OperationalProvenance Provenance { get; }
        public double ObservedAtGameTime { get; }
        public DateTimeOffset ReceivedAtUtc { get; }
        public WorldPosition? Position { get; }
        public double? PositionErrorMeters { get; }
        public double BaseConfidence { get; }
        public string Classification { get; }
        public string PerceivedSide { get; }
        public string State { get; }
        public string Summary { get; }
        public bool ConstraintConflict { get; }
        public string ConstraintLocationAlias { get; }
        public WorldPosition? ConstraintPosition { get; }
        public double? ConstraintRadiusMeters { get; }
        public string Supersedes { get; }
        public DateTimeOffset? RetractedAtUtc { get; set; }
        public HashSet<string> Corroborates { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Contradicts { get; } = new(StringComparer.Ordinal);
    }

    private sealed record ReportInput(string SourceQuote, string TimeReference, OperationalEntityKind Kind, string Classification,
        string State, double? RangeMeters, double? BearingDegrees, string BearingReference, double RangePrecisionMeters,
        double BearingPrecisionDegrees, double? AgeSeconds, string NamedLocation);

    private sealed record ReportGeometry(double ObservedAtGameTime, WorldPosition? Position, double? UncertaintyMeters,
        bool ConstraintConflict, string ConstraintLocationAlias, WorldPosition? ConstraintPosition,
        double? ConstraintRadiusMeters);

    private sealed class ToolValidationException : Exception
    {
        public ToolValidationException(string code) : base(code) => Code = code;
        public string Code { get; }
    }
}
