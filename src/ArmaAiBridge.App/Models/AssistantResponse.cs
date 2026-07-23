namespace ArmaAiBridge.App.Models;

public sealed record AssistantResponse(
    string Text,
    string Model,
    int ToolCalls,
    int InputTokens,
    int OutputTokens,
    int ReasoningTokens = 0,
    string GroupCallsign = "",
    AssistantRequestMetrics? RequestMetrics = null,
    IReadOnlyList<RadioTransmission>? Transmissions = null,
    bool AwaitingCopyConfirmation = false);

public sealed record RadioTransmission(
    string Text,
    int PauseBeforeMilliseconds = 0);

public sealed record AssistantRequestMetrics(
    int SnapshotUtf8Bytes,
    IReadOnlyDictionary<string, int> SectionRecordCounts,
    int HistoryMessageCount,
    int HistoryCharacterCount,
    int SelectedToolCount,
    long ResponseLatencyMilliseconds,
    string AcknowledgementVariationId = "",
    bool AcknowledgementEligible = false,
    bool AcknowledgementEmitted = false,
    int AcknowledgementThresholdMilliseconds = 0,
    long AnswerTextLatencyMilliseconds = 0,
    bool RetryPerformed = false,
    string InitialIncompleteReason = "",
    string RadioVariationId = "",
    int RadioTransmissionCount = 1,
    bool CopyConfirmationRequested = false);
