using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class OperationalSnapshotBuilder
{
    public const string Schema = "arma-ai-bridge/operational-snapshot-v1";
    private readonly IStateRepository _repository;
    private readonly EnvironmentInterpretationService _environment = new();
    private readonly LoadoutSummaryService _loadout = new();
    private readonly ForceSummaryService _force = new();
    private readonly ContactSummaryService _contacts = new();
    public OperationalSnapshotBuilder(IStateRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public Dictionary<string, object?> Build(WorldStateView view, MapGazetteerSnapshot gazetteer)
    {
        StatePlayer? player = _repository.GetPlayer();
        if (player is null) throw new InvalidOperationException("The canonical player state is unavailable.");
        StateRepositoryDiagnostics diagnostics = _repository.GetDiagnostics();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema"] = Schema,
            ["world"] = new { name = diagnostics.WorldName, sizeMeters = view.Map?.SizeMeters ?? 0 },
            ["player"] = Player(player),
            ["namedLocations"] = NamedLocations(player.PositionAtl, gazetteer),
            ["environment"] = Environment(),
            ["time"] = Time(),
            ["loadout"] = Loadout(),
            ["friendlyForces"] = FriendlyForces(player.PositionAtl),
            ["knownContacts"] = KnownContacts(),
            ["tasks"] = Tasks(player.PositionAtl),
            ["markers"] = Markers(player.PositionAtl),
            ["capabilities"] = Capabilities(gazetteer)
        };
    }

    private static object Capabilities(MapGazetteerSnapshot gazetteer)
        => new
        {
            terrainObjectQuery = false,
            officialNamedLocations = gazetteer.Readiness is MapGazetteerReadiness.Ready or MapGazetteerReadiness.Empty,
            friendlyForcePicture = true,
            supportExecution = false
        };

    private static object Player(StatePlayer player)
    {
        Dictionary<string, object?> value = new(StringComparer.Ordinal)
        {
            ["side"] = player.Side,
            ["position"] = player.PositionAtl,
            ["grid"] = player.Grid,
            ["elevationAslMeters"] = Math.Round(player.PositionAsl.Z, 1)
        };
        if (!string.IsNullOrWhiteSpace(player.GroupCallsign)) value["groupCallsign"] = player.GroupCallsign;
        AddSemanticState(value, player.Metadata);
        return value;
    }

    private static object NamedLocations(WorldPosition player, MapGazetteerSnapshot gazetteer)
    {
        if (gazetteer.Readiness is not (MapGazetteerReadiness.Ready or MapGazetteerReadiness.Empty))
            return new { unavailable = true };
        object[] records = gazetteer.Locations
            .Select(location => new
            {
                location,
                distance = Distance(player, new WorldPosition(location.X, location.Y, 0))
            })
            .OrderBy(item => item.distance)
            .ThenBy(item => item.location.Name, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(item => (object)new
            {
                name = item.location.Name,
                type = item.location.Type,
                position = new WorldPosition(item.location.X, item.location.Y, 0),
                distanceMeters = Math.Round(item.distance)
            })
            .ToArray();
        return records.Length == 0 ? new { count = 0 } : new { count = records.Length, records };
    }

    private object Environment()
    {
        StateEnvironment? state = _repository.GetEnvironment();
        if (state is null) return new { unavailable = true };
        EnvironmentInterpretation interpretation = _environment.Interpret(state, _repository.GetTimeAstronomy());
        Dictionary<string, object?> value = new(StringComparer.Ordinal)
        {
            ["overcast"] = state.Overcast,
            ["rain"] = state.Rain,
            ["rainDescription"] = interpretation.RainClassification,
            ["fog"] = state.Fog,
            ["fogDescription"] = interpretation.FogClassification,
            ["wind"] = new
            {
                speedMetersPerSecond = interpretation.WindSpeedMetersPerSecond,
                fromBearingDegrees = interpretation.WindBearingDegrees,
                fromDirection = interpretation.WindCardinalDirection
            },
            ["lightning"] = state.Lightning
        };
        if (state.TemperatureCelsius.HasValue) value["temperatureCelsius"] = state.TemperatureCelsius.Value;
        AddSemanticState(value, state.Metadata);
        return value;
    }

    private object Time()
    {
        StateTimeAstronomy? state = _repository.GetTimeAstronomy();
        if (state is null) return new { unavailable = true };
        Dictionary<string, object?> value = new(StringComparer.Ordinal)
        {
            ["missionDate"] = state.MissionDate,
            ["daytime"] = state.Daytime,
            ["daylight"] = state.SunOrMoon < 0.2 ? "dark" : state.Daytime switch
            {
                < 6 => "dawn", < 18 => "daylight", < 20 => "dusk", _ => "dark"
            },
            ["timeMultiplier"] = state.TimeMultiplier,
            ["moonPhase"] = state.MoonPhase
        };
        AddSemanticState(value, state.Metadata);
        return value;
    }

    private object Loadout()
    {
        StateLoadout? state = _repository.GetLoadout();
        if (state is null) return new { unavailable = true };
        LoadoutSummary summary = _loadout.Summarize(state);
        Dictionary<string, object?> value = new(StringComparer.Ordinal)
        {
            ["loadedRounds"] = summary.LoadedRounds,
            ["reserveMagazines"] = summary.ReserveMagazines,
            ["reserveRounds"] = summary.ReserveRounds,
            ["grenades"] = summary.Grenades,
            ["throwables"] = summary.Throwables,
            ["mines"] = summary.Mines,
            ["explosives"] = summary.Explosives
        };
        AddText(value, "currentWeapon", summary.CurrentWeapon);
        AddText(value, "currentWeaponDisplayName", summary.CurrentWeaponDisplayName);
        string[] attachments = summary.OpticsAndAttachments.Where(item => !string.IsNullOrWhiteSpace(item)).Take(8).ToArray();
        if (attachments.Length > 0) value["attachments"] = attachments;
        object[] magazines = state.MagazineTotals
            .OrderByDescending(item => string.Equals(item.Class, state.CurrentMagazine, StringComparison.Ordinal))
            .ThenByDescending(item => item.Rounds)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(item => (object)new
            {
                name = string.IsNullOrWhiteSpace(item.DisplayName) ? item.Class : item.DisplayName,
                item.MagazineCount,
                item.Rounds
            })
            .ToArray();
        if (magazines.Length > 0) value["magazineSummary"] = magazines;
        AddSemanticState(value, state.Metadata);
        return value;
    }

    private object FriendlyForces(WorldPosition player)
    {
        IReadOnlyList<StateFriendlyGroup> groups = _repository.GetFriendlyGroups(100, includeStale: true);
        IReadOnlyList<StateFriendlyUnit> units = _repository.GetFriendlyUnits(100, includeStale: true);
        ForceSummary summary = _force.Summarize(groups, units);
        Dictionary<string, object?> value = new(StringComparer.Ordinal)
        {
            ["summary"] = new
            {
                summary.GroupCount,
                summary.UnitCount,
                summary.WoundedCount,
                summary.IncapacitatedCount,
                summary.DeadCount
            }
        };
        object[] records = groups
            .OrderBy(item => Distance(player, item.LeaderPosition))
            .ThenBy(item => item.Callsign, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(item => (object)Group(item))
            .ToArray();
        if (records.Length > 0) value["groups"] = records;
        if (summary.IsStale) { value["stale"] = true; value["lastKnown"] = true; }
        return value;
    }

    private static object Group(StateFriendlyGroup group)
    {
        Dictionary<string, object?> value = new(StringComparer.Ordinal)
        {
            ["memberCount"] = group.MemberAliases.Count,
            ["leaderPosition"] = group.LeaderPosition
        };
        AddText(value, "callsign", group.Callsign);
        AddText(value, "behaviour", group.Behaviour);
        AddText(value, "combatMode", group.CombatMode);
        AddText(value, "formation", group.Formation);
        if (group.Waypoint is not null)
            value["waypoint"] = new { group.Waypoint.Position, group.Waypoint.Type };
        AddSemanticState(value, group.Metadata);
        return value;
    }

    private object KnownContacts()
    {
        StateKnownContact[] contacts = _repository.GetKnownContacts(100, includeStale: true)
            .Where(ContactEligibilityPolicy.IsEligible).ToArray();
        ContactSummary summary = _contacts.Summarize(contacts);
        Dictionary<string, object?> value = new(StringComparer.Ordinal)
        {
            ["summary"] = new
            {
                summary.KnownContactCount,
                summary.StaleContactCount,
                summary.ByRelationship,
                summary.ByContactType
            }
        };
        object[] records = contacts
            .OrderBy(item => item.Metadata.IsStale)
            .ThenBy(item => item.LastSeenAgeSeconds < 0 ? double.MaxValue : item.LastSeenAgeSeconds)
            .ThenBy(item => item.PositionErrorMeters)
            .Take(8)
            .Select(item => (object)Contact(item))
            .ToArray();
        if (records.Length > 0) value["contacts"] = records;
        return value;
    }

    private static object Contact(StateKnownContact contact)
    {
        Dictionary<string, object?> value = new(StringComparer.Ordinal)
        {
            ["estimatedPosition"] = contact.EstimatedPosition,
            ["positionErrorMeters"] = contact.PositionErrorMeters,
            ["approximate"] = contact.PositionErrorMeters > 0
        };
        AddText(value, "description", ContactEligibilityPolicy.Description(contact));
        AddText(value, "type", contact.ContactType);
        AddText(value, "perceivedSide", contact.PerceivedSide);
        AddText(value, "relationship", contact.Relationship);
        if (contact.LastSeenAgeSeconds >= 0) value["lastSeenSecondsAgo"] = contact.LastSeenAgeSeconds;
        AddSemanticState(value, contact.Metadata);
        return value;
    }

    private object Tasks(WorldPosition player)
    {
        IReadOnlyList<StateTask> tasks = _repository.GetTasks(100, includeStale: true);
        StateTask? active = tasks.FirstOrDefault(item => item.Active);
        StateTask[] additional = tasks.Where(item => !ReferenceEquals(item, active))
            .OrderByDescending(item => item.Active)
            .ThenBy(item => item.Destination is null ? double.MaxValue : Distance(player, item.Destination))
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        Dictionary<string, object?> value = new(StringComparer.Ordinal) { ["count"] = tasks.Count };
        if (active is not null) value["active"] = Task(active);
        if (additional.Length > 0) value["additional"] = additional.Select(Task).ToArray();
        return value;
    }

    private static object Task(StateTask task)
    {
        Dictionary<string, object?> value = new(StringComparer.Ordinal) { ["active"] = task.Active };
        AddText(value, "title", Truncate(task.Title, 160));
        AddText(value, "description", Truncate(task.Description, 256));
        AddText(value, "type", task.Type);
        AddText(value, "status", task.Status);
        if (task.Destination is not null) value["destination"] = task.Destination;
        AddSemanticState(value, task.Metadata);
        return value;
    }

    private object Markers(WorldPosition player)
    {
        IReadOnlyList<StateMarker> markers = _repository.GetMarkers(100, includeStale: true);
        object[] records = markers
            .OrderByDescending(item => !string.IsNullOrWhiteSpace(item.Text))
            .ThenBy(item => Distance(player, item.Position))
            .ThenBy(item => item.Text, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(item => (object)Marker(item))
            .ToArray();
        return records.Length == 0
            ? new { count = 0 }
            : new { count = markers.Count, records };
    }

    private static object Marker(StateMarker marker)
    {
        Dictionary<string, object?> value = new(StringComparer.Ordinal)
        {
            ["position"] = marker.Position,
            ["direction"] = marker.Direction
        };
        AddText(value, "text", Truncate(marker.Text, 160));
        AddText(value, "type", marker.Type);
        AddText(value, "color", marker.Color);
        AddText(value, "shape", marker.Shape);
        AddSemanticState(value, marker.Metadata);
        return value;
    }

    private static void AddSemanticState(IDictionary<string, object?> target, StateSectionMetadata metadata)
    {
        if (metadata.IsStale || metadata.Readiness == StateSectionReadiness.Stale)
        {
            target["stale"] = true;
            target["lastKnown"] = true;
        }
        else if (metadata.Readiness is StateSectionReadiness.Unavailable or StateSectionReadiness.Failed)
        {
            target["unavailable"] = true;
        }
    }

    private static void AddText(IDictionary<string, object?> target, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) target[key] = value.Trim();
    }

    private static double Distance(WorldPosition from, WorldPosition? to)
        => to is null ? double.PositiveInfinity : Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));

    private static string Truncate(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
