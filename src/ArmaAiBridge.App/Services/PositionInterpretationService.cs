using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class PositionInterpretationService
{
    private static readonly HashSet<string> SettlementTypes = new(StringComparer.OrdinalIgnoreCase)
    { "NameCityCapital", "NameCity", "NameVillage", "CityCenter", "Airport", "NameMarine", "Port" };
    private static readonly HashSet<string> GeographicTypes = new(StringComparer.OrdinalIgnoreCase)
    { "NameLocal", "Mount", "Bay", "Lake", "BorderCrossing" };
    private static readonly HashSet<string> MinorTypes = new(StringComparer.OrdinalIgnoreCase)
    { "Hill", "ViewPoint", "RockArea" };

    public PositionInterpretation Interpret(WorldStateView world, MapGazetteerSnapshot gazetteer)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (world.Map is null || world.Player?.Metadata.Position is null)
            throw new InvalidOperationException("No player position is available for interpretation.");

        WorldPosition measured = world.Player.PositionAsl ?? world.Player.Metadata.Position;
        IReadOnlyList<RankedLocation> ranked = Rank(world.Player.Metadata.Position, gazetteer)
            .Take(3)
            .ToArray();
        return new PositionInterpretation(
            world.Player.Metadata.FreshnessClass is WorldFreshness.Live or WorldFreshness.Recent
                ? "live" : "last-known",
            world.Map.Name,
            world.Map.Grid,
            measured,
            Round(world.Player.Metadata.AgeSeconds, 1),
            ranked.Count == 0 ? null : Reference(ranked[0]),
            ranked.Skip(1).Select(Reference).ToArray(),
            EnumText(gazetteer.Readiness));
    }

    public string FindNamedLocations(
        WorldStateView world,
        MapGazetteerSnapshot gazetteer,
        JsonElement arguments)
    {
        if (world.Player?.Metadata.Position is null)
            throw new InvalidOperationException("No player position is available.");
        if (gazetteer.Readiness is not (MapGazetteerReadiness.Ready or MapGazetteerReadiness.Empty))
            throw new InvalidOperationException("The named-location gazetteer is not ready.");

        string? query = ReadNullableString(arguments, "query", 160);
        double maximumDistance = ReadNumber(arguments, "maxDistanceMeters", 50, 50000);
        int limit = (int)ReadNumber(arguments, "limit", 1, 10, integer: true);
        RankedLocation[] results = Rank(world.Player.Metadata.Position, gazetteer)
            .Where(item => item.Distance <= maximumDistance)
            .Where(item => string.IsNullOrWhiteSpace(query) ||
                           item.Location.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           item.Location.Type.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToArray();
        return JsonSerializer.Serialize(new
        {
            schema = WorldSnapshotBuilder.SnapshotSchema,
            purpose = "named-locations",
            query = new { text = query, maxDistanceMeters = maximumDistance, limit },
            playerStatus = world.Player.Metadata.FreshnessClass is WorldFreshness.Live or WorldFreshness.Recent
                ? "live" : "last-known",
            locations = results.Select(item => new
            {
                item.Location.Name,
                item.Location.Type,
                item.Inside,
                distanceMeters = Round(item.Distance, 1),
                roundedDistanceMeters = RoundMilitaryDistance(item.Distance),
                distanceKlicks = DistanceKlicks(item.Distance),
                bearingFromReference = Bearing(item.Location, world.Player.Metadata.Position),
                directionFromReference = Direction(BearingExact(item.Location, world.Player.Metadata.Position))
            })
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    internal static int RoundMilitaryDistance(double distance)
    {
        double increment = distance < 100 ? 10 : distance < 1000 ? 50 : 100;
        return checked((int)(Math.Round(distance / increment, MidpointRounding.AwayFromZero) * increment));
    }

    internal static double? DistanceKlicks(double distance)
        => distance < 1000 ? null : Math.Round(distance / 1000, 1, MidpointRounding.AwayFromZero);

    internal static int Bearing(MapGazetteerLocation location, WorldPosition player)
        => (int)Math.Round(BearingExact(location, player), MidpointRounding.AwayFromZero) % 360;

    internal static string Direction(double bearing)
    {
        string[] sectors = { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };
        return sectors[(int)Math.Floor((Normalize(bearing) + 22.5) / 45) % 8];
    }

    internal static bool IsInside(MapGazetteerLocation location, WorldPosition player)
    {
        if (location.RadiusA <= 0 || location.RadiusB <= 0) return false;
        double dx = player.X - location.X;
        double dy = player.Y - location.Y;
        double radians = location.Angle * Math.PI / 180;
        double localX = (dx * Math.Cos(radians)) - (dy * Math.Sin(radians));
        double localY = (dx * Math.Sin(radians)) + (dy * Math.Cos(radians));
        double value = Math.Pow(localX / location.RadiusA, 2) + Math.Pow(localY / location.RadiusB, 2);
        return value <= 1 + 1e-12;
    }

    private static IEnumerable<RankedLocation> Rank(WorldPosition player, MapGazetteerSnapshot gazetteer)
    {
        if (gazetteer.Readiness != MapGazetteerReadiness.Ready) return Array.Empty<RankedLocation>();
        IEnumerable<MapGazetteerLocation> deduplicated = gazetteer.Locations
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .GroupBy(item => $"{item.Name.ToUpperInvariant()}\0{item.Type.ToUpperInvariant()}\0{item.X:R}\0{item.Y:R}", StringComparer.Ordinal)
            .Select(group => group.First());
        return deduplicated
            .Select(item =>
            {
                double distance = Math.Sqrt(Math.Pow(player.X - item.X, 2) + Math.Pow(player.Y - item.Y, 2));
                int tier = Tier(item.Type);
                bool inside = IsInside(item, player);
                return new RankedLocation(item, inside, distance, tier, distance + Penalty(tier))
                {
                    PlayerPosition = player
                };
            })
            .OrderBy(item => item.Inside ? 0 : 1)
            .ThenBy(item => item.Inside ? item.Tier : 0)
            .ThenBy(item => item.Inside ? item.Distance : item.EffectiveDistance)
            .ThenBy(item => item.Tier)
            .ThenBy(item => item.Distance)
            .ThenBy(item => item.Location.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Location.Key, StringComparer.Ordinal);
    }

    private static InterpretedLocationReference Reference(RankedLocation item)
    {
        WorldPosition player = item.PlayerPosition ?? throw new InvalidOperationException();
        double bearing = BearingExact(item.Location, player);
        return new InterpretedLocationReference(
            item.Location.Name,
            item.Location.Type,
            item.Inside,
            Round(item.Distance, 1),
            RoundMilitaryDistance(item.Distance),
            DistanceKlicks(item.Distance),
            (int)Math.Round(bearing, MidpointRounding.AwayFromZero) % 360,
            Direction(bearing));
    }

    private static double BearingExact(MapGazetteerLocation location, WorldPosition player)
        => Normalize(Math.Atan2(player.X - location.X, player.Y - location.Y) * 180 / Math.PI);
    private static double Normalize(double value) => (value % 360 + 360) % 360;
    private static int Tier(string type) => SettlementTypes.Contains(type) ? 1 : GeographicTypes.Contains(type) ? 2 : MinorTypes.Contains(type) ? 3 : 4;
    private static int Penalty(int tier) => (tier - 1) * 250;
    private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.AwayFromZero);
    private static string EnumText<T>(T value) where T : struct, Enum
        => string.Concat(value.ToString().Select((c, i) => char.IsUpper(c) && i > 0 ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static string? ReadNullableString(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value)) throw new InvalidOperationException($"{name} is required.");
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidOperationException($"{name} must be a string or null.");
        string result = (value.GetString() ?? string.Empty).Trim();
        if (result.Length > maximum || result.Any(char.IsControl)) throw new InvalidOperationException($"{name} is invalid.");
        return result;
    }
    private static double ReadNumber(JsonElement root, string name, double minimum, double maximum, bool integer = false)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double result) || !double.IsFinite(result) || result < minimum || result > maximum ||
            (integer && result != Math.Truncate(result)))
            throw new InvalidOperationException($"{name} must be between {minimum} and {maximum}.");
        return result;
    }

    private sealed record RankedLocation(
        MapGazetteerLocation Location,
        bool Inside,
        double Distance,
        int Tier,
        double EffectiveDistance)
    {
        public WorldPosition? PlayerPosition { get; init; }
    }
}
