using System.IO.Pipes;
using System.Text;

namespace AICopilot.App.Services;

public sealed class TelemetryPipeServer : IAsyncDisposable
{
    public const string PipeName = "AICopilot.Arma3.Telemetry";
    private const int MaximumLineCharacters = 1024 * 1024;

    private readonly LogService _log;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;

    public TelemetryPipeServer(LogService log)
    {
        _log = log;
    }

    public bool IsRunning => _serverTask is { IsCompleted: false };

    public event Action<bool>? ClientConnectionChanged;
    public event Action<string>? TelemetryReceived;

    public Task StartAsync()
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _serverTask = Task.Run(() => RunAsync(_cts.Token));
        _log.Info($"Named Pipe listener started: {PipeName}");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            if (_serverTask is not null)
            {
                await _serverTask;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _serverTask = null;
            ClientConnectionChanged?.Invoke(false);
            _log.Info("Named Pipe listener stopped.");
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using NamedPipeServerStream pipe = new(
                PipeName,
                PipeDirection.In,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            _log.Info("Waiting for Arma bridge connection.");
            await pipe.WaitForConnectionAsync(cancellationToken);
            ClientConnectionChanged?.Invoke(true);
            _log.Info("Arma bridge connected.");

            try
            {
                using StreamReader reader = new(pipe, new UTF8Encoding(false),
                    detectEncodingFromByteOrderMarks: false, bufferSize: 16 * 1024, leaveOpen: true);

                while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }

                    if (line.Length == 0)
                    {
                        continue;
                    }

                    if (line.Length > MaximumLineCharacters)
                    {
                        _log.Warn($"Discarded telemetry line larger than {MaximumLineCharacters} characters.");
                        continue;
                    }

                    TelemetryReceived?.Invoke(line);
                }
            }
            catch (IOException exception)
            {
                _log.Warn($"Arma bridge disconnected: {exception.Message}");
            }
            finally
            {
                ClientConnectionChanged?.Invoke(false);
                _log.Info("Arma bridge connection closed.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
