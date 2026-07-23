using System.Globalization;
using System.Text.RegularExpressions;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed record TacticalPositionDescription(
    string Text,
    string Grid,
    string ReferenceKind,
    string ReferenceName);

public interface ITacticalPositionReporter
{
    TacticalPositionDescription Describe(WorldPosition target);
}

public sealed class TacticalPositionReportingService : ITacticalPositionReporter
{
    private const double MaximumNearbyReferenceMeters = 5000;
    private const double MaximumFriendlyReferenceMeters = 1500;
    private const double MaximumStableFriendlySpeedKph = 5;
    private readonly IStateRepository _state;

    public TacticalPositionReportingService(IStateRepository state)
        => _state = state ?? throw new ArgumentNullException(nameof(state));

    public TacticalPositionDescription Describe(WorldPosition target)
    {
        string grid = Grid(target);
        StateMarker? bullseye = _state.GetMarkers(256)
            .Where(marker => marker.ReferenceRole == "bullseye" && SafeLabel(marker.ReferenceLabel).Length > 0)
            .OrderBy(marker => Distance(marker.Position, target))
            .ThenBy(marker => marker.ReferenceLabel, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (bullseye is not null)
            return Relative(target, bullseye.Position, bullseye.ReferenceLabel, "bullseye", grid);

        List<ReferenceCandidate> nearby = new();
        foreach (StateMarker marker in _state.GetMarkers(256)
                     .Where(marker => marker.ReferenceRole == "location"))
            Add(nearby, target, marker.Position, marker.ReferenceLabel, "mission location",
                MaximumNearbyReferenceMeters);

        foreach (StateTask task in _state.GetTasks(128)
                     .Where(task => task.Active && task.Destination is not null))
        {
            string label = SafeLabel(task.Title);
            if (label.Length == 0) label = SafeLabel(task.Description);
            Add(nearby, target, task.Destination!, label, "mission objective",
                MaximumNearbyReferenceMeters);
        }

        foreach (MapGazetteerLocation location in _state.GetNearestNamedLocations(target, 10))
            Add(nearby, target, new WorldPosition(location.X, location.Y, 0), location.Name,
                "named location", MaximumNearbyReferenceMeters);

        StatePlayer? player = _state.GetPlayer();
        HashSet<string> livingGroups = _state.GetFriendlyUnits(512)
            .Where(unit => unit.Alive)
            .Select(unit => unit.GroupAlias)
            .ToHashSet(StringComparer.Ordinal);
        foreach (StateFriendlyGroup group in _state.GetFriendlyGroups(128)
                     .Where(group => livingGroups.Contains(group.Alias))
                     .Where(group => group.LeaderSpeedKph <= MaximumStableFriendlySpeedKph)
                     .Where(group => player is null ||
                                     (!string.Equals(group.Alias, player.GroupAlias, StringComparison.Ordinal) &&
                                      !string.Equals(group.Callsign, player.GroupCallsign, StringComparison.OrdinalIgnoreCase))))
            Add(nearby, target, group.LeaderPosition, group.Callsign, "friendly reference",
                MaximumFriendlyReferenceMeters);

        ReferenceCandidate? selected = nearby
            .OrderBy(candidate => candidate.DistanceMeters)
            .ThenBy(candidate => candidate.Kind, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Label, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return selected is null
            ? new TacticalPositionDescription($"grid {grid}", grid, "grid", string.Empty)
            : Relative(target, selected.Position, selected.Label, selected.Kind, grid);
    }

    internal static (string Role, string Label) ClassifyMarker(string? text, string? sourceId)
    {
        string visible = SafeLabel(text);
        if (ContainsBullseye(visible)) return ("bullseye", visible);
        string raw = (sourceId ?? string.Empty).Trim();
        if (raw.Contains("_USER_DEFINED", StringComparison.OrdinalIgnoreCase)) return ("", "");
        Match match = Regex.Match(raw, "bullseye[a-z0-9_-]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
        {
            string derived = Regex.Replace(match.Value, "[-_]+", " ").Trim();
            derived = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(derived.ToLowerInvariant());
            return ("bullseye", SafeLabel(derived));
        }
        return visible.Length > 0 && visible.Any(char.IsLetterOrDigit)
            ? ("location", visible)
            : ("", "");
    }

    private static TacticalPositionDescription Relative(
        WorldPosition target,
        WorldPosition reference,
        string label,
        string kind,
        string grid)
    {
        double distance = Distance(reference, target);
        string text = distance < 25
            ? $"at {SafeLabel(label)}"
            : $"{FormatDistance(distance)} {Direction(reference, target)} of {SafeLabel(label)}";
        return new TacticalPositionDescription(text, grid, kind, SafeLabel(label));
    }

    private static void Add(List<ReferenceCandidate> candidates, WorldPosition target,
        WorldPosition position, string? label, string kind, double maximumDistance)
    {
        string safe = SafeLabel(label);
        if (safe.Length == 0 || !safe.Any(char.IsLetterOrDigit)) return;
        double distance = Distance(position, target);
        if (distance <= maximumDistance)
            candidates.Add(new ReferenceCandidate(position, safe, kind, distance));
    }

    private static bool ContainsBullseye(string value)
        => value.Contains("bullseye", StringComparison.OrdinalIgnoreCase);

    internal static string SafeLabel(string? value)
    {
        string normalized = Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
        if (normalized.Length > 80) normalized = normalized[..80].TrimEnd();
        return normalized.Any(char.IsControl) ? string.Empty : normalized;
    }

    public static string FormatDistance(double meters)
    {
        if (meters > 2000)
        {
            int kilometres = Math.Max(1, (int)Math.Round(meters / 1000, MidpointRounding.AwayFromZero));
            return kilometres == 1 ? "1 kilometre" : $"{kilometres} kilometres";
        }
        double increment = meters < 500 ? 50 : 100;
        int rounded = Math.Max((int)increment,
            (int)(Math.Round(meters / increment, MidpointRounding.AwayFromZero) * increment));
        return $"{rounded.ToString("N0", CultureInfo.InvariantCulture)} metres";
    }

    public static string Direction(WorldPosition reference, WorldPosition target)
    {
        double bearing = (Math.Atan2(target.X - reference.X, target.Y - reference.Y) * 180 / Math.PI + 360) % 360;
        string[] sectors = ["north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest"];
        return sectors[(int)Math.Floor((bearing + 22.5) / 45) % 8];
    }

    public static string Grid(WorldPosition value)
        => $"{Math.Clamp((int)Math.Floor(value.X / 100), 0, 999):000}{Math.Clamp((int)Math.Floor(value.Y / 100), 0, 999):000}";

    private static double Distance(WorldPosition left, WorldPosition right)
        => Math.Sqrt(Math.Pow(right.X - left.X, 2) + Math.Pow(right.Y - left.Y, 2));

    private sealed record ReferenceCandidate(
        WorldPosition Position,
        string Label,
        string Kind,
        double DistanceMeters);
}
