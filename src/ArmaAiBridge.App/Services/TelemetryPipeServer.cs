using System.IO.Pipes;
using System.Text;

namespace ArmaAiBridge.App.Services;

public sealed class TelemetryPipeServer : IAsyncDisposable
{
    public const string PipeName = "ArmaAiBridge.Arma3.Telemetry";
    private const int MaximumLineCharacters = 1024 * 1024;

    private readonly LogService _log;
    private readonly object _connectionGate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private NamedPipeServerStream? _activePipe;
    private StreamWriter? _activeWriter;

    public TelemetryPipeServer(LogService log) => _log = log;

    public bool IsRunning => _serverTask is { IsCompleted: false };

    public bool IsClientConnected
    {
        get
        {
            lock (_connectionGate)
            {
                return _activePipe?.IsConnected == true && _activeWriter is not null;
            }
        }
    }

    public event Action<bool>? ClientConnectionChanged;
    public event Action<string>? MessageReceived;

    public Task StartAsync()
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _serverTask = Task.Run(() => RunAsync(_cts.Token));
        _log.Info($"Bidirectional Named Pipe listener started: {PipeName}");
        return Task.CompletedTask;
    }

    public async Task<bool> SendCommandAsync(string json, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Command JSON must not be empty.", nameof(json));
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StreamWriter? writer;
            NamedPipeServerStream? pipe;
            lock (_connectionGate)
            {
                writer = _activeWriter;
                pipe = _activePipe;
            }

            if (writer is null || pipe?.IsConnected != true)
            {
                return false;
            }

            try
            {
                await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (IOException exception)
            {
                _log.Warn($"Could not send command to Arma bridge: {exception.Message}");
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts = _cts;
        Task? serverTask = _serverTask;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        DisposeActiveConnection();

        try
        {
            if (serverTask is not null)
            {
                await serverTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                _serverTask = null;
            }
            NotifyClientConnectionChanged(false);
            _log.Info("Named Pipe listener stopped.");
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunSingleConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _log.Error("Named Pipe listener error", exception);
                await DelayBeforeRetryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunSingleConnectionAsync(CancellationToken cancellationToken)
    {
        await using NamedPipeServerStream pipe = new(
            PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        _log.Info("Waiting for Arma bridge connection.");
        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

        using StreamReader reader = new(pipe, new UTF8Encoding(false), false, 16 * 1024, true);
        using StreamWriter writer = new(pipe, new UTF8Encoding(false), 16 * 1024, true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        lock (_connectionGate)
        {
            _activePipe = pipe;
            _activeWriter = writer;
        }

        NotifyClientConnectionChanged(true);
        _log.Info("Arma bridge connected with duplex messaging.");

        try
        {
            while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
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
                    _log.Warn($"Discarded bridge message larger than {MaximumLineCharacters} characters.");
                    continue;
                }
                NotifyMessageReceived(line);
            }
        }
        catch (IOException exception)
        {
            _log.Warn($"Arma bridge disconnected: {exception.Message}");
        }
        finally
        {
            lock (_connectionGate)
            {
                if (ReferenceEquals(_activePipe, pipe))
                {
                    _activeWriter = null;
                    _activePipe = null;
                }
            }
            NotifyClientConnectionChanged(false);
            _log.Info("Arma bridge connection closed.");
        }
    }

    private void DisposeActiveConnection()
    {
        lock (_connectionGate)
        {
            try
            {
                _activeWriter?.Dispose();
                _activePipe?.Dispose();
            }
            catch (IOException)
            {
            }
            finally
            {
                _activeWriter = null;
                _activePipe = null;
            }
        }
    }

    private void NotifyClientConnectionChanged(bool connected)
    {
        try
        {
            ClientConnectionChanged?.Invoke(connected);
        }
        catch (Exception exception)
        {
            _log.Error("Connection status subscriber failed", exception);
        }
    }

    private void NotifyMessageReceived(string line)
    {
        try
        {
            MessageReceived?.Invoke(line);
        }
        catch (Exception exception)
        {
            _log.Error("Bridge message subscriber failed", exception);
        }
    }

    private static async Task DelayBeforeRetryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
