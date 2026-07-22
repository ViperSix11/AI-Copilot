using System.Text.Json;

namespace ArmaAiBridge.App.Services;

/// <summary>
/// The single validated ingestion boundary shared by typed reports and any future
/// finalized speech transcript. Implementations must reject questions, hypotheses,
/// inferred facts, and text that is not quoted from the current user turn.
/// </summary>
public interface IPlayerReportIngestor
{
    string RecordPlayerObservation(JsonElement arguments, string currentUserTurn);
    string CorrectPlayerObservation(JsonElement arguments, string currentUserTurn);
}
