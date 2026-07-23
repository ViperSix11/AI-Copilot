using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

internal static class ContactPresentationPolicy
{
    public const double ClusterRadiusMeters = 250;
    public static readonly TimeSpan ObservationWindow = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan AnnouncementBatchWindow = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan SimilarAnnouncementCooldown = TimeSpan.FromSeconds(30);

    public static bool CanCluster(MissionContactTrack left, MissionContactTrack right)
        => string.Equals(left.Relationship, right.Relationship, StringComparison.Ordinal) &&
           Math.Abs((left.LastObservedAtUtc - right.LastObservedAtUtc).TotalSeconds) <=
           ObservationWindow.TotalSeconds &&
           Distance(left.EstimatedPosition, right.EstimatedPosition) <= ClusterRadiusMeters;

    public static double Distance(WorldPosition left, WorldPosition right)
        => Math.Sqrt(Math.Pow(right.X - left.X, 2) + Math.Pow(right.Y - left.Y, 2));
}
