using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

// Compatibility facade retained for callers compiled against the release 0.8 name.
// The only model-facing state produced here is tactical-snapshot-v2.
public sealed class OperationalSnapshotBuilder
{
    public const string Schema = TacticalSnapshotBuilder.Schema;
    private readonly TacticalSnapshotBuilder _inner;

    public OperationalSnapshotBuilder(IStateRepository repository, TimeProvider? timeProvider = null)
        => _inner = new TacticalSnapshotBuilder(repository, repository as IMissionMemoryRepository, timeProvider);

    public Dictionary<string, object?> Build(WorldStateView view, MapGazetteerSnapshot gazetteer)
        => _inner.Build(string.Empty);

    public Dictionary<string, object?> Build(string question) => _inner.Build(question);
}

public sealed class TacticalSnapshotBuilder
{
    public const string Schema = "arma-ai-bridge/tactical-snapshot-v2";
    public const int MaximumPayloadBytes = 256 * 1024;
    private readonly IStateRepository _state;
    private readonly IMissionMemoryRepository? _memory;
    private readonly TimeProvider _timeProvider;
    private readonly EnvironmentInterpretationService _environment = new();
    private string _focusMission = string.Empty;
    private string _lastQuestion = string.Empty;
    private string _friendlyFocus = string.Empty;
    private string _hostileFocus = string.Empty;

    public TacticalSnapshotBuilder(IStateRepository state, IMissionMemoryRepository? memory = null,
        TimeProvider? timeProvider = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _memory = memory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Dictionary<string, object?> Build(string question)
    {
        StatePlayer player = _state.GetPlayer() ?? throw new InvalidOperationException("The canonical player state is unavailable.");
        object dialogueFocus = UpdateDialogueFocus(question, player);
        (object friendlyForces, int groups, int units) = BuildFriendlies(player);
        (object enemyContacts, int contacts) = BuildContacts(player);
        object[] markers = BuildMarkers(player);
        object[] memories = BuildMemory(question);
        object[] lore = BuildLore(question);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema"] = Schema,
            ["player"] = Player(player),
            ["environment"] = Environment(),
            ["time"] = Time(),
            ["friendlyForces"] = friendlyForces,
            ["enemyContacts"] = enemyContacts,
            ["markers"] = new { count = markers.Length, records = markers },
            ["retrievedMemory"] = new { count = memories.Length, records = memories, dialogueFocus },
            ["lore"] = new { count = lore.Length, sections = lore, untrustedContext = true },
            ["modelPayloadTruncated"] = false,
            ["includedCounts"] = new
            {
                friendlyGroups = new { original = groups, included = groups },
                friendlyUnits = new { original = units, included = units },
                enemyContacts = new { original = contacts, included = contacts },
                markers = new { original = markers.Length, included = markers.Length },
                memoryEntries = new { original = memories.Length, included = memories.Length },
                loreSections = new { original = lore.Length, included = lore.Length }
            }
        };
    }

    private object UpdateDialogueFocus(string question, StatePlayer player)
    {
        string mission = _memory?.ActiveMissionKey ?? string.Empty;
        if (!string.Equals(mission, _focusMission, StringComparison.Ordinal))
        {
            _focusMission = mission; _lastQuestion = _friendlyFocus = _hostileFocus = string.Empty;
        }
        string normalized = (question ?? string.Empty).Trim();
        string resolved = normalized;
        int correction = normalized.IndexOf(", not ", StringComparison.OrdinalIgnoreCase);
        if (correction > 0 && _lastQuestion.Length > 0)
        {
            string replacement = normalized[..correction].Trim();
            string rejected = normalized[(correction + 6)..].Trim().TrimEnd('.', '!', '?');
            resolved = _lastQuestion.Replace(rejected, replacement, StringComparison.OrdinalIgnoreCase);
        }

