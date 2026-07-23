using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class MapGazetteerStore
{
    public const string Schema = "arma-ai-bridge/arma3/map-gazetteer-v1";
    public const int MaximumLocations = 8192;
    public const int MaximumLocationsPerPage = 128;
    public const int MaximumPages = 64;
    private static readonly TimeSpan AssemblyTimeout = TimeSpan.FromSeconds(10);
    private static readonly HashSet<string> WireFailureCodes = new(StringComparer.Ordinal)
    {
        "gazetteer_limit_exceeded",
        "gazetteer_collection_failed",
        "gazetteer_collection_in_progress",
        "gazetteer_config_invalid"
    };

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private string _sourceSessionId = string.Empty;
    private string _sourceMissionId = string.Empty;
    private string _worldName = string.Empty;
    private double _worldSizeMeters;
    private string _requestId = string.Empty;
    private MapGazetteerReadiness _readiness;
    private IReadOnlyList<MapGazetteerLocation> _active = Array.Empty<MapGazetteerLocation>();
    private PendingBatch? _pending;
    private DateTimeOffset? _requestedAtUtc;
    private DateTimeOffset? _completedAtUtc;
    private string _diagnosticCode = string.Empty;

    public MapGazetteerStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event Action? Changed;

    public void BeginRequest(
        string sourceSessionId,
        string sourceMissionId,
        string worldName,
        double worldSizeMeters,
        string requestId)
    {
        if (string.IsNullOrWhiteSpace(sourceSessionId) || string.IsNullOrWhiteSpace(sourceMissionId) ||
            string.IsNullOrWhiteSpace(worldName) || string.IsNullOrWhiteSpace(requestId) ||
            !double.IsFinite(worldSizeMeters) || worldSizeMeters <= 0)
            throw new ArgumentException("Gazetteer request identity is invalid.");

        lock (_gate)
        {
            bool changed = !string.Equals(_sourceSessionId, sourceSessionId, StringComparison.Ordinal) ||
                           !string.Equals(_sourceMissionId, sourceMissionId, StringComparison.Ordinal) ||
                           !string.Equals(_worldName, worldName, StringComparison.OrdinalIgnoreCase) ||
                           Math.Abs(_worldSizeMeters - worldSizeMeters) > 0.5;
            if (changed)
            {
                _active = Array.Empty<MapGazetteerLocation>();
                _completedAtUtc = null;
            }
            _sourceSessionId = sourceSessionId;
            _sourceMissionId = sourceMissionId;
            _worldName = worldName;
            _worldSizeMeters = worldSizeMeters;
            _requestId = requestId;
            _pending = null;
            _readiness = MapGazetteerReadiness.Requesting;
            _requestedAtUtc = _timeProvider.GetUtcNow();
            _diagnosticCode = string.Empty;
        }
        Changed?.Invoke();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _sourceSessionId = string.Empty;
            _sourceMissionId = string.Empty;
            _worldName = string.Empty;
            _worldSizeMeters = 0;
            _requestId = string.Empty;
            _active = Array.Empty<MapGazetteerLocation>();
            _pending = null;
            _readiness = MapGazetteerReadiness.Unavailable;
            _requestedAtUtc = null;
            _completedAtUtc = null;
            _diagnosticCode = string.Empty;
        }
        Changed?.Invoke();
    }

    public void MarkFailed(string diagnosticCode)
    {
        lock (_gate)
        {
            _pending = null;
            _readiness = MapGazetteerReadiness.Failed;
            _diagnosticCode = string.IsNullOrWhiteSpace(diagnosticCode) ? "gazetteer_failed" : diagnosticCode;
        }
        Changed?.Invoke();
    }

    public GazetteerIngestResult Ingest(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ReadString(root, "schema") != Schema)
                return Reject("unrelated_schema");

            Page page = ParsePage(root);
            bool activated = false;
            string diagnostic = string.Empty;
            lock (_gate)
            {
                ExpirePendingLocked();
                if (!MatchesActiveRequest(page)) return RejectLocked("gazetteer_identity_mismatch");
                if (page.Status == "failed")
                {
                    _pending = null;
                    _readiness = MapGazetteerReadiness.Failed;
                    _diagnosticCode = page.ErrorCode;
                    diagnostic = page.ErrorCode;
                }
                else
                {
                    if (_pending is null)
                    {
                        _pending = new PendingBatch(
                            page.BatchId, page.PageCount, page.TotalLocations,
                            _timeProvider.GetUtcNow(), new Dictionary<int, IReadOnlyList<MapGazetteerLocation>>());
                    }
                    if (!_pending.Matches(page)) return RejectLocked("gazetteer_batch_mismatch");
                    if (!_pending.Pages.TryAdd(page.PageIndex, page.Locations))
                        return RejectLocked("gazetteer_duplicate_page");
                    _readiness = MapGazetteerReadiness.Assembling;
                    if (_pending.Pages.Count == _pending.PageCount)
                    {
                        MapGazetteerLocation[] locations = _pending.Pages
                            .OrderBy(item => item.Key)
                            .SelectMany(item => item.Value)
                            .ToArray();
                        if (locations.Length != _pending.TotalLocations)
                            return RejectLocked("gazetteer_total_mismatch");
                        HashSet<string> keys = new(StringComparer.Ordinal);
                        if (locations.Any(item => !keys.Add(item.Key)))
                            return RejectLocked("gazetteer_duplicate_key");
                        _active = Array.AsReadOnly(locations);
                        _pending = null;
                        _completedAtUtc = _timeProvider.GetUtcNow();
                        _readiness = locations.Length == 0
                            ? MapGazetteerReadiness.Empty
                            : MapGazetteerReadiness.Ready;
                        _diagnosticCode = string.Empty;
                        activated = true;
                    }
                }
            }
            Changed?.Invoke();
            return new GazetteerIngestResult(true, activated, diagnostic);
        }
        catch (JsonException) { return Reject("gazetteer_invalid_json"); }
        catch (GazetteerFormatException exception) { return Reject(exception.Code); }
        catch (OverflowException) { return Reject("gazetteer_number_out_of_range"); }
    }

    public MapGazetteerSnapshot GetSnapshot()
    {
        bool changed;
        MapGazetteerSnapshot snapshot;
        lock (_gate)
        {
            changed = ExpirePendingLocked();
            snapshot = new MapGazetteerSnapshot(
                _readiness, _worldName, _worldSizeMeters, _active.ToArray(), _diagnosticCode);
        }
        if (changed) Changed?.Invoke();
        return snapshot;
    }

    public MapGazetteerDiagnostics GetDiagnostics()
    {
        lock (_gate)
        {
            ExpirePendingLocked();
            return new MapGazetteerDiagnostics(
                _readiness, _worldName, _pending?.Pages.Count ?? 0, _pending?.PageCount ?? 0,
                _active.Count, _requestedAtUtc, _completedAtUtc, _diagnosticCode);
        }
    }

    private bool MatchesActiveRequest(Page page)
        => string.Equals(page.SessionId, _sourceSessionId, StringComparison.Ordinal) &&
           string.Equals(page.MissionId, _sourceMissionId, StringComparison.Ordinal) &&
           string.Equals(page.RequestId, _requestId, StringComparison.Ordinal) &&
           string.Equals(page.WorldName, _worldName, StringComparison.OrdinalIgnoreCase) &&
           Math.Abs(page.WorldSizeMeters - _worldSizeMeters) <= 0.5;

    private bool ExpirePendingLocked()
    {
        if (_pending is null || _timeProvider.GetUtcNow() - _pending.ReceivedAtUtc <= AssemblyTimeout)
            return false;
        _pending = null;
        _readiness = MapGazetteerReadiness.Failed;
        _diagnosticCode = "gazetteer_incomplete_batch";
        return true;
    }

    private GazetteerIngestResult Reject(string code)
    {
        lock (_gate) return RejectLocked(code);
    }

    private GazetteerIngestResult RejectLocked(string code)
    {
        if (code != "unrelated_schema")
        {
            _pending = null;
            _readiness = MapGazetteerReadiness.Failed;
            _diagnosticCode = code;
        }
        return new GazetteerIngestResult(false, false, code);
    }

    private static Page ParsePage(JsonElement root)
    {
        _ = Required(root, "messageId", 128);
        _ = PositiveLong(root, "sequence");
        string missionId = Required(root, "missionId", 128);
        string sessionId = Required(root, "sessionId", 128);
        string requestId = Required(root, "requestId", 128);
        string batchId = Required(root, "batchId", 128);
        double gameTime = Number(root, "timestamp", 0, double.MaxValue);
        int pageIndex = Integer(root, "pageIndex", 0, MaximumPages - 1);
        int pageCount = Integer(root, "pageCount", 1, MaximumPages);
        if (pageIndex >= pageCount) throw Invalid("gazetteer_page_index_invalid");
        int total = Integer(root, "totalLocations", 0, MaximumLocations);
        string status = Required(root, "status", 16).ToLowerInvariant();
        string error = Optional(root, "errorCode", 64);
        JsonElement world = Object(root, "world");
        string worldName = Required(world, "name", 128);
        double worldSize = Number(world, "sizeMeters", 1, 500000);
        JsonElement array = RequiredArray(root, "locations");
        if (array.GetArrayLength() > MaximumLocationsPerPage) throw Invalid("gazetteer_page_oversized");
        List<MapGazetteerLocation> locations = new();
        foreach (JsonElement item in array.EnumerateArray()) locations.Add(ParseLocation(item));
        if (status == "failed")
        {
            if (pageCount != 1 || pageIndex != 0 || total != 0 || locations.Count != 0 ||
                !WireFailureCodes.Contains(error))
                throw Invalid("gazetteer_failure_shape_invalid");
        }
        else if (status == "complete")
        {
            if (error.Length != 0) throw Invalid("gazetteer_success_error_invalid");
            if (total == 0 && pageCount != 1) throw Invalid("gazetteer_page_count_invalid");
        }
        else throw Invalid("gazetteer_status_invalid");
        return new Page(missionId, sessionId, requestId, batchId, gameTime, pageIndex,
            pageCount, total, status, error, worldName, worldSize, locations);
    }

    private static MapGazetteerLocation ParseLocation(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) throw Invalid("gazetteer_location_not_object");
        string key = Required(item, "key", 128);
        string name = Required(item, "name", 160);
        string type = Required(item, "type", 64);
        if (!NamedLocationEligibilityPolicy.IsAllowed(type)) throw Invalid("gazetteer_location_type_ineligible");
        JsonElement position = RequiredArray(item, "position");
        if (position.GetArrayLength() != 2) throw Invalid("gazetteer_position_invalid");
        double x = ElementNumber(position[0], -50000, 500000, "gazetteer_position_invalid");
        double y = ElementNumber(position[1], -50000, 500000, "gazetteer_position_invalid");
        double radiusA = Number(item, "radiusA", 0, 100000);
        double radiusB = Number(item, "radiusB", 0, 100000);
        double angle = Number(item, "angle", 0, 359.999999999);
        return new MapGazetteerLocation(key, name, type, x, y, radiusA, radiusB, angle);
    }

    private static JsonElement Object(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            ? value : throw Invalid($"gazetteer_{name}_invalid");
    private static JsonElement RequiredArray(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value : throw Invalid($"gazetteer_{name}_invalid");
    private static string Required(JsonElement parent, string name, int maximum)
    {
        string value = Optional(parent, name, maximum);
        return value.Length > 0 ? value : throw Invalid($"gazetteer_{name}_missing");
    }
    private static string Optional(JsonElement parent, string name, int maximum)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return string.Empty;
        if (value.ValueKind != JsonValueKind.String) throw Invalid($"gazetteer_{name}_invalid");
        string result = (value.GetString() ?? string.Empty).Trim();
        if (result.Length > maximum || result.Any(char.IsControl)) throw Invalid($"gazetteer_{name}_invalid");
        return result;
    }
    private static int Integer(JsonElement parent, string name, int minimum, int maximum)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out int result) && result >= minimum && result <= maximum
            ? result : throw Invalid($"gazetteer_{name}_invalid");
    private static long PositiveLong(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt64(out long result) && result > 0
            ? result : throw Invalid($"gazetteer_{name}_invalid");
    private static double Number(JsonElement parent, string name, double minimum, double maximum)
        => parent.TryGetProperty(name, out JsonElement value)
            ? ElementNumber(value, minimum, maximum, $"gazetteer_{name}_invalid")
            : throw Invalid($"gazetteer_{name}_missing");
    private static double ElementNumber(JsonElement value, double minimum, double maximum, string code)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double result) &&
           double.IsFinite(result) && result >= minimum && result <= maximum
            ? result : throw Invalid(code);
    private static string ReadString(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;
    private static GazetteerFormatException Invalid(string code) => new(code);

    private sealed record Page(
        string MissionId, string SessionId, string RequestId, string BatchId, double GameTime,
        int PageIndex, int PageCount, int TotalLocations, string Status, string ErrorCode,
        string WorldName, double WorldSizeMeters, IReadOnlyList<MapGazetteerLocation> Locations);

    private sealed record PendingBatch(
        string BatchId, int PageCount, int TotalLocations,
        DateTimeOffset ReceivedAtUtc,
        Dictionary<int, IReadOnlyList<MapGazetteerLocation>> Pages)
    {
        public bool Matches(Page page)
            => page.BatchId == BatchId && page.PageCount == PageCount &&
               page.TotalLocations == TotalLocations;
    }

    private sealed class GazetteerFormatException(string code) : Exception
    {
        public string Code { get; } = code;
    }
}
