using System.Text.Json.Serialization;

namespace ArmaAiBridge.App.Models;

public enum BallisticSolverPreference { Automatic, AceCompatibleAdvanced, SimplifiedCoefficient, VanillaArma }

public enum BallisticTwistDirection { None, Right, Left }

public sealed class BallisticProfileDocument
{
    public const string CurrentSchema = "arma-ai-bridge/ballistic-profiles-v1";
    public string Schema { get; set; } = CurrentSchema;
    public List<UserBallisticProfile> Profiles { get; set; } = [];
}

public sealed class UserBallisticProfile
{
    public string ProfileId { get; set; } = Guid.NewGuid().ToString("D");
    public string DisplayName { get; set; } = "New ballistic profile";
    public bool Enabled { get; set; } = true;
    public string Notes { get; set; } = string.Empty;
    public string WeaponClassMatch { get; set; } = string.Empty;
    public string MuzzleClassMatch { get; set; } = string.Empty;
    public string MagazineClassMatch { get; set; } = string.Empty;
    public string AmmunitionClassMatch { get; set; } = string.Empty;
    public string WeaponDisplayNameMatch { get; set; } = string.Empty;
    public int MatchPriority { get; set; }
    public string WeaponDisplayName { get; set; } = string.Empty;
    public double? BarrelLengthMillimeters { get; set; }
    public double? BarrelTwistMillimetersPerTurn { get; set; }
    public BallisticTwistDirection TwistDirection { get; set; }
    public double? SightHeightMillimeters { get; set; }
    public double? DefaultZeroRangeMeters { get; set; }
    public double? ScopeBaseAngleMilliradians { get; set; }
    public double? MuzzleVelocityMultiplier { get; set; }
    public string AmmunitionDisplayName { get; set; } = string.Empty;
    public double? BulletDiameterMillimeters { get; set; }
    public double? BulletLengthMillimeters { get; set; }
    public double? BulletMassGrams { get; set; }
    public string DragModel { get; set; } = "G7";
    public List<double> BallisticCoefficients { get; set; } = [];
    public List<double> VelocityBoundariesMetersPerSecond { get; set; } = [];
    public string StandardAtmosphere { get; set; } = "ICAO";
    public double? TransonicStabilityCoefficient { get; set; }
    public double? NominalMuzzleVelocityMetersPerSecond { get; set; }
    public double? MuzzleVelocityVariationStandardDeviationPercent { get; set; }
    public List<double> TemperatureMuzzleVelocityShifts { get; set; } = [];
    public List<double> BarrelLengthsMillimeters { get; set; } = [];
    public List<double> BarrelMuzzleVelocitiesMetersPerSecond { get; set; } = [];
    public double? GravityCoefficient { get; set; }
    public double? VanillaAirFriction { get; set; }
    public double? ManualPressureHectopascals { get; set; }
    public BallisticSolverPreference PreferredSolver { get; set; }
    public bool WindCorrectionEnabled { get; set; } = true;
    public bool SpinDriftEnabled { get; set; }
    public bool CoriolisEnabled { get; set; }
    public bool TransonicModelingEnabled { get; set; } = true;
    public bool TemperatureCorrectionEnabled { get; set; } = true;
    public bool BarrelLengthCorrectionEnabled { get; set; } = true;
    public bool NominalDeterministicMuzzleVelocity { get; set; } = true;
    public double MaximumSupportedRangeMeters { get; set; } = 2000;

    public UserBallisticProfile CloneAsNew() => new()
    {
        ProfileId = Guid.NewGuid().ToString("D"), DisplayName = DisplayName + " copy", Enabled = Enabled, Notes = Notes,
        WeaponClassMatch = WeaponClassMatch, MuzzleClassMatch = MuzzleClassMatch, MagazineClassMatch = MagazineClassMatch,
        AmmunitionClassMatch = AmmunitionClassMatch, WeaponDisplayNameMatch = WeaponDisplayNameMatch, MatchPriority = MatchPriority,
        WeaponDisplayName = WeaponDisplayName, BarrelLengthMillimeters = BarrelLengthMillimeters,
        BarrelTwistMillimetersPerTurn = BarrelTwistMillimetersPerTurn, TwistDirection = TwistDirection,
        SightHeightMillimeters = SightHeightMillimeters, DefaultZeroRangeMeters = DefaultZeroRangeMeters,
        ScopeBaseAngleMilliradians = ScopeBaseAngleMilliradians, MuzzleVelocityMultiplier = MuzzleVelocityMultiplier,
        AmmunitionDisplayName = AmmunitionDisplayName, BulletDiameterMillimeters = BulletDiameterMillimeters,
        BulletLengthMillimeters = BulletLengthMillimeters, BulletMassGrams = BulletMassGrams, DragModel = DragModel,
        BallisticCoefficients = [.. BallisticCoefficients], VelocityBoundariesMetersPerSecond = [.. VelocityBoundariesMetersPerSecond],
        StandardAtmosphere = StandardAtmosphere, TransonicStabilityCoefficient = TransonicStabilityCoefficient,
        NominalMuzzleVelocityMetersPerSecond = NominalMuzzleVelocityMetersPerSecond,
        MuzzleVelocityVariationStandardDeviationPercent = MuzzleVelocityVariationStandardDeviationPercent,
        TemperatureMuzzleVelocityShifts = [.. TemperatureMuzzleVelocityShifts], BarrelLengthsMillimeters = [.. BarrelLengthsMillimeters],
        BarrelMuzzleVelocitiesMetersPerSecond = [.. BarrelMuzzleVelocitiesMetersPerSecond], GravityCoefficient = GravityCoefficient,
        VanillaAirFriction = VanillaAirFriction, ManualPressureHectopascals = ManualPressureHectopascals,
        PreferredSolver = PreferredSolver, WindCorrectionEnabled = WindCorrectionEnabled,
        SpinDriftEnabled = SpinDriftEnabled, CoriolisEnabled = CoriolisEnabled, TransonicModelingEnabled = TransonicModelingEnabled,
        TemperatureCorrectionEnabled = TemperatureCorrectionEnabled, BarrelLengthCorrectionEnabled = BarrelLengthCorrectionEnabled,
        NominalDeterministicMuzzleVelocity = NominalDeterministicMuzzleVelocity, MaximumSupportedRangeMeters = MaximumSupportedRangeMeters
    };
}

public sealed record BallisticProfileValidation(bool IsValid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);
public sealed record BallisticProfileMatch(UserBallisticProfile? Profile, string Reason, int Specificity, bool Forced);
public sealed record ResolvedBallisticProfile(
    UserBallisticProfile ManualProfile,
    string Model,
    double MuzzleVelocityMetersPerSecond,
    double BallisticCoefficient,
    string DragModel,
    double BulletMassGrams,
    double BulletDiameterMillimeters,
    double? BarrelTwistMillimeters,
    BallisticTwistDirection TwistDirection,
    double MaximumRangeMeters,
    bool WindCorrectionEnabled,
    IReadOnlyDictionary<string, string> Provenance);

public sealed record FrozenBallisticEnvironment(
    double WindX, double WindY, double TemperatureCelsius, double Humidity, double ShooterElevationAslMeters,
    double? PressureHectopascals = null);
