using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class StateContextSelector
{
    private static readonly (string Section, string[] Terms)[] Rules =
    {
        ("position", new[] { "where am i", "position", "location", "grid", "wo bin ich", "position", "standort", "koordinate", "gitter" }),
        ("environment", new[] { "weather", "wind", "rain", "fog", "temperature", "wetter", "wind", "regen", "nebel", "temperatur" }),
        ("time", new[] { "time", "dark", "daylight", "moon", "sun", "uhr", "zeit", "dunkel", "tageslicht", "mond", "sonne" }),
        ("loadout", new[] { "weapon", "ammo", "ammunition", "magazine", "grenade", "mine", "waffe", "munition", "magazin", "granate", "mine" }),
        ("friendly_forces", new[] { "friendly", "our forces", "our units", "group", "friendlies", "eigene", "freund", "kräfte", "unsere kräfte", "unsere einheiten", "gruppe" }),
        ("contacts", new[] { "contact", "enemy", "hostile", "threat", "feind", "kontakt", "gegner", "bedrohung" }),
        ("tasks", new[] { "task", "objective", "mission", "auftrag", "ziel", "aufgabe" }),
        ("markers", new[] { "marker", "marking", "karte mark", "markierung", "kartenmarker" })
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
        string[] selected = Rules.Where(rule => rule.Terms.Any(term => normalized.Contains(term, StringComparison.Ordinal)))
            .Select(rule => rule.Section).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        StateEnvironment? environment = _repository.GetEnvironment();
        StateTimeAstronomy? time = _repository.GetTimeAstronomy();
        IReadOnlyList<StateFriendlyGroup> groups = _repository.GetFriendlyGroups(100, includeStale: true);
        IReadOnlyList<StateFriendlyUnit> units = _repository.GetFriendlyUnits(100, includeStale: true);
        IReadOnlyList<StateKnownContact> contacts = _repository.GetKnownContacts(100, includeStale: true);
        StateRepositoryDiagnostics diagnostics = _repository.GetDiagnostics();
        ForceSummary force = _force.Summarize(groups, units);
        ContactSummary contact = _contacts.Summarize(contacts);
        StateTask? activeTask = _repository.GetTasks(32, includeStale: true).FirstOrDefault(item => item.Active);
        Dictionary<string, object?> baseContext = new()
        {
            ["world"] = diagnostics.WorldName,
            ["sessionReadiness"] = diagnostics.Readiness.ToString().ToLowerInvariant(),
            ["environment"] = environment is null ? null : _environment.Interpret(environment, time),
            ["currentTask"] = activeTask is null ? null : new { activeTask.Alias, title = Truncate(activeTask.Title, 160), activeTask.Status, activeTask.Metadata.AgeSeconds, activeTask.Metadata.IsStale },
            ["friendlyCounts"] = new { force.GroupCount, force.UnitCount, force.WoundedCount, force.IncapacitatedCount, force.DeadCount },
            ["contactCounts"] = new { contact.KnownContactCount, contact.ByPerceivedSide, contact.ByBroadType, contact.StaleContactCount },
            ["sectionFreshness"] = diagnostics.Sections.ToDictionary(item => item.Section, item => new { readiness = item.Readiness.ToString().ToLowerInvariant(), item.AgeSeconds, item.IsStale }, StringComparer.Ordinal)
        };
        Dictionary<string, object?> selectedContext = new();
        foreach (string section in selected)
        {
            selectedContext[section] = section switch
            {
                "environment" => environment is null ? null : _environment.Interpret(environment, time),
                "time" => time,
                "loadout" => _repository.GetLoadout() is { } loadout ? _loadout.Summarize(loadout) : null,
                "friendly_forces" => new
                {
                    summary = force,
                    groups = groups.Take(20).Select(item => new { item.Alias, item.Callsign, item.LeaderAlias, memberCount = item.MemberAliases.Count, item.LeaderPosition, item.Behaviour, item.CombatMode, item.Formation, item.Waypoint, item.ExpectedDestination, item.Metadata.AgeSeconds, item.Metadata.IsStale }),
                    units = units.Take(20).Select(item => new { item.Alias, item.GroupAlias, item.DisplayRole, item.Position, item.Alive, item.LifeState, item.Mobile, item.Damage, item.CurrentCommand, item.VehicleAlias, item.VehicleRole, item.Metadata.AgeSeconds, item.Metadata.IsStale })
                },
                "contacts" => new { summary = contact, contacts = contacts.Take(20).Select(item => new { item.Alias, item.Class, item.BroadType, item.PerceivedSide, item.EstimatedPosition, item.PositionErrorMeters, item.LastSeenAgeSeconds, item.LastThreatAgeSeconds, item.ObserverGroupAliases, item.Metadata.IsStale }) },
                "tasks" => _repository.GetTasks(20, true).Select(item => new { item.Alias, title = Truncate(item.Title, 160), description = Truncate(item.Description, 256), item.Destination, item.Type, item.Status, item.ParentAlias, item.Active, item.Metadata.AgeSeconds, item.Metadata.IsStale }),
                "markers" => _repository.GetMarkers(20, true).Select(item => new { item.Alias, text = Truncate(item.Text, 160), item.Position, item.Type, item.Color, item.Shape, item.Size, item.Direction, item.Alpha, polyline = item.Polyline.Take(32), item.Metadata.AgeSeconds, item.Metadata.IsStale }),
                _ => null
            };
        }
        return new StateContextSelection(selected, baseContext, selectedContext);
    }

    private static string Truncate(string value, int limit) => value.Length <= limit ? value : value[..limit];
}
