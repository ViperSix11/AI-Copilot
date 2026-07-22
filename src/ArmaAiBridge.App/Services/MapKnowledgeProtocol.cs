using System.Text.Json;
using System.Text.RegularExpressions;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public static partial class MapKnowledgeProtocol
{
    public const string HandshakeSchema = "arma-ai-bridge/arma3/session-handshake-v1";
    public const string ManifestSchema = "arma-ai-bridge/arma3/map-manifest-v1";
    public const string TileSchema = "arma-ai-bridge/arma3/map-tile-v1";
    public const string ProgressSchema = "arma-ai-bridge/arma3/map-index-progress-v1";
    public const string CommandSchema = "arma-ai-bridge/map-index-command-v1";

    public static MapManifestData ParseManifest(JsonElement root)
    {
        ValidateEnvelope(root, ManifestSchema, "indexVersion", "world", "product", "addons", "export");
        string missionId = ReadString(root, "missionId", 128);
        string sessionId = ReadIdentifier(root, "sessionId");
        int indexVersion = ReadInt(root, "indexVersion", 1, 1);
        JsonElement world = ReadObject(root, "world");
        RequireProperties(world, "name", "sizeMeters", "terrainInfo", "config", "gridReferences");
        string worldName = ReadString(world, "name", 128);
        double worldSize = ReadFinite(world, "sizeMeters", 1, 131072);

        JsonElement terrain = ReadArray(world, "terrainInfo");
        if (terrain.GetArrayLength() != 5) throw Error("invalid_terrain_info");
        double[] terrainInfo = terrain.EnumerateArray()
            .Select(value => ReadFinite(value, -1_000_000, 1_000_000, "invalid_terrain_info"))
            .ToArray();

        JsonElement config = ReadObject(world, "config");
        RequireProperties(config, "class", "description", "mapSize", "mapZone", "latitude", "longitude", "sourceAddons");
        JsonElement sources = ReadArray(config, "sourceAddons");
        if (sources.GetArrayLength() > 256) throw Error("config_addon_limit_exceeded");
        string[] sourceAddons = sources.EnumerateArray()
            .Select(value => ReadString(value, 192, "invalid_config_addon"))
            .ToArray();
        MapWorldConfigFingerprintInput worldConfig = new(
            ReadString(config, "class", 128),
            ReadString(config, "description", 256, allowEmpty: true),
            ReadNullableFinite(config, "mapSize"),
            ReadNullableFinite(config, "mapZone"),
            ReadNullableFinite(config, "latitude"),
            ReadNullableFinite(config, "longitude"),
            sourceAddons);

        JsonElement grids = ReadArray(world, "gridReferences");
        if (grids.GetArrayLength() != 4) throw Error("invalid_grid_references");
        List<MapGridReference> gridReferences = new(4);
        foreach (JsonElement grid in grids.EnumerateArray())
        {
            RequireProperties(grid, "position", "label");
            JsonElement position = ReadVector(grid, "position", 2);
            gridReferences.Add(new MapGridReference(
                position[0].GetDouble(), position[1].GetDouble(),
                ReadString(grid, "label", 64, allowEmpty: true)));
        }

        JsonElement product = ReadObject(root, "product");
        RequireProperties(product, "shortName", "version", "build", "buildType", "platform", "architecture", "branch");
        MapProductFingerprintInput productInput = new(
            ReadString(product, "shortName", 64),
            ReadFinite(product, "version", 0, 1000),
            ReadLong(product, "build", 0, int.MaxValue),
            ReadString(product, "buildType", 32, allowEmpty: true),
            ReadString(product, "platform", 32, allowEmpty: true),
            ReadString(product, "architecture", 32, allowEmpty: true),
            ReadString(product, "branch", 64, allowEmpty: true));

        JsonElement addons = ReadArray(root, "addons");
        if (addons.GetArrayLength() > MapFingerprint.MaximumAddons) throw Error("addon_limit_exceeded");
        List<MapAddonFingerprintInput> addonInputs = new(addons.GetArrayLength());
        foreach (JsonElement addon in addons.EnumerateArray())
        {
            RequireProperties(addon, "prefix", "version", "patched", "hash");
            addonInputs.Add(new MapAddonFingerprintInput(
                ReadString(addon, "prefix", 384),
                ReadString(addon, "version", 128, allowEmpty: true),
                ReadBoolean(addon, "patched"),
                ReadString(addon, "hash", 128, allowEmpty: true)));
        }

        JsonElement export = ReadObject(root, "export");
        RequireProperties(export, "tileSizeMeters", "terrainSampleSpacingMeters", "totalTiles", "maxRecordsPerPage");
        MapExportSettings exportSettings = new(
            ReadInt(export, "tileSizeMeters", 256, 1024),
            ReadInt(export, "terrainSampleSpacingMeters", 32, 256),
            ReadInt(export, "totalTiles", 1, 16384),
            ReadInt(export, "maxRecordsPerPage", 16, 96));
        int columns = (int)Math.Ceiling(worldSize / exportSettings.TileSizeMeters);
        if (columns * columns != exportSettings.TotalTiles) throw Error("invalid_total_tiles");

        return new MapManifestData(
            missionId, sessionId, indexVersion, worldName, worldSize, terrainInfo,
            worldConfig, gridReferences, productInput, addonInputs, exportSettings);
    }

    public static MapTilePageData ParseTilePage(JsonElement root)
    {
        ValidateEnvelope(root, TileSchema, "exportId", "fingerprint", "indexVersion", "tile", "pageIndex", "pageCount", "records");
        string sessionId = ReadIdentifier(root, "sessionId");
        string exportId = ReadIdentifier(root, "exportId");
        string fingerprint = ReadFingerprint(root, "fingerprint");
        int indexVersion = ReadInt(root, "indexVersion", 1, 1);
        JsonElement tile = ReadObject(root, "tile");
        RequireProperties(tile, "ordinal", "column", "row", "minX", "minY", "maxX", "maxY");
        MapTileBounds bounds = new(
            ReadInt(tile, "ordinal", 0, 16383),
            ReadInt(tile, "column", 0, 1023),
            ReadInt(tile, "row", 0, 1023),
            ReadFinite(tile, "minX", 0, 131072),
            ReadFinite(tile, "minY", 0, 131072),
            ReadFinite(tile, "maxX", 0, 131072),
            ReadFinite(tile, "maxY", 0, 131072));
        if (bounds.MaxX <= bounds.MinX || bounds.MaxY <= bounds.MinY) throw Error("invalid_tile_bounds");

        int pageIndex = ReadInt(root, "pageIndex", 0, 255);
        int pageCount = ReadInt(root, "pageCount", 1, 256);
        if (pageIndex >= pageCount) throw Error("invalid_page_index");
        JsonElement records = ReadArray(root, "records");
        if (records.GetArrayLength() > 96) throw Error("page_record_limit_exceeded");
        foreach (JsonElement record in records.EnumerateArray()) ValidateRecord(record);
        return new MapTilePageData(
            sessionId, exportId, fingerprint, indexVersion, bounds,
            pageIndex, pageCount, records.GetRawText());
    }

    public static string CreateStartCommand(
        string requestId, string sessionId, string exportId, string fingerprint,
        MapExportSettings settings, int startTileOrdinal)
        => JsonSerializer.Serialize(new
        {
            schema = CommandSchema,
            requestId,
            command = "start",
            parameters = new
            {
                sessionId,
                exportId,
                fingerprint,
                indexVersion = MapFingerprint.IndexVersion,
                startTileOrdinal,
                tileSizeMeters = settings.TileSizeMeters,
                terrainSampleSpacingMeters = settings.TerrainSampleSpacingMeters,
                maxRecordsPerPage = settings.MaxRecordsPerPage
            }
        });

    public static string CreateCancelCommand(string requestId, string sessionId, string exportId, string fingerprint)
        => JsonSerializer.Serialize(new
        {
            schema = CommandSchema,
            requestId,
            command = "cancel",
            parameters = new { sessionId, exportId, fingerprint }
        });

    public static bool HandshakeSupportsMapExport(JsonElement root, out string sessionId)
    {
        sessionId = string.Empty;
        if (ReadOptionalString(root, "schema") != HandshakeSchema) return false;
        sessionId = ReadIdentifier(root, "sessionId");
        JsonElement features = ReadArray(root, "features");
        return features.EnumerateArray().Any(feature =>
            ReadOptionalString(feature, "name") == "static-map-export" &&
            feature.TryGetProperty("version", out JsonElement version) &&
            version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int value) && value == 1);
    }

    private static void ValidateRecord(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object) throw Error("invalid_tile_record");
        string kind = ReadString(record, "kind", 32);
        switch (kind)
        {
            case "location":
                RequireProperties(record, "kind", "name", "locationType", "positionASL");
                ReadString(record, "name", 256);
                ReadString(record, "locationType", 64);
                ReadVector(record, "positionASL", 3);
                break;
            case "terrain":
                RequireProperties(record, "kind", "positionASL", "slopeDegrees", "water");
                ReadVector(record, "positionASL", 3);
                ReadFinite(record, "slopeDegrees", 0, 90);
                ReadBoolean(record, "water");
                break;
            case "building":
                RequireProperties(record, "kind", "class", "model", "terrainType", "positionASL");
                ReadString(record, "class", 256, allowEmpty: true);
                ReadString(record, "model", 384, allowEmpty: true);
                ReadString(record, "terrainType", 64);
                ReadVector(record, "positionASL", 3);
                break;
            case "road":
                RequireProperties(record, "kind", "roadType", "widthMeters", "pedestrian", "beginASL", "endASL", "bridge");
                ReadString(record, "roadType", 64);
                ReadFinite(record, "widthMeters", 0, 100);
                ReadBoolean(record, "pedestrian");
                ReadVector(record, "beginASL", 3);
                ReadVector(record, "endASL", 3);
                ReadBoolean(record, "bridge");
                break;
            case "vegetation":
                RequireProperties(record, "kind", "treeCount", "bushCount", "forestCount", "densityPerHectare");
                ReadInt(record, "treeCount", 0, 1_000_000);
                ReadInt(record, "bushCount", 0, 1_000_000);
                ReadInt(record, "forestCount", 0, 1_000_000);
                ReadFinite(record, "densityPerHectare", 0, 1_000_000);
                break;
            case "water":
                RequireProperties(record, "kind", "classification", "waterSamples", "landSamples");
                string classification = ReadString(record, "classification", 16);
                if (classification is not ("land" or "water" or "coast")) throw Error("invalid_water_classification");
                ReadInt(record, "waterSamples", 0, 10000);
                ReadInt(record, "landSamples", 0, 10000);
                break;
            default:
                throw Error("unknown_tile_record_kind");
        }
    }

    public static void RequireSchema(JsonElement root, string expected)
    {
        if (root.ValueKind != JsonValueKind.Object || ReadOptionalString(root, "schema") != expected)
            throw Error("schema_mismatch");
    }

    public static void ValidateEnvelope(JsonElement root, string expectedSchema, params string[] payloadProperties)
    {
        RequireSchema(root, expectedSchema);
        RequireProperties(root, new[] { "schema", "messageId", "missionId", "sessionId", "timestamp", "sequence" }
            .Concat(payloadProperties).ToArray());
        ReadIdentifier(root, "messageId");
        ReadString(root, "missionId", 128);
        ReadIdentifier(root, "sessionId");
        ReadFinite(root, "timestamp", 0, 1_000_000_000_000);
        ReadLong(root, "sequence", 1, long.MaxValue);
    }

    public static void RequireProperties(JsonElement value, params string[] allowedProperties)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Error("invalid_object");
        HashSet<string> allowed = new(allowedProperties, StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!allowed.Contains(property.Name)) throw Error("unexpected_property");
    }

    public static string ReadFingerprint(JsonElement parent, string name)
    {
        string value = ReadString(parent, name, 64);
        if (!FingerprintRegex().IsMatch(value)) throw Error("invalid_fingerprint");
        return value;
    }

    public static string ReadIdentifier(JsonElement parent, string name)
    {
        string value = ReadString(parent, name, 128);
        if (!IdentifierRegex().IsMatch(value)) throw Error("invalid_identifier");
        return value;
    }

    public static string ReadString(JsonElement parent, string name, int maximum, bool allowEmpty = false)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)) throw Error("missing_" + name);
        return ReadString(value, maximum, "invalid_" + name, allowEmpty);
    }

    private static string ReadString(JsonElement value, int maximum, string code, bool allowEmpty = false)
    {
        if (value.ValueKind != JsonValueKind.String) throw Error(code);
        string text = value.GetString() ?? string.Empty;
        if ((!allowEmpty && text.Length == 0) || text.Length > maximum || text.Any(char.IsControl)) throw Error(code);
        return text;
    }

    public static string ReadOptionalString(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    public static JsonElement ReadObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
            throw Error("invalid_" + name);
        return value;
    }

    public static JsonElement ReadArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            throw Error("invalid_" + name);
        return value;
    }

    private static JsonElement ReadVector(JsonElement parent, string name, int length)
    {
        JsonElement value = ReadArray(parent, name);
        if (value.GetArrayLength() != length) throw Error("invalid_" + name);
        foreach (JsonElement coordinate in value.EnumerateArray())
            ReadFinite(coordinate, -50_000, 500_000, "invalid_" + name);
        return value;
    }

    public static int ReadInt(JsonElement parent, string name, int minimum, int maximum)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int number) || number < minimum || number > maximum)
            throw Error("invalid_" + name);
        return number;
    }

    public static long ReadLong(JsonElement parent, string name, long minimum, long maximum)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long number) || number < minimum || number > maximum)
            throw Error("invalid_" + name);
        return number;
    }

    public static double ReadFinite(JsonElement parent, string name, double minimum, double maximum)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)) throw Error("missing_" + name);
        return ReadFinite(value, minimum, maximum, "invalid_" + name);
    }

    private static double ReadFinite(JsonElement value, double minimum, double maximum, string code)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) ||
            !double.IsFinite(number) || number < minimum || number > maximum)
            throw Error(code);
        return number;
    }

    private static double? ReadNullableFinite(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)) throw Error("missing_" + name);
        return value.ValueKind == JsonValueKind.Null
            ? null
            : ReadFinite(value, -1_000_000, 1_000_000, "invalid_" + name);
    }

    public static bool ReadBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Error("invalid_" + name);
        return value.GetBoolean();
    }

    private static InvalidDataException Error(string code) => new(code);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintRegex();
}
