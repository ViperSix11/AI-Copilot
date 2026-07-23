using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class MapGazetteerCoordinator : IDisposable
{
    private const string HandshakeSchema = "arma-ai-bridge/arma3/session-handshake-v1";
    private const string CommandSchema = "arma-ai-bridge/command-v1";
    private readonly TelemetryPipeServer _pipe;
    private readonly WorldStateStore _worldStore;
    private readonly MapGazetteerStore _gazetteerStore;
    private readonly LogService _log;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private string _requestedIdentity = string.Empty;
    private bool _disposed;

    public MapGazetteerCoordinator(
        TelemetryPipeServer pipe,
        WorldStateStore worldStore,
        MapGazetteerStore gazetteerStore,
        LogService log)
    {
        _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
        _worldStore = worldStore ?? throw new ArgumentNullException(nameof(worldStore));
        _gazetteerStore = gazetteerStore ?? throw new ArgumentNullException(nameof(gazetteerStore));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _pipe.MessageReceived += OnMessage;
    }

    private void OnMessage(string json)
    {
        if (_disposed) return;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string schema = ReadString(root, "schema");
            if (schema == HandshakeSchema)
            {
                HandleHandshake(root);
            }
            else if (schema == MapGazetteerStore.Schema)
            {
                ProtocolEnvelope envelope = ParseEnvelope(root);
                TelemetryIngestResult protocol = _worldStore.AcceptProtocolEvent(envelope);
                if (protocol.Status is TelemetryIngestStatus.Applied or TelemetryIngestStatus.OutOfOrder)
                {
                    GazetteerIngestResult result = _gazetteerStore.Ingest(json);
                    if (!result.Applied)
                        _log.Warn($"Map gazetteer page rejected: code={result.DiagnosticCode}.");
                    else if (result.Activated)
                        _log.Info("Map gazetteer activated from a complete validated batch.");
                }
            }
        }
        catch (JsonException)
        {
        }
        catch (Exception exception)
        {
            _log.Warn($"Map gazetteer message rejected: exceptionType={exception.GetType().Name}.");
        }
    }

    private void HandleHandshake(JsonElement root)
    {
        JsonElement features = RequiredArray(root, "features");
        bool supported = features.EnumerateArray().Any(item =>
            ReadString(item, "name") == "map-gazetteer" && ReadInt(item, "version") == 1);
        string sourceSessionId = RequiredString(root, "sessionId");
        string sourceMissionId = RequiredString(root, "missionId");
        JsonElement world = RequiredObject(root, "world");
        string worldName = RequiredString(world, "name");
        double worldSize = RequiredNumber(world, "sizeMeters");
        string identity = $"{sourceSessionId}\0{sourceMissionId}\0{worldName.ToUpperInvariant()}\0{worldSize:R}";
        if (identity == _requestedIdentity) return;
        _gazetteerStore.Reset();
        if (!supported)
        {
            _requestedIdentity = identity;
            return;
        }
        _ = RequestAsync(identity, sourceSessionId, sourceMissionId, worldName, worldSize);
    }

    private async Task RequestAsync(
        string identity,
        string sourceSessionId,
        string sourceMissionId,
        string worldName,
        double worldSize)
    {
        await _requestGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || identity == _requestedIdentity) return;
            string requestId = Guid.NewGuid().ToString("N");
            _gazetteerStore.BeginRequest(
                sourceSessionId, sourceMissionId, worldName, worldSize, requestId);
            string command = JsonSerializer.Serialize(new
            {
                schema = CommandSchema,
                requestId,
                command = "request_map_gazetteer",
                parameters = new { }
            });
            if (!await _pipe.SendCommandAsync(command).ConfigureAwait(false))
            {
                _gazetteerStore.MarkFailed("gazetteer_request_not_sent");
                return;
            }
            _requestedIdentity = identity;
            _log.Info("Map gazetteer requested for the active session.");
        }
        catch (Exception exception)
        {
            _gazetteerStore.MarkFailed("gazetteer_request_failed");
            _log.Warn($"Map gazetteer request failed: exceptionType={exception.GetType().Name}.");
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static ProtocolEnvelope ParseEnvelope(JsonElement root)
        => new(
            RequiredString(root, "messageId"),
            RequiredString(root, "missionId"),
            RequiredString(root, "sessionId"),
            RequiredNumber(root, "timestamp"),
            RequiredLong(root, "sequence"),
            DateTimeOffset.UtcNow);

    private static JsonElement RequiredObject(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            ? value : throw new JsonException();
    private static JsonElement RequiredArray(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value : throw new JsonException();
    private static string RequiredString(JsonElement root, string name)
    {
        string value = ReadString(root, name);
        return value.Length > 0 && value.Length <= 128 && !value.Any(char.IsControl) ? value : throw new JsonException();
    }
    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty).Trim() : string.Empty;
    private static int ReadInt(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : -1;
    private static long RequiredLong(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result) && result > 0
            ? result : throw new JsonException();
    private static double RequiredNumber(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double result) && double.IsFinite(result) && result >= 0
            ? result : throw new JsonException();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pipe.MessageReceived -= OnMessage;
        _requestGate.Dispose();
    }
}
