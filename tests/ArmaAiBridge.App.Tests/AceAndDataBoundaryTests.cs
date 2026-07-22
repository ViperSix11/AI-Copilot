using System.Text.Json;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class AceAndDataBoundaryTests
{
    [Fact]
    public async Task AceInstalledButDisabled_UsesDeterministicVanillaSolver()
    {
        int terrainCalls = 0;
        StateBallisticProfile profile = Profile() with
        {
            AdvancedBallisticsDetected = true,
            AceAdvancedBallisticsEnabled = false
        };
        BallisticToolService service = new((_, _, _) =>
        {
            terrainCalls++;
            return Task.FromResult(100d);
        });

        string first = await service.CalculateAsync(Arguments(), profile, TestContext.Current.CancellationToken);
        string second = await service.CalculateAsync(Arguments(), profile, TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        Assert.Contains("arma-vanilla-config", first, StringComparison.Ordinal);
        Assert.Equal(0, terrainCalls);
    }

    [Fact]
    public async Task AceActive_UsesOnlyAceAdapterAndPreservesNominalMetadata()
    {
        FakeAceAdapter adapter = new("""{"firingSolution":{"available":true,"model":"ace3-advanced-ballistics","nominalSolution":true,"muzzleVelocityVariationStandardDeviationPercent":0.35}}""");
        BallisticToolService service = new((_, _, _) => throw new Xunit.Sdk.XunitException("Vanilla terrain path must not run."), aceAdapter: adapter);
        StateBallisticProfile profile = Profile() with
        {
            Model = "ace3-advanced-ballistics",
            AdvancedBallisticsDetected = true,
            AceAdvancedBallisticsEnabled = true,
            AceAdapterAvailable = true,
            AceProfileSupported = true,
            AceMuzzleVelocityVariationEnabled = true,
            AceMuzzleVelocityVariationStandardDeviationPercent = 0.35,
            AceVersion = "3.21.0"
        };

        string result = await service.CalculateAsync(Arguments(), profile, TestContext.Current.CancellationToken);

        Assert.Equal(1, adapter.CallCount);
        Assert.Contains("ace3-advanced-ballistics", result, StringComparison.Ordinal);
        Assert.Contains("nominalSolution", result, StringComparison.Ordinal);
        Assert.DoesNotContain("ACE_ballisticCoefficients", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true, "ace_advanced_ballistics_interface_unsupported")]
    [InlineData(true, false, "ace_ballistic_profile_incomplete")]
    public async Task AceActiveUnsupported_FailsClosedWithoutVanilla(bool adapterAvailable, bool profileSupported, string reason)
    {
        BallisticToolService service = new((_, _, _) => throw new Xunit.Sdk.XunitException("Vanilla path must not run."));
        StateBallisticProfile profile = Profile() with
        {
            AdvancedBallisticsDetected = true,
            AceAdvancedBallisticsEnabled = true,
            AceAdapterAvailable = adapterAvailable,
            AceProfileSupported = profileSupported
        };

        string result = await service.CalculateAsync(Arguments(), profile, TestContext.Current.CancellationToken);

        Assert.Contains(reason, result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Land_LampHalogen_F")]
    [InlineData("Land_NavigLight")]
    [InlineData("Land_runway_edgelight")]
    [InlineData("Land_LightHouse_F")]
    [InlineData("Land_FuelStation_Feed_F")]
    [InlineData("House")]
    [InlineData("Building")]
    [InlineData("Thing")]
    [InlineData("Static")]
    [InlineData("Logic")]
    public void ForbiddenStaticClasses_AreRejectedByOutboundBoundary(string className)
        => Assert.False(ContactEligibilityPolicy.IsSafeClass(className));

    [Fact]
    public void ClosedContactPolicy_AcceptsActorAndRejectsLegacyOther()
    {
        StateKnownContact actor = Contact();
        Assert.True(ContactEligibilityPolicy.IsEligible(actor));
        Assert.False(ContactEligibilityPolicy.IsEligible(actor with { ContactType = "other" }));
        Assert.Equal("hostile infantry", ContactEligibilityPolicy.Description(actor));
    }

    [Theory]
    [InlineData("NameVillage", true)]
    [InlineData("NameCity", true)]
    [InlineData("Airport", true)]
    [InlineData("NameLocal", true)]
    [InlineData("Mount", true)]
    [InlineData("HistoricalSite", true)]
    [InlineData("VegetationBroadleaf", false)]
    [InlineData("FlatArea", false)]
    [InlineData("RockArea", false)]
    [InlineData("ModdedMysteryPoi", false)]
    public void GazetteerTypeAllowlist_FailsClosed(string type, bool expected)
        => Assert.Equal(expected, NamedLocationEligibilityPolicy.IsAllowed(type));

    [Fact]
    public void SqfCollectors_FilterBeforeIdentityAndShareOneNormalizer()
    {
        string root = RepositoryRoot();
        foreach (string file in new[] { "fn_collectContacts.sqf", "fn_collectSensorContacts.sqf" })
        {
            string source = File.ReadAllText(Path.Combine(root, "arma3", "addon-source", "arma_ai_bridge_client", "functions", file));
            int normalize = source.IndexOf("AAB_fnc_normalizeKnownContact", StringComparison.Ordinal);
            int identity = source.IndexOf("AAB_fnc_getStableEntityId", StringComparison.Ordinal);
            Assert.True(normalize >= 0 && identity > normalize);
        }
        string normalizer = File.ReadAllText(Path.Combine(root, "arma3", "addon-source", "arma_ai_bridge_client", "functions", "fn_normalizeKnownContact.sqf"));
        Assert.All(new[] { "fn_collectContacts.sqf", "fn_collectSensorContacts.sqf" }, file =>
            Assert.Contains("targetKnowledge", File.ReadAllText(Path.Combine(root, "arma3", "addon-source", "arma_ai_bridge_client", "functions", file)), StringComparison.Ordinal));
        Assert.Contains("BIS_fnc_sideIsEnemy", normalizer, StringComparison.Ordinal);
        Assert.DoesNotContain("allMissionObjects", normalizer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpenAiBoundary_HasNoArbitraryEnvironmentToolOrForbiddenFixtures()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "ArmaAiBridge.App", "Services", "OpenAiAssistantService.cs"));
        Assert.DoesNotContain("QueryEnvironmentTool", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Land_LampHalogen_F", source, StringComparison.Ordinal);
        Assert.Contains("official named locations", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AceSqfAdapter_IsBoundedVersionGatedAndNeverFiresAProjectile()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "arma3", "addon-source", "arma_ai_bridge_client", "functions", "fn_calculateAceFiringSolution.sqf"));
        Assert.Contains("3.21.", source, StringComparison.Ordinal);
        Assert.Contains("ace_atragmx_fnc_calculate_solution", source, StringComparison.Ordinal);
        Assert.Contains("diag_tickTime", source, StringComparison.Ordinal);
        Assert.Contains("weapon_changed_during_calculation", source, StringComparison.Ordinal);
        Assert.DoesNotContain("createVehicle", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" fire ", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("compile", source, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement Arguments() => JsonDocument.Parse("""{"rangeMeters":300,"bearingDegrees":90,"targetElevationAslMeters":100,"targetHeightAboveTerrainMeters":null}""").RootElement.Clone();

    private static StateBallisticProfile Profile() => new(
        true, "", "arma-vanilla-config", "bullet", "srifle_LRR_F", "M320 LRR", "srifle_LRR_F", "Single",
        "7Rnd_408_Mag", ".408 magazine", "B_408_Ball", ".408 bullet", "shotBullet", 7, 100, 0,
        910, -0.0005, 1, 910, new WorldPosition(1000, 1000, 100), false,
        false, false, "", "3.21.x", false, false, 0, "profile-1");

    private static StateKnownContact Contact() => new(
        "contact-1234abcd", "O_Soldier_F", "Rifleman", "person", "EAST", "hostile",
        new WorldPosition(1200, 1400, 0), 25, 5, 10, new[] { "group-1234abcd" },
        new StateSectionMetadata("knownContacts", StateSectionReadiness.Ready, 10, DateTimeOffset.UtcNow, 0, false));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed class FakeAceAdapter(string result) : IAceBallisticAdapter
    {
        public int CallCount { get; private set; }
        public Task<string> CalculateAsync(JsonElement arguments, StateBallisticProfile profile, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }
}
