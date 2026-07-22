using System.Text.Json;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class BallisticProfileTests
{
    [Fact]
    public async Task CompleteManual338Profile_CalculatesDeterministicallyAt800Meters()
    {
        using TempProfileStore temporary = new();
        BallisticProfileManager manager = temporary.Manager(Profile());
        StateBallisticProfile game = Game(ace: true, available: false);
        using JsonDocument arguments = Arguments(800, 45);
        FrozenBallisticEnvironment weather = new(3, -1, 18, 0.6, 100);
        BallisticToolService service = new((_, _, _) => Task.FromResult(100d), profiles: manager, environment: () => weather);

        string first = await service.CalculateAsync(arguments.RootElement, game, TestContext.Current.CancellationToken);
        string second = await service.CalculateAsync(arguments.RootElement, game, TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        using JsonDocument result = JsonDocument.Parse(first);
        JsonElement solution = result.RootElement.GetProperty("firingSolution");
        Assert.True(solution.GetProperty("available").GetBoolean());
        Assert.Equal("ace-compatible-resolved-profile", solution.GetProperty("model").GetString());
        Assert.Equal("Vector .338 LM 250 gr", solution.GetProperty("profileName").GetString());
        Assert.Equal(800, solution.GetProperty("rangeMeters").GetDouble());
        Assert.True(solution.GetProperty("timeOfFlightSeconds").GetDouble() > 0);
        Assert.True(solution.GetProperty("impactVelocityMetersPerSecond").GetDouble() > 0);
    }

    [Fact]
    public async Task ManualProfile_Supports4800OnlyWhenConfiguredMaximumAllowsIt()
    {
        using TempProfileStore temporary = new();
        UserBallisticProfile profile = Profile(); profile.MaximumSupportedRangeMeters = 5000;
        BallisticProfileManager manager = temporary.Manager(profile);
        using JsonDocument request = Arguments(4800, 45);
        BallisticToolService service = new((_, _, _) => Task.FromResult(100d), profiles: manager);
        string result = await service.CalculateAsync(request.RootElement, Game(), TestContext.Current.CancellationToken);
        Assert.Contains("\"available\":true", result, StringComparison.Ordinal);

        profile.MaximumSupportedRangeMeters = 1200; manager.Save();
        string rejected = await service.CalculateAsync(request.RootElement, Game(), TestContext.Current.CancellationToken);
        Assert.Contains("range_exceeds_profile_maximum", rejected, StringComparison.Ordinal);
    }

    [Fact]
    public void Matching_IsCaseSensitiveSpecificAndFailsClosedWhenAmbiguous()
    {
        using TempProfileStore temporary = new();
        UserBallisticProfile ammo = Profile(); ammo.ProfileId = Guid.NewGuid().ToString(); ammo.DisplayName = "ammo"; ammo.WeaponClassMatch = ""; ammo.MuzzleClassMatch = ""; ammo.MagazineClassMatch = "";
        UserBallisticProfile exact = Profile(); exact.ProfileId = Guid.NewGuid().ToString(); exact.DisplayName = "exact";
        BallisticProfileManager manager = temporary.Manager(ammo, exact);
        Assert.Equal("exact", manager.Match(Game()).Profile!.DisplayName);
        exact.WeaponClassMatch = "Srifle_LRR_F";
        Assert.Equal("ammo", manager.Match(Game()).Profile!.DisplayName);
        ammo.WeaponClassMatch = "SRIFLE_LRR_F";
        Assert.Null(manager.Match(Game()).Profile);

        ammo.WeaponClassMatch = ""; exact.WeaponClassMatch = ""; exact.MuzzleClassMatch = ""; exact.MagazineClassMatch = "";
        Assert.Equal("ambiguous_manual_ballistic_profile", manager.Match(Game()).Reason);
    }

    [Fact]
    public void ForcedProfileOverridesAutomaticButInvalidForcedProfileFailsClosed()
    {
        using TempProfileStore temporary = new();
        UserBallisticProfile exact = Profile(); UserBallisticProfile forced = Profile(); forced.ProfileId = Guid.NewGuid().ToString(); forced.DisplayName = "forced"; forced.WeaponClassMatch = "does-not-match";
        BallisticProfileManager manager = temporary.Manager(exact, forced);
        manager.ForceTemporary(forced.ProfileId);
        Assert.Equal("forced", manager.Match(Game()).Profile!.DisplayName);
        forced.BallisticCoefficients.Clear();
        Assert.Equal("invalid_forced_ballistic_profile", manager.Match(Game()).Reason);
        manager.ForceTemporary(null);
        Assert.Equal(exact.ProfileId, manager.Match(Game()).Profile!.ProfileId);
    }

    [Fact]
    public void Resolution_UsesManualOverridesThenLiveGameFallbackWithProvenance()
    {
        using TempProfileStore temporary = new();
        UserBallisticProfile profile = Profile(); profile.NominalMuzzleVelocityMetersPerSecond = null;
        BallisticProfileManager manager = temporary.Manager(profile);
        ResolvedBallisticProfile resolved = manager.Resolve(Game(ace: true), out _)!;
        Assert.Equal(915, resolved.MuzzleVelocityMetersPerSecond);
        Assert.Equal("ace-config", resolved.Provenance["muzzleVelocity"]);
        profile.NominalMuzzleVelocityMetersPerSecond = 860;
        resolved = manager.Resolve(Game(ace: true), out _)!;
        Assert.Equal(860, resolved.MuzzleVelocityMetersPerSecond);
        Assert.Equal("manual", resolved.Provenance["muzzleVelocity"]);
    }

    [Fact]
    public void Storage_IsVersionedAtomicStrictAndExportsNoCredentials()
    {
        using TempProfileStore temporary = new();
        BallisticProfileManager manager = temporary.Manager(Profile());
        string exported = manager.Export();
        Assert.Contains(BallisticProfileDocument.CurrentSchema, exported, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", exported, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transcript", exported, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(temporary.Path + ".tmp"));
        Assert.Throws<JsonException>(() => manager.PreviewImport("{\"schema\":\"arma-ai-bridge/ballistic-profiles-v1\",\"profiles\":[],\"unknown\":1}"));
    }

    [Fact]
    public void ValidationRejectsMalformedCoefficientAndBarrelTables()
    {
        UserBallisticProfile profile = Profile(); profile.VelocityBoundariesMetersPerSecond = [700, 500];
        BallisticProfileValidation coefficient = BallisticProfileValidator.Validate(profile);
        Assert.Contains(coefficient.Errors, error => error.Contains("Coefficient count", StringComparison.Ordinal));
        profile.BallisticCoefficients = [0.31, 0.30, 0.29]; profile.BarrelLengthsMillimeters = [600]; profile.BarrelMuzzleVelocitiesMetersPerSecond = [];
        Assert.Contains(BallisticProfileValidator.Validate(profile).Errors, error => error.Contains("Barrel-length", StringComparison.Ordinal));
    }

    [Fact]
    public void CompactCapabilityNeverContainsRawTablesNotesOrCoefficients()
    {
        using TempProfileStore temporary = new();
        UserBallisticProfile profile = Profile(); profile.Notes = "private detailed load notes";
        BallisticProfileManager manager = temporary.Manager(profile);
        string compact = JsonSerializer.Serialize(manager.CompactCapability(Game()));
        Assert.Contains("Vector .338 LM 250 gr", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("private detailed", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("ballisticCoefficients", compact, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0.31", compact, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrosswindDirectionChangesOnlyCompactWindCorrectionAndCallsNoProvider()
    {
        using TempProfileStore temporary = new(); BallisticProfileManager manager = temporary.Manager(Profile());
        using JsonDocument args = Arguments(800, 0); StateBallisticProfile game = Game();
        string left = await new BallisticToolService((_, _, _) => Task.FromResult(100d), profiles: manager, environment: () => new(4, 0, 15, .5, 100)).CalculateAsync(args.RootElement, game, TestContext.Current.CancellationToken);
        string right = await new BallisticToolService((_, _, _) => Task.FromResult(100d), profiles: manager, environment: () => new(-4, 0, 15, .5, 100)).CalculateAsync(args.RootElement, game, TestContext.Current.CancellationToken);
        Assert.NotEqual(left, right);
        Assert.Contains("horizontalCorrectionMilliradians", left, StringComparison.Ordinal);
    }

    private static UserBallisticProfile Profile() => new()
    {
        DisplayName = "Vector .338 LM 250 gr", WeaponClassMatch = "srifle_LRR_F", MuzzleClassMatch = "srifle_LRR_F",
        MagazineClassMatch = "7Rnd_408_Mag", AmmunitionClassMatch = "B_408_Ball", BulletDiameterMillimeters = 8.6,
        BulletLengthMillimeters = 39.5, BulletMassGrams = 16.2, DragModel = "G7", BallisticCoefficients = [0.31],
        NominalMuzzleVelocityMetersPerSecond = 860, BarrelLengthMillimeters = 660, BarrelTwistMillimetersPerTurn = 254,
        TwistDirection = BallisticTwistDirection.Right, MaximumSupportedRangeMeters = 2000, WindCorrectionEnabled = true
    };

    private static JsonDocument Arguments(double range, double bearing) => JsonDocument.Parse($"{{\"rangeMeters\":{range},\"bearingDegrees\":{bearing},\"targetElevationAslMeters\":100,\"targetHeightAboveTerrainMeters\":null}}");
    private static StateBallisticProfile Game(bool ace = false, bool available = true) => new(
        available, available ? "" : "ace_ballistic_profile_incomplete", ace ? "ace3-advanced-ballistics" : "arma-vanilla-config", "bullet",
        "srifle_LRR_F", "Vector .338 LM", "srifle_LRR_F", "Single", "7Rnd_408_Mag", "10-round .338 LM",
        "B_408_Ball", ".338 Lapua Magnum", "shotBullet", 10, 300, 3, 915, -0.0005, 1, 850,
        new WorldPosition(1000, 2000, 100), ace, ace, true, "3.21.1", "3.21.x", false, true, 0.3, "fixture");

    private sealed class TempProfileStore : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aab-ballistics-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "profiles.json");
        public BallisticProfileManager Manager(params UserBallisticProfile[] profiles)
        {
            Directory.CreateDirectory(_directory); BallisticProfileManager manager = new(new BallisticProfileStore(Path));
            foreach (UserBallisticProfile profile in profiles) manager.Add(profile);
            return manager;
        }
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
}
