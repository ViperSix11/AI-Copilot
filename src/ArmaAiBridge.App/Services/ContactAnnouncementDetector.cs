using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed record ContactAnnouncement(string TrackId, string Kind, string Grid, string VisibleText);

public sealed class ContactAnnouncementDetector
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _statusByTrack = new(StringComparer.Ordinal);
    private string _sessionKey = string.Empty;
    private bool _baselineEstablished;

    public IReadOnlyList<ContactAnnouncement> Evaluate(
        string sessionKey,
        long snapshotSequence,
        IReadOnlyList<MissionContactTrack> tracks,
        string? playerCallsign)
    {
        lock (_gate)
        {
            if (!string.Equals(sessionKey, _sessionKey, StringComparison.Ordinal))
            {
                _sessionKey = sessionKey ?? string.Empty;
                _statusByTrack.Clear();
                _baselineEstablished = false;
            }
            if (snapshotSequence <= 0) return Array.Empty<ContactAnnouncement>();

            MissionContactTrack[] eligible = tracks
                .Where(track => track.Relationship is "hostile" or "unknown")
                .Where(track => track.Status is "current" or "last-known")
                .OrderBy(track => track.TrackId, StringComparer.Ordinal).ToArray();
            if (!_baselineEstablished)
            {
                foreach (MissionContactTrack track in eligible) _statusByTrack[track.TrackId] = track.Status;
                _baselineEstablished = true;
                return Array.Empty<ContactAnnouncement>();
            }

            List<TransitionedContact> transitions = new();
            HashSet<string> retained = new(StringComparer.Ordinal);
            foreach (MissionContactTrack track in eligible)
            {
                retained.Add(track.TrackId);
                bool known = _statusByTrack.TryGetValue(track.TrackId, out string? previous);
                _statusByTrack[track.TrackId] = track.Status;
                if (track.Status != "current") continue;
                string kind = !known ? "new" : previous == "last-known" ? "reacquired" : string.Empty;
                if (kind.Length == 0) continue;
                transitions.Add(new TransitionedContact(track, kind));
            }
            foreach (string removed in _statusByTrack.Keys.Where(key => !retained.Contains(key)).ToArray())
                _statusByTrack.Remove(removed);
            return GroupTransitions(transitions, playerCallsign);
        }
    }

    private static IReadOnlyList<ContactAnnouncement> GroupTransitions(
        IReadOnlyList<TransitionedContact> transitions,
        string? playerCallsign)
    {
        List<List<TransitionedContact>> groups = new();
        foreach (TransitionedContact transition in transitions.OrderBy(item => item.Track.TrackId, StringComparer.Ordinal))
        {
            List<TransitionedContact>? group = groups.FirstOrDefault(candidate => candidate.All(item =>
                item.Kind == transition.Kind &&
                item.Track.Relationship == transition.Track.Relationship &&
                item.Track.ContactType == transition.Track.ContactType &&
                Math.Abs((item.Track.LastObservedAtUtc - transition.Track.LastObservedAtUtc).TotalSeconds) <= 120 &&
                Distance(item.Track.EstimatedPosition, transition.Track.EstimatedPosition) <= 40));
            if (group is null) groups.Add(group = new List<TransitionedContact>());
            group.Add(transition);
        }

        string callsign = SafeCallsign(playerCallsign);
        return groups.Select(group =>
        {
            TransitionedContact first = group[0];
            WorldPosition center = new(
                group.Average(item => item.Track.EstimatedPosition.X),
                group.Average(item => item.Track.EstimatedPosition.Y),
                group.Average(item => item.Track.EstimatedPosition.Z));
            string grid = Grid(center);
            string noun = ContactNoun(first.Track.ContactType);
            string classification = first.Track.Relationship == "unknown" ? "unknown" : "enemy";
            string body = group.Count == 1
                ? first.Kind == "new"
                    ? $"New {classification} {noun} contact, grid {grid}."
                    : $"{Capitalize(classification)} {noun} contact reacquired, grid {grid}."
                : first.Kind == "new"
                    ? $"New {classification} {noun} group, {CountWord(group.Count)} contacts, grid {grid}."
                    : $"{Capitalize(classification)} {noun} group reacquired, {CountWord(group.Count)} contacts, grid {grid}.";
            string visible = callsign.Length == 0
                ? $"Papa Bear. {body} Over."
                : $"{callsign}, Papa Bear. {body} Over.";
            return new ContactAnnouncement(first.Track.TrackId, first.Kind, grid, visible);
        }).ToArray();
    }

    private static string Grid(WorldPosition value)
        => $"{Math.Clamp((int)Math.Floor(value.X / 100), 0, 999):000}{Math.Clamp((int)Math.Floor(value.Y / 100), 0, 999):000}";
    private static double Distance(WorldPosition left, WorldPosition right)
        => Math.Sqrt(Math.Pow(right.X - left.X, 2) + Math.Pow(right.Y - left.Y, 2));
    private static string ContactNoun(string type) => type switch
    {
        "person" => "infantry",
        "ground-vehicle" => "vehicle",
        "air" => "aircraft",
        "naval" => "vessel",
        "static-weapon" => "static weapon",
        "unmanned-ground" => "unmanned vehicle",
        "unmanned-air" => "unmanned aircraft",
        _ => ""
    };
    private static string SafeCallsign(string? value)
    {
        string result = (value ?? string.Empty).Trim();
        return result.Length is > 0 and <= 80 && !result.Any(char.IsControl) ? result : string.Empty;
    }
    private static string Capitalize(string value) => char.ToUpperInvariant(value[0]) + value[1..];
    private static string CountWord(int value) => value switch
    {
        2 => "two", 3 => "three", 4 => "four", 5 => "five", 6 => "six",
        7 => "seven", 8 => "eight", 9 => "nine", 10 => "ten", 11 => "eleven", 12 => "twelve",
        _ => value.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };
    private sealed record TransitionedContact(MissionContactTrack Track, string Kind);
}
