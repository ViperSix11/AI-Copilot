using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class MissionMemoryToolService
{
    private readonly IMissionMemoryRepository _repository;

    public MissionMemoryToolService(IMissionMemoryRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public string Execute(string name, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Tool arguments must be an object.");
        return name switch
        {
            "remember_information" => Remember(arguments),
            "search_memory" => Search(arguments),
            "update_memory" => Update(arguments),
            "forget_memory" => Forget(arguments),
            _ => throw new InvalidOperationException("Unsupported local tool.")
        };
    }

    private string Remember(JsonElement root)
    {
        RequireOnly(root, "category", "subject", "content", "position", "grid", "tags");
        string category = RequiredString(root, "category", 40);
        string subject = RequiredString(root, "subject", 160);
        List<string> tags = Tags(root).ToList(); tags.Add("category:" + category.ToLowerInvariant()); tags.Add("subject:" + subject.ToLowerInvariant());
        string grid = NullableString(root, "grid", 20); if (grid.Length > 0) tags.Add("grid:" + grid);
        long id = _repository.Remember(RequiredString(root, "content", 2000), "user-reported", tags, Position(root));
        return JsonSerializer.Serialize(new { ok = true, id, provenance = "user-reported" });
    }

    private string Search(JsonElement root)
    {
        RequireOnly(root, "query", "category", "subject", "includeCurrentMissionOnly", "maximumResults");
        if (!RequiredBoolean(root, "includeCurrentMissionOnly")) throw new InvalidOperationException("Only current-mission retrieval is permitted.");
        string query = RequiredString(root, "query", 500);
        string category = NullableString(root, "category", 40), subject = NullableString(root, "subject", 160);
        if (category.Length > 0) query += " " + category; if (subject.Length > 0) query += " " + subject;
        int limit = OptionalInteger(root, "maximumResults", 12, 1, 12);
        var records = _repository.SearchMemory(query, limit, 6000).Select(x => new
        {
            id = x.Id, text = x.Text, provenance = x.Provenance, updatedAtUtc = x.UpdatedAtUtc, tags = x.Tags
        });
        return JsonSerializer.Serialize(new { ok = true, records });
    }

    private string Update(JsonElement root)
    {
        RequireOnly(root, "memoryEntryId", "replacementContent", "replacementTags", "replacementPosition");
        long id = RequiredInteger(root, "memoryEntryId", 1, long.MaxValue);
        bool updated = _repository.UpdateMemory(id, RequiredString(root, "replacementContent", 2000), Tags(root, "replacementTags"), Position(root, "replacementPosition"));
        return JsonSerializer.Serialize(new { ok = updated, id });
    }

    private string Forget(JsonElement root)
    {
        RequireOnly(root, "memoryEntryId");
        long id = RequiredInteger(root, "memoryEntryId", 1, long.MaxValue);
        bool forgotten = _repository.ForgetMemory(id);
        return JsonSerializer.Serialize(new { ok = forgotten, id });
    }

    private static void RequireOnly(JsonElement root, params string[] allowed)
    {
        HashSet<string> names = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
            if (!names.Contains(property.Name)) throw new InvalidOperationException($"Unexpected argument: {property.Name}.");
    }
    private static string RequiredString(JsonElement root, string name, int max)
    {
        string result = OptionalString(root, name, max);
        return result.Length == 0 ? throw new InvalidOperationException($"{name} is required.") : result;
    }
    private static string OptionalString(JsonElement root, string name, int max)
    {
        if (!root.TryGetProperty(name, out JsonElement value)) return string.Empty;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidOperationException($"{name} must be text.");
        string result = (value.GetString() ?? string.Empty).Trim();
        if (result.Length > max) throw new InvalidOperationException($"{name} is too long.");
        return result;
    }
    private static string NullableString(JsonElement root, string name, int max)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return string.Empty;
        return OptionalString(root, name, max);
    }
    private static bool RequiredBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException($"{name} must be a boolean.");
        return value.GetBoolean();
    }
    private static int OptionalInteger(JsonElement root, string name, int fallback, int min, int max)
        => !root.TryGetProperty(name, out JsonElement value) ? fallback : (int)ReadInteger(value, name, min, max);
    private static long RequiredInteger(JsonElement root, string name, long min, long max)
        => root.TryGetProperty(name, out JsonElement value) ? ReadInteger(value, name, min, max) : throw new InvalidOperationException($"{name} is required.");
    private static long ReadInteger(JsonElement value, string name, long min, long max)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long result) || result < min || result > max)
            throw new InvalidOperationException($"{name} is invalid.");
        return result;
    }
    private static string[] Tags(JsonElement root, string name = "tags")
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return Array.Empty<string>();
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("tags must be an array.");
        string[] tags = value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? (x.GetString() ?? string.Empty).Trim() : throw new InvalidOperationException("Each tag must be text."))
            .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();
        if (tags.Any(x => x.Length > 40)) throw new InvalidOperationException("A tag is too long.");
        return tags;
    }
    private static WorldPosition? Position(JsonElement root, string name = "position")
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"{name} must be an object or null.");
        RequireOnly(value, "x", "y", "z");
        double Read(string field)
        {
            if (!value.TryGetProperty(field, out JsonElement number) || number.ValueKind != JsonValueKind.Number || !number.TryGetDouble(out double result) || !double.IsFinite(result))
                throw new InvalidOperationException($"{name}.{field} is invalid.");
            return result;
        }
        return new WorldPosition(Read("x"), Read("y"), Read("z"));
    }
}
