using System.Text;

namespace ArmaAiBridge.App.Services;

public sealed class LogService
{
    private readonly object _gate = new();
    public event Action<string>? EntryWritten;
    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? exception = null) => Write("ERROR", exception is null ? message : $"{message}: {exception.Message}");

    private void Write(string level, string message)
    {
        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                string path = Path.Combine(AppPaths.LogDirectory, $"arma-ai-bridge-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            line += $" [file logging unavailable: {exception.Message}]";
        }
        try { EntryWritten?.Invoke(line); }
        catch { }
    }
}
