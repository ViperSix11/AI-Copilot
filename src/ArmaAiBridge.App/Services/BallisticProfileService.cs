using System.Text.Json;
using System.Text.Json.Serialization;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class BallisticProfileStore
{
    public const int MaximumProfiles = 128;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly string _path;
    public BallisticProfileStore(string? path = null) => _path = path ?? AppPaths.BallisticProfilesFile;
    public string Path => _path;

    public BallisticProfileDocument Load()
    {
        if (!File.Exists(_path)) return new();
        string json = File.ReadAllText(_path);
        if (json.Length > 2_000_000) throw new InvalidDataException("Ballistic profile file is too large.");
        BallisticProfileDocument document = JsonSerializer.Deserialize<BallisticProfileDocument>(json, JsonOptions)
            ?? throw new InvalidDataException("Ballistic profile file is empty.");
        ValidateDocument(document);
        return document;
    }

    public void Save(BallisticProfileDocument document)
    {
        ValidateDocument(document);
        string? directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporary, _path, true);
    }

    public BallisticProfileDocument ImportPreview(string json)
    {
        if (json.Length > 2_000_000) throw new InvalidDataException("Ballistic profile import is too large.");
        BallisticProfileDocument document = JsonSerializer.Deserialize<BallisticProfileDocument>(json, JsonOptions)
            ?? throw new InvalidDataException("Ballistic profile import is empty.");
        ValidateDocument(document);
        return document;
    }

    public string Export(BallisticProfileDocument document)
    {
        ValidateDocument(document);
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    private static void ValidateDocument(BallisticProfileDocument document)
    {
        if (document.Schema != BallisticProfileDocument.CurrentSchema) throw new InvalidDataException("Unsupported ballistic profile schema.");
        if (document.Profiles.Count > MaximumProfiles) throw new InvalidDataException("Too many ballistic profiles.");
        if (document.Profiles.Any(profile => profile is null)) throw new InvalidDataException("A ballistic profile is null.");
        if (document.Profiles.Select(profile => profile.ProfileId).Distinct(StringComparer.Ordinal).Count() != document.Profiles.Count)
            throw new InvalidDataException("Duplicate ballistic profile ID.");
        foreach (UserBallisticProfile profile in document.Profiles)
        {
            if (!Guid.TryParse(profile.ProfileId, out _) || profile.DisplayName.Length is 0 or > 128 || profile.Notes.Length > 4096)
                throw new InvalidDataException("Invalid ballistic profile identity.");
            if (profile.MatchPriority is < -100000 or > 100000 || new[] { profile.WeaponClassMatch, profile.MuzzleClassMatch,
                    profile.MagazineClassMatch, profile.AmmunitionClassMatch, profile.WeaponDisplayNameMatch,
                    profile.WeaponDisplayName, profile.AmmunitionDisplayName }.Any(value => value.Length > 256))
                throw new InvalidDataException("Ballistic profile match or display field exceeds its bound.");
            if (profile.BallisticCoefficients.Count > 32 || profile.VelocityBoundariesMetersPerSecond.Count > 31 ||
                profile.TemperatureMuzzleVelocityShifts.Count > 64 || profile.BarrelLengthsMillimeters.Count > 64 ||
                profile.BarrelMuzzleVelocitiesMetersPerSecond.Count > 64)
                throw new InvalidDataException("Ballistic profile array exceeds its limit.");
            if (AllNumbers(profile).Any(value => !double.IsFinite(value))) throw new InvalidDataException("Ballistic profile contains a non-finite number.");
        }
    }

    private static IEnumerable<double> AllNumbers(UserBallisticProfile value)
    {
        foreach (double number in value.BallisticCoefficients.Concat(value.VelocityBoundariesMetersPerSecond)
                     .Concat(value.TemperatureMuzzleVelocityShifts).Concat(value.BarrelLengthsMillimeters)
                     .Concat(value.BarrelMuzzleVelocitiesMetersPerSecond)) yield return number;
        foreach (double? number in new[] { value.BarrelLengthMillimeters, value.BarrelTwistMillimetersPerTurn, value.SightHeightMillimeters,
                     value.DefaultZeroRangeMeters, value.ScopeBaseAngleMilliradians, value.MuzzleVelocityMultiplier,
                     value.BulletDiameterMillimeters, value.BulletLengthMillimeters, value.BulletMassGrams,
                     value.TransonicStabilityCoefficient, value.NominalMuzzleVelocityMetersPerSecond,
                     value.MuzzleVelocityVariationStandardDeviationPercent, value.GravityCoefficient, value.VanillaAirFriction,
                     value.ManualPressureHectopascals })
            if (number.HasValue) yield return number.Value;
        yield return value.MaximumSupportedRangeMeters;
    }
}