        StateFriendlyGroup[] groups = _state.GetFriendlyGroups(128, true).ToArray();
        StateFriendlyGroup? named = groups.FirstOrDefault(x => x.Callsign.Length > 0 && normalized.Contains(x.Callsign, StringComparison.OrdinalIgnoreCase));
        if (named is not null) _friendlyFocus = named.Callsign;
        else if (ContainsAny(normalized, "friendlies nearby", "friendly nearby", "nearest friendly", "nearby friendlies"))
        {
            StateFriendlyGroup? nearest = groups.Where(x => !string.Equals(x.Callsign, player.GroupCallsign, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => Distance(player.PositionAtl, x.LeaderPosition)).ThenBy(x => x.Callsign, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (nearest is not null) _friendlyFocus = nearest.Callsign;
        }
        if (ContainsAny(resolved, "hostile", "enemy", "contact"))
            _hostileFocus = _memory?.GetContactTracks(256).FirstOrDefault()?.TrackId ?? _hostileFocus;
        _lastQuestion = resolved;
        return new
        {
            resolvedQuestion = resolved == normalized ? null : resolved,
            friendlyGroupCallsign = _friendlyFocus.Length == 0 ? null : _friendlyFocus,
            hostileContactReference = _hostileFocus.Length == 0 ? null : _hostileFocus
        };
    }

    private static bool ContainsAny(string value, params string[] terms)
        => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static object Player(StatePlayer player)
    {
        Dictionary<string, object?> result = new(StringComparer.Ordinal) { ["side"] = player.Side };
        if (!string.IsNullOrWhiteSpace(player.GroupCallsign)) result["groupCallsign"] = player.GroupCallsign.Trim();
        return result;
    }

    private object Environment()
    {
        StateEnvironment? state = _state.GetEnvironment();
        if (state is null) return new { unavailable = true };
        EnvironmentInterpretation value = _environment.Interpret(state, _state.GetTimeAstronomy());
        return new
        {
            overcast = Round(state.Overcast, 2),
            rain = Round(state.Rain, 2),
            rainDescription = value.RainClassification,
            fog = Round(state.Fog, 2),
            fogDescription = value.FogClassification,
            wind = new
            {
                speedMetersPerSecond = Round(value.WindSpeedMetersPerSecond, 1),
                fromBearingDegrees = value.WindBearingDegrees,
                fromDirection = value.WindCardinalDirection
            },
            temperatureCelsius = state.TemperatureCelsius is null ? (double?)null : Round(state.TemperatureCelsius.Value, 1)
        };
    }

    private object Time()
    {
        StateTimeAstronomy? state = _state.GetTimeAstronomy();
        if (state is null) return new { unavailable = true };
        string daylight = state.SunOrMoon < .2 ? "dark" : state.Daytime switch
        {
            < 6 => "dawn", < 18 => "daylight", < 20 => "dusk", _ => "dark"
        };
        return new { missionDate = state.MissionDate, daytime = Round(state.Daytime, 2), daylight };
    }

    private (object Snapshot, int Groups, int Units) BuildFriendlies(StatePlayer player)
    {
        StateFriendlyGroup[] groups = _state.GetFriendlyGroups(128, includeStale: true).Take(128).ToArray();
        StateFriendlyUnit[] units = _state.GetFriendlyUnits(512, includeStale: true).Take(512).ToArray();
        Dictionary<string, StateFriendlyUnit[]> byGroup = units.GroupBy(x => x.GroupAlias)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        object[] records = groups.Select(group =>
        {
            StateFriendlyUnit[] members = byGroup.GetValueOrDefault(group.Alias) ?? Array.Empty<StateFriendlyUnit>();
            double distance = Distance(player.PositionAtl, group.LeaderPosition);
            return (object)new
            {
                callsign = string.IsNullOrWhiteSpace(group.Callsign) ? null : group.Callsign,
                memberCount = group.MemberAliases.Count,
                elementType = ElementType(members),
                compositionSummary = Composition(members),
                approximatePosition = new { x = RoundTo(group.LeaderPosition.X, 10), y = RoundTo(group.LeaderPosition.Y, 10), precisionMeters = 10 },
                bearingFromPlayerDegrees = Bearing(player.PositionAtl, group.LeaderPosition),
                rangeFromPlayerMeters = RoundTo(distance, 10),
                stale = group.Metadata.IsStale
            };
        }).ToArray();
        int wounded = units.Count(x => x.Alive && x.Damage > 0);
        int incapacitated = units.Count(x => x.Alive && x.LifeState.Contains("INCAPACITATED", StringComparison.OrdinalIgnoreCase));
        int dead = units.Count(x => !x.Alive);
        return (new
        {
            summary = new { groupCount = groups.Length, unitCount = units.Length, woundedCount = wounded, incapacitatedCount = incapacitated, deadCount = dead },
            groups = records
        }, groups.Length, units.Length);
    }

    private (object Snapshot, int Contacts) BuildContacts(StatePlayer player)
    {
        MissionContactTrack[] tracks = (_memory?.GetContactTracks(256) ?? Array.Empty<MissionContactTrack>()).Take(256).ToArray();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        object[] records = tracks.Select(track =>
        {
            MissionContactObservation[] observations = (_memory?.GetContactObservations(track.TrackId, 8) ?? Array.Empty<MissionContactObservation>()).ToArray();
            MovementEstimate movement = Movement(observations);
            return (object)new
            {
                contactTrackReference = track.TrackId,
                contactType = track.ContactType,
                perceivedSide = track.PerceivedSide,
                description = track.Description,
                status = track.Status,
                estimatedPosition = new { x = track.EstimatedPosition.X, y = track.EstimatedPosition.Y, z = track.EstimatedPosition.Z },
                positionUncertaintyMeters = track.UncertaintyRadiusMeters,
                rangeFromPlayerMeters = RoundTo(Distance(player.PositionAtl, track.EstimatedPosition), 50),
                bearingFromPlayerDegrees = Bearing(player.PositionAtl, track.EstimatedPosition),
                direction = Cardinal(Bearing(player.PositionAtl, track.EstimatedPosition)),
                lastSeenSecondsAgo = Math.Max(0, Math.Round((now - track.LastObservedAtUtc).TotalSeconds)),
                lastThreatSecondsAgo = Math.Max(0, Math.Round((now - track.LastThreatAtUtc).TotalSeconds)),
                stale = track.Status != "current",
                movementTrend = movement.Trend,
                estimatedRelativeSpeedMetersPerSecond = movement.RelativeSpeedMetersPerSecond,
                movementConfidence = movement.Confidence,
                corroborated = track.Corroborated,
                observationCount = track.ObservationCount
            };
        }).ToArray();
        object[] groups = GroupContacts(tracks, player.PositionAtl);
        return (new
        {
            summary = new
            {
                currentEnemyContactCount = tracks.Count(x => x.Status == "current"),
                lastKnownEnemyContactCount = tracks.Count(x => x.Status == "last-known"),
                confirmedDeadEnemyContactCount = tracks.Count(x => x.Status == "dead"),
                byPerceivedSide = tracks.GroupBy(x => x.PerceivedSide).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal),
                byContactType = tracks.GroupBy(x => x.ContactType).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal)
            },
            groups,
            records
        }, tracks.Length);
    }

