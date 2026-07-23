using System.Text.Json;

namespace ArmaAiBridge.App.Services;

/// <summary>
/// Persists the player's original message before any model interpretation and
/// accepts a separately labelled, locally validated semantic interpretation.
/// The two records are intentionally distinct so inferred meaning can never
/// replace what the player actually said.
/// </summary>
public sealed class PlayerInformationJournal
{
    private static readonly HashSet<string> Bases = new(StringComparer.Ordinal)
    {
        "explicit", "inferred"
    };

    private static readonly HashSet<string> Confidences = new(StringComparer.Ordinal)
    {
        "reported", "low", "medium", "high"
    };

    private static readonly HashSet<string> ClarificationStatuses = new(StringComparer.Ordinal)
    {
        "none", "requested", "answered", "declined", "unknown", "deferred",
        "no_response", "no_longer_relevant", "superseded"
    };

    private readonly IMissionMemoryRepository _repository;
    private readonly object _eventGate = new();
    private readonly HashSet<string> _eventCandidates = new(StringComparer.Ordinal);

    public PlayerInformationJournal(IMissionMemoryRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public long RecordRaw(string text, UserTurnSource source)
    {
        string content = RequireText(text, 2000, "Player message");
        return _repository.Remember(
            content,
            "raw-player-message",
            ["raw-player-message", $"input:{source.ToString().ToLowerInvariant()}"]);
    }

    public string Execute(JsonElement root)
    {
        RequireObject(root);
        RequireOnly(
            root,
            "group", "category", "subject", "summary", "basis", "confidence",
            "clarificationStatus", "clarificationTopic", "clarificationReason");

        string group = HierarchicalContextCatalogue.NormalizeGroup(
            RequiredString(root, "group", 80));
        string category = HierarchicalContextCatalogue.NormalizeCategory(
            RequiredString(root, "category", 80));
        if (!HierarchicalContextCatalogue.Contains(group, category))
            throw new InvalidOperationException(
                "The interpretation category does not belong to the selected group.");

        string subject = RequiredString(root, "subject", 160);
        string summary = RequiredString(root, "summary", 2000);
        string basis = RequiredEnum(root, "basis", Bases);
        string confidence = RequiredEnum(root, "confidence", Confidences);
        string clarificationStatus = RequiredEnum(
            root,
            "clarificationStatus",
            ClarificationStatuses);
        string clarificationTopic = NullableString(root, "clarificationTopic", 160);
        string clarificationReason = NullableString(root, "clarificationReason", 400);

        if (clarificationStatus != "none" && clarificationTopic.Length == 0)
            throw new InvalidOperationException(
                "A clarification topic is required for a clarification state.");

        List<string> tags =
        [
            "structured-player-information",
            $"group:{group}",
            $"category:{category}",
            $"subject:{NormalizeTag(subject)}",
            $"basis:{basis}",
            $"confidence:{confidence}"
        ];
        if (clarificationStatus != "none")
        {
            tags.Add($"clarification:{clarificationStatus}");
            tags.Add($"clarification-topic:{NormalizeTag(clarificationTopic)}");
        }

        long id = _repository.Remember(
            summary,
            basis == "explicit" ? "player-interpreted-explicit" : "ai-derived-from-player",
            tags);

        long? clarificationId = null;
        if (clarificationStatus != "none")
        {
            string clarification = $"Clarification for {clarificationTopic}: {clarificationStatus}.";
            if (clarificationReason.Length > 0)
                clarification += $" {clarificationReason}";
            clarificationId = _repository.Remember(
                clarification,
                "clarification-state",
                [
                    "clarification-state",
                    $"clarification:{clarificationStatus}",
                    $"clarification-topic:{NormalizeTag(clarificationTopic)}"
                ]);
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            id,
            clarificationId,
            group,
            category,
            basis,
            confidence,
            clarificationStatus
        });
    }

    public long RecordEventCandidate(string normalizedEventJson)
    {
        using JsonDocument document = JsonDocument.Parse(normalizedEventJson);
        JsonElement root = document.RootElement;
        string eventAlias = RequiredString(root, "eventAlias", 86);
        if (!eventAlias.StartsWith("event-", StringComparison.Ordinal))
            throw new InvalidOperationException("The event alias is invalid.");
        lock (_eventGate) _eventCandidates.Add(eventAlias);
        return _repository.Remember(
            root.GetRawText(),
            "normalized-event-candidate",
            ["event-candidate", $"event:{eventAlias}"]);
    }

    public string ExecuteEventAssessment(JsonElement root)
    {
        RequireObject(root);
        RequireOnly(root, "eventAlias", "priority", "outcome", "summary", "confidence");
        string eventAlias = RequiredString(root, "eventAlias", 86);
        lock (_eventGate)
            if (!_eventCandidates.Contains(eventAlias))
                throw new InvalidOperationException(
                    "The event assessment does not match an active event candidate.");
        string priority = RequiredEnum(
            root,
            "priority",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "critical", "immediate_developing", "important", "routine",
                "informational", "ignored"
            });
        string outcome = RequiredEnum(
            root,
            "outcome",
            new HashSet<string>(StringComparer.Ordinal)
            {
                "warn", "sitrep", "guidance", "store", "clarify", "silent"
            });
        string summary = RequiredString(root, "summary", 800);
        string confidence = RequiredEnum(root, "confidence", Confidences);
        long id = _repository.Remember(
            summary,
            "ai-event-assessment",
            [
                "event-assessment",
                $"event:{eventAlias}",
                $"priority:{priority}",
                $"outcome:{outcome}",
                $"confidence:{confidence}"
            ]);
        lock (_eventGate) _eventCandidates.Remove(eventAlias);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            id,
            eventAlias,
            priority,
            outcome,
            confidence
        });
    }

    private static void RequireObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Tool arguments must be an object.");
    }

    private static void RequireOnly(JsonElement root, params string[] allowed)
    {
        HashSet<string> names = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
            if (!names.Contains(property.Name))
                throw new InvalidOperationException($"Unexpected argument: {property.Name}.");
    }

    private static string RequiredString(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"{name} is required.");
        return RequireText(value.GetString(), maximum, name);
    }

    private static string NullableString(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind == JsonValueKind.Null)
            return string.Empty;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"{name} must be text or null.");
        string result = (value.GetString() ?? string.Empty).Trim();
        if (result.Length > maximum)
            throw new InvalidOperationException($"{name} is too long.");
        return result;
    }

    private static string RequiredEnum(
        JsonElement root,
        string name,
        IReadOnlySet<string> allowed)
    {
        string value = RequiredString(root, name, 80)
            .ToLowerInvariant()
            .Replace('-', '_')
            .Replace(' ', '_');
        return allowed.Contains(value)
            ? value
            : throw new InvalidOperationException($"{name} is invalid.");
    }

    private static string RequireText(string? value, int maximum, string name)
    {
        string result = (value ?? string.Empty).Trim();
        if (result.Length == 0)
            throw new InvalidOperationException($"{name} is required.");
        if (result.Length > maximum || result.Any(char.IsControl))
            throw new InvalidOperationException($"{name} is invalid.");
        return result;
    }

    private static string NormalizeTag(string value)
    {
        string normalized = new(
            value.Trim().ToLowerInvariant()
                .Select(character =>
                    char.IsLetterOrDigit(character) || character is '_' or '-'
                        ? character
                        : '-')
                .ToArray());
        while (normalized.Contains("--", StringComparison.Ordinal))
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        return normalized.Trim('-')[..Math.Min(40, normalized.Trim('-').Length)];
    }
}
