using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArmaAiBridge.App.Services;

/// <summary>
/// Projects locally validated tool results into the small, readable fact
/// snippets supplied to the language model. The structured result remains a
/// local implementation detail and is never forwarded directly.
/// </summary>
public static partial class ContextFactFormatter
{
    private const int MaximumResultCharacters = 8000;

    public static string Format(string toolName, string structuredResult)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(structuredResult);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("ok", out JsonElement ok) &&
                ok.ValueKind == JsonValueKind.False)
            {
                return "The requested local information is unavailable. Continue using other supplied facts, or state plainly that the missing detail is unavailable.";
            }
            string text = toolName switch
            {
                "inspect_context_catalogue" => FormatCatalogue(root),
                "query_context" or "query_long_term_map_intelligence" =>
                    FormatContext(root),
                "search_memory" => FormatMemorySearch(root),
                "record_player_information" =>
                    "The current message was retained and interpreted locally.",
                "record_event_assessment" =>
                    "The current state update was assessed and retained locally.",
                "remember_information" =>
                    "The stated information was retained for the current mission.",
                "update_memory" =>
                    Boolean(root, "ok")
                        ? "The retained information was corrected."
                        : "The requested retained information could not be corrected.",
                "forget_memory" =>
                    Boolean(root, "ok")
                        ? "The retained information was withdrawn."
                        : "The requested retained information could not be withdrawn.",
                _ => FormatContext(root)
            };
            return Finalize(text);
        }
        catch (JsonException)
        {
            return "No readable local facts were returned. Continue using only the facts already supplied.";
        }
    }

    private static string FormatCatalogue(JsonElement root)
    {
        string group = Humanize(String(root, "group"));
        if (!root.TryGetProperty("categories", out JsonElement categories) ||
            categories.ValueKind != JsonValueKind.Array)
            return $"No currently available information was found under {group}.";

        string[] available = categories.EnumerateArray()
            .Where(item => Boolean(item, "currentlyAvailable"))
            .Select(item => Humanize(String(item, "description", String(item, "name"))))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToArray();
        return available.Length == 0
            ? $"No currently available information was found under {group}."
            : $"{Capitalize(group)} can provide: {string.Join("; ", available)}.";
    }

    private static string FormatContext(JsonElement root)
    {
        if (root.TryGetProperty("ok", out JsonElement ok) &&
            ok.ValueKind == JsonValueKind.False)
            return "The requested local information is unavailable. Continue using other supplied facts, or state plainly that the missing detail is unavailable.";

        string category = Humanize(String(root, "category"));
        JsonElement result = root.TryGetProperty("result", out JsonElement nested) &&
                             nested.ValueKind == JsonValueKind.Object
            ? nested
            : root;
        if ((root.TryGetProperty("available", out JsonElement outerAvailable) &&
             outerAvailable.ValueKind == JsonValueKind.False) ||
            (result.TryGetProperty("available", out JsonElement available) &&
             available.ValueKind == JsonValueKind.False))
        {
            return $"No authorized information is currently available for {category}.";
        }

        List<string> facts = new();
        if (category.Length > 0) facts.Add($"{Capitalize(category)}.");
        AddSummary(facts, result);
        AddScalarFacts(facts, result);
        if (result.TryGetProperty("player", out JsonElement player) &&
            player.ValueKind == JsonValueKind.Object)
        {
            string callsign = String(player, "groupCallsign");
            string side = String(player, "side");
            if (callsign.Length > 0)
                facts.Add(side.Length > 0
                    ? $"The current group callsign is {callsign}, on side {side}."
                    : $"The current group callsign is {callsign}.");
        }
        foreach (string collection in new[]
                 {
                     "groups", "records", "reports", "units", "possibleEnemySources"
                 })
            AddRecords(facts, result, collection);

        if (facts.Count == 0)
            facts.Add("No relevant local facts were returned.");
        return string.Join(Environment.NewLine, facts.Distinct(StringComparer.Ordinal));
    }

    private static string FormatMemorySearch(JsonElement root)
    {
        if (!Boolean(root, "ok"))
            return "No retained mission information was available.";
        List<string> facts = ["Relevant retained mission information."];
        AddRecords(facts, root, "records");
        if (facts.Count == 1) facts.Add("No matching retained reports were found.");
        return string.Join(Environment.NewLine, facts);
    }

    private static void AddSummary(List<string> facts, JsonElement result)
    {
        if (!result.TryGetProperty("summary", out JsonElement summary)) return;
        if (summary.ValueKind == JsonValueKind.String)
        {
            AddFact(facts, summary.GetString());
            return;
        }
        if (summary.ValueKind != JsonValueKind.Object) return;

        int current = Integer(summary, "current");
        int lastKnown = Integer(summary, "lastKnown");
        int dead = Integer(summary, "confirmedDead");
        if (current >= 0 || lastKnown >= 0 || dead >= 0)
        {
            List<string> clauses = new();
            if (current >= 0) clauses.Add($"{Words(current)} current");
            if (lastKnown >= 0) clauses.Add($"{Words(lastKnown)} last known");
            if (dead >= 0) clauses.Add($"{Words(dead)} confirmed dead");
            facts.Add($"Known contact picture: {JoinNatural(clauses)}.");
        }
        if (Boolean(summary, "observationsMayOverlap"))
            facts.Add("Some observations may describe the same contacts.");
    }

    private static void AddScalarFacts(List<string> facts, JsonElement result)
    {
        string condition = String(result, "condition");
        if (condition.Length > 0) facts.Add($"Current conditions are {Humanize(condition)}.");

        string lighting = String(result, "lighting");
        if (lighting.Length > 0) facts.Add($"Current light condition is {Humanize(lighting)}.");

        string missionDate = String(result, "missionDate");
        if (missionDate.Length > 0) facts.Add($"Mission date: {missionDate}.");

        if (result.TryGetProperty("localMissionTime", out JsonElement time) &&
            time.ValueKind == JsonValueKind.Number)
            facts.Add($"Local mission time is approximately {time.GetRawText()}.");

        if (result.TryGetProperty("overcast", out JsonElement overcast) &&
            overcast.ValueKind == JsonValueKind.Number && condition.Length == 0)
            facts.Add($"Overcast is {overcast.GetRawText()}.");
        if (result.TryGetProperty("fog", out JsonElement fog) &&
            fog.ValueKind == JsonValueKind.Number)
            facts.Add($"Fog level is {fog.GetRawText()}.");
    }

    private static void AddRecords(
        List<string> facts,
        JsonElement root,
        string property)
    {
        if (!root.TryGetProperty(property, out JsonElement records) ||
            records.ValueKind != JsonValueKind.Array)
            return;
        foreach (JsonElement record in records.EnumerateArray().Take(32))
        {
            string fact = FormatRecord(record);
            if (fact.Length > 0) facts.Add(fact);
        }
    }

    private static string FormatRecord(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object) return string.Empty;

        string content = String(record, "content", String(record, "text"));
        if (content.Length > 0)
        {
            string provenance = String(record, "provenance");
            if (provenance == "normalized-event-candidate" ||
                LooksLikeStructuredData(content))
                return "A prior mission-state update was retained.";
            string source = SourcePhrase(provenance);
            string conversationRole = String(record, "role");
            if (source.Length == 0)
                source = conversationRole switch
                {
                    "user" => "You said",
                    "assistant" => "Papa Bear said",
                    _ => string.Empty
                };
            if (source.Length == 0)
            {
                string scope = String(record, "scope");
                if (scope.Length > 0) source = $"{scope} context";
            }
            return Sentence(source.Length == 0 ? content : $"{source}: {content}");
        }

        string description = String(record, "description");
        if (description.Length > 0 &&
            record.TryGetProperty("distance", out _))
            return Sentence(description);

        string officialName = String(record, "officialName");
        if (officialName.Length > 0)
        {
            string type = Humanize(String(record, "type"));
            string grid = String(record, "grid");
            string fact = type.Length == 0
                ? $"{officialName} is an official named location"
                : $"{officialName} is an official {type}";
            if (grid.Length > 0) fact += $" at grid {grid}";
            return Sentence(fact);
        }

        string label = String(record, "label");
        if (label.Length > 0)
        {
            string position = String(record, "position");
            string shape = Humanize(String(record, "shape"));
            string fact = shape.Length == 0
                ? $"{label} is a known mission location"
                : $"{label} is a known {shape} mission area";
            if (position.Length > 0) fact += $" at {position}";
            return Sentence(fact);
        }

        string title = String(record, "title");
        if (title.Length > 0)
        {
            string summary = String(record, "summary", description);
            string status = Humanize(String(record, "status"));
            string position = String(record, "position");
            string fact = title;
            if (status.Length > 0) fact += $" is {status}";
            if (position.Length > 0) fact += $" at {position}";
            if (summary.Length > 0) fact += $". {summary}";
            string source = SourcePhrase(String(record, "source"));
            if (source.Length > 0) fact += $". Source: {source}";
            return Sentence(fact);
        }

        if (record.TryGetProperty("relationship", out _) ||
            record.TryGetProperty("classification", out _))
            return FormatContact(record);

        string callsign = String(record, "callsign");
        if (callsign.Length > 0)
        {
            int members = Integer(record, "memberCount");
            string position = String(record, "position");
            string fact = members >= 0
                ? $"{callsign} has {Words(members)} known members"
                : callsign;
            if (position.Length > 0) fact += $" and is {position}";
            if (Boolean(record, "stale")) fact += ". This is a last-known report";
            return Sentence(fact);
        }

        string role = Humanize(String(record, "role"));
        if (role.Length > 0)
        {
            string damage = Humanize(String(record, "damageState"));
            string position = String(record, "position");
            string fact = $"A friendly {role}";
            if (record.TryGetProperty("alive", out JsonElement alive) &&
                alive.ValueKind is JsonValueKind.True or JsonValueKind.False)
                fact += alive.GetBoolean() ? " is alive" : " is dead";
            if (damage.Length > 0) fact += $" and {damage}";
            if (position.Length > 0) fact += $" at {position}";
            if (Boolean(record, "stale")) fact += ". This is a last-known report";
            return Sentence(fact);
        }

        return string.Empty;
    }

    private static string FormatContact(JsonElement record)
    {
        int count = Integer(record, "memberCount");
        string relationship = Humanize(String(record, "relationship"));
        string[] classifications = Strings(record, "classification");
        string classification = classifications.Length == 0
            ? Humanize(String(record, "classification"))
            : string.Join(" and ", classifications.Select(Humanize));
        if (classification.Length == 0) classification = "contact";
        string noun = count == 1 ? "contact" : "contacts";
        string fact = count >= 0
            ? $"{Words(count)} {relationship} {classification} {noun}".Trim()
            : $"A {relationship} {classification} contact".Trim();

        string status = Humanize(String(record, "status"));
        if (status.Length > 0) fact += $" are {status}";
        string position = String(record, "position");
        if (position.Length > 0) fact += $" at {position}";
        int uncertainty = Integer(record, "uncertaintyMeters");
        if (uncertainty > 25) fact += $", with about {Words(uncertainty)} metres of position uncertainty";
        int age = Integer(record, "lastObservedSecondsAgo");
        if (age > 0) fact += $". Last observed about {Words(age)} seconds ago";
        string[] reporters = Strings(record, "reporters");
        if (reporters.Length > 0) fact += $". Reported by {JoinNatural(reporters)}";
        return Sentence(fact);
    }

    private static string SourcePhrase(string provenance)
        => provenance switch
        {
            "raw-player-message" or "user-reported" or
                "player-interpreted-explicit" => "You reported",
            "ai-derived-from-player" => "Earlier interpretation",
            "clarification-state" => "Clarification state",
            "normalized-event-candidate" => "Earlier state update",
            "ai-event-assessment" => "Earlier assessment",
            _ => Humanize(provenance)
        };

    private static string Finalize(string text)
    {
        string clean = string.Join(
            Environment.NewLine,
            text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => Whitespace().Replace(line, " ").Trim())
                .Where(line => line.Length > 0));
        clean = RadioSpeechTextNormalizer.Normalize(clean);
        return clean.Length <= MaximumResultCharacters
            ? clean
            : clean[..MaximumResultCharacters].TrimEnd() +
              Environment.NewLine + "Additional matching facts were omitted.";
    }

    private static void AddFact(List<string> facts, string? value)
    {
        string clean = Safe(value);
        if (clean.Length > 0) facts.Add(Sentence(clean));
    }

    private static string Safe(string? value)
        => Whitespace().Replace(value ?? string.Empty, " ").Trim()[..Math.Min(
            800,
            Whitespace().Replace(value ?? string.Empty, " ").Trim().Length)];

    private static string String(JsonElement root, string property, string fallback = "")
        => root.TryGetProperty(property, out JsonElement value) &&
           value.ValueKind == JsonValueKind.String
            ? Safe(value.GetString())
            : fallback;

    private static string[] Strings(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement value)) return [];
        if (value.ValueKind == JsonValueKind.String)
        {
            string single = Safe(value.GetString());
            return single.Length == 0 ? [] : [single];
        }
        if (value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => Safe(item.GetString()))
            .Where(item => item.Length > 0)
            .Take(12)
            .ToArray();
    }

    private static bool Boolean(JsonElement root, string property)
        => root.TryGetProperty(property, out JsonElement value) &&
           value.ValueKind == JsonValueKind.True;

    private static int Integer(JsonElement root, string property)
        => root.TryGetProperty(property, out JsonElement value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out int number)
            ? number
            : -1;

    private static string Humanize(string value)
        => Safe(value.Replace('_', ' ').Replace('-', ' ')).ToLowerInvariant();

    private static bool LooksLikeStructuredData(string value)
    {
        string trimmed = value.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[') ||
               trimmed.Contains("\"schema\"", StringComparison.Ordinal) ||
               trimmed.Contains("\"eventAlias\"", StringComparison.Ordinal);
    }

    private static string Capitalize(string value)
        => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static string Words(int value)
        => RadioSpeechTextNormalizer.NumberToWords(value.ToString(
            System.Globalization.CultureInfo.InvariantCulture));

    private static string JoinNatural(IReadOnlyList<string> values)
        => values.Count switch
        {
            0 => string.Empty,
            1 => values[0],
            2 => $"{values[0]} and {values[1]}",
            _ => string.Join(", ", values.Take(values.Count - 1)) + $", and {values[^1]}"
        };

    private static string Sentence(string value)
    {
        string clean = Safe(value);
        return clean.Length == 0 || clean[^1] is '.' or '!' or '?'
            ? clean
            : clean + ".";
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
