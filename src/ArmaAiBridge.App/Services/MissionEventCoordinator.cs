using System.Text.Json;

namespace ArmaAiBridge.App.Services;

/// <summary>
/// Maintains technical observation windows without assigning semantic meaning.
/// Contact candidates receive an immediate heads-up opportunity, one silent
/// accumulation snapshot and a developed-report opportunity on the third
/// snapshot. All changed state domains are also accumulated into an independent
/// six-snapshot window.
/// </summary>
public sealed class MissionEventCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ContactEpisode> _contactEpisodes =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _domainSignatures =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _changedDomains = new(StringComparer.Ordinal);
    private string _sessionKey = string.Empty;
    private int _generalWindowSnapshots;
    private long _lastSnapshotSequence;

    public IReadOnlyList<string> Observe(
        string sessionKey,
        long snapshotSequence,
        IReadOnlyList<ContactAnnouncement> contactCandidates,
        IReadOnlyDictionary<string, string> domainSignatures,
        DateTimeOffset observedAtUtc)
    {
        lock (_gate)
        {
            if (!string.Equals(_sessionKey, sessionKey, StringComparison.Ordinal))
                ResetLocked(sessionKey);
            if (snapshotSequence <= 0) return Array.Empty<string>();

            List<string> events = new();
            bool newSnapshot = snapshotSequence > _lastSnapshotSequence;
            if (newSnapshot)
            {
                AdvanceContacts(snapshotSequence, observedAtUtc, events);
                _lastSnapshotSequence = snapshotSequence;
            }
            foreach (ContactAnnouncement candidate in contactCandidates)
            {
                if (_contactEpisodes.ContainsKey(candidate.TrackId)) continue;
                ContactEpisode episode = new(
                    "event-" + Guid.NewGuid().ToString("N")[..12],
                    candidate.TrackId,
                    candidate.Kind == "reacquired" ? "reacquired" : "possible-new",
                    snapshotSequence,
                    1);
                _contactEpisodes[candidate.TrackId] = episode;
                events.Add(ContactEvent(episode, "initial", observedAtUtc));
            }

            if (newSnapshot)
            {
                foreach ((string domain, string signature) in domainSignatures)
                {
                    if (_domainSignatures.TryGetValue(domain, out string? previous) &&
                        !string.Equals(previous, signature, StringComparison.Ordinal))
                        _changedDomains.Add(domain);
                    _domainSignatures[domain] = signature;
                }

                _generalWindowSnapshots++;
                if (_generalWindowSnapshots >= 6)
                {
                    if (_changedDomains.Count > 0)
                    {
                        events.Add(JsonSerializer.Serialize(new
                        {
                            schema = "arma-ai-bridge/normalized-event-v2",
                            eventAlias = "event-" + Guid.NewGuid().ToString("N")[..12],
                            eventType = "state-change-bundle",
                            transition = "window-complete",
                            entityAliases = Array.Empty<string>(),
                            changedDomains = _changedDomains.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                            snapshotSequence,
                            windowSnapshotCount = _generalWindowSnapshots,
                            observedAtUtc
                        }));
                    }
                    _generalWindowSnapshots = 0;
                    _changedDomains.Clear();
                }
            }
            return events;
        }
    }

    public void Reset(string sessionKey = "")
    {
        lock (_gate) ResetLocked(sessionKey);
    }

    private void AdvanceContacts(
        long sequence,
        DateTimeOffset observedAtUtc,
        ICollection<string> events)
    {
        foreach ((string trackId, ContactEpisode current) in
                 _contactEpisodes.ToArray())
        {
            if (sequence <= current.LastSequence) continue;
            int count = current.SnapshotCount + 1;
            ContactEpisode advanced = current with
            {
                LastSequence = sequence,
                SnapshotCount = count
            };
            if (count < 3)
            {
                _contactEpisodes[trackId] = advanced;
            }
            else
            {
                events.Add(ContactEvent(advanced, "developed", observedAtUtc));
                _contactEpisodes.Remove(trackId);
            }
        }
    }

    private static string ContactEvent(
        ContactEpisode episode,
        string phase,
        DateTimeOffset observedAtUtc)
        => JsonSerializer.Serialize(new
        {
            schema = "arma-ai-bridge/normalized-event-v2",
            episode.EventAlias,
            eventType = "contact-development",
            transition = phase == "initial" ? episode.InitialTransition : phase,
            entityAliases = new[] { episode.TrackAlias },
            changedDomains = new[] { "contacts" },
            snapshotSequence = episode.LastSequence,
            windowSnapshotCount = episode.SnapshotCount,
            observedAtUtc
        });

    private void ResetLocked(string sessionKey)
    {
        _sessionKey = sessionKey ?? string.Empty;
        _contactEpisodes.Clear();
        _domainSignatures.Clear();
        _changedDomains.Clear();
        _generalWindowSnapshots = 0;
        _lastSnapshotSequence = 0;
    }

    private sealed record ContactEpisode(
        string EventAlias,
        string TrackAlias,
        string InitialTransition,
        long LastSequence,
        int SnapshotCount);
}
