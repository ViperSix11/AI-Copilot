using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class LoadoutSummaryService
{
    public LoadoutSummary Summarize(StateLoadout loadout)
    {
        StateMagazine[] reserve = loadout.Magazines
            .Where(item => !item.Loaded && (loadout.CurrentMagazine.Length == 0 || item.Class == loadout.CurrentMagazine))
            .ToArray();
        return new LoadoutSummary(loadout.SelectedWeapon, loadout.SelectedWeaponDisplayName,
            loadout.LoadedRounds, reserve.Length, reserve.Sum(item => item.Rounds), loadout.GrenadeCount,
            loadout.ThrowableCount, loadout.MineCount, loadout.ExplosiveCount,
            loadout.OpticsAndAttachments.Take(16).ToArray(), loadout.Metadata.AgeSeconds, loadout.Metadata.IsStale);
    }
}
