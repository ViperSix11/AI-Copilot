using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class EnvironmentInterpretationService
{
    private static readonly string[] Directions = { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };

    public EnvironmentInterpretation Interpret(StateEnvironment environment, StateTimeAstronomy? time)
    {
        double speed = Math.Sqrt(environment.WindX * environment.WindX + environment.WindY * environment.WindY);
        double exact = Math.Atan2(environment.WindX, environment.WindY) * 180 / Math.PI;
        if (exact < 0) exact += 360;
        int bearing = (int)Math.Round(exact, MidpointRounding.AwayFromZero) % 360;
        int sector = (int)Math.Floor((bearing + 22.5) / 45) % 8;
        return new EnvironmentInterpretation(
            Math.Round(speed, 1, MidpointRounding.AwayFromZero), bearing, Directions[sector],
            Classify(environment.Rain, 0.05, 0.25, 0.6, "none", "light", "moderate", "heavy"),
            Classify(environment.Fog, 0.02, 0.15, 0.5, "clear", "light", "moderate", "dense"),
            environment.TemperatureCelsius,
            Daytime(time), environment.Metadata.AgeSeconds, environment.Metadata.IsStale);
    }

    private static string Daytime(StateTimeAstronomy? time)
    {
        if (time is null) return "unknown";
        if (time.SunOrMoon < 0.2) return "dark";
        return time.Daytime switch { < 6 => "dawn", < 18 => "daylight", < 20 => "dusk", _ => "dark" };
    }

    private static string Classify(double value, double a, double b, double c, string zero, string low, string medium, string high)
        => value < a ? zero : value < b ? low : value < c ? medium : high;
}
