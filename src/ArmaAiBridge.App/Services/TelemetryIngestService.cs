using System.Globalization;
using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class TelemetryIngestService : IDisposable
{
    public const string TelemetrySchema = "arma-ai-bridge/arma3/telemetry-v1";
    public const string SessionHandshakeSchema = "arma-ai-bridge/arma3/session-handshake-v1";
    public const string FriendlyForceSnapshotSchema = "arma-ai-bridge/arma3/friendly-force-snapshot-v1";
    public const string FriendlyForceDeltaSchema = "arma-ai-bridge/arma3/friendly-force-delta-v1";
    public const string MissionCapabilitiesSchema = "arma-ai-bridge/arma3/mission-capabilities-v1";
    public const string MapGazetteerSchema = "arma-ai-bridge/arma3/map-gazetteer-v1";
    public const string OperationalObservationBatchSchema = "arma-ai-bridge/arma3/operational-observation-batch-v1";
    private const int MaximumContactsPerMessage = 128;
    private const int MaximumProtocolEntitiesPerMessage = 512;
    private static readonly TimeSpan SnapshotAssemblyTimeout = TimeSpan.FromSeconds(5);
    private static readonly HashSet<string> AllowedSides = new(StringComparer.Ordinal)
    { "WEST", "EAST", "GUER", "CIV" };
    private static readonly HashSet<string> AllowedVisibilities = new(StringComparer.Ordinal)
    { "own-side", "own-group" };
    private static readonly HashSet<string> AllowedAssetKinds = new(StringComparer.Ordinal)
    { "rotary_transport", "ground_transport", "medevac", "resupply", "reconnaissance", "vehicle_recovery", "other" };
    private static readonly HashSet<string> AllowedAssetStatuses = new(StringComparer.Ordinal)
    { "available", "busy", "degraded", "unavailable", "unknown" };
    private static readonly HashSet<string> AllowedCapabilityKinds = new(AllowedAssetKinds, StringComparer.Ordinal)
    { "artillery", "cas", "marker_management", "task_management" };
    private readonly WorldStateStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly TelemetryPipeServer? _pipe;
    private readonly LogService? _log;
    private readonly OperationalMemoryStore? _operationalMemory;
    private readonly Dictionary<string, PendingReconciliation> _pendingReconciliations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingGazetteer> _pendingGazetteers = new(StringComparer.Ordinal);
    private bool _disposed;

    public TelemetryIngestService(
        WorldStateStore store,
        TimeProvider? timeProvider = null,
        OperationalMemoryStore? operationalMemory = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _operationalMemory = operationalMemory;
    }

    public TelemetryIngestService(
        TelemetryPipeServer pipe,
        WorldStateStore store,
        LogService log,
        TimeProvider? timeProvider = null,
        OperationalMemoryStore? operationalMemory = null)
        : this(store, timeProvider, operationalMemory)
    {
        _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _pipe.MessageReceived += OnMessageReceived;
        _pipe.ClientConnectionChanged += OnClientConnectionChanged;
        _store.SetConnected(_pipe.IsClientConnected);
    }

    public TelemetryIngestResult Ingest(string json)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(json))
            return Rejected("empty_message");

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Rejected("root_not_object");

            string schema = ReadOptionalString(root, "schema");
            DateTimeOffset receivedAtUtc = _timeProvider.GetUtcNow();
            ExpirePendingReconciliations(receivedAtUtc);
            return schema switch
            {
                TelemetrySchema => _store.Apply(ParseObservation(root, receivedAtUtc)),
                SessionHandshakeSchema => IngestHandshake(ParseHandshake(root, receivedAtUtc)),
                FriendlyForceSnapshotSchema => IngestSnapshotPage(ParseSnapshotPage(root, receivedAtUtc)),
                FriendlyForceDeltaSchema => IngestDelta(ParseDelta(root, receivedAtUtc)),
                MissionCapabilitiesSchema => IngestCapabilities(ParseCapabilities(root, receivedAtUtc)),
                MapGazetteerSchema => IngestGazetteerPage(ParseGazetteerPage(root, receivedAtUtc)),
                OperationalObservationBatchSchema => IngestOperationalObservations(ParseOperationalObservations(root, receivedAtUtc)),
                _ => new TelemetryIngestResult(TelemetryIngestStatus.Ignored, DiagnosticCode: "unrelated_schema")
            };
        }
        catch (JsonException)
        {
            return Rejected("invalid_json");
        }
        catch (TelemetryFormatException exception)
        {
            return Rejected(exception.Code);
        }
        catch (FormatException)
        {
            return Rejected("invalid_number");
        }
        catch (OverflowException)
        {
            return Rejected("number_out_of_range");
        }
    }

    public static bool IsWorldStateSchema(string schema)
        => schema is TelemetrySchema or SessionHandshakeSchema or FriendlyForceSnapshotSchema or
            FriendlyForceDeltaSchema or MissionCapabilitiesSchema or MapGazetteerSchema or
            OperationalObservationBatchSchema;

    private TelemetryIngestResult IngestHandshake(SessionHandshakeObservation handshake)
    {
        TelemetryIngestResult result = _store.ApplyHandshake(handshake);
        if (result.Status == TelemetryIngestStatus.Applied)
            _operationalMemory?.ActivateSession(handshake);
        if (result.SessionReset)
        {
            _pendingReconciliations.Clear();
            _pendingGazetteers.Clear();
        }
        return result;
    }

    private static TelemetryObservation ParseObservation(JsonElement root, DateTimeOffset receivedAtUtc)
    {
        string missionId = ReadOptionalString(root, "missionId");
        string sessionId = ReadOptionalString(root, "sessionId");
        double gameTime = ReadRequiredFiniteNumber(root, "timestamp");
        if (gameTime < 0) throw Invalid("timestamp_out_of_range");
        long frame = ReadOptionalFrame(root);

        JsonElement mapObject = ReadRequiredObject(root, "map");
        string mapName = ReadRequiredString(mapObject, "name");
        double mapSize = ReadRequiredFiniteNumber(mapObject, "sizeMeters");
        if (mapSize <= 0) throw Invalid("map_size_out_of_range");
        MapObservation map = new(
            mapName,
            mapSize,
            ReadOptionalString(mapObject, "grid"),
            ReadOptionalFiniteNumber(mapObject, "daytime"));

        JsonElement playerObject = ReadRequiredObject(root, "player");
        WorldPosition positionAtl = ReadRequiredVector(playerObject, "positionATL");
        PlayerObservation player = new(
            ReadOptionalString(playerObject, "id"),
            ReadOptionalString(playerObject, "side"),
            ReadOptionalString(playerObject, "group"),
            ReadOptionalString(playerObject, "groupId"),
            positionAtl,
            ReadOptionalVector(playerObject, "positionASL"),
            ReadOptionalFiniteNumber(playerObject, "bodyHeading"),
            ReadRequiredFiniteNumber(playerObject, "viewHeading"),
            ReadOptionalFiniteNumber(playerObject, "speedKph"),
            ReadOptionalFiniteNumber(playerObject, "damage"),
            ReadOptionalString(playerObject, "lifeState"),
            ReadOptionalString(playerObject, "stance"),
            ReadOptionalString(playerObject, "weapon"),
            ReadOptionalString(playerObject, "magazine"),
            ReadOptionalString(playerObject, "muzzle"),
            ReadOptionalInt(playerObject, "loadedRounds"),
            ReadOptionalInt(playerObject, "matchingMagazineCount"),
            ReadOptionalInt(playerObject, "matchingMagazineRounds"));

        VehicleObservation? vehicle = ParseVehicle(root);
        JsonElement contactsArray = ReadRequiredArray(root, "contacts");
        JsonElement sensorsArray = ReadRequiredArray(root, "sensorContacts");
        List<ContactObservation> contacts = new();
        List<SensorContactObservation> sensorContacts = new();
        int skipped = 0;

        foreach (JsonElement item in contactsArray.EnumerateArray())
        {
            if (contacts.Count >= MaximumContactsPerMessage)
            {
                skipped++;
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(ReadOptionalString(item, "id")))
            {
                skipped++;
                continue;
            }
            contacts.Add(ParseContact(item));
        }

        foreach (JsonElement item in sensorsArray.EnumerateArray())
        {
            if (sensorContacts.Count >= MaximumContactsPerMessage)
            {
                skipped++;
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(ReadOptionalString(item, "id")))
            {
                skipped++;
                continue;
            }
            sensorContacts.Add(ParseSensorContact(item));
        }

        return new TelemetryObservation(
            missionId, sessionId, gameTime, frame, receivedAtUtc, map, player, vehicle, contacts, sensorContacts, skipped);
    }

    private static VehicleObservation? ParseVehicle(JsonElement root)
    {
        if (!root.TryGetProperty("vehicle", out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("vehicle_not_object");
        return new VehicleObservation(
            ReadOptionalString(value, "id"),
            ReadOptionalString(value, "class"),
            ReadOptionalString(value, "displayName"),
            ReadOptionalVector(value, "positionATL"),
            ReadOptionalFiniteNumber(value, "heading"),
            ReadOptionalFiniteNumber(value, "speedKph"),
            ReadOptionalFiniteNumber(value, "fuel"),
            ReadOptionalFiniteNumber(value, "damage"),
            ReadVehicleRole(value));
    }

    private static ContactObservation ParseContact(JsonElement item)
    {
        WorldPosition? position = ReadOptionalVector(item, "estimatedPosition");
        double? positionError = ReadOptionalNonNegativeNumber(item, "positionErrorMeters");
        ContactObservation value = new(
            ReadRequiredString(item, "id"),
            ReadOptionalString(item, "class"),
            ReadOptionalString(item, "displayName"),
            ReadOptionalBoolean(item, "knownByPlayer"),
            ReadOptionalBoolean(item, "knownByGroup"),
            ReadOptionalAge(item, "lastSeenAgeSeconds"),
            ReadOptionalAge(item, "lastThreatAgeSeconds"),
            ReadOptionalString(item, "perceivedSide"),
            positionError,
            position,
            ReadOptionalBoolean(item, "ignored"),
            string.Empty);
        return value with { Signature = BuildContactSignature(value) };
    }

    private static SensorContactObservation ParseSensorContact(JsonElement item)
        => new(
            ReadRequiredString(item, "id"),
            ReadOptionalString(item, "class"),
            ReadOptionalString(item, "targetType"),
            ReadOptionalString(item, "relationship"),
            ReadStringArray(item, "sensors"));

    private static SessionHandshakeObservation ParseHandshake(JsonElement root, DateTimeOffset receivedAtUtc)
    {
        ProtocolEnvelope envelope = ParseProtocolEnvelope(root, receivedAtUtc);
        JsonElement protocol = ReadRequiredObject(root, "protocol");
        int major = ReadRequiredInt(protocol, "major", 1, 1);
        int minor = ReadRequiredInt(protocol, "minor", 0, 100);
        JsonElement world = ReadRequiredObject(root, "world");
        string worldName = ReadRequiredProtocolString(world, "name");
        double worldSize = ReadRequiredFiniteNumber(world, "sizeMeters");
        if (worldSize <= 0) throw Invalid("world_size_out_of_range");
        JsonElement viewer = ReadRequiredObject(root, "viewer");
        string viewerSide = ReadRequiredProtocolString(viewer, "side").ToUpperInvariant();
        string visibility = ReadRequiredProtocolString(viewer, "visibility").ToLowerInvariant();
        if (!AllowedSides.Contains(viewerSide)) throw Invalid("viewer_side_invalid");
        if (!AllowedVisibilities.Contains(visibility)) throw Invalid("visibility_invalid");

        JsonElement featuresArray = ReadRequiredArray(root, "features");
        if (featuresArray.GetArrayLength() is < 1 or > 16) throw Invalid("features_count_invalid");
        List<WorldProtocolFeatureState> features = new();
        foreach (JsonElement item in featuresArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw Invalid("feature_not_object");
            string name = ReadRequiredProtocolString(item, "name", 64);
            int version = ReadRequiredInt(item, "version", 1, 100);
            if (features.Any(feature => string.Equals(feature.Name, name, StringComparison.Ordinal)))
                throw Invalid("duplicate_feature");
            features.Add(new WorldProtocolFeatureState(name, version));
        }

        return new SessionHandshakeObservation(
            envelope, major, minor, worldName, worldSize, viewerSide, visibility, features);
    }

    private static FriendlyForceSnapshotPageObservation ParseSnapshotPage(
        JsonElement root, DateTimeOffset receivedAtUtc)
    {
        ProtocolEnvelope envelope = ParseProtocolEnvelope(root, receivedAtUtc);
        string reconciliationId = ReadRequiredProtocolString(root, "reconciliationId");
        int pageIndex = ReadRequiredInt(root, "pageIndex", 0, 63);
        int pageCount = ReadRequiredInt(root, "pageCount", 1, 64);
        if (pageIndex >= pageCount) throw Invalid("page_index_out_of_range");
        return new FriendlyForceSnapshotPageObservation(
            envelope,
            reconciliationId,
            pageIndex,
            pageCount,
            ParseGroups(root, "groups"),
            ParseUnits(root, "units", maximum: 32),
            ParseVehicles(root, "vehicles"),
            ParseAssets(root, "assets"));
    }

    private static FriendlyForceDeltaObservation ParseDelta(JsonElement root, DateTimeOffset receivedAtUtc)
    {
        ProtocolEnvelope envelope = ParseProtocolEnvelope(root, receivedAtUtc);
        return new FriendlyForceDeltaObservation(
            envelope,
            ReadOptionalProtocolString(root, "baseReconciliationId"),
            ParseGroups(root, "upsertGroups"),
            ParseUnits(root, "upsertUnits"),
            ParseVehicles(root, "upsertVehicles"),
            ParseAssets(root, "upsertAssets"),
            ReadRequiredIdArray(root, "removedGroupIds"),
            ReadRequiredIdArray(root, "removedUnitIds"),
            ReadRequiredIdArray(root, "removedVehicleIds"),
            ReadRequiredIdArray(root, "removedAssetIds"));
    }

    private static MissionCapabilitiesObservation ParseCapabilities(JsonElement root, DateTimeOffset receivedAtUtc)
    {
        ProtocolEnvelope envelope = ParseProtocolEnvelope(root, receivedAtUtc);
        int registryVersion = ReadRequiredInt(root, "registryVersion", 1, int.MaxValue);
        JsonElement array = ReadRequiredArray(root, "capabilities");
        if (array.GetArrayLength() > 64) throw Invalid("capabilities_count_invalid");
        List<CapabilityObservation> capabilities = new();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw Invalid("capability_not_object");
            string kind = ReadRequiredProtocolString(item, "capability").ToLowerInvariant();
            if (!AllowedCapabilityKinds.Contains(kind)) throw Invalid("capability_kind_invalid");
            JsonElement constraints = ReadRequiredObject(item, "constraints");
            CapabilityConstraintsObservation parsedConstraints = new(
                ReadRequiredInt(constraints, "maxConcurrent", 0, 64),
                ReadRequiredSideArray(constraints, "allowedRequesterSides"),
                ReadRequiredBoundedNumber(constraints, "maxRangeMeters", 0, 100000),
                ReadRequiredInt(constraints, "maxPassengers", 0, 256),
                ReadRequiredBoolean(constraints, "supportsCasualties"),
                ReadRequiredBoolean(constraints, "requiresConfirmation"));
            capabilities.Add(new CapabilityObservation(
                ReadRequiredProtocolString(item, "id"),
                kind,
                ReadRequiredBoolean(item, "enabled"),
                ReadOptionalProtocolString(item, "provider"),
                parsedConstraints));
        }
        EnsureUnique(capabilities.Select(item => item.SourceId), "duplicate_capability_id");
        return new MissionCapabilitiesObservation(envelope, registryVersion, capabilities);
    }

    private static MapGazetteerPageObservation ParseGazetteerPage(JsonElement root, DateTimeOffset receivedAtUtc)
    {
        EnsureClosedObject(root, "gazetteer_unexpected_property", "schema", "messageId", "missionId", "sessionId",
            "timestamp", "sequence", "gazetteerId", "pageIndex", "pageCount", "world", "locations");
        ProtocolEnvelope envelope = ParseProtocolEnvelope(root, receivedAtUtc);
        string gazetteerId = ReadRequiredProtocolString(root, "gazetteerId");
        int pageIndex = ReadRequiredInt(root, "pageIndex", 0, 63);
        int pageCount = ReadRequiredInt(root, "pageCount", 1, 64);
        if (pageIndex >= pageCount) throw Invalid("gazetteer_page_index_out_of_range");
        JsonElement world = ReadRequiredObject(root, "world");
        EnsureClosedObject(world, "gazetteer_world_unexpected_property", "name", "sizeMeters", "grid");
        string worldName = ReadRequiredProtocolString(world, "name");
        double worldSize = ReadRequiredBoundedNumber(world, "sizeMeters", 1, 100000);
        JsonElement grid = ReadRequiredObject(world, "grid");
        EnsureClosedObject(grid, "gazetteer_grid_unexpected_property", "format", "samples");
        if (ReadRequiredProtocolString(grid, "format", 64) != "arma-map-grid")
            throw Invalid("grid_format_invalid");
        JsonElement samples = ReadRequiredArray(grid, "samples");
        if (samples.GetArrayLength() is < 1 or > 9) throw Invalid("grid_samples_count_invalid");
        List<MapGridSample> gridSamples = new();
        foreach (JsonElement sample in samples.EnumerateArray())
        {
            if (sample.ValueKind != JsonValueKind.Object) throw Invalid("grid_sample_not_object");
            EnsureClosedObject(sample, "grid_sample_unexpected_property", "position", "grid");
            WorldPosition position = ReadRequiredVector2(sample, "position");
            if (position.X < -100 || position.Y < -100 || position.X > worldSize + 100 || position.Y > worldSize + 100)
                throw Invalid("grid_sample_position_out_of_range");
            gridSamples.Add(new MapGridSample(position, ReadRequiredProtocolString(sample, "grid", 32)));
        }

        JsonElement locations = ReadRequiredArray(root, "locations");
        if (locations.GetArrayLength() > 64) throw Invalid("gazetteer_page_locations_count_invalid");
        List<GazetteerLocationObservation> parsedLocations = new();
        foreach (JsonElement location in locations.EnumerateArray())
        {
            if (location.ValueKind != JsonValueKind.Object) throw Invalid("gazetteer_location_not_object");
            EnsureClosedObject(location, "gazetteer_location_unexpected_property", "configKey", "officialName",
                "locationType", "position", "size");
            WorldPosition position = ReadRequiredVector2(location, "position");
            if (position.X < -100 || position.Y < -100 || position.X > worldSize + 100 || position.Y > worldSize + 100)
                throw Invalid("gazetteer_location_position_out_of_range");
            (double? sizeX, double? sizeY) = ReadOptionalVector2(location, "size", 0, worldSize);
            parsedLocations.Add(new GazetteerLocationObservation(
                ReadRequiredProtocolString(location, "configKey"),
                ReadRequiredProtocolString(location, "officialName", 160),
                ReadRequiredProtocolString(location, "locationType", 64),
                position, sizeX, sizeY));
        }
        EnsureUnique(parsedLocations.Select(item => item.ConfigKey), "duplicate_gazetteer_config_key");
        EnsureUnique(parsedLocations.Select(GazetteerRecordKey), "duplicate_gazetteer_record");
        return new MapGazetteerPageObservation(envelope, gazetteerId, pageIndex, pageCount,
            worldName, worldSize, gridSamples, parsedLocations);
    }

    private static OperationalObservationBatch ParseOperationalObservations(JsonElement root, DateTimeOffset receivedAtUtc)
    {
        EnsureClosedObject(root, "operational_batch_unexpected_property", "schema", "messageId", "missionId",
            "sessionId", "timestamp", "sequence", "batchId", "observations");
        ProtocolEnvelope envelope = ParseProtocolEnvelope(root, receivedAtUtc);
        string batchId = ReadRequiredProtocolString(root, "batchId");
        JsonElement array = ReadRequiredArray(root, "observations");
        if (array.GetArrayLength() is < 1 or > 24) throw Invalid("operational_observations_count_invalid");
        List<OperationalObservation> observations = new();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw Invalid("operational_observation_not_object");
            EnsureClosedObject(item, "operational_observation_unexpected_property", "observationId", "sourceEntityId",
                "targetEntityId", "provenance", "entityKind", "classification", "displayName", "perceivedSide",
                "observedAt", "position", "positionErrorMeters", "state", "alive", "confidenceBasis",
                "correlationHint", "retractsObservationId");
            OperationalProvenance provenance = ReadRequiredProtocolString(item, "provenance", 32) switch
            {
                "visual" => OperationalProvenance.Visual,
                "sensor" => OperationalProvenance.Sensor,
                "side-knowledge" => OperationalProvenance.SideKnowledge,
                "mission-report" => OperationalProvenance.MissionReport,
                _ => throw Invalid("operational_provenance_invalid")
            };
            OperationalEntityKind kind = ReadRequiredProtocolString(item, "entityKind", 32) switch
            {
                "contact" => OperationalEntityKind.Contact,
                "vehicle" => OperationalEntityKind.Vehicle,
                "supply" => OperationalEntityKind.Supply,
                "weapon" => OperationalEntityKind.Weapon,
                "fortification" => OperationalEntityKind.Fortification,
                "static" => OperationalEntityKind.Static,
                "other" => OperationalEntityKind.Other,
                _ => throw Invalid("operational_entity_kind_invalid")
            };
            string side = ReadRequiredProtocolString(item, "perceivedSide", 16).ToUpperInvariant();
            if (side != "UNKNOWN" && !AllowedSides.Contains(side)) throw Invalid("operational_side_invalid");
            string state = ReadRequiredProtocolString(item, "state", 32).ToLowerInvariant();
            if (state is not ("intact" or "damaged" or "destroyed" or "changed" or "unknown"))
                throw Invalid("operational_state_invalid");
            double observedAt = ReadRequiredBoundedNumber(item, "observedAt", 0, envelope.GameTime + 1);
            WorldPosition? position = ReadOptionalVector(item, "position");
            double? uncertainty = ReadOptionalNonNegativeNumber(item, "positionErrorMeters");
            if (uncertainty > 5000) throw Invalid("operational_uncertainty_out_of_range");
            observations.Add(new OperationalObservation(
                ReadRequiredProtocolString(item, "observationId"),
                ReadRequiredProtocolString(item, "sourceEntityId"),
                ReadRequiredProtocolString(item, "targetEntityId"),
                provenance, kind,
                ReadOptionalProtocolString(item, "classification", 96),
                ReadOptionalProtocolString(item, "displayName", 96), side, observedAt, position, uncertainty,
                state,
                item.TryGetProperty("alive", out JsonElement alive) && alive.ValueKind != JsonValueKind.Null
                    ? ReadRequiredBoolean(item, "alive") : null,
                ReadRequiredProtocolString(item, "confidenceBasis", 64),
                ReadOptionalProtocolString(item, "correlationHint", 128),
                ReadOptionalProtocolString(item, "retractsObservationId", 128)));
        }
        EnsureUnique(observations.Select(item => item.SourceObservationId), "duplicate_operational_observation_id");
        return new OperationalObservationBatch(envelope, batchId, observations);
    }

    private static ProtocolEnvelope ParseProtocolEnvelope(JsonElement root, DateTimeOffset receivedAtUtc)
    {
        string messageId = ReadRequiredProtocolString(root, "messageId");
        string missionId = ReadRequiredProtocolString(root, "missionId");
        string sessionId = ReadRequiredProtocolString(root, "sessionId");
        double gameTime = ReadRequiredFiniteNumber(root, "timestamp");
        if (gameTime < 0) throw Invalid("timestamp_out_of_range");
        long sequence = ReadRequiredLong(root, "sequence", 1);
        return new ProtocolEnvelope(messageId, missionId, sessionId, gameTime, sequence, receivedAtUtc);
    }

    private static IReadOnlyList<FriendlyGroupObservation> ParseGroups(JsonElement root, string propertyName)
    {
        JsonElement array = ReadRequiredArray(root, propertyName);
        if (array.GetArrayLength() > 256) throw Invalid("groups_count_invalid");
        List<FriendlyGroupObservation> result = new();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw Invalid("group_not_object");
            string side = ReadRequiredSide(item, "side");
            result.Add(new FriendlyGroupObservation(
                ReadRequiredProtocolString(item, "id"),
                ReadOptionalProtocolString(item, "callsign"),
                side,
                ReadRequiredProtocolString(item, "leaderId"),
                ReadRequiredIdArray(item, "unitIds", 64),
                ReadRequiredVector(item, "positionATL"),
                ReadOptionalProtocolString(item, "behaviour")));
        }
        EnsureUnique(result.Select(item => item.SourceId), "duplicate_group_id");
        return result;
    }

    private static IReadOnlyList<FriendlyUnitObservation> ParseUnits(
        JsonElement root, string propertyName, int maximum = MaximumProtocolEntitiesPerMessage)
    {
        JsonElement array = ReadRequiredArray(root, propertyName);
        if (array.GetArrayLength() > maximum) throw Invalid("units_count_invalid");
        List<FriendlyUnitObservation> result = new();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw Invalid("unit_not_object");
            double damage = ReadRequiredBoundedNumber(item, "damage", 0, 1);
            result.Add(new FriendlyUnitObservation(
                ReadRequiredProtocolString(item, "id"),
                ReadRequiredProtocolString(item, "groupId"),
                ReadOptionalProtocolString(item, "callsign"),
                ReadRequiredSide(item, "side"),
                ReadOptionalProtocolString(item, "class"),
                ReadOptionalProtocolString(item, "role"),
                ReadRequiredVector(item, "positionATL"),
                ReadRequiredBoolean(item, "alive"),
                ReadOptionalProtocolString(item, "lifeState"),
                ReadRequiredBoolean(item, "mobile"),
                damage,
                ReadOptionalProtocolString(item, "vehicleId"),
                ReadOptionalProtocolString(item, "vehicleRole"),
                ReadOptionalProtocolString(item, "medicalReadiness")));
        }
        EnsureUnique(result.Select(item => item.SourceId), "duplicate_unit_id");
        return result;
    }

    private static IReadOnlyList<FriendlyVehicleObservation> ParseVehicles(JsonElement root, string propertyName)
    {
        JsonElement array = ReadRequiredArray(root, propertyName);
        if (array.GetArrayLength() > 512) throw Invalid("vehicles_count_invalid");
        List<FriendlyVehicleObservation> result = new();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw Invalid("friendly_vehicle_not_object");
            result.Add(new FriendlyVehicleObservation(
                ReadRequiredProtocolString(item, "id"),
                ReadRequiredSide(item, "side"),
                ReadOptionalProtocolString(item, "class"),
                ReadOptionalProtocolString(item, "displayName"),
                ReadRequiredVector(item, "positionATL"),
                ReadRequiredBoolean(item, "alive"),
                ReadRequiredBoolean(item, "mobile"),
                ReadRequiredBoundedNumber(item, "damage", 0, 1),
                ReadRequiredBoundedNumber(item, "fuel", 0, 1),
                ReadRequiredFiniteNumber(item, "speedKph"),
                ReadRequiredIdArray(item, "crewUnitIds", 64),
                ReadRequiredInt(item, "cargoCapacity", 0, 256),
                ReadRequiredInt(item, "emptyCargoSeats", 0, 256)));
        }
        EnsureUnique(result.Select(item => item.SourceId), "duplicate_vehicle_id");
        return result;
    }

    private static IReadOnlyList<SupportAssetObservation> ParseAssets(JsonElement root, string propertyName)
    {
        JsonElement array = ReadRequiredArray(root, propertyName);
        if (array.GetArrayLength() > 128) throw Invalid("assets_count_invalid");
        List<SupportAssetObservation> result = new();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw Invalid("asset_not_object");
            string kind = ReadRequiredProtocolString(item, "kind").ToLowerInvariant();
            string status = ReadRequiredProtocolString(item, "status").ToLowerInvariant();
            if (!AllowedAssetKinds.Contains(kind)) throw Invalid("asset_kind_invalid");
            if (!AllowedAssetStatuses.Contains(status)) throw Invalid("asset_status_invalid");
            result.Add(new SupportAssetObservation(
                ReadRequiredProtocolString(item, "id"),
                kind,
                ReadOptionalProtocolString(item, "callsign"),
                ReadOptionalProtocolString(item, "provider"),
                ReadOptionalProtocolString(item, "vehicleId"),
                status,
                ReadRequiredBoolean(item, "available"),
                ReadRequiredInt(item, "capacity", 0, 256)));
        }
        EnsureUnique(result.Select(item => item.SourceId), "duplicate_asset_id");
        return result;
    }

    private TelemetryIngestResult IngestSnapshotPage(FriendlyForceSnapshotPageObservation page)
    {
        TelemetryIngestResult sequenceResult = _store.AcceptProtocolEvent(page.Envelope);
        if (sequenceResult.Status != TelemetryIngestStatus.Applied) return sequenceResult;

        string key = page.Envelope.SessionId + "\0" + page.ReconciliationId;
        if (!_pendingReconciliations.TryGetValue(key, out PendingReconciliation? pending))
        {
            pending = new PendingReconciliation(page, page.Envelope.ReceivedAtUtc);
            _pendingReconciliations.Add(key, pending);
        }
        else if (pending.PageCount != page.PageCount ||
                 pending.GameTime != page.Envelope.GameTime ||
                 !string.Equals(pending.MissionId, page.Envelope.MissionId, StringComparison.Ordinal))
        {
            _pendingReconciliations.Remove(key);
            _store.MarkReconciliationDegraded("inconsistent_snapshot_pages", _pendingReconciliations.Count);
            return Rejected("inconsistent_snapshot_pages");
        }

        if (!pending.Pages.TryAdd(page.PageIndex, page))
            return new TelemetryIngestResult(TelemetryIngestStatus.OutOfOrder, DiagnosticCode: "duplicate_snapshot_page");

        _store.SetPendingReconciliationPages(pending.PageCount - pending.Pages.Count);
        if (pending.Pages.Count != pending.PageCount)
            return new TelemetryIngestResult(TelemetryIngestStatus.Applied, DiagnosticCode: "snapshot_page_buffered");

        FriendlyForceSnapshotPageObservation[] ordered = pending.Pages.Values
            .OrderBy(item => item.PageIndex)
            .ToArray();
        IReadOnlyList<FriendlyGroupObservation> groups = ordered.SelectMany(item => item.Groups).ToArray();
        IReadOnlyList<FriendlyUnitObservation> units = ordered.SelectMany(item => item.Units).ToArray();
        IReadOnlyList<FriendlyVehicleObservation> vehicles = ordered.SelectMany(item => item.Vehicles).ToArray();
        IReadOnlyList<SupportAssetObservation> assets = ordered.SelectMany(item => item.Assets).ToArray();
        try
        {
            EnsureUnique(groups.Select(item => item.SourceId), "duplicate_group_id_across_pages");
            EnsureUnique(units.Select(item => item.SourceId), "duplicate_unit_id_across_pages");
            EnsureUnique(vehicles.Select(item => item.SourceId), "duplicate_vehicle_id_across_pages");
            EnsureUnique(assets.Select(item => item.SourceId), "duplicate_asset_id_across_pages");
        }
        catch (TelemetryFormatException exception)
        {
            _pendingReconciliations.Remove(key);
            _store.MarkReconciliationDegraded(exception.Code,
                _pendingReconciliations.Values.Sum(item => item.PageCount - item.Pages.Count));
            return Rejected(exception.Code);
        }

        _pendingReconciliations.Remove(key);
        _store.SetPendingReconciliationPages(_pendingReconciliations.Values.Sum(item => item.PageCount - item.Pages.Count));
        ProtocolEnvelope envelope = ordered[^1].Envelope with { GameTime = pending.GameTime };
        return _store.ApplyFriendlySnapshot(new FriendlyForceSnapshotObservation(
            envelope, page.ReconciliationId, groups, units, vehicles, assets));
    }

    private TelemetryIngestResult IngestDelta(FriendlyForceDeltaObservation delta)
    {
        TelemetryIngestResult sequenceResult = _store.AcceptProtocolEvent(delta.Envelope);
        return sequenceResult.Status == TelemetryIngestStatus.Applied
            ? _store.ApplyFriendlyDelta(delta)
            : sequenceResult;
    }

    private TelemetryIngestResult IngestCapabilities(MissionCapabilitiesObservation capabilities)
    {
        TelemetryIngestResult sequenceResult = _store.AcceptProtocolEvent(capabilities.Envelope);
        return sequenceResult.Status == TelemetryIngestStatus.Applied
            ? _store.ApplyCapabilities(capabilities)
            : sequenceResult;
    }

    private TelemetryIngestResult IngestGazetteerPage(MapGazetteerPageObservation page)
    {
        if (_operationalMemory is null)
            return new TelemetryIngestResult(TelemetryIngestStatus.Ignored, DiagnosticCode: "operational_memory_unavailable");
        TelemetryIngestResult sequenceResult = _store.AcceptProtocolEvent(page.Envelope);
        if (sequenceResult.Status != TelemetryIngestStatus.Applied) return sequenceResult;
        string key = page.Envelope.SessionId + "\0" + page.GazetteerId;
        if (!_pendingGazetteers.TryGetValue(key, out PendingGazetteer? pending))
        {
            pending = new PendingGazetteer(page, page.Envelope.ReceivedAtUtc);
            _pendingGazetteers.Add(key, pending);
            _operationalMemory.BeginGazetteerReceive();
        }
        else if (pending.PageCount != page.PageCount || pending.GameTime != page.Envelope.GameTime ||
                 !string.Equals(pending.WorldName, page.WorldName, StringComparison.OrdinalIgnoreCase) ||
                 Math.Abs(pending.WorldSizeMeters - page.WorldSizeMeters) > 0.5 ||
                 pending.GridSignature != JsonSerializer.Serialize(page.GridSamples))
        {
            _pendingGazetteers.Remove(key);
            _operationalMemory.MarkGazetteerFailed("inconsistent_gazetteer_pages");
            return Rejected("inconsistent_gazetteer_pages");
        }
        if (!pending.Pages.TryAdd(page.PageIndex, page))
            return new TelemetryIngestResult(TelemetryIngestStatus.OutOfOrder, DiagnosticCode: "duplicate_gazetteer_page");
        if (pending.Pages.Count != pending.PageCount)
            return new TelemetryIngestResult(TelemetryIngestStatus.Applied, DiagnosticCode: "gazetteer_page_buffered");

        MapGazetteerPageObservation[] ordered = pending.Pages.Values.OrderBy(item => item.PageIndex).ToArray();
        IReadOnlyList<GazetteerLocationObservation> locations = ordered.SelectMany(item => item.Locations).ToArray();
        try
        {
            if (locations.Count > 4096) throw Invalid("gazetteer_locations_count_invalid");
            EnsureUnique(locations.Select(item => item.ConfigKey), "duplicate_gazetteer_config_key_across_pages");
            EnsureUnique(locations.Select(GazetteerRecordKey), "duplicate_gazetteer_record_across_pages");
        }
        catch (TelemetryFormatException exception)
        {
            _pendingGazetteers.Remove(key);
            return Rejected(exception.Code);
        }
        _pendingGazetteers.Remove(key);
        ProtocolEnvelope envelope = ordered[^1].Envelope with { GameTime = pending.GameTime };
        return _operationalMemory.ApplyGazetteer(new MapGazetteerObservation(
            envelope, page.GazetteerId, page.WorldName, page.WorldSizeMeters,
            ordered[0].GridSamples, locations));
    }

    private TelemetryIngestResult IngestOperationalObservations(OperationalObservationBatch batch)
    {
        if (_operationalMemory is null)
            return new TelemetryIngestResult(TelemetryIngestStatus.Ignored, DiagnosticCode: "operational_memory_unavailable");
        TelemetryIngestResult sequenceResult = _store.AcceptProtocolEvent(batch.Envelope);
        return sequenceResult.Status == TelemetryIngestStatus.Applied
            ? _operationalMemory.ApplyObservationBatch(batch)
            : sequenceResult;
    }

    private void ExpirePendingReconciliations(DateTimeOffset now)
    {
        string[] expired = _pendingReconciliations
            .Where(pair => now - pair.Value.FirstReceivedAtUtc > SnapshotAssemblyTimeout)
            .Select(pair => pair.Key)
            .ToArray();
        if (expired.Length > 0)
        {
            foreach (string key in expired) _pendingReconciliations.Remove(key);
            _store.MarkReconciliationDegraded("incomplete_snapshot_expired",
                _pendingReconciliations.Values.Sum(item => item.PageCount - item.Pages.Count));
        }

        string[] expiredGazetteers = _pendingGazetteers
            .Where(pair => now - pair.Value.FirstReceivedAtUtc > SnapshotAssemblyTimeout)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (string key in expiredGazetteers) _pendingGazetteers.Remove(key);
        if (expiredGazetteers.Length > 0)
            _operationalMemory?.MarkGazetteerFailed("incomplete_gazetteer_expired");
    }

    private static string BuildContactSignature(ContactObservation value)
        => string.Join("|", new[]
        {
            value.Class,
            value.DisplayName,
            value.KnownByPlayer ? "1" : "0",
            value.KnownByGroup ? "1" : "0",
            Format(value.LastSeenAgeSeconds),
            Format(value.LastThreatAgeSeconds),
            value.PerceivedSide,
            Format(value.PositionErrorMeters),
            value.EstimatedPosition is null ? string.Empty :
                $"{Format(value.EstimatedPosition.X)},{Format(value.EstimatedPosition.Y)},{Format(value.EstimatedPosition.Z)}",
            value.Ignored ? "1" : "0"
        });

    private static string Format(double? value)
        => value?.ToString("R", CultureInfo.InvariantCulture) ?? "null";

    private static string GazetteerRecordKey(GazetteerLocationObservation location)
        => string.Join('|',
            location.OfficialName.Trim().ToUpperInvariant(),
            location.LocationType.Trim().ToUpperInvariant(),
            Format(location.Position.X),
            Format(location.Position.Y));

    private static void EnsureClosedObject(JsonElement value, string code, params string[] allowedProperties)
    {
        HashSet<string> allowed = new(allowedProperties, StringComparer.Ordinal);
        if (value.EnumerateObject().Any(property => !allowed.Contains(property.Name))) throw Invalid(code);
    }

    private static JsonElement ReadRequiredObject(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw Invalid($"{name}_not_object");

    private static JsonElement ReadRequiredArray(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value
            : throw Invalid($"{name}_not_array");

    private static long ReadRequiredLong(JsonElement parent, string name, long minimum)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long result) || result < minimum)
        {
            throw Invalid($"{name}_not_integer");
        }
        return result;
    }

    private static int ReadRequiredInt(JsonElement parent, string name, int minimum, int maximum)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int result) || result < minimum || result > maximum)
        {
            throw Invalid($"{name}_not_integer");
        }
        return result;
    }

    private static bool ReadRequiredBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)) throw Invalid($"{name}_missing");
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid($"{name}_not_boolean")
        };
    }

    private static double ReadRequiredBoundedNumber(
        JsonElement parent, string name, double minimum, double maximum)
    {
        double result = ReadRequiredFiniteNumber(parent, name);
        return result >= minimum && result <= maximum
            ? result
            : throw Invalid($"{name}_out_of_range");
    }

    private static string ReadRequiredSide(JsonElement parent, string name)
    {
        string value = ReadRequiredProtocolString(parent, name).ToUpperInvariant();
        return AllowedSides.Contains(value) ? value : throw Invalid($"{name}_invalid");
    }

    private static IReadOnlyList<string> ReadRequiredSideArray(JsonElement parent, string name)
    {
        JsonElement array = ReadRequiredArray(parent, name);
        if (array.GetArrayLength() > 4) throw Invalid($"{name}_count_invalid");
        List<string> result = new();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw Invalid($"{name}_item_not_string");
            string side = (item.GetString() ?? string.Empty).Trim().ToUpperInvariant();
            if (!AllowedSides.Contains(side)) throw Invalid($"{name}_item_invalid");
            result.Add(side);
        }
        EnsureUnique(result, $"{name}_duplicate");
        return result;
    }

    private static IReadOnlyList<string> ReadRequiredIdArray(
        JsonElement parent, string name, int maximum = MaximumProtocolEntitiesPerMessage)
    {
        JsonElement array = ReadRequiredArray(parent, name);
        if (array.GetArrayLength() > maximum) throw Invalid($"{name}_count_invalid");
        List<string> result = new();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw Invalid($"{name}_item_not_string");
            string id = (item.GetString() ?? string.Empty).Trim();
            if (id.Length is < 1 or > 128) throw Invalid($"{name}_item_invalid");
            result.Add(id);
        }
        EnsureUnique(result, $"{name}_duplicate");
        return result;
    }

    private static void EnsureUnique(IEnumerable<string> values, string diagnosticCode)
    {
        HashSet<string> unique = new(StringComparer.Ordinal);
        foreach (string value in values)
        {
            if (!unique.Add(value)) throw Invalid(diagnosticCode);
        }
    }

    private static string ReadRequiredString(JsonElement parent, string name)
    {
        string value = ReadOptionalString(parent, name);
        return value.Length > 0 ? value : throw Invalid($"{name}_missing");
    }

    private static string ReadOptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return string.Empty;
        if (value.ValueKind != JsonValueKind.String) throw Invalid($"{name}_not_string");
        string result = (value.GetString() ?? string.Empty).Trim();
        return result.Length <= 256 ? result : result[..256];
    }

    private static string ReadRequiredProtocolString(JsonElement parent, string name, int maximum = 128)
    {
        string value = ReadOptionalProtocolString(parent, name, maximum);
        return value.Length > 0 ? value : throw Invalid($"{name}_missing");
    }

    private static string ReadOptionalProtocolString(JsonElement parent, string name, int maximum = 128)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return string.Empty;
        if (value.ValueKind != JsonValueKind.String) throw Invalid($"{name}_not_string");
        string result = (value.GetString() ?? string.Empty).Trim();
        if (result.Any(char.IsControl)) throw Invalid($"{name}_control_character");
        return result.Length <= maximum ? result : throw Invalid($"{name}_too_long");
    }

    private static double ReadRequiredFiniteNumber(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double result) || !double.IsFinite(result))
        {
            throw Invalid($"{name}_not_number");
        }
        return result;
    }

    private static double ReadOptionalFiniteNumber(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result) || !double.IsFinite(result))
            throw Invalid($"{name}_not_number");
        return result;
    }

    private static double? ReadOptionalNonNegativeNumber(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return null;
        double result = ReadRequiredFiniteNumber(parent, name);
        return result >= 0 ? result : null;
    }

    private static double? ReadOptionalAge(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return null;
        double result = ReadRequiredFiniteNumber(parent, name);
        return result >= 0 ? result : null;
    }

    private static int ReadOptionalInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
            throw Invalid($"{name}_not_integer");
        return result;
    }

    private static long ReadOptionalFrame(JsonElement parent)
    {
        if (!parent.TryGetProperty("frame", out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return -1;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long frame))
            throw Invalid("frame_not_integer");
        return frame;
    }

    private static bool ReadOptionalBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid($"{name}_not_boolean")
        };
    }

    private static WorldPosition ReadRequiredVector(JsonElement parent, string name)
        => ReadOptionalVector(parent, name) ?? throw Invalid($"{name}_missing");

    private static WorldPosition ReadRequiredVector2(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
            throw Invalid($"{name}_not_vector2");
        double[] values = value.EnumerateArray().Select(ReadFiniteVectorItem).ToArray();
        return new WorldPosition(values[0], values[1], 0);
    }

    private static (double? X, double? Y) ReadOptionalVector2(
        JsonElement parent, string name, double minimum, double maximum)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return (null, null);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
            throw Invalid($"{name}_not_vector2");
        double[] values = value.EnumerateArray().Select(ReadFiniteVectorItem).ToArray();
        if (values.Any(item => item < minimum || item > maximum)) throw Invalid($"{name}_out_of_range");
        return (values[0], values[1]);
    }

    private static WorldPosition? ReadOptionalVector(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3)
            throw Invalid($"{name}_not_vector3");
        double[] values = value.EnumerateArray().Select(ReadFiniteVectorItem).ToArray();
        return new WorldPosition(values[0], values[1], values[2]);
    }

    private static double ReadFiniteVectorItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double value) || !double.IsFinite(value))
            throw Invalid("vector_item_not_number");
        return value;
    }

    private static string[] ReadStringArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return Array.Empty<string>();
        if (value.ValueKind != JsonValueKind.Array) throw Invalid($"{name}_not_array");
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => (item.GetString() ?? string.Empty).Trim())
            .Where(item => item.Length > 0)
            .Select(item => item.Length <= 128 ? item : item[..128])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .Take(32)
            .ToArray();
    }

    private static string ReadVehicleRole(JsonElement parent)
    {
        if (!parent.TryGetProperty("role", out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return string.Empty;
        if (value.ValueKind == JsonValueKind.String)
            return ReadOptionalString(parent, "role");
        if (value.ValueKind != JsonValueKind.Array) throw Invalid("role_not_string_or_array");

        JsonElement[] parts = value.EnumerateArray().ToArray();
        string role = parts.FirstOrDefault().ValueKind == JsonValueKind.String
            ? parts[0].GetString() ?? string.Empty
            : string.Empty;
        if (parts.Length < 2 || parts[1].ValueKind != JsonValueKind.Array) return role;
        string turretPath = string.Join(",", parts[1].EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out _))
            .Select(item => item.GetInt32().ToString(CultureInfo.InvariantCulture)));
        return turretPath.Length == 0 ? role : $"{role} [{turretPath}]";
    }

    private void OnMessageReceived(string json)
    {
        TelemetryIngestResult result = Ingest(json);
        if (result.Status == TelemetryIngestStatus.Rejected)
        {
            _log?.Warn($"World telemetry rejected: code={result.DiagnosticCode}.");
        }
        else if (result.SessionReset)
        {
            _log?.Info($"World state session started: reason={result.ResetReason}, contacts={result.KnownContactCount}.");
        }
    }

    private void OnClientConnectionChanged(bool connected) => _store.SetConnected(connected);

    private static TelemetryIngestResult Rejected(string code)
        => new(TelemetryIngestStatus.Rejected, DiagnosticCode: code);

    private static TelemetryFormatException Invalid(string code) => new(code);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_pipe is not null)
        {
            _pipe.MessageReceived -= OnMessageReceived;
            _pipe.ClientConnectionChanged -= OnClientConnectionChanged;
        }
    }

    private sealed class TelemetryFormatException : FormatException
    {
        public TelemetryFormatException(string code) : base(code) => Code = code;
        public string Code { get; }
    }

    private sealed class PendingReconciliation
    {
        public PendingReconciliation(FriendlyForceSnapshotPageObservation firstPage, DateTimeOffset firstReceivedAtUtc)
        {
            PageCount = firstPage.PageCount;
            GameTime = firstPage.Envelope.GameTime;
            MissionId = firstPage.Envelope.MissionId;
            FirstReceivedAtUtc = firstReceivedAtUtc;
        }

        public int PageCount { get; }
        public double GameTime { get; }
        public string MissionId { get; }
        public DateTimeOffset FirstReceivedAtUtc { get; }
        public Dictionary<int, FriendlyForceSnapshotPageObservation> Pages { get; } = new();
    }

    private sealed class PendingGazetteer
    {
        public PendingGazetteer(MapGazetteerPageObservation firstPage, DateTimeOffset firstReceivedAtUtc)
        {
            PageCount = firstPage.PageCount;
            GameTime = firstPage.Envelope.GameTime;
            WorldName = firstPage.WorldName;
            WorldSizeMeters = firstPage.WorldSizeMeters;
            GridSignature = JsonSerializer.Serialize(firstPage.GridSamples);
            FirstReceivedAtUtc = firstReceivedAtUtc;
        }
        public int PageCount { get; }
        public double GameTime { get; }
        public string WorldName { get; }
        public double WorldSizeMeters { get; }
        public string GridSignature { get; }
        public DateTimeOffset FirstReceivedAtUtc { get; }
        public Dictionary<int, MapGazetteerPageObservation> Pages { get; } = new();
    }
}