    private object[] BuildMarkers(StatePlayer player) => _state.GetMarkers(256, includeStale: true).Take(256).Select(marker => (object)new
    {
        text = string.IsNullOrWhiteSpace(marker.Text) ? null : Truncate(marker.Text, 160),
        type = marker.Type,
        color = marker.Color,
        shape = marker.Shape,
        direction = marker.Direction,
        approximatePosition = new { x = RoundTo(marker.Position.X, 10), y = RoundTo(marker.Position.Y, 10), precisionMeters = 10 },
        rangeMeters = RoundTo(Distance(player.PositionAtl, marker.Position), 50),
        bearingDegrees = Bearing(player.PositionAtl, marker.Position),
        stale = marker.Metadata.IsStale
    }).ToArray();

    private object[] BuildMemory(string question) => (_memory?.SearchMemory(question, 12, 6000) ?? Array.Empty<MissionMemoryEntry>())
        .Select(entry => (object)new
        {
            id = entry.Id,
            text = entry.Text,
            provenance = entry.Provenance,
            updatedAtUtc = entry.UpdatedAtUtc,
            tags = entry.Tags,
            approximatePosition = entry.Position is null ? null : new { x = RoundTo(entry.Position.X, 10), y = RoundTo(entry.Position.Y, 10) }
        }).ToArray();

    private object[] BuildLore(string question)
    {
        HashSet<string> terms = Terms(question);
        int characters = 0;
        List<object> result = new();
        foreach (LoreSection section in (_memory?.GetLoreSections() ?? Array.Empty<LoreSection>()).Where(x => x.Enabled))
        {
            bool relevant = section.AlwaysInclude || terms.Count == 0 || Terms(section.Content).Overlaps(terms);
            if (!relevant || characters + section.Content.Length > 8000) continue;
            result.Add(new { scope = section.Scope, content = section.Content, untrustedContext = true });
            characters += section.Content.Length;
        }
        return result.ToArray();
    }

    private object[] GroupContacts(MissionContactTrack[] contacts, WorldPosition player)
    {
        List<List<MissionContactTrack>> groups = new();
        foreach (MissionContactTrack contact in contacts.Where(x => x.Status != "dead").OrderBy(x => x.TrackId, StringComparer.Ordinal))
        {
            List<MissionContactTrack>? group = groups.FirstOrDefault(g => g.All(x =>
                string.Equals(x.ContactType, contact.ContactType, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs((x.LastObservedAtUtc - contact.LastObservedAtUtc).TotalSeconds) <= 120 &&
                Distance(x.EstimatedPosition, contact.EstimatedPosition) <= 40));
            if (group is null) groups.Add(group = new List<MissionContactTrack>());
            group.Add(contact);
        }
        return groups.Where(g => g.Count > 1).Select((group, index) =>
        {
            WorldPosition center = new(group.Average(x => x.EstimatedPosition.X), group.Average(x => x.EstimatedPosition.Y), 0);
            return (object)new
            {
                groupReference = $"hostile-group-{index + 1}", memberCount = group.Count,
                description = $"hostile {GroupNoun(group[0].ContactType)} group",
                grid = Grid(center),
                estimatedCentroid = new { x = RoundTo(center.X, 10), y = RoundTo(center.Y, 10) },
                positionUncertaintyMeters = Math.Ceiling(group.Max(x => x.UncertaintyRadiusMeters) / 10) * 10,
                rangeFromPlayerMeters = RoundTo(Distance(player, center), 50), bearingFromPlayerDegrees = Bearing(player, center), direction = Cardinal(Bearing(player, center)),
                status = group.Any(x => x.Status == "current") ? "current" : "last-known",
                lastSeenSecondsAgo = Math.Max(0, Math.Round((_timeProvider.GetUtcNow() - group.Max(x => x.LastObservedAtUtc)).TotalSeconds))
            };
        }).ToArray();
    }

