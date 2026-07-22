namespace ArmaAiBridge.App.Models;

public sealed record AssistantResponse(
    string Text,
    string Model,
    int ToolCalls,
    int InputTokens,
    int OutputTokens,
    int ReasoningTokens = 0,
    string GroupCallsign = "",
    AssistantRequestMetrics? RequestMetrics = null);

public sealed record AssistantRequestMetrics(
    int SnapshotUtf8Bytes,
    IReadOnlyDictionary<string, int> SectionRecordCounts,
    int HistoryMessageCount,
    int HistoryCharacterCount,
    int SelectedToolCount,
    long ResponseLatencyMilliseconds,
    string AcknowledgementVariationId = "");
