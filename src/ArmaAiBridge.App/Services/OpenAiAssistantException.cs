namespace ArmaAiBridge.App.Services;

public sealed class OpenAiAssistantException : InvalidOperationException
{
    public OpenAiAssistantException(
        string message,
        string stage,
        int? httpStatus = null,
        string? errorType = null,
        string? errorCode = null,
        string? diagnosticMessage = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
        HttpStatus = httpStatus;
        ErrorType = errorType;
        ErrorCode = errorCode;
        DiagnosticMessage = diagnosticMessage;
    }

    public string Stage { get; }
    public int? HttpStatus { get; }
    public string? ErrorType { get; }
    public string? ErrorCode { get; }
    public string? DiagnosticMessage { get; }
}
