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
            ["knownContacts"] = Contacts(view.KnownContacts)
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
