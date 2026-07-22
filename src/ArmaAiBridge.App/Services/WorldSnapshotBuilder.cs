using System.Globalization;
using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class WorldSnapshotBuilder
{
    public const string SnapshotSchema = "arma-ai-bridge/world-snapshot-v1";
    private readonly WorldStateStore _store;
    private readonly TimeProvider _timeProvider;

    public WorldSnapshotBuilder(WorldStateStore store, TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryBuildCurrentSituation(out string json)
    {
        WorldStateView view = _store.GetCurrentView();
        if (!view.HasTelemetry || view.Map is null || view.Player is null)
        {
            json = string.Empty;
            return false;
        }
        json = Serialize(BuildCurrentSituation(view));
        return true;
    }

    public string BuildCurrentSituation()
    {
        WorldStateView view = _store.GetCurrentView();
        if (!view.HasTelemetry || view.Map is null || view.Player is null)
            throw new InvalidOperationException("No world-state telemetry is available yet.");
        return Serialize(BuildCurrentSituation(view));
    }

    public string BuildKnownContacts()
    {
        WorldStateView view = _store.GetCurrentView();
        if (!view.HasTelemetry || view.Map is null || view.Player is null)
            throw new InvalidOperationException("No world-state telemetry is available yet.");

        return Serialize(new Dictionary<string, object?>
        {
            ["schema"] = SnapshotSchema,
            ["purpose"] = "known-contacts",
            ["generatedAtUtc"] = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            ["session"] = Session(view),
            ["map"] = Map(view.Map),
            ["playerReference"] = PlayerReference(view.Player),
            ["knownContacts"] = Contacts(view.KnownContacts)
        });
    }

    public string BuildFriendlyForces(JsonElement arguments)
    {
        WorldStateView view = RequireWorldState();
        string entityType = ReadEnum(arguments, "entityType", "group", "unit", "vehicle", "all");
        double maxDistance = ReadNumber(arguments, "maxDistanceMeters", 100, 50000);
        bool includeStale = ReadBoolean(arguments, "includeStale");
        int limit = (int)ReadNumber(arguments, "limit", 1, 100, integer: true);
        WorldPosition playerPosition = view.Player!.Metadata.Position!;

        IEnumerable<object> groups = view.FriendlyGroups
            .Where(item => Include(item.Metadata, includeStale, playerPosition, maxDistance))
            .Select(item => (object)FriendlyGroup(item, playerPosition));
        IEnumerable<object> units = view.FriendlyUnits
            .Where(item => Include(item.Metadata, includeStale, playerPosition, maxDistance))
            .Select(item => (object)FriendlyUnit(item, playerPosition));
        IEnumerable<object> vehicles = view.FriendlyVehicles
            .Where(item => Include(item.Metadata, includeStale, playerPosition, maxDistance))
            .Select(item => (object)FriendlyVehicle(item, playerPosition));
        object[] entities = entityType switch
        {
            "group" => groups.Take(limit).ToArray(),
            "unit" => units.Take(limit).ToArray(),
            "vehicle" => vehicles.Take(limit).ToArray(),
            _ => groups.Concat(units).Concat(vehicles).Take(limit).ToArray()
        };

        return Serialize(new Dictionary<string, object?>
        {
            ["schema"] = SnapshotSchema,
            ["purpose"] = "friendly-forces",
            ["generatedAtUtc"] = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            ["session"] = Session(view),
            ["query"] = new { entityType, maxDistanceMeters = Round(maxDistance), includeStale, limit },
            ["reconciliation"] = Reconciliation(view.Reconciliation),
            ["entities"] = entities
        });
    }

    public string BuildAssets(JsonElement arguments)
    {
        WorldStateView view = RequireWorldState();
        string kind = ReadEnum(arguments, "kind", "any", "rotary_transport", "ground_transport", "medevac",
            "resupply", "reconnaissance", "vehicle_recovery", "other");
        bool availableOnly = ReadBoolean(arguments, "availableOnly");
        double maxDistance = ReadNumber(arguments, "maxDistanceMeters", 100, 50000);
        bool includeStale = ReadBoolean(arguments, "includeStale");
        int limit = (int)ReadNumber(arguments, "limit", 1, 100, integer: true);
        WorldPosition playerPosition = view.Player!.Metadata.Position!;
        object[] assets = view.SupportAssets
            .Where(item => (kind == "any" || item.Kind == kind) && (!availableOnly || item.Available))
            .Where(item => Include(item.Metadata, includeStale, playerPosition, maxDistance))
            .OrderBy(item => Distance(playerPosition, item.Metadata.Position))
            .ThenBy(item => item.Alias, StringComparer.Ordinal)
            .Take(limit)
            .Select(item => (object)SupportAsset(item, playerPosition, view.FriendlyVehicles))
            .ToArray();

        return Serialize(new Dictionary<string, object?>
        {
            ["schema"] = SnapshotSchema,
            ["purpose"] = "assets",
            ["generatedAtUtc"] = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            ["session"] = Session(view),
            ["query"] = new { kind, availableOnly, maxDistanceMeters = Round(maxDistance), includeStale, limit },
            ["reconciliation"] = Reconciliation(view.Reconciliation),
            ["assets"] = assets
        });
    }

    public string BuildMissionCapabilities(JsonElement arguments)
    {
        WorldStateView view = RequireWorldState();
        bool enabledOnly = ReadBoolean(arguments, "enabledOnly");
        bool includeStale = ReadBoolean(arguments, "includeStale");
        object[] capabilities = view.Capabilities
            .Where(item => !enabledOnly || item.Enabled)
            .Where(item => IncludeFreshness(item.Metadata, includeStale))
            .OrderBy(item => item.Capability, StringComparer.Ordinal)
            .Select(item => (object)Capability(item))
            .ToArray();

        return Serialize(new Dictionary<string, object?>
        {
            ["schema"] = SnapshotSchema,
            ["purpose"] = "mission-capabilities",
            ["generatedAtUtc"] = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            ["session"] = Session(view),
            ["query"] = new { enabledOnly, includeStale },
            ["reconciliation"] = Reconciliation(view.Reconciliation),
            ["capabilities"] = capabilities
        });
    }

    private Dictionary<string, object?> BuildCurrentSituation(WorldStateView view)
        => new()
        {
            ["schema"] = SnapshotSchema,
            ["purpose"] = "current-situation",
            ["generatedAtUtc"] = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            ["session"] = Session(view),
            ["map"] = Map(view.Map!),
            ["player"] = Player(view.Player!),
            ["group"] = view.Group is null ? null : Group(view.Group),
            ["vehicle"] = view.Vehicle is null ? null : Vehicle(view.Vehicle),
            ["knownContacts"] = Contacts(view.KnownContacts),
            ["friendlyForceSummary"] = FriendlyForceSummary(view),
            ["missionCapabilitySummary"] = MissionCapabilitySummary(view),
            ["reconciliation"] = Reconciliation(view.Reconciliation)
        };

    private static Dictionary<string, object?> Session(WorldStateView view)
        => new()
        {
            ["id"] = view.SessionId,
            ["telemetryConnected"] = view.IsConnected,
            ["observedAtGameTime"] = Round(view.LastObservedAtGameTime),
            ["frame"] = view.LastFrame,
            ["lastResetReason"] = view.LastResetReason is null ? null : EnumText(view.LastResetReason.Value)
        };

    private static Dictionary<string, object?> Map(WorldMapState map)
    {
        Dictionary<string, object?> result = Metadata(map.Metadata);
        result["name"] = map.Name;
        result["sizeMeters"] = Round(map.SizeMeters);
        result["grid"] = map.Grid;
        result["daytime"] = Round(map.Daytime);
        return result;
    }

    private static Dictionary<string, object?> Player(WorldPlayerState player)
    {
        Dictionary<string, object?> result = Metadata(player.Metadata);
        result["side"] = player.Side;
        result["positionAsl"] = Position(player.PositionAsl);
        result["bodyHeading"] = Round(player.BodyHeading);
        result["viewHeading"] = Round(player.ViewHeading);
        result["speedKph"] = Round(player.SpeedKph);
        result["damage"] = Round(player.Damage);
        result["lifeState"] = player.LifeState;
        result["stance"] = player.Stance;
        result["weapon"] = player.Weapon;
        result["magazine"] = player.Magazine;
        result["muzzle"] = player.Muzzle;
        result["loadedRounds"] = player.LoadedRounds;
        result["matchingMagazineCount"] = player.MatchingMagazineCount;
        result["matchingMagazineRounds"] = player.MatchingMagazineRounds;
        return result;
    }

    private static Dictionary<string, object?> PlayerReference(WorldPlayerState player)
    {
        Dictionary<string, object?> result = Metadata(player.Metadata);
        result["side"] = player.Side;
        result["bodyHeading"] = Round(player.BodyHeading);
        result["viewHeading"] = Round(player.ViewHeading);
        return result;
    }

    private static Dictionary<string, object?> Group(WorldGroupState group)
    {
        Dictionary<string, object?> result = Metadata(group.Metadata, "group:self");
        result["side"] = group.Side;
        return result;
    }

    private static Dictionary<string, object?> Vehicle(WorldVehicleState vehicle)
    {
        Dictionary<string, object?> result = Metadata(vehicle.Metadata);
        result["class"] = vehicle.Class;
        result["displayName"] = vehicle.DisplayName;
        result["heading"] = Round(vehicle.Heading);
        result["speedKph"] = Round(vehicle.SpeedKph);
        result["fuel"] = Round(vehicle.Fuel);
        result["damage"] = Round(vehicle.Damage);
        result["role"] = vehicle.Role;
        return result;
    }

    private static object[] Contacts(IReadOnlyList<WorldKnownContactState> contacts)
        => contacts
            .Where(contact => contact.Metadata.FreshnessClass != WorldFreshness.Historical)
            .OrderBy(contact => contact.Alias, StringComparer.Ordinal)
            .Take(32)
            .Select(contact => (object)Contact(contact))
            .ToArray();

    private static Dictionary<string, object?> Contact(WorldKnownContactState contact)
    {
        Dictionary<string, object?> result = Metadata(contact.Metadata);
        result["class"] = contact.Class;
        result["displayName"] = contact.DisplayName;
        result["knownByPlayer"] = contact.KnownByPlayer;
        result["knownByGroup"] = contact.KnownByGroup;
        result["lastSeenAgeSeconds"] = Round(contact.LastSeenAgeSeconds);
        result["lastThreatAgeSeconds"] = Round(contact.LastThreatAgeSeconds);
        result["perceivedSide"] = contact.PerceivedSide;
        result["ignored"] = contact.Ignored;
        result["targetType"] = contact.TargetType;
        result["relationship"] = contact.Relationship;
        result["sensors"] = contact.Sensors;
        return result;
    }

    private static Dictionary<string, object?> FriendlyForceSummary(WorldStateView view)
        => new()
        {
            ["groups"] = VisibleCount(view.FriendlyGroups.Select(item => item.Metadata)),
            ["units"] = VisibleCount(view.FriendlyUnits.Select(item => item.Metadata)),
            ["vehicles"] = VisibleCount(view.FriendlyVehicles.Select(item => item.Metadata)),
            ["assets"] = VisibleCount(view.SupportAssets.Select(item => item.Metadata)),
            ["detailsAvailableVia"] = new[] { "query_friendly_forces", "query_assets" }
        };

    private static Dictionary<string, object?> MissionCapabilitySummary(WorldStateView view)
        => new()
        {
            ["enabled"] = view.Capabilities.Count(item =>
                item.Enabled && item.Metadata.FreshnessClass != WorldFreshness.Historical),
            ["detailsAvailableVia"] = "query_mission_capabilities"
        };

    private static int VisibleCount(IEnumerable<WorldEntityMetadata> metadata)
        => metadata.Count(item => item.FreshnessClass != WorldFreshness.Historical);

    private static Dictionary<string, object?> FriendlyGroup(
        WorldFriendlyGroupState group, WorldPosition playerPosition)
    {
        Dictionary<string, object?> result = Metadata(group.Metadata);
        result["entityType"] = "group";
        result["callsign"] = group.Callsign;
        result["side"] = group.Side;
        result["leaderAlias"] = group.LeaderAlias;
        result["unitAliases"] = group.UnitAliases;
        result["behaviour"] = group.Behaviour;
        result["distanceMeters"] = Round(Distance(playerPosition, group.Metadata.Position));
        return result;
    }

    private static Dictionary<string, object?> FriendlyUnit(
        WorldFriendlyUnitState unit, WorldPosition playerPosition)
    {
        Dictionary<string, object?> result = Metadata(unit.Metadata);
        result["entityType"] = "unit";
        result["groupAlias"] = unit.GroupAlias;
        result["callsign"] = unit.Callsign;
        result["side"] = unit.Side;
        result["class"] = unit.Class;
        result["role"] = unit.Role;
        result["alive"] = unit.Alive;
        result["lifeState"] = unit.LifeState;
        result["mobile"] = unit.Mobile;
        result["damage"] = Round(unit.Damage);
        result["vehicleAlias"] = unit.VehicleAlias;
        result["vehicleRole"] = unit.VehicleRole;
        result["medicalReadiness"] = unit.MedicalReadiness;
        result["distanceMeters"] = Round(Distance(playerPosition, unit.Metadata.Position));
        return result;
    }

    private static Dictionary<string, object?> FriendlyVehicle(
        WorldFriendlyVehicleState vehicle, WorldPosition playerPosition)
    {
        Dictionary<string, object?> result = Metadata(vehicle.Metadata);
        result["entityType"] = "vehicle";
        result["side"] = vehicle.Side;
        result["class"] = vehicle.Class;
        result["displayName"] = vehicle.DisplayName;
        result["alive"] = vehicle.Alive;
        result["mobile"] = vehicle.Mobile;
        result["damage"] = Round(vehicle.Damage);
        result["fuel"] = Round(vehicle.Fuel);
        result["speedKph"] = Round(vehicle.SpeedKph);
        result["crewUnitAliases"] = vehicle.CrewUnitAliases;
        result["cargoCapacity"] = vehicle.CargoCapacity;
        result["emptyCargoSeats"] = vehicle.EmptyCargoSeats;
        result["distanceMeters"] = Round(Distance(playerPosition, vehicle.Metadata.Position));
        return result;
    }

    private static Dictionary<string, object?> SupportAsset(
        WorldSupportAssetState asset,
        WorldPosition playerPosition,
        IReadOnlyList<WorldFriendlyVehicleState> vehicles)
    {
        Dictionary<string, object?> result = Metadata(asset.Metadata);
        result["kind"] = asset.Kind;
        result["callsign"] = asset.Callsign;
        result["provider"] = asset.Provider;
        result["vehicleAlias"] = asset.VehicleAlias;
        result["status"] = asset.Status;
        result["available"] = asset.Available;
        result["capacity"] = asset.Capacity;
        result["distanceMeters"] = Round(Distance(playerPosition, asset.Metadata.Position));
        WorldFriendlyVehicleState? vehicle = vehicles.FirstOrDefault(item => item.Alias == asset.VehicleAlias);
        result["vehicle"] = vehicle is null ? null : new
        {
            vehicle.Alias,
            vehicle.Class,
            vehicle.DisplayName,
            vehicle.Alive,
            vehicle.Mobile,
            damage = Round(vehicle.Damage),
            fuel = Round(vehicle.Fuel),
            vehicle.CargoCapacity,
            vehicle.EmptyCargoSeats
        };
        return result;
    }

    private static Dictionary<string, object?> Capability(WorldCapabilityState capability)
    {
        Dictionary<string, object?> result = Metadata(capability.Metadata);
        result["capability"] = capability.Capability;
        result["enabled"] = capability.Enabled;
        result["provider"] = capability.Provider;
        result["constraints"] = new
        {
            capability.Constraints.MaxConcurrent,
            capability.Constraints.AllowedRequesterSides,
            maxRangeMeters = Round(capability.Constraints.MaxRangeMeters),
            capability.Constraints.MaxPassengers,
            capability.Constraints.SupportsCasualties,
            capability.Constraints.RequiresConfirmation
        };
        return result;
    }

    private static Dictionary<string, object?> Reconciliation(WorldReconciliationState state)
        => new()
        {
            ["hasHandshake"] = state.HasHandshake,
            ["hasCompleteReconciliation"] = state.HasCompleteReconciliation,
            ["lastSequence"] = state.LastSequence,
            ["sequenceGap"] = state.SequenceGap,
            ["pendingPageCount"] = state.PendingPageCount,
            ["degraded"] = state.IsDegraded,
            ["capabilityRegistryVersion"] = state.CapabilityRegistryVersion
        };

    private WorldStateView RequireWorldState()
    {
        WorldStateView view = _store.GetCurrentView();
        if (!view.HasTelemetry || view.Map is null || view.Player?.Metadata.Position is null)
            throw new InvalidOperationException("No world-state telemetry is available yet.");
        return view;
    }

    private static bool Include(
        WorldEntityMetadata metadata,
        bool includeStale,
        WorldPosition playerPosition,
        double maxDistance)
        => IncludeFreshness(metadata, includeStale) && metadata.Position is not null &&
           Distance(playerPosition, metadata.Position) <= maxDistance;

    private static bool IncludeFreshness(WorldEntityMetadata metadata, bool includeStale)
        => metadata.FreshnessClass != WorldFreshness.Historical &&
           (includeStale || metadata.FreshnessClass != WorldFreshness.Stale);

    private static double Distance(WorldPosition from, WorldPosition? to)
        => to is null
            ? double.PositiveInfinity
            : Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));

    private static string ReadEnum(JsonElement root, string name, params string[] allowed)
    {
        string value = ReadString(root, name).ToLowerInvariant();
        return allowed.Contains(value, StringComparer.Ordinal)
            ? value
            : throw new InvalidOperationException($"Unsupported {name}: {value}.");
    }

    private static bool ReadBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
            throw new InvalidOperationException($"{name} is required.");
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidOperationException($"{name} must be a boolean.")
        };
    }

    private static double ReadNumber(
        JsonElement root, string name, double minimum, double maximum, bool integer = false)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double result) || !double.IsFinite(result) ||
            result < minimum || result > maximum || (integer && result != Math.Truncate(result)))
        {
            throw new InvalidOperationException($"{name} must be between {minimum} and {maximum}.");
        }
        return result;
    }

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static Dictionary<string, object?> Metadata(WorldEntityMetadata metadata, string? entityId = null)
        => new()
        {
            ["entityId"] = entityId ?? metadata.EntityId,
            ["identityQuality"] = EnumText(metadata.IdentityQuality),
            ["source"] = EnumText(metadata.Source),
            ["evidenceSources"] = metadata.EvidenceSources.Select(EnumText).ToArray(),
            ["observedAtGameTime"] = Round(metadata.ObservedAtGameTime),
            ["receivedAtUtc"] = metadata.ReceivedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["ageSeconds"] = Round(metadata.AgeSeconds),
            ["freshnessClass"] = EnumText(metadata.FreshnessClass),
            ["confidence"] = Round(metadata.Confidence),
            ["position"] = Position(metadata.Position),
            ["positionErrorMeters"] = Round(metadata.PositionErrorMeters)
        };

    private static double[]? Position(WorldPosition? position)
        => position is null ? null : new[] { Round(position.X), Round(position.Y), Round(position.Z) };

    private static double Round(double value) => Math.Round(value, 3);
    private static double? Round(double? value) => value is null ? null : Round(value.Value);

    private static string EnumText<T>(T value) where T : struct, Enum
        => string.Concat(value.ToString().Select((character, index) =>
            char.IsUpper(character) && index > 0 ? "-" + char.ToLowerInvariant(character) :
            char.ToLowerInvariant(character).ToString()));

    private static string Serialize(object value)
        => JsonSerializer.Serialize(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}