public static class BallisticProfileValidator
{
    private static readonly HashSet<string> DragModels = new(StringComparer.Ordinal) { "G1", "G2", "G5", "G6", "G7", "G8" };
    public static BallisticProfileValidation Validate(UserBallisticProfile profile)
    {
        List<string> errors = [], warnings = [];
        if (!DragModels.Contains(profile.DragModel)) errors.Add("Unsupported drag model.");
        if (profile.BallisticCoefficients.Count == 0) errors.Add("Missing ballistic coefficient.");
        if (profile.BallisticCoefficients.Count != profile.VelocityBoundariesMetersPerSecond.Count + 1)
            errors.Add("Coefficient count does not match velocity-boundary count.");
        if (profile.BallisticCoefficients.Any(value => value is <= 0 or > 10)) errors.Add("Ballistic coefficients must be positive and bounded.");
        if (!StrictlyOrdered(profile.VelocityBoundariesMetersPerSecond)) errors.Add("Velocity boundaries are not strictly ordered.");
        if (profile.NominalMuzzleVelocityMetersPerSecond is > 3000 or <= 0) errors.Add("Invalid muzzle velocity.");
        else if (!profile.NominalMuzzleVelocityMetersPerSecond.HasValue) warnings.Add("Muzzle velocity must be supplied by current Arma or ACE configuration.");
        if (profile.BulletMassGrams is null or <= 0 or > 500) errors.Add("Missing or invalid bullet mass.");
        if (profile.BulletDiameterMillimeters is null or <= 0 or > 50) errors.Add("Missing or invalid bullet diameter.");
        if (profile.SpinDriftEnabled && profile.BarrelTwistMillimetersPerTurn is null or <= 0) errors.Add("Missing barrel twist for spin drift.");
        if (profile.StandardAtmosphere is not ("ASM" or "ICAO")) errors.Add("Invalid standard atmosphere.");
        if (profile.ManualPressureHectopascals is < 300 or > 1100) errors.Add("Manual pressure is outside supported bounds.");
        if (profile.PreferredSolver == BallisticSolverPreference.VanillaArma && !profile.VanillaAirFriction.HasValue)
            errors.Add("Vanilla Arma mode requires an air-friction value.");
        if (profile.MaximumSupportedRangeMeters is < 25 or > 5000) errors.Add("Maximum range is outside supported bounds.");
        if (profile.BarrelLengthsMillimeters.Count != profile.BarrelMuzzleVelocitiesMetersPerSecond.Count)
            errors.Add("Barrel-length and muzzle-velocity tables differ in size.");
        if (!StrictlyOrdered(profile.BarrelLengthsMillimeters) || profile.BarrelMuzzleVelocitiesMetersPerSecond.Any(value => value <= 0))
            errors.Add("Barrel-length table must be strictly ordered with positive velocities.");
        if (!profile.Enabled) warnings.Add("Profile is disabled.");
        return new(errors.Count == 0, errors, warnings);
    }

