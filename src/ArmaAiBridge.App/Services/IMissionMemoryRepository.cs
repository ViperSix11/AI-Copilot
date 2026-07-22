using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public interface IMissionMemoryRepository
{
    string ActiveMissionKey { get; }
    IReadOnlyList<MissionContactTrack> GetContactTracks(int limit = 256, bool includeForgotten = false);
    IReadOnlyList<MissionContactObservation> GetContactObservations(string trackId, int limit = 20);
    bool MarkContactDead(string trackId);
    bool ForgetContact(string trackId);
    long Remember(string text, string provenance, IReadOnlyList<string>? tags = null, WorldPosition? position = null);
    IReadOnlyList<MissionMemoryEntry> SearchMemory(string query, int limit = 12, int maximumCharacters = 6000);
    bool UpdateMemory(long id, string text, IReadOnlyList<string>? tags = null, WorldPosition? position = null);
    bool ForgetMemory(long id);
    IReadOnlyList<LoreSection> GetLoreSections();
    void SaveLoreSection(string scope, string content, bool enabled, bool alwaysInclude);
    void ClearLoreSection(string scope);
}
