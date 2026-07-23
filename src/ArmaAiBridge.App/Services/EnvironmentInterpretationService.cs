using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class EnvironmentInterpretationService
{
    public EnvironmentInterpretation Interpret(StateEnvironment environment, StateTimeAstronomy? time)
        => new(environment.Overcast, Classify(environment.Overcast), environment.Metadata.AgeSeconds, environment.Metadata.IsStale);

    public static string Classify(double overcast) => overcast switch
    {
        < 0.35 => "calm",
        < 0.7 => "unsettled",
        _ => "storm"
    };
}
