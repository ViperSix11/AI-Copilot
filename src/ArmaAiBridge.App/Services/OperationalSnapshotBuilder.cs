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
        => _inner.Build(string.Empty, commitDialogueFocus: false);

    public Dictionary<string, object?> Build(string question) => _inner.Build(question);
    public Dictionary<string, object?> BuildPreview(string question) => _inner.Build(question, commitDialogueFocus: false);
    public void ResetDialogueFocus() => _inner.ResetDialogueFocus();
}

public sealed class TacticalSnapshotBuilder
{
    public const string Schema = "arma-ai-bridge/tactical-snapshot-v2";
    public const int MaximumPayloadBytes = 256 * 1024;
    private readonly IStateRepository _state;
    private readonly IMissionMemoryRepository? _memory;
    private readonly TimeProvider _timeProvider;
    private readonly TacticalPositionReportingService _positionReports;
    private readonly object _focusGate = new();
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
        _positionReports = new TacticalPositionReportingService(state);
    }

    public void ResetDialogueFocus()
    {
        lock (_focusGate)
        {
            _focusMission = string.Empty;
            _lastQuestion = string.Empty;
            _friendlyFocus = string.Empty;
            _hostileFocus = string.Empty;
        }
    }

    public Dictionary<string, object?> Build(string question, bool commitDialogueFocus = true)
    {
        StatePlayer player = _state.GetPlayer() ?? throw new InvalidOperationException("The canonical player state is unavailable.");
        object dialogueFocus = UpdateDialogueFocus(question, player, commitDialogueFocus);
        (object friendlyForces, int groups, int units) = BuildFriendlies(player);
        (object enemyContacts, int contacts) = BuildContacts();
        object[] objectives = BuildObjectives();
        object[] markers = BuildMarkers();
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
            ["objectives"] = new { count = objectives.Length, records = objectives },
            ["markers"] = new { count = markers.Length, records = markers },
            ["retrievedMemory"] = new { count = memories.Length, records = memories, dialogueFocus },
            ["lore"] = new { count = lore.Length, sections = lore, untrustedContext = true },
            ["modelPayloadTruncated"] = false,
            ["includedCounts"] = new
            {
                friendlyGroups = new { original = groups, included = groups },
                friendlyUnits = new { original = units, included = units },
                enemyContacts = new { original = contacts, included = contacts },
                objectives = new { original = objectives.Length, included = objectives.Length },
                markers = new { original = markers.Length, included = markers.Length },
                memoryEntries = new { original = memories.Length, included = memories.Length },
                loreSections = new { original = lore.Length, included = lore.Length }
            }
        };
    }

    private object UpdateDialogueFocus(string question, StatePlayer player, bool commit)
    {
        lock (_focusGate) return UpdateDialogueFocusCore(question, player, commit);
    }

    private object UpdateDialogueFocusCore(string question, StatePlayer player, bool commit)
    {
        string mission = _memory?.ActiveMissionKey ?? string.Empty;
        string lastQuestion = _lastQuestion;
        string friendlyFocus = _friendlyFocus;
        string hostileFocus = _hostileFocus;
        if (!string.Equals(mission, _focusMission, StringComparison.Ordinal))
        {
            lastQuestion = friendlyFocus = hostileFocus = string.Empty;
        }
        string normalized = (question ?? string.Empty).Trim();
        string resolved = normalized;
        int correction = normalized.IndexOf(", not ", StringComparison.OrdinalIgnoreCase);
        if (correction > 0 && lastQuestion.Length > 0)
        {
            string replacement = normalized[..correction].Trim();
            string rejected = normalized[(correction + 6)..].Trim().TrimEnd('.', '!', '?');
            resolved = lastQuestion.Replace(rejected, replacement, StringComparison.OrdinalIgnoreCase);
        }

        StateFriendlyGroup[] groups = _state.GetFriendlyGroups(128, true).ToArray();
        StateFriendlyGroup? named = groups.FirstOrDefault(x => x.Callsign.Length > 0 && normalized.Contains(x.Callsign, StringComparison.OrdinalIgnoreCase));
        if (named is not null) friendlyFocus = named.Callsign;
        else if (ContainsAny(normalized, "friendlies nearby", "friendly nearby", "nearest friendly", "nearby friendlies"))
        {
            StateFriendlyGroup? nearest = groups.Where(x => !string.Equals(x.Callsign, player.GroupCallsign, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => Distance(player.PositionAtl, x.LeaderPosition)).ThenBy(x => x.Callsign, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (nearest is not null) friendlyFocus = nearest.Callsign;
        }
        if (ContainsAny(resolved, "hostile", "enemy", "contact"))
            hostileFocus = _memory?.GetContactTracks(256).FirstOrDefault()?.TrackId ?? hostileFocus;
        if (commit)
        {
            _focusMission = mission;
            _lastQuestion = resolved;
            _friendlyFocus = friendlyFocus;
            _hostileFocus = hostileFocus;
        }
        return new
        {
            resolvedQuestion = resolved == normalized ? null : resolved,
            friendlyGroupCallsign = friendlyFocus.Length == 0 ? null : friendlyFocus,
            hostileContactReference = hostileFocus.Length == 0 ? null : hostileFocus
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
        return new
        {
            overcast = Round(state.Overcast, 2),
            condition = EnvironmentInterpretationService.Classify(state.Overcast)
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
            Dictionary<string, object?> record = new(StringComparer.Ordinal)
            {
                ["callsign"] = string.IsNullOrWhiteSpace(group.Callsign) ? null : group.Callsign,
                ["memberCount"] = group.MemberAliases.Count,
                ["elementType"] = ElementType(members),
                ["compositionSummary"] = Composition(members),
                ["stale"] = group.Metadata.IsStale
            };
            bool playerGroup = string.Equals(group.Alias, player.GroupAlias, StringComparison.Ordinal) ||
                               (group.Callsign.Length > 0 && string.Equals(group.Callsign, player.GroupCallsign, StringComparison.OrdinalIgnoreCase));
            if (!playerGroup)
            {
                record["approximatePosition"] = new { x = RoundTo(group.LeaderPosition.X, 10), y = RoundTo(group.LeaderPosition.Y, 10), precisionMeters = 10 };
                record["positionDescription"] = _positionReports.Describe(group.LeaderPosition).Text;
            }
            return (object)record;
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

    private object[] BuildObjectives() => _state.GetTasks(32, includeStale: true)
        .Where(task => task.Active && task.Destination is not null)
        .Take(32)
        .Select(task => (object)new
        {
            title = Truncate(string.IsNullOrWhiteSpace(task.Title) ? task.Description.Trim() : task.Title.Trim(), 160),
            type = Truncate(task.Type, 80),
            status = Truncate(task.Status, 80),
            positionDescription = _positionReports.Describe(task.Destination!).Text,
            stale = task.Metadata.IsStale
        }).ToArray();

    private (object Snapshot, int Contacts) BuildContacts()
    {
        MissionContactTrack[] tracks = (_memory?.GetContactTracks(256) ?? Array.Empty<MissionContactTrack>()).Take(256).ToArray();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        object[] records = tracks.Select(track =>
        {
            return (object)new
            {
                contactTrackReference = track.TrackId,
                contactType = track.ContactType,
                perceivedSide = track.PerceivedSide,
                description = track.Description,
                status = track.Status,
                focused = string.Equals(track.TrackId, _hostileFocus, StringComparison.Ordinal),
                estimatedPosition = new { x = track.EstimatedPosition.X, y = track.EstimatedPosition.Y, z = track.EstimatedPosition.Z },
                positionDescription = _positionReports.Describe(track.EstimatedPosition).Text,
                positionUncertaintyMeters = track.UncertaintyRadiusMeters,
                lastSeenSecondsAgo = Math.Max(0, Math.Round((now - track.LastObservedAtUtc).TotalSeconds)),
                lastThreatSecondsAgo = Math.Max(0, Math.Round((now - track.LastThreatAtUtc).TotalSeconds)),
                stale = track.Status != "current",
                corroborated = track.Corroborated,
                observationCount = track.ObservationCount,
                reporterCallsigns = track.ReporterCallsigns
            };
        }).ToArray();
        object[] groups = GroupContacts(tracks);
        return (new
        {
            summary = new
            {
                currentEnemyContactCount = tracks.Count(x => x.Relationship == "hostile" && x.Status == "current"),
                lastKnownEnemyContactCount = tracks.Count(x => x.Relationship == "hostile" && x.Status == "last-known"),
                confirmedDeadEnemyContactCount = tracks.Count(x => x.Relationship == "hostile" && x.Status == "dead"),
                currentUnknownContactCount = tracks.Count(x => x.Relationship == "unknown" && x.Status == "current"),
                lastKnownUnknownContactCount = tracks.Count(x => x.Relationship == "unknown" && x.Status == "last-known"),
                byPerceivedSide = tracks.GroupBy(x => x.PerceivedSide).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal),
                byContactType = tracks.GroupBy(x => x.ContactType).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal)
            },
            groups,
            records
        }, tracks.Length);
    }

    private object[] BuildMarkers() => _state.GetMarkers(512, includeStale: true)
        .Where(marker => !string.IsNullOrWhiteSpace(marker.Text) && marker.Text.Any(char.IsLetterOrDigit))
        .Take(256)
        .Select(marker => (object)new
        {
            text = Truncate(marker.Text.Trim(), 160),
            type = marker.Type,
            color = marker.Color,
            shape = marker.Shape,
            size = marker.Size.Take(2).Select(value => Round(Math.Max(0, value), 2)).ToArray(),
            direction = marker.Direction,
            positionDescription = _positionReports.Describe(marker.Position).Text,
            approximatePosition = new { x = RoundTo(marker.Position.X, 10), y = RoundTo(marker.Position.Y, 10), precisionMeters = 10 },
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

    private object[] GroupContacts(MissionContactTrack[] contacts)
    {
        List<List<MissionContactTrack>> groups = new();
        foreach (MissionContactTrack contact in contacts.Where(x => x.Status != "dead").OrderBy(x => x.TrackId, StringComparer.Ordinal))
        {
            List<MissionContactTrack>? group = groups.FirstOrDefault(g => g.All(x =>
                string.Equals(x.Relationship, contact.Relationship, StringComparison.Ordinal) &&
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
                memberTrackReferences = group.Select(item => item.TrackId).ToArray(),
                description = GroupDescription(group),
                grid = Grid(center),
                positionDescription = _positionReports.Describe(center).Text,
                estimatedCentroid = new { x = RoundTo(center.X, 10), y = RoundTo(center.Y, 10) },
                positionUncertaintyMeters = Math.Ceiling(group.Max(x => x.UncertaintyRadiusMeters) / 10) * 10,
                status = group.Any(x => x.Status == "current") ? "current" : "last-known",
                lastSeenSecondsAgo = Math.Max(0, Math.Round((_timeProvider.GetUtcNow() - group.Max(x => x.LastObservedAtUtc)).TotalSeconds))
            };
        }).ToArray();
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
    private static string GroupDescription(IReadOnlyList<MissionContactTrack> group)
    {
        string[] nouns = group.GroupBy(item => GroupNoun(item.ContactType), StringComparer.Ordinal)
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Key == "infantry" && item.Count() > 1
                ? "infantry group"
                : item.Count() > 1 && item.Key is not ("infantry" or "aircraft")
                    ? item.Key + "s"
                    : item.Key)
            .ToArray();
        string subject = nouns.Length switch
        {
            1 => nouns[0],
            2 => $"{nouns[0]} and {nouns[1]}",
            _ => $"{string.Join(", ", nouns[..^1])} and {nouns[^1]}"
        };
        return $"{group[0].Relationship} {subject}";
    }
    private static string NumberWord(int value) => value switch
    { 0 => "no", 1 => "one", 2 => "two", 3 => "three", 4 => "four", 5 => "five", 6 => "six", 7 => "seven", 8 => "eight", 9 => "nine", 10 => "ten", 11 => "eleven", 12 => "twelve", _ => value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    private static string Grid(WorldPosition value) => $"{Math.Clamp((int)Math.Floor(value.X / 100), 0, 999):000}{Math.Clamp((int)Math.Floor(value.Y / 100), 0, 999):000}";

    private static HashSet<string> Terms(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(x => new string(x.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()).Where(x => x.Length > 2).ToHashSet(StringComparer.Ordinal);
    private static double Distance(WorldPosition a, WorldPosition b) => Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
    private static double Round(double value, int digits) => Math.Round(value, digits);
    private static double RoundTo(double value, double increment) => Math.Round(value / increment) * increment;
    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
