using System.Diagnostics;
using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed record BallisticSolutionRequest(
    double RangeMeters,
    double BearingDegrees,
    double? TargetElevationAslMeters,
    double? TargetHeightAboveTerrainMeters);

public sealed record BallisticSolution(
    string Model,
    double RangeMeters,
    double BearingDegrees,
    double ShooterElevationAslMeters,
    double TargetElevationAslMeters,
    double HeightDifferenceMeters,
    double CurrentZeroingMeters,
    double RequiredElevationAngleDegrees,
    double CurrentZeroElevationAngleDegrees,
    double ElevationCorrectionDegrees,
    double ElevationCorrectionMilliradians,
    string HoldDirection,
    double TimeOfFlightSeconds,
    double PredictedImpactVelocityMetersPerSecond,
    bool TerrainPointAssumed,
    bool WindCorrectionAvailable);

public sealed class VanillaBallisticSolver
{
    private const double Gravity = 9.80665;
    private const double StepSeconds = 0.0025;
    private const double MaximumFlightSeconds = 30;
    private const int BisectionIterations = 48;

    public BallisticSolution Solve(
        StateBallisticProfile profile,
        BallisticSolutionRequest request,
        double targetElevationAslMeters,
        bool terrainPointAssumed)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateProfile(profile);
        ValidateRequest(request);
        if (!double.IsFinite(targetElevationAslMeters) || targetElevationAslMeters is < -1000 or > 10000)
            throw new InvalidOperationException("Target elevation is outside the supported Arma range.");

        double heightDifference = targetElevationAslMeters - profile.ShooterPositionAsl.Z;
        Trajectory required = SolveLowAngle(profile, request.RangeMeters, heightDifference);
        Trajectory zero = profile.CurrentZeroingMeters >= 25
            ? SolveLowAngle(profile, profile.CurrentZeroingMeters, 0)
            : new Trajectory(0, 0, profile.InitialSpeedMetersPerSecond);
        double correctionRadians = required.AngleRadians - zero.AngleRadians;
        double correctionMils = correctionRadians * 1000;
        string hold = Math.Abs(correctionMils) < 0.1 ? "no material correction" : correctionMils > 0 ? "high" : "low";

