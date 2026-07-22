using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class ContactSummaryService
{
    public ContactSummary Summarize(IReadOnlyList<StateKnownContact> contacts)
    {
        StateSectionMetadata? metadata = contacts.FirstOrDefault()?.Metadata;
        return new ContactSummary(contacts.Count,
            contacts.GroupBy(item => item.PerceivedSide).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            contacts.GroupBy(item => item.BroadType).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            contacts.Count == 0 ? null : contacts.Min(item => NonNegative(item.LastSeenAgeSeconds)),
            contacts.Count == 0 ? null : contacts.Max(item => item.PositionErrorMeters),
            contacts.Count(item => item.Metadata.IsStale || item.LastSeenAgeSeconds > 120),
            metadata?.AgeSeconds ?? 0, metadata?.IsStale ?? true);
    }

    private static double NonNegative(double value) => value < 0 ? double.MaxValue : value;
}
