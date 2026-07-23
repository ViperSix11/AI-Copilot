using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public enum LocationCorroboration
{
    UnknownLocation,
    None,
    Earlier,
    Recent
}

public sealed record SemanticLocationDefinition(
    string Name,
    WorldPosition Center,
    string Shape,
    double HalfWidthMeters,
    double HalfHeightMeters,
    double DirectionDegrees,
    bool IsStale);

public static class SemanticLocationPolicy
{
    public const double PointLocationRadiusMeters = 200;
    public static readonly TimeSpan RecentObservationWindow = TimeSpan.FromMinutes(10);

    public static IReadOnlyList<SemanticLocationDefinition> FromMarkers(IEnumerable<StateMarker> markers)
        => markers.Where(marker => !string.IsNullOrWhiteSpace(marker.Text) && marker.Text.Any(char.IsLetterOrDigit))
            .Select(marker => new SemanticLocationDefinition(
                marker.Text.Trim(), marker.Position, NormalizeShape(marker.Shape),
                Size(marker.Size, 0), Size(marker.Size, 1), NormalizeDirection(marker.Direction), marker.Metadata.IsStale))
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public static SemanticLocationDefinition? Resolve(IEnumerable<SemanticLocationDefinition> locations, string utterance)
    {
        HashSet<string> words = Words(utterance);
        return locations.Where(location => Words(location.Name).All(words.Contains))
            .OrderByDescending(location => location.Name.Length)
            .ThenBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public static LocationCorroboration Assess(
        SemanticLocationDefinition? location,
        IEnumerable<MissionContactTrack> contacts,
        DateTimeOffset now)
    {
        if (location is null) return LocationCorroboration.UnknownLocation;
        MissionContactTrack[] overlapping = contacts.Where(contact => contact.Status != "dead" &&
            Contains(location, contact.EstimatedPosition, contact.UncertaintyRadiusMeters)).ToArray();
        if (overlapping.Any(contact => contact.Status == "current" && now - contact.LastObservedAtUtc <= RecentObservationWindow))
            return LocationCorroboration.Recent;
        return overlapping.Length > 0 ? LocationCorroboration.Earlier : LocationCorroboration.None;
    }

    public static bool Contains(SemanticLocationDefinition location, WorldPosition position, double uncertaintyMeters)
    {
        double uncertainty = double.IsFinite(uncertaintyMeters) ? Math.Max(0, uncertaintyMeters) : 0;
        double dx = position.X - location.Center.X;
        double dy = position.Y - location.Center.Y;
        if (location.Shape is not ("rectangle" or "ellipse"))
            return Math.Sqrt(dx * dx + dy * dy) <= PointLocationRadiusMeters + uncertainty;

        double radians = NormalizeDirection(location.DirectionDegrees) * Math.PI / 180d;
        double localX = dx * Math.Cos(radians) + dy * Math.Sin(radians);
        double localY = -dx * Math.Sin(radians) + dy * Math.Cos(radians);
        double a = Math.Max(1, location.HalfWidthMeters) + uncertainty;
        double b = Math.Max(1, location.HalfHeightMeters) + uncertainty;
        return location.Shape == "rectangle"
            ? Math.Abs(localX) <= a && Math.Abs(localY) <= b
            : localX * localX / (a * a) + localY * localY / (b * b) <= 1;
    }

    private static HashSet<string> Words(string value)
        => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
            .Where(word => word.Length > 1).ToHashSet(StringComparer.Ordinal);
    private static string NormalizeShape(string value) => value.Trim().ToLowerInvariant() switch
    { "rectangle" => "rectangle", "ellipse" => "ellipse", _ => "point" };
    private static double NormalizeDirection(double value)
        => double.IsFinite(value) ? (value % 360 + 360) % 360 : 0;
    private static double Size(IReadOnlyList<double> values, int index)
        => index < values.Count && double.IsFinite(values[index]) ? Math.Max(0, values[index]) : 0;
}
