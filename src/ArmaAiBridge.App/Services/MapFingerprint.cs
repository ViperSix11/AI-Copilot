using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public static class MapFingerprint
{
    public const int IndexVersion = 1;
    public const int MaximumAddons = 4096;
    public const int MaximumCanonicalBytes = 768 * 1024;

    public static string Compute(MapManifestData manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.IndexVersion != IndexVersion)
        {
            throw new InvalidDataException("unsupported_index_version");
        }
        if (manifest.Addons.Count > MaximumAddons)
        {
            throw new InvalidDataException("addon_limit_exceeded");
        }

        byte[] canonical = BuildCanonicalJson(manifest);
        if (canonical.Length > MaximumCanonicalBytes)
        {
            throw new InvalidDataException("manifest_size_exceeded");
        }
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    public static byte[] BuildCanonicalJson(MapManifestData manifest)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("indexVersion", manifest.IndexVersion);
            writer.WriteString("worldName", Normalize(manifest.WorldName));
            writer.WriteNumber("worldSizeMeters", manifest.WorldSizeMeters);

            writer.WritePropertyName("terrainInfo");
            writer.WriteStartArray();
            foreach (double value in manifest.TerrainInfo) writer.WriteNumberValue(value);
            writer.WriteEndArray();

            writer.WritePropertyName("worldConfig");
            writer.WriteStartObject();
            writer.WriteString("class", Normalize(manifest.WorldConfig.ClassName));
            writer.WriteString("description", Normalize(manifest.WorldConfig.Description));
            WriteNullable(writer, "mapSize", manifest.WorldConfig.MapSize);
            WriteNullable(writer, "mapZone", manifest.WorldConfig.MapZone);
            WriteNullable(writer, "latitude", manifest.WorldConfig.Latitude);
            WriteNullable(writer, "longitude", manifest.WorldConfig.Longitude);
            writer.WritePropertyName("sourceAddons");
            writer.WriteStartArray();
            foreach (string addon in manifest.WorldConfig.SourceAddons
                         .Select(Normalize).Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(addon);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WritePropertyName("gridReferences");
            writer.WriteStartArray();
            foreach (MapGridReference grid in manifest.GridReferences.OrderBy(value => value.X).ThenBy(value => value.Y))
            {
                writer.WriteStartObject();
                writer.WriteNumber("x", grid.X);
                writer.WriteNumber("y", grid.Y);
                writer.WriteString("label", Normalize(grid.Label));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WritePropertyName("product");
            writer.WriteStartObject();
            writer.WriteString("shortName", Normalize(manifest.Product.ShortName));
            writer.WriteNumber("version", manifest.Product.Version);
            writer.WriteNumber("build", manifest.Product.Build);
            writer.WriteString("buildType", Normalize(manifest.Product.BuildType));
            writer.WriteString("platform", Normalize(manifest.Product.Platform));
            writer.WriteString("architecture", Normalize(manifest.Product.Architecture));
            writer.WriteString("branch", Normalize(manifest.Product.Branch));
            writer.WriteEndObject();

            writer.WritePropertyName("addons");
            writer.WriteStartArray();
            foreach (MapAddonFingerprintInput addon in manifest.Addons
                         .Select(value => new MapAddonFingerprintInput(
                             Normalize(value.Prefix), Normalize(value.Version), value.Patched, Normalize(value.Hash)))
                         .OrderBy(value => value.Prefix, StringComparer.Ordinal)
                         .ThenBy(value => value.Version, StringComparer.Ordinal)
                         .ThenBy(value => value.Patched)
                         .ThenBy(value => value.Hash, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("prefix", addon.Prefix);
                writer.WriteString("version", addon.Version);
                writer.WriteBoolean("patched", addon.Patched);
                writer.WriteString("hash", addon.Hash);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static void WriteNullable(Utf8JsonWriter writer, string name, double? value)
    {
        if (value.HasValue) writer.WriteNumber(name, value.Value);
        else writer.WriteNull(name);
    }
}
