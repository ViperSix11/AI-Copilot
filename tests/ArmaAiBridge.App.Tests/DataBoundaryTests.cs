using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class DataBoundaryTests
{
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
        Assert.Contains("\"UNKNOWN\"", normalizer, StringComparison.Ordinal);
        Assert.Contains("_relationship = \"unknown\"", normalizer, StringComparison.Ordinal);
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

    private static StateKnownContact Contact() => new(
        "contact-1234abcd", "O_Soldier_F", "Rifleman", "person", "EAST", "hostile",
        new WorldPosition(1200, 1400, 0), 25, 5, 10, new[] { "group-1234abcd" },
        new StateSectionMetadata("knownContacts", StateSectionReadiness.Ready, 10, DateTimeOffset.UtcNow, 0, false));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(
                   directory.FullName, "src", "ArmaAiBridge.App", "ArmaAiBridge.App.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}
