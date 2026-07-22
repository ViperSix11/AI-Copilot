using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class MissionMemoryConversationService
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromMinutes(5);
    private readonly IMissionMemoryRepository _repository;
    private readonly TimeProvider _timeProvider;
    private bool _receiving;
    private DateTimeOffset _lastReceiveAt;
    private long[] _pendingBroadDelete = Array.Empty<long>();

    public MissionMemoryConversationService(IMissionMemoryRepository repository, TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryHandle(string input, out string response)
    {
        string text = (input ?? string.Empty).Trim();
        string lower = text.ToLowerInvariant();
        if (_pendingBroadDelete.Length > 0)
        {
            if (lower is "yes" or "confirm" or "confirm deletion")
            {
                int deleted = _pendingBroadDelete.Count(_repository.ForgetMemory);
                _pendingBroadDelete = Array.Empty<long>(); response = $"Deleted {deleted} memory entries."; return true;
            }
            if (lower is "no" or "cancel") { _pendingBroadDelete = Array.Empty<long>(); response = "Deletion cancelled."; return true; }
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
            if (!LooksFactual(text)) { response = "Not stored. Send a mission fact, or say end data entry."; return true; }
            long id = _repository.Remember(text, "user-reported"); response = $"Stored as mission memory {id}."; return true;
        }
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
            long id = _repository.Remember(content, "user-reported"); response = $"Stored as mission memory {id}."; return true;
        }
        if (lower.StartsWith("forget ", StringComparison.Ordinal) || lower.StartsWith("delete ", StringComparison.Ordinal))
        {
            string query = text[(text.IndexOf(' ') + 1)..].Trim();
            bool broad = lower.Contains("everything", StringComparison.Ordinal);
            query = query.Replace("everything about", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("the note about", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("entry", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(' ', '.', '!');
            MissionMemoryEntry[] matches = _repository.SearchMemory(query, 12, 6000).ToArray();
            if (matches.Length == 0) { response = "No matching mission memory was found."; return true; }
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
        response = string.Empty; return false;
    }

    private static bool IsEndDataEntry(string value) => value is "end data entry" or "end data" or "stop data entry" or "finished receiving data";
    private static bool ContainsContactTerm(string value) => value.Contains("hostile", StringComparison.Ordinal) || value.Contains("enemy", StringComparison.Ordinal) || value.Contains("contact", StringComparison.Ordinal);
    private static bool LooksFactual(string value)
    {
        if (value.Length < 8 || value.EndsWith('?')) return false;
        string lower = value.ToLowerInvariant();
        string[] filler = { "joke", "haha", "lol", "you are", "you're", "bitch", "idiot", "useless" };
        return !filler.Any(lower.Contains);
    }
}
