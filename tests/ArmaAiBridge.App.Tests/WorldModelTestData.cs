using System.Text.Json;

namespace ArmaAiBridge.App.Tests;

internal static class WorldModelTestData
{
    public static string Telemetry(
        double timestamp = 100,
        long frame = 1000,
        string mapName = "Altis",
        double mapSize = 30720,
        string group = "Alpha 1-1",
        object[]? contacts = null,
        object[]? sensorContacts = null,
        object? vehicle = null)
        => JsonSerializer.Serialize(new
        {
            schema = "arma-ai-bridge/arma3/telemetry-v1",
            timestamp,
            frame,
            map = new { name = mapName, sizeMeters = mapSize, grid = "034056", daytime = 14.5 },
            player = new
            {
                uid = "SECRET-PLAYER-UID",
                name = "SECRET PLAYER NAME",
                side = "WEST",
                group,
                positionATL = new[] { 3400.1, 5600.2, 12.3 },
                positionASL = new[] { 3400.1, 5600.2, 42.3 },
                bodyHeading = 38.0,
                viewHeading = 42.5,
                speedKph = 3.2,
                damage = 0.1,
                lifeState = "HEALTHY",
                stance = "STAND",
                weapon = "arifle_MX_F",
                magazine = "30Rnd_65x39_caseless_mag",
                muzzle = "arifle_MX_F",
                loadedRounds = 23,
                matchingMagazineCount = 4,
                matchingMagazineRounds = 113
            },
            vehicle,
            contacts = contacts ?? Array.Empty<object>(),
            sensorContacts = sensorContacts ?? Array.Empty<object>()
        });

    public static object Contact(
        string id,
        double lastSeenAge = 4,
        bool knownByPlayer = true,
        bool knownByGroup = true)
        => new
        {
            id,
            @class = "O_Soldier_F",
            displayName = "Rifleman",
            knownByPlayer,
            knownByGroup,
            lastSeenAgeSeconds = lastSeenAge,
            lastThreatAgeSeconds = lastSeenAge + 1,
            perceivedSide = "EAST",
            positionErrorMeters = 18.0,
            estimatedPosition = new[] { 3600.0, 5800.0, 0.0 },
            ignored = false
        };

    public static object UnknownAgeContact(string id)
        => new
        {
            id,
            @class = "O_Soldier_F",
            displayName = "Unknown contact",
            knownByPlayer = false,
            knownByGroup = true,
            lastSeenAgeSeconds = -1,
            lastThreatAgeSeconds = -1,
            perceivedSide = "EAST",
            positionErrorMeters = 200.0,
            estimatedPosition = new[] { 3600.0, 5800.0, 0.0 },
            ignored = false
        };

    public static object Sensor(string id)
        => new
        {
            id,
            @class = "O_Soldier_F",
            targetType = "Man",
            relationship = "ENEMY",
            sensors = new[] { "VisualSensorComponent", "IRSensorComponent" }
        };

    public static object Vehicle(object? role = null)
        => new
        {
            @class = "B_MRAP_01_F",
            displayName = "Hunter",
            positionATL = new[] { 3401.0, 5601.0, 12.0 },
            heading = 40.0,
            speedKph = 24.0,
            fuel = 0.8,
            damage = 0.05,
            role = role ?? "driver"
        };
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration) => _utcNow += duration;
}
