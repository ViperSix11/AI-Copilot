using System.Text.RegularExpressions;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class MissionMemoryConversationService
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromMinutes(5);
    private static readonly Regex Grid = new(@"\bgrid\s*[0-9]{2,10}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Bearing = new(@"\bbearing\s*(?:of\s*)?[0-9]{1,3}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Range = new(@"\b[0-9]+(?:[.,][0-9]+)?\s*(?:m|metres?|meters?|km|kilometres?|kilometers?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly string[] TacticalTerms =
    {
        "landmark", "tower", "building", "house", "warehouse", "church", "bridge", "road", "route", "hill", "mountain", "forest", "tree", "rock", "wall", "gate", "checkpoint", "bunker", "fortification", "base", "camp", "airfield", "airport", "harbor", "harbour", "port", "antenna", "mast", "ruin", "minefield", "mine", "cache", "crate", "supply", "weapon", "wreck",
        "enemy", "hostile", "contact", "infantry", "soldier", "troop", "sniper", "tank", "apc", "vehicle", "truck", "car", "helicopter", "aircraft", "drone", "boat", "artillery", "mortar", "gun",
        "unknown object", "unidentified object", "unknown vehicle", "strange object", "suspicious object", "object", "structure",
        "destroyed", "damaged", "abandoned", "empty", "occupied", "blocked", "clear", "moved", "gone"
    };
    private static readonly string[] DirectionTerms =
    { "north", "northeast", "north-east", "east", "southeast", "south-east", "south", "southwest", "south-west", "west", "northwest", "north-west", "ahead", "behind", "left", "right" };
    private static readonly string[] DescriptionTerms =
    { "red", "blue", "green", "white", "black", "grey", "gray", "yellow", "orange", "large", "small", "tall", "short", "metal", "wooden", "concrete", "strange", "burning", "damaged", "camouflaged", "round", "square" };
    private static readonly string[] StateChangeTerms =
    { "destroyed", "damaged", "gone", "moved", "cleared", "clear now", "no longer", "incorrect", "correction", "updated", "abandoned", "occupied", "empty" };
    private static readonly string[] FillerTerms =
    { "joke", "haha", "hehe", "lol", "you are", "you're", "bitch", "idiot", "useless", "stupid", "fuck you", "thank you", "thanks", "good morning", "good evening" };
    private static readonly string[] HypotheticalTerms =
    { "maybe ", "perhaps ", "possibly ", "if ", "what if", "could be", "would be", "should be", "imagine ", "suppose " };
    private static readonly string[] CannotClarifyTerms =
    {
        "i have no idea", "i don't know", "i do not know", "not sure", "unknown to me",
        "i don't have", "i do not have", "cannot provide", "can't provide", "no bearing", "no range", "no grid"
    };

    private readonly IMissionMemoryRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly ReportedGridConversationService _reportedGrids;
    private bool _receiving;
    private DateTimeOffset _lastReceiveAt;
    private long[] _pendingBroadDelete = Array.Empty<long>();
    private PendingReport? _pendingReport;
    private PendingConfirmation? _pendingConfirmation;

    public MissionMemoryConversationService(IMissionMemoryRepository repository, TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _reportedGrids = new ReportedGridConversationService(repository, _timeProvider);
    }

    public bool TryHandle(string input, out string response)
    {
        string text = (input ?? string.Empty).Trim();
        string lower = text.ToLowerInvariant();
        ClearExpiredOrPreviousSessionPendingState();
        if (_pendingBroadDelete.Length > 0)
        {
            if (lower is "yes" or "confirm" or "confirm deletion")
            {
                int deleted = _pendingBroadDelete.Count(_repository.ForgetMemory);
                _pendingBroadDelete = Array.Empty<long>(); response = $"Deleted {deleted} memory entries."; return true;
            }
            if (lower is "no" or "cancel") { _pendingBroadDelete = Array.Empty<long>(); response = "Deletion cancelled."; return true; }
        }
        if (_pendingConfirmation is not null)
        {
            PendingConfirmation pending = _pendingConfirmation;
            _pendingConfirmation = null;
            if (PlayerUtteranceClassifier.IsAffirmative(text))
            {
                RememberIfNew(ConfirmedReportText(pending.Text), pending.Tags.Append("explicitly-confirmed").ToArray());
                response = "Copy. Report logged.";
                return true;
            }
            if (PlayerUtteranceClassifier.IsNegativeOrCancel(text))
            {
                response = "Copy. Report discarded.";
                return true;
            }
        }
        if (lower.Contains("ready to receive data", StringComparison.Ordinal))
        {
            _receiving = true; _lastReceiveAt = _timeProvider.GetUtcNow(); response = "Ready. Send it."; return true;
        }
        if (_receiving && _timeProvider.GetUtcNow() - _lastReceiveAt > ReceiveTimeout) _receiving = false;
        if (_receiving && IsEndDataEntry(lower))
        {
            _receiving = false; response = "Data entry ended."; return true;
        }
        if (_receiving)
        {
            _lastReceiveAt = _timeProvider.GetUtcNow();
            if (!LooksFactual(text)) { response = "Not stored. Send a direct mission fact of at least ten characters, or say end data entry."; return true; }
            RememberIfNew(text, BuildTags(text)); response = "Stored as session report."; return true;
        }
        if (_reportedGrids.TryHandle(text, out response)) return true;
        if (ContainsContactTerm(lower) && lower.Contains("dead", StringComparison.Ordinal))
        {
            MissionContactTrack[] candidates = _repository.GetContactTracks(256).Where(x => x.Status != "dead").ToArray();
            if (candidates.Length == 1 && _repository.MarkContactDead(candidates[0].TrackId))
            { response = "Contact marked confirmed dead from your explicit report."; return true; }
            if (candidates.Length > 1) { response = "Several contacts match. Specify which contact is confirmed dead."; return true; }
        }
        if ((lower.StartsWith("forget ", StringComparison.Ordinal) || lower.StartsWith("delete ", StringComparison.Ordinal)) && ContainsContactTerm(lower))
        {
            MissionContactTrack[] candidates = _repository.GetContactTracks(256).Where(x => x.Status != "forgotten").ToArray();
            if (candidates.Length == 1 && _repository.ForgetContact(candidates[0].TrackId))
            { response = "Contact forgotten."; return true; }
            if (candidates.Length > 1) { response = "Several contacts match. Specify which contact to forget."; return true; }
        }
        foreach (string prefix in new[] { "remember that ", "remember ", "note that ", "note ", "store this: ", "store ", "save this: ", "save this " })
        {
            if (!lower.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string content = text[prefix.Length..].Trim();
            if (content.Length == 0) { response = "Send the information you want stored."; return true; }
            RememberIfNew(content, BuildTags(content)); response = "Stored as session report."; return true;
        }
        if (lower.StartsWith("forget ", StringComparison.Ordinal) || lower.StartsWith("delete ", StringComparison.Ordinal))
            return HandleForget(text, lower, out response);

        if (_pendingReport is not null)
        {
            if (lower is "cancel" or "cancel report" or "disregard")
            { _pendingReport = null; response = "Pending report discarded."; return true; }
            string combined = $"{_pendingReport.Text} Additional detail: {text}";
            if (ContainsAnySubstring(lower, CannotClarifyTerms) && MissingDetails(combined).Length > 0)
            {
                _pendingReport = null;
                response = "Understood. The incomplete report was not stored.";
                return true;
            }
            if (!LooksFactual(text))
            {
                _pendingReport = null;
                response = string.Empty;
                return false;
            }
            string[] missing = MissingDetails(combined);
            if (missing.Length > 0)
            {
                _pendingReport = new PendingReport(combined, missing, _repository.ActiveMissionKey);
                response = Clarification(missing);
                return true;
            }
            _pendingReport = null;
            if (PlayerUtteranceClassifier.IsTentativeReport(combined))
                return StageConfirmation(combined, out response);
            if (SaveOrCorrect(combined, out response)) return true;
            response = string.Empty;
            return false;
        }

        if (!IsAutomaticTacticalReport(text)) { response = string.Empty; return false; }
        string[] details = MissingDetails(text);
        if (details.Length > 0)
        {
            _pendingReport = new PendingReport(text, details, _repository.ActiveMissionKey);
            response = Clarification(details);
            return true;
        }
        if (PlayerUtteranceClassifier.IsTentativeReport(text))
            return StageConfirmation(text, out response);
        if (SaveOrCorrect(text, out response)) return true;
        response = string.Empty;
        return false;
    }

    private bool HandleForget(string text, string lower, out string response)
    {
        string query = text[(text.IndexOf(' ') + 1)..].Trim();
        bool broad = lower.Contains("everything", StringComparison.Ordinal);
        query = query.Replace("everything about", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("the note about", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("entry", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(' ', '.', '!');
        MissionMemoryEntry[] matches = _repository.SearchMemory(query, 12, 6000).ToArray();
        if (matches.Length == 0) { response = "No matching session memory was found."; return true; }
        if (broad && matches.Length > 1)
        {
            _pendingBroadDelete = matches.Select(x => x.Id).ToArray();
            response = $"Confirm deletion of {matches.Length} matching memory entries."; return true;
        }
        if (matches.Length > 1)
        {
            response = "Several memory entries match. Specify which one to forget: " + string.Join(", ", matches.Select(x => x.Id)); return true;
        }
        _repository.ForgetMemory(matches[0].Id); response = "Memory entry deleted."; return true;
    }

    private bool SaveOrCorrect(string text, out string response)
    {
        if (ContainsAnyTerm(text.ToLowerInvariant(), StateChangeTerms))
        {
            string query = CorrectionQuery(text);
            MissionMemoryEntry[] matches = query.Length == 0 ? Array.Empty<MissionMemoryEntry>() : _repository.SearchMemory(query, 12, 6000)
                .Where(x => x.Provenance == "user-reported" && !string.Equals(x.Text, text, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1)
            {
                _repository.UpdateMemory(matches[0].Id, text, BuildTags(text).Append("corrected").ToArray());
                response = "Matching session report updated.";
                return false;
            }
            if (matches.Length > 1)
            {
                response = "Several stored reports could match that change. Specify the object and grid or bearing.";
                return true;
            }
        }
        RememberIfNew(text, BuildTags(text));
        response = "Stored as session report.";
        return false;
    }

    private bool StageConfirmation(string text, out string response)
    {
        string[] tags = BuildTags(text);
        _pendingConfirmation = new PendingConfirmation(text, tags, _repository.ActiveMissionKey, _timeProvider.GetUtcNow());
        string summary = ReportSummary(text);
        string support = Corroboration(text) switch
        {
            LocationCorroboration.Recent => " Recent observations support it.",
            LocationCorroboration.Earlier => " Earlier observations also indicate activity there.",
            LocationCorroboration.None => " Nothing else currently confirms it.",
            _ => string.Empty
        };
        response = $"Copy—you report {summary}.{support} Can you confirm?";
        return true;
    }

    private LocationCorroboration Corroboration(string text)
    {
        string lower = text.ToLowerInvariant();
        if (!ContainsAnyTerm(lower, "enemy", "hostile", "contact", "movement", "infantry", "tank", "vehicle", "soldier"))
            return LocationCorroboration.UnknownLocation;
        if (_repository is not IStateRepository state) return LocationCorroboration.UnknownLocation;
        SemanticLocationDefinition? location = SemanticLocationPolicy.Resolve(
            SemanticLocationPolicy.FromMarkers(state.GetMarkers(256, includeStale: true)), text);
        return SemanticLocationPolicy.Assess(location, _repository.GetContactTracks(256), _timeProvider.GetUtcNow());
    }

    private void RememberIfNew(string text, IReadOnlyList<string> tags)
    {
        if (_repository.SearchMemory(string.Empty, 12, 6000).Any(x => string.Equals(x.Text, text, StringComparison.OrdinalIgnoreCase))) return;
        _repository.Remember(text, "user-reported", tags);
    }

    private static bool IsAutomaticTacticalReport(string value)
        => LooksFactual(value) && ContainsAnyTerm(value.ToLowerInvariant(), TacticalTerms);

    private static bool LooksFactual(string value)
    {
        if (value.Length < 10 || PlayerUtteranceClassifier.IsQuestion(value) || PlayerUtteranceClassifier.IsPlanningIntent(value)) return false;
        string lower = value.ToLowerInvariant();
        if (ContainsAnySubstring(lower, FillerTerms) || ContainsAnySubstring(lower, HypotheticalTerms)) return false;
        return true;
    }

    private static string[] MissingDetails(string value)
    {
        string lower = value.ToLowerInvariant();
        List<string> missing = new();
        bool grid = Grid.IsMatch(value);
        bool bearingAndRange = Bearing.IsMatch(value) && Range.IsMatch(value);
        bool directionAndRange = ContainsAnyTerm(lower, DirectionTerms) && Range.IsMatch(value);
        bool namedRelation = Regex.IsMatch(lower, @"\b(?:at|near|inside|outside|beside|north of|south of|east of|west of)\s+(?:the\s+)?[a-z][a-z0-9-]{2,}", RegexOptions.CultureInvariant);
        if (!grid && !bearingAndRange && !directionAndRange && !namedRelation) missing.Add("location");
        bool needsDescription = ContainsAnyTerm(lower,
            "unknown object", "unidentified object", "strange object", "suspicious object", "object", "tower", "landmark");
        bool hasDescription = ContainsAnyTerm(lower, DescriptionTerms) || ContainsAnyTerm(lower, StateChangeTerms);
        if (needsDescription && !hasDescription) missing.Add("description");
        return missing.ToArray();
    }

    private static string Clarification(IReadOnlyCollection<string> missing)
    {
        if (missing.Contains("location") && missing.Contains("description"))
            return "Before I store that report, give me a grid or bearing and range, describe the object, and say whether it is mission-relevant.";
        if (missing.Contains("location"))
            return "Before I store that report, give me a grid, or a bearing and range.";
        return "Before I store that report, describe what the object looks like and whether it is mission-relevant.";
    }

    private static string CorrectionQuery(string text)
    {
        HashSet<string> ignored = new(StringComparer.OrdinalIgnoreCase)
        { "the", "that", "this", "earlier", "saw", "seen", "reported", "report", "is", "was", "now", "has", "been", "additional", "detail" };
        foreach (string term in StateChangeTerms) ignored.Add(term);
        return string.Join(' ', Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+", RegexOptions.CultureInvariant)
            .Select(x => x.Value).Where(x => x.Length > 1 && !ignored.Contains(x)).Take(6));
    }

    private static string[] BuildTags(string text)
    {
        string lower = text.ToLowerInvariant();
        List<string> tags = new() { "player-report", "session-scoped" };
        tags.AddRange(TacticalTerms.Where(term => term.Length <= 40 && ContainsTerm(lower, term)).Take(10));
        Match grid = Grid.Match(text);
        if (grid.Success) tags.Add(grid.Value.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant());
        return tags.Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();
    }

    private static bool IsEndDataEntry(string value) => value is "end data entry" or "end data" or "stop data entry" or "finished receiving data";
    private void ClearExpiredOrPreviousSessionPendingState()
    {
        string session = _repository.ActiveMissionKey;
        if (_pendingReport is not null && !string.Equals(_pendingReport.SessionKey, session, StringComparison.Ordinal))
            _pendingReport = null;
        if (_pendingConfirmation is not null &&
            (!string.Equals(_pendingConfirmation.SessionKey, session, StringComparison.Ordinal) ||
             _timeProvider.GetUtcNow() - _pendingConfirmation.CreatedAt > ConfirmationTimeout))
            _pendingConfirmation = null;
    }
    private static string ReportSummary(string text)
    {
        string value = text.Trim().TrimEnd('.', '!', '?');
        value = Regex.Replace(value,
            @"^(?:be advised[,:]?\s*)?(?:it seems(?: like)?(?: there is| that)?|i think(?: there is| that)?|i believe(?: there is| that)?|it appears(?: that)?|appears to be|looks like|sounds like)\s+",
            string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        value = Regex.Replace(value, @"^there (?:is|are)\s+", string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        return value.Length == 0 ? "possible activity" : char.ToLowerInvariant(value[0]) + value[1..];
    }
    private static string ConfirmedReportText(string text)
    {
        string summary = ReportSummary(text);
        return char.ToUpperInvariant(summary[0]) + summary[1..].TrimEnd('.', '!', '?') + ".";
    }
    private static bool ContainsContactTerm(string value) => ContainsAnyTerm(value, "hostile", "enemy", "contact");
    private static bool ContainsAnySubstring(string value, params string[] terms) => terms.Any(value.Contains);
    private static bool ContainsAnyTerm(string value, params string[] terms) => terms.Any(term => ContainsTerm(value, term));
    private static bool ContainsTerm(string value, string term)
    {
        int start = 0;
        while ((start = value.IndexOf(term, start, StringComparison.Ordinal)) >= 0)
        {
            bool before = start == 0 || !char.IsLetterOrDigit(value[start - 1]);
            int end = start + term.Length;
            bool after = end == value.Length || !char.IsLetterOrDigit(value[end]);
            if (!after)
            {
                foreach (string suffix in new[] { "s", "es", "ies" })
                {
                    int suffixEnd = end + suffix.Length;
                    if (suffixEnd <= value.Length && value.AsSpan(end, suffix.Length).SequenceEqual(suffix.AsSpan()) &&
                        (suffixEnd == value.Length || !char.IsLetterOrDigit(value[suffixEnd])))
                    { after = true; break; }
                }
            }
            if (before && after) return true;
            start++;
        }
        return false;
    }
    private sealed record PendingReport(string Text, IReadOnlyList<string> Missing, string SessionKey);
    private sealed record PendingConfirmation(string Text, IReadOnlyList<string> Tags, string SessionKey, DateTimeOffset CreatedAt);
}
