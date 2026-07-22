using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class ContactSummaryService
{
    public ContactSummary Summarize(IReadOnlyList<StateKnownContact> contacts)
    {
        StateSectionMetadata? metadata = contacts.FirstOrDefault()?.Metadata;
        StateKnownContact[] eligible = contacts.Where(ContactEligibilityPolicy.IsEligible).ToArray();
        return new ContactSummary(eligible.Length,
            eligible.GroupBy(item => item.Relationship).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            eligible.GroupBy(item => item.ContactType).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            eligible.Length == 0 ? null : eligible.Min(item => NonNegative(item.LastSeenAgeSeconds)),
            eligible.Length == 0 ? null : eligible.Max(item => item.PositionErrorMeters),
            eligible.Count(item => item.Metadata.IsStale || item.LastSeenAgeSeconds > 120),
            metadata?.AgeSeconds ?? 0, metadata?.IsStale ?? true);
    }

    private static double NonNegative(double value) => value < 0 ? double.MaxValue : value;
}
