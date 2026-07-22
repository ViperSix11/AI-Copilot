namespace ArmaAiBridge.App.Services;

public sealed class SpeechServiceException : Exception
{
    public SpeechServiceException(
        string message,
        string provider,
        string stage,
        int? httpStatus = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Provider = provider;
        Stage = stage;
        HttpStatus = httpStatus;
    }

    public string Provider { get; }
    public string Stage { get; }
    public int? HttpStatus { get; }

    public static string FormatForLog(Exception exception)
    {
        if (exception is not SpeechServiceException speech)
            return $"Voice operation failed: stage=unhandled, exceptionType={exception.GetType().Name}.";

        string status = speech.HttpStatus.HasValue ? $", httpStatus={speech.HttpStatus.Value}" : string.Empty;
        return $"Voice operation failed: provider={Safe(speech.Provider)}, stage={Safe(speech.Stage)}{status}.";
    }

    private static string Safe(string value)
        => new(value.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').Take(40).ToArray());
}
