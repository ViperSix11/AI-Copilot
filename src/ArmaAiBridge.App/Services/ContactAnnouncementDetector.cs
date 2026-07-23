using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed record ContactAnnouncement(string TrackId, string Kind, string Grid, string VisibleText);

public sealed class ContactAnnouncementDetector
{
    internal static readonly TimeSpan MinimumReacquisitionInterval = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly Dictionary<string, TrackState> _stateByTrack = new(StringComparer.Ordinal);
    private readonly List<TransitionedContact> _pendingTransitions = new();
    private readonly List<PresentationState> _recentPresentations = new();
    private readonly ITacticalPositionReporter _positionReports;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _batchWindow;
    private string _sessionKey = string.Empty;
    private bool _baselineEstablished;
    private DateTimeOffset? _pendingSinceUtc;

    public ContactAnnouncementDetector(
        ITacticalPositionReporter? positionReports = null,
        TimeProvider? timeProvider = null,
        TimeSpan? batchWindow = null)
    {
        _positionReports = positionReports ?? new GridOnlyPositionReporter();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _batchWindow = batchWindow ?? ContactPresentationPolicy.AnnouncementBatchWindow;
    }

    public bool HasPending
    {
        get { lock (_gate) return _pendingTransitions.Count > 0; }
    }

