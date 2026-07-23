namespace ArmaAiBridge.App.Services;

public sealed record OpenAiResponseDiagnostics(
    string Status,
    string IncompleteReason,
    string EffectiveModel,
    IReadOnlyDictionary<string, int> OutputTypeCounts,
    IReadOnlyList<string> MessageStatuses,
    bool HasOutputText,
    bool HasRefusal,
    int InputTokens,
    int OutputTokens,
    int ReasoningTokens,
    int ToolCalls);

public sealed class OpenAiAssistantException : InvalidOperationException
{
    public OpenAiAssistantException(
        string message,
        string stage,
        int? httpStatus = null,
        string? errorType = null,
        string? errorCode = null,
        string? diagnosticMessage = null,
        OpenAiResponseDiagnostics? responseDiagnostics = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
        HttpStatus = httpStatus;
        ErrorType = errorType;
        ErrorCode = errorCode;
        DiagnosticMessage = diagnosticMessage;
        ResponseDiagnostics = responseDiagnostics;
    }

    public string Stage { get; }
    public int? HttpStatus { get; }
    public string? ErrorType { get; }
    public string? ErrorCode { get; }
    public string? DiagnosticMessage { get; }
    public OpenAiResponseDiagnostics? ResponseDiagnostics { get; }
}