        return new BallisticSolution(
            "arma-vanilla-config",
            Round(request.RangeMeters, 1),
            Round(NormalizeBearing(request.BearingDegrees), 3),
            Round(profile.ShooterPositionAsl.Z, 2),
            Round(targetElevationAslMeters, 2),
            Round(heightDifference, 2),
            Round(profile.CurrentZeroingMeters, 1),
            Round(RadiansToDegrees(required.AngleRadians), 4),
            Round(RadiansToDegrees(zero.AngleRadians), 4),
            Round(RadiansToDegrees(correctionRadians), 4),
            Round(correctionMils, 3),
            hold,
            Round(required.FlightSeconds, 3),
            Round(required.ImpactSpeed, 2),
            terrainPointAssumed,
            false);
    }

    public static BallisticSolutionRequest ParseRequest(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Ballistic tool arguments must be an object.");
        HashSet<string> allowed = new(StringComparer.Ordinal)
        { "rangeMeters", "bearingDegrees", "targetElevationAslMeters", "targetHeightAboveTerrainMeters" };
        if (arguments.EnumerateObject().Any(item => !allowed.Contains(item.Name)))
            throw new InvalidOperationException("Unsupported ballistic tool argument.");
        double range = RequiredFinite(arguments, "rangeMeters");
        double bearing = RequiredFinite(arguments, "bearingDegrees");
        double? elevation = OptionalFinite(arguments, "targetElevationAslMeters", -1000, 10000);
        double? height = OptionalFinite(arguments, "targetHeightAboveTerrainMeters", -100, 1000);
        if (elevation.HasValue && height.HasValue)
            throw new InvalidOperationException("Specify target elevation or target height above terrain, not both.");
        BallisticSolutionRequest request = new(range, NormalizeBearing(bearing), elevation, height);
        ValidateRequest(request);
        return request;
    }

    public static (double X, double Y) TargetPoint(WorldPosition shooter, double rangeMeters, double bearingDegrees)
    {
        double radians = NormalizeBearing(bearingDegrees) * Math.PI / 180;
        return (shooter.X + Math.Sin(radians) * rangeMeters, shooter.Y + Math.Cos(radians) * rangeMeters);
    }

    private static void ValidateProfile(StateBallisticProfile profile)
    {
        if (!profile.Available)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(profile.Reason) ? "missing_ballistic_config" : profile.Reason);
        if (profile.AceAdvancedBallisticsEnabled)
            throw new InvalidOperationException("ace_advanced_ballistics_interface_unsupported");
        if (profile.SupportedProjectileType is not ("bullet" or "shell") ||
            profile.InitialSpeedMetersPerSecond <= 0 || profile.AirFriction > 0 ||
            profile.GravityCoefficient <= 0 || !double.IsFinite(profile.InitialSpeedMetersPerSecond) ||
            !double.IsFinite(profile.AirFriction) || !double.IsFinite(profile.GravityCoefficient))
            throw new InvalidOperationException("missing_ballistic_config");
    }

    private static void ValidateRequest(BallisticSolutionRequest request)
    {
        if (!double.IsFinite(request.RangeMeters) || request.RangeMeters is < 25 or > 5000)
            throw new InvalidOperationException("rangeMeters must be between 25 and 5000.");
        if (!double.IsFinite(request.BearingDegrees))
            throw new InvalidOperationException("bearingDegrees must be finite.");
    }

    private static Trajectory SolveLowAngle(StateBallisticProfile profile, double range, double height)
    {
        double lower = DegreesToRadians(-20);
        Sample? previous = null;
        for (double degrees = -20; degrees <= 75; degrees += 0.5)
        {
            double angle = DegreesToRadians(degrees);
            Sample sample = Simulate(profile, angle, range, height);
            if (!sample.Reached) continue;
            if (Math.Abs(sample.Residual) < 1e-8) return sample.Trajectory;
            if (previous is not null && Math.Sign(previous.Value.Residual) != Math.Sign(sample.Residual))
            {
                lower = previous.Value.Trajectory.AngleRadians;
                double upper = angle;
                Sample best = sample;
                for (int index = 0; index < BisectionIterations; index++)
                {
                    double midpoint = (lower + upper) / 2;
                    Sample middle = Simulate(profile, midpoint, range, height);
                    if (!middle.Reached) { upper = midpoint; continue; }
                    best = middle;
                    if (Math.Sign(previous.Value.Residual) == Math.Sign(middle.Residual)) lower = midpoint;
                    else upper = midpoint;
                }
                return best.Trajectory;
            }
            previous = sample;
        }
        throw new InvalidOperationException("The requested low-angle trajectory does not converge within bounded solver limits.");
    }

    private static Sample Simulate(StateBallisticProfile profile, double angle, double range, double targetHeight)
    {
        double x = 0, z = 0, time = 0;
        double vx = profile.InitialSpeedMetersPerSecond * Math.Cos(angle);
        double vz = profile.InitialSpeedMetersPerSecond * Math.Sin(angle);
        double previousX = x, previousZ = z, previousTime = time, previousVx = vx, previousVz = vz;
        int maximumSteps = (int)(MaximumFlightSeconds / StepSeconds);
        for (int index = 0; index < maximumSteps; index++)
        {
            previousX = x; previousZ = z; previousTime = time; previousVx = vx; previousVz = vz;
            IntegrateRk4(ref x, ref z, ref vx, ref vz, profile.AirFriction, profile.GravityCoefficient, StepSeconds);
            time += StepSeconds;
            if (!double.IsFinite(x) || !double.IsFinite(z) || vx <= 0) break;
            if (x >= range)
            {
                double fraction = (range - previousX) / (x - previousX);
                double impactZ = previousZ + (z - previousZ) * fraction;
                double impactVx = previousVx + (vx - previousVx) * fraction;
                double impactVz = previousVz + (vz - previousVz) * fraction;
                double impactTime = previousTime + StepSeconds * fraction;
                return new Sample(true, impactZ - targetHeight,
                    new Trajectory(angle, impactTime, Math.Sqrt(impactVx * impactVx + impactVz * impactVz)));
            }
        }
        return new Sample(false, double.NaN, new Trajectory(angle, time, 0));
    }

    private static void IntegrateRk4(ref double x, ref double z, ref double vx, ref double vz, double friction, double gravityCoefficient, double dt)
    {
        Derivative k1 = Derive(vx, vz, friction, gravityCoefficient);
        Derivative k2 = Derive(vx + k1.Ax * dt / 2, vz + k1.Az * dt / 2, friction, gravityCoefficient);
        Derivative k3 = Derive(vx + k2.Ax * dt / 2, vz + k2.Az * dt / 2, friction, gravityCoefficient);
        Derivative k4 = Derive(vx + k3.Ax * dt, vz + k3.Az * dt, friction, gravityCoefficient);
        x += dt / 6 * (k1.Vx + 2 * k2.Vx + 2 * k3.Vx + k4.Vx);
        z += dt / 6 * (k1.Vz + 2 * k2.Vz + 2 * k3.Vz + k4.Vz);
        vx += dt / 6 * (k1.Ax + 2 * k2.Ax + 2 * k3.Ax + k4.Ax);
        vz += dt / 6 * (k1.Az + 2 * k2.Az + 2 * k3.Az + k4.Az);
    }

    private static Derivative Derive(double vx, double vz, double friction, double gravityCoefficient)
    {
        double speed = Math.Sqrt(vx * vx + vz * vz);
        return new Derivative(vx, vz, vx * speed * friction, vz * speed * friction - Gravity * gravityCoefficient);
    }

    private static double RequiredFinite(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
           value.TryGetDouble(out double number) && double.IsFinite(number)
            ? number : throw new InvalidOperationException($"{name} must be a finite number.");

    private static double? OptionalFinite(JsonElement root, string name, double min, double max)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        double number = RequiredFinite(root, name);
        if (number < min || number > max) throw new InvalidOperationException($"{name} is outside supported bounds.");
        return number;
    }

    private static double NormalizeBearing(double value) => ((value % 360) + 360) % 360;
    private static double DegreesToRadians(double value) => value * Math.PI / 180;
    private static double RadiansToDegrees(double value) => value * 180 / Math.PI;
    private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.AwayFromZero);
    private readonly record struct Derivative(double Vx, double Vz, double Ax, double Az);
    private readonly record struct Trajectory(double AngleRadians, double FlightSeconds, double ImpactSpeed);
    private readonly record struct Sample(bool Reached, double Residual, Trajectory Trajectory);
}

