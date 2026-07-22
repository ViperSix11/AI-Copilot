using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class StateContextSelector
{
    private static readonly (string Section, string[] Terms)[] Rules =
    {
        ("position", new[] { "where am i", "position", "location", "grid", "coordinate", "wo bin ich", "standort", "koordinate", "gitter" }),
        ("environment", new[] { "weather", "wind", "rain", "fog", "temperature", "wetter", "regen", "nebel", "temperatur" }),
        ("time", new[] { "time", "dark", "daylight", "moon", "sun", "uhr", "zeit", "dunkel", "tageslicht", "mond", "sonne" }),
        ("loadout", new[] { "weapon", "ammo", "ammunition", "magazine", "grenade", "mine", "equipment", "waffe", "munition", "magazin", "granate", "ausrüstung" }),
        ("friendly_forces", new[] { "friendly", "our forces", "our units", "group", "friendlies", "eigene", "freund", "kräfte", "unsere kräfte", "unsere einheiten", "gruppe" }),
        ("contacts", new[] { "contact", "enemy", "hostile", "threat", "feind", "kontakt", "gegner", "bedrohung" }),
        ("tasks", new[] { "task", "objective", "mission", "auftrag", "ziel", "aufgabe" }),
        ("markers", new[] { "marker", "marking", "map mark", "markierung", "kartenmarker" })
    };

    private static readonly string[] SituationTerms =
    {
        "current situation", "situation report", "sitrep", "operational picture",
        "aktuelle lage", "lagebericht", "gesamtlage"
    };

    private readonly IStateRepository _repository;
    private readonly EnvironmentInterpretationService _environment = new();
    private readonly LoadoutSummaryService _loadout = new();
    private readonly ForceSummaryService _force = new();
    private readonly ContactSummaryService _contacts = new();

    public StateContextSelector(IStateRepository repository) => _repository = repository;

    public StateContextSelection Select(string question)
    {
        string normalized = " " + question.Trim().ToLowerInvariant() + " ";
        bool situation = SituationTerms.Any(term => normalized.Contains(term, StringComparison.Ordinal));
        string[] selected = situation
            ? new[] { "position", "environment", "friendly_forces", "contacts", "tasks" }
            : Rules.Where(rule => rule.Terms.Any(term => normalized.Contains(term, StringComparison.Ordinal)))
                .Select(rule => rule.Section)
                .Distinct(StringComparer.Ordinal)
                .Take(3)
                .ToArray();

        if (selected.Length == 0)
            return new StateContextSelection(selected, new Dictionary<string, object?>());

        Dictionary<string, object?> context = new(StringComparer.Ordinal);
        foreach (string section in selected)
        {
            object? value = section switch
            {
                "position" => null, // The builder places the deterministic location DTO at the root exactly once.
                "environment" => EnvironmentContext(),
                "time" => TimeContext(),
                "loadout" => LoadoutContext(),
                "friendly_forces" => FriendlyContext(situation),
                "contacts" => ContactContext(situation),
                "tasks" => TaskContext(situation),
                "markers" => MarkerContext(),
                _ => null
            };
            if (value is not null) context[section] = value;
        }
        return new StateContextSelection(selected, context);
    }

    private object? EnvironmentContext()
    {
        StateEnvironment? environment = _repository.GetEnvironment();
        if (environment is null) return null;
        EnvironmentInterpretation interpretation = _environment.Interpret(environment, _repository.GetTimeAstronomy());
        Dictionary<string, object?> result = new(StringComparer.Ordinal)
        {
            ["overcast"] = environment.Overcast,
            ["rain"] = environment.Rain,
            ["fog"] = environment.Fog,
            ["wind"] = new
            {
                speedMetersPerSecond = interpretation.WindSpeedMetersPerSecond,
                bearingDegrees = interpretation.WindBearingDegrees,
                cardinalDirection = interpretation.WindCardinalDirection
            },
            ["rainClassification"] = interpretation.RainClassification,
            ["fogClassification"] = interpretation.FogClassification,
            ["lightning"] = environment.Lightning,
            ["freshness"] = Freshness(environment.Metadata)
        };
        if (interpretation.TemperatureCelsius.HasValue)
            result["temperatureCelsius"] = interpretation.TemperatureCelsius.Value;
        if (interpretation.DaytimeDescription != "unknown")
            result["daytimeDescription"] = interpretation.DaytimeDescription;
        return result;
    }

    private object? TimeContext()
    {
        StateTimeAstronomy? time = _repository.GetTimeAstronomy();
        if (time is null) return null;
        return new
        {
            time.MissionDate,
            time.Daytime,
            time.ElapsedMissionTime,
            time.TimeMultiplier,
            time.MoonPhase,
            time.SunOrMoon,
            daylight = time.SunOrMoon < 0.2 ? "dark" : time.Daytime switch
            {
                < 6 => "dawn", < 18 => "daylight", < 20 => "dusk", _ => "dark"
            },
            freshness = Freshness(time.Metadata)
        };
    }

    private object? LoadoutContext()
    {
        StateLoadout? loadout = _repository.GetLoadout();
        if (loadout is null) return null;
        LoadoutSummary summary = _loadout.Summarize(loadout);
        Dictionary<string, object?> result = new(StringComparer.Ordinal)
        {
            ["loadedRounds"] = summary.LoadedRounds,
            ["reserveMagazines"] = summary.ReserveMagazines,
            ["reserveRounds"] = summary.ReserveRounds,
            ["grenades"] = summary.Grenades,
            ["throwables"] = summary.Throwables,
            ["mines"] = summary.Mines,
            ["explosives"] = summary.Explosives,
            ["freshness"] = Freshness(loadout.Metadata)
        };
        AddText(result, "currentWeapon", summary.CurrentWeapon);
        AddText(result, "currentWeaponDisplayName", summary.CurrentWeaponDisplayName);
        if (summary.OpticsAndAttachments.Count > 0)
            result["opticsAndAttachments"] = summary.OpticsAndAttachments;
        return result;
    }

    private object FriendlyContext(bool summaryOnly)
    {
        IReadOnlyList<StateFriendlyGroup> groups = _repository.GetFriendlyGroups(20, includeStale: true);
        IReadOnlyList<StateFriendlyUnit> units = _repository.GetFriendlyUnits(20, includeStale: true);
        ForceSummary summary = _force.Summarize(groups, units);
        Dictionary<string, object?> result = new(StringComparer.Ordinal)
        {
            ["summary"] = new
            {
                summary.GroupCount,
                summary.UnitCount,
                summary.WoundedCount,
                summary.IncapacitatedCount,
                summary.DeadCount,
                freshness = new { summary.AgeSeconds, summary.IsStale }
            }
        };
        if (summaryOnly) return result;
        if (groups.Count > 0) result["groups"] = groups.Select(GroupContext).ToArray();
        if (units.Count > 0) result["units"] = units.Select(UnitContext).ToArray();
        return result;
    }

    private object ContactContext(bool summaryOnly)
    {
        IReadOnlyList<StateKnownContact> contacts = _repository.GetKnownContacts(20, includeStale: true);
        ContactSummary summary = _contacts.Summarize(contacts);
        Dictionary<string, object?> summaryContext = new(StringComparer.Ordinal)
        {
            ["knownContactCount"] = summary.KnownContactCount,
            ["staleContactCount"] = summary.StaleContactCount,
            ["freshness"] = new { summary.AgeSeconds, summary.IsStale }
        };
        if (summary.ByPerceivedSide.Count > 0) summaryContext["byPerceivedSide"] = summary.ByPerceivedSide;
        if (summary.ByBroadType.Count > 0) summaryContext["byBroadType"] = summary.ByBroadType;
        if (summary.NewestContactAgeSeconds.HasValue)
            summaryContext["newestContactAgeSeconds"] = summary.NewestContactAgeSeconds.Value;
        if (summary.MaximumPositionUncertaintyMeters.HasValue)
            summaryContext["maximumPositionUncertaintyMeters"] = summary.MaximumPositionUncertaintyMeters.Value;
        Dictionary<string, object?> result = new(StringComparer.Ordinal) { ["summary"] = summaryContext };
        if (!summaryOnly && contacts.Count > 0)
            result["contacts"] = contacts.Select(ContactRow).ToArray();
        return result;
    }

    private static object ContactRow(StateKnownContact item)
    {
        Dictionary<string, object?> contact = new(StringComparer.Ordinal)
        {
            ["estimatedPosition"] = item.EstimatedPosition,
            ["positionErrorMeters"] = item.PositionErrorMeters,
            ["lastSeenAgeSeconds"] = item.LastSeenAgeSeconds,
            ["lastThreatAgeSeconds"] = item.LastThreatAgeSeconds,
            ["observerGroupCount"] = item.ObserverGroupAliases.Count,
            ["isStale"] = item.Metadata.IsStale
        };
        AddText(contact, "class", item.Class);
        AddText(contact, "broadType", item.BroadType);
        AddText(contact, "perceivedSide", item.PerceivedSide);
        return contact;
    }

    private object? TaskContext(bool activeOnly)
    {
        IEnumerable<StateTask> tasks = _repository.GetTasks(20, includeStale: true);
        if (activeOnly) tasks = tasks.Where(item => item.Active).Take(1);
        object[] context = tasks.Select(item =>
        {
            Dictionary<string, object?> task = new(StringComparer.Ordinal)
            {
                ["active"] = item.Active,
                ["freshness"] = Freshness(item.Metadata)
            };
            AddText(task, "title", Truncate(item.Title, 160));
            AddText(task, "description", Truncate(item.Description, 256));
            AddText(task, "type", item.Type);
            AddText(task, "status", item.Status);
            if (item.Destination is not null) task["destination"] = item.Destination;
            return task;
        }).ToArray();
        return context.Length == 0 ? null : context;
    }

    private object? MarkerContext()
    {
        object[] context = _repository.GetMarkers(20, includeStale: true).Select(item =>
        {
            Dictionary<string, object?> marker = new(StringComparer.Ordinal)
            {
                ["position"] = item.Position,
                ["size"] = item.Size,
                ["direction"] = item.Direction,
                ["alpha"] = item.Alpha,
                ["freshness"] = Freshness(item.Metadata)
            };
            AddText(marker, "text", Truncate(item.Text, 160));
            AddText(marker, "type", item.Type);
            AddText(marker, "color", item.Color);
            AddText(marker, "shape", item.Shape);
            if (item.Polyline.Count > 0) marker["polyline"] = item.Polyline.Take(32).ToArray();
            return marker;
        }).ToArray();
        return context.Length == 0 ? null : context;
    }

    private static object GroupContext(StateFriendlyGroup item)
    {
        Dictionary<string, object?> group = new(StringComparer.Ordinal)
        {
            ["memberCount"] = item.MemberAliases.Count,
            ["freshness"] = Freshness(item.Metadata)
        };
        if (item.LeaderPosition is not null) group["leaderPosition"] = item.LeaderPosition;
        AddText(group, "callsign", item.Callsign);
        AddText(group, "behaviour", item.Behaviour);
        AddText(group, "combatMode", item.CombatMode);
        AddText(group, "formation", item.Formation);
        if (item.Waypoint is not null)
        {
            Dictionary<string, object?> waypoint = new(StringComparer.Ordinal)
            {
                ["index"] = item.Waypoint.Index,
                ["position"] = item.Waypoint.Position
            };
            AddText(waypoint, "type", item.Waypoint.Type);
            group["waypoint"] = waypoint;
        }
        if (item.ExpectedDestination is not null) group["expectedDestination"] = item.ExpectedDestination;
        return group;
    }

    private static object UnitContext(StateFriendlyUnit item)
    {
        Dictionary<string, object?> unit = new(StringComparer.Ordinal)
        {
            ["position"] = item.Position,
            ["alive"] = item.Alive,
            ["mobile"] = item.Mobile,
            ["damage"] = item.Damage,
            ["freshness"] = Freshness(item.Metadata)
        };
        AddText(unit, "role", item.DisplayRole);
        AddText(unit, "lifeState", item.LifeState);
        AddText(unit, "currentCommand", item.CurrentCommand);
        return unit;
    }

    private static object Freshness(StateSectionMetadata metadata)
        => new
        {
            readiness = metadata.Readiness.ToString().ToLowerInvariant(),
            metadata.AgeSeconds,
            metadata.IsStale
        };

    private static void AddText(IDictionary<string, object?> target, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) target[name] = value;
    }

    private static string Truncate(string value, int limit) => value.Length <= limit ? value : value[..limit];
}
