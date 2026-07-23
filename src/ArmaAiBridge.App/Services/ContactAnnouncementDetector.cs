using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed record ContactAnnouncement(string TrackId, string Kind, string Grid, string VisibleText);

public sealed class ContactAnnouncementDetector
{
    internal static readonly TimeSpan MinimumReacquisitionInterval = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly Dictionary<string, TrackState> _stateByTrack = new(StringComparer.Ordinal);
    private readonly ITacticalPositionReporter _positionReports;
    private readonly TimeProvider _timeProvider;
    private string _sessionKey = string.Empty;
    private bool _baselineEstablished;

    public ContactAnnouncementDetector(
        ITacticalPositionReporter? positionReports = null,
        TimeProvider? timeProvider = null)
    {
        _positionReports = positionReports ?? new GridOnlyPositionReporter();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<ContactAnnouncement> Evaluate(
        string sessionKey,
        long snapshotSequence,
        IReadOnlyList<MissionContactTrack> tracks,
        string? playerCallsign)
    {
        lock (_gate)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (!string.Equals(sessionKey, _sessionKey, StringComparison.Ordinal))
            {
                _sessionKey = sessionKey ?? string.Empty;
                _stateByTrack.Clear();
                _baselineEstablished = false;
            }
            if (snapshotSequence <= 0) return Array.Empty<ContactAnnouncement>();

            MissionContactTrack[] eligible = tracks
                .Where(track => track.Relationship is "hostile" or "unknown")
                .Where(track => track.Status is "current" or "last-known")
                .OrderBy(track => track.TrackId, StringComparer.Ordinal).ToArray();
            if (!_baselineEstablished)
            {
                foreach (MissionContactTrack track in eligible)
                    _stateByTrack[track.TrackId] = new TrackState(
                        track.Status,
                        track.Status == "last-known" ? now : null);
                _baselineEstablished = true;
                return Array.Empty<ContactAnnouncement>();
            }

            List<TransitionedContact> transitions = new();
            HashSet<string> retained = new(StringComparer.Ordinal);
            foreach (MissionContactTrack track in eligible)
            {
                retained.Add(track.TrackId);
                bool known = _stateByTrack.TryGetValue(track.TrackId, out TrackState? previous);
                if (track.Status == "last-known")
                {
                    DateTimeOffset lostSince = known && previous!.Status == "last-known" && previous.LostSinceUtc is not null
                        ? previous.LostSinceUtc.Value
                        : now;
                    _stateByTrack[track.TrackId] = new TrackState("last-known", lostSince);
                    continue;
                }

                _stateByTrack[track.TrackId] = new TrackState("current", null);
                string kind = !known
                    ? "new"
                    : previous!.Status == "last-known" && previous.LostSinceUtc is not null &&
                      now - previous.LostSinceUtc.Value >= MinimumReacquisitionInterval
                        ? "reacquired"
                        : string.Empty;
                if (kind.Length > 0) transitions.Add(new TransitionedContact(track, kind));
            }
            foreach (string removed in _stateByTrack.Keys.Where(key => !retained.Contains(key)).ToArray())
                _stateByTrack.Remove(removed);
            return GroupTransitions(transitions, playerCallsign);
        }
    }

    private IReadOnlyList<ContactAnnouncement> GroupTransitions(
        IReadOnlyList<TransitionedContact> transitions,
        string? playerCallsign)
    {
        List<List<TransitionedContact>> groups = new();
        foreach (TransitionedContact transition in transitions.OrderBy(item => item.Track.TrackId, StringComparer.Ordinal))
        {
            List<TransitionedContact>? group = groups.FirstOrDefault(candidate => candidate.All(item =>
                item.Kind == transition.Kind &&
                item.Track.Relationship == transition.Track.Relationship &&
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
            TacticalPositionDescription position = _positionReports.Describe(center);
            string classification = first.Track.Relationship == "unknown" ? "unknown" : "enemy";
            string subject = ContactSubject(group);
            string body = first.Kind == "reacquired"
                ? $"Previously reported {classification} {subject} reacquired, {position.Text}."
                : $"{Capitalize(classification)} {subject}, {position.Text}.";
            string visible = callsign.Length == 0 ? body : $"{callsign}, {LowercaseFirst(body)}";
            return new ContactAnnouncement(first.Track.TrackId, first.Kind, position.Grid, visible);
        }).ToArray();
    }

    private static string ContactSubject(IReadOnlyList<TransitionedContact> group)
    {
        string[] subjects = group.GroupBy(item => ContactNoun(item.Track.ContactType), StringComparer.Ordinal)
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Key == "infantry" && item.Count() > 1
                ? "infantry group"
                : item.Count() > 1 ? Plural(item.Key) : item.Key)
            .ToArray();
        string joined = subjects.Length switch
        {
            0 => "contact",
            1 => subjects[0],
            2 => $"{subjects[0]} and {subjects[1]}",
            _ => $"{string.Join(", ", subjects[..^1])} and {subjects[^1]}"
        };
        return group.Count == 1 ? joined : $"{joined}, approximately {CountWord(group.Count)} contacts";
    }

    private static string ContactNoun(string type) => type switch
    {
        "person" => "infantry",
        "ground-vehicle" => "vehicle",
        "air" => "aircraft",
        "naval" => "vessel",
        "static-weapon" => "static weapon",
        "unmanned-ground" => "unmanned vehicle",
        "unmanned-air" => "unmanned aircraft",
        _ => "contact"
    };

    private static string Plural(string value) => value switch
    {
        "infantry" => "infantry",
        "aircraft" => "aircraft",
        _ => value + "s"
    };

    private static string SafeCallsign(string? value)
    {
        string result = (value ?? string.Empty).Trim();
        return result.Length is > 0 and <= 80 && !result.Any(char.IsControl) ? result : string.Empty;
    }

    private static string Capitalize(string value) => char.ToUpperInvariant(value[0]) + value[1..];
    private static string LowercaseFirst(string value) => char.ToLowerInvariant(value[0]) + value[1..];
    private static double Distance(WorldPosition left, WorldPosition right)
        => Math.Sqrt(Math.Pow(right.X - left.X, 2) + Math.Pow(right.Y - left.Y, 2));
    private static string CountWord(int value) => value switch
    {
        2 => "two", 3 => "three", 4 => "four", 5 => "five", 6 => "six",
        7 => "seven", 8 => "eight", 9 => "nine", 10 => "ten", 11 => "eleven", 12 => "twelve",
        _ => value.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    private sealed record TrackState(string Status, DateTimeOffset? LostSinceUtc);
    private sealed record TransitionedContact(MissionContactTrack Track, string Kind);
    private sealed class GridOnlyPositionReporter : ITacticalPositionReporter
    {
        public TacticalPositionDescription Describe(WorldPosition target)
        {
            string grid = TacticalPositionReportingService.Grid(target);
            return new TacticalPositionDescription($"grid {grid}", grid, "grid", string.Empty);
        }
    }
}
