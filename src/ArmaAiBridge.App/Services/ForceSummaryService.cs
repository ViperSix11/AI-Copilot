using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class ForceSummaryService
{
    public ForceSummary Summarize(IReadOnlyList<StateFriendlyGroup> groups, IReadOnlyList<StateFriendlyUnit> units)
    {
        StateSectionMetadata? metadata = groups.FirstOrDefault()?.Metadata ?? units.FirstOrDefault()?.Metadata;
        return new ForceSummary(groups.Count, units.Count,
            units.Count(unit => unit.Alive && unit.Damage > 0 && !Incapacitated(unit)),
            units.Count(Incapacitated), units.Count(unit => !unit.Alive),
            metadata?.AgeSeconds ?? 0, metadata?.IsStale ?? true);
    }

    private static bool Incapacitated(StateFriendlyUnit unit)
        => unit.Alive && unit.LifeState is "INCAPACITATED" or "UNCONSCIOUS";
}
