using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public interface IStateRepository
{
    StatePlayer? GetPlayer();
    StateEnvironment? GetEnvironment();
    StateTimeAstronomy? GetTimeAstronomy();
    StateLoadout? GetLoadout();
    IReadOnlyList<StateFriendlyGroup> GetFriendlyGroups(int limit = 100, bool includeStale = false);
    IReadOnlyList<StateFriendlyUnit> GetFriendlyUnits(int limit = 100, bool includeStale = false);
    IReadOnlyList<StateKnownContact> GetKnownContacts(int limit = 100, bool includeStale = false);
    IReadOnlyList<StateTask> GetTasks(int limit = 100, bool includeStale = false);
    IReadOnlyList<StateMarker> GetMarkers(int limit = 100, bool includeStale = false);
    IReadOnlyList<MapGazetteerLocation> GetNamedLocations(string? query = null, int limit = 100);
    IReadOnlyList<MapGazetteerLocation> GetNearestNamedLocations(WorldPosition position, int limit = 10);
    IReadOnlyList<StateSectionMetadata> GetSectionMetadata();
    StateRepositoryDiagnostics GetDiagnostics();
}
