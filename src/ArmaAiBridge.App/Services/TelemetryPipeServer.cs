using System.IO.Pipes;
using System.Text;

namespace ArmaAiBridge.App.Services;

public sealed class TelemetryPipeServer : IAsyncDisposable
{
    public const string PipeName = "ArmaAiBridge.Arma3.Telemetry";
    private const int MaximumLineCharacters = 1024 * 1024;
    private readonly LogService _log;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;

    public TelemetryPipeServer(LogService log) => _log = log;
    public bool IsRunning => _serverTask is { IsCompleted: false };
    public event Action<bool>? ClientConnectionChanged;
    public event Action<string>? TelemetryReceived;

    public Task StartAsync()
    {
        if (IsRunning) return Task.CompletedTask;
        _cts = new CancellationTokenSource();
        _serverTask = Task.Run(() => RunAsync(_cts.Token));
        _log.Info($"Named Pipe listener started: {PipeName}");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts = _cts;
        Task? serverTask = _serverTask;
        if (cts is null) return;
        cts.Cancel();
        try { if (serverTask is not null) await serverTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(_cts, cts)) { _cts = null; _serverTask = null; }
            NotifyClientConnectionChanged(false);
            _log.Info("Named Pipe listener stopped.");
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await RunSingleConnectionAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                _log.Error("Named Pipe listener error", exception);
                await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunSingleConnectionAsync(CancellationToken cancellationToken)
    {
        await using NamedPipeServerStream pipe = new(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _log.Info("Waiting for Arma bridge connection.");
        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        NotifyClientConnectionChanged(true);
        _log.Info("Arma bridge connected.");
        try
        {
            using StreamReader reader = new(pipe, new UTF8Encoding(false), false, 16 * 1024, true);
            while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;
                if (line.Length > MaximumLineCharacters)
                {
                    _log.Warn($"Discarded telemetry line larger than {MaximumLineCharacters} characters.");
                    continue;
                }
                NotifyTelemetryReceived(line);
            }
        }
        catch (IOException exception) { _log.Warn($"Arma bridge disconnected: {exception.Message}"); }
        finally
        {
            NotifyClientConnectionChanged(false);
            _log.Info("Arma bridge connection closed.");
        }
    }

    private void NotifyClientConnectionChanged(bool connected)
    {
        try { ClientConnectionChanged?.Invoke(connected); }
        catch (Exception exception) { _log.Error("Connection status subscriber failed", exception); }
    }

    private void NotifyTelemetryReceived(string line)
    {
        try { TelemetryReceived?.Invoke(line); }
        catch (Exception exception) { _log.Error("Telemetry subscriber failed", exception); }
    }

    private static async Task DelayBeforeRetryAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
