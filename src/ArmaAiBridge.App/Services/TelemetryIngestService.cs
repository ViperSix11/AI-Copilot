using System.Globalization;
using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class TelemetryIngestService : IDisposable
{
    public const string TelemetrySchema = "arma-ai-bridge/arma3/telemetry-v1";
    private const int MaximumContactsPerMessage = 128;
    private readonly WorldStateStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly TelemetryPipeServer? _pipe;
    private readonly LogService? _log;
    private bool _disposed;

    public TelemetryIngestService(WorldStateStore store, TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public TelemetryIngestService(
        TelemetryPipeServer pipe,
        WorldStateStore store,
        LogService log,
        TimeProvider? timeProvider = null)
        : this(store, timeProvider)
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
            if (!string.Equals(schema, TelemetrySchema, StringComparison.Ordinal))
                return new TelemetryIngestResult(TelemetryIngestStatus.Ignored, DiagnosticCode: "unrelated_schema");

            TelemetryObservation observation = ParseObservation(root, _timeProvider.GetUtcNow());
            return _store.Apply(observation);
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

    private static TelemetryObservation ParseObservation(JsonElement root, DateTimeOffset receivedAtUtc)
    {
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
            ReadOptionalString(playerObject, "side"),
            ReadOptionalString(playerObject, "group"),
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
            gameTime, frame, receivedAtUtc, map, player, vehicle, contacts, sensorContacts, skipped);
    }

    private static VehicleObservation? ParseVehicle(JsonElement root)
    {
        if (!root.TryGetProperty("vehicle", out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("vehicle_not_object");
        return new VehicleObservation(
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

    private static JsonElement ReadRequiredObject(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw Invalid($"{name}_not_object");

    private static JsonElement ReadRequiredArray(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value
            : throw Invalid($"{name}_not_array");

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
}