public sealed class BallisticToolService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly VanillaBallisticSolver _solver;
    private readonly Func<double, double, CancellationToken, Task<double>> _terrainHeight;
    private readonly IAceBallisticAdapter? _aceAdapter;
    private readonly BallisticProfileManager? _profiles;
    private readonly Func<FrozenBallisticEnvironment?>? _environment;

    public BallisticToolService(
        Func<double, double, CancellationToken, Task<double>> terrainHeight,
        VanillaBallisticSolver? solver = null,
        IAceBallisticAdapter? aceAdapter = null,
        BallisticProfileManager? profiles = null,
        Func<FrozenBallisticEnvironment?>? environment = null)
    {
        _terrainHeight = terrainHeight ?? throw new ArgumentNullException(nameof(terrainHeight));
        _solver = solver ?? new VanillaBallisticSolver();
        _aceAdapter = aceAdapter;
        _profiles = profiles;
        _environment = environment;
    }

    public async Task<string> CalculateAsync(
        JsonElement arguments,
        StateBallisticProfile? profile,
        CancellationToken cancellationToken)
    {
        BallisticSolutionRequest request = VanillaBallisticSolver.ParseRequest(arguments);
        string resolutionReason = string.Empty;
        ResolvedBallisticProfile? resolved = _profiles?.Resolve(profile, out resolutionReason);
        if (resolved is not null && profile is not null)
            return await CalculateManualAsync(request, profile, resolved, cancellationToken).ConfigureAwait(false);
        if (profile is null) return Error("missing_ballistic_config");
        if (!profile.Available)
            return Error(resolutionReason is "matched_manual_ballistic_profile_invalid" or "ambiguous_manual_ballistic_profile" or
                         "invalid_forced_ballistic_profile" or "missing_muzzle_velocity" or "manual_vanilla_forbidden_while_ace_active"
                ? resolutionReason
                : string.IsNullOrWhiteSpace(profile.Reason) ? "missing_ballistic_config" : profile.Reason);
        if (profile.AceAdvancedBallisticsEnabled)
        {
            if (!profile.AceAdapterAvailable) return Error("ace_advanced_ballistics_interface_unsupported");
            if (!profile.AceProfileSupported) return Error("ace_ballistic_profile_incomplete");
            if (_aceAdapter is null) return Error("ace_advanced_ballistics_interface_unsupported");
            return await _aceAdapter.CalculateAsync(arguments, profile, cancellationToken).ConfigureAwait(false);
        }
        (double x, double y) = VanillaBallisticSolver.TargetPoint(profile.ShooterPositionAsl, request.RangeMeters, request.BearingDegrees);
        bool terrainPointAssumed = !request.TargetElevationAslMeters.HasValue;
        double terrain = terrainPointAssumed
            ? await _terrainHeight(x, y, cancellationToken).ConfigureAwait(false)
            : request.TargetElevationAslMeters!.Value;
        double target = request.TargetElevationAslMeters ?? terrain + (request.TargetHeightAboveTerrainMeters ?? 0);
        BallisticSolution solution = _solver.Solve(profile, request, target, terrainPointAssumed);
        return JsonSerializer.Serialize(new
        {
            firingSolution = solution,
            projectile = new
            {
                weapon = profile.WeaponDisplayName,
                magazine = profile.MagazineDisplayName,
                ammunition = profile.AmmunitionDisplayName,
                projectileType = profile.SupportedProjectileType
            }
        }, JsonOptions);
    }

    private async Task<string> CalculateManualAsync(
        BallisticSolutionRequest request,
        StateBallisticProfile game,
        ResolvedBallisticProfile resolved,
        CancellationToken cancellationToken)
    {
        if (request.RangeMeters > resolved.MaximumRangeMeters)
            return Error("range_exceeds_profile_maximum");
        FrozenBallisticEnvironment frozen = _environment?.Invoke() ?? new(0, 0, 15, 0.5, game.ShooterPositionAsl.Z);
        double dragScale = resolved.DragModel switch { "G1" => 1.25, "G2" => 1.1, "G5" => 1.02, "G6" => 0.98, "G7" => 0.9, "G8" => 0.95, _ => 1 };
        double airFriction = resolved.ManualProfile.PreferredSolver == BallisticSolverPreference.VanillaArma
            ? resolved.ManualProfile.VanillaAirFriction!.Value
            : -0.00004 * dragScale / resolved.BallisticCoefficient;
        airFriction *= AirDensityRatio(resolved.ManualProfile, frozen);
        double muzzleVelocity = ApplyTemperatureCorrection(resolved,
            ApplyBarrelCorrection(resolved), frozen.TemperatureCelsius);
        StateBallisticProfile local = game with
        {
            Available = true,
            Reason = string.Empty,
            Model = resolved.Model,
            InitialSpeedMetersPerSecond = muzzleVelocity,
            TypicalSpeedMetersPerSecond = muzzleVelocity,
            AirFriction = airFriction,
            GravityCoefficient = resolved.ManualProfile.GravityCoefficient ?? 1,
            AceAdvancedBallisticsEnabled = false
        };
        (double x, double y) = VanillaBallisticSolver.TargetPoint(local.ShooterPositionAsl, request.RangeMeters, request.BearingDegrees);
        bool terrainPointAssumed = !request.TargetElevationAslMeters.HasValue;
        double terrain = terrainPointAssumed
            ? await _terrainHeight(x, y, cancellationToken).ConfigureAwait(false)
            : request.TargetElevationAslMeters!.Value;
        double target = request.TargetElevationAslMeters ?? terrain + (request.TargetHeightAboveTerrainMeters ?? 0);
        BallisticSolution solution = _solver.Solve(local, request, target, terrainPointAssumed);
        double bearing = request.BearingDegrees * Math.PI / 180;
        double crosswind = frozen.WindX * Math.Cos(bearing) - frozen.WindY * Math.Sin(bearing);
        double horizontal = resolved.WindCorrectionEnabled
            ? -crosswind * solution.TimeOfFlightSeconds * 0.65 / request.RangeMeters * 1000 : 0;
        string horizontalDirection = Math.Abs(horizontal) < 0.05 ? "no material correction" : horizontal > 0 ? "right" : "left";
        return JsonSerializer.Serialize(new
        {
            firingSolution = new
            {
                available = true,
                model = resolved.Model,
                profileName = resolved.ManualProfile.DisplayName,
                rangeMeters = solution.RangeMeters,
                bearingDegrees = solution.BearingDegrees,
                currentZeroingMeters = solution.CurrentZeroingMeters,
                heightDifferenceMeters = solution.HeightDifferenceMeters,
                verticalCorrectionMilliradians = solution.ElevationCorrectionMilliradians,
                verticalHoldDirection = solution.HoldDirection,
                horizontalCorrectionMilliradians = Math.Round(Math.Abs(horizontal), 3, MidpointRounding.AwayFromZero),
                horizontalHoldDirection = horizontalDirection,
                timeOfFlightSeconds = solution.TimeOfFlightSeconds,
                impactVelocityMetersPerSecond = solution.PredictedImpactVelocityMetersPerSecond,
                nominalMuzzleVelocityMetersPerSecond = muzzleVelocity,
                nominalSolution = true,
                windCorrectionAvailable = resolved.WindCorrectionEnabled
            }
        }, JsonOptions);
    }

    private static double ApplyTemperatureCorrection(ResolvedBallisticProfile resolved, double baseVelocity, double temperature)
    {
        UserBallisticProfile profile = resolved.ManualProfile;
        if (!profile.TemperatureCorrectionEnabled || profile.TemperatureMuzzleVelocityShifts.Count == 0)
            return baseVelocity;
        double normalizedIndex = Math.Clamp((temperature + 15) / 5, 0, profile.TemperatureMuzzleVelocityShifts.Count - 1);
        int lower = (int)Math.Floor(normalizedIndex), upper = Math.Min(lower + 1, profile.TemperatureMuzzleVelocityShifts.Count - 1);
        double fraction = normalizedIndex - lower;
        double shift = profile.TemperatureMuzzleVelocityShifts[lower] * (1 - fraction) + profile.TemperatureMuzzleVelocityShifts[upper] * fraction;
        return baseVelocity + shift;
    }

    private static double ApplyBarrelCorrection(ResolvedBallisticProfile resolved)
    {
        UserBallisticProfile profile = resolved.ManualProfile;
        if (!profile.BarrelLengthCorrectionEnabled || !profile.BarrelLengthMillimeters.HasValue ||
            profile.BarrelLengthsMillimeters.Count == 0 ||
            profile.BarrelLengthsMillimeters.Count != profile.BarrelMuzzleVelocitiesMetersPerSecond.Count)
            return resolved.MuzzleVelocityMetersPerSecond;
        double barrel = profile.BarrelLengthMillimeters.Value;
        int upper = profile.BarrelLengthsMillimeters.FindIndex(value => value >= barrel);
        if (upper <= 0) return profile.BarrelMuzzleVelocitiesMetersPerSecond[upper < 0 ? ^1 : 0];
        double x0 = profile.BarrelLengthsMillimeters[upper - 1], x1 = profile.BarrelLengthsMillimeters[upper];
        double y0 = profile.BarrelMuzzleVelocitiesMetersPerSecond[upper - 1], y1 = profile.BarrelMuzzleVelocitiesMetersPerSecond[upper];
        return y0 + (y1 - y0) * (barrel - x0) / (x1 - x0);
    }

    private static double AirDensityRatio(UserBallisticProfile profile, FrozenBallisticEnvironment environment)
    {
        double altitude = Math.Clamp(environment.ShooterElevationAslMeters, -500, 10000);
        double pressurePa = environment.PressureHectopascals.HasValue || profile.ManualPressureHectopascals.HasValue
            ? (environment.PressureHectopascals ?? profile.ManualPressureHectopascals!.Value) * 100
            : 101325 * Math.Pow(Math.Max(0.01, 1 - 2.25577e-5 * altitude), 5.25588);
        double celsius = Math.Clamp(environment.TemperatureCelsius, -80, 60);
        double temperatureKelvin = celsius + 273.15;
        double saturationVaporPressure = 610.94 * Math.Exp(17.625 * celsius / (celsius + 243.04));
        double vaporPressure = Math.Clamp(environment.Humidity, 0, 1) * saturationVaporPressure;
        double density = (pressurePa - vaporPressure) / (287.05 * temperatureKelvin) + vaporPressure / (461.495 * temperatureKelvin);
        return Math.Clamp(density / 1.225, 0.3, 1.5);
    }

    private static string Error(string reason) => JsonSerializer.Serialize(new
    {
        firingSolution = new { available = false, reason }
    });
}
