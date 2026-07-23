using System.Globalization;
using System.Text.RegularExpressions;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed partial class ReportedGridConversationService
{
    public static readonly TimeSpan ReportedPositionMaximumAge = TimeSpan.FromMinutes(30);
    private const double SixDigitGridUncertaintyMeters = 70.71067811865476;
    private readonly IMissionMemoryRepository _repository;
    private readonly TimeProvider _timeProvider;

    public ReportedGridConversationService(IMissionMemoryRepository repository, TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryHandle(string input, out string response)
    {
        string text = (input ?? string.Empty).Trim();
        if (TryReadAnchor(text, out string key, out string label, out string grid))
        {
            WorldPosition position = GridCenter(grid);
            _repository.SaveReportedLocation(new ReportedLocationAnchor(
                key, label, grid, position, SixDigitGridUncertaintyMeters, _timeProvider.GetUtcNow()));
            response = key == "reported-position"
                ? $"Position report received at grid {grid}."
                : $"{Capitalize(label)} noted at grid {grid}.";
            return true;
        }

        if (!IsGridRelationQuestion(text))
        {
            response = string.Empty;
            return false;
        }

        ReportedLocationAnchor? goal = _repository.GetReportedLocation("mission-goal");
        if (goal is null)
        {
            response = "Send the six-digit grid for your mission goal.";
            return true;
        }
        ReportedLocationAnchor? reportedPosition = _repository.GetReportedLocation("reported-position");
        if (reportedPosition is null)
        {
            response = "Send your current six-digit grid so I can calculate that locally.";
            return true;
        }
        if (_timeProvider.GetUtcNow() - reportedPosition.ReportedAtUtc > ReportedPositionMaximumAge)
        {
            response = "Your reported position is too old for that calculation. Send an updated six-digit grid.";
            return true;
        }

        double east = goal.Position.X - reportedPosition.Position.X;
        double north = goal.Position.Y - reportedPosition.Position.Y;
        double distance = Math.Sqrt((east * east) + (north * north));
        int bearing = (int)Math.Round((Math.Atan2(east, north) * 180 / Math.PI + 360) % 360) % 360;
        string cardinal = new[] { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" }
            [(int)Math.Round(bearing / 45d) % 8];
        string distanceText = distance >= 1000
            ? $"{Math.Round(distance / 1000, 1).ToString("0.#", CultureInfo.InvariantCulture)} kilometres"
            : $"{Math.Round(distance / 100) * 100:0} metres";
        response = $"Your mission goal is approximately {distanceText} {cardinal} of your reported position.";
        return true;
    }

    public static WorldPosition GridCenter(string grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (!SixDigitGrid().IsMatch(grid)) throw new ArgumentException("A six-digit grid is required.", nameof(grid));
        int easting = int.Parse(grid[..3], CultureInfo.InvariantCulture);
        int northing = int.Parse(grid[3..], CultureInfo.InvariantCulture);
        return new WorldPosition((easting * 100) + 50, (northing * 100) + 50, 0);
    }

    private static bool TryReadAnchor(string text, out string key, out string label, out string grid)
    {
        key = label = grid = string.Empty;
        Match match = SixDigitInText().Match(text);
        if (!match.Success) return false;
        string lower = text.ToLowerInvariant();
        if (ContainsAny(lower, "mission goal", "mission objective", "objective", "destination"))
        {
            key = "mission-goal";
            label = "mission goal";
        }
        else if (ContainsAny(lower, "my current position", "my position", "current position", "my current location", "my location", "current location"))
        {
            key = "reported-position";
            label = "reported position";
        }
        else return false;
        grid = match.Groups[1].Value;
        return true;
    }

    private static bool IsGridRelationQuestion(string value)
    {
        string lower = value.ToLowerInvariant();
        return PlayerUtteranceClassifier.IsQuestion(value) &&
               ContainsAny(lower, "how far", "distance", "how close") &&
               ContainsAny(lower, "mission goal", "mission objective", "objective", "goal", "it");
    }

    private static bool ContainsAny(string value, params string[] terms)
        => terms.Any(term => value.Contains(term, StringComparison.Ordinal));
    private static string Capitalize(string value) => char.ToUpperInvariant(value[0]) + value[1..];

    [GeneratedRegex("^[0-9]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex SixDigitGrid();

    [GeneratedRegex("(?:\\bgrid\\s*)?\\b([0-9]{6})\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SixDigitInText();
}
