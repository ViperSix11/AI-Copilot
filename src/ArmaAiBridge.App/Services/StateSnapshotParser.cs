using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public static class StateSnapshotParser
{
    public const string Schema = "arma-ai-bridge/arma3/state-snapshot-v2";
    private static readonly string[] RequiredSections =
    {
        "player", "environment", "timeAstronomy", "loadout",
        "friendlyForces", "knownContacts", "tasks", "markers"
    };

    public static StateSnapshotMessage Parse(JsonElement root, DateTimeOffset receivedAtUtc)
    {
        if (root.ValueKind != JsonValueKind.Object || Text(root, "schema") != Schema)
            throw Invalid("state_schema_invalid");
        string messageId = RequiredString(root, "messageId", 128);
        string missionId = RequiredString(root, "missionId", 128);
        string sessionId = RequiredString(root, "sessionId", 128);
        double timestamp = RequiredNumber(root, "timestamp", 0, 1e12);
        long sequence = RequiredInteger(root, "sequence", 1, long.MaxValue);
        bool full = RequiredBoolean(root, "fullReconciliation");
        JsonElement sections = RequiredObject(root, "sections");
        Dictionary<string, StateSnapshotSection> parsed = new(StringComparer.Ordinal);
        foreach (string name in RequiredSections)
        {
            if (!sections.TryGetProperty(name, out JsonElement section) || section.ValueKind != JsonValueKind.Object)
                throw Invalid("state_required_section_missing");
            string readinessText = RequiredString(section, "readiness", 16);
            StateSectionReadiness readiness = readinessText switch
            {
                "ready" => StateSectionReadiness.Ready,
                "stale" => StateSectionReadiness.Stale,
                "unavailable" => StateSectionReadiness.Unavailable,
                "failed" => StateSectionReadiness.Failed,
                _ => throw Invalid("state_readiness_invalid")
            };
            double sampledAt = RequiredNumber(section, "sampledAt", 0, timestamp + 1);
            Validate(name, section, readiness);
            parsed.Add(name, new StateSnapshotSection(name, readiness, sampledAt, section.Clone()));
        }
        foreach (JsonProperty property in sections.EnumerateObject())
            if (!parsed.ContainsKey(property.Name)) throw Invalid("state_unknown_section");
        return new StateSnapshotMessage(messageId, missionId, sessionId, timestamp, sequence, full, receivedAtUtc, parsed);
    }

    private static void Validate(string name, JsonElement section, StateSectionReadiness readiness)
    {
        if (readiness is StateSectionReadiness.Failed or StateSectionReadiness.Unavailable) return;
        switch (name)
        {
            case "player":
                RequiredString(section, "sourceId", 128);
                RequiredString(section, "side", 16);
                RequiredString(section, "groupSourceId", 128, allowEmpty: true);
                RequiredString(section, "groupCallsign", 160, allowEmpty: true);
                Vector(section, "positionATL"); Vector(section, "positionASL");
                RequiredString(section, "grid", 32, allowEmpty: true);
                foreach (string forbidden in new[] { "cameraPosition", "eyeDirection", "viewFocus", "cursorTarget", "crosshairObject" })
                    if (section.TryGetProperty(forbidden, out _)) throw Invalid("state_player_forbidden_field");
                break;
            case "environment":
                foreach (string field in new[] { "overcast", "forecastOvercast", "rain", "fog", "forecastFog", "waves", "lightning", "humidity" })
                    RequiredNumber(section, field, 0, 1);
                ArrayLength(section, "fogParameters", 3, 3);
                RequiredNumber(section, "nextWeatherChange", -1, 1e12);
                break;
            case "timeAstronomy":
                ArrayLength(section, "missionDate", 5, 5);
                foreach (string field in new[] { "daytime", "elapsedMissionTime", "timeMultiplier", "moonPhase", "sunOrMoon" })
                    RequiredNumber(section, field, field == "moonPhase" ? -1 : 0, field == "elapsedMissionTime" ? 1e12 : 120);
                HashSet<string> timeFields = new(StringComparer.Ordinal)
                {
                    "sampledAt", "readiness", "missionDate", "daytime", "elapsedMissionTime",
                    "timeMultiplier", "moonPhase", "sunOrMoon"
                };
                if (section.EnumerateObject().Any(property => !timeFields.Contains(property.Name)))
                    throw Invalid("state_time_unknown_field");
                break;
            case "loadout":
                foreach (string field in new[] { "primaryWeapon", "launcher", "handgun", "selectedWeapon", "selectedWeaponDisplayName", "muzzle", "fireMode", "currentMagazine", "binocular", "uniformClass", "vestClass", "backpackClass", "loadoutHash" })
                    RequiredString(section, field, 256, allowEmpty: true);
                RequiredInteger(section, "loadedRounds", 0, 100000);
                ArrayLength(section, "opticsAndAttachments", 0, 32); ArrayLength(section, "magazines", 0, 64);
                ArrayLength(section, "magazineTotals", 0, 64); ArrayLength(section, "assignedItems", 0, 64);
                foreach (string field in new[] { "grenadeCount", "throwableCount", "mineCount", "explosiveCount" })
                    RequiredInteger(section, field, 0, 10000);
                break;
            case "friendlyForces":
                ArrayLength(section, "groups", 0, 128); ArrayLength(section, "units", 0, 512);
                foreach (JsonElement group in section.GetProperty("groups").EnumerateArray())
                {
                    if (group.ValueKind != JsonValueKind.Object) throw Invalid("state_group_invalid");
                    RequiredString(group, "sourceId", 128); RequiredString(group, "callsign", 160, allowEmpty: true);
                    RequiredString(group, "leaderSourceId", 128, allowEmpty: true); ArrayLength(group, "memberSourceIds", 0, 512);
                    Vector(group, "leaderPosition"); RequiredNumber(group, "leaderSpeedKph", 0, 2000);
                }
                foreach (JsonElement unit in section.GetProperty("units").EnumerateArray())
                {
                    if (unit.ValueKind != JsonValueKind.Object) throw Invalid("state_unit_invalid");
                    RequiredString(unit, "sourceId", 128); RequiredString(unit, "groupSourceId", 128);
                    Vector(unit, "position"); RequiredBoolean(unit, "alive"); RequiredBoolean(unit, "mobile");
                    RequiredNumber(unit, "damage", 0, 1);
                }
                break;
            case "knownContacts":
                ArrayLength(section, "contacts", 0, 256);
                foreach (JsonElement contact in section.GetProperty("contacts").EnumerateArray())
                {
                    if (contact.ValueKind != JsonValueKind.Object || contact.TryGetProperty("actualPosition", out _))
                        throw Invalid("state_contact_invalid");
                    HashSet<string> contactFields = new(StringComparer.Ordinal)
                    {
                        "sourceId", "class", "displayName", "contactType", "perceivedSide", "relationship",
                        "estimatedPosition", "positionErrorMeters", "lastSeenAgeSeconds", "lastThreatAgeSeconds",
                        "observerGroupSourceIds"
                    };
                    if (contact.EnumerateObject().Any(property => !contactFields.Contains(property.Name)))
                        throw Invalid("state_contact_unknown_field");
                    RequiredString(contact, "sourceId", 128);
                    string contactClass = RequiredString(contact, "class", 256);
                    if (!ContactEligibilityPolicy.IsSafeClass(contactClass)) throw Invalid("state_contact_ineligible");
                    RequiredString(contact, "displayName", 160, allowEmpty: true);
                    if (!ContactEligibilityPolicy.ContactTypes.Contains(RequiredString(contact, "contactType", 32)))
                        throw Invalid("state_contact_type_invalid");
                    if (!ContactEligibilityPolicy.PerceivedSides.Contains(RequiredString(contact, "perceivedSide", 16)))
                        throw Invalid("state_contact_side_invalid");
                    if (!ContactEligibilityPolicy.Relationships.Contains(RequiredString(contact, "relationship", 16)))
                        throw Invalid("state_contact_relationship_invalid");
                    Vector(contact, "estimatedPosition");
                    RequiredNumber(contact, "positionErrorMeters", 0, 100000);
                    RequiredNumber(contact, "lastSeenAgeSeconds", -1, 600);
                    RequiredNumber(contact, "lastThreatAgeSeconds", -1, 600);
                    ArrayLength(contact, "observerGroupSourceIds", 1, 128);
                }
                break;
            case "tasks":
                ArrayLength(section, "tasks", 0, 128);
                foreach (JsonElement task in section.GetProperty("tasks").EnumerateArray())
                {
                    RequiredString(task, "sourceId", 128); RequiredString(task, "title", 512, allowEmpty: true);
                    RequiredString(task, "description", 1024, allowEmpty: true);
                }
                break;
            case "markers":
                ArrayLength(section, "markers", 0, 256);
                foreach (JsonElement marker in section.GetProperty("markers").EnumerateArray())
                {
                    RequiredString(marker, "sourceId", 128); RequiredString(marker, "text", 512, allowEmpty: true);
                    Vector(marker, "position"); RequiredInteger(marker, "channel", -1, 9999);
                    ArrayLength(marker, "polyline", 0, 128);
                }
                break;
        }
    }

    private static JsonElement RequiredObject(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object ? value : throw Invalid("state_object_required");
    private static string RequiredString(JsonElement root, string name, int maxLength, bool allowEmpty = false)
    {
        string value = Text(root, name);
        if ((!allowEmpty && value.Length == 0) || value.Length > maxLength || value.Any(char.IsControl)) throw Invalid("state_string_invalid");
        return value;
    }
    private static string Text(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    private static double RequiredNumber(JsonElement root, string name, double min, double max)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) ||
            !double.IsFinite(number) || number < min || number > max) throw Invalid("state_number_invalid");
        return number;
    }
    private static long RequiredInteger(JsonElement root, string name, long min, long max)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long number) || number < min || number > max)
            throw Invalid("state_integer_invalid");
        return number;
    }
    private static bool RequiredBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid("state_boolean_invalid");
        return value.GetBoolean();
    }
    private static void ArrayLength(JsonElement root, string name, int min, int max)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < min || value.GetArrayLength() > max)
            throw Invalid("state_array_invalid");
    }
    private static void Vector(JsonElement root, string name) { ArrayLength(root, name, 3, 3); foreach (JsonElement value in root.GetProperty(name).EnumerateArray()) if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double n) || !double.IsFinite(n) || Math.Abs(n) > 1e7) throw Invalid("state_vector_invalid"); }
    private static void Vector2(JsonElement root, string name) { ArrayLength(root, name, 3, 3); foreach (JsonElement value in root.GetProperty(name).EnumerateArray()) if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double n) || !double.IsFinite(n) || Math.Abs(n) > 1e6) throw Invalid("state_vector_invalid"); }
    private static InvalidDataException Invalid(string code) => new(code);
}
