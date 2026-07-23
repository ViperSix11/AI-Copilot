using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class StateMirrorIngestService : IDisposable
{
    public const string HandshakeSchema = "arma-ai-bridge/arma3/session-handshake-v1";
    private readonly TelemetryPipeServer? _pipe;
    private readonly SqliteStateRepository _repository;
    private readonly TelemetryIngestService? _legacyProjection;
    private readonly MapGazetteerStore? _gazetteer;
    private readonly LogService? _log;
    private readonly TimeProvider _timeProvider;
    private string _worldName = string.Empty;
    private double _worldSize;
    private bool _disposed;

    public StateMirrorIngestService(
        SqliteStateRepository repository,
        TelemetryIngestService? legacyProjection = null,
        MapGazetteerStore? gazetteer = null,
        TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _legacyProjection = legacyProjection;
        _gazetteer = gazetteer;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_gazetteer is not null) _gazetteer.Changed += OnGazetteerChanged;
    }

    public StateMirrorIngestService(
        TelemetryPipeServer pipe,
        SqliteStateRepository repository,
        TelemetryIngestService legacyProjection,
        MapGazetteerStore gazetteer,
        LogService log,
        TimeProvider? timeProvider = null)
        : this(repository, legacyProjection, gazetteer, timeProvider)
    {
        _pipe = pipe; _log = log;
        _pipe.MessageReceived += OnMessageReceived;
    }

    public static bool IsStateMirrorSchema(string schema)
        => schema is HandshakeSchema or StateSnapshotParser.Schema;

    public StateIngestResult Ingest(string json)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string schema = Text(root, "schema");
            DateTimeOffset received = _timeProvider.GetUtcNow();
            if (schema == HandshakeSchema) return IngestHandshake(root, received);
            if (schema != StateSnapshotParser.Schema) return new StateIngestResult(TelemetryIngestStatus.Ignored, "unrelated_schema");
            StateSnapshotMessage snapshot = StateSnapshotParser.Parse(root, received);
            StateIngestResult result = _repository.ApplySnapshot(snapshot);
            if (result.Status == TelemetryIngestStatus.Applied) ProjectLegacy(snapshot);
            return result;
        }
        catch (JsonException) { return new StateIngestResult(TelemetryIngestStatus.Rejected, "state_invalid_json"); }
        catch (InvalidDataException exception) { return new StateIngestResult(TelemetryIngestStatus.Rejected, SafeCode(exception.Message)); }
    }

    private StateIngestResult IngestHandshake(JsonElement root, DateTimeOffset received)
    {
        string missionId = Required(root, "missionId"); string sessionId = Required(root, "sessionId");
        JsonElement world = root.GetProperty("world");
        _worldName = Required(world, "name"); _worldSize = world.GetProperty("sizeMeters").GetDouble();
        bool feature = root.TryGetProperty("features", out JsonElement features) && features.ValueKind == JsonValueKind.Array &&
            features.EnumerateArray().Any(item => Text(item, "name") == "state-snapshot" && item.TryGetProperty("version", out JsonElement version) && version.GetInt32() == 2);
        if (!feature) return new StateIngestResult(TelemetryIngestStatus.Rejected, "state_feature_missing");
        StateIngestResult result = _repository.ApplyHandshake(missionId, sessionId, _worldName, _worldSize, received);
        OnGazetteerChanged();
        return result;
    }

    private void ProjectLegacy(StateSnapshotMessage snapshot)
    {
        if (_legacyProjection is null || _worldName.Length == 0) return;
        JsonElement player = snapshot.Sections["player"].Payload;
        if (snapshot.Sections["player"].Readiness is StateSectionReadiness.Failed or StateSectionReadiness.Unavailable) return;
        JsonElement time = snapshot.Sections["timeAstronomy"].Payload;
        JsonElement loadout = snapshot.Sections["loadout"].Payload;
        JsonElement contacts = snapshot.Sections["knownContacts"].Payload;
        object[] contactProjection = contacts.TryGetProperty("contacts", out JsonElement values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Take(128).Select(item => (object)new
            {
                id = Text(item, "sourceId"), @class = Text(item, "class"), displayName = Text(item, "displayName"),
                knownByPlayer = false, knownByGroup = true,
                lastSeenAgeSeconds = Number(item, "lastSeenAgeSeconds", -1),
                lastThreatAgeSeconds = Number(item, "lastThreatAgeSeconds", -1),
                perceivedSide = Text(item, "perceivedSide"),
                positionErrorMeters = Number(item, "positionErrorMeters", 0),
                estimatedPosition = RawArray(item, "estimatedPosition"), ignored = false
            }).ToArray() : Array.Empty<object>();
        string legacy = JsonSerializer.Serialize(new
        {
            schema = TelemetryIngestService.TelemetrySchema,
            missionId = snapshot.MissionId, sessionId = snapshot.SessionId,
            timestamp = snapshot.PublishedAtGameTime, frame = snapshot.Sequence,
            map = new { name = _worldName, sizeMeters = _worldSize, grid = Text(player, "grid"), daytime = Number(time, "daytime", 0) },
            player = new
            {
                id = Text(player, "sourceId"), side = Text(player, "side"), group = "", groupId = Text(player, "groupSourceId"),
                positionATL = RawArray(player, "positionATL"), positionASL = RawArray(player, "positionASL"),
                bodyHeading = 0, viewHeading = 0, speedKph = 0, damage = 0, lifeState = "", stance = "",
                weapon = Text(loadout, "selectedWeapon"), magazine = Text(loadout, "currentMagazine"),
                muzzle = Text(loadout, "muzzle"), loadedRounds = (int)Number(loadout, "loadedRounds", 0),
                matchingMagazineCount = 0, matchingMagazineRounds = 0
            },
            vehicle = (object?)null, contacts = contactProjection, sensorContacts = Array.Empty<object>()
        });
        _legacyProjection.Ingest(legacy);
    }

    private void OnGazetteerChanged()
    {
        if (_disposed || _gazetteer is null) return;
        _repository.ReplaceNamedLocations(_gazetteer.GetSnapshot());
    }

    private void OnMessageReceived(string json)
    {
        StateIngestResult result = Ingest(json);
        if (result.Status == TelemetryIngestStatus.Rejected)
            _log?.Warn($"State Mirror message rejected: code={SafeCode(result.DiagnosticCode)}.");
        else if (result.Status == TelemetryIngestStatus.Applied && json.Contains(StateSnapshotParser.Schema, StringComparison.Ordinal))
            _log?.Info("State Mirror snapshot accepted.");
    }

    private static string Required(JsonElement root, string name)
    {
        string value = Text(root, name); if (value.Length == 0 || value.Length > 128) throw new InvalidDataException("state_handshake_invalid"); return value;
    }
    private static string Text(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    private static double Number(JsonElement root, string name, double fallback)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double result) ? result : fallback;
    private static double[] RawArray(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Take(3).Select(item => item.GetDouble()).ToArray() : new[] { 0d, 0d, 0d };
    private static string SafeCode(string value)
        => value.Length is > 0 and <= 80 && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_') ? value : "state_message_invalid";

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_pipe is not null) _pipe.MessageReceived -= OnMessageReceived;
        if (_gazetteer is not null) _gazetteer.Changed -= OnGazetteerChanged;
    }
}
