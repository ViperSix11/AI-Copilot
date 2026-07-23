using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class StateQueryService
{
    private static readonly HashSet<string> Sections = new(StringComparer.Ordinal)
    { "environment", "time", "loadout", "friendly_forces", "contacts", "tasks", "markers", "named_locations" };
    private readonly IStateRepository _repository;
    private readonly EnvironmentInterpretationService _environment = new();
    private readonly LoadoutSummaryService _loadout = new();
    private readonly ForceSummaryService _force = new();
    private readonly ContactSummaryService _contacts = new();

    public StateQueryService(IStateRepository repository) => _repository = repository;

    public string Query(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("query_state arguments must be an object.");
        foreach (JsonProperty property in arguments.EnumerateObject())
            if (property.Name is not ("section" or "includeStale" or "limit")) throw new InvalidOperationException("query_state contains an unsupported argument.");
        string section = ReadString(arguments, "section");
        if (!Sections.Contains(section)) throw new InvalidOperationException("Unsupported state section.");
        bool includeStale = ReadBoolean(arguments, "includeStale");
        int limit = ReadInteger(arguments, "limit", 1, 100);
        object? data = section switch
        {
            "environment" => _repository.GetEnvironment() is { } environment ? _environment.Interpret(environment, _repository.GetTimeAstronomy()) : null,
            "time" => _repository.GetTimeAstronomy(),
            "loadout" => _repository.GetLoadout() is { } loadout ? _loadout.Summarize(loadout) : null,
            "friendly_forces" => Friendly(limit, includeStale),
            "contacts" => Contacts(limit, includeStale),
            "tasks" => _repository.GetTasks(limit, includeStale).Select(item => new { item.Alias, title = Truncate(item.Title, 160), description = Truncate(item.Description, 256), item.Destination, item.Type, item.Status, item.ParentAlias, item.Active, item.Metadata.AgeSeconds, item.Metadata.IsStale }),
            "markers" => _repository.GetMarkers(limit, includeStale).Select(item => new { item.Alias, text = Truncate(item.Text, 160), item.ReferenceRole, item.ReferenceLabel, item.Channel, item.Position, item.Type, item.Color, item.Shape, item.Size, item.Direction, item.Alpha, polyline = item.Polyline.Take(32), item.Metadata.AgeSeconds, item.Metadata.IsStale }),
            "named_locations" => _repository.GetNamedLocations(limit: limit).Select(item => new { item.Name, item.Type, position = new[] { item.X, item.Y }, item.RadiusA, item.RadiusB, item.Angle }),
            _ => null
        };
        return JsonSerializer.Serialize(new { schema = WorldSnapshotBuilder.SnapshotSchema, purpose = "query-state", query = new { section, includeStale, limit }, data });
    }

    private object Friendly(int limit, bool includeStale)
    {
        IReadOnlyList<StateFriendlyGroup> groups = _repository.GetFriendlyGroups(limit, includeStale);
        IReadOnlyList<StateFriendlyUnit> units = _repository.GetFriendlyUnits(limit, includeStale);
        return new { summary = _force.Summarize(groups, units), groups, units };
    }
    private object Contacts(int limit, bool includeStale)
    {
        StateKnownContact[] contacts = _repository.GetKnownContacts(limit, includeStale)
            .Where(ContactEligibilityPolicy.IsEligible).ToArray();
        return new
        {
            summary = _contacts.Summarize(contacts),
            contacts = contacts.Select(item => new
            {
                description = ContactEligibilityPolicy.Description(item),
                item.ContactType,
                item.PerceivedSide,
                item.Relationship,
                item.EstimatedPosition,
                item.PositionErrorMeters,
                item.LastSeenAgeSeconds,
                item.LastThreatAgeSeconds,
                item.Metadata.AgeSeconds,
                item.Metadata.IsStale
            })
        };
    }
    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : throw new InvalidOperationException($"{name} is required.");
    private static bool ReadBoolean(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new InvalidOperationException($"{name} must be a boolean.");
    private static int ReadInteger(JsonElement root, string name, int minimum, int maximum)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) && result >= minimum && result <= maximum ? result : throw new InvalidOperationException($"{name} is out of range.");
    private static string Truncate(string value, int limit) => value.Length <= limit ? value : value[..limit];
}
