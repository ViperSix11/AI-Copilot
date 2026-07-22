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
        if (profile.AdvancedBallisticsDetected)
            throw new InvalidOperationException("advanced_ballistics_mod_detected");
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

    public BallisticToolService(
        Func<double, double, CancellationToken, Task<double>> terrainHeight,
        VanillaBallisticSolver? solver = null)
    {
        _terrainHeight = terrainHeight ?? throw new ArgumentNullException(nameof(terrainHeight));
        _solver = solver ?? new VanillaBallisticSolver();
    }

    public async Task<string> CalculateAsync(
        JsonElement arguments,
        StateBallisticProfile? profile,
        CancellationToken cancellationToken)
    {
        if (profile is null) return Error("missing_ballistic_config");
        if (!profile.Available) return Error(string.IsNullOrWhiteSpace(profile.Reason) ? "missing_ballistic_config" : profile.Reason);
        BallisticSolutionRequest request = VanillaBallisticSolver.ParseRequest(arguments);
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

    private static string Error(string reason) => JsonSerializer.Serialize(new
    {
        firingSolution = new { available = false, reason }
    });
}
