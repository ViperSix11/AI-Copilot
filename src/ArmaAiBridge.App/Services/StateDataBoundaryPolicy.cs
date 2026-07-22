using System.Text.RegularExpressions;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public static partial class ContactEligibilityPolicy
{
    public static readonly IReadOnlySet<string> ContactTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "person", "ground-vehicle", "air", "naval", "static-weapon",
        "unmanned-ground", "unmanned-air"
    };

    public static readonly IReadOnlySet<string> PerceivedSides = new HashSet<string>(StringComparer.Ordinal)
    { "WEST", "EAST", "GUER", "CIV", "ENEMY" };

    public static readonly IReadOnlySet<string> Relationships = new HashSet<string>(StringComparer.Ordinal)
    { "friendly", "hostile", "neutral", "civilian" };

    private static readonly string[] ForbiddenClassPrefixes =
    {
        "Land_", "House_", "Building_", "Thing", "Static", "Logic", "Animal"
    };

    private static readonly HashSet<string> ForbiddenClasses = new(StringComparer.OrdinalIgnoreCase)
    { "House", "Building", "Thing", "Static", "Logic", "Location", "Animal", "Man" };

    public static bool IsEligible(StateKnownContact contact)
        => contact is not null && ValidAlias(contact.Alias) && IsSafeClass(contact.Class) &&
           ContactTypes.Contains(contact.ContactType) && PerceivedSides.Contains(contact.PerceivedSide) &&
           Relationships.Contains(contact.Relationship) && ValidPosition(contact.EstimatedPosition) &&
           double.IsFinite(contact.PositionErrorMeters) && contact.PositionErrorMeters is >= 0 and <= 100000 &&
           ValidAge(contact.LastSeenAgeSeconds) && ValidAge(contact.LastThreatAgeSeconds) &&
           contact.ObserverGroupAliases is { Count: > 0 and <= 128 } &&
           contact.ObserverGroupAliases.All(ValidGroupAlias);

    public static bool IsSafeClass(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl) &&
           !ForbiddenClasses.Contains(value) &&
           !ForbiddenClassPrefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public static string Description(StateKnownContact contact)
    {
        string noun = contact.ContactType switch
        {
            "person" => contact.Relationship == "civilian" ? "person" : "infantry",
            "ground-vehicle" => "ground vehicle",
            "air" => "aircraft",
            "naval" => "naval vessel",
            "static-weapon" => "static weapon",
            "unmanned-ground" => "unmanned ground vehicle",
            "unmanned-air" => "unmanned aircraft",
            _ => "contact"
        };
        return $"{contact.Relationship} {noun}";
    }

    private static bool ValidPosition(WorldPosition value)
        => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z) &&
           Math.Abs(value.X) <= 10_000_000 && Math.Abs(value.Y) <= 10_000_000 && Math.Abs(value.Z) <= 10_000_000;

    private static bool ValidAge(double value) => double.IsFinite(value) && value is >= -1 and <= 600;
    private static bool ValidAlias(string value) => ContactAliasRegex().IsMatch(value ?? string.Empty);
    private static bool ValidGroupAlias(string value) => GroupAliasRegex().IsMatch(value ?? string.Empty);

    [GeneratedRegex("^contact-[a-f0-9]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex ContactAliasRegex();

    [GeneratedRegex("^group-[a-f0-9]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupAliasRegex();
}

public static class NamedLocationEligibilityPolicy
{
    public static readonly IReadOnlySet<string> AllowedTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Name", "NameCityCapital", "NameCity", "NameVillage", "CityCenter", "NameLocal",
        "Airport", "Strategic", "StrongpointArea", "Mount", "Hill", "ViewPoint", "NameMarine",
        "BorderCrossing", "HistoricalSite", "CulturalProperty", "CivilDefense"
    };

    public static bool IsAllowed(string type) => AllowedTypes.Contains(type ?? string.Empty);
}

public static class LegacyContactEligibilityPolicy
{
    public static bool IsEligible(WorldKnownContactState contact)
        => contact is not null && ContactEligibilityPolicy.IsSafeClass(contact.Class) &&
           (contact.KnownByPlayer || contact.KnownByGroup) && !contact.Ignored &&
           ContactEligibilityPolicy.PerceivedSides.Contains(contact.PerceivedSide) &&
           NormalizeRelationship(contact.Relationship).Length > 0 &&
           contact.TargetType is "person" or "Man" or "ground-vehicle" or "Car" or "Tank" or
               "air" or "Air" or "naval" or "Ship" or "static-weapon" or "StaticWeapon" or
               "unmanned-ground" or "UGV" or "unmanned-air" or "UAV";

    public static string NormalizeRelationship(string value) => value switch
    {
        "friendly" or "FRIENDLY" => "friendly",
        "hostile" or "HOSTILE" or "ENEMY" => "hostile",
        "neutral" or "NEUTRAL" => "neutral",
        "civilian" or "CIVILIAN" or "CIV" => "civilian",
        _ => string.Empty
    };
}
