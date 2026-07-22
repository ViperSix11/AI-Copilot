using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class MapKnowledgeSnapshotBuilder
{
    public const string SnapshotSchema = "arma-ai-bridge/map-knowledge-snapshot-v1";
    private const int MaximumOutputCharacters = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly MapKnowledgeService _map;
    private readonly WorldStateStore _world;

    public MapKnowledgeSnapshotBuilder(MapKnowledgeService map, WorldStateStore world)
    {
        _map = map;
        _world = world;
    }

    public string FindNearestLocations(JsonElement arguments)
    {
        WorldPosition origin = RequirePlayerPosition();
        double distance = RequiredNumber(arguments, "maxDistanceMeters", 100, 50000);
        int limit = RequiredInteger(arguments, "limit", 1, 20);
        string[] types = RequiredStringArray(arguments, "locationTypes", 0, 16, 64);
        MapKnowledgeDiagnostics diagnostics = _map.GetDiagnostics();
        return Serialize(new
        {
            schema = SnapshotSchema,
            purpose = "nearest-locations",
            mapKnowledge = Status(diagnostics),
            origin = new { source = "player", positionATL = new[] { origin.X, origin.Y, origin.Z } },
            locations = _map.Database.FindNearestLocations(origin.X, origin.Y, distance, types, limit).Select(Location)
        });
    }

    public string FindLocationsByName(JsonElement arguments)
    {
        string name = RequiredString(arguments, "name", 1, 80);
        int limit = RequiredInteger(arguments, "limit", 1, 20);
        WorldPosition? origin = _world.GetCurrentView().Player?.Metadata.Position;
        MapKnowledgeDiagnostics diagnostics = _map.GetDiagnostics();
        return Serialize(new
        {
            schema = SnapshotSchema,
            purpose = "locations-by-name",
            mapKnowledge = Status(diagnostics),
            query = name,
            locations = _map.Database.FindLocationsByName(name, limit, origin?.X, origin?.Y).Select(Location)
        });
    }

    public string QueryMapArea(JsonElement arguments)
    {
        WorldPosition origin = RequirePlayerPosition();
        double radius = RequiredNumber(arguments, "radiusMeters", 100, 5000);
        int limit = RequiredInteger(arguments, "limitPerCategory", 1, 25);
        string[] categories = RequiredStringArray(arguments, "categories", 1, 6, 32);
        string[] allowed = { "location", "terrain", "building", "road", "vegetation", "water" };
        if (categories.Any(category => !allowed.Contains(category, StringComparer.Ordinal)) || categories.Distinct().Count() != categories.Length)
            throw new InvalidOperationException("Invalid map category.");
        MapKnowledgeDiagnostics diagnostics = _map.GetDiagnostics();
        Dictionary<string, object?> result = new(StringComparer.Ordinal);
        if (categories.Contains("location")) result["locations"] = _map.Database.FindNearestLocations(origin.X, origin.Y, radius, Array.Empty<string>(), limit).Select(Location).ToArray();
        if (categories.Contains("building")) result["buildings"] = _map.Database.QueryBuildings(origin.X, origin.Y, radius, limit).Select(value => new
        {
            value.EntityId, @class = value.ClassName, model = value.ModelName, terrainType = value.TerrainType,
            positionASL = new[] { value.X, value.Y, value.ZAsl }, value.DistanceMeters, value.BearingDegrees
        }).ToArray();
        if (categories.Contains("road"))
        {
            result["roads"] = _map.Database.QueryRoads(origin.X, origin.Y, radius, limit).Select(value => new
            {
                value.EntityId, value.RoadType, value.WidthMeters, value.Pedestrian, value.Bridge,
                beginASL = new[] { value.BeginX, value.BeginY }, endASL = new[] { value.EndX, value.EndY }, value.DistanceMeters
            }).ToArray();
            result["intersections"] = _map.Database.QueryIntersections(origin.X, origin.Y, radius, limit).Select(value => new
            {
                value.EntityId, positionASL = new[] { value.X, value.Y, value.ZAsl }, value.ConnectedSegments, value.DistanceMeters
            }).ToArray();
        }
        if (categories.Contains("terrain")) result["terrain"] = _map.Database.QueryTerrain(origin.X, origin.Y, radius);
        if (categories.Contains("vegetation") || categories.Contains("water"))
        {
            IReadOnlyList<MapTileSummaryResult> summaries = _map.Database.QueryTileSummaries(origin.X, origin.Y, radius, limit);
            if (categories.Contains("vegetation")) result["vegetation"] = new
            {
                tileCount = summaries.Count, treeCount = summaries.Sum(value => value.TreeCount), bushCount = summaries.Sum(value => value.BushCount),
                forestCount = summaries.Sum(value => value.ForestCount), averageDensityPerHectare = summaries.Count == 0 ? 0 : summaries.Average(value => value.VegetationDensityPerHectare),
                truncated = summaries.Count == limit
            };
            if (categories.Contains("water")) result["water"] = new
            {
                tileCount = summaries.Count,
                classifications = summaries.GroupBy(value => value.WaterClassification).ToDictionary(group => group.Key, group => group.Count()),
                waterSamples = summaries.Sum(value => value.WaterSamples), landSamples = summaries.Sum(value => value.LandSamples),
                truncated = summaries.Count == limit, pondCoverage = "off-camera ponds unverified"
            };
        }
        return Serialize(new
        {
            schema = SnapshotSchema,
            purpose = "map-area",
            mapKnowledge = Status(diagnostics),
            origin = new { source = "player", positionATL = new[] { origin.X, origin.Y, origin.Z } },
            radiusMeters = radius,
            result
        });
    }

    private WorldPosition RequirePlayerPosition()
        => _world.GetCurrentView().Player?.Metadata.Position
           ?? throw new InvalidOperationException("Current player position is unavailable.");

    private static object Status(MapKnowledgeDiagnostics value) => new
    {
        readiness = value.Readiness.ToString().ToLowerInvariant(),
        complete = value.Readiness == MapKnowledgeReadiness.Ready,
        value.CompletedTiles,
        value.TotalTiles,
        coveragePercent = Math.Round(value.ProgressPercent, 2),
        indexVersion = value.IndexVersion,
        fingerprintTag = value.Fingerprint.Length >= 12 ? value.Fingerprint[..12] : ""
    };

    private static object Location(MapLocationResult value) => new
    {
        value.EntityId, name = value.OfficialName, type = value.LocationType,
        positionASL = new[] { value.X, value.Y, value.ZAsl },
        distanceMeters = value.DistanceMeters >= 0 ? value.DistanceMeters : (double?)null,
        bearingDegrees = value.BearingDegrees >= 0 ? value.BearingDegrees : (double?)null
    };

    private static string Serialize(object value)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions);
        if (json.Length > MaximumOutputCharacters) throw new InvalidOperationException("Map result exceeded the local output limit.");
        return json;
    }

    private static double RequiredNumber(JsonElement root, string name, double minimum, double maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double number) || !double.IsFinite(number) || number < minimum || number > maximum)
            throw new InvalidOperationException($"{name} is outside the allowed range.");
        return number;
    }

    private static int RequiredInteger(JsonElement root, string name, int minimum, int maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int number) || number < minimum || number > maximum)
            throw new InvalidOperationException($"{name} is outside the allowed range.");
        return number;
    }

    private static string RequiredString(JsonElement root, string name, int minimum, int maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String) throw new InvalidOperationException($"{name} is required.");
        string text = value.GetString() ?? "";
        if (text.Length < minimum || text.Length > maximum || text.Any(char.IsControl)) throw new InvalidOperationException($"{name} is invalid.");
        return text;
    }

    private static string[] RequiredStringArray(JsonElement root, string name, int minimum, int maximum, int maximumLength)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() < minimum || value.GetArrayLength() > maximum) throw new InvalidOperationException($"{name} is invalid.");
        return value.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.String) throw new InvalidOperationException($"{name} is invalid.");
            string text = item.GetString() ?? "";
            if (text.Length == 0 || text.Length > maximumLength || text.Any(char.IsControl)) throw new InvalidOperationException($"{name} is invalid.");
            return text;
        }).ToArray();
    }
}
