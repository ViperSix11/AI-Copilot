namespace ArmaAiBridge.App.Services;

/// <summary>
/// Compatibility facade for the release 0.8 interpreted context boundary.
/// The evidence pipeline performs projection, relevance selection, fusion and
/// natural rendering without forwarding the closed JSON snapshot to OpenAI.
/// </summary>
public static class TacticalContextInterpreter
{
    public static string Interpret(string snapshotJson)
        => TacticalEvidencePipeline.Build(snapshotJson, string.Empty).ModelContext;

    public static string Interpret(string snapshotJson, string question)
        => TacticalEvidencePipeline.Build(snapshotJson, question).ModelContext;

    public static TacticalEvidenceReport Analyze(string snapshotJson, string question)
        => TacticalEvidencePipeline.Build(snapshotJson, question);
}