    private static MovementEstimate Movement(IReadOnlyList<MissionContactObservation> observations)
    {
        if (observations.Count < 2) return new("movement unknown", null, "low");
        MissionContactObservation newest = observations[0], oldest = observations[^1];
        double seconds = (newest.ObservedAtUtc - oldest.ObservedAtUtc).TotalSeconds;
        if (seconds < 1) return new("movement unknown", null, "low");
        double displacement = Distance(newest.EstimatedPosition, oldest.EstimatedPosition);
        if (displacement < Math.Max(10, newest.UncertaintyRadiusMeters + oldest.UncertaintyRadiusMeters))
            return new("stationary", 0, "medium");
        if (newest.PlayerPosition is null || oldest.PlayerPosition is null) return new("movement unknown", null, "low");
        double oldRange = Distance(oldest.PlayerPosition, oldest.EstimatedPosition);
        double newRange = Distance(newest.PlayerPosition, newest.EstimatedPosition);
        double relativeSpeed = Math.Round(Math.Abs(newRange - oldRange) / seconds, 1);
        if (newRange < oldRange - 15) return new("closing", relativeSpeed, "medium");
        if (newRange > oldRange + 15) return new("moving away", relativeSpeed, "medium");
        double oldBearing = Bearing(oldest.PlayerPosition, oldest.EstimatedPosition);
        double newBearing = Bearing(newest.PlayerPosition, newest.EstimatedPosition);
        double delta = ((newBearing - oldBearing + 540) % 360) - 180;
        return new(delta < 0 ? "crossing left" : "crossing right", relativeSpeed, "medium");
    }

    private static string ElementType(IReadOnlyList<StateFriendlyUnit> members)
    {
        if (members.Count == 0) return "unknown";
        if (members.Any(x => x.VehicleRole.Length > 0)) return "vehicle-element";
        if (members.Any(x => x.DisplayRole.Contains("medic", StringComparison.OrdinalIgnoreCase))) return "medical team";
        return members.Count switch { <= 2 => "infantry pair", <= 6 => "infantry fireteam", <= 12 => "infantry squad", _ => "support element" };
    }

    private static string Composition(IReadOnlyList<StateFriendlyUnit> members)
        => $"{NumberWord(members.Count(x => x.Alive))} {ElementType(members)}";

    private static string GroupNoun(string type) => type.ToLowerInvariant() switch
    { "person" or "man" => "infantry", "air" or "aircraft" => "aircraft", "ship" or "naval" => "naval", _ => "vehicle" };
    private static string NumberWord(int value) => value switch
    { 0 => "no", 1 => "one", 2 => "two", 3 => "three", 4 => "four", 5 => "five", 6 => "six", 7 => "seven", 8 => "eight", 9 => "nine", 10 => "ten", 11 => "eleven", 12 => "twelve", _ => value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    private static string Grid(WorldPosition value) => $"{Math.Clamp((int)Math.Floor(value.X / 100), 0, 999):000}{Math.Clamp((int)Math.Floor(value.Y / 100), 0, 999):000}";

    private static HashSet<string> Terms(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(x => new string(x.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()).Where(x => x.Length > 2).ToHashSet(StringComparer.Ordinal);
    private static double Distance(WorldPosition a, WorldPosition b) => Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
    private static int Bearing(WorldPosition from, WorldPosition to) => (int)Math.Round((Math.Atan2(to.X - from.X, to.Y - from.Y) * 180 / Math.PI + 360) % 360) % 360;
    private static string Cardinal(int bearing) => new[] { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" }[(int)Math.Round(bearing / 45d) % 8];
    private static double Round(double value, int digits) => Math.Round(value, digits);
    private static double RoundTo(double value, double increment) => Math.Round(value / increment) * increment;
    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
    private sealed record MovementEstimate(string Trend, double? RelativeSpeedMetersPerSecond, string Confidence);
}