    public TimeSpan PendingDelay
    {
        get
        {
            lock (_gate)
            {
                if (_pendingTransitions.Count == 0 || _pendingSinceUtc is null) return TimeSpan.Zero;
                TimeSpan remaining = _batchWindow - (_timeProvider.GetUtcNow() - _pendingSinceUtc.Value);
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
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
                _pendingTransitions.Clear();
                _recentPresentations.Clear();
                _pendingSinceUtc = null;
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
            if (transitions.Count > 0)
            {
                if (_pendingTransitions.Count == 0) _pendingSinceUtc = now;
                _pendingTransitions.AddRange(transitions);
            }
            return DrainPending(now, playerCallsign, force: _batchWindow <= TimeSpan.Zero);
        }
    }

    public IReadOnlyList<ContactAnnouncement> FlushPending(string? playerCallsign)
    {
        lock (_gate)
            return DrainPending(_timeProvider.GetUtcNow(), playerCallsign, force: false);
    }

    private IReadOnlyList<ContactAnnouncement> DrainPending(
        DateTimeOffset now,
        string? playerCallsign,
        bool force)
    {
        if (_pendingTransitions.Count == 0 || _pendingSinceUtc is null)
            return Array.Empty<ContactAnnouncement>();
        if (!force && now - _pendingSinceUtc.Value < _batchWindow)
            return Array.Empty<ContactAnnouncement>();

        TransitionedContact[] pending = _pendingTransitions
            .GroupBy(item => item.Track.TrackId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
        _pendingTransitions.Clear();
        _pendingSinceUtc = null;
        return GroupTransitions(pending, playerCallsign, now);
    }

    private IReadOnlyList<ContactAnnouncement> GroupTransitions(
        IReadOnlyList<TransitionedContact> transitions,
        string? playerCallsign,
        DateTimeOffset now)
    {
        PositionedTransition[] positioned = transitions
            .Select(transition => new PositionedTransition(
                transition,
                _positionReports.Describe(transition.Track.EstimatedPosition)))
            .OrderBy(item => item.Transition.Track.TrackId, StringComparer.Ordinal)
            .ToArray();
        List<List<PositionedTransition>> groups = new();
        foreach (PositionedTransition transition in positioned)
        {
            List<PositionedTransition>? group = groups.FirstOrDefault(candidate => candidate.All(item =>
                item.Transition.Kind == transition.Transition.Kind &&
                (ContactPresentationPolicy.CanCluster(
                     item.Transition.Track,
                     transition.Transition.Track) ||
                 (ContactNoun(item.Transition.Track.ContactType) ==
                  ContactNoun(transition.Transition.Track.ContactType) &&
                  item.Transition.Track.Relationship == transition.Transition.Track.Relationship &&
                  string.Equals(
                      item.Position.Text,
                      transition.Position.Text,
                      StringComparison.Ordinal)))));
            if (group is null) groups.Add(group = new List<PositionedTransition>());
            group.Add(transition);
        }

        string callsign = SafeCallsign(playerCallsign);
        _recentPresentations.RemoveAll(item =>
            now - item.AnnouncedAtUtc > ContactPresentationPolicy.SimilarAnnouncementCooldown);
        List<ContactAnnouncement> announcements = new();
        foreach (List<PositionedTransition> group in groups)
        {
            PositionedTransition first = group[0];
            WorldPosition center = new(
                group.Average(item => item.Transition.Track.EstimatedPosition.X),
                group.Average(item => item.Transition.Track.EstimatedPosition.Y),
                group.Average(item => item.Transition.Track.EstimatedPosition.Z));
            string nounKey = string.Join(
                "|",
                group.Select(item => ContactNoun(item.Transition.Track.ContactType))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(item => item, StringComparer.Ordinal));
            bool recentlyPresented = _recentPresentations.Any(item =>
                item.Relationship == first.Transition.Track.Relationship &&
                item.NounKey == nounKey &&
                ContactPresentationPolicy.Distance(item.Center, center) <=
                ContactPresentationPolicy.ClusterRadiusMeters);
            if (recentlyPresented) continue;

            TacticalPositionDescription position = group.All(item =>
                    string.Equals(item.Position.Text, first.Position.Text, StringComparison.Ordinal))
                ? first.Position
                : _positionReports.Describe(center);
            string classification = first.Transition.Track.Relationship == "unknown" ? "unknown" : "enemy";
            string subject = ContactSubject(group.Select(item => item.Transition).ToArray());
            string body = first.Transition.Kind == "reacquired"
                ? $"Previously reported {classification} {subject} reacquired, {position.Text}."
                : $"{Capitalize(classification)} {subject}, {position.Text}.";
            string visible = callsign.Length == 0 ? body : $"{callsign}, {LowercaseFirst(body)}";
            announcements.Add(new ContactAnnouncement(
                first.Transition.Track.TrackId,
                first.Transition.Kind,
                position.Grid,
                visible));
            _recentPresentations.Add(new PresentationState(
                first.Transition.Track.Relationship,
                nounKey,
                center,
                now));
        }
        return announcements;
    }

    private static string ContactSubject(IReadOnlyList<TransitionedContact> group)
    {
        string[] nouns = group.Select(item => ContactNoun(item.Track.ContactType))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (group.Count > 1 && nouns.Length == 1 && nouns[0] != "infantry")
            return $"{nouns[0]} group, {CountWord(group.Count)} {Plural(nouns[0])}";

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
    private static string CountWord(int value) => value switch
    {
        2 => "two", 3 => "three", 4 => "four", 5 => "five", 6 => "six",
        7 => "seven", 8 => "eight", 9 => "nine", 10 => "ten", 11 => "eleven", 12 => "twelve",
        _ => value.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    private sealed record TrackState(string Status, DateTimeOffset? LostSinceUtc);
    private sealed record TransitionedContact(MissionContactTrack Track, string Kind);
    private sealed record PositionedTransition(
        TransitionedContact Transition,
        TacticalPositionDescription Position);
    private sealed record PresentationState(
        string Relationship,
        string NounKey,
        WorldPosition Center,
        DateTimeOffset AnnouncedAtUtc);
    private sealed class GridOnlyPositionReporter : ITacticalPositionReporter
    {
        public TacticalPositionDescription Describe(WorldPosition target)
        {
            string grid = TacticalPositionReportingService.Grid(target);
            return new TacticalPositionDescription($"grid {grid}", grid, "grid", string.Empty);
        }
    }
}
