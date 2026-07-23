using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed record ContactAnnouncement(string TrackId, string Kind, string Grid, string VisibleText);

public sealed class ContactAnnouncementDetector
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _statusByTrack = new(StringComparer.Ordinal);
    private string _sessionKey = string.Empty;
    private bool _baselineEstablished;

    public IReadOnlyList<ContactAnnouncement> Evaluate(
        string sessionKey,
        long snapshotSequence,
        IReadOnlyList<MissionContactTrack> tracks,
        string? playerCallsign)
    {
        lock (_gate)
        {
            if (!string.Equals(sessionKey, _sessionKey, StringComparison.Ordinal))
            {
                _sessionKey = sessionKey ?? string.Empty;
                _statusByTrack.Clear();
                _baselineEstablished = false;
            }
            if (snapshotSequence <= 0) return Array.Empty<ContactAnnouncement>();

            MissionContactTrack[] eligible = tracks
                .Where(track => track.Relationship is "hostile" or "unknown")
                .Where(track => track.Status is "current" or "last-known")
                .OrderBy(track => track.TrackId, StringComparer.Ordinal).ToArray();
            if (!_baselineEstablished)
            {
                foreach (MissionContactTrack track in eligible) _statusByTrack[track.TrackId] = track.Status;
                _baselineEstablished = true;
                return Array.Empty<ContactAnnouncement>();
            }

            List<ContactAnnouncement> result = new();
            HashSet<string> retained = new(StringComparer.Ordinal);
            foreach (MissionContactTrack track in eligible)
            {
                retained.Add(track.TrackId);
                bool known = _statusByTrack.TryGetValue(track.TrackId, out string? previous);
                _statusByTrack[track.TrackId] = track.Status;
                if (track.Status != "current") continue;
                string kind = !known ? "new" : previous == "last-known" ? "reacquired" : string.Empty;
                if (kind.Length == 0) continue;
                string grid = Grid(track.EstimatedPosition);
                string noun = ContactNoun(track.ContactType);
                string classification = track.Relationship == "unknown" ? "unknown" : "enemy";
                string body = kind == "new"
                    ? $"New {classification} {noun} contact, grid {grid}."
                    : $"{Capitalize(classification)} {noun} contact reacquired, grid {grid}.";
                string callsign = SafeCallsign(playerCallsign);
                string visible = callsign.Length == 0
                    ? $"Papa Bear. {body} Over."
                    : $"{callsign}, Papa Bear. {body} Over.";
                result.Add(new ContactAnnouncement(track.TrackId, kind, grid, visible));
            }
            foreach (string removed in _statusByTrack.Keys.Where(key => !retained.Contains(key)).ToArray())
                _statusByTrack.Remove(removed);
            return result;
        }
    }

    private static string Grid(WorldPosition value)
        => $"{Math.Clamp((int)Math.Floor(value.X / 100), 0, 999):000}{Math.Clamp((int)Math.Floor(value.Y / 100), 0, 999):000}";
    private static string ContactNoun(string type) => type switch
    {
        "person" => "infantry",
        "ground-vehicle" => "vehicle",
        "air" => "aircraft",
        "naval" => "vessel",
        "static-weapon" => "static weapon",
        "unmanned-ground" => "unmanned vehicle",
        "unmanned-air" => "unmanned aircraft",
        _ => ""
    };
    private static string SafeCallsign(string? value)
    {
        string result = (value ?? string.Empty).Trim();
        return result.Length is > 0 and <= 80 && !result.Any(char.IsControl) ? result : string.Empty;
    }
    private static string Capitalize(string value) => char.ToUpperInvariant(value[0]) + value[1..];
}