    private static bool StrictlyOrdered(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return true;
        bool ascending = values[1] > values[0];
        return values.Zip(values.Skip(1)).All(pair => ascending ? pair.Second > pair.First : pair.Second < pair.First);
    }
}

public sealed class BallisticProfileManager
{
    private readonly BallisticProfileStore _store;
    private BallisticProfileDocument _document;
    private string? _forcedProfileId;
    public BallisticProfileManager(BallisticProfileStore? store = null)
    {
        _store = store ?? new BallisticProfileStore();
        try { _document = _store.Load(); } catch { _document = new(); }
    }
    public IReadOnlyList<UserBallisticProfile> Profiles => _document.Profiles;
    public string StoragePath => _store.Path;
    public string? ForcedProfileId => _forcedProfileId;
    public void Save() => _store.Save(_document);
    public void Add(UserBallisticProfile profile) { _document.Profiles.Add(profile); Save(); }
    public void Remove(UserBallisticProfile profile) { _document.Profiles.Remove(profile); Save(); }
    public void Replace(BallisticProfileDocument document) { _document = document; _forcedProfileId = null; Save(); }
    public string Export() => _store.Export(_document);
    public BallisticProfileDocument PreviewImport(string json) => _store.ImportPreview(json);
    public void ForceTemporary(string? profileId) => _forcedProfileId = profileId;

    public BallisticProfileMatch Match(StateBallisticProfile? game)
    {
        if (_forcedProfileId is not null)
        {
            UserBallisticProfile? forced = _document.Profiles.FirstOrDefault(item => item.ProfileId == _forcedProfileId && item.Enabled);
            if (forced is null || !BallisticProfileValidator.Validate(forced).IsValid) return new(null, "invalid_forced_ballistic_profile", 0, true);
            return new(forced, "temporarily_forced", int.MaxValue, true);
        }
        if (game is null) return new(null, "missing_ballistic_config", 0, false);
        var candidates = _document.Profiles.Where(item => item.Enabled && Matches(item, game))
            .Select(item => new { Profile = item, Specificity = Specificity(item) })
            .OrderByDescending(item => item.Profile.MatchPriority).ThenByDescending(item => item.Specificity).ToArray();
        if (candidates.Length == 0) return new(null, "no_manual_ballistic_profile_match", 0, false);
        var first = candidates[0];
        if (candidates.Skip(1).Any(item => item.Profile.MatchPriority == first.Profile.MatchPriority && item.Specificity == first.Specificity))
            return new(null, "ambiguous_manual_ballistic_profile", first.Specificity, false);
        if (!BallisticProfileValidator.Validate(first.Profile).IsValid) return new(null, "matched_manual_ballistic_profile_invalid", first.Specificity, false);
        return new(first.Profile, "automatic_exact_class_match", first.Specificity, false);
    }

    public ResolvedBallisticProfile? Resolve(StateBallisticProfile? game, out string reason)
    {
        BallisticProfileMatch match = Match(game);
        if (match.Profile is null) { reason = match.Reason; return null; }
        UserBallisticProfile manual = match.Profile;
        Dictionary<string, string> provenance = new(StringComparer.Ordinal)
        {
            ["muzzleVelocity"] = manual.NominalMuzzleVelocityMetersPerSecond.HasValue ? "manual" : game?.AceAdvancedBallisticsEnabled == true ? "ace-config" : "arma-config",
            ["ballisticCoefficient"] = "manual", ["dragModel"] = "manual", ["bulletMass"] = "manual",
            ["bulletDiameter"] = "manual", ["wind"] = "live-game-state", ["zeroRange"] = "current-arma-state",
            ["temperatureCorrectionSetting"] = game?.AceTemperatureCorrectionEnabled == true ? "ace-config" : "manual",
            ["barrelLengthCorrectionSetting"] = game?.AceBarrelLengthCorrectionEnabled == true ? "ace-config" : "manual"
        };
        double velocity = manual.NominalMuzzleVelocityMetersPerSecond ?? game?.InitialSpeedMetersPerSecond ?? 0;
        if (velocity <= 0) { reason = "missing_muzzle_velocity"; return null; }
        if (manual.MuzzleVelocityMultiplier.HasValue) velocity *= manual.MuzzleVelocityMultiplier.Value;
        if (manual.PreferredSolver == BallisticSolverPreference.VanillaArma && game?.AceAdvancedBallisticsEnabled == true)
        { reason = "manual_vanilla_forbidden_while_ace_active"; return null; }
        string model = manual.PreferredSolver switch
        {
            BallisticSolverPreference.AceCompatibleAdvanced => "ace-compatible-resolved-profile",
            BallisticSolverPreference.VanillaArma => "arma-vanilla-config",
            BallisticSolverPreference.SimplifiedCoefficient => "manual-coefficient-profile",
            _ => game?.AceAdvancedBallisticsEnabled == true ? "ace-compatible-resolved-profile" : "manual-coefficient-profile"
        };
        reason = match.Reason;
        return new(manual, model, velocity, SelectCoefficient(manual, velocity), manual.DragModel,
            manual.BulletMassGrams!.Value, manual.BulletDiameterMillimeters!.Value,
            manual.BarrelTwistMillimetersPerTurn, manual.TwistDirection, manual.MaximumSupportedRangeMeters,
            manual.WindCorrectionEnabled, provenance);
    }

    public object CompactCapability(StateBallisticProfile? game)
    {
        ResolvedBallisticProfile? resolved = Resolve(game, out string reason);
        if (resolved is null)
        {
            Dictionary<string, object?> compact = new(StringComparer.Ordinal)
            {
                ["available"] = game?.Available == true,
                ["model"] = game?.Model ?? "unavailable",
                ["currentWeaponMatched"] = game?.Available == true,
                ["windCorrectionAvailable"] = game?.AceAdvancedBallisticsEnabled == true && game.Available
            };
            if (game?.Available != true) compact["reason"] = string.IsNullOrWhiteSpace(game?.Reason) ? reason : game.Reason;
            return compact;
        }
        return new { available = true, model = resolved.Model, profileName = resolved.ManualProfile.DisplayName, currentWeaponMatched = true, windCorrectionAvailable = resolved.WindCorrectionEnabled };
    }

    private static bool Matches(UserBallisticProfile profile, StateBallisticProfile game)
        => Field(profile.WeaponClassMatch, game.WeaponClass) && Field(profile.MuzzleClassMatch, game.MuzzleClass) &&
           Field(profile.MagazineClassMatch, game.MagazineClass) && Field(profile.AmmunitionClassMatch, game.AmmunitionClass) &&
           Field(profile.WeaponDisplayNameMatch, game.WeaponDisplayName);
    private static bool Field(string expected, string actual) => expected.Length == 0 || string.Equals(expected, actual, StringComparison.Ordinal);
    private static int Specificity(UserBallisticProfile profile) => new[] { profile.WeaponClassMatch, profile.MuzzleClassMatch, profile.MagazineClassMatch, profile.AmmunitionClassMatch, profile.WeaponDisplayNameMatch }.Count(value => value.Length > 0);
    private static double SelectCoefficient(UserBallisticProfile profile, double velocity)
    {
        for (int index = 0; index < profile.VelocityBoundariesMetersPerSecond.Count; index++)
        {
            double boundary = profile.VelocityBoundariesMetersPerSecond[index];
            bool descending = profile.VelocityBoundariesMetersPerSecond.Count < 2 ||
                              profile.VelocityBoundariesMetersPerSecond[1] < profile.VelocityBoundariesMetersPerSecond[0];
            if (descending ? velocity >= boundary : velocity <= boundary) return profile.BallisticCoefficients[index];
        }
        return profile.BallisticCoefficients[^1];
    }
}
